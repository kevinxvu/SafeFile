using System.CommandLine;
using SafeFile.Cli.Output;
using SafeFile.Core.Models;
using SafeFile.Core.Services;

namespace SafeFile.Cli.Infrastructure;

internal static class GlobalOptions
{
    public static readonly Option<bool> Json = new("--json")
    {
        Description = "Write machine-readable NDJSON events (one JSON object per line) to stdout.",
        Recursive = true
    };

    public static readonly Option<bool> NoProgress = new("--no-progress")
    {
        Description = "Do not show the progress line (text mode) or emit progress events (JSON mode).",
        Recursive = true
    };

    public static readonly Option<int> ProgressInterval = new("--progress-interval")
    {
        Description = "Minimum milliseconds between progress updates.",
        DefaultValueFactory = _ => 250,
        Recursive = true
    };

    public static readonly Option<bool> IgnoreSettings = new("--ignore-settings")
    {
        Description = "Use built-in defaults instead of the desktop app's saved settings.",
        Recursive = true
    };

    public static readonly Option<int?> Threads = new("--threads")
    {
        Description = "Worker threads for encryption/decryption (default: the app setting).",
        Recursive = true
    };

    public static void AddTo(RootCommand root)
    {
        root.Add(Json);
        root.Add(NoProgress);
        root.Add(ProgressInterval);
        root.Add(IgnoreSettings);
        root.Add(Threads);
    }
}

internal sealed class CliContext
{
    public required ParseResult Parse { get; init; }
    public required Reporter Reporter { get; init; }
    public required AppSettings Settings { get; init; }
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>Set by a command so a failure/cancellation result can include its counters.</summary>
    public ProgressTracker? Tracker { get; set; }
}

internal static class CommandRunner
{
    public static async Task<int> RunAsync(
        ParseResult parse,
        CancellationToken cancellationToken,
        string operation,
        Func<CliContext, Task<int>> body)
    {
        var intervalMs = Math.Max(50, parse.GetValue(GlobalOptions.ProgressInterval));
        var reporter = new Reporter(
            parse.GetValue(GlobalOptions.Json),
            !parse.GetValue(GlobalOptions.NoProgress),
            TimeSpan.FromMilliseconds(intervalMs));

        CliContext? context = null;
        try
        {
            context = new CliContext
            {
                Parse = parse,
                Reporter = reporter,
                Settings = LoadSettings(parse),
                CancellationToken = cancellationToken
            };
            return await body(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var error = ErrorMapper.Map(ex);
            ReportFailure(reporter, operation, error, context?.Tracker);
            return error.ExitCode;
        }
    }

    public static void ReportFailure(
        Reporter reporter,
        string operation,
        MappedError error,
        ProgressTracker? tracker)
    {
        reporter.EndProgress();
        var cancelled = error.ExitCode == ExitCodes.Cancelled;
        reporter.Emit("error", Fields.Of(
            ("operation", operation),
            ("code", error.Code),
            ("message", error.Message),
            ("path", null)));
        reporter.Emit("result", Fields.Of(
            ("operation", operation),
            ("status", cancelled ? "cancelled" : "failed"),
            ("exitCode", error.ExitCode),
            ("errorCode", error.Code),
            ("message", error.Message),
            ("succeeded", tracker?.Succeeded ?? 0),
            ("failed", tracker?.Failed ?? 0),
            ("elapsedSeconds", tracker is null ? null : Format.Round(tracker.ElapsedSeconds, 1))));
        reporter.Message(cancelled ? "Cancelled." : $"Error ({error.Code}): {error.Message}");
    }

    public static string StatusFor(int succeeded, int failed) =>
        failed == 0 ? "succeeded" : succeeded == 0 ? "failed" : "completed_with_errors";

    /// <summary>
    /// Exit code for a batch: success, partial success, or the first item's
    /// mapped failure when nothing succeeded.
    /// </summary>
    public static int ExitCodeFor(int succeeded, int failed, int? firstFailureExitCode) =>
        failed == 0
            ? ExitCodes.Success
            : succeeded > 0
                ? ExitCodes.CompletedWithErrors
                : firstFailureExitCode ?? ExitCodes.Failure;

    private static AppSettings LoadSettings(ParseResult parse)
    {
        var settings = parse.GetValue(GlobalOptions.IgnoreSettings)
            ? AppSettings.GetDefaults()
            : new SettingsService().Load();
        if (parse.GetValue(GlobalOptions.Threads) is { } threads)
        {
            if (threads < 1 || threads > 256)
                throw new CliException("invalid_argument", "--threads must be between 1 and 256.", ExitCodes.InvalidUsage);
            settings.MaxThreads = threads;
        }
        return settings;
    }
}

/// <summary>
/// Synchronous <see cref="IProgress{T}"/>. <see cref="Progress{T}"/> would post
/// callbacks to the thread pool in a console app, so they could run out of order.
/// </summary>
internal sealed class SyncProgress<T>(object gate, Action<T> handler) : IProgress<T>
{
    public void Report(T value)
    {
        lock (gate)
            handler(value);
    }
}
