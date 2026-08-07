using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeFile.Core.Models;
using SafeFile.Core.Services;
using SafeFile.Services;
using Serilog;

namespace SafeFile.ViewModels;

public sealed partial class FolderNamesViewModel : ViewModelBase
{
    private static readonly Serilog.ILogger Logger = Log.ForContext<FolderNamesViewModel>();
    private readonly IFilePickerService _picker;
    private readonly IErrorDialogService _errors;
    private readonly SettingsService _settingsService;
    private readonly FolderNameProtectionService _service;
    private AppSettings _settings;
    private FolderNameSession? _session;
    private CancellationTokenSource? _operationCts;

    [ObservableProperty] private string _rootPath = "";
    [ObservableProperty] private bool _hasFolder;
    [ObservableProperty] private bool _hasManifest;
    [ObservableProperty] private bool _isManifestVerified;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private bool _showPassword;
    [ObservableProperty] private bool _showConfirmPassword;
    [ObservableProperty] private FolderNameProtectionMode _selectedMode = FolderNameProtectionMode.Aes;
    [ObservableProperty] private int _totalFolders;
    [ObservableProperty] private int _protectedFolders;
    [ObservableProperty] private int _clearFolders;
    [ObservableProperty] private int _newFolders;
    [ObservableProperty] private int _conflictingFolders;
    [ObservableProperty] private string _manifestStatus = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isStatusBarVisible;
    [ObservableProperty] private string _statusAction = "";
    [ObservableProperty] private string _statusCurrentFolder = "";
    [ObservableProperty] private string _statusCurrentPath = "";
    [ObservableProperty] private string _statusDetails = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusProgressPercent))]
    private double _statusProgress;

    public int StatusProgressPercent => StatusProgress >= 1
        ? 100
        : (int)Math.Floor(Math.Clamp(StatusProgress, 0, 1) * 100);

    public string RootName => string.IsNullOrWhiteSpace(RootPath) ? "" : Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    public char PasswordChar => ShowPassword ? '\0' : '•';
    public char ConfirmPasswordChar => ShowConfirmPassword ? '\0' : '•';
    public bool IsPasswordConfirmationRequired => _settings.ConfirmPasswordToggle && !HasManifest;
    public bool IsModeLocked => HasManifest;
    public bool IsAesMode { get => SelectedMode == FolderNameProtectionMode.Aes; set { if (value && !IsModeLocked) SelectedMode = FolderNameProtectionMode.Aes; } }
    public bool IsShaMode { get => SelectedMode == FolderNameProtectionMode.Sha256; set { if (value && !IsModeLocked) SelectedMode = FolderNameProtectionMode.Sha256; } }
    public bool CanEncrypt => HasFolder && !IsBusy && ConflictingFolders == 0 && (!HasManifest || IsManifestVerified);
    public bool CanDecrypt => HasFolder && HasManifest && IsManifestVerified && !IsBusy && ConflictingFolders == 0;
    public bool CanCheckManifest => HasManifest && HasFolder && !IsBusy && !string.IsNullOrEmpty(Password);

    public IAsyncRelayCommand BrowseCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand CheckManifestCommand { get; }
    public IAsyncRelayCommand EncryptCommand { get; }
    public IAsyncRelayCommand DecryptCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public IRelayCommand OpenFolderCommand { get; }
    public IRelayCommand TogglePasswordVisibilityCommand { get; }
    public IRelayCommand ToggleConfirmPasswordVisibilityCommand { get; }
    public IRelayCommand CloseStatusCommand { get; }

    public FolderNamesViewModel(IFilePickerService picker, IErrorDialogService errors,
        SettingsService settingsService, FolderNameProtectionService service)
    {
        _picker = picker; _errors = errors; _settingsService = settingsService; _service = service;
        _settings = settingsService.Load();
        BrowseCommand = new AsyncRelayCommand(BrowseAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CheckManifestCommand = new AsyncRelayCommand(CheckManifestAsync);
        EncryptCommand = new AsyncRelayCommand(() => RunAsync(true));
        DecryptCommand = new AsyncRelayCommand(() => RunAsync(false));
        ResetCommand = new RelayCommand(Reset);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        TogglePasswordVisibilityCommand = new RelayCommand(() => ShowPassword = !ShowPassword);
        ToggleConfirmPasswordVisibilityCommand = new RelayCommand(() => ShowConfirmPassword = !ShowConfirmPassword);
        CloseStatusCommand = new RelayCommand(() => { if (IsBusy) _operationCts?.Cancel(); else IsStatusBarVisible = false; });
        ManifestStatus = L("FolderManifestNone");
    }

    partial void OnRootPathChanged(string value) => NotifyState();
    partial void OnShowPasswordChanged(bool value) => OnPropertyChanged(nameof(PasswordChar));
    partial void OnShowConfirmPasswordChanged(bool value) => OnPropertyChanged(nameof(ConfirmPasswordChar));
    partial void OnSelectedModeChanged(FolderNameProtectionMode value) { OnPropertyChanged(nameof(IsAesMode)); OnPropertyChanged(nameof(IsShaMode)); }
    partial void OnPasswordChanged(string value)
    {
        if (HasManifest && IsManifestVerified) { IsManifestVerified = false; _session = null; ManifestStatus = L("FolderManifestDetected"); }
        NotifyState();
    }
    partial void OnHasManifestChanged(bool value) { OnPropertyChanged(nameof(IsModeLocked)); OnPropertyChanged(nameof(IsPasswordConfirmationRequired)); NotifyState(); }
    partial void OnIsManifestVerifiedChanged(bool value) => NotifyState();
    partial void OnIsBusyChanged(bool value) => NotifyState();
    partial void OnConflictingFoldersChanged(int value) => NotifyState();

    public void RefreshSettings() { if (!IsBusy) { _settings = _settingsService.Load(); OnPropertyChanged(nameof(IsPasswordConfirmationRequired)); } }

    private async Task BrowseAsync()
    {
        var path = await _picker.PickFolderAsync(L("SelectFolderNameRoot"));
        if (path is not null) await SelectFolderAsync(path);
    }

    public async Task SelectFolderAsync(string path)
    {
        if (!Directory.Exists(path) || IsBusy) return;
        RootPath = Path.GetFullPath(path);
        Password = ConfirmPassword = "";
        _session = null;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (!Directory.Exists(RootPath)) return;
        try
        {
            var scan = await _service.ScanAsync(RootPath);
            HasFolder = true; HasManifest = scan.HasManifest; TotalFolders = scan.PhysicalFolderCount;
            ProtectedFolders = ClearFolders = NewFolders = ConflictingFolders = 0;
            IsManifestVerified = false; _session = null;
            ManifestStatus = HasManifest ? L("FolderManifestDetected") : L("FolderManifestNone");
        }
        catch (Exception ex) { await _errors.ShowErrorAsync(ex.Message, L("FolderNamesCannotProcess")); }
        NotifyState();
    }

    private async Task CheckManifestAsync()
    {
        if (!CanCheckManifest) return;
        byte[]? bytes = null;
        try
        {
            bytes = Encoding.UTF8.GetBytes(Password);
            _session = await _service.VerifyManifestAsync(RootPath, bytes);
            IsManifestVerified = true; SelectedMode = _session.Mode;
            ApplySession(_session); ManifestStatus = L("FolderManifestVerified");
        }
        catch (Exception ex) { IsManifestVerified = false; _session = null; await _errors.ShowErrorAsync(ex.Message, L("FolderManifestInvalid")); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task RunAsync(bool encrypt)
    {
        if (string.IsNullOrEmpty(Password)) { await _errors.ShowErrorAsync(L("PasswordRequired")); return; }
        if (encrypt && !HasManifest && Password.Length < _settings.MinPasswordLength) { await _errors.ShowErrorAsync(F("PasswordTooShort", _settings.MinPasswordLength)); return; }
        if (encrypt && IsPasswordConfirmationRequired && Password != ConfirmPassword) { await _errors.ShowErrorAsync(L("PasswordsDoNotMatch")); return; }
        if (HasManifest && !IsManifestVerified) { await _errors.ShowErrorAsync(L("FolderNamesManifestRequired")); return; }
        byte[]? bytes = null;
        try
        {
            IsBusy = true; IsStatusBarVisible = true; StatusProgress = 0;
            StatusAction = L(encrypt ? "FolderNamesEncrypting" : "FolderNamesDecrypting");
            _operationCts = new CancellationTokenSource();
            bytes = Encoding.UTF8.GetBytes(Password);
            var session = _session ?? await _service.CreateSessionAsync(RootPath, SelectedMode, _operationCts.Token);
            ApplySession(session);
            Logger.Information("Folder-name {Operation} started using {Mode} for {FolderCount} folders",
                encrypt ? "encryption" : "decryption", session.Mode,
                encrypt ? session.ClearCount : session.ProtectedCount);
            var progress = new Progress<FolderNameProgress>(p => { StatusCurrentPath = p.CurrentPath; StatusCurrentFolder = Path.GetFileName(p.CurrentPath); StatusDetails = $"{p.Completed:N0} / {p.Total:N0}"; StatusProgress = p.Total == 0 ? 1 : (double)p.Completed / p.Total; });
            if (encrypt) await _service.EncryptAsync(session, bytes, progress, _operationCts.Token);
            else await _service.DecryptAsync(session, bytes, progress, _operationCts.Token);
            StatusMessage = L(encrypt ? "FolderNamesEncrypted" : "FolderNamesDecrypted");
        }
        catch (OperationCanceledException) { Logger.Information("Folder-name operation cancelled; state remains resumable"); StatusAction = L("FolderNamesCancelled"); StatusMessage = L("FolderNamesCancelled"); }
        catch (Exception ex) { Logger.Error(ex, "Folder-name operation failed"); await _errors.ShowErrorAsync(ex.Message, L("FolderNamesCannotProcess")); }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            _operationCts?.Dispose(); _operationCts = null; IsBusy = false;
            await RefreshAfterOperationAsync();
        }
    }

    private async Task RefreshAfterOperationAsync()
    {
        var password = Password;
        await RefreshAsync();
        if (HasManifest && !string.IsNullOrEmpty(password)) { Password = password; await CheckManifestAsync(); }
    }

    private void ApplySession(FolderNameSession s)
    { TotalFolders = s.TotalCount; ProtectedFolders = s.ProtectedCount; ClearFolders = s.ClearCount; NewFolders = s.NewCount; ConflictingFolders = s.ConflictCount; }

    private void Reset() { if (IsBusy) return; RootPath = Password = ConfirmPassword = ""; HasFolder = HasManifest = IsManifestVerified = false; _session = null; TotalFolders = ProtectedFolders = ClearFolders = NewFolders = ConflictingFolders = 0; ManifestStatus = L("FolderManifestNone"); StatusMessage = ""; }
    private void OpenFolder()
    {
        if (!Directory.Exists(RootPath)) return;
        try { _picker.OpenFolder(RootPath); Logger.Debug("Opened selected folder"); }
        catch (Exception ex) { Logger.Error(ex, "Failed to open selected folder"); StatusMessage = $"{L("CannotOpenOutput")}: {ex.Message}"; }
    }
    private void NotifyState() { OnPropertyChanged(nameof(RootName)); OnPropertyChanged(nameof(CanEncrypt)); OnPropertyChanged(nameof(CanDecrypt)); OnPropertyChanged(nameof(CanCheckManifest)); }
    private static string L(string key) => LocalizationService.Instance.Get(key);
    private static string F(string key, params object?[] args) => LocalizationService.Instance.Format(key, args);
}
