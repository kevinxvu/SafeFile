using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SafeFile.Core.Crypto;
using SafeFile.Core.Format;
using SafeFile.Core.IO;
using SafeFile.Core.Models;
using SafeFile.Core.Pipeline;

namespace SafeFile.Core.IO;

public sealed class FileEncryptor
{
    private readonly CryptoPipeline _pipeline;
    private readonly IProgress<double>? _progress;
    private readonly IProgress<PerFileProgress>? _perFileProgress;
    private readonly ILogger<FileEncryptor> _logger;
    private readonly int _minimumPasswordLength;

    public FileEncryptor(
        int consumerThreads = -1,
        IProgress<double>? progress = null,
        AppSettings? settings = null,
        IProgress<PerFileProgress>? perFileProgress = null,
        ILogger<FileEncryptor>? logger = null)
    {
        _progress = progress;
        _perFileProgress = perFileProgress;
        _logger = logger ?? NullLogger<FileEncryptor>.Instance;
        var effectiveSettings = settings ?? AppSettings.GetDefaults();
        _minimumPasswordLength = effectiveSettings.MinPasswordLength;
        if (_minimumPasswordLength < 1 || _minimumPasswordLength > 128)
            throw new ArgumentOutOfRangeException(nameof(settings), "Minimum password length must be between 1 and 128.");
        _pipeline = new CryptoPipeline(consumerThreads, progress);
    }

    public async Task<string> EncryptFolderZipAsync(
        string sourceFolderPath,
        string destinationPath,
        byte[] passwordBytes,
        int chunkSizeBytes = 1_048_576,
        Argon2Parameters? kdfParams = null,
        OutputFileNameMode outputFileNameMode = OutputFileNameMode.None,
        CancellationToken cancellationToken = default,
        bool overwriteExisting = false,
        IReadOnlyCollection<string>? excludedFolderPaths = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFolderPath);
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(passwordBytes);

        ValidatePasswordForEncryption(passwordBytes);

