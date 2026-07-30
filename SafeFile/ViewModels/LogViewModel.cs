using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeFile.Models;
using SafeFile.Services;
using Serilog;
using Serilog.Events;

namespace SafeFile.ViewModels;

public sealed partial class LogViewModel : ViewModelBase
{
    private static readonly FilePickerFileType TextFileType = new("Text log")
    {
        Patterns = ["*.log", "*.txt"]
    };

    private readonly LogService _logService;
    private readonly IFilePickerService _filePicker;
    private readonly IErrorDialogService _errorDialog;

    public ObservableCollection<LogEntry> FilteredEntries { get; } = [];
    public ObservableCollection<string> LevelOptions { get; } = [];

    [ObservableProperty] private string _selectedLevel = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private string _statusMessage = "";

    public string EntryCountText =>
        F("EventsCount", FilteredEntries.Count, _logService.Entries.Count);
    public bool HasEntries => FilteredEntries.Count > 0;
    public string LogDirectory => _logService.LogDirectory;

    public IRelayCommand ClearCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IRelayCommand OpenLogFolderCommand { get; }

    public LogViewModel(
        LogService logService,
        IFilePickerService filePicker,
        IErrorDialogService errorDialog)
    {
        _logService = logService;
        _filePicker = filePicker;
        _errorDialog = errorDialog;
        RefreshLevelOptions();
        LocalizationService.Instance.CultureChanged += (_, _) =>
            RefreshLevelOptions();

        ClearCommand = new RelayCommand(Clear);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);

        ((INotifyCollectionChanged)_logService.Entries).CollectionChanged +=
            OnLogEntriesChanged;
        RefreshFilter();
    }

    partial void OnSelectedLevelChanged(string value) => RefreshFilter();
    partial void OnSearchTextChanged(string value) => RefreshFilter();

    private void OnLogEntriesChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        RefreshFilter();
    }

    private void RefreshFilter()
    {
        var query = _logService.Entries.AsEnumerable();
        query = SelectedLevel switch
        {
            "Debug" => query.Where(entry =>
                entry.Level is LogEventLevel.Verbose or LogEventLevel.Debug),
            var level when level == L("Information") => query.Where(entry =>
                entry.Level == LogEventLevel.Information),
            var level when level == L("Warning") => query.Where(entry =>
                entry.Level == LogEventLevel.Warning),
            var level when level == L("Error") => query.Where(entry =>
                entry.Level is LogEventLevel.Error or LogEventLevel.Fatal),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(entry =>
                entry.FullMessage.Contains(
                    SearchText.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        }

        FilteredEntries.Clear();
        foreach (var entry in query)
            FilteredEntries.Add(entry);

        OnPropertyChanged(nameof(EntryCountText));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(LogDirectory));
    }

    private void Clear()
    {
        _logService.Clear();
        StatusMessage = L("LogsCleared");
    }

    private async Task ExportAsync()
    {
        try
        {
            var path = await _filePicker.PickSaveFileAsync(
                L("ExportLogs"),
                $"SafeFile-log-{DateTime.Now:yyyyMMdd-HHmmss}.log",
                [TextFileType]);
            if (path is null)
                return;

            var content = new StringBuilder();
            foreach (var entry in FilteredEntries)
            {
                content.Append('[').Append(entry.TimestampText).Append("] [")
                    .Append(entry.LevelText).Append("] ")
                    .AppendLine(entry.FullMessage);
            }

            await File.WriteAllTextAsync(
                path,
                content.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusMessage = F("LogsExported", FilteredEntries.Count);
            Log.Information(
                "Exported {LogEntryCount} log events to {ExportPath}",
                FilteredEntries.Count,
                path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to export logs");
            await _errorDialog.ShowErrorAsync(
                ex.Message, L("CannotExportLogs"));
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_logService.LogDirectory);
            _filePicker.OpenFolder(_logService.LogDirectory);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open log directory");
            _ = _errorDialog.ShowErrorAsync(
                ex.Message, L("CannotOpenLogFolder"));
        }
    }

    private void RefreshLevelOptions()
    {
        var selectedIndex = Math.Max(0, LevelOptions.IndexOf(SelectedLevel));
        LevelOptions.Clear();
        foreach (var option in new[]
                 { L("All"), "Debug", L("Information"), L("Warning"), L("Error") })
            LevelOptions.Add(option);
        SelectedLevel = LevelOptions[Math.Min(selectedIndex, LevelOptions.Count - 1)];
        OnPropertyChanged(nameof(EntryCountText));
        RefreshFilter();
    }

    private static string L(string key) => LocalizationService.Instance.Get(key);
    private static string F(string key, params object?[] args) =>
        LocalizationService.Instance.Format(key, args);
}
