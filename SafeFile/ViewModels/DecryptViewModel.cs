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
using SafeFile.Models;
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
    private readonly Microsoft.Extensions.Logging.ILogger<FileEncryptor> _fileEncryptorLogger;
    private readonly TaskbarProgressTracker _taskbarProgress;
    private AppSettings _settings;
    private readonly HashSet<string> _sourcePaths = new(
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
    private readonly HashSet<string> _folderSourceRoots = new(
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
    private CancellationTokenSource? _activeCts;

    public ObservableCollection<DecryptQueueItem> Items { get; } = [];
    public ObservableCollection<ExcludedFolderItem> ExcludedFolders { get; } = [];
    public bool HasExcludedFolders => ExcludedFolders.Count > 0;
    public bool HasFolderSources => _folderSourceRoots.Count > 0;

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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isDecrypting;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isScanning;
    [ObservableProperty] private bool _isStatusBarVisible;
    [ObservableProperty] private string _statusAction = "";
    [ObservableProperty] private string _statusCurrentFile = "";
    [ObservableProperty] private string _statusCurrentFilePath = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusProgressPercent))]
    private double _statusProgress;

    public int StatusProgressPercent => StatusProgress >= 1
        ? 100
        : (int)Math.Floor(Math.Clamp(StatusProgress, 0, 1) * 100);
    [ObservableProperty] private string _statusDetails = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusTiming))]
    private string _statusSpeed = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusTiming))]
    private string _statusEta = "";
    public string StatusTiming => string.Join(
        " \u2022 ",
        new[] { StatusSpeed, StatusEta }
            .Where(value => !string.IsNullOrEmpty(value)));
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = "";
    [ObservableProperty] private string _passwordCheckMessage = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordCheckForeground))]
    private bool _isPasswordCheckError;

    public bool HasItems => Items.Count > 0;
    public bool IsBusy => IsDecrypting || IsScanning;
    public bool HasSelectedItem => SelectedItem is not null;
    public char PasswordChar => ShowPassword ? '\0' : '•';
    public string OutputPath => _settings.DefaultDecryptOutputPath;
    public string PasswordCheckForeground =>
        IsPasswordCheckError ? "#DC2626" : "#16A34A";
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public string SelectionSummary =>
        Items.Count == 0
            ? L("NoDataSelected")
            : $"{Items.Count:N0} vault • {FormatBytes(Items.Sum(item => item.VaultSizeBytes))}";
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
    public IAsyncRelayCommand BrowseExcludedFoldersCommand { get; }
    public IAsyncRelayCommand ClearExcludedFoldersCommand { get; }
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
        SettingsService settingsService,
        Microsoft.Extensions.Logging.ILogger<FileEncryptor> fileEncryptorLogger,
        ITaskbarProgressService? taskbarProgress = null)
    {
        _filePicker = filePicker;
        _errorDialog = errorDialog;
        _settingsService = settingsService;
        _fileEncryptorLogger = fileEncryptorLogger;
        _taskbarProgress = new TaskbarProgressTracker(
            taskbarProgress ?? NullTaskbarProgressService.Instance,
            Logger);
        _settings = _settingsService.Load();

        BrowseSourceCommand = new AsyncRelayCommand(BrowseSourceAsync);
        BrowseSourceFolderCommand = new AsyncRelayCommand(BrowseSourceFolderAsync);
        BrowseExcludedFoldersCommand = new AsyncRelayCommand(BrowseExcludedFoldersAsync);
        ClearExcludedFoldersCommand = new AsyncRelayCommand(ClearExcludedFoldersAsync);
        CheckPasswordCommand = new AsyncRelayCommand(CheckPasswordAsync);
        DecryptCommand = new AsyncRelayCommand(DecryptAsync);
        ClearItemsCommand = new RelayCommand(ClearItems);
        ResetCommand = new RelayCommand(Reset);
        TogglePasswordVisibilityCommand = new RelayCommand(
            () => ShowPassword = !ShowPassword);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        CloseStatusCommand = new RelayCommand(CloseOrCancelStatus);
    }

    partial void OnIsDecryptingChanged(bool value)
    {
        if (value)
            _taskbarProgress.Begin();
        else
            _taskbarProgress.End();
    }

    partial void OnStatusProgressChanged(double value) =>
        _taskbarProgress.Report(value);

    public void RefreshSettings()
    {
        if (IsBusy)
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
        if (IsBusy)
            return;

        try
        {
            await AddSourcesAsync(paths);
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

    private async Task BrowseExcludedFoldersAsync()
    {
        if (_folderSourceRoots.Count == 0)
            return;
        var paths = await _filePicker.PickFoldersAsync(L("SelectExcludedFolders"));
        if (paths.Count == 0)
            return;
        try
        {
            var merged = ExcludedFolderPathValidator.MergeAndValidate(
                paths, _folderSourceRoots,
                ExcludedFolders.Select(item => item.FullPath));
            ReplaceExcludedFolders(merged);
            RemoveExcludedQueueItems();
        }
        catch (Exception ex)
        {
            var message = ex is ExcludedFolderValidationException validation
                ? L(validation.ResourceKey)
                : ex.Message;
            await _errorDialog.ShowErrorAsync(message, L("CannotAddExcludedFolders"));
        }
    }

    private async void RemoveExcludedFolder(ExcludedFolderItem item)
    {
        if (IsBusy || !ExcludedFolders.Remove(item))
            return;
        OnPropertyChanged(nameof(HasExcludedFolders));
        await RescanFolderSourcesAsync();
    }

    private async Task ClearExcludedFoldersAsync()
    {
        if (IsBusy || ExcludedFolders.Count == 0)
            return;
        ExcludedFolders.Clear();
        OnPropertyChanged(nameof(HasExcludedFolders));
        await RescanFolderSourcesAsync();
    }

    private void ReplaceExcludedFolders(IEnumerable<string> paths)
    {
        ExcludedFolders.Clear();
        foreach (var path in paths)
            ExcludedFolders.Add(new ExcludedFolderItem(path, RemoveExcludedFolder));
        OnPropertyChanged(nameof(HasExcludedFolders));
    }

    private void RemoveExcludedQueueItems()
    {
        foreach (var item in Items.Where(item => IsExcluded(item.SourcePath)).ToArray())
            RemoveItem(item);
    }

    private async Task RescanFolderSourcesAsync()
    {
        if (_folderSourceRoots.Count == 0)
            return;
        try
        {
            await AddSourcesAsync(_folderSourceRoots.ToArray());
        }
        catch (InvalidDataException)
        {
            NotifySummaries();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to rescan decryption source folders");
            await _errorDialog.ShowErrorAsync(
                ex.Message,
                L("CannotAddDecryptData"));
        }
    }

    private async Task AddSourcesAsync(IEnumerable<string> paths)
    {
        var hadExistingItems = Items.Count > 0;
        var inputPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToArray();
        var excludedPaths = ExcludedFolders
            .Select(item => item.FullPath)
            .ToArray();
        var readyText = L("Ready");
        var invalidText = L("Invalid");

        IsScanning = true;
        SetQueueLocked(true);
        IsStatusBarVisible = true;
        StatusAction = L("ScanningVaults");
        StatusDetails = F("VaultsFound", 0);
        StatusCurrentFile = inputPaths.FirstOrDefault() ?? "";
        StatusCurrentFilePath = StatusCurrentFile;
        StatusProgress = 0;
        StatusSpeed = "";
        StatusEta = "";
        var cts = new CancellationTokenSource();
        _activeCts = cts;
        var scanProgress = new Progress<SourceScanProgress>(value =>
        {
            StatusDetails = F("VaultsFound", value.VaultCount);
            StatusCurrentFile = Path.GetFileName(value.CurrentPath);
            StatusCurrentFilePath = value.CurrentPath;
        });

        try
        {
            var scanResult = await Task.Run(() => ScanSources(
                inputPaths,
                excludedPaths,
                readyText,
                invalidText,
                scanProgress,
                cts.Token));

            foreach (var folderRoot in scanResult.FolderRoots)
                _folderSourceRoots.Add(folderRoot);

            const int UiBatchSize = 100;
            for (var index = 0; index < scanResult.Vaults.Count; index++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var vault = scanResult.Vaults[index];
                if (_sourcePaths.Add(vault.SourcePath))
                {
                    Items.Add(new DecryptQueueItem(
                        vault.SourcePath,
                        vault.SourceRoot,
                        vault.RelativeDirectory,
                        vault.Length,
                        FormatBytes(vault.Length),
                        vault.LastWriteTimeUtc,
                        vault.Header,
                        vault.InitialStatus,
                        vault.InitialError,
                        RemoveItem));
                    if (Items.Count == 1)
                        OnPropertyChanged(nameof(HasItems));
                }

                if ((index + 1) % UiBatchSize == 0)
                    await Task.Yield();
            }

            if (scanResult.Vaults.Count == 0 && !hadExistingItems)
                throw new InvalidDataException(
                    L("NoSafeFilesFound"));

            StatusAction = L("ScanCompleted");
            StatusDetails = F("VaultsFound", scanResult.Vaults.Count);
            StatusCurrentFile = "";
            StatusCurrentFilePath = "";
            RenumberItems();
            SelectedItem ??= Items.FirstOrDefault();
            PasswordCheckMessage = "";
            NotifySummaries();
            OnPropertyChanged(nameof(HasFolderSources));
            Logger.Information(
                "Added decryption sources; queue now contains {VaultCount} vaults",
                Items.Count);
        }
        catch (OperationCanceledException)
        {
            StatusAction = L("ScanCancelled");
            StatusCurrentFile = "";
            StatusCurrentFilePath = "";
            RenumberItems();
            SelectedItem ??= Items.FirstOrDefault();
            NotifySummaries();
            OnPropertyChanged(nameof(HasFolderSources));
            Logger.Warning("Decryption source scan cancelled");
        }
        catch
        {
            StatusAction = L("ScanFailed");
            StatusCurrentFile = "";
            StatusCurrentFilePath = "";
            throw;
        }
        finally
        {
            IsScanning = false;
            SetQueueLocked(false);
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }

    private static SourceScanResult ScanSources(
        IReadOnlyCollection<string> inputPaths,
        IReadOnlyCollection<string> excludedPaths,
        string readyText,
        string invalidText,
        IProgress<SourceScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var folderRoots = new List<string>();
        var vaults = new List<ScannedVault>();
        foreach (var fullPath in inputPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(fullPath))
            {
                var folderRoot = Path.TrimEndingDirectorySeparator(fullPath);
                folderRoots.Add(folderRoot);
                foreach (var file in EnumerateVaultFiles(
                             new DirectoryInfo(folderRoot), excludedPaths,
                             cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    vaults.Add(ReadVaultForQueue(
                        file,
                        folderRoot,
                        GetRelativeDirectory(folderRoot, file),
                        readyText,
                        invalidText));
                    if (vaults.Count == 1 || vaults.Count % 25 == 0)
                        progress?.Report(new SourceScanProgress(file, vaults.Count));
                }
            }
            else if (File.Exists(fullPath) &&
                     string.Equals(Path.GetExtension(fullPath), ".safe",
                         StringComparison.OrdinalIgnoreCase) &&
                     !IsExcluded(fullPath, excludedPaths))
            {
                vaults.Add(ReadVaultForQueue(
                    fullPath,
                    Path.GetDirectoryName(fullPath) ?? "",
                    "",
                    readyText,
                    invalidText));
                progress?.Report(new SourceScanProgress(fullPath, vaults.Count));
            }
        }

        return new SourceScanResult(folderRoots, vaults);
    }

    private static IEnumerable<string> EnumerateVaultFiles(
        DirectoryInfo directory,
        IReadOnlyCollection<string> excludedPaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var file in directory.EnumerateFiles("*.safe"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((file.Attributes & FileAttributes.ReparsePoint) == 0 &&
                !IsExcluded(file.FullName, excludedPaths))
                yield return file.FullName;
        }

        foreach (var child in directory.EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0 ||
                IsExcluded(child.FullName, excludedPaths))
                continue;
            foreach (var file in EnumerateVaultFiles(
                         child, excludedPaths, cancellationToken))
                yield return file;
        }
    }

    private bool IsExcluded(string path) => ExcludedFolders.Any(item =>
        ExcludedFolderPathValidator.IsSameOrDescendant(item.FullPath, path));

    private static bool IsExcluded(
        string path,
        IReadOnlyCollection<string> excludedPaths) => excludedPaths.Any(
        excludedPath => ExcludedFolderPathValidator.IsSameOrDescendant(
            excludedPath,
            path));

    private static ScannedVault ReadVaultForQueue(
        string sourcePath,
        string sourceRoot,
        string relativeDirectory,
        string readyText,
        string invalidText)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        VaultHeader? header = null;
        var initialStatus = readyText;
        var initialError = "";
        try
        {
            using var stream = File.Open(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            header = VaultHeader.ReadFrom(stream);
        }
        catch (Exception ex)
        {
            initialStatus = invalidText;
            initialError = ex.Message;
        }

        var fileInfo = new FileInfo(sourcePath);
        return new ScannedVault(
            sourcePath,
            sourceRoot,
            relativeDirectory,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc,
            header,
            initialStatus,
            initialError);
    }

    private sealed record SourceScanResult(
        IReadOnlyList<string> FolderRoots,
        IReadOnlyList<ScannedVault> Vaults);

    private sealed record SourceScanProgress(
        string CurrentPath,
        int VaultCount);

    private sealed record ScannedVault(
        string SourcePath,
        string SourceRoot,
        string RelativeDirectory,
        long Length,
        DateTime LastWriteTimeUtc,
        VaultHeader? Header,
        string InitialStatus,
        string InitialError);

    private async Task CheckPasswordAsync()
    {
        if (IsBusy)
            return;

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
        StatusCurrentFile = "";
        StatusCurrentFilePath = "";
        StatusProgress = 0;
        StatusSpeed = "";
        StatusEta = "";
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
                StatusCurrentFilePath = item.SourcePath;
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
            StatusMessage = L("OperationCancelled");
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
                settings: _settings,
                logger: _fileEncryptorLogger).ReadVaultMetadataAsync(
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
        if (IsBusy)
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
        StatusCurrentFile = "";
        StatusCurrentFilePath = "";
        StatusProgress = 0;
        StatusSpeed = "";
        StatusEta = "";
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
        var totalBytes = Items.Sum(item => TryGetLength(item.SourcePath));
        var resolvedBytes = Items
            .Where(item => !item.IsValid)
            .Sum(item => TryGetLength(item.SourcePath));
        var transferredBytes = 0L;
        var etaEstimator = new TransferMetricsEstimator();
        if (totalBytes > 0)
            StatusEta = L("CalculatingEta");

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
                var currentItemBytes = TryGetLength(item.SourcePath);
                StatusCurrentFile = item.VaultName;
                StatusCurrentFilePath = item.SourcePath;

                if (!item.HasVerifiedMetadata &&
                    !await VerifyItemAsync(item, passwordBytes, cts.Token))
                {
                    item.Status = L("Failed");
                    failed++;
                    completedItems++;
                    resolvedBytes += currentItemBytes;
                    UpdateOverallProgress(completedItems, Items.Count, 0);
                    UpdateTransferMetrics(
                        transferredBytes,
                        resolvedBytes,
                        totalBytes,
                        etaEstimator,
                        includeSample: false);
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

                var lastObservedProgress = 0d;
                var itemSucceeded = false;
                var itemCancelled = false;
                try
                {
                    await DecryptItemAsync(
                        item,
                        passwordBytes,
                        cts.Token,
                        new Progress<double>(value =>
                        {
                            item.Progress = value;
                            lastObservedProgress = Math.Max(
                                lastObservedProgress,
                                Math.Clamp(value, 0, 1));
                            UpdateOverallProgress(
                                completedItems, Items.Count, value);
                            UpdateTransferMetrics(
                                transferredBytes +
                                    (long)(currentItemBytes * lastObservedProgress),
                                resolvedBytes +
                                    (long)(currentItemBytes * lastObservedProgress),
                                totalBytes,
                                etaEstimator);
                        }));
                    item.Progress = 1;
                    item.Status = L("Succeeded");
                    item.StatusForeground = "#16A34A";
                    success++;
                    itemSucceeded = true;
                    Logger.Information(
                        "Vault decrypted successfully: {VaultPath}",
                        item.SourcePath);
                }
                catch (OperationCanceledException)
                {
                    itemCancelled = true;
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
                    if (!itemCancelled)
                    {
                        completedItems++;
                        transferredBytes += itemSucceeded
                            ? currentItemBytes
                            : (long)(currentItemBytes * lastObservedProgress);
                        resolvedBytes += currentItemBytes;
                        UpdateOverallProgress(completedItems, Items.Count, 0);
                        UpdateTransferMetrics(
                            transferredBytes,
                            resolvedBytes,
                            totalBytes,
                            etaEstimator,
                            includeSample: itemSucceeded || lastObservedProgress > 0);
                    }
                    RefreshSelectionDetails();
                    NotifySummaries();
                }
            }

            StatusAction = failed == 0
                ? L("DecryptCompleted")
                : L("DecryptCompletedWithErrors");
            StatusDetails = F("SuccessFailureSummary", success, failed);
            StatusSpeed = "";
            StatusEta = "";
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
            StatusEta = "";
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
            _settings.MaxThreads,
            progress,
            _settings,
            logger: _fileEncryptorLogger);
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
        if (IsBusy)
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
        if (IsBusy)
            return;
        Items.Clear();
        _sourcePaths.Clear();
        _folderSourceRoots.Clear();
        ExcludedFolders.Clear();
        SelectedItem = null;
        PasswordCheckMessage = "";
        StatusMessage = "";
        NotifySummaries();
        OnPropertyChanged(nameof(HasFolderSources));
        OnPropertyChanged(nameof(HasExcludedFolders));
    }

    private void Reset()
    {
        if (IsBusy)
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
        StatusCurrentFilePath = "";
        StatusProgress = 0;
        StatusDetails = "";
        StatusSpeed = "";
        StatusEta = "";
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
        if (IsBusy)
        {
            StatusAction = L("CancellationRequested");
            if (IsDecrypting)
                StatusMessage = L("CancellationRequested");
            _activeCts?.Cancel();
        }
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

    private void UpdateTransferMetrics(
        long transferredBytes,
        long resolvedBytes,
        long totalBytes,
        TransferMetricsEstimator estimator,
        bool includeSample = true)
    {
        if (totalBytes <= 0)
        {
            StatusSpeed = "";
            StatusEta = "";
            return;
        }

        var estimate = estimator.Update(
            transferredBytes,
            totalBytes,
            resolvedBytes,
            includeSample);
        StatusSpeed = estimate.BytesPerSecond is > 0
            ? FormatSpeed(estimate.BytesPerSecond.Value)
            : "";
        StatusEta = estimate.RemainingSeconds is { } remaining
            ? F("EstimatedTimeRemaining", FormatEta(remaining))
            : resolvedBytes < totalBytes
                ? L("CalculatingEta")
                : "";
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

    private static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond switch
        {
            >= 1_000_000 => $"{bytesPerSecond / 1_000_000:F0} MB/s",
            >= 1_000 => $"{bytesPerSecond / 1_000:F0} KB/s",
            _ => $"{bytesPerSecond:F0} B/s"
        };

    private static string FormatEta(double seconds)
    {
        if (seconds < 0)
            return "";
        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : $"{duration.Minutes:D2}:{duration.Seconds:D2}";
    }

    private static string L(string key) => LocalizationService.Instance.Get(key);
    private static string F(string key, params object?[] args) =>
        LocalizationService.Instance.Format(key, args);
}
