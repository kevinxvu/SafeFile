using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SafeFile.Cli.Output;

/// <summary>
/// Writes everything the CLI shows. In JSON mode stdout carries only NDJSON
/// events (one JSON object per line) and nothing else; in text mode progress
/// and messages go to stderr and the final summary goes to stdout.
/// </summary>
internal sealed class Reporter
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null
    };

    private readonly object _gate = new();
    private readonly TextWriter _jsonOut;
    private readonly bool _liveProgressLine;
    private int _progressLineLength;

    public Reporter(bool json, bool showProgress, TimeSpan progressInterval)
    {
        IsJson = json;
        ShowProgress = showProgress;
        ProgressInterval = progressInterval;
        _liveProgressLine = !json && showProgress && !Console.IsErrorRedirected;
        _jsonOut = json
            ? new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true }
            : TextWriter.Null;
    }

    public bool IsJson { get; }
    public bool ShowProgress { get; }
    public TimeSpan ProgressInterval { get; }

    /// <summary>Text-mode progress lines are throttled harder when stderr is a log file.</summary>
    public TimeSpan TextProgressInterval => _liveProgressLine
        ? ProgressInterval
        : TimeSpan.FromMilliseconds(Math.Max(2000, ProgressInterval.TotalMilliseconds));

    public void Emit(string eventName, IEnumerable<KeyValuePair<string, object?>> fields)
    {
        if (!IsJson)
            return;
        var payload = new Dictionary<string, object?>
        {
            ["event"] = eventName,
            ["time"] = DateTimeOffset.UtcNow.ToString("O")
        };
        foreach (var (key, value) in fields)
            payload[key] = value;
        var line = JsonSerializer.Serialize(payload, JsonOptions);
        lock (_gate)
            _jsonOut.WriteLine(line);
    }

    /// <summary>Informational text-mode line on stderr.</summary>
    public void Message(string text)
    {
        if (IsJson)
            return;
        lock (_gate)
        {
            ClearProgressLine();
            Console.Error.WriteLine(text);
        }
    }

    /// <summary>Text-mode result line on stdout.</summary>
    public void Result(string text)
    {
        if (IsJson)
            return;
        lock (_gate)
        {
            ClearProgressLine();
            Console.Out.WriteLine(text);
        }
    }

    public void ProgressLine(string text)
    {
        if (IsJson || !ShowProgress)
            return;
        lock (_gate)
        {
            if (!_liveProgressLine)
            {
                Console.Error.WriteLine(text);
                return;
            }

            var width = GetConsoleWidth() - 1;
            if (text.Length > width)
                text = text[..Math.Max(0, width)];
            var padding = Math.Max(0, _progressLineLength - text.Length);
            Console.Error.Write("\r" + text + new string(' ', padding));
            _progressLineLength = text.Length;
        }
    }

    public void EndProgress()
    {
        lock (_gate)
            ClearProgressLine();
    }

    private void ClearProgressLine()
    {
        if (_progressLineLength == 0)
            return;
        Console.Error.Write("\r" + new string(' ', _progressLineLength) + "\r");
        _progressLineLength = 0;
    }

    private static int GetConsoleWidth()
    {
        try
        {
            var width = Console.WindowWidth;
            return width > 20 ? width : 120;
        }
        catch
        {
            return 120;
        }
    }
}

internal static class Fields
{
    public static List<KeyValuePair<string, object?>> Of(params (string Key, object? Value)[] values) =>
        values.Select(v => new KeyValuePair<string, object?>(v.Key, v.Value)).ToList();
}
