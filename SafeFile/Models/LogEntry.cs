using System;
using Serilog.Events;

namespace SafeFile.Models;

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogEventLevel Level,
    string Message,
    string? Exception)
{
    public string TimestampText => Timestamp.ToLocalTime()
        .ToString("yyyy-MM-dd HH:mm:ss.fff");

    public string LevelText => Level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => Level.ToString().ToUpperInvariant()
    };

    public string LevelForeground => Level switch
    {
        LogEventLevel.Verbose => "#94A3B8",
        LogEventLevel.Debug => "#A78BFA",
        LogEventLevel.Information => "#38BDF8",
        LogEventLevel.Warning => "#FBBF24",
        LogEventLevel.Error or LogEventLevel.Fatal => "#F87171",
        _ => "#E5E7EB"
    };

    public string FullMessage => string.IsNullOrWhiteSpace(Exception)
        ? Message
        : $"{Message}{Environment.NewLine}{Exception}";
}
