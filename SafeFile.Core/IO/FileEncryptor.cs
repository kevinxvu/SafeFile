using System.Buffers;
using System.Security.Cryptography;
using SafeFile.Core.Crypto;
using SafeFile.Core.Format;
using SafeFile.Core.IO;
using SafeFile.Core.Pipeline;

namespace SafeFile.Core.IO;

public sealed class FileEncryptor
{
    private readonly Argon2Kdf _kdf = new();
    private readonly CryptoPipeline _pipeline;
    private readonly IProgress<double>? _progress;

    public FileEncryptor(int consumerThreads = -1, IProgress<double>? progress = null)
    {
        _progress = progress;
        _pipeline = new CryptoPipeline(consumerThreads, progress);
    }

    public async Task EncryptFolderZipAsync(
        string sourceFolderPath,
        string destinationPath,
        byte[] passwordBytes,
        int chunkSizeBytes = 1_048_576,
        Argon2Parameters? kdfParams = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFolderPath);
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(passwordBytes);

        if (!Directory.Exists(sourceFolderPath))
            throw new DirectoryNotFoundException($"Source folder not found: {sourceFolderPath}");

        var folderName = Path.GetFileName(sourceFolderPath);
        var encryptedFileName = System.Text.Encoding.UTF8.GetBytes(folderName + ".zip");

