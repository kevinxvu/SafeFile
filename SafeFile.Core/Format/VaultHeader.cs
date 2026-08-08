using System.Buffers.Binary;
using System.Text;
using SafeFile.Core.Crypto;

namespace SafeFile.Core.Format;

public sealed class VaultHeader
{
    private const byte ProtectOutputFileNameFlag = 1 << 0;
    private const byte Sha256OutputFileNameFlag = 1 << 1;
    private const byte Md5OutputFileNameFlag = 1 << 2;
    private const byte KnownFlags = ProtectOutputFileNameFlag |
                                    Sha256OutputFileNameFlag |
                                    Md5OutputFileNameFlag;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SAFE");
    public static ReadOnlySpan<byte> MagicBytes => Magic;
    public const byte CurrentVersion = 1;
    public const int SaltSize = 16;
    public const int NoncePrefixSize = 4;
    public const int KdfParamsSize = 12;
    public const int PasswordChecksumSize = 4;
    public const int MinimumChunkSize = 1_048_576;
    public const int MaximumChunkSize = 16_777_216;
    public const int HeaderSize = 4 + 1 + 1 + 1 + KdfParamsSize + SaltSize + NoncePrefixSize + 4 + PasswordChecksumSize;

    public byte Version { get; init; } = CurrentVersion;
    public VaultMode Mode { get; init; }
    public OutputFileNameMode OutputFileNameMode { get; init; }
    public Argon2Parameters KdfParameters { get; init; } = Argon2Kdf.DefaultParameters;
    public byte[] Salt { get; init; } = Array.Empty<byte>();
    public byte[] NoncePrefix { get; init; } = Array.Empty<byte>();
    public int ChunkSize { get; init; }
    public byte[] PasswordChecksum { get; init; } = Array.Empty<byte>();

    public void WriteTo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ValidateForWrite();

        Span<byte> fixedBuffer = stackalloc byte[HeaderSize];
        var offset = 0;

        MagicBytes.CopyTo(fixedBuffer[offset..]);
        offset += MagicBytes.Length;

        fixedBuffer[offset++] = Version;
        fixedBuffer[offset++] = (byte)Mode;
        fixedBuffer[offset++] = GetFlags(OutputFileNameMode);

        BinaryPrimitives.WriteInt32LittleEndian(fixedBuffer[offset..], KdfParameters.MemorySizeKb);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(fixedBuffer[offset..], KdfParameters.Iterations);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(fixedBuffer[offset..], KdfParameters.Parallelism);
        offset += 4;

        Salt.CopyTo(fixedBuffer[offset..]);
        offset += SaltSize;

        NoncePrefix.CopyTo(fixedBuffer[offset..]);
        offset += NoncePrefixSize;

        BinaryPrimitives.WriteInt32LittleEndian(fixedBuffer[offset..], ChunkSize);
        offset += 4;

        PasswordChecksum.CopyTo(fixedBuffer[offset..]);

