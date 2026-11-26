using System.Diagnostics;
using System.Text.Json;

namespace SafeFile.Cli.Tests;

/// <summary>Runs the built <c>safefile</c> executable with the password on stdin.</summary>
internal static class CliRunner
{
    public static async Task<CliRun> RunAsync(
        string[] args,
        Action<StreamWriter> writeStdin,
        bool cancelOnStdin = true,
        bool passwordStdin = true)
    {
        var start = new ProcessStartInfo(CliPath())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        if (passwordStdin)
            start.ArgumentList.Add("--password-stdin");
        start.ArgumentList.Add("--json");
        start.ArgumentList.Add("--ignore-settings");
        if (cancelOnStdin)
            start.ArgumentList.Add("--cancel-on-stdin");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the CLI.");
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        writeStdin(process.StandardInput);

        var events = new List<JsonElement>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                events.Add(JsonDocument.Parse(line).RootElement.Clone());
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }

        var results = events.Where(e => e.GetProperty("event").GetString() == "result").ToList();
        Assert.True(results.Count == 1, $"Expected one result event. stderr: {await stderr}");
        Assert.Equal("result", events[^1].GetProperty("event").GetString());
        return new CliRun(process.ExitCode, results[0]);
    }

    private static string CliPath()
    {
        var name = OperatingSystem.IsWindows() ? "safefile.exe" : "safefile";
        var path = Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(path), $"CLI not found next to the tests: {path}");
        return path;
    }

}

internal sealed record CliRun(int ExitCode, JsonElement Result);
