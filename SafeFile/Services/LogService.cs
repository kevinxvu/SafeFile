using System;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using SafeFile.Models;
using Serilog.Events;

namespace SafeFile.Services;

public sealed class LogService
{
    private const int MaximumInMemoryEntries = 5_000;
    private readonly ObservableCollection<LogEntry> _entries = [];

    public static LogService Instance { get; } = new();
    public ReadOnlyObservableCollection<LogEntry> Entries { get; }
    public string LogDirectory { get; }
    public string LogFilePattern => Path.Combine(
        LogDirectory, "safefile-.log");

    private LogService()
    {
        Entries = new ReadOnlyObservableCollection<LogEntry>(_entries);
        LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SafeFile",
            "Logs");
    }

    internal void Publish(LogEvent logEvent)
    {
        var entry = new LogEntry(
            logEvent.Timestamp,
            logEvent.Level,
            logEvent.RenderMessage(),
            logEvent.Exception?.ToString());

        if (Dispatcher.UIThread.CheckAccess())
            AddEntry(entry);
        else
            Dispatcher.UIThread.Post(() => AddEntry(entry));
    }

    public void Clear()
    {
        if (Dispatcher.UIThread.CheckAccess())
            _entries.Clear();
        else
            Dispatcher.UIThread.Post(_entries.Clear);
    }

    private void AddEntry(LogEntry entry)
    {
        _entries.Add(entry);
        while (_entries.Count > MaximumInMemoryEntries)
            _entries.RemoveAt(0);
    }
}
