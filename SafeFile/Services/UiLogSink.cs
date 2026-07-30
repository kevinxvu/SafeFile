using Serilog.Core;
using Serilog.Events;

namespace SafeFile.Services;

public sealed class UiLogSink : ILogEventSink
{
    private readonly LogService _logService;

    public UiLogSink(LogService logService)
    {
        _logService = logService;
    }

    public void Emit(LogEvent logEvent)
    {
        _logService.Publish(logEvent);
    }
}
