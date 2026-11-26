using System.Security.Cryptography;

namespace SafeFile.Cli.Tests;

/// <summary>
/// Runs the real CLI the way a GUI host does: password on the first stdin line,
/// cancellation through the rest of stdin.
/// </summary>
public sealed class CancelOnStdinTests : IDisposable
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

    [Fact]
    public async Task CancelLine_StopsEncryption_AndLeavesNoOutput()
    {
        var (source, output) = await CreateSourceAsync();

        var run = await CliRunner.RunAsync(["encrypt", source, "-o", output], stdin =>
        {
            stdin.WriteLine(Password);
            stdin.WriteLine("cancel");
            stdin.Flush();
        });

        Assert.Equal(130, run.ExitCode);
        var result = run.Result;
        Assert.Equal("cancelled", result.GetProperty("status").GetString());
        Assert.Equal("cancelled", result.GetProperty("errorCode").GetString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public async Task ClosingStdin_CancelsLikeTheCancelLine()
    {
        var (source, output) = await CreateSourceAsync();

        var run = await CliRunner.RunAsync(["encrypt", source, "-o", output], stdin =>
        {
            stdin.WriteLine(Password);
            stdin.Close();
        });

        Assert.Equal(130, run.ExitCode);
        Assert.Equal("cancelled", run.Result.GetProperty("status").GetString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public async Task OpenStdin_LetsTheOperationFinish()
    {
        var (source, output) = await CreateSourceAsync();

        // Keep stdin open until the CLI reports its result, as a host would.
        var run = await CliRunner.RunAsync(["encrypt", source, "-o", output], stdin =>
        {
            stdin.WriteLine(Password);
            stdin.WriteLine("not a command");
            stdin.Flush();
        });

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("succeeded", run.Result.GetProperty("status").GetString());
        Assert.Single(Directory.EnumerateFiles(output, "*.safe"));
    }

    [Fact]
    public async Task WithoutTheOption_ClosingStdinAfterThePasswordStillWorks()
    {
        var (source, output) = await CreateSourceAsync();

        var run = await CliRunner.RunAsync(["encrypt", source, "-o", output], stdin =>
        {
            stdin.WriteLine(Password);
            stdin.Close();
        }, cancelOnStdin: false);

        Assert.Equal(0, run.ExitCode);
        Assert.Single(Directory.EnumerateFiles(output, "*.safe"));
    }

    private async Task<(string Source, string Output)> CreateSourceAsync()
    {
        var source = Path.Combine(root, "data.bin");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(256 * 1024));
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(output);
        return (source, output);
    }
}
