using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SafeFile.Core.Services;
using SafeFile.Services;

namespace SafeFile.Core.Tests;

public sealed class FolderNameProtectionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SafeFileFolderNames", Guid.NewGuid().ToString("N"), "Photos");
    private readonly byte[] _password = Encoding.UTF8.GetBytes("correct-password");

    [Theory]
    [InlineData(FolderNameProtectionMode.Aes)]
    [InlineData(FolderNameProtectionMode.Sha256)]
    [InlineData(FolderNameProtectionMode.Md5)]
    public async Task EncryptDecrypt_RoundTrip_KeepsRootAndFiles(FolderNameProtectionMode mode)
    {
        Directory.CreateDirectory(Path.Combine(_root, "2025", "Trips"));
        var file = Path.Combine(_root, "2025", "Trips", "photo.txt");
        await File.WriteAllTextAsync(file, "unchanged");
        var service = CreateService();

        var created = await service.CreateSessionAsync(_root, mode);
        await service.EncryptAsync(created, _password);

        Assert.True(Directory.Exists(_root));
        Assert.True(File.Exists(Path.Combine(_root, FolderNameProtectionService.ManifestFileName)));
        var protectedSession = await service.VerifyManifestAsync(_root, _password);
        Assert.Equal(2, protectedSession.ProtectedCount);
        Assert.Equal(mode, protectedSession.Mode);

        await service.DecryptAsync(protectedSession, _password);

        Assert.Equal("unchanged", await File.ReadAllTextAsync(file));
        Assert.False(File.Exists(Path.Combine(_root, FolderNameProtectionService.ManifestFileName)));
    }

    [Fact]
    public async Task IncrementalEncrypt_FindsFolderInsideProtectedParent()
    {
        Directory.CreateDirectory(Path.Combine(_root, "2025"));
        var service = CreateService();
        await service.EncryptAsync(
            await service.CreateSessionAsync(_root, FolderNameProtectionMode.Aes), _password);
        var first = await service.VerifyManifestAsync(_root, _password);
        var protectedParent = Directory.EnumerateDirectories(_root).Single();
        Directory.CreateDirectory(Path.Combine(protectedParent, "NewAlbum"));

        var incremental = await service.VerifyManifestAsync(_root, _password);
        Assert.Equal(1, incremental.NewCount);
        await service.EncryptAsync(incremental, _password);
        await service.DecryptAsync(await service.VerifyManifestAsync(_root, _password), _password);

        Assert.True(Directory.Exists(Path.Combine(_root, "2025", "NewAlbum")));
    }

    [Fact]
    public async Task Encrypt_ExcludedBranch_RemainsUnchangedAndUnmapped()
    {
        var excluded = Directory.CreateDirectory(
            Path.Combine(_root, "Excluded", "Nested")).Parent!.FullName;
        Directory.CreateDirectory(Path.Combine(_root, "Included"));
        var service = CreateService();

        var session = await service.CreateSessionAsync(
            _root,
            FolderNameProtectionMode.Md5,
            excludedFolderPaths: [excluded]);
        Assert.Equal(1, session.TotalCount);
        await service.EncryptAsync(session, _password);

        Assert.True(Directory.Exists(excluded));
        Assert.True(Directory.Exists(Path.Combine(excluded, "Nested")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Included")));

        var filtered = await service.VerifyManifestAsync(
            _root, _password, excludedFolderPaths: [excluded]);
        Assert.Equal(0, filtered.ClearCount);
        await service.DecryptAsync(
            await service.VerifyManifestAsync(_root, _password), _password);
        Assert.True(Directory.Exists(Path.Combine(_root, "Included")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Excluded", "Nested")));
    }

    [Fact]
    public async Task IncrementalEncrypt_ExcludedProtectedBranch_PreservesManifestMappings()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Mapped", "Child"));
        var service = CreateService();
        await service.EncryptAsync(
            await service.CreateSessionAsync(_root, FolderNameProtectionMode.Md5), _password);
        var protectedRoot = Directory.EnumerateDirectories(_root).Single();

        var filtered = await service.VerifyManifestAsync(
            _root, _password, excludedFolderPaths: [protectedRoot]);
        await service.EncryptAsync(filtered, _password);

        var complete = await service.VerifyManifestAsync(_root, _password);
        Assert.Equal(2, complete.ProtectedCount);
        await service.DecryptAsync(complete, _password);
        Assert.True(Directory.Exists(Path.Combine(_root, "Mapped", "Child")));
    }

    [Fact]
    public async Task ManifestJson_PreservesUnicodeWithoutEscaping()
    {
        const string folderName = "Tài liệu tiếng Việt";
        Directory.CreateDirectory(Path.Combine(_root, folderName));
        var service = CreateService();
        await service.EncryptAsync(
            await service.CreateSessionAsync(
                _root,
                FolderNameProtectionMode.Md5),
            _password);

        var encrypted = await File.ReadAllTextAsync(
            Path.Combine(_root, FolderNameProtectionService.ManifestFileName));
        var textCrypto = new TextCryptoService();
        var jsonBytes = await textCrypto.DecryptBytesAsync(
            encrypted,
            _password,
            FolderNameProtectionService.MaximumManifestBytes);
        try
        {
            var json = Encoding.UTF8.GetString(jsonBytes);
            Assert.Contains(folderName, json, StringComparison.Ordinal);
            Assert.DoesNotContain("\\u", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Array.Clear(jsonBytes);
        }
    }

    private static FolderNameProtectionService CreateService() => new(
        new TextCryptoService(), NullLogger<FolderNameProtectionService>.Instance);

    public void Dispose()
    {
        Array.Clear(_password);
        var parent = Directory.GetParent(_root)?.Parent?.FullName;
        if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent, true);
    }
}
