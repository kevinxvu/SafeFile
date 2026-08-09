using System.Text;
using SafeFile.Core.Services;
using SafeFile.Services;

namespace SafeFile.Core.Tests;

public sealed class TextCryptoServiceTests
{
    [Fact]
    public async Task TextApi_StillRejectsMoreThanOneMillionCharacters()
    {
        var service = new TextCryptoService();
        var password = "text-limit-password"u8.ToArray();
        var oversized = new string(
            'x',
            TextCryptoService.MaximumTextCharacters + 1);

        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.EncryptAsync(oversized, password));
        }
        finally
        {
            Array.Clear(password);
        }
    }

    [Fact]
    public async Task ByteApi_UsesIndependentByteLimit()
    {
        var service = new TextCryptoService();
        var password = "manifest-limit-password"u8.ToArray();
        var content = Encoding.UTF8.GetBytes(new string(
            'x',
            TextCryptoService.MaximumTextCharacters + 1));
        try
        {
            var encrypted = await service.EncryptBytesAsync(
                content,
                password,
                FolderNameProtectionService.MaximumManifestBytes);
            var decrypted = await service.DecryptBytesAsync(
                encrypted,
                password,
                FolderNameProtectionService.MaximumManifestBytes);
            try
            {
                Assert.Equal(content, decrypted);
            }
            finally
            {
                Array.Clear(decrypted);
            }
        }
        finally
        {
            Array.Clear(content);
            Array.Clear(password);
        }
    }
}
