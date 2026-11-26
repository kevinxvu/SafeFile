using System.Security.Cryptography;
using SafeFile.Core.Crypto;
using SafeFile.Core.IO;

namespace SafeFile.Core.Tests;

/// <summary>
/// What a failed or cancelled decryption leaves behind, with and without
/// <see cref="FileEncryptor.DeleteIncompleteDecryptionOutput"/>.
/// </summary>
public sealed class IncompleteDecryptionOutputTests
{
    private static readonly Argon2Parameters FastKdf = new(16_384, 1, 1);
    private static readonly byte[] Password = "incomplete-output-password"u8.ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledFileDecryption_KeepsOrDeletesThePartialFile(bool deleteIncomplete)
    {
        using var temp = new TempDirectory();
        var vault = await CreateFileVaultAsync(temp.Path);
        var destination = Path.Combine(temp.Path, "restored.bin");
        using var cts = new CancellationTokenSource();
        var decryptor = new FileEncryptor(
            consumerThreads: 2,
            new CancelAtProgress(cts, threshold: 0))
        {
            DeleteIncompleteDecryptionOutput = deleteIncomplete
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => decryptor.DecryptFileAsync(vault, destination, Password, cts.Token));

        if (deleteIncomplete)
        {
            Assert.False(File.Exists(destination));
        }
        else
        {
            Assert.True(File.Exists(destination));
            Assert.InRange(new FileInfo(destination).Length, 1, 3_200_000 - 1);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptedVault_KeepsOrDeletesThePartialFile(bool deleteIncomplete)
    {
        using var temp = new TempDirectory();
        var vault = await CreateFileVaultAsync(temp.Path);
        var destination = Path.Combine(temp.Path, "restored.bin");
        // Flip a byte near the end so the first chunks decrypt and a later one fails.
        var bytes = await File.ReadAllBytesAsync(vault);
        bytes[^100] ^= 0xFF;
        await File.WriteAllBytesAsync(vault, bytes);
        var decryptor = new FileEncryptor(consumerThreads: 2)
        {
            DeleteIncompleteDecryptionOutput = deleteIncomplete
        };

        await Assert.ThrowsAnyAsync<Exception>(
            () => decryptor.DecryptFileAsync(vault, destination, Password));

        Assert.Equal(!deleteIncomplete, File.Exists(destination));
    }

    [Fact]
    public async Task WrongPassword_NeverCreatesOutput_WithTheOptionOn()
    {
        using var temp = new TempDirectory();
        var vault = await CreateFileVaultAsync(temp.Path);
        var destination = Path.Combine(temp.Path, "restored.bin");
        var decryptor = new FileEncryptor(consumerThreads: 2) { DeleteIncompleteDecryptionOutput = true };

        await Assert.ThrowsAnyAsync<Exception>(
            () => decryptor.DecryptFileAsync(vault, destination, "not-the-password"u8.ToArray()));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task WrongPassword_DoesNotDeleteAnExistingFile()
    {
        using var temp = new TempDirectory();
        var vault = await CreateFileVaultAsync(temp.Path);
        var destination = Path.Combine(temp.Path, "restored.bin");
        await File.WriteAllTextAsync(destination, "keep me");
        var decryptor = new FileEncryptor(consumerThreads: 2) { DeleteIncompleteDecryptionOutput = true };

        await Assert.ThrowsAnyAsync<Exception>(() => decryptor.DecryptFileAsync(
            vault, destination, "not-the-password"u8.ToArray(), overwriteExisting: true));

        Assert.Equal("keep me", await File.ReadAllTextAsync(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledZipExtraction_KeepsOrDeletesThePartialFolder(bool deleteIncomplete)
    {
        using var temp = new TempDirectory();
        var sourceFolder = Path.Combine(temp.Path, "album");
        Directory.CreateDirectory(sourceFolder);
        for (var i = 0; i < 8; i++)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(sourceFolder, $"photo{i}.bin"),
                RandomNumberGenerator.GetBytes(400_000));
        }
        var vault = await new FileEncryptor(consumerThreads: 2).EncryptFolderZipAsync(
            sourceFolder, Path.Combine(temp.Path, "album.safe"), Password, 1_048_576, FastKdf);
        var destination = Path.Combine(temp.Path, "restored");

        using var cts = new CancellationTokenSource();
        // Progress above 0.6 means the vault is decrypted and extraction has started.
        var decryptor = new FileEncryptor(
            consumerThreads: 2,
            new CancelAtProgress(cts, threshold: 0.6))
        {
            DeleteIncompleteDecryptionOutput = deleteIncomplete
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => decryptor.DecryptFolderZipAsync(vault, destination, Password, cts.Token));

        Assert.Equal(!deleteIncomplete, Directory.Exists(destination));
    }

    private static async Task<string> CreateFileVaultAsync(string folder)
    {
        var source = Path.Combine(folder, "source.bin");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(3_200_000));
        return await new FileEncryptor(consumerThreads: 2).EncryptFileAsync(
            source, Path.Combine(folder, "source.safe"), Password, 1_048_576, FastKdf);
    }

    /// <summary>Cancels as soon as progress passes the threshold (reported synchronously).</summary>
    private sealed class CancelAtProgress(CancellationTokenSource cts, double threshold) : IProgress<double>
    {
        public void Report(double value)
        {
            if (value > threshold)
                cts.Cancel();
        }
    }
}
