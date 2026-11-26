using System.Text;

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
    private static readonly Lazy<TextReader> Reader = new(() =>
        Console.IsInputRedirected ? CreateReader(Console.OpenStandardInput()) : Console.In);
    private static bool passwordLineRead;
    private static string? passwordLine;

    /// <summary>
    /// The one reader of standard input; it buffers, so every consumer must
    /// share it. Pipes and files are UTF-8: <see cref="Console.In"/> would
    /// decode them with the console code page and silently change a non-ASCII
    /// password. A terminal is still read through the console.
    /// </summary>
    public static TextReader Input => Reader.Value;

    /// <summary>
    /// A UTF-8 reader that fails on invalid bytes instead of replacing them,
    /// which would turn different passwords into the same one.
    /// </summary>
    internal static TextReader CreateReader(Stream stream) => new StreamReader(
        stream,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        detectEncodingFromByteOrderMarks: false);

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
                try
                {
                    // Some hosts, Windows PowerShell among them, start piped UTF-8 with a byte order mark.
                    passwordLine = input.ReadLine()?.TrimStart('\uFEFF');
                }
                catch (DecoderFallbackException)
                {
                    // The exception message quotes the offending bytes, which are part of the password.
                    throw new CliException(
                        "invalid_argument",
                        "Standard input is not valid UTF-8.",
                        ExitCodes.InvalidUsage);
                }
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
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or DecoderFallbackException)
        {
            // A broken pipe means the host is gone, and undecodable input leaves
            // no way to receive a later cancel; treat both like end of input.
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
