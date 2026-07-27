using System.Security.Cryptography;

namespace SafeFile.Core.IO;

/// <summary>
/// Password validation helper using HMAC-SHA256-based checksum.
/// Checksum is stored in vault header to enable early password validation without full decryption.
/// </summary>
public sealed class PasswordValidator
{
    public const int ChecksumSize = 4; // First 4 bytes of HMAC-SHA256 hash
    public const int MinPasswordLength = 8;

    /// <summary>
    /// Validate that a password string meets the minimum length requirement.
    /// </summary>
    public static bool IsPasswordLengthValid(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return password.Length >= MinPasswordLength;
    }

    /// <summary>
    /// Compute a password checksum using HMAC-SHA256(password, salt).
    /// </summary>
    public static byte[] ComputeChecksum(byte[] passwordBytes, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(passwordBytes);
        ArgumentNullException.ThrowIfNull(salt);

        using var hmac = new HMACSHA256(salt);
        var hash = hmac.ComputeHash(passwordBytes);
        return hash.AsSpan(0, ChecksumSize).ToArray();
    }

    /// <summary>
    /// Validate a stored checksum against a password and salt.
    /// Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    public static bool ValidateChecksum(byte[] checksum, byte[] passwordBytes, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(checksum);
        ArgumentNullException.ThrowIfNull(passwordBytes);
        ArgumentNullException.ThrowIfNull(salt);

        if (checksum.Length != ChecksumSize)
            return false;

        var computed = ComputeChecksum(passwordBytes, salt);
        return CryptographicOperations.FixedTimeEquals(checksum, computed);
    }
}
