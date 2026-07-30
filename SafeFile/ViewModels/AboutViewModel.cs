using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeFile.Services;
using Serilog;

namespace SafeFile.ViewModels;

public sealed partial class AboutViewModel : ViewModelBase
{
    private static readonly ILogger Logger = Log.ForContext<AboutViewModel>();
    private readonly IClipboardService _clipboard;
    private readonly IFilePickerService _filePicker;
    private readonly IErrorDialogService _errorDialog;
    private readonly LogService _logService;

    public string VersionText { get; } = GetVersion();
    public string OperatingSystem => RuntimeInformation.OSDescription.Trim();
    public string Runtime => RuntimeInformation.FrameworkDescription;
    public string Architecture =>
        $"{RuntimeInformation.ProcessArchitecture} ({(Environment.Is64BitProcess ? "64-bit" : "32-bit")})";
    public string AvaloniaVersion { get; } =
        typeof(Avalonia.Application).Assembly.GetName().Version?.ToString(3) ?? "—";
    public string LogDirectory => _logService.LogDirectory;

    [ObservableProperty] private string _statusMessage = "";

    public IAsyncRelayCommand CopySystemInfoCommand { get; }
    public IRelayCommand OpenLogFolderCommand { get; }

    public AboutViewModel(
        IClipboardService clipboard,
        IFilePickerService filePicker,
        IErrorDialogService errorDialog,
        LogService logService)
    {
        _clipboard = clipboard;
        _filePicker = filePicker;
        _errorDialog = errorDialog;
        _logService = logService;
        CopySystemInfoCommand = new AsyncRelayCommand(CopySystemInfoAsync);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
    }

    private async Task CopySystemInfoAsync()
    {
        var text = new StringBuilder()
            .AppendLine($"SafeFile {VersionText}")
            .AppendLine($"OS: {OperatingSystem}")
            .AppendLine($"Runtime: {Runtime}")
            .AppendLine($"Architecture: {Architecture}")
            .AppendLine($"Avalonia: {AvaloniaVersion}")
            .ToString();
        await _clipboard.SetTextAsync(text);
        StatusMessage = LocalizationService.Instance.Get("SystemInfoCopied");
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            _filePicker.OpenFolder(LogDirectory);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open log directory from About page");
            _ = _errorDialog.ShowErrorAsync(
                ex.Message,
                LocalizationService.Instance.Get("CannotOpenLogFolder"));
        }
    }

    private static string GetVersion()
    {
        var assembly = typeof(AboutViewModel).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var version = informational?.Split('+')[0]
            ?? assembly.GetName().Version?.ToString(3)
            ?? "1.0.0";
        return $"v{version}";
    }
}
