using System.IO.Compression;
using System.Security.Cryptography;
using SafeFile.Core.Crypto;
using SafeFile.Core.Format;
using SafeFile.Core.IO;
using SafeFile.Core.Models;

namespace SafeFile.Core.Tests;

public sealed class FileEncryptorTests
{
    private static readonly Argon2Parameters FastKdf = new(16_384, 1, 1);

    [Fact]
    public async Task FileRoundTrip_PreservesContentAndCallerPassword()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        var vault = Path.Combine(temp.Path, "source.safe");
        var destination = Path.Combine(temp.Path, "restored.bin");
        var content = RandomNumberGenerator.GetBytes(128_123);
        await File.WriteAllBytesAsync(source, content);
        var password = "correct horse battery staple"u8.ToArray();
        var originalPassword = password.ToArray();

        var encryptor = new FileEncryptor(consumerThreads: 4);
        await encryptor.EncryptFileAsync(source, vault, password, 1_048_576, FastKdf);
        Assert.Equal(originalPassword, password);

        await encryptor.DecryptFileAsync(vault, destination, password);
        Assert.Equal(originalPassword, password);
        Assert.Equal(content, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task FileRoundTrip_EncryptedOutputName_IsHiddenAndRestoredFromHeader()
    {
        using var temp = new TempDirectory();
        var sourceFolder = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var restoredFolder = Directory.CreateDirectory(Path.Combine(temp.Path, "restored")).FullName;
        var source = Path.Combine(sourceFolder, "private-name.txt");
        var requestedVault = Path.Combine(temp.Path, "private-name.txt.safe");
        await File.WriteAllTextAsync(source, "hidden filename");
        await File.WriteAllTextAsync(requestedVault, "existing destination");
        var password = "password-for-test"u8.ToArray();
        var encryptor = new FileEncryptor(consumerThreads: 2);

        var actualVault = await encryptor.EncryptFileAsync(
            source,
            requestedVault,
            password,
            1_048_576,
            FastKdf,
            encryptFileName: true);

        Assert.Equal("existing destination", await File.ReadAllTextAsync(requestedVault));
        Assert.True(File.Exists(actualVault));
        Assert.DoesNotContain("private-name", Path.GetFileName(actualVault), StringComparison.Ordinal);
        Assert.Equal(
            "private-name.txt",
            await encryptor.DecryptOutputFileNameAsync(Path.GetFileName(actualVault), password));
        using (var stream = File.OpenRead(actualVault))
            Assert.True(VaultHeader.ReadFrom(stream).EncryptFileNames);

        var actualRestored = await encryptor.DecryptFileAsync(
            actualVault,
            Path.Combine(restoredFolder, "ignored-name"),
            password);

        Assert.Equal(Path.Combine(restoredFolder, "private-name.txt"), actualRestored);
        Assert.Equal("hidden filename", await File.ReadAllTextAsync(actualRestored));
    }

    [Fact]
    public async Task FileRoundTrip_LongEncryptedOutputName_IsTruncatedButFullyRestored()
    {
        using var temp = new TempDirectory();
        var sourceFolder = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var restoredFolder = Directory.CreateDirectory(Path.Combine(temp.Path, "restored")).FullName;
        var originalFileName = new string('a', 200) + ".txt";
        var source = Path.Combine(sourceFolder, originalFileName);
        await File.WriteAllTextAsync(source, "long filename");
        var password = "password-for-test"u8.ToArray();
        var encryptor = new FileEncryptor(consumerThreads: 2);

        var actualVault = await encryptor.EncryptFileAsync(
            source,
            Path.Combine(temp.Path, "requested.safe"),
            password,
            1_048_576,
            FastKdf,
            encryptFileName: true);

        Assert.Equal(255, Path.GetFileName(actualVault).Length);
        Assert.Equal(
            new string('a', 136) + ".txt",
            await encryptor.DecryptOutputFileNameAsync(Path.GetFileName(actualVault), password));

        var actualRestored = await encryptor.DecryptFileAsync(
            actualVault,
            Path.Combine(restoredFolder, "ignored"),
            password);

        Assert.Equal(originalFileName, Path.GetFileName(actualRestored));
        Assert.Equal("long filename", await File.ReadAllTextAsync(actualRestored));
    }

    [Fact]
    public async Task ZipRoundTrip_EncryptedVaultName_IsHiddenAndRecorded()
    {
        using var temp = new TempDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "private-folder")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "content.txt"), "zip content");
        var requestedVault = Path.Combine(temp.Path, "private-folder.safe");
        var restoredFolder = Path.Combine(temp.Path, "restored");
        var password = "password-for-test"u8.ToArray();
        var encryptor = new FileEncryptor(consumerThreads: 2);
        await File.WriteAllTextAsync(requestedVault, "existing destination");

        var actualVault = await encryptor.EncryptFolderZipAsync(
            source,
            requestedVault,
            password,
            1_048_576,
            FastKdf,
            encryptFileName: true);

        Assert.Equal("existing destination", await File.ReadAllTextAsync(requestedVault));
        Assert.True(File.Exists(actualVault));
        Assert.DoesNotContain("private-folder", Path.GetFileName(actualVault), StringComparison.Ordinal);
        Assert.Equal(
            "private-folder.zip",
            await encryptor.DecryptOutputFileNameAsync(Path.GetFileName(actualVault), password));
        using (var stream = File.OpenRead(actualVault))
            Assert.True(VaultHeader.ReadFrom(stream).EncryptFileNames);

        await encryptor.DecryptFolderZipAsync(actualVault, restoredFolder, password);
        Assert.Equal(
            "zip content",
            await File.ReadAllTextAsync(Path.Combine(restoredFolder, "content.txt")));
    }

    [Fact]
    public async Task EncryptFile_RejectsExtensionTooLongForStandaloneEncryptedName()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "file." + new string('x', 141));
        await File.WriteAllTextAsync(source, "content");
        var encryptor = new FileEncryptor(consumerThreads: 2);

        var exception = await Assert.ThrowsAsync<PathTooLongException>(() =>
            encryptor.EncryptFileAsync(
                source,
                Path.Combine(temp.Path, "requested.safe"),
                "password-for-test"u8.ToArray(),
                1_048_576,
                FastKdf,
                encryptFileName: true));

        Assert.Contains("shorter extension", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyFile_RoundTrips()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "empty.txt");
        var vault = Path.Combine(temp.Path, "empty.safe");
        var destination = Path.Combine(temp.Path, "restored.txt");
        await File.WriteAllBytesAsync(source, []);
        var password = "password-for-test"u8.ToArray();

        var encryptor = new FileEncryptor(consumerThreads: 3);
        await encryptor.EncryptFileAsync(source, vault, password, 1_048_576, FastKdf);
        await encryptor.DecryptFileAsync(vault, destination, password);

        Assert.Empty(await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task EncryptFile_PreCanceledToken_DoesNotCreateOutput()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source.txt");
        var destination = Path.Combine(temp.Path, "destination.safe");
        await File.WriteAllTextAsync(source, "content");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var encryptor = new FileEncryptor(consumerThreads: 2);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            encryptor.EncryptFileAsync(
                source,
                destination,
                "password-for-test"u8.ToArray(),
                1_048_576,
                FastKdf,
                cancellationToken: cancellation.Token));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task FileRoundTrip_UsesMinimumPasswordLengthFromSettings()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "settings-source.txt");
        var vault = Path.Combine(temp.Path, "settings.safe");
        var destination = Path.Combine(temp.Path, "settings-restored.txt");
        await File.WriteAllTextAsync(source, "settings-driven password policy");
        var settings = AppSettings.GetDefaults();
        settings.MinPasswordLength = 6;
        var encryptor = new FileEncryptor(consumerThreads: 2, settings: settings);

        await encryptor.EncryptFileAsync(
            source,
            vault,
            "123456"u8.ToArray(),
            1_048_576,
            FastKdf);
        await encryptor.DecryptFileAsync(vault, destination, "123456"u8.ToArray());

        Assert.Equal(
            "settings-driven password policy",
            await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task TruncatedVault_IsRejectedAndPartialOutputIsDeleted()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        var vault = Path.Combine(temp.Path, "source.safe");
        var destination = Path.Combine(temp.Path, "restored.bin");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(20_000));
        var password = "password-for-test"u8.ToArray();
        var encryptor = new FileEncryptor(consumerThreads: 4);
        await encryptor.EncryptFileAsync(source, vault, password, 1_048_576, FastKdf);

        await using (var stream = new FileStream(vault, FileMode.Open, FileAccess.Write))
            stream.SetLength(stream.Length - 1);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => encryptor.DecryptFileAsync(vault, destination, password));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task FolderRoundTrip_PreservesExistingDestinationOnConflict()
    {
        using var temp = new TempDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "hello.txt"), "hello");
        var largeContent = RandomNumberGenerator.GetBytes(6_000_000);
        await File.WriteAllBytesAsync(Path.Combine(source, "large.bin"), largeContent);
        var vault = Path.Combine(temp.Path, "folder.safe");
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        var marker = Path.Combine(destination, "keep.txt");
        await File.WriteAllTextAsync(marker, "keep");
        var password = "password-for-test"u8.ToArray();
        var progress = new RecordingProgress();
        var encryptor = new FileEncryptor(consumerThreads: 3, progress);
        await encryptor.EncryptFolderZipAsync(source, vault, password, 1_048_576, FastKdf);
        using (var vaultStream = File.OpenRead(vault))
            Assert.Equal(VaultMode.Zip, VaultHeader.ReadFrom(vaultStream).Mode);
        Assert.Equal(0, progress.Values.First());
        Assert.Equal(1, progress.Values.Last());
        Assert.Contains(progress.Values, value => value is > 0 and < 1);

        await Assert.ThrowsAsync<IOException>(
            () => encryptor.DecryptFolderZipAsync(vault, destination, password));
        Assert.Equal("keep", await File.ReadAllTextAsync(marker));

        Directory.Delete(destination, recursive: true);
        await encryptor.DecryptFolderZipAsync(vault, destination, password);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(destination, "nested", "hello.txt")));
        Assert.Equal(largeContent, await File.ReadAllBytesAsync(Path.Combine(destination, "large.bin")));
    }

    [Fact]
    public async Task ZipSlipEntry_IsRejected()
    {
        using var temp = new TempDirectory();
        await using var zipData = new MemoryStream();
        using (var archive = new ZipArchive(zipData, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("../escape.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("escaped");
        }
        zipData.Position = 0;

        var destination = Path.Combine(temp.Path, "extract");
        await Assert.ThrowsAsync<InvalidDataException>(
            () => StreamZipper.ExtractZipStreamAsync(zipData, destination));
        Assert.False(File.Exists(Path.Combine(temp.Path, "escape.txt")));
    }
}

internal sealed class RecordingProgress : IProgress<double>
{
    private readonly List<double> _values = new();

    public IReadOnlyList<double> Values => _values;

    public void Report(double value) => _values.Add(value);
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"SafeFileTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
