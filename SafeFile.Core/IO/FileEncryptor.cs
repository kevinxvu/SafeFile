using System.Buffers;
using System.Buffers.Binary;
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

        if (passwordBytes.Length == 0)
            throw new ArgumentException("Password cannot be empty.", nameof(passwordBytes));

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

            var encryptedFileNameChunk = new AesGcmEngine().EncryptChunk(
                encryptedFileName,
                masterKey,
                noncePrefix,
                0,  // Filename is chunk 0
                true);

            WriteEncryptedFileNameChunk(destStream, encryptedFileNameChunk);

            var zipSize = zipStream.Length;

            // Calculate total chunks for accurate progress reporting
            var totalChunks = (zipSize + chunkSizeBytes - 1) / chunkSizeBytes;

            // Lock for thread-safe dest stream writes (multiple consumer threads)
            var destLock = new object();

            // Use pipeline for efficient parallel encryption
            await _pipeline.EncryptAsync(
                async (chunkIndex) =>
                {
                    var zipBuffer = ArrayPool<byte>.Shared.Rent(chunkSizeBytes);
                    try
                    {
                        // ProducerAsync calls this sequentially with chunkIndex = 0, 1, 2...
                        // So zipStream.Read is never called in parallel
                        int bytesRead = zipStream.Read(zipBuffer, 0, chunkSizeBytes);
                        if (bytesRead == 0)
                            return null;

                        // Calculate isLastChunk based on whether we've reached stream end
                        var remainingInZip = zipSize - (chunkIndex * (long)chunkSizeBytes + bytesRead);
                        var isLast = remainingInZip <= 0;

                        return new UnencryptedChunk(
                            Index: chunkIndex,
                            Data: zipBuffer.AsSpan(0, bytesRead).ToArray(),
                            IsLastChunk: isLast);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(zipBuffer);
                    }
                },
                async (encryptedChunk) =>
                {
                    // Multiple consumer threads may call this at same time
                    // Lock protects destStream.Write from concurrent access
                    await Task.Run(() =>
                    {
                        lock (destLock)
                        {
                            WriteEncryptedChunk(destStream, encryptedChunk);
                        }
                    }, cancellationToken).ConfigureAwait(false);
                },
                masterKey,
                noncePrefix,
                totalChunks: totalChunks,
                cancellationToken: cancellationToken);

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

        if (passwordBytes.Length == 0)
            throw new ArgumentException("Password cannot be empty.", nameof(passwordBytes));

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

                // Validate filename chunk index
                if (encryptedFileNameChunk.Index != 0)
                    throw new InvalidDataException($"Expected filename chunk index 0, got {encryptedFileNameChunk.Index}.");

                var decryptedFileName = aesGcm.DecryptChunk(encryptedFileNameChunk, masterKey);

                var memoryStream = new MemoryStream();
                long expectedChunkIndex = 1;  // Data chunks start at index 1

                while (true)
                {
                    var encryptedChunk = ReadEncryptedChunk(sourceStream);
                    if (encryptedChunk is null)
                        break;

                    // Validate chunk index matches expected order
                    if (encryptedChunk.Index != expectedChunkIndex)
                        throw new InvalidDataException(
                            $"Chunk index mismatch: expected {expectedChunkIndex}, got {encryptedChunk.Index}. " +
                            $"This indicates file corruption or tampering.");

                    var plaintext = aesGcm.DecryptChunk(encryptedChunk, masterKey);
                    memoryStream.Write(plaintext);

                    expectedChunkIndex++;

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

        if (passwordBytes.Length == 0)
            throw new ArgumentException("Password cannot be empty.", nameof(passwordBytes));

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

            var aesGcm = new AesGcmEngine();

            var encryptedFileNameChunk = aesGcm.EncryptChunk(
                encryptedFileName,
                masterKey,
                noncePrefix,
                0,  // Filename chunk always has index 0
                true);

            WriteEncryptedFileNameChunk(destStream, encryptedFileNameChunk);

            using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var fileSize = sourceStream.Length;

            // Calculate total number of chunks for progress tracking
            var totalChunks = (fileSize + chunkSizeBytes - 1) / chunkSizeBytes;

            // Lock for thread-safe dest stream writes (multiple consumer threads)
            var destLock = new object();

            // Use pipeline for efficient parallel encryption
            await _pipeline.EncryptAsync(
                async (chunkIndex) =>
                {
                    var sourceBuffer = ArrayPool<byte>.Shared.Rent(chunkSizeBytes);
                    try
                    {
                        // ProducerAsync calls this sequentially with chunkIndex = 0, 1, 2...
                        // So sourceStream.Read is never called in parallel
                        int bytesRead = sourceStream.Read(sourceBuffer, 0, chunkSizeBytes);
                        if (bytesRead == 0)
                            return null;

                        // Calculate isLastChunk based on whether we've reached file end
                        var remainingInFile = fileSize - (chunkIndex * (long)chunkSizeBytes + bytesRead);
                        var isLast = remainingInFile <= 0;

                        return new UnencryptedChunk(
                            Index: chunkIndex,
                            Data: sourceBuffer.AsSpan(0, bytesRead).ToArray(),
                            IsLastChunk: isLast);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(sourceBuffer);
                    }
                },
                async (encryptedChunk) =>
                {
                    // Multiple consumer threads may call this at same time
                    // Writer also calls this, and it buffers/reorders
                    // Lock protects destStream.Write from concurrent access
                    await Task.Run(() =>
                    {
                        lock (destLock)
                        {
                            WriteEncryptedChunk(destStream, encryptedChunk);
                        }
                    }, cancellationToken).ConfigureAwait(false);
                },
                masterKey,
                noncePrefix,
                totalChunks: totalChunks,
                cancellationToken: cancellationToken);

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

        if (passwordBytes.Length == 0)
            throw new ArgumentException("Password cannot be empty.", nameof(passwordBytes));

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

                // Validate filename chunk index
                if (encryptedFileNameChunk.Index != 0)
                    throw new InvalidDataException($"Expected filename chunk index 0, got {encryptedFileNameChunk.Index}.");

                var decryptedFileName = aesGcm.DecryptChunk(encryptedFileNameChunk, masterKey);
                var originalFileName = System.Text.Encoding.UTF8.GetString(decryptedFileName);

                using var destStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

                long expectedChunkIndex = 1;  // Data chunks start at index 1
                while (true)
                {
                    var encryptedChunk = ReadEncryptedChunk(sourceStream);
                    if (encryptedChunk is null)
                        break;

                    // Validate chunk index matches expected order
                    if (encryptedChunk.Index != expectedChunkIndex)
                        throw new InvalidDataException(
                            $"Chunk index mismatch: expected {expectedChunkIndex}, got {encryptedChunk.Index}. " +
                            $"This indicates file corruption or tampering.");

                    var plaintext = aesGcm.DecryptChunk(encryptedChunk, masterKey);
                    destStream.Write(plaintext);

                    expectedChunkIndex++;

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
        // Write full nonce (4-byte prefix + 8-byte chunk index = 12 bytes)
        var fullNonce = new byte[12];
        chunk.NoncePrefix.CopyTo(fullNonce, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(fullNonce.AsSpan(4, 8), (ulong)chunk.Index);

        var ciphertextSize = chunk.Ciphertext.Length;

        stream.Write(BitConverter.GetBytes(12));  // Full nonce size is always 12
        stream.Write(fullNonce);
        stream.Write(BitConverter.GetBytes(ciphertextSize));
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

        // Read isLastChunk flag (1 byte)
        int isLastChunkByte = stream.ReadByte();
        if (isLastChunkByte == -1)
            throw new InvalidDataException("Unexpected end of stream while reading isLastChunk flag.");
        var isLastChunk = isLastChunkByte == 1;

        // Extract chunk index from nonce (bytes 4-11 contain the chunk index as uint64)
        if (nonceSize < 12)
            throw new InvalidDataException($"Nonce must be at least 12 bytes, got {nonceSize}.");

        var noncePrefix = new byte[4];
        Array.Copy(nonce, 0, noncePrefix, 0, 4);

        var chunkIndexBytes = nonce.AsSpan(4, 8);
        var chunkIndex = (long)BinaryPrimitives.ReadUInt64LittleEndian(chunkIndexBytes);

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
    private static EncryptedChunk? ReadEncryptedFileNameChunk(Stream stream)
    {
        return ReadEncryptedChunk(stream);
    }
}
