using System.Security.Cryptography;

namespace SafeFile.Cli.Tests;

/// <summary>
/// <c>decrypt --delete-incomplete</c>: a vault that fails midway (here: corrupted
/// near the end) leaves its partial output only without the option.
/// </summary>
public sealed class DecryptDeleteIncompleteTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private readonly string root = Directory.CreateTempSubdirectory("safefile-cli-tests-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptedVault_KeepsPartialOutputUnlessAsked(bool deleteIncomplete)
    {
        var vault = await CreateCorruptedVaultAsync();
        var output = Path.Combine(root, "restored");
        Directory.CreateDirectory(output);
        string[] args = deleteIncomplete
            ? ["decrypt", vault, "-o", output, "--delete-incomplete"]
            : ["decrypt", vault, "-o", output];

        var run = await CliRunner.RunAsync(args, stdin =>
        {
            stdin.WriteLine(Password);
            stdin.Flush();
        });

        Assert.NotEqual(0, run.ExitCode);
        Assert.Equal("failed", run.Result.GetProperty("status").GetString());
        Assert.Equal(!deleteIncomplete, File.Exists(Path.Combine(output, "data.bin")));
    }

    private async Task<string> CreateCorruptedVaultAsync()
    {
        var source = Path.Combine(root, "data.bin");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(3_200_000));
        var vaults = Path.Combine(root, "vaults");
        Directory.CreateDirectory(vaults);

        var encrypt = await CliRunner.RunAsync(["encrypt", source, "-o", vaults, "--chunk-size-mb", "1"], stdin =>
        {
            stdin.WriteLine(Password);
            stdin.Flush();
        });
        Assert.Equal(0, encrypt.ExitCode);

        // Flip a byte near the end so the first chunks decrypt and a later one fails.
        var vault = Directory.EnumerateFiles(vaults, "*.safe").Single();
        var bytes = await File.ReadAllBytesAsync(vault);
        bytes[^100] ^= 0xFF;
        await File.WriteAllBytesAsync(vault, bytes);
        return vault;
    }
}