        if (!Directory.Exists(sourceFolderPath))
            throw new DirectoryNotFoundException($"Source folder not found: {sourceFolderPath}");
        ValidateChunkSize(chunkSizeBytes);
        _pipeline.ValidateMemoryBudget(chunkSizeBytes);
        EnsureDestinationOutsideSourceFolder(sourceFolderPath, destinationPath);
        ValidateOutputFileNameMode(outputFileNameMode);
        var excludedFolders = ExcludedFolderMatcher.Create(
            sourceFolderPath, excludedFolderPaths);
        var actualDestinationPath = destinationPath;
        var destinationOpened = false;

        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceFolderPath));
        var encryptedFileName = System.Text.Encoding.UTF8.GetBytes(folderName + ".zip");
        var estimatedInputBytes = StreamZipper.GetInputSize(sourceFolderPath, excludedFolders);
        long sourceBytesRead = 0;
        _progress?.Report(0);
        var masterKey = Array.Empty<byte>();

        try
        {
            var salt = Argon2Kdf.GenerateSalt();
            var noncePrefix = new byte[4];
            RandomNumberGenerator.Fill(noncePrefix);

            var effectiveKdfParams = kdfParams ?? Argon2Kdf.DefaultParameters;
            masterKey = await KdfDerivation.DeriveKeyAsync(
                passwordBytes,
                salt,
                effectiveKdfParams,
                cancellationToken).ConfigureAwait(false);
            var encryptedFileNameChunk = new AesGcmEngine().EncryptChunk(
                encryptedFileName,
                masterKey,
                noncePrefix,
                0,
                true);
            if (outputFileNameMode != OutputFileNameMode.None)
                actualDestinationPath = GetProtectedVaultPath(
                    destinationPath,
                    encryptedFileName,
                    masterKey,
                    salt,
                    effectiveKdfParams,
                    outputFileNameMode);

            var destinationMode = overwriteExisting ? FileMode.Create : FileMode.CreateNew;
            using var destStream = new FileStream(
                actualDestinationPath, destinationMode, FileAccess.Write, FileShare.None);
            destinationOpened = true;
            var passwordChecksum = PasswordValidator.ComputeChecksum(masterKey, salt);
            var header = new VaultHeader
            {
                Mode = VaultMode.Zip,
                OutputFileNameMode = outputFileNameMode,
                KdfParameters = effectiveKdfParams,
                Salt = salt,
                NoncePrefix = noncePrefix,
                ChunkSize = chunkSizeBytes,
                PasswordChecksum = passwordChecksum
            };

            header.WriteTo(destStream);

            WriteEncryptedFileNameChunk(destStream, encryptedFileNameChunk);

                var pauseWriterThreshold = Math.Clamp(
                    (long)chunkSizeBytes * 4,
                    1_048_576,
                    67_108_864);
                var pipe = new Pipe(new PipeOptions(
                    pauseWriterThreshold: pauseWriterThreshold,
                    resumeWriterThreshold: pauseWriterThreshold / 2,
                    useSynchronizationContext: false));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var pipeToken = linkedCts.Token;
                var zipProducerTask = ProduceZipToPipeAsync(
                    sourceFolderPath,
                    pipe.Writer,
                    excludedFolders,
                    bytesRead =>
                    {
                        var processedBytes = Interlocked.Add(ref sourceBytesRead, bytesRead);
                        if (estimatedInputBytes > 0)
                        {
                            var progress = Math.Min(
                                0.99,
                                (double)processedBytes / estimatedInputBytes * 0.99);
                            _progress?.Report(progress);
                        }
                    },
                    pipeToken);
                await using var zipReadStream = pipe.Reader.AsStream(leaveOpen: true);
                int? prefetchedByte = null;

                try
                {
                    await _pipeline.EncryptAsync(
                        async (chunkIndex) =>
                        {
                            var zipBuffer = ArrayPool<byte>.Shared.Rent(chunkSizeBytes);
                            try
                            {
                                var bytesRead = 0;
                                if (prefetchedByte.HasValue)
                                {
                                    zipBuffer[bytesRead++] = (byte)prefetchedByte.Value;
                                    prefetchedByte = null;
                                }

                                bytesRead += await ReadUpToChunkAsync(
                                    zipReadStream,
                                    zipBuffer.AsMemory(bytesRead, chunkSizeBytes - bytesRead),
                                    pipeToken).ConfigureAwait(false);

                                if (bytesRead == 0)
                                    return null;

                                var lookAhead = new byte[1];
                                var lookAheadRead = await zipReadStream.ReadAsync(
                                    lookAhead, pipeToken).ConfigureAwait(false);
                                var isLastChunk = lookAheadRead == 0;
                                if (!isLastChunk)
                                    prefetchedByte = lookAhead[0];

                                return new UnencryptedChunk(
                                    chunkIndex,
                                    zipBuffer.AsSpan(0, bytesRead).ToArray(),
                                    isLastChunk);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(zipBuffer);
                            }
                        },
                        encryptedChunk =>
                        {
                            WriteEncryptedChunk(destStream, encryptedChunk);
                            return Task.CompletedTask;
                        },
                        masterKey,
                        noncePrefix,
                        startingIndex: 1,
                        reportProgress: false,
                        cancellationToken: pipeToken).ConfigureAwait(false);

                    await zipProducerTask.ConfigureAwait(false);
                    _progress?.Report(1);
                }
                catch
                {
                    linkedCts.Cancel();
                    pipe.Reader.CancelPendingRead();
                    pipe.Writer.CancelPendingFlush();
                    try
                    {
                        await zipProducerTask.ConfigureAwait(false);
                    }
                    catch
                    {
                    }

                    throw;
                }
                finally
                {
                    await pipe.Reader.CompleteAsync().ConfigureAwait(false);
                }
        }
        catch (Exception ex)
        {
            if (destinationOpened)
                TryDeleteFile(
                    actualDestinationPath,
                    FileCleanupKind.IncompleteEncryptionOutput,
                    ex is OperationCanceledException);

            throw;
        }
        finally
        {
            if (masterKey.Length > 0)
                CryptographicOperations.ZeroMemory(masterKey);
        }

        return actualDestinationPath;
    }

    public async Task DecryptFolderZipAsync(
        string sourcePath,
        string destinationFolder,
        byte[] passwordBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destinationFolder);
        ArgumentNullException.ThrowIfNull(passwordBytes);

        if (passwordBytes.Length == 0)
            throw new ArgumentException("Password cannot be empty.", nameof(passwordBytes));

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Source file not found: {sourcePath}");
        EnsureDistinctPaths(sourcePath, destinationFolder);
        _progress?.Report(0);

        try
        {
            using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var header = VaultHeader.ReadFrom(sourceStream);
            if (header.Mode != VaultMode.Zip)
                throw new InvalidDataException("Vault is not in Zip mode.");

            var masterKey = await KdfDerivation.DeriveKeyAsync(
                passwordBytes,
                header.Salt,
                header.KdfParameters,
                cancellationToken).ConfigureAwait(false);
            var aesGcm = new AesGcmEngine();

            try
            {
                if (!PasswordValidator.ValidateChecksum(header.PasswordChecksum, masterKey, header.Salt))
                    throw new InvalidOperationException("Invalid password or corrupted vault file (key verifier mismatch).");

                var encryptedFileNameChunk = ReadEncryptedFileNameChunk(sourceStream, header);
                if (encryptedFileNameChunk is null)
                    throw new InvalidDataException("Missing encrypted filename chunk in vault file.");

                // Validate filename chunk index
                if (encryptedFileNameChunk.Index != 0)
                    throw new InvalidDataException($"Expected filename chunk index 0, got {encryptedFileNameChunk.Index}.");

                var decryptedFileName = aesGcm.DecryptChunk(encryptedFileNameChunk, masterKey);
                CryptographicOperations.ZeroMemory(decryptedFileName);

                var tempZipPath = Path.Combine(Path.GetTempPath(), $"SafeFile-{Guid.NewGuid():N}.zip");
                long expectedChunkIndex = 1;  // Data chunks start at index 1
                var sawLastChunk = false;
                double lastReportedDecryptProgress = 0;

                try
                {
                    await using (var tempZipStream = new FileStream(
                        tempZipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                        81_920, FileOptions.Asynchronous | FileOptions.DeleteOnClose))
                    {
                        while (true)
                        {
                            var encryptedChunk = ReadEncryptedChunk(sourceStream, header);
                            if (encryptedChunk is null)
                                break;
                            if (sawLastChunk)
                                throw new InvalidDataException("Vault contains data after the final chunk.");
                            if (encryptedChunk.Index != expectedChunkIndex)
                                throw new InvalidDataException(
                                    $"Chunk index mismatch: expected {expectedChunkIndex}, got {encryptedChunk.Index}.");

                            var plaintext = aesGcm.DecryptChunk(encryptedChunk, masterKey);
                            await tempZipStream.WriteAsync(plaintext, cancellationToken).ConfigureAwait(false);
                            sawLastChunk = encryptedChunk.IsLastChunk;
                            expectedChunkIndex++;
                            var decryptProgress = Math.Min(
                                (double)sourceStream.Position / sourceStream.Length * 0.6,
                                0.6);
                            if (decryptProgress - lastReportedDecryptProgress >= 0.001)
                            {
                                _progress?.Report(decryptProgress);
                                lastReportedDecryptProgress = decryptProgress;
                            }
                        }

                        if (!sawLastChunk)
                            throw new InvalidDataException("Vault is truncated or has no final data chunk.");

                        tempZipStream.Position = 0;
                        await ExtractFolderPreservingPartialOutputAsync(
                            tempZipStream,
                            destinationFolder,
                            cancellationToken,
                            progress => _progress?.Report(0.6 + progress * 0.4))
                            .ConfigureAwait(false);
                    }
                    _progress?.Report(1);
                }
                finally
                {
                    TryDeleteFile(
                        tempZipPath,
                        FileCleanupKind.TemporaryDecryptionFile,
                        cancellationToken.IsCancellationRequested);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
        }
        catch
        {
            throw;
        }
    }

    public Task<string> EncryptFileAsync(
        string sourcePath,
        string destinationPath,
        byte[] passwordBytes,
        int chunkSizeBytes = 1_048_576,
        Argon2Parameters? kdfParams = null,
        OutputFileNameMode outputFileNameMode = OutputFileNameMode.None,
        CancellationToken cancellationToken = default,
        bool overwriteExisting = false) =>
        EncryptFileCoreAsync(
            sourcePath,
            destinationPath,
            passwordBytes,
            VaultMode.File,
            chunkSizeBytes,
            kdfParams,
            cancellationToken,
            progressCallback: null,
            outputFileNameMode,
            overwriteExisting);

    /// <summary>
    /// Validates the password and reads authenticated vault metadata without
    /// decrypting the file contents. Argon2 is only run when this method is called.
    /// </summary>
    public async Task<VaultMetadata> ReadVaultMetadataAsync(
        string sourcePath,
        byte[] passwordBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(passwordBytes);

        if (passwordBytes.Length == 0)
            throw new ArgumentException("Password cannot be empty.", nameof(passwordBytes));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Source file not found: {sourcePath}");

        cancellationToken.ThrowIfCancellationRequested();

        using var sourceStream = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = VaultHeader.ReadFrom(sourceStream);
        var masterKey = await KdfDerivation.DeriveKeyAsync(
            passwordBytes,
            header.Salt,
            header.KdfParameters,
            cancellationToken).ConfigureAwait(false);

        try
        {
            if (!PasswordValidator.ValidateChecksum(
                    header.PasswordChecksum, masterKey, header.Salt))
            {
                throw new InvalidOperationException(
                    "Invalid password or corrupted vault file (key verifier mismatch).");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var fileNameChunk = ReadEncryptedFileNameChunk(sourceStream, header)
                ?? throw new InvalidDataException(
                    "Missing encrypted filename chunk in vault file.");
            if (fileNameChunk.Index != 0)
            {
                throw new InvalidDataException(
                    $"Expected filename chunk index 0, got {fileNameChunk.Index}.");
            }

            var plaintext = new AesGcmEngine().DecryptChunk(fileNameChunk, masterKey);
            try
            {
                var originalFileName =
                    new System.Text.UTF8Encoding(false, true).GetString(plaintext);
                ValidateStoredFileName(originalFileName);
                var fileInfo = new FileInfo(sourcePath);

                return new VaultMetadata(
                    fileInfo.FullName,
                    originalFileName,
                    fileInfo.Length,
                    fileInfo.LastWriteTimeUtc,
                    header.Version,
                    header.Mode,
                    header.OutputFileNameMode,
                    header.ChunkSize,
                    header.KdfParameters,
                    "AES-256-GCM");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public async Task<string> DecryptOutputFileNameAsync(
        string encryptedOutputFileName,
        byte[] passwordBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encryptedOutputFileName);
        ArgumentNullException.ThrowIfNull(passwordBytes);
        if (passwordBytes.Length == 0)
            throw new ArgumentException("Password cannot be empty.", nameof(passwordBytes));

        var fileName = Path.GetFileName(encryptedOutputFileName);
        if (!fileName.EndsWith(".safe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Encrypted output filename must end with .safe.");

        var encodedName = fileName[..^".safe".Length];
        if ((encodedName.Length is 32 or 64) && encodedName.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                "A hashed output filename cannot be decrypted without reading its vault metadata.");
        }

        var base64Url = encodedName
            .Replace('-', '+')
            .Replace('_', '/');
        var paddingLength = (4 - base64Url.Length % 4) % 4;
        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(base64Url + new string('=', paddingLength));
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Encrypted output filename is not valid Base64URL.", ex);
        }

        if (payload.Length is < 47 or > 187 ||
            payload[0] != (byte)'S' ||
            payload[1] != (byte)'F' ||
            payload[2] != 1)
        {
            throw new InvalidDataException("Encrypted output filename has an invalid format.");
        }

        var kdfParameters = new Argon2Parameters(
            BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(3, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(7, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(11, 4)));
        try
        {
            kdfParameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException(
                "Encrypted output filename contains unsafe Argon2 parameters.", ex);
        }

        var salt = payload.AsSpan(15, Argon2Kdf.SaltSize).ToArray();
        var ciphertextLength = payload.Length - 47;
        var ciphertext = payload.AsSpan(31, ciphertextLength).ToArray();
        var tag = payload.AsSpan(31 + ciphertextLength, AesGcmEngine.TagSize).ToArray();
        var masterKey = await KdfDerivation.DeriveKeyAsync(
            passwordBytes,
            salt,
            kdfParameters,
            cancellationToken).ConfigureAwait(false);
        var fileNameKey = DeriveOutputFileNameKey(masterKey);
        try
        {
            var plaintext = new AesGcmEngine().DecryptChunk(
                new EncryptedChunk(
                    0,
                    new byte[AesGcmEngine.NoncePrefixSize],
                    ciphertext,
                    tag,
                    true),
                fileNameKey);
            try
            {
                var decryptedName =
                    new System.Text.UTF8Encoding(false, true).GetString(plaintext);
                ValidateStoredFileName(decryptedName);
                return decryptedName;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileNameKey);
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public async Task EncryptFolderPerFileAsync(
        string sourceFolderPath,
        string destinationFolderPath,
        byte[] passwordBytes,
        int chunkSizeBytes = 1_048_576,
        Argon2Parameters? kdfParams = null,
        OutputFileNameMode outputFileNameMode = OutputFileNameMode.None,
        CancellationToken cancellationToken = default,
        bool overwriteExisting = false,
        IReadOnlyCollection<string>? excludedFolderPaths = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFolderPath);
        ArgumentNullException.ThrowIfNull(destinationFolderPath);
        ArgumentNullException.ThrowIfNull(passwordBytes);

        if (!Directory.Exists(sourceFolderPath))
            throw new DirectoryNotFoundException($"Source folder not found: {sourceFolderPath}");
        if (File.Exists(destinationFolderPath))
            throw new IOException($"Destination path is an existing file: {destinationFolderPath}");
        ValidatePasswordForEncryption(passwordBytes);

        ValidateChunkSize(chunkSizeBytes);
        _pipeline.ValidateMemoryBudget(chunkSizeBytes);
        EnsureDestinationOutsideSourceFolder(sourceFolderPath, destinationFolderPath);

        var sourceRoot = Path.GetFullPath(sourceFolderPath);
        var excludedFolders = ExcludedFolderMatcher.Create(
            sourceFolderPath, excludedFolderPaths);
        ValidateOutputFileNameMode(outputFileNameMode);
        Directory.CreateDirectory(destinationFolderPath);
        var hasFailures = false;

        // Collision fast-fail paths for None and deterministic hashes can complete synchronously.
        // Move the batch loop off a caller UI context before processing them.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        foreach (var sourceFile in EnumerateRegularFiles(
                     new DirectoryInfo(sourceRoot), excludedFolders))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile.FullName);
            try
            {
                var encryptedFileName = sourceFile.Name + ".safe";
                var encryptedRelativePath = Path.Combine(
                    Path.GetDirectoryName(relativePath) ?? string.Empty,
                    encryptedFileName);
                var encryptedPath = Path.Combine(destinationFolderPath, encryptedRelativePath);
                var sourceFileNameBytes = System.Text.Encoding.UTF8.GetBytes(sourceFile.Name);
                var expectedOutputPath = outputFileNameMode switch
                {
                    OutputFileNameMode.Sha256 => GetSha256VaultPath(encryptedPath, sourceFileNameBytes),
                    OutputFileNameMode.Md5 => GetMd5VaultPath(encryptedPath, sourceFileNameBytes),
                    _ => encryptedPath
                };

                if (!overwriteExisting &&
                    (outputFileNameMode == OutputFileNameMode.None ||
                     outputFileNameMode == OutputFileNameMode.Sha256 ||
                     outputFileNameMode == OutputFileNameMode.Md5) &&
                    File.Exists(expectedOutputPath))
                {
                    hasFailures = true;
                    _perFileProgress?.Report(
                        new PerFileProgress(
                            sourceFile.FullName,
                            1,
                            PerFileResult.DestinationExists));
                    continue;
                }

                Directory.CreateDirectory(
                    Path.GetDirectoryName(encryptedPath)
                    ?? throw new InvalidOperationException("Encrypted file has no parent directory."));

                await EncryptFileCoreAsync(
                    sourceFile.FullName,
                    encryptedPath,
                    passwordBytes,
                    VaultMode.PerFile,
                    chunkSizeBytes,
                    kdfParams,
                    cancellationToken,
                    progress => _perFileProgress?.Report(
                        new PerFileProgress(sourceFile.FullName, progress)),
                    outputFileNameMode,
                    overwriteExisting).ConfigureAwait(false);
                _perFileProgress?.Report(
                    new PerFileProgress(
                        sourceFile.FullName,
                        1,
                        PerFileResult.Succeeded));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                hasFailures = true;
                _perFileProgress?.Report(
                    new PerFileProgress(
                        sourceFile.FullName,
                        1,
                        ex is DestinationAlreadyExistsException
                            ? PerFileResult.DestinationExists
                            : PerFileResult.Failed));
            }
        }

        if (hasFailures)
        {
            throw new IOException("Per-file encryption completed with errors.");
        }
    }

    public async Task DecryptFolderPerFileAsync(
        string sourceFolderPath,
        string destinationFolderPath,
        byte[] passwordBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFolderPath);
        ArgumentNullException.ThrowIfNull(destinationFolderPath);
        ArgumentNullException.ThrowIfNull(passwordBytes);

        if (!Directory.Exists(sourceFolderPath))
            throw new DirectoryNotFoundException($"Source folder not found: {sourceFolderPath}");
        if (Directory.Exists(destinationFolderPath) || File.Exists(destinationFolderPath))
            throw new IOException($"Destination already exists: {destinationFolderPath}");
        if (passwordBytes.Length == 0)
            throw new ArgumentException("Password cannot be empty.", nameof(passwordBytes));

        EnsureDestinationOutsideSourceFolder(sourceFolderPath, destinationFolderPath);

        var sourceRoot = Path.GetFullPath(sourceFolderPath);
        Directory.CreateDirectory(destinationFolderPath);

        try
        {
            foreach (var encryptedFile in EnumerateRegularFiles(new DirectoryInfo(sourceRoot)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!encryptedFile.Name.EndsWith(".safe", StringComparison.OrdinalIgnoreCase))
                    continue;

                var relativePath = Path.GetRelativePath(sourceRoot, encryptedFile.FullName);
                var decryptedRelativePath = relativePath[..^".safe".Length];
                var decryptedPath = Path.Combine(destinationFolderPath, decryptedRelativePath);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(decryptedPath)
                    ?? throw new InvalidOperationException("Decrypted file has no parent directory."));

                await DecryptFileCoreAsync(
                    encryptedFile.FullName,
                    decryptedPath,
                    passwordBytes,
                    VaultMode.PerFile,
                    cancellationToken,
                    overwriteExisting: false).ConfigureAwait(false);
            }
        }
        catch
        {
            throw;
        }
    }

    private async Task<string> EncryptFileCoreAsync(
        string sourcePath,
        string destinationPath,
        byte[] passwordBytes,
        VaultMode vaultMode,
        int chunkSizeBytes,
        Argon2Parameters? kdfParams,
        CancellationToken cancellationToken,
        Action<double>? progressCallback,
        OutputFileNameMode outputFileNameMode,
        bool overwriteExisting)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(passwordBytes);

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Source file not found: {sourcePath}");
        EnsureDistinctPaths(sourcePath, destinationPath);

        ValidatePasswordForEncryption(passwordBytes);
        ValidateOutputFileNameMode(outputFileNameMode);

        ValidateChunkSize(chunkSizeBytes);
        _pipeline.ValidateMemoryBudget(chunkSizeBytes);

        var fileInfo = new FileInfo(sourcePath);
        var originalFileName = fileInfo.Name;
        var encryptedFileName = System.Text.Encoding.UTF8.GetBytes(originalFileName);
        var actualDestinationPath = outputFileNameMode switch
        {
            OutputFileNameMode.Sha256 => GetSha256VaultPath(destinationPath, encryptedFileName),
            OutputFileNameMode.Md5 => GetMd5VaultPath(destinationPath, encryptedFileName),
            _ => destinationPath
        };

        EnsureDistinctPaths(sourcePath, actualDestinationPath);
        if (!overwriteExisting &&
            (outputFileNameMode == OutputFileNameMode.None ||
             outputFileNameMode == OutputFileNameMode.Sha256 ||
             outputFileNameMode == OutputFileNameMode.Md5) &&
            File.Exists(actualDestinationPath))
        {
            if (vaultMode == VaultMode.PerFile)
                throw new DestinationAlreadyExistsException(actualDestinationPath);
            throw new IOException($"Destination already exists: {actualDestinationPath}");
        }

        using var sourceStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var fileSize = sourceStream.Length;
        var masterKey = Array.Empty<byte>();
        var destinationOpened = false;

        try
        {
            var salt = Argon2Kdf.GenerateSalt();
            var noncePrefix = new byte[4];
            RandomNumberGenerator.Fill(noncePrefix);

            var effectiveKdfParams = kdfParams ?? Argon2Kdf.DefaultParameters;
            masterKey = await KdfDerivation.DeriveKeyAsync(
                passwordBytes,
                salt,
                effectiveKdfParams,
                cancellationToken).ConfigureAwait(false);
            var aesGcm = new AesGcmEngine();
            var encryptedFileNameChunk = aesGcm.EncryptChunk(
                encryptedFileName, masterKey, noncePrefix, 0, true);
            if (outputFileNameMode == OutputFileNameMode.Aes)
                actualDestinationPath = GetProtectedVaultPath(
                    destinationPath,
                    encryptedFileName,
                    masterKey,
                    salt,
                    effectiveKdfParams,
                    outputFileNameMode);

            EnsureDistinctPaths(sourcePath, actualDestinationPath);
            var destinationMode = overwriteExisting ? FileMode.Create : FileMode.CreateNew;
            FileStream destStream;
            try
            {
                destStream = new FileStream(
                    actualDestinationPath, destinationMode, FileAccess.Write, FileShare.None);
            }
            catch (IOException ex) when (
                !overwriteExisting && File.Exists(actualDestinationPath))
            {
                if (vaultMode == VaultMode.PerFile)
                    throw new DestinationAlreadyExistsException(actualDestinationPath, ex);
                throw;
            }
            using (destStream)
            {
            destinationOpened = true;
            var passwordChecksum = PasswordValidator.ComputeChecksum(masterKey, salt);
            var header = new VaultHeader
            {
                Mode = vaultMode,
                OutputFileNameMode = outputFileNameMode,
                KdfParameters = effectiveKdfParams,
                Salt = salt,
                NoncePrefix = noncePrefix,
                ChunkSize = chunkSizeBytes,
                PasswordChecksum = passwordChecksum
            };

            header.WriteTo(destStream);

            WriteEncryptedFileNameChunk(destStream, encryptedFileNameChunk);

            var totalChunks = (fileSize + chunkSizeBytes - 1) / chunkSizeBytes;

            await _pipeline.EncryptAsync(
                    async (chunkIndex) =>
                    {
                        var sourceBuffer = ArrayPool<byte>.Shared.Rent(chunkSizeBytes);
                        try
                        {
                            var dataChunkIndex = chunkIndex - 1;
                            var remainingBytes = fileSize - sourceStream.Position;
                            if (remainingBytes == 0 && fileSize == 0 && dataChunkIndex == 0)
                                return new UnencryptedChunk(chunkIndex, Array.Empty<byte>(), true);
                            if (remainingBytes <= 0)
                                return null;

                            var requestedBytes = (int)Math.Min(chunkSizeBytes, remainingBytes);
                            int bytesRead = ReadUpToChunk(sourceStream, sourceBuffer, requestedBytes);
                            if (bytesRead == 0)
                                throw new IOException("Source file was truncated while it was being encrypted.");

                            return new UnencryptedChunk(
                                chunkIndex,
                                sourceBuffer.AsSpan(0, bytesRead).ToArray(),
                                sourceStream.Position == fileSize);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(sourceBuffer);
                        }
                    },
                    encryptedChunk =>
                    {
                        WriteEncryptedChunk(destStream, encryptedChunk);
                        return Task.CompletedTask;
                    },
                    masterKey,
                    noncePrefix,
                    totalChunks: Math.Max(1, totalChunks),
                    startingIndex: 1,
                    cancellationToken: cancellationToken,
                progressCallback: progressCallback).ConfigureAwait(false);

            if (sourceStream.Length != fileSize)
                throw new IOException("Source file size changed while it was being encrypted.");
            }
        }
        catch (Exception ex)
        {
            if (destinationOpened)
                TryDeleteFile(
                    actualDestinationPath,
                    FileCleanupKind.IncompleteEncryptionOutput,
                    ex is OperationCanceledException);

            throw;
        }
        finally
        {
            if (masterKey.Length > 0)
                CryptographicOperations.ZeroMemory(masterKey);
        }

        return actualDestinationPath;
    }

    public Task<string> DecryptFileAsync(
        string sourcePath,
        string destinationPath,
        byte[] passwordBytes,
        CancellationToken cancellationToken = default,
        bool overwriteExisting = false) =>
        DecryptFileCoreAsync(
            sourcePath,
            destinationPath,
            passwordBytes,
            VaultMode.File,
            cancellationToken,
            overwriteExisting);

    public Task<string> DecryptPerFileVaultAsync(
        string sourcePath,
        string destinationPath,
        byte[] passwordBytes,
        CancellationToken cancellationToken = default,
        bool overwriteExisting = false) =>
        DecryptFileCoreAsync(
            sourcePath,
            destinationPath,
            passwordBytes,
            VaultMode.PerFile,
            cancellationToken,
            overwriteExisting);

    private async Task<string> DecryptFileCoreAsync(
        string sourcePath,
        string destinationPath,
        byte[] passwordBytes,
        VaultMode expectedMode,
        CancellationToken cancellationToken,
        bool overwriteExisting)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(passwordBytes);

        if (passwordBytes.Length == 0)
            throw new ArgumentException("Password cannot be empty.", nameof(passwordBytes));

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Source file not found: {sourcePath}");
        EnsureDistinctPaths(sourcePath, destinationPath);
        _progress?.Report(0);

        var actualDestinationPath = destinationPath;
        try
        {
            using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var header = VaultHeader.ReadFrom(sourceStream);
            if (header.Mode != expectedMode)
                throw new InvalidDataException(
                    $"Vault mode mismatch: expected {expectedMode}, got {header.Mode}.");
            var restoreStoredFileName =
                header.OutputFileNameMode != OutputFileNameMode.None;

            var masterKey = await KdfDerivation.DeriveKeyAsync(
                passwordBytes,
                header.Salt,
                header.KdfParameters,
                cancellationToken).ConfigureAwait(false);
            var aesGcm = new AesGcmEngine();

            try
            {
                if (!PasswordValidator.ValidateChecksum(header.PasswordChecksum, masterKey, header.Salt))
                    throw new InvalidOperationException("Invalid password or corrupted vault file (key verifier mismatch).");

                var encryptedFileNameChunk = ReadEncryptedFileNameChunk(sourceStream, header);
                if (encryptedFileNameChunk is null)
                    throw new InvalidDataException("Missing encrypted filename chunk in vault file.");

                // Validate filename chunk index
                if (encryptedFileNameChunk.Index != 0)
                    throw new InvalidDataException($"Expected filename chunk index 0, got {encryptedFileNameChunk.Index}.");

                var decryptedFileName = aesGcm.DecryptChunk(encryptedFileNameChunk, masterKey);
                var originalFileName = System.Text.Encoding.UTF8.GetString(decryptedFileName);
                if (restoreStoredFileName)
                {
                    ValidateStoredFileName(originalFileName);
                    actualDestinationPath = Path.Combine(
                        Path.GetDirectoryName(destinationPath)
                        ?? throw new InvalidOperationException("Destination file has no parent directory."),
                        originalFileName);
                }

                var destinationMode = overwriteExisting
                    ? FileMode.Create
                    : FileMode.CreateNew;
                using var destStream = new FileStream(
                    actualDestinationPath, destinationMode, FileAccess.Write, FileShare.None);

                long expectedChunkIndex = 1;  // Data chunks start at index 1
                var sawLastChunk = false;
                double lastReportedProgress = 0;
                while (true)
                {
                    var encryptedChunk = ReadEncryptedChunk(sourceStream, header);
                    if (encryptedChunk is null)
                        break;
                    if (sawLastChunk)
                        throw new InvalidDataException("Vault contains data after the final chunk.");

                    // Validate chunk index matches expected order
                    if (encryptedChunk.Index != expectedChunkIndex)
                        throw new InvalidDataException(
                            $"Chunk index mismatch: expected {expectedChunkIndex}, got {encryptedChunk.Index}. " +
                            $"This indicates file corruption or tampering.");

                    var plaintext = aesGcm.DecryptChunk(encryptedChunk, masterKey);
                    destStream.Write(plaintext);

                    sawLastChunk = encryptedChunk.IsLastChunk;
                    expectedChunkIndex++;

                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException();

                    var currentProgress = Math.Min(
                        (double)sourceStream.Position / sourceStream.Length,
                        0.999);
                    if (currentProgress - lastReportedProgress >= 0.001)
                    {
                        _progress?.Report(currentProgress);
                        lastReportedProgress = currentProgress;
                    }
                }

                if (!sawLastChunk)
                    throw new InvalidDataException("Vault is truncated or has no final data chunk.");
                _progress?.Report(1);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
        }
        catch
        {
            throw;
        }

        return actualDestinationPath;
    }

    private static void WriteEncryptedChunk(Stream stream, EncryptedChunk chunk)
    {
        // Write full nonce (4-byte prefix + 8-byte chunk index = 12 bytes)
        var fullNonce = new byte[12];
        chunk.NoncePrefix.CopyTo(fullNonce, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(fullNonce.AsSpan(4, 8), (ulong)chunk.Index);

        var ciphertextSize = chunk.Ciphertext.Length;

        Span<byte> sizeBuffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(sizeBuffer, AesGcmEngine.NonceSize);
        stream.Write(sizeBuffer);
        stream.Write(fullNonce);
        BinaryPrimitives.WriteInt32LittleEndian(sizeBuffer, ciphertextSize);
        stream.Write(sizeBuffer);
        stream.Write(chunk.Ciphertext);
        stream.Write(chunk.Tag);
        stream.WriteByte(chunk.IsLastChunk ? (byte)1 : (byte)0);  // Write isLastChunk flag
    }

    /// <summary>
    /// Writes encrypted filename chunk using standard encrypted chunk format.
    /// No special prefix - uses the same format as WriteEncryptedChunk for consistency.
    /// </summary>
    private static void WriteEncryptedFileNameChunk(Stream stream, EncryptedChunk chunk)
    {
        WriteEncryptedChunk(stream, chunk);
    }

    private static EncryptedChunk? ReadEncryptedChunk(
        Stream stream,
        VaultHeader header,
        bool isFileNameChunk = false)
    {
        Span<byte> sizeBuffer = stackalloc byte[4];

        var firstRead = stream.Read(sizeBuffer);
        if (firstRead == 0)
            return null;
        ReadExactly(stream, sizeBuffer[firstRead..], "nonce size");

        var nonceSize = BinaryPrimitives.ReadInt32LittleEndian(sizeBuffer);
        if (nonceSize != AesGcmEngine.NonceSize)
            throw new InvalidDataException($"Nonce must be exactly {AesGcmEngine.NonceSize} bytes, got {nonceSize}.");

        var nonce = new byte[nonceSize];
        ReadExactly(stream, nonce, "nonce");
        ReadExactly(stream, sizeBuffer, "ciphertext size");

        var ciphertextSize = BinaryPrimitives.ReadInt32LittleEndian(sizeBuffer);
        var maximumCiphertextSize = isFileNameChunk ? 4_096 : header.ChunkSize;
        if (ciphertextSize < 0 || ciphertextSize > maximumCiphertextSize)
            throw new InvalidDataException(
                $"Ciphertext size {ciphertextSize} is outside the allowed range 0..{maximumCiphertextSize}.");

        var ciphertext = new byte[ciphertextSize];
        ReadExactly(stream, ciphertext, "ciphertext");

        var tag = new byte[AesGcmEngine.TagSize];
        ReadExactly(stream, tag, "authentication tag");

        int isLastChunkByte = stream.ReadByte();
        if (isLastChunkByte == -1)
            throw new InvalidDataException("Unexpected end of stream while reading isLastChunk flag.");
        if (isLastChunkByte is not 0 and not 1)
            throw new InvalidDataException($"Invalid isLastChunk flag: {isLastChunkByte}.");
        var isLastChunk = isLastChunkByte == 1;

        var noncePrefix = new byte[4];
        Array.Copy(nonce, 0, noncePrefix, 0, 4);
        if (!noncePrefix.AsSpan().SequenceEqual(header.NoncePrefix))
            throw new InvalidDataException("Chunk nonce prefix does not match the vault header.");

        var chunkIndexBytes = nonce.AsSpan(4, 8);
        var unsignedChunkIndex = BinaryPrimitives.ReadUInt64LittleEndian(chunkIndexBytes);
        if (unsignedChunkIndex > long.MaxValue)
            throw new InvalidDataException("Chunk index is outside the supported range.");
        var chunkIndex = (long)unsignedChunkIndex;

        return new EncryptedChunk(
            Index: chunkIndex,
            NoncePrefix: noncePrefix,
            Ciphertext: ciphertext,
            Tag: tag,
            IsLastChunk: isLastChunk);
    }

    /// <summary>
    /// Reads encrypted filename chunk using standard encrypted chunk format.
    /// </summary>
    private static EncryptedChunk? ReadEncryptedFileNameChunk(Stream stream, VaultHeader header)
    {
        return ReadEncryptedChunk(stream, header, isFileNameChunk: true);
    }

    private static void ValidateStoredFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName is "." or ".." ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("Vault contains an invalid stored filename.");
        }
    }

    private static string GetProtectedVaultPath(
        string requestedPath,
        byte[] fullFileName,
        byte[] masterKey,
        byte[] salt,
        Argon2Parameters kdfParameters,
        OutputFileNameMode outputFileNameMode) => outputFileNameMode switch
    {
        OutputFileNameMode.Aes => GetAesEncryptedVaultPath(
            requestedPath, fullFileName, masterKey, salt, kdfParameters),
        OutputFileNameMode.Sha256 => GetSha256VaultPath(requestedPath, fullFileName),
        OutputFileNameMode.Md5 => GetMd5VaultPath(requestedPath, fullFileName),
        _ => throw new ArgumentOutOfRangeException(
            nameof(outputFileNameMode), outputFileNameMode, "Unsupported output filename mode.")
    };

    private static string GetAesEncryptedVaultPath(
        string requestedPath,
        byte[] fullFileName,
        byte[] masterKey,
        byte[] salt,
        Argon2Parameters kdfParameters)
    {
        var parent = Path.GetDirectoryName(requestedPath) ?? string.Empty;
        var fullName = System.Text.Encoding.UTF8.GetString(fullFileName);
        var shortenedName = ShortenFileNameForStandaloneEncryption(fullName);
        var shortenedBytes = System.Text.Encoding.UTF8.GetBytes(shortenedName);
        var fileNameKey = DeriveOutputFileNameKey(masterKey);
        try
        {
            var encrypted = new AesGcmEngine().EncryptChunk(
                shortenedBytes,
                fileNameKey,
                new byte[AesGcmEngine.NoncePrefixSize],
                chunkIndex: 0,
                isLastChunk: true);
            var payload = new byte[47 + encrypted.Ciphertext.Length];
            payload[0] = (byte)'S';
            payload[1] = (byte)'F';
            payload[2] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(3, 4), kdfParameters.MemorySizeKb);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(7, 4), kdfParameters.Iterations);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(11, 4), kdfParameters.Parallelism);
            salt.CopyTo(payload, 15);
            encrypted.Ciphertext.CopyTo(payload, 31);
            encrypted.Tag.CopyTo(payload, 31 + encrypted.Ciphertext.Length);

            var encodedName = Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
            return Path.Combine(parent, encodedName + ".safe");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileNameKey);
            CryptographicOperations.ZeroMemory(shortenedBytes);
        }
    }

    private static string GetSha256VaultPath(string requestedPath, byte[] fullFileName)
    {
        var parent = Path.GetDirectoryName(requestedPath) ?? string.Empty;
        var hash = SHA256.HashData(fullFileName);
        try
        {
            return Path.Combine(parent, Convert.ToHexStringLower(hash) + ".safe");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static string GetMd5VaultPath(string requestedPath, byte[] fullFileName)
    {
        var parent = Path.GetDirectoryName(requestedPath) ?? string.Empty;
        var hash = MD5.HashData(fullFileName);
        try
        {
            return Path.Combine(parent, Convert.ToHexStringLower(hash) + ".safe");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static string ShortenFileNameForStandaloneEncryption(string fullName)
    {
        const int maximumPlaintextBytes = 140;
        var extension = Path.GetExtension(fullName);
        var extensionBytes = System.Text.Encoding.UTF8.GetByteCount(extension);
        if (extensionBytes > maximumPlaintextBytes)
        {
            throw new PathTooLongException(
                "The file extension is too long to encrypt as an output filename. " +
                "Rename the file with a shorter extension and try again.");
        }

        var stem = Path.GetFileNameWithoutExtension(fullName);
        var remainingBytes = maximumPlaintextBytes - extensionBytes;
        var builder = new System.Text.StringBuilder();
        foreach (var rune in stem.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > remainingBytes)
                break;
            builder.Append(rune.ToString());
            remainingBytes -= rune.Utf8SequenceLength;
        }

        return builder + extension;
    }

    private static byte[] DeriveOutputFileNameKey(byte[] masterKey)
    {
        using var hmac = new HMACSHA256(masterKey);
        return hmac.ComputeHash(
            System.Text.Encoding.ASCII.GetBytes("SafeFile.OutputFilename.v1"));
    }

    private static void ValidateOutputFileNameMode(OutputFileNameMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode), mode, "Unsupported output filename mode.");
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer, string fieldName)
    {
        while (!buffer.IsEmpty)
        {
            var read = stream.Read(buffer);
            if (read == 0)
                throw new InvalidDataException($"Unexpected end of stream while reading {fieldName}.");
            buffer = buffer[read..];
        }
    }

    private static int ReadUpToChunk(Stream stream, byte[] buffer, int count)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(buffer, totalRead, count - totalRead);
            if (read == 0)
                break;
            totalRead += read;
        }

        return totalRead;
    }

    private static async Task<int> ReadUpToChunkAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            totalRead += read;
        }

        return totalRead;
    }

    private static async Task ProduceZipToPipeAsync(
        string sourceFolderPath,
        PipeWriter writer,
        ExcludedFolderMatcher excludedFolders,
        Action<int> bytesRead,
        CancellationToken cancellationToken)
    {
        Exception? completionException = null;
        try
        {
            await using var pipeStream = writer.AsStream(leaveOpen: true);
            await StreamZipper.WriteZipStreamAsync(
                sourceFolderPath,
                pipeStream,
                excludedFolders,
                cancellationToken,
                bytesRead).ConfigureAwait(false);
            await pipeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            completionException = ex;
            throw;
        }
        finally
        {
            await writer.CompleteAsync(completionException).ConfigureAwait(false);
        }
    }

    private static void EnsureDistinctPaths(string sourcePath, string destinationPath)
    {
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var destinationFullPath = Path.GetFullPath(destinationPath);
        if (string.Equals(sourceFullPath, destinationFullPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Source and destination paths must be different.");
    }

    private static void EnsureDestinationOutsideSourceFolder(
        string sourceFolderPath,
        string destinationPath)
    {
        var sourceRoot = Path.GetFullPath(sourceFolderPath);
        var destinationFullPath = Path.GetFullPath(destinationPath);
        var relativePath = Path.GetRelativePath(sourceRoot, destinationFullPath);

        var isOutside = relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath);

        if (!isOutside)
            throw new IOException("Folder vault destination must be outside the source folder.");
    }

    private static void ValidateChunkSize(int chunkSizeBytes)
    {
        if (chunkSizeBytes < VaultHeader.MinimumChunkSize ||
            chunkSizeBytes > VaultHeader.MaximumChunkSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkSizeBytes),
                "Chunk size must be between 1 MiB and 16 MiB.");
        }
    }

    private void ValidatePasswordForEncryption(byte[] passwordBytes)
    {
        if (passwordBytes.Length < _minimumPasswordLength)
        {
            throw new ArgumentException(
                $"Password must be at least {_minimumPasswordLength} bytes.",
                nameof(passwordBytes));
        }
    }

    private static IEnumerable<FileInfo> EnumerateRegularFiles(
        DirectoryInfo directory,
        ExcludedFolderMatcher? excludedFolders = null)
    {
        foreach (var file in directory.GetFiles())
        {
            if (!IsReparsePoint(file))
                yield return file;
        }

        foreach (var subDirectory in directory.GetDirectories())
        {
            if (IsReparsePoint(subDirectory) ||
                excludedFolders?.IsExcluded(subDirectory) == true)
                continue;

            foreach (var file in EnumerateRegularFiles(subDirectory, excludedFolders))
                yield return file;
        }
    }

    private static bool IsReparsePoint(FileSystemInfo item) =>
        (item.Attributes & FileAttributes.ReparsePoint) != 0;

    private static async Task ExtractFolderPreservingPartialOutputAsync(
        Stream zipStream,
        string destinationFolder,
        CancellationToken cancellationToken,
        Action<double>? progressCallback = null)
    {
        if (Directory.Exists(destinationFolder) || File.Exists(destinationFolder))
            throw new IOException($"Destination already exists: {destinationFolder}");

        Directory.CreateDirectory(destinationFolder);
        await StreamZipper.ExtractZipStreamAsync(
            zipStream,
            destinationFolder,
            cancellationToken,
            progressCallback).ConfigureAwait(false);
    }

    private void TryDeleteFile(
        string path,
        FileCleanupKind kind,
        bool causedByCancellation)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation(
                    "Deleted cleanup file {Path} ({CleanupKind}); cancellation: {CausedByCancellation}",
                    path,
                    kind,
                    causedByCancellation);
            }
            else
            {
                _logger.LogInformation(
                    "Cleanup file was already absent: {Path} ({CleanupKind}); cancellation: {CausedByCancellation}",
                    path,
                    kind,
                    causedByCancellation);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to delete cleanup file {Path} ({CleanupKind}); cancellation: {CausedByCancellation}",
                path,
                kind,
                causedByCancellation);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}

public sealed record PerFileProgress(
    string SourceFilePath,
    double Progress,
    PerFileResult Result = PerFileResult.InProgress);

internal enum FileCleanupKind
{
    IncompleteEncryptionOutput,
    TemporaryDecryptionFile
}

public enum PerFileResult
{
    InProgress,
    Succeeded,
    DestinationExists,
    Failed
}

internal sealed class DestinationAlreadyExistsException : IOException
{
    public DestinationAlreadyExistsException(string path, Exception? innerException = null)
        : base($"Destination already exists: {path}", innerException)
    {
    }
}
