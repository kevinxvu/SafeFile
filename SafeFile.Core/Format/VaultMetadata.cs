using SafeFile.Core.Crypto;

namespace SafeFile.Core.Format;

public sealed record VaultMetadata(
    string SourcePath,
    string OriginalFileName,
    long VaultSizeBytes,
    DateTime LastModifiedUtc,
    byte Version,
    VaultMode Mode,
    bool EncryptFileNames,
    int ChunkSize,
    Argon2Parameters KdfParameters,
    string EncryptionAlgorithm);
