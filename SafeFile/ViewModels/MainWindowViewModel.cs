using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeFile.Core.Services;
using SafeFile.Services;
using Serilog;

namespace SafeFile.ViewModels;

public enum NavItem { Encrypt, Decrypt, Tools, Logs, Settings, About }

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private static readonly ILogger Logger =
        Log.ForContext<MainWindowViewModel>();
    private readonly SettingsService _settingsService;
    private readonly IErrorDialogService _errorDialog;
    private readonly EncryptViewModel _encryptPage;
    private readonly DecryptViewModel _decryptPage;
    private readonly ToolsViewModel _toolsPage;
    private readonly LogViewModel _logsPage;
    private readonly SettingsViewModel _settingsPage;
    private readonly AboutViewModel _aboutPage;

    // ── Navigation ────────────────────────────────────────────────
    [ObservableProperty] private ViewModelBase _currentPage;
    [ObservableProperty] private string _activePage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEncryptActive))]
    [NotifyPropertyChangedFor(nameof(IsDecryptActive))]
    [NotifyPropertyChangedFor(nameof(IsToolsActive))]
    [NotifyPropertyChangedFor(nameof(IsLogsActive))]
    [NotifyPropertyChangedFor(nameof(IsSettingsActive))]
    [NotifyPropertyChangedFor(nameof(IsAboutActive))]
    private NavItem _activeNav = NavItem.Encrypt;

    public bool IsEncryptActive  => ActiveNav == NavItem.Encrypt;
    public bool IsDecryptActive  => ActiveNav == NavItem.Decrypt;
    public bool IsToolsActive    => ActiveNav == NavItem.Tools;
    public bool IsLogsActive     => ActiveNav == NavItem.Logs;
    public bool IsSettingsActive => ActiveNav == NavItem.Settings;
    public bool IsAboutActive   => ActiveNav == NavItem.About;

    // ── Commands ──────────────────────────────────────────────────
    public IRelayCommand NavigateToEncryptCommand { get; }
    public IRelayCommand NavigateToDecryptCommand { get; }
    public IRelayCommand NavigateToToolsCommand { get; }
    public IRelayCommand NavigateToLogsCommand { get; }
    public IRelayCommand NavigateToSettingsCommand { get; }
    public IRelayCommand NavigateToAboutCommand { get; }

    public MainWindowViewModel(
        IFilePickerService filePicker,
        SettingsService settingsService,
        IErrorDialogService errorDialog,
        LogService logService,
        IClipboardService clipboard)
    {
        _settingsService = settingsService;
        _errorDialog = errorDialog;
        _encryptPage = new EncryptViewModel(
            filePicker, errorDialog, settingsService);
        _decryptPage = new DecryptViewModel(
            filePicker, errorDialog, settingsService);
        _toolsPage = new ToolsViewModel(
            filePicker, clipboard, errorDialog, settingsService);
        _settingsPage = new SettingsViewModel(
            settingsService, filePicker, errorDialog);
        _logsPage = new LogViewModel(logService, filePicker, errorDialog);
        _aboutPage = new AboutViewModel(
            clipboard, filePicker, errorDialog, logService);
        _currentPage = _encryptPage;
        RefreshLocalizedPageTitle();
        LocalizationService.Instance.CultureChanged += OnCultureChanged;

        NavigateToEncryptCommand = new RelayCommand(() =>
        {
            ActiveNav = NavItem.Encrypt;
            RefreshLocalizedPageTitle();
            _encryptPage.RefreshSettings();
            CurrentPage = _encryptPage;
            Logger.Debug("Navigated to Encrypt page");
        });
        NavigateToDecryptCommand = new RelayCommand(() =>
        {
            ActiveNav = NavItem.Decrypt;
            RefreshLocalizedPageTitle();
            _decryptPage.RefreshSettings();
            CurrentPage = _decryptPage;
            Logger.Debug("Navigated to Decrypt page");
        });
        NavigateToToolsCommand = new RelayCommand(() =>
        {
            ActiveNav = NavItem.Tools;
            RefreshLocalizedPageTitle();
            CurrentPage = _toolsPage;
            Logger.Debug("Navigated to Tools page");
        });
        NavigateToLogsCommand = new RelayCommand(() =>
        {
            ActiveNav = NavItem.Logs;
            RefreshLocalizedPageTitle();
            CurrentPage = _logsPage;
            Logger.Debug("Navigated to Logs page");
        });
        NavigateToSettingsCommand = new RelayCommand(() =>
        {
            ActiveNav = NavItem.Settings;
            RefreshLocalizedPageTitle();
            CurrentPage = _settingsPage;
            Logger.Debug("Navigated to Settings page");
        });
        NavigateToAboutCommand = new RelayCommand(() =>
        {
            ActiveNav = NavItem.About;
            RefreshLocalizedPageTitle();
            CurrentPage = _aboutPage;
            Logger.Debug("Navigated to About page");
        });
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedPageTitle();
    }

    private void RefreshLocalizedPageTitle()
    {
        var key = ActiveNav switch
        {
            NavItem.Encrypt => "PageEncrypt",
            NavItem.Decrypt => "PageDecrypt",
            NavItem.Tools => "PageTools",
            NavItem.Logs => "PageLogs",
            NavItem.Settings => "PageSettings",
            NavItem.About => "PageAbout",
            _ => "PageEncrypt"
        };
        ActivePage = LocalizationService.Instance.Get(key);
    }
}
