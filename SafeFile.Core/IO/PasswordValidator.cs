using System.Security.Cryptography;

namespace SafeFile.Core.IO;

/// <summary>
/// Password validation helper using HMAC-SHA256-based checksum.
/// Checksum is stored in vault header to enable early password validation without full decryption.
/// </summary>
public sealed class PasswordValidator
{
    public const int ChecksumSize = 4; // First 4 bytes of HMAC-SHA256 hash
    /// <summary>
    /// Validate that a password string meets the minimum length requirement.
    /// </summary>
    public static bool IsPasswordLengthValid(string password, int minimumLength)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (minimumLength < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumLength));
        return password.Length >= minimumLength;
    }

    /// <summary>
    /// Compute a key verifier after the password has passed through Argon2.
    /// This must never be called with the raw password because doing so would
    /// provide a cheap offline password oracle.
    /// </summary>
    public static byte[] ComputeChecksum(byte[] derivedKey, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(derivedKey);
        ArgumentNullException.ThrowIfNull(salt);

        using var hmac = new HMACSHA256(salt);
        var hash = hmac.ComputeHash(derivedKey);
        return hash.AsSpan(0, ChecksumSize).ToArray();
    }

    /// <summary>
    /// Validate a stored checksum against a password and salt.
    /// Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    public static bool ValidateChecksum(byte[] checksum, byte[] derivedKey, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(checksum);
        ArgumentNullException.ThrowIfNull(derivedKey);
        ArgumentNullException.ThrowIfNull(salt);

        if (checksum.Length != ChecksumSize)
            return false;

        var computed = ComputeChecksum(derivedKey, salt);
        try
        {
            return CryptographicOperations.FixedTimeEquals(checksum, computed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(computed);
        }
    }
}
