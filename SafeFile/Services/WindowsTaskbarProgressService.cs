using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Serilog;

namespace SafeFile.Services;

/// <summary>
/// Mirrors long-running UI operation progress on the Windows taskbar button.
/// All failures are isolated from the file-processing workflows.
/// </summary>
public sealed class WindowsTaskbarProgressService : ITaskbarProgressService
{
    private static readonly ILogger Logger =
        Log.ForContext<WindowsTaskbarProgressService>();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, OperationState> _operations = [];
    private Window? _window;
    private ITaskbarList3? _taskbar;
    private IntPtr _windowHandle;
    private long _nextSequence;
    private bool _refreshQueued;

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindows())
            return;
        if (_window is not null)
            throw new InvalidOperationException("The taskbar progress service is already attached.");

        _window = window;
        window.Opened += OnWindowOpened;
        window.Closed += OnWindowClosed;
    }

    public ITaskbarProgressOperation StartOperation()
    {
        if (!OperatingSystem.IsWindows())
            return NullTaskbarProgressService.Instance.StartOperation();

        var id = Guid.NewGuid();
        lock (_gate)
            _operations[id] = new OperationState(++_nextSequence, 0, false);
        QueueRefresh();
        return new TaskbarProgressOperation(this, id);
    }

    private void Report(Guid id, double progress)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(id, out var operation))
                return;
            _operations[id] = operation with
            {
                Progress = Math.Clamp(progress, 0, 1),
                HasProgress = true
            };
        }

        QueueRefresh();
    }

    private void Complete(Guid id)
    {
        lock (_gate)
            _operations.Remove(id);
        QueueRefresh();
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            _windowHandle = _window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (_windowHandle == IntPtr.Zero)
                return;

            var taskbarType = Type.GetTypeFromCLSID(
                new Guid("56FDF344-FD6D-11D0-958A-006097C9A090"),
                throwOnError: true)!;
            _taskbar = (ITaskbarList3)Activator.CreateInstance(taskbarType)!;
            var result = _taskbar.HrInit();
            if (result < 0)
                Marshal.ThrowExceptionForHR(result);
            Logger.Debug(
                "Windows taskbar progress initialized for window {WindowHandle}",
                _windowHandle);
            QueueRefresh();
        }
        catch (Exception ex)
        {
            ReleaseTaskbar();
            _windowHandle = IntPtr.Zero;
            Logger.Warning(ex, "Windows taskbar progress could not be initialized");
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            if (_taskbar is not null && _windowHandle != IntPtr.Zero)
                _taskbar.SetProgressState(
                    _windowHandle,
                    TaskbarProgressState.NoProgress);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Windows taskbar progress could not be cleared");
        }
        finally
        {
            if (_window is not null)
            {
                _window.Opened -= OnWindowOpened;
                _window.Closed -= OnWindowClosed;
            }

            ReleaseTaskbar();
            _window = null;
            _windowHandle = IntPtr.Zero;
            lock (_gate)
                _operations.Clear();
        }
    }

    private void QueueRefresh()
    {
        lock (_gate)
        {
            if (_refreshQueued)
                return;
            _refreshQueued = true;
        }

        Dispatcher.UIThread.Post(() =>
        {
            lock (_gate)
                _refreshQueued = false;
            RefreshTaskbar();
        }, DispatcherPriority.Background);
    }

    private void RefreshTaskbar()
    {
        if (_taskbar is null || _windowHandle == IntPtr.Zero)
            return;

        try
        {
            OperationState? current;
            lock (_gate)
                current = _operations.Values.MaxBy(operation => operation.Sequence);

            if (current is null)
            {
                ThrowIfFailed(_taskbar.SetProgressState(
                    _windowHandle,
                    TaskbarProgressState.NoProgress));
                return;
            }

            if (!current.HasProgress)
            {
                ThrowIfFailed(_taskbar.SetProgressState(
                    _windowHandle,
                    TaskbarProgressState.Indeterminate));
                return;
            }

            const ulong total = 10_000;
            var completed = (ulong)Math.Round(current.Progress * total);
            ThrowIfFailed(_taskbar.SetProgressState(
                _windowHandle,
                TaskbarProgressState.Normal));
            ThrowIfFailed(
                _taskbar.SetProgressValue(_windowHandle, completed, total));
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Windows taskbar progress update failed");
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
    }

    private void ReleaseTaskbar()
    {
        var taskbar = _taskbar;
        _taskbar = null;
        if (taskbar is null || !OperatingSystem.IsWindows())
            return;

        try
        {
            if (Marshal.IsComObject(taskbar))
                Marshal.FinalReleaseComObject(taskbar);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Windows taskbar COM object could not be released");
        }
    }

    private sealed class TaskbarProgressOperation(
        WindowsTaskbarProgressService owner,
        Guid id) : ITaskbarProgressOperation
    {
        private WindowsTaskbarProgressService? _owner = owner;

        public void Report(double progress) => _owner?.Report(id, progress);

        public void Dispose()
        {
            var currentOwner = _owner;
            _owner = null;
            currentOwner?.Complete(id);
        }
    }

    private sealed record OperationState(
        long Sequence,
        double Progress,
        bool HasProgress);

    private enum TaskbarProgressState
    {
        NoProgress = 0,
        Indeterminate = 1,
        Normal = 2
    }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(IntPtr hwnd, TaskbarProgressState state);
    }
}
