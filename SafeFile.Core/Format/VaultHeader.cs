using System.Buffers.Binary;
using System.Text;
using SafeFile.Core.Crypto;

namespace SafeFile.Core.Format;

public sealed class VaultHeader
{
    public static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("SAFE");
    public const byte CurrentVersion = 1;
    public const int SaltSize = 16;
    public const int NoncePrefixSize = 4;
    public const int KdfParamsSize = 12;
    public const int PasswordChecksumSize = 4;
    public const int HeaderSize = 4 + 1 + 1 + KdfParamsSize + SaltSize + NoncePrefixSize + 4 + PasswordChecksumSize;

    public byte Version { get; init; } = CurrentVersion;
    public VaultMode Mode { get; init; }
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

        var headerBytes = new byte[HeaderSize];
        ReadExactly(stream, headerBytes);

        var offset = 0;
        var actualMagic = headerBytes.AsSpan(offset, MagicBytes.Length);

        if (!actualMagic.SequenceEqual(MagicBytes))
        {
            throw new InvalidDataException("Invalid vault file: magic bytes mismatch.");
        }

        offset += MagicBytes.Length;

        var version = headerBytes[offset++];
        if (version != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported vault version: {version}.");
        }

        var mode = (VaultMode)headerBytes[offset++];
        if (!Enum.IsDefined(mode))
        {
            throw new InvalidDataException($"Invalid vault mode: {mode}.");
        }

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
        kdfParameters.Validate();

        if (chunkSize <= 0)
        {
            throw new InvalidDataException("Invalid vault header: chunk size must be greater than zero.");
        }

        return new VaultHeader
        {
            Version = version,
            Mode = mode,
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

        KdfParameters.Validate();

        if (Salt.Length != SaltSize)
        {
            throw new InvalidOperationException($"Salt must be {SaltSize} bytes.");
        }

        if (NoncePrefix.Length != NoncePrefixSize)
        {
            throw new InvalidOperationException($"Nonce prefix must be {NoncePrefixSize} bytes.");
        }

        if (ChunkSize <= 0)
        {
            throw new InvalidOperationException("Chunk size must be greater than zero.");
        }

        if (PasswordChecksum.Length != PasswordChecksumSize)
        {
            throw new InvalidOperationException($"Password checksum must be {PasswordChecksumSize} bytes.");
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading vault header.");
            }

            totalRead += read;
        }
    }
}
