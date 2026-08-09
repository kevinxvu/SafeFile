using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SafeFile.Core.Crypto;

namespace SafeFile.Core.Services;

public sealed class TextCryptoService
{
    public const int MaximumTextCharacters = 1_000_000;
    public const string Prefix = "SAFETEXT1.";

    private const int HeaderSize = 33;
    private const int FixedPayloadOverhead = HeaderSize + AesGcmEngine.TagSize;
    private const long ChunkIndex = 1;
    private static readonly byte[] Magic = "SFTX"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<string> EncryptAsync(
        string plaintext,
        byte[] passwordBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ValidatePassword(passwordBytes);
        ValidateCharacterCount(plaintext);

        byte[]? plaintextBytes = null;
        try
        {
            plaintextBytes = StrictUtf8.GetBytes(plaintext);
            return await EncryptBytesCoreAsync(
                plaintextBytes,
                passwordBytes,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (plaintextBytes is not null)
                CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    public async Task<string> EncryptBytesAsync(
        ReadOnlyMemory<byte> plaintextBytes,
        byte[] passwordBytes,
        int maximumPlaintextBytes,
        CancellationToken cancellationToken = default)
    {
        ValidatePassword(passwordBytes);
        ValidateMaximumPlaintextBytes(maximumPlaintextBytes);
        if (plaintextBytes.Length > maximumPlaintextBytes)
            throw new ArgumentException(
                $"Content cannot exceed {maximumPlaintextBytes:N0} UTF-8 bytes.",
                nameof(plaintextBytes));

        var plaintextCopy = plaintextBytes.ToArray();
        try
        {
            return await EncryptBytesCoreAsync(
                plaintextCopy,
                passwordBytes,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextCopy);
        }
    }

    private static async Task<string> EncryptBytesCoreAsync(
        byte[] plaintextBytes,
        byte[] passwordBytes,
        CancellationToken cancellationToken)
    {
        byte[]? salt = null;
        byte[]? noncePrefix = null;
        byte[]? masterKey = null;
        try
        {
            salt = Argon2Kdf.GenerateSalt();
            noncePrefix = RandomNumberGenerator.GetBytes(AesGcmEngine.NoncePrefixSize);
            masterKey = await KdfDerivation.DeriveKeyAsync(
                passwordBytes,
                salt,
                Argon2Kdf.DefaultParameters,
                cancellationToken).ConfigureAwait(false);

            var encrypted = new AesGcmEngine().EncryptChunk(
                plaintextBytes,
                masterKey,
                noncePrefix,
                ChunkIndex,
                isLastChunk: true);
            var payload = new byte[FixedPayloadOverhead + encrypted.Ciphertext.Length];
            Magic.CopyTo(payload, 0);
            payload[4] = 1;
            salt.CopyTo(payload, 5);
            noncePrefix.CopyTo(payload, 21);
            BinaryPrimitives.WriteInt64LittleEndian(
                payload.AsSpan(25, 8), encrypted.Ciphertext.LongLength);
            encrypted.Ciphertext.CopyTo(payload, HeaderSize);
            encrypted.Tag.CopyTo(payload, HeaderSize + encrypted.Ciphertext.Length);

            return ToBase64Url(payload);
        }
        finally
        {
            if (masterKey is not null)
                CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public async Task<string> DecryptAsync(
        string encryptedText,
        byte[] passwordBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encryptedText);
        ValidatePassword(passwordBytes);
        byte[]? plaintextBytes = null;
        try
        {
            plaintextBytes = await DecryptBytesAsync(
                encryptedText,
                passwordBytes,
                checked(MaximumTextCharacters * 4),
                cancellationToken).ConfigureAwait(false);
            var plaintext = StrictUtf8.GetString(plaintextBytes);
            ValidateCharacterCount(plaintext);
            return plaintext;
        }
        finally
        {
            if (plaintextBytes is not null)
                CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    public async Task<byte[]> DecryptBytesAsync(
        string encryptedText,
        byte[] passwordBytes,
        int maximumPlaintextBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encryptedText);
        ValidatePassword(passwordBytes);
        ValidateMaximumPlaintextBytes(maximumPlaintextBytes);
        var encodedPayload = encryptedText.StartsWith(Prefix, StringComparison.Ordinal)
            ? encryptedText[Prefix.Length..]
            : encryptedText;

        var maximumEncodedCharacters = GetMaximumEncodedCharacters(maximumPlaintextBytes);
        if (encodedPayload.Length > maximumEncodedCharacters)
            throw new InvalidDataException("Encrypted text exceeds the maximum safe length.");

        var payload = FromBase64Url(encodedPayload);
        try
        {
            if (payload.Length < FixedPayloadOverhead ||
                !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
                payload[4] != 1)
            {
                throw new InvalidDataException("Encrypted text has an invalid format or unsupported version.");
            }

            var ciphertextLength = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(25, 8));
            if (ciphertextLength < 0 || ciphertextLength > maximumPlaintextBytes ||
                ciphertextLength != payload.LongLength - FixedPayloadOverhead)
            {
                throw new InvalidDataException("Encrypted text contains an invalid or unsafe content length.");
            }

            var salt = payload.AsSpan(5, Argon2Kdf.SaltSize).ToArray();
            var noncePrefix = payload.AsSpan(21, AesGcmEngine.NoncePrefixSize).ToArray();
            var ciphertext = payload.AsSpan(HeaderSize, (int)ciphertextLength).ToArray();
            var tag = payload.AsSpan(HeaderSize + (int)ciphertextLength, AesGcmEngine.TagSize).ToArray();
            byte[]? masterKey = null;
            try
            {
                masterKey = await KdfDerivation.DeriveKeyAsync(
                    passwordBytes,
                    salt,
                    Argon2Kdf.DefaultParameters,
                    cancellationToken).ConfigureAwait(false);
                return new AesGcmEngine().DecryptChunk(
                    new EncryptedChunk(ChunkIndex, noncePrefix, ciphertext, tag, true),
                    masterKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                if (masterKey is not null)
                    CryptographicOperations.ZeroMemory(masterKey);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public static long GetMaximumEncodedCharacters(int maximumPlaintextBytes)
    {
        ValidateMaximumPlaintextBytes(maximumPlaintextBytes);
        var maximumPayloadBytes = checked((long)maximumPlaintextBytes + FixedPayloadOverhead);
        return checked((maximumPayloadBytes + 2) / 3 * 4);
    }

    public static string ComputeSha256Hex(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateCharacterCount(content);
        var bytes = StrictUtf8.GetBytes(content);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static bool IsEncryptedText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var encoded = value.StartsWith(Prefix, StringComparison.Ordinal)
            ? value[Prefix.Length..]
            : value;
        if (encoded.Length < 7)
            return false;
        try
        {
            var sample = FromBase64Url(encoded[..Math.Min(8, encoded.Length)]);
            return sample.Length >= 5 &&
                   sample.AsSpan(0, Magic.Length).SequenceEqual(Magic) &&
                   sample[4] == 1;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static string ComputeSha256Base64(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateCharacterCount(content);
        var bytes = StrictUtf8.GetBytes(content);
        try
        {
            return Convert.ToBase64String(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ValidateCharacterCount(string text)
    {
        if (text.Length > MaximumTextCharacters)
            throw new ArgumentException($"Text cannot exceed {MaximumTextCharacters:N0} characters.");
    }

    private static void ValidateMaximumPlaintextBytes(int maximumPlaintextBytes)
    {
        if (maximumPlaintextBytes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maximumPlaintextBytes),
                "The maximum plaintext size must be positive.");
    }

    private static void ValidatePassword(byte[] passwordBytes)
    {
        ArgumentNullException.ThrowIfNull(passwordBytes);
        if (passwordBytes.Length == 0)
            throw new ArgumentException("Password cannot be empty.", nameof(passwordBytes));
    }

    private static string ToBase64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        if (value.Length == 0 || value.Any(c =>
                !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidDataException("Encrypted text is not valid Base64URL.");
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(base64 + new string('=', (4 - base64.Length % 4) % 4));
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Encrypted text is not valid Base64URL.", ex);
        }
    }
}
