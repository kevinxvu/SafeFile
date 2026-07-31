using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeFile.Core.Crypto;
using SafeFile.Core.Format;
using SafeFile.Core.IO;
using SafeFile.Core.Models;
using SafeFile.Core.Services;
using SafeFile.Services;
using Serilog;

namespace SafeFile.ViewModels;

public sealed partial class DecryptViewModel : ViewModelBase
{
    private static readonly ILogger Logger =
        Log.ForContext<DecryptViewModel>();
    private static readonly FilePickerFileType SafeFileType = new("SafeFile vault")
    {
        Patterns = ["*.safe"]
    };

    private readonly IFilePickerService _filePicker;
    private readonly IErrorDialogService _errorDialog;
    private readonly SettingsService _settingsService;
    private AppSettings _settings;
    private readonly HashSet<string> _sourcePaths = new(
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
    private CancellationTokenSource? _activeCts;

    public ObservableCollection<DecryptQueueItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedItem))]
    [NotifyPropertyChangedFor(nameof(DetailVaultName))]
    [NotifyPropertyChangedFor(nameof(DetailVaultPath))]
    [NotifyPropertyChangedFor(nameof(DetailOriginalFileName))]
    [NotifyPropertyChangedFor(nameof(DetailFormat))]
    [NotifyPropertyChangedFor(nameof(DetailMode))]
    [NotifyPropertyChangedFor(nameof(DetailChunkSize))]
    [NotifyPropertyChangedFor(nameof(DetailKdf))]
    [NotifyPropertyChangedFor(nameof(DetailAlgorithm))]
    [NotifyPropertyChangedFor(nameof(DetailVaultSize))]
    [NotifyPropertyChangedFor(nameof(DetailModifiedTime))]
    [NotifyPropertyChangedFor(nameof(DetailStatus))]
    [NotifyPropertyChangedFor(nameof(DetailError))]
    [NotifyPropertyChangedFor(nameof(HasDetailError))]
    private DecryptQueueItem? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordChar))]
    private string _password = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordChar))]
    private bool _showPassword;

    [ObservableProperty] private bool _overwriteExisting;
    [ObservableProperty] private bool _openFolderWhenDone = true;
    [ObservableProperty] private bool _isDecrypting;
    [ObservableProperty] private bool _isStatusBarVisible;
    [ObservableProperty] private string _statusAction = "";
    [ObservableProperty] private string _statusCurrentFile = "";
    [ObservableProperty] private double _statusProgress;
    [ObservableProperty] private string _statusDetails = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = "";
    [ObservableProperty] private string _passwordCheckMessage = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordCheckForeground))]
    private bool _isPasswordCheckError;

    public bool HasItems => Items.Count > 0;
    public bool HasSelectedItem => SelectedItem is not null;
    public char PasswordChar => ShowPassword ? '\0' : '•';
    public string OutputPath => _settings.DefaultDecryptOutputPath;
    public string PasswordCheckForeground =>
        IsPasswordCheckError ? "#DC2626" : "#16A34A";
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public string SelectionSummary =>
        Items.Count == 0
            ? L("NoDataSelected")
            : $"{Items.Count:N0} vault • {FormatBytes(Items.Sum(item => TryGetLength(item.SourcePath)))}";
    public string ResultSummary
    {
        get
        {
            var success = Items.Count(item => item.Status == L("Succeeded"));
            var failed = Items.Count(item =>
                item.Status == L("Failed") || item.Status == L("Invalid") ||
                item.Status == L("VerificationFailed"));
            var pending = Items.Count - success - failed;
            return F("BatchSummary", success, failed, pending);
        }
    }

    public string DetailVaultName => SelectedItem?.VaultName ?? "";
    public string DetailVaultPath => SelectedItem?.SourcePath ?? "";
    public string DetailOriginalFileName => SelectedItem?.OriginalFileName ?? "";
    public string DetailFormat => SelectedItem?.Header is { } header
        ? $"SafeFile v{header.Version}"
        : "";
    public string DetailMode => SelectedItem?.Header?.Mode switch
    {
        VaultMode.File => "File",
        VaultMode.Zip => L("VaultModeZip"),
        VaultMode.PerFile => "Per-file",
        { } mode => mode.ToString(),
        null => ""
    };
    public string DetailChunkSize => SelectedItem?.Header is { } header
        ? FormatBytes(header.ChunkSize)
        : "";
    public string DetailKdf => SelectedItem?.Header is { } header
        ? F("KdfDetails", header.KdfParameters.Iterations,
            header.KdfParameters.MemorySizeKb / 1024)
        : "";
    public string DetailAlgorithm => SelectedItem is null ? "" : "AES-256-GCM";
    public string DetailVaultSize => SelectedItem?.VaultSizeText ?? "";
    public string DetailModifiedTime => SelectedItem?.LastModifiedUtc
        .ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "";
    public string DetailStatus => SelectedItem?.Status ?? "";
    public string DetailError => SelectedItem?.ErrorMessage ?? "";
    public bool HasDetailError => !string.IsNullOrWhiteSpace(DetailError);

    public IAsyncRelayCommand BrowseSourceCommand { get; }
    public IAsyncRelayCommand BrowseSourceFolderCommand { get; }
    public IAsyncRelayCommand CheckPasswordCommand { get; }
    public IAsyncRelayCommand DecryptCommand { get; }
    public IRelayCommand ClearItemsCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public IRelayCommand TogglePasswordVisibilityCommand { get; }
    public IRelayCommand OpenOutputFolderCommand { get; }
    public IRelayCommand CloseStatusCommand { get; }

    public DecryptViewModel(
        IFilePickerService filePicker,
        IErrorDialogService errorDialog,
        SettingsService settingsService)
    {
        _filePicker = filePicker;
        _errorDialog = errorDialog;
        _settingsService = settingsService;
        _settings = _settingsService.Load();

        BrowseSourceCommand = new AsyncRelayCommand(BrowseSourceAsync);
        BrowseSourceFolderCommand = new AsyncRelayCommand(BrowseSourceFolderAsync);
        CheckPasswordCommand = new AsyncRelayCommand(CheckPasswordAsync);
        DecryptCommand = new AsyncRelayCommand(DecryptAsync);
        ClearItemsCommand = new RelayCommand(ClearItems);
        ResetCommand = new RelayCommand(Reset);
        TogglePasswordVisibilityCommand = new RelayCommand(
            () => ShowPassword = !ShowPassword);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        CloseStatusCommand = new RelayCommand(CloseOrCancelStatus);
    }

    public void RefreshSettings()
    {
        if (IsDecrypting)
            return;

        _settings = _settingsService.Load();
        OnPropertyChanged(nameof(OutputPath));
    }

    partial void OnPasswordChanged(string value)
    {
        PasswordCheckMessage = "";
        IsPasswordCheckError = false;
        foreach (var item in Items)
            item.ResetAuthentication();
        RefreshSelectionDetails();
        NotifySummaries();
    }

    public async Task AddDroppedSourcesAsync(IEnumerable<string> paths)
    {
        if (IsDecrypting)
            return;

        try
        {
            AddSources(paths);
        }
        catch (Exception ex)
        {
            await _errorDialog.ShowErrorAsync(
                ex.Message, L("CannotAddDecryptData"));
        }
    }

    private async Task BrowseSourceAsync()
    {
        var paths = await _filePicker.PickFilesAsync(
            L("SelectSafeFiles"), [SafeFileType]);
        await AddDroppedSourcesAsync(paths);
    }

    private async Task BrowseSourceFolderAsync()
    {
        var path = await _filePicker.PickFolderAsync(
            L("SelectSafeFolder"));
        if (path is not null)
            await AddDroppedSourcesAsync([path]);
    }

    private void AddSources(IEnumerable<string> paths)
    {
        var hadExistingItems = Items.Count > 0;
        var foundVault = false;
        foreach (var inputPath in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var fullPath = Path.GetFullPath(inputPath);
            if (Directory.Exists(fullPath))
            {
                foreach (var file in Directory.EnumerateFiles(
                             fullPath, "*.safe", SearchOption.AllDirectories))
                {
                    foundVault = true;
                    AddVault(
                        file,
                        fullPath,
                        GetRelativeDirectory(fullPath, file));
                }
            }
            else if (File.Exists(fullPath) &&
                     string.Equals(
                         Path.GetExtension(fullPath),
                         ".safe",
                         StringComparison.OrdinalIgnoreCase))
            {
                foundVault = true;
                AddVault(
                    fullPath,
                    Path.GetDirectoryName(fullPath) ?? "",
                    "");
            }
        }

        if (!foundVault && !hadExistingItems)
            throw new InvalidDataException(
                L("NoSafeFilesFound"));

        SelectedItem ??= Items.FirstOrDefault();
        PasswordCheckMessage = "";
        NotifySummaries();
        Logger.Information(
            "Added decryption sources; queue now contains {VaultCount} vaults",
            Items.Count);
    }

    private void AddVault(
        string sourcePath,
        string sourceRoot,
        string relativeDirectory)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!_sourcePaths.Add(sourcePath))
            return;

        VaultHeader? header = null;
        var initialStatus = L("Ready");
        var initialError = "";
        try
        {
            using var stream = File.Open(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            header = VaultHeader.ReadFrom(stream);
        }
        catch (Exception ex)
        {
            initialStatus = L("Invalid");
            initialError = ex.Message;
        }

        var fileInfo = new FileInfo(sourcePath);
        Items.Add(new DecryptQueueItem(
            sourcePath,
            sourceRoot,
            relativeDirectory,
            FormatBytes(fileInfo.Length),
            fileInfo.LastWriteTimeUtc,
            header,
            initialStatus,
            initialError,
            RemoveItem));
        RenumberItems();
    }

    private async Task CheckPasswordAsync()
    {
        if (!ValidateSelectionAndPassword(out var validationError))
        {
            PasswordCheckMessage = validationError;
            IsPasswordCheckError = true;
            return;
        }

        IsDecrypting = true;
        Logger.Information(
            "Password verification started for {VaultCount} vaults",
            Items.Count(item => item.IsValid));
        SetQueueLocked(true);
        IsStatusBarVisible = true;
        StatusAction = L("CheckingPassword");
        StatusProgress = 0;
        var cts = new CancellationTokenSource();
        _activeCts = cts;
        byte[]? passwordBytes = null;

        try
        {
            passwordBytes = Encoding.UTF8.GetBytes(Password);
            var validItems = Items.Where(item => item.IsValid).ToArray();
            var verified = 0;
            for (var index = 0; index < validItems.Length; index++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var item = validItems[index];
                StatusCurrentFile = item.VaultName;
                item.Status = L("Verifying");
                item.StatusForeground = "#2563EB";
                item.ErrorMessage = "";
                NotifySummaries();

                if (await VerifyItemAsync(item, passwordBytes, cts.Token))
                    verified++;

                StatusProgress = (index + 1d) / validItems.Length;
            }

            var failed = validItems.Length - verified;
            PasswordCheckMessage = F(
                "VerificationSummary", verified, validItems.Length,
                failed > 0 ? F("FailureSuffix", failed) : "");
            IsPasswordCheckError = failed > 0;
            StatusAction = L("VerificationCompleted");
            StatusDetails = PasswordCheckMessage;
            Logger.Information(
                "Password verification completed: {VerifiedCount} verified, {FailedCount} failed",
                verified,
                failed);
        }
        catch (OperationCanceledException)
        {
            StatusAction = L("VerificationCancelled");
            PasswordCheckMessage = L("OperationCancelled");
            Logger.Warning("Password verification cancelled");
        }
        finally
        {
            if (passwordBytes is not null)
                CryptographicOperations.ZeroMemory(passwordBytes);
            IsDecrypting = false;
            SetQueueLocked(false);
            _activeCts?.Dispose();
            _activeCts = null;
            RefreshSelectionDetails();
            NotifySummaries();
        }
    }

    private async Task<bool> VerifyItemAsync(
        DecryptQueueItem item,
        byte[] passwordBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await new FileEncryptor(
                consumerThreads: _settings.MaxThreads,
                settings: _settings).ReadVaultMetadataAsync(
                    item.SourcePath, passwordBytes, cancellationToken);

            item.OriginalFileName = metadata.OriginalFileName;
            item.HasVerifiedMetadata = true;
            item.Status = L("ReadyToDecrypt");
            item.StatusForeground = "#16A34A";
            item.ErrorMessage = "";
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            item.OriginalFileName = "—";
            item.HasVerifiedMetadata = false;
            item.Status = L("VerificationFailed");
            item.StatusForeground = "#DC2626";
            item.ErrorMessage = ex is InvalidOperationException or CryptographicException
                ? L("WrongPassword")
                : ex.Message;
            Logger.Warning(
                ex,
                "Vault verification failed for {VaultPath}",
                item.SourcePath);
            return false;
        }
    }

    private async Task DecryptAsync()
    {
        if (IsDecrypting)
            return;

        if (!ValidateSelectionAndPassword(out var validationError))
        {
            await _errorDialog.ShowErrorAsync(
                validationError, L("CannotDecrypt"));
            return;
        }
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            await _errorDialog.ShowErrorAsync(
                L("DecryptFolderNotConfigured"),
                L("CannotDecrypt"));
            return;
        }

        Directory.CreateDirectory(Path.GetFullPath(OutputPath));
        Logger.Information(
            "Batch decryption started for {VaultCount} vaults into {OutputDirectory}",
            Items.Count,
            OutputPath);
        IsDecrypting = true;
        SetQueueLocked(true);
        IsStatusBarVisible = true;
        StatusAction = L("Decrypting");
        StatusProgress = 0;
        StatusMessage = "";
        foreach (var item in Items.Where(item => item.IsValid))
        {
            item.Progress = 0;
            item.IsProcessing = false;
            item.ErrorMessage = "";
            item.Status = item.HasVerifiedMetadata
                ? L("ReadyToDecrypt")
                : L("Ready");
            item.StatusForeground = item.HasVerifiedMetadata
                ? "#16A34A"
                : "#4B5563";
        }
        NotifySummaries();
        StatusDetails = ResultSummary;
        var cts = new CancellationTokenSource();
        _activeCts = cts;
        byte[]? passwordBytes = null;
        var success = 0;
        var failed = Items.Count(item => !item.IsValid);

        try
        {
            passwordBytes = Encoding.UTF8.GetBytes(Password);
            foreach (var invalidItem in Items.Where(item => !item.IsValid))
            {
                invalidItem.Status = L("Failed");
                invalidItem.StatusForeground = "#DC2626";
            }
            var validItems = Items.Where(item => item.IsValid).ToArray();
            var completedItems = failed;
            for (var index = 0; index < validItems.Length; index++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var item = validItems[index];
                StatusCurrentFile = item.VaultName;

                if (!item.HasVerifiedMetadata &&
                    !await VerifyItemAsync(item, passwordBytes, cts.Token))
                {
                    item.Status = L("Failed");
                    failed++;
                    completedItems++;
                    UpdateOverallProgress(completedItems, Items.Count, 0);
                    NotifySummaries();
                    continue;
                }

                item.IsProcessing = true;
                item.Progress = 0;
                item.Status = L("Decrypting");
                item.StatusForeground = "#2563EB";
                item.ErrorMessage = "";
                NotifySummaries();
                StatusDetails = ResultSummary;

                try
                {
                    await DecryptItemAsync(
                        item,
                        passwordBytes,
                        cts.Token,
                        new Progress<double>(value =>
                        {
                            item.Progress = value;
                            UpdateOverallProgress(
                                completedItems, Items.Count, value);
                        }));
                    item.Progress = 1;
                    item.Status = L("Succeeded");
                    item.StatusForeground = "#16A34A";
                    success++;
                    Logger.Information(
                        "Vault decrypted successfully: {VaultPath}",
                        item.SourcePath);
                }
                catch (OperationCanceledException)
                {
                    item.Status = L("Cancelled");
                    item.StatusForeground = "#6B7280";
                    Logger.Warning(
                        "Vault decryption cancelled: {VaultPath}",
                        item.SourcePath);
                    throw;
                }
                catch (Exception ex)
                {
                    item.Status = L("Failed");
                    item.StatusForeground = "#DC2626";
                    item.ErrorMessage = GetFriendlyError(ex);
                    failed++;
                    Logger.Error(
                        ex,
                        "Vault decryption failed: {VaultPath}",
                        item.SourcePath);
                }
                finally
                {
                    item.IsProcessing = false;
                    completedItems++;
                    UpdateOverallProgress(completedItems, Items.Count, 0);
                    RefreshSelectionDetails();
                    NotifySummaries();
                }
            }

            StatusAction = failed == 0
                ? L("DecryptCompleted")
                : L("DecryptCompletedWithErrors");
            StatusDetails = F("SuccessFailureSummary", success, failed);
            StatusMessage = F(
                "CompletionSummary", success,
                failed > 0 ? F("FailureSuffix", failed) : "");
            Logger.Information(
                "Batch decryption completed: {SuccessCount} succeeded, {FailedCount} failed",
                success,
                failed);

            if (failed > 0)
            {
                await _errorDialog.ShowErrorAsync(
                    F("BatchFailureDetails", success, failed),
                    L("CompletedWithErrors"));
            }
            if (OpenFolderWhenDone && success > 0)
                _filePicker.OpenFolder(OutputPath);
        }
        catch (OperationCanceledException)
        {
            StatusAction = L("DecryptCancelled");
            StatusMessage = L("OperationCancelled");
            Logger.Warning(
                "Batch decryption cancelled after {SuccessCount} successful and {FailedCount} failed vaults",
                success,
                failed);
        }
        finally
        {
            if (passwordBytes is not null)
                CryptographicOperations.ZeroMemory(passwordBytes);
            IsDecrypting = false;
            SetQueueLocked(false);
            _activeCts?.Dispose();
            _activeCts = null;
            NotifySummaries();
        }
    }

    private async Task DecryptItemAsync(
        DecryptQueueItem item,
        byte[] passwordBytes,
        CancellationToken cancellationToken,
        IProgress<double> progress)
    {
        var relativeRoot = string.IsNullOrWhiteSpace(item.RelativeDirectory)
            ? Path.GetFullPath(OutputPath)
            : Path.Combine(Path.GetFullPath(OutputPath), item.RelativeDirectory);
        Directory.CreateDirectory(relativeRoot);

        var destination = GetDestinationPath(item, relativeRoot);
        var mode = item.Header!.Mode;
        if ((File.Exists(destination) || Directory.Exists(destination)) &&
            !(mode is VaultMode.File or VaultMode.PerFile &&
              OverwriteExisting &&
              File.Exists(destination)))
        {
            throw new IOException(
                L("OutputAlreadyExists"));
        }

        var encryptor = new FileEncryptor(
            _settings.MaxThreads, progress, _settings);
        switch (mode)
        {
            case VaultMode.File:
                await encryptor.DecryptFileAsync(
                    item.SourcePath,
                    destination,
                    passwordBytes,
                    cancellationToken,
                    OverwriteExisting);
                break;
            case VaultMode.Zip:
                await encryptor.DecryptFolderZipAsync(
                    item.SourcePath,
                    destination,
                    passwordBytes,
                    cancellationToken);
                break;
            case VaultMode.PerFile:
                await encryptor.DecryptPerFileVaultAsync(
                    item.SourcePath,
                    destination,
                    passwordBytes,
                    cancellationToken,
                    OverwriteExisting);
                break;
            default:
                throw new InvalidDataException(
                    F("UnsupportedVaultMode", mode));
        }
    }

    private static string GetDestinationPath(
        DecryptQueueItem item,
        string destinationRoot)
    {
        if (item.Header?.Mode == VaultMode.Zip)
        {
            var folderName = Path.GetFileNameWithoutExtension(
                item.OriginalFileName);
            return Path.Combine(destinationRoot, folderName);
        }

        return Path.Combine(destinationRoot, item.OriginalFileName);
    }

    private bool ValidateSelectionAndPassword(out string error)
    {
        if (Items.Count == 0)
        {
            error = L("DecryptSelectionRequired");
            return false;
        }
        if (!Items.Any(item => item.IsValid))
        {
            error = L("NoValidVaults");
            return false;
        }
        if (string.IsNullOrEmpty(Password))
        {
            error = L("DecryptPasswordRequired");
            return false;
        }

        error = "";
        return true;
    }

    private void RemoveItem(DecryptQueueItem item)
    {
        if (IsDecrypting)
            return;

        var index = Items.IndexOf(item);
        if (index < 0)
            return;
        Items.RemoveAt(index);
        RenumberItems();
        _sourcePaths.Remove(item.SourcePath);
        if (ReferenceEquals(SelectedItem, item))
            SelectedItem = Items.Count == 0
                ? null
                : Items[Math.Min(index, Items.Count - 1)];
        NotifySummaries();
    }

    private void RenumberItems()
    {
        for (var index = 0; index < Items.Count; index++)
            Items[index].SequenceNumber = index + 1;
    }

    private void ClearItems()
    {
        if (IsDecrypting)
            return;
        Items.Clear();
        _sourcePaths.Clear();
        SelectedItem = null;
        PasswordCheckMessage = "";
        StatusMessage = "";
        NotifySummaries();
    }

    private void Reset()
    {
        if (IsDecrypting)
            return;

        ClearItems();
        Password = "";
        ShowPassword = false;
        OverwriteExisting = false;
        OpenFolderWhenDone = true;
        PasswordCheckMessage = "";
        IsPasswordCheckError = false;
        IsStatusBarVisible = false;
        StatusAction = "";
        StatusCurrentFile = "";
        StatusProgress = 0;
        StatusDetails = "";
        StatusMessage = "";
    }

    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(OutputPath);
            _filePicker.OpenFolder(OutputPath);
            Logger.Debug(
                "Opened decrypted output directory {OutputDirectory}",
                OutputPath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open decrypted output directory");
            _ = _errorDialog.ShowErrorAsync(
                ex.Message, L("CannotOpenOutputFolder"));
        }
    }

    private void CloseOrCancelStatus()
    {
        if (IsDecrypting)
            _activeCts?.Cancel();
        else
            IsStatusBarVisible = false;
    }

    private void UpdateOverallProgress(
        int completedItems,
        int totalItems,
        double currentItemProgress)
    {
        StatusProgress = totalItems == 0
            ? 0
            : Math.Clamp(
                (completedItems + currentItemProgress) / totalItems,
                0,
                1);
        StatusDetails = ResultSummary;
    }

    private void NotifySummaries()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void SetQueueLocked(bool isLocked)
    {
        foreach (var item in Items)
            item.IsLocked = isLocked;
    }

    private void RefreshSelectionDetails()
    {
        OnPropertyChanged(nameof(DetailVaultName));
        OnPropertyChanged(nameof(DetailVaultPath));
        OnPropertyChanged(nameof(DetailOriginalFileName));
        OnPropertyChanged(nameof(DetailFormat));
        OnPropertyChanged(nameof(DetailMode));
        OnPropertyChanged(nameof(DetailChunkSize));
        OnPropertyChanged(nameof(DetailKdf));
        OnPropertyChanged(nameof(DetailAlgorithm));
        OnPropertyChanged(nameof(DetailVaultSize));
        OnPropertyChanged(nameof(DetailModifiedTime));
        OnPropertyChanged(nameof(DetailStatus));
        OnPropertyChanged(nameof(DetailError));
        OnPropertyChanged(nameof(HasDetailError));
    }

    private static string GetRelativeDirectory(
        string sourceRoot,
        string filePath)
    {
        var directory = Path.GetDirectoryName(filePath) ?? sourceRoot;
        var relative = Path.GetRelativePath(sourceRoot, directory);
        return relative == "." ? "" : relative;
    }

    private static long TryGetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string GetFriendlyError(Exception ex) =>
        ex is InvalidOperationException or CryptographicException
            ? L("WrongPassword")
            : ex.Message;

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private static string L(string key) => LocalizationService.Instance.Get(key);
    private static string F(string key, params object?[] args) =>
        LocalizationService.Instance.Format(key, args);
}
