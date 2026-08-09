using System;
using System.Threading;
using Serilog;

namespace SafeFile.Services;

public interface ITaskbarProgressService
{
    ITaskbarProgressOperation StartOperation();
}

public interface ITaskbarProgressOperation : IDisposable
{
    void Report(double progress);
}

/// <summary>
/// Prevents an optional taskbar integration from propagating failures into
/// the operation that it is observing.
/// </summary>
public sealed class TaskbarProgressTracker(
    ITaskbarProgressService service,
    ILogger logger)
{
    private ITaskbarProgressOperation? _operation;

    public void Begin()
    {
        End();
        try
        {
            _operation = service.StartOperation();
        }
        catch (Exception ex)
        {
            _operation = null;
            logger.Debug(ex, "Taskbar progress operation could not be started");
        }
    }

    public void Report(double progress)
    {
        var operation = Volatile.Read(ref _operation);
        if (operation is null)
            return;

        try
        {
            operation.Report(progress);
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Taskbar progress operation could not be updated");
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _operation,
                        null,
                        operation),
                    operation))
                DisposeSafely(operation);
        }
    }

    public void End()
    {
        var operation = Interlocked.Exchange(ref _operation, null);
        if (operation is not null)
            DisposeSafely(operation);
    }

    private void DisposeSafely(ITaskbarProgressOperation operation)
    {
        try
        {
            operation.Dispose();
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Taskbar progress operation could not be ended");
        }
    }
}

public sealed class NullTaskbarProgressService : ITaskbarProgressService
{
    public static NullTaskbarProgressService Instance { get; } = new();

    private NullTaskbarProgressService()
    {
    }

    public ITaskbarProgressOperation StartOperation() => NullOperation.Instance;

    private sealed class NullOperation : ITaskbarProgressOperation
    {
        public static NullOperation Instance { get; } = new();

        public void Report(double progress)
        {
        }

        public void Dispose()
        {
        }
    }
}
