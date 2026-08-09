using SafeFile.Services;
using Serilog;

namespace SafeFile.Core.Tests;

public sealed class TaskbarProgressTrackerTests
{
    private static readonly ILogger Logger =
        new LoggerConfiguration().CreateLogger();

    [Fact]
    public void ProviderStartFailure_DoesNotEscape()
    {
        var tracker = new TaskbarProgressTracker(
            new ThrowingStartService(),
            Logger);

        tracker.Begin();
        tracker.Report(0.5);
        tracker.End();
    }

    [Fact]
    public void ProviderReportAndDisposeFailures_DoNotEscape()
    {
        var tracker = new TaskbarProgressTracker(
            new ThrowingOperationService(),
            Logger);

        tracker.Begin();
        tracker.Report(0.5);
        tracker.End();

        tracker.Begin();
        tracker.End();
    }

    private sealed class ThrowingStartService : ITaskbarProgressService
    {
        public ITaskbarProgressOperation StartOperation() =>
            throw new InvalidOperationException("Simulated start failure.");
    }

    private sealed class ThrowingOperationService : ITaskbarProgressService
    {
        public ITaskbarProgressOperation StartOperation() =>
            new ThrowingOperation();
    }

    private sealed class ThrowingOperation : ITaskbarProgressOperation
    {
        public void Report(double progress) =>
            throw new InvalidOperationException("Simulated report failure.");

        public void Dispose() =>
            throw new InvalidOperationException("Simulated dispose failure.");
    }
}