        stream.Write(fixedBuffer);
    }

    public static VaultHeader ReadFrom(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var prefix = new byte[MagicBytes.Length + 1];
        ReadExactly(stream, prefix);

        if (!prefix.AsSpan(0, MagicBytes.Length).SequenceEqual(MagicBytes))
            throw new InvalidDataException("Invalid vault file: magic bytes mismatch.");

        var version = prefix[MagicBytes.Length];
        if (version != CurrentVersion)
            throw new InvalidDataException($"Unsupported vault version: {version}.");

        var headerBytes = new byte[HeaderSize];
        prefix.CopyTo(headerBytes, 0);
        ReadExactly(stream, headerBytes.AsSpan(prefix.Length));

        var offset = 0;
        var actualMagic = headerBytes.AsSpan(offset, MagicBytes.Length);

        if (!actualMagic.SequenceEqual(MagicBytes))
        {
            throw new InvalidDataException("Invalid vault file: magic bytes mismatch.");
        }

        offset += MagicBytes.Length;

        offset++;

        var mode = (VaultMode)headerBytes[offset++];
        if (!Enum.IsDefined(mode))
        {
            throw new InvalidDataException($"Invalid vault mode: {mode}.");
        }

        var flags = headerBytes[offset++];
        if ((flags & ~KnownFlags) != 0)
            throw new InvalidDataException($"Invalid vault flags: {flags}.");
        var outputFileNameMode = flags switch
        {
            0 => OutputFileNameMode.None,
            ProtectOutputFileNameFlag => OutputFileNameMode.Aes,
            ProtectOutputFileNameFlag | Sha256OutputFileNameFlag => OutputFileNameMode.Sha256,
            ProtectOutputFileNameFlag | Md5OutputFileNameFlag => OutputFileNameMode.Md5,
            _ => throw new InvalidDataException($"Invalid vault flags: {flags}.")
        };

        var memorySizeKb = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(offset, 4));
        offset += 4;
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(offset, 4));
        offset += 4;
        var parallelism = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(offset, 4));
        offset += 4;

        var salt = headerBytes.AsSpan(offset, SaltSize).ToArray();
        offset += SaltSize;

        var noncePrefix = headerBytes.AsSpan(offset, NoncePrefixSize).ToArray();
        offset += NoncePrefixSize;

        var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(offset, 4));
        offset += 4;

        var passwordChecksum = headerBytes.AsSpan(offset, PasswordChecksumSize).ToArray();

        var kdfParameters = new Argon2Parameters(memorySizeKb, iterations, parallelism);
        try
        {
            kdfParameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException("Invalid or unsafe Argon2 parameters in vault header.", ex);
        }

        if (chunkSize < MinimumChunkSize || chunkSize > MaximumChunkSize)
        {
            throw new InvalidDataException("Invalid vault header: chunk size must be between 1 MiB and 16 MiB.");
        }

        return new VaultHeader
        {
            Version = version,
            Mode = mode,
            OutputFileNameMode = outputFileNameMode,
            KdfParameters = kdfParameters,
            Salt = salt,
            NoncePrefix = noncePrefix,
            ChunkSize = chunkSize,
            PasswordChecksum = passwordChecksum
        };
    }

    private void ValidateForWrite()
    {
        if (Version != CurrentVersion)
        {
            throw new InvalidOperationException($"Unsupported vault version: {Version}.");
        }

        if (!Enum.IsDefined(Mode))
        {
            throw new InvalidOperationException($"Unsupported vault mode: {Mode}.");
        }

        if (!Enum.IsDefined(OutputFileNameMode))
        {
            throw new InvalidOperationException(
                $"Unsupported output filename mode: {OutputFileNameMode}.");
        }

        KdfParameters.Validate();

        if (Salt.Length != SaltSize)
        {
            throw new InvalidOperationException($"Salt must be {SaltSize} bytes.");
        }

        if (NoncePrefix.Length != NoncePrefixSize)
        {
            throw new InvalidOperationException($"Nonce prefix must be {NoncePrefixSize} bytes.");
        }

        if (ChunkSize < MinimumChunkSize || ChunkSize > MaximumChunkSize)
        {
            throw new InvalidOperationException("Chunk size must be between 1 MiB and 16 MiB.");
        }

        if (PasswordChecksum.Length != PasswordChecksumSize)
        {
            throw new InvalidOperationException($"Password checksum must be {PasswordChecksumSize} bytes.");
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var read = stream.Read(buffer);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of stream while reading vault header.");
            buffer = buffer[read..];
        }
    }

    private static byte GetFlags(OutputFileNameMode mode) => mode switch
    {
        OutputFileNameMode.None => 0,
        OutputFileNameMode.Aes => ProtectOutputFileNameFlag,
        OutputFileNameMode.Sha256 => ProtectOutputFileNameFlag | Sha256OutputFileNameFlag,
        OutputFileNameMode.Md5 => ProtectOutputFileNameFlag | Md5OutputFileNameFlag,
        _ => throw new InvalidOperationException($"Unsupported output filename mode: {mode}.")
    };
}
