namespace SafeFile.Core.Crypto;

public static class KdfDerivation
{
    public static async Task<byte[]> DeriveKeyAsync(
        byte[] passwordBytes,
        byte[] salt,
        Argon2Parameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passwordBytes);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(parameters);

        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(
            () => new Argon2Kdf().DeriveKey(passwordBytes, salt, parameters),
            cancellationToken).ConfigureAwait(false);
    }
}
