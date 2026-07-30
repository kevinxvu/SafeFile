using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeFile.Core.Services;
using SafeFile.Services;

namespace SafeFile.ViewModels;

public enum NavItem { Encrypt, Decrypt, Logs, Settings }

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly IErrorDialogService _errorDialog;

    // ── Navigation ────────────────────────────────────────────────
    [ObservableProperty] private ViewModelBase _currentPage;
    [ObservableProperty] private string _activePage = "Mã hoá dữ liệu";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEncryptActive))]
    [NotifyPropertyChangedFor(nameof(IsDecryptActive))]
    [NotifyPropertyChangedFor(nameof(IsLogsActive))]
    [NotifyPropertyChangedFor(nameof(IsSettingsActive))]
    private NavItem _activeNav = NavItem.Encrypt;

    public bool IsEncryptActive  => ActiveNav == NavItem.Encrypt;
    public bool IsDecryptActive  => ActiveNav == NavItem.Decrypt;
    public bool IsLogsActive     => ActiveNav == NavItem.Logs;
    public bool IsSettingsActive => ActiveNav == NavItem.Settings;

    // ── Commands ──────────────────────────────────────────────────
    public IRelayCommand NavigateToEncryptCommand { get; }
    public IRelayCommand NavigateToDecryptCommand { get; }
    public IRelayCommand NavigateToLogsCommand { get; }
    public IRelayCommand NavigateToSettingsCommand { get; }
    public IRelayCommand NavigateToAboutCommand { get; }

    public MainWindowViewModel(
        IFilePickerService filePicker,
        SettingsService settingsService,
        IErrorDialogService errorDialog)
    {
        _settingsService = settingsService;
        _errorDialog = errorDialog;
        _currentPage = new EncryptViewModel(filePicker, errorDialog);

        NavigateToEncryptCommand = new RelayCommand(() =>
        {
            ActiveNav = NavItem.Encrypt;
            ActivePage = "Mã hoá dữ liệu";
            CurrentPage = new EncryptViewModel(filePicker, _errorDialog);
        });
        NavigateToDecryptCommand = new RelayCommand(() =>
        {
            ActiveNav = NavItem.Decrypt;
            ActivePage = "Giải mã dữ liệu";
            CurrentPage = new DecryptViewModel(filePicker, _errorDialog);
        });
        NavigateToLogsCommand = new RelayCommand(() =>
        {
            ActiveNav = NavItem.Logs;
            ActivePage = "Nhật ký";
            CurrentPage = new PlaceholderViewModel("Nhật ký");
        });
        NavigateToSettingsCommand = new RelayCommand(() =>
        {
            ActiveNav = NavItem.Settings;
            ActivePage = "Thiết lập";
            CurrentPage = new SettingsViewModel(
                _settingsService, filePicker, _errorDialog);
        });
        NavigateToAboutCommand = new RelayCommand(() =>
        {
            ActivePage = "Về ứng dụng";
            CurrentPage = new PlaceholderViewModel("Về ứng dụng");
        });
    }
}
