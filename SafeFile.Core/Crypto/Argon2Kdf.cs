using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Konscious.Security.Cryptography;

namespace SafeFile.Core.Crypto;

public sealed class Argon2Kdf
{
    public const int SaltSize = 16;
    public const int KeySize = 32;
    public const int MinimumMemorySizeKb = 16_384;
    public const int MaximumMemorySizeKb = 262_144;
    public const int MaximumIterations = 20;
    public const int MaximumParallelism = 16;

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

        var passwordCopy = passwordBytes.ToArray();
        GCHandle passwordHandle = default;

        try
        {
            passwordHandle = GCHandle.Alloc(passwordCopy, GCHandleType.Pinned);

            var argon2 = new Argon2id(passwordCopy)
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

            CryptographicOperations.ZeroMemory(passwordCopy);
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
        if (MemorySizeKb < Argon2Kdf.MinimumMemorySizeKb ||
            MemorySizeKb > Argon2Kdf.MaximumMemorySizeKb)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MemorySizeKb),
                "Memory size must be between 16 MiB and 256 MiB.");
        }

        if (Iterations < 1 || Iterations > Argon2Kdf.MaximumIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Iterations),
                $"Iterations must be between 1 and {Argon2Kdf.MaximumIterations}.");
        }

        if (Parallelism < 1 || Parallelism > Argon2Kdf.MaximumParallelism)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Parallelism),
                $"Parallelism must be between 1 and {Argon2Kdf.MaximumParallelism}.");
        }
    }
}