        try
        {
            var zipStream = await StreamZipper.CreateZipStreamAsync(sourceFolderPath).ConfigureAwait(false);

            using var destStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var salt = Argon2Kdf.GenerateSalt();
            var noncePrefix = new byte[4];
            RandomNumberGenerator.Fill(noncePrefix);

            var effectiveKdfParams = kdfParams ?? Argon2Kdf.DefaultParameters;
            var masterKey = _kdf.DeriveKey(passwordBytes, salt, effectiveKdfParams);
            var passwordChecksum = PasswordValidator.ComputeChecksum(passwordBytes, salt);

            var header = new VaultHeader
            {
                Mode = VaultMode.Zip,
                KdfParameters = effectiveKdfParams,
                Salt = salt,
                NoncePrefix = noncePrefix,
                ChunkSize = chunkSizeBytes,
                PasswordChecksum = passwordChecksum
            };

            header.WriteTo(destStream);

            var aesGcm = new AesGcmEngine();

            var encryptedFileNameChunk = aesGcm.EncryptChunk(
                encryptedFileName,
                masterKey,
                noncePrefix,
                -1,
                true);

            WriteEncryptedFileNameChunk(destStream, encryptedFileNameChunk);

            var zipSize = zipStream.Length;
            var totalRead = 0L;
            var chunkIndex = 0L;

            // Calculate total chunks for accurate progress reporting
            var totalChunks = (zipSize + chunkSizeBytes - 1) / chunkSizeBytes + 1; // +1 for filename chunk
            var pipelineWithProgress = new CryptoPipeline(
                Math.Max(1, Environment.ProcessorCount - 1),
                _progress,
                totalChunks);

            var zipBuffer = ArrayPool<byte>.Shared.Rent(chunkSizeBytes);
            try
            {
                while (totalRead < zipSize && !cancellationToken.IsCancellationRequested)
                {
                    var bytesRead = zipStream.Read(zipBuffer, 0, chunkSizeBytes);
                    if (bytesRead == 0)
                        break;

                    var isLast = totalRead + bytesRead >= zipSize;
                    var encryptedChunk = aesGcm.EncryptChunk(
                        zipBuffer.AsSpan(0, bytesRead),
                        masterKey,
                        noncePrefix,
                        chunkIndex,
                        isLast);

                    WriteEncryptedChunk(destStream, encryptedChunk);

                    totalRead += bytesRead;
                    chunkIndex++;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(zipBuffer);
                zipStream.Dispose();
            }

            CryptographicOperations.ZeroMemory(masterKey);
        }
        catch
        {
            if (File.Exists(destinationPath))
            {
                try
                {
                    File.Delete(destinationPath);
                }
                catch
                {
                }
            }

            throw;
        }
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

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Source file not found: {sourcePath}");

        try
        {
            using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var header = VaultHeader.ReadFrom(sourceStream);
            if (header.Mode != VaultMode.Zip)
                throw new InvalidDataException("Vault is not in Zip mode.");

            // Validate password checksum early to fail fast on wrong password
            if (!PasswordValidator.ValidateChecksum(header.PasswordChecksum, passwordBytes, header.Salt))
                throw new InvalidOperationException("Invalid password or corrupted vault file (checksum mismatch).");

            var masterKey = _kdf.DeriveKey(passwordBytes, header.Salt, header.KdfParameters);
            var aesGcm = new AesGcmEngine();

            try
            {
                var encryptedFileNameChunk = ReadEncryptedFileNameChunk(sourceStream);
                if (encryptedFileNameChunk is null)
                    throw new InvalidDataException("Missing encrypted filename chunk in vault file.");

                var decryptedFileName = aesGcm.DecryptChunk(encryptedFileNameChunk, masterKey);

                var memoryStream = new MemoryStream();
                long chunkIndex = 0;

                while (true)
                {
                    var encryptedChunk = ReadEncryptedChunk(sourceStream);
                    if (encryptedChunk is null)
                        break;

                    var plaintext = aesGcm.DecryptChunk(encryptedChunk, masterKey);
                    memoryStream.Write(plaintext);

                    chunkIndex++;

                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException();
                }

                memoryStream.Position = 0;
                await StreamZipper.ExtractZipStreamAsync(memoryStream, destinationFolder).ConfigureAwait(false);
                memoryStream.Dispose();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
        }
        catch
        {
            if (Directory.Exists(destinationFolder))
            {
                try
                {
                    Directory.Delete(destinationFolder, recursive: true);
                }
                catch
                {
                }
            }

            throw;
        }
    }

    public async Task EncryptFileAsync(
        string sourcePath,
        string destinationPath,
        byte[] passwordBytes,
        int chunkSizeBytes = 1_048_576,
        Argon2Parameters? kdfParams = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(passwordBytes);

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Source file not found: {sourcePath}");

        if (chunkSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSizeBytes), "Chunk size must be greater than zero.");

        var fileInfo = new FileInfo(sourcePath);
        var originalFileName = fileInfo.Name;
        var encryptedFileName = System.Text.Encoding.UTF8.GetBytes(originalFileName);

        try
        {
            using var destStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var salt = Argon2Kdf.GenerateSalt();
            var noncePrefix = new byte[4];
            RandomNumberGenerator.Fill(noncePrefix);

            var effectiveKdfParams = kdfParams ?? Argon2Kdf.DefaultParameters;
            var masterKey = _kdf.DeriveKey(passwordBytes, salt, effectiveKdfParams);

            var header = new VaultHeader
            {
                Mode = VaultMode.File,
                KdfParameters = effectiveKdfParams,
                Salt = salt,
                NoncePrefix = noncePrefix,
                ChunkSize = chunkSizeBytes
            };

            header.WriteTo(destStream);

            var pipeline = new CryptoPipeline(-1, null);
            var aesGcm = new AesGcmEngine();

            var encryptedFileNameChunk = aesGcm.EncryptChunk(
                encryptedFileName,
                masterKey,
                noncePrefix,
                -1,
                true);

            WriteEncryptedFileNameChunk(destStream, encryptedFileNameChunk);

            using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var fileSize = sourceStream.Length;
            var totalRead = 0L;
            var chunkIndex = 0L;

            var sourceBuffer = ArrayPool<byte>.Shared.Rent(chunkSizeBytes);
            try
            {
                while (totalRead < fileSize && !cancellationToken.IsCancellationRequested)
                {
                    var bytesRead = sourceStream.Read(sourceBuffer, 0, chunkSizeBytes);
                    if (bytesRead == 0)
                        break;

                    var isLast = totalRead + bytesRead >= fileSize;
                    var encryptedChunk = aesGcm.EncryptChunk(
                        sourceBuffer.AsSpan(0, bytesRead),
                        masterKey,
                        noncePrefix,
                        chunkIndex,
                        isLast);

                    WriteEncryptedChunk(destStream, encryptedChunk);

                    totalRead += bytesRead;
                    chunkIndex++;

                    var progress = (double)totalRead / fileSize;
                    ((IProgress<double>?)null)?.Report(progress);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sourceBuffer);
            }

            if (totalRead != fileSize)
                throw new InvalidOperationException($"File read incomplete: expected {fileSize} bytes, got {totalRead}.");

            CryptographicOperations.ZeroMemory(masterKey);
        }
        catch
        {
            if (File.Exists(destinationPath))
            {
                try
                {
                    File.Delete(destinationPath);
                }
                catch
                {
                }
            }

            throw;
        }
    }

