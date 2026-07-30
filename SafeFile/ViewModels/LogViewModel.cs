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
    public string[] LevelOptions { get; } =
        ["Tất cả", "Debug", "Thông tin", "Cảnh báo", "Lỗi"];

    [ObservableProperty] private string _selectedLevel = "Tất cả";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private string _statusMessage = "";

    public string EntryCountText =>
        $"{FilteredEntries.Count:N0} / {_logService.Entries.Count:N0} sự kiện";
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
            "Thông tin" => query.Where(entry =>
                entry.Level == LogEventLevel.Information),
            "Cảnh báo" => query.Where(entry =>
                entry.Level == LogEventLevel.Warning),
            "Lỗi" => query.Where(entry =>
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
        StatusMessage = "Đã xóa nhật ký khỏi màn hình.";
    }

    private async Task ExportAsync()
    {
        try
        {
            var path = await _filePicker.PickSaveFileAsync(
                "Xuất nhật ký",
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
            StatusMessage = $"Đã xuất {FilteredEntries.Count:N0} sự kiện.";
            Log.Information(
                "Exported {LogEntryCount} log events to {ExportPath}",
                FilteredEntries.Count,
                path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to export logs");
            await _errorDialog.ShowErrorAsync(
                ex.Message, "Không thể xuất nhật ký");
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
                ex.Message, "Không thể mở thư mục nhật ký");
        }
    }
}
