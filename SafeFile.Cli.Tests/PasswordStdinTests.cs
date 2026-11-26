using System.Text;

namespace SafeFile.Cli.Tests;

/// <summary>
/// <c>--password-stdin</c> reads UTF-8 whatever the console code page is, so a
/// password opens the same vault however it reached the CLI.
/// </summary>
public sealed class PasswordStdinTests : IDisposable
{
    private const string Password = "mật-khẩu-Đặng-€-密码-🔒";
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
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task NonAsciiPassword_MatchesThePasswordFile(bool byteOrderMark, bool cancelOnStdin)
    {
        var (source, output) = await CreateSourceAsync();
        byte[] line = [.. byteOrderMark ? Encoding.UTF8.GetPreamble() : [], .. Encoding.UTF8.GetBytes(Password + "\r\n")];

        var encrypt = await CliRunner.RunAsync(
            ["encrypt", source, "-o", output],
            stdin => Write(stdin, line),
            cancelOnStdin);
        Assert.Equal(0, encrypt.ExitCode);

        var passwordFile = Path.Combine(root, "password.txt");
        await File.WriteAllTextAsync(passwordFile, Password, new UTF8Encoding(false), TestContext.Current.CancellationToken);
        var vault = Assert.Single(Directory.EnumerateFiles(output, "*.safe"));
        var verify = await CliRunner.RunAsync(
            ["verify", vault, "--password-file", passwordFile],
            stdin => stdin.Close(),
            cancelOnStdin: false,
            passwordStdin: false);

        Assert.Equal(0, verify.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InputThatIsNotUtf8_IsRejected(bool cancelOnStdin)
    {
        var (source, output) = await CreateSourceAsync();
        // "password-é" in Windows-1252: replacing the bad byte would silently change the password.
        byte[] line = [.. "password-"u8, 0xE9, (byte)'\n'];

        var run = await CliRunner.RunAsync(
            ["encrypt", source, "-o", output],
            stdin => Write(stdin, line),
            cancelOnStdin);

        Assert.Equal(2, run.ExitCode);
        Assert.Equal("invalid_argument", run.Result.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("password-", run.Result.GetProperty("message").GetString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    private static void Write(StreamWriter stdin, byte[] bytes)
    {
        // Bypass the writer so the test controls the exact bytes.
        stdin.BaseStream.Write(bytes);
        stdin.BaseStream.Flush();
    }

    private async Task<(string Source, string Output)> CreateSourceAsync()
    {
        var source = Path.Combine(root, "data.txt");
        await File.WriteAllTextAsync(source, "content", TestContext.Current.CancellationToken);
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(output);
        return (source, output);
    }
}
