using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Konscious.Security.Cryptography;

namespace SafeFile.Core.Crypto;

public sealed class Argon2Kdf
{
    public const int SaltSize = 16;
    public const int KeySize = 32;

    public static readonly Argon2Parameters DefaultParameters = new(
        MemorySizeKb: 65_536,
        Iterations: 4,
        Parallelism: 2);

    public byte[] DeriveKey(byte[] passwordBytes, byte[] salt, Argon2Parameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(passwordBytes);
        ArgumentNullException.ThrowIfNull(salt);

        if (salt.Length != SaltSize)
        {
            throw new ArgumentException($"Salt must be {SaltSize} bytes.", nameof(salt));
        }

        var effectiveParameters = parameters ?? DefaultParameters;
        effectiveParameters.Validate();

        GCHandle passwordHandle = default;

        try
        {
            passwordHandle = GCHandle.Alloc(passwordBytes, GCHandleType.Pinned);

            var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                MemorySize = effectiveParameters.MemorySizeKb,
                Iterations = effectiveParameters.Iterations,
                DegreeOfParallelism = effectiveParameters.Parallelism
            };

            return argon2.GetBytes(KeySize);
        }
        finally
        {
            if (passwordHandle.IsAllocated)
            {
                passwordHandle.Free();
            }

            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public static byte[] GenerateSalt()
    {
        var salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }
}

public sealed record Argon2Parameters(int MemorySizeKb, int Iterations, int Parallelism)
{
    public void Validate()
    {
        if (MemorySizeKb <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MemorySizeKb), "Memory size must be greater than zero.");
        }

        if (Iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Iterations), "Iterations must be greater than zero.");
        }

        if (Parallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Parallelism), "Parallelism must be greater than zero.");
        }
    }
}
