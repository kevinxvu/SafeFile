namespace SafeFile.Cli.Infrastructure;

/// <summary>
/// Standard input shared by <c>--password-stdin</c> (first line) and
/// <c>--cancel-on-stdin</c> (every later line). A GUI host cannot send Ctrl+C
/// to a child process that has no console, so it cancels by writing
/// <c>cancel</c> or closing stdin instead.
/// </summary>
internal static class StdinControl
{
    public const string CancelCommand = "cancel";

    private static readonly object Gate = new();
    private static bool passwordLineRead;
    private static string? passwordLine;

    /// <summary>
    /// Reads the password line once. Later calls return the same value, so the
    /// runner can read it before starting the cancel watcher and the command
    /// can still resolve it afterwards.
    /// </summary>
    public static string? ReadPasswordLine(TextReader input)
    {
        lock (Gate)
        {
            if (!passwordLineRead)
            {
                passwordLine = input.ReadLine();
                passwordLineRead = true;
            }
            return passwordLine;
        }
    }

    /// <summary>
    /// Watches <paramref name="input"/> on a background thread and cancels
    /// <paramref name="cts"/> on a <c>cancel</c> line or end of input. Other
    /// lines are ignored so the protocol can grow.
    /// </summary>
    public static void StartCancelWatcher(TextReader input, CancellationTokenSource cts)
    {
        var thread = new Thread(() => Watch(input, cts))
        {
            IsBackground = true,
            Name = "stdin cancel watcher"
        };
        thread.Start();
    }

    internal static void Watch(TextReader input, CancellationTokenSource cts)
    {
        try
        {
            while (input.ReadLine() is { } line)
            {
                if (string.Equals(line.Trim(), CancelCommand, StringComparison.OrdinalIgnoreCase))
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // A broken pipe means the host is gone; treat it like end of input.
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The command already finished.
        }
    }
}
