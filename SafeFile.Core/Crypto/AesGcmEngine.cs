using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SafeFile.Core.Crypto;

public sealed class AesGcmEngine
{
    public const int NonceSize = 12;
    public const int NoncePrefixSize = 4;
    public const int TagSize = 16;

    public EncryptedChunk EncryptChunk(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> noncePrefix, long chunkIndex, bool isLastChunk)
    {
        ValidateInputs(key, noncePrefix, chunkIndex);

        var nonce = BuildNonce(noncePrefix, chunkIndex);
        var aad = BuildAad(chunkIndex, isLastChunk);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        return new EncryptedChunk(chunkIndex, noncePrefix.ToArray(), ciphertext, tag, isLastChunk);
    }

    public byte[] DecryptChunk(EncryptedChunk chunk, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(chunk.Ciphertext);
        ArgumentNullException.ThrowIfNull(chunk.Tag);

        ValidateInputs(key, chunk.NoncePrefix, chunk.Index);

        var plaintext = new byte[chunk.Ciphertext.Length];
        var nonce = BuildNonce(chunk.NoncePrefix, chunk.Index);
        var aad = BuildAad(chunk.Index, chunk.IsLastChunk);

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Decrypt(nonce, chunk.Ciphertext, chunk.Tag, plaintext, aad);

        return plaintext;
    }

    public static byte[] BuildAad(long chunkIndex, bool isLastChunk)
    {
        var aad = new byte[9];
        BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(0, 8), chunkIndex);
        aad[8] = isLastChunk ? (byte)1 : (byte)0;
        return aad;
    }

    public static byte[] BuildNonce(ReadOnlySpan<byte> noncePrefix, long chunkIndex)
    {
        if (noncePrefix.Length != NoncePrefixSize)
        {
            throw new ArgumentException($"Nonce prefix must be {NoncePrefixSize} bytes.", nameof(noncePrefix));
        }

        var nonce = new byte[NonceSize];
        noncePrefix.CopyTo(nonce.AsSpan(0, NoncePrefixSize));
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(NoncePrefixSize, 8), (ulong)chunkIndex);
        return nonce;
    }

    private static void ValidateInputs(ReadOnlySpan<byte> key, ReadOnlySpan<byte> noncePrefix, long chunkIndex)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("AES-256-GCM key must be 32 bytes.", nameof(key));
        }

        if (noncePrefix.Length != NoncePrefixSize)
        {
            throw new ArgumentException($"Nonce prefix must be {NoncePrefixSize} bytes.", nameof(noncePrefix));
        }

        if (chunkIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex), "Chunk index must be non-negative.");
        }
    }
}

public sealed record EncryptedChunk(long Index, byte[] NoncePrefix, byte[] Ciphertext, byte[] Tag, bool IsLastChunk);
