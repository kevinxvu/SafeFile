using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeFile.Core.Services;
using SafeFile.Services;
using Serilog;

namespace SafeFile.ViewModels;

public enum NavItem { Encrypt, Decrypt, Logs, Settings, About }

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private static readonly ILogger Logger =
        Log.ForContext<MainWindowViewModel>();
    private readonly SettingsService _settingsService;
    private readonly IErrorDialogService _errorDialog;
    private readonly EncryptViewModel _encryptPage;
    private readonly DecryptViewModel _decryptPage;
    private readonly LogViewModel _logsPage;
    private readonly SettingsViewModel _settingsPage;
    private readonly PlaceholderViewModel _aboutPage = new("Về ứng dụng");

    // ── Navigation ────────────────────────────────────────────────
    [ObservableProperty] private ViewModelBase _currentPage;
    [ObservableProperty] private string _activePage = "Mã hoá dữ liệu";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEncryptActive))]
    [NotifyPropertyChangedFor(nameof(IsDecryptActive))]
    [NotifyPropertyChangedFor(nameof(IsLogsActive))]
    [NotifyPropertyChangedFor(nameof(IsSettingsActive))]
    [NotifyPropertyChangedFor(nameof(IsAboutActive))]
    private NavItem _activeNav = NavItem.Encrypt;

    public bool IsEncryptActive  => ActiveNav == NavItem.Encrypt;
    public bool IsDecryptActive  => ActiveNav == NavItem.Decrypt;
    public bool IsLogsActive     => ActiveNav == NavItem.Logs;
    public bool IsSettingsActive => ActiveNav == NavItem.Settings;
    public bool IsAboutActive   => ActiveNav == NavItem.About;

    // ── Commands ──────────────────────────────────────────────────
    public IRelayCommand NavigateToEncryptCommand { get; }
    public IRelayCommand NavigateToDecryptCommand { get; }
    public IRelayCommand NavigateToLogsCommand { get; }
    public IRelayCommand NavigateToSettingsCommand { get; }
    public IRelayCommand NavigateToAboutCommand { get; }

    public MainWindowViewModel(
        IFilePickerService filePicker,
        SettingsService settingsService,
        IErrorDialogService errorDialog,
        LogService logService)
    {
        _settingsService = settingsService;
        _errorDialog = errorDialog;
        _encryptPage = new EncryptViewModel(
            filePicker, errorDialog, settingsService);
        _decryptPage = new DecryptViewModel(
            filePicker, errorDialog, settingsService);
        _settingsPage = new SettingsViewModel(
            settingsService, filePicker, errorDialog);
        _logsPage = new LogViewModel(logService, filePicker, errorDialog);
        _currentPage = _encryptPage;

        NavigateToEncryptCommand = new RelayCommand(() =>
        {
            ActiveNav = NavItem.Encrypt;
            ActivePage = "Mã hoá dữ liệu";
            _encryptPage.RefreshSettings();
            CurrentPage = _encryptPage;
            Logger.Debug("Navigated to Encrypt page");
        });
        NavigateToDecryptCommand = new RelayCommand(() =>
        {
            ActiveNav = NavItem.Decrypt;
            ActivePage = "Giải mã dữ liệu";
            _decryptPage.RefreshSettings();
            CurrentPage = _decryptPage;
            Logger.Debug("Navigated to Decrypt page");
        });
        NavigateToLogsCommand = new RelayCommand(() =>
        {
            ActiveNav = NavItem.Logs;
            ActivePage = "Nhật ký";
            CurrentPage = _logsPage;
            Logger.Debug("Navigated to Logs page");
        });
        NavigateToSettingsCommand = new RelayCommand(() =>
        {
            ActiveNav = NavItem.Settings;
            ActivePage = "Thiết lập";
            CurrentPage = _settingsPage;
            Logger.Debug("Navigated to Settings page");
        });
        NavigateToAboutCommand = new RelayCommand(() =>
        {
            ActiveNav = NavItem.About;
            ActivePage = "Về ứng dụng";
            CurrentPage = _aboutPage;
            Logger.Debug("Navigated to About page");
        });
    }
}