    public async Task DecryptFileAsync(
        string sourcePath,
        string destinationPath,
        byte[] passwordBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(passwordBytes);

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Source file not found: {sourcePath}");

        try
        {
            using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var header = VaultHeader.ReadFrom(sourceStream);

            // Validate password checksum early to fail fast on wrong password
            if (!PasswordValidator.ValidateChecksum(header.PasswordChecksum, passwordBytes, header.Salt))
                throw new InvalidOperationException("Invalid password or corrupted vault file (checksum mismatch).");

            var masterKey = _kdf.DeriveKey(passwordBytes, header.Salt, header.KdfParameters);
            var aesGcm = new AesGcmEngine();

            try
            {
                var encryptedFileNameChunk = ReadEncryptedFileNameChunk(sourceStream);
                if (encryptedFileNameChunk is null)
                    throw new InvalidDataException("Missing encrypted filename chunk in vault file.");

                var decryptedFileName = aesGcm.DecryptChunk(encryptedFileNameChunk, masterKey);
                var originalFileName = System.Text.Encoding.UTF8.GetString(decryptedFileName);

                using var destStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

                long chunkIndex = 0;
                while (true)
                {
                    var encryptedChunk = ReadEncryptedChunk(sourceStream);
                    if (encryptedChunk is null)
                        break;

                    var plaintext = aesGcm.DecryptChunk(encryptedChunk, masterKey);
                    destStream.Write(plaintext);

                    chunkIndex++;

                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
        }
        catch
        {
            if (File.Exists(destinationPath))
            {
                try
                {
                    File.Delete(destinationPath);
                }
                catch
                {
                }
            }

            throw;
        }
    }

    private static void WriteEncryptedChunk(Stream stream, EncryptedChunk chunk)
    {
        var nonceSize = chunk.NoncePrefix.Length;
        var ciphertextSize = chunk.Ciphertext.Length;
        var tagSize = chunk.Tag.Length;

        stream.Write(BitConverter.GetBytes(nonceSize));
        stream.Write(chunk.NoncePrefix);
        stream.Write(BitConverter.GetBytes(ciphertextSize));
        stream.Write(chunk.Ciphertext);
        stream.Write(chunk.Tag);
    }

    /// <summary>
    /// Writes encrypted filename chunk with 2-byte length prefix for safe parsing.
    /// Format: [length:2B][nonceSize:4B][nonce:nonceSize][ciphertextSize:4B][ciphertext:ciphertextSize][tag:16B]
    /// </summary>
    private static void WriteEncryptedFileNameChunk(Stream stream, EncryptedChunk chunk)
    {
        var ciphertextSize = (ushort)chunk.Ciphertext.Length;
        stream.Write(BitConverter.GetBytes(ciphertextSize));
        WriteEncryptedChunk(stream, chunk);
    }

    private static EncryptedChunk? ReadEncryptedChunk(Stream stream)
    {
        Span<byte> sizeBuffer = stackalloc byte[4];

        if (stream.Read(sizeBuffer) < 4)
            return null;

        var nonceSize = BitConverter.ToInt32(sizeBuffer);
        var nonce = new byte[nonceSize];

        if (stream.Read(nonce) < nonceSize)
            throw new InvalidDataException("Unexpected end of stream while reading nonce.");

        if (stream.Read(sizeBuffer) < 4)
            return null;

        var ciphertextSize = BitConverter.ToInt32(sizeBuffer);
        var ciphertext = new byte[ciphertextSize];

        if (stream.Read(ciphertext) < ciphertextSize)
            throw new InvalidDataException("Unexpected end of stream while reading ciphertext.");

        var tag = new byte[16];
        if (stream.Read(tag) < 16)
            throw new InvalidDataException("Unexpected end of stream while reading authentication tag.");

        return new EncryptedChunk(
            Index: 0,
            NoncePrefix: nonce,
            Ciphertext: ciphertext,
            Tag: tag,
            IsLastChunk: false);
    }

    /// <summary>
    /// Reads encrypted filename chunk with 2-byte length prefix validation.
    /// </summary>
    private static EncryptedChunk? ReadEncryptedFileNameChunk(Stream stream)
    {
        Span<byte> lengthBuffer = stackalloc byte[2];
        if (stream.Read(lengthBuffer) < 2)
            return null;

        var ciphertextSize = BitConverter.ToUInt16(lengthBuffer);
        if (ciphertextSize == 0)
            throw new InvalidDataException($"Invalid encrypted filename size: {ciphertextSize}");

        var chunk = ReadEncryptedChunk(stream);
        if (chunk is null)
            throw new InvalidDataException("Unexpected end of stream while reading encrypted filename chunk.");

        if (chunk.Ciphertext.Length != ciphertextSize)
            throw new InvalidDataException($"Encrypted filename size mismatch: expected {ciphertextSize}, got {chunk.Ciphertext.Length}");

        return chunk;
    }
}
