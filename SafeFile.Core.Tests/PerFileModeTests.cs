using System.Security.Cryptography;
using SafeFile.Core.Crypto;
using SafeFile.Core.Format;
using SafeFile.Core.IO;
using SafeFile.Core.Models;

namespace SafeFile.Core.Tests;

public sealed class PerFileModeTests
{
    private static readonly Argon2Parameters FastKdf = new(16_384, 1, 1);

    [Fact]
    public async Task PerFileOption_EncryptsAndDecryptsEveryFileIndependently()
    {
        using var temp = new TempDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
        var binaryContent = RandomNumberGenerator.GetBytes(1_500_000);

        await File.WriteAllTextAsync(Path.Combine(source, "readme.txt"), "SafeFile per-file mode");
        await File.WriteAllBytesAsync(Path.Combine(nested, "payload.bin"), binaryContent);
        await File.WriteAllBytesAsync(Path.Combine(nested, "already.safe"), []);

        var encryptedFolder = Path.Combine(temp.Path, "encrypted");
        var restoredFolder = Path.Combine(temp.Path, "restored");
        var password = "per-file-test-password"u8.ToArray();
        var fileProgress = new RecordingPerFileProgress();
        var encryptor = new FileEncryptor(
            consumerThreads: 4,
            perFileProgress: fileProgress);

        await encryptor.EncryptFolderPerFileAsync(
            source,
            encryptedFolder,
            password,
            chunkSizeBytes: 1_048_576,
            kdfParams: FastKdf);

        var expectedVaults = new[]
        {
            Path.Combine(encryptedFolder, "readme.txt.safe"),
            Path.Combine(encryptedFolder, "nested", "payload.bin.safe"),
            Path.Combine(encryptedFolder, "nested", "already.safe.safe")
        };

        foreach (var vaultPath in expectedVaults)
        {
            Assert.True(File.Exists(vaultPath), $"Missing per-file vault: {vaultPath}");
            using var stream = File.OpenRead(vaultPath);
            Assert.Equal(VaultMode.PerFile, VaultHeader.ReadFrom(stream).Mode);
        }
        Assert.Contains(
            fileProgress.Values,
            item => item.SourceFilePath.EndsWith("readme.txt") && item.Progress == 1);
        Assert.Contains(
            fileProgress.Values,
            item => item.SourceFilePath.EndsWith("payload.bin") && item.Progress == 1);

        await encryptor.DecryptFolderPerFileAsync(
            encryptedFolder,
            restoredFolder,
            password);

        Assert.Equal(
            "SafeFile per-file mode",
            await File.ReadAllTextAsync(Path.Combine(restoredFolder, "readme.txt")));
        Assert.Equal(
            binaryContent,
            await File.ReadAllBytesAsync(Path.Combine(restoredFolder, "nested", "payload.bin")));
        Assert.Empty(
            await File.ReadAllBytesAsync(Path.Combine(restoredFolder, "nested", "already.safe")));
    }

    [Fact]
    public async Task PerFileOption_EncryptedFileNames_AreHiddenAndRestored()
    {
        using var temp = new TempDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
        await File.WriteAllTextAsync(Path.Combine(nested, "private-name.txt"), "secret");

        var encryptedFolder = Path.Combine(temp.Path, "encrypted");
        var restoredFolder = Path.Combine(temp.Path, "restored");
        var password = "filename-test-password"u8.ToArray();
        var encryptor = new FileEncryptor(consumerThreads: 2);

        await encryptor.EncryptFolderPerFileAsync(
            source,
            encryptedFolder,
            password,
            chunkSizeBytes: 1_048_576,
            kdfParams: FastKdf,
            encryptFileNames: true);

        var vault = Assert.Single(Directory.GetFiles(
            Path.Combine(encryptedFolder, "nested"), "*.safe"));
        Assert.DoesNotContain("private-name", Path.GetFileName(vault), StringComparison.Ordinal);
        using (var stream = File.OpenRead(vault))
            Assert.True(VaultHeader.ReadFrom(stream).EncryptFileNames);

        var decryptorWithDefaultSettings = new FileEncryptor(consumerThreads: 2);
        await decryptorWithDefaultSettings.DecryptFolderPerFileAsync(
            encryptedFolder,
            restoredFolder,
            password);

        Assert.Equal(
            "secret",
            await File.ReadAllTextAsync(
                Path.Combine(restoredFolder, "nested", "private-name.txt")));
    }
}

internal sealed class RecordingPerFileProgress : IProgress<PerFileProgress>
{
    private readonly List<PerFileProgress> _values = new();

    public IReadOnlyList<PerFileProgress> Values => _values;

    public void Report(PerFileProgress value) => _values.Add(value);
}
