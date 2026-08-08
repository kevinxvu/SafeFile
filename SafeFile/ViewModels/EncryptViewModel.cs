using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeFile.Core.Format;
using SafeFile.Core.IO;
using SafeFile.Core.Models;
using SafeFile.Core.Services;
using SafeFile.Models;
using SafeFile.Services;
using Serilog;

namespace SafeFile.ViewModels;

public sealed partial class EncryptViewModel : ViewModelBase
{
    private static readonly ILogger Logger =
        Log.ForContext<EncryptViewModel>();
    private readonly IFilePickerService _filePicker;
    private readonly IErrorDialogService _errorDialog;
    private readonly SettingsService _settingsService;
    private readonly Microsoft.Extensions.Logging.ILogger<FileEncryptor> _fileEncryptorLogger;
    private AppSettings _settings;
    private CancellationTokenSource? _activeCts;
    private CancellationTokenSource? _sourceInfoCts;
    private IReadOnlyList<string> _sourcePaths = [];

    public ObservableCollection<ExcludedFolderItem> ExcludedFolders { get; } = [];
    public bool HasExcludedFolders => ExcludedFolders.Count > 0;

    // ── Source ────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedOutputName))]
    [NotifyPropertyChangedFor(nameof(IsFolderSource))]
    private bool _isFileSource = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedOutputName))]
    private string _sourcePath = "";

    [ObservableProperty] private bool _hasSourceInfo;
    [ObservableProperty] private string _sourceDisplayName = "";
    [ObservableProperty] private string _sourceTypeText = "";
    [ObservableProperty] private string _sourceSizeText = "";
    [ObservableProperty] private string _sourceFileCountText = "";

    public bool IsFolderSource => !IsFileSource;
    public bool IsMultipleFileSource => IsFileSource && _sourcePaths.Count > 1;
    public string SourceFilesTooltip => IsMultipleFileSource
        ? FormatNumberedLines(
            _sourcePaths.Select(path => Path.GetFileName(path) ?? path))
        : Path.GetFileName(SourcePath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
    public string SourcePathsTooltip => IsMultipleFileSource
        ? FormatNumberedLines(_sourcePaths)
        : SourcePath;

    partial void OnIsFileSourceChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFolderSource));
        _sourcePaths = [];
        SourcePath = "";
    }

    partial void OnSourcePathChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            (_sourcePaths.Count == 0 || !_sourcePaths.Contains(value, PathComparer)))
            _sourcePaths = [value];
        if (string.IsNullOrWhiteSpace(value))
            _sourcePaths = [];
        OnPropertyChanged(nameof(IsMultipleFileSource));
        OnPropertyChanged(nameof(SourceFilesTooltip));
        OnPropertyChanged(nameof(SourcePathsTooltip));
        _sourceInfoCts?.Cancel();
        _sourceInfoCts?.Dispose();
        _sourceInfoCts = new CancellationTokenSource();
        _ = RefreshSourceInfoAsync(value, _sourceInfoCts.Token);
    }

    private async Task RefreshSourceInfoAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            HasSourceInfo = false;
            SourceDisplayName = "";
            SourceTypeText = "";
            SourceSizeText = "";
            SourceFileCountText = "";
            return;
        }

        var selectedPaths = IsFileSource
            ? _sourcePaths.Where(File.Exists).ToArray()
            : [path];
        var normalizedPath = path.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var isFile = selectedPaths.Length > 0 && IsFileSource;
        var isFolder = Directory.Exists(path);
        if (!isFile && !isFolder)
        {
            HasSourceInfo = false;
            return;
        }

        HasSourceInfo = true;
        SourceDisplayName = selectedPaths.Length > 1
            ? F("FilesSelected", selectedPaths.Length)
            : Path.GetFileName(normalizedPath);
        SourceTypeText = L(selectedPaths.Length > 1 ? "Files" : isFile ? "File" : "Folder");
        SourceSizeText = L("CalculatingSourceInfo");
        SourceFileCountText = L("CalculatingSourceInfo");
        var excludedPaths = ExcludedFolders.Select(item => item.FullPath).ToArray();

        try
        {
            var result = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (isFile)
                    return (
                        Size: selectedPaths.Sum(file => new FileInfo(file).Length),
                        FileCount: selectedPaths.Length);

                long size = 0;
                var fileCount = 0;
                foreach (var file in EnumerateRegularFiles(
                             new DirectoryInfo(path), excludedPaths))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    size += file.Length;
                    fileCount++;
                }

                return (Size: size, FileCount: fileCount);
            }, cancellationToken);

            if (SourcePath != path)
                return;

            SourceSizeText = FormatBytes(result.Size);
            SourceFileCountText = F("SourceFileCount", result.FileCount);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (SourcePath == path)
            {
                SourceSizeText = L("SourceInfoUnavailable");
                SourceFileCountText = L("SourceInfoUnavailable");
            }
        }
    }

    // ── Password ──────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordStrength))]
    [NotifyPropertyChangedFor(nameof(PasswordStrengthLabel))]
    [NotifyPropertyChangedFor(nameof(PasswordStrengthForeground))]
    [NotifyPropertyChangedFor(nameof(PasswordChar))]
    private string _password = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmPasswordChar))]
    private string _confirmPassword = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordChar))]
    private bool _showPassword;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmPasswordChar))]
    private bool _showConfirmPassword;

    public char PasswordChar => ShowPassword ? '\0' : '•';
    public char ConfirmPasswordChar => ShowConfirmPassword ? '\0' : '•';
    public bool IsPasswordConfirmationRequired =>
        _settings.ConfirmPasswordToggle;

    public double PasswordStrength
    {
        get
        {
            if (string.IsNullOrEmpty(Password)) return 0;
            double score = 0;
            if (Password.Length >= 8) score += 25;
            if (Password.Length >= 12) score += 15;
            if (Password.Length >= 16) score += 10;
            if (Password.Any(char.IsUpper)) score += 15;
            if (Password.Any(char.IsLower)) score += 10;
            if (Password.Any(char.IsDigit)) score += 15;
            if (Password.Any(c => !char.IsLetterOrDigit(c))) score += 10;
            return Math.Min(score, 100);
        }
    }

    public string PasswordStrengthLabel =>
        PasswordStrength switch
        {
            0 => "",
            < 30 => L("Weak"),
            < 60 => L("Medium"),
            < 80 => L("Strong"),
            _ => L("VeryStrong")
        };

    public string PasswordStrengthForeground =>
        PasswordStrength switch
        {
            0 => "Transparent",
            < 30 => "#E53935",
            < 60 => "#FB8C00",
            < 80 => "#43A047",
            _ => "#00897B"
        };

    // ── Encryption options ────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedOutputName))]
    private bool _encryptFileNames;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAesOutputFileNameMode))]
    [NotifyPropertyChangedFor(nameof(IsSha256OutputFileNameMode))]
    [NotifyPropertyChangedFor(nameof(IsMd5OutputFileNameMode))]
    [NotifyPropertyChangedFor(nameof(EstimatedOutputName))]
    private OutputFileNameMode _selectedOutputFileNameMode = OutputFileNameMode.Md5;

    public bool IsAesOutputFileNameMode
    {
        get => SelectedOutputFileNameMode == OutputFileNameMode.Aes;
        set
        {
            if (value)
                SelectedOutputFileNameMode = OutputFileNameMode.Aes;
        }
    }

    public bool IsSha256OutputFileNameMode
    {
        get => SelectedOutputFileNameMode == OutputFileNameMode.Sha256;
        set
        {
            if (value)
                SelectedOutputFileNameMode = OutputFileNameMode.Sha256;
        }
    }

    public bool IsMd5OutputFileNameMode
    {
        get => SelectedOutputFileNameMode == OutputFileNameMode.Md5;
        set
        {
            if (value)
                SelectedOutputFileNameMode = OutputFileNameMode.Md5;
        }
    }

    private OutputFileNameMode EffectiveOutputFileNameMode =>
        EncryptFileNames
            ? SelectedOutputFileNameMode
            : OutputFileNameMode.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolderModePerFile))]
    [NotifyPropertyChangedFor(nameof(EstimatedOutputName))]
    private bool _isFolderModeZip = true;

    [ObservableProperty]
    private bool _overwriteExisting;

    public bool IsFolderModePerFile
    {
        get => !IsFolderModeZip;
        set => IsFolderModeZip = !value;
    }

    // ── Output preview ────────────────────────────────────────────
    public string EstimatedOutputName
    {
        get
        {
            if (string.IsNullOrEmpty(SourcePath)) return "";

            if (IsMultipleFileSource)
                return F("MultipleFilesOutput", _sourcePaths.Count);

            var sourceName = Path.GetFileName(
                SourcePath.TrimEnd(Path.DirectorySeparatorChar));

            if (IsFolderSource && !IsFolderModeZip)
            {
                var folderName = sourceName + "_encrypted";
                return EncryptFileNames
                    ? F("EncryptedFileNamesSuffix", folderName)
                    : folderName;
            }

            return EncryptFileNames
                ? SelectedOutputFileNameMode switch
                {
                    OutputFileNameMode.Sha256 => L("Sha256FileNamePlaceholder"),
                    OutputFileNameMode.Md5 => L("Md5FileNamePlaceholder"),
                    _ => IsFileSource
                        ? L("EncryptedFileNamePlaceholder")
                        : L("EncryptedFolderNamePlaceholder")
                }
                : sourceName + ".safe";
        }
    }

    // ── Operation state ───────────────────────────────────────────
    [ObservableProperty]
    private bool _isEncrypting;

    [ObservableProperty] private bool _isStatusBarVisible;
    [ObservableProperty] private string _statusAction = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDetails))]
    private string _statusCurrentFile = "";

    [ObservableProperty]
    private string _statusCurrentFilePath = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusProgressPercent))]
    private double _statusProgress;

    public int StatusProgressPercent => StatusProgress >= 1
        ? 100
        : (int)Math.Floor(Math.Clamp(StatusProgress, 0, 1) * 100);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDetails))]
    private string _statusBytes = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDetails))]
    [NotifyPropertyChangedFor(nameof(StatusTiming))]
    private string _statusSpeed = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusTiming))]
    private string _statusEta = "";

    public string StatusDetails => StatusBytes;

    public string StatusTiming => string.Join(
        " \u2022 ",
        new[] { StatusSpeed, StatusEta }
            .Where(s => !string.IsNullOrEmpty(s)));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = "";

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    [ObservableProperty] private bool _hasError;

    // ── Commands ──────────────────────────────────────────────────
    public IAsyncRelayCommand BrowseSourceCommand { get; }
    public IAsyncRelayCommand BrowseSourceFolderCommand { get; }
    public IAsyncRelayCommand BrowseExcludedFoldersCommand { get; }
    public IRelayCommand ClearExcludedFoldersCommand { get; }
    public IAsyncRelayCommand EncryptCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public IRelayCommand OpenOutputFolderCommand { get; }
    public IRelayCommand CloseStatusCommand { get; }
    public IRelayCommand TogglePasswordVisibilityCommand { get; }
    public IRelayCommand ToggleConfirmPasswordVisibilityCommand { get; }

    public EncryptViewModel(
        IFilePickerService filePicker,
        IErrorDialogService errorDialog,
        SettingsService settingsService,
        Microsoft.Extensions.Logging.ILogger<FileEncryptor> fileEncryptorLogger)
    {
        _filePicker = filePicker;
        _errorDialog = errorDialog;
        _settingsService = settingsService;
        _fileEncryptorLogger = fileEncryptorLogger;
        _settings = _settingsService.Load();
        _encryptFileNames = false;

        BrowseSourceCommand = new AsyncRelayCommand(BrowseFileAsync);
        BrowseSourceFolderCommand = new AsyncRelayCommand(BrowseFolderAsync);
        BrowseExcludedFoldersCommand = new AsyncRelayCommand(BrowseExcludedFoldersAsync);
        ClearExcludedFoldersCommand = new RelayCommand(ClearExcludedFolders);
        EncryptCommand = new AsyncRelayCommand(EncryptAsync);
        ResetCommand = new RelayCommand(Reset);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        CloseStatusCommand = new RelayCommand(CloseOrCancelStatus);
        TogglePasswordVisibilityCommand = new RelayCommand(
            () => ShowPassword = !ShowPassword);
        ToggleConfirmPasswordVisibilityCommand = new RelayCommand(
            () => ShowConfirmPassword = !ShowConfirmPassword);
    }

    public void RefreshSettings()
    {
        if (IsEncrypting)
            return;

        _settings = _settingsService.Load();
        OnPropertyChanged(nameof(IsPasswordConfirmationRequired));
    }

    private async Task BrowseFileAsync()
    {
        var paths = await _filePicker.PickFilesAsync(L("SelectSourceFiles"));

        if (paths.Count > 0)
            SelectSourceFiles(paths);
    }

    public void SelectSourceFiles(IEnumerable<string> paths)
    {
        var files = paths.Where(File.Exists).Distinct(PathComparer).ToArray();
        if (files.Length == 0)
            return;

        ClearExcludedFolders();
        IsFileSource = true;
        _sourcePaths = files;
        SourcePath = files[0];
        OnPropertyChanged(nameof(IsMultipleFileSource));
        OnPropertyChanged(nameof(EstimatedOutputName));
        OnPropertyChanged(nameof(SourceFilesTooltip));
        OnPropertyChanged(nameof(SourcePathsTooltip));
    }

    public void SelectSourceFolder(string path)
    {
        if (!Directory.Exists(path))
            return;

        ClearExcludedFolders();
        IsFileSource = false;
        _sourcePaths = [path];
        SourcePath = path;
        OnPropertyChanged(nameof(SourceFilesTooltip));
        OnPropertyChanged(nameof(SourcePathsTooltip));
    }

    private async Task BrowseFolderAsync()
    {
        var path = await _filePicker.PickFolderAsync(L("SelectSourceFolder"));

        if (path is not null)
        {
            SelectSourceFolder(path);
        }
    }

    private async Task BrowseExcludedFoldersAsync()
    {
        if (!IsFolderSource || !Directory.Exists(SourcePath))
            return;
        var paths = await _filePicker.PickFoldersAsync(L("SelectExcludedFolders"));
        if (paths.Count == 0)
            return;
        try
        {
            var merged = ExcludedFolderPathValidator.MergeAndValidate(
                paths, [SourcePath], ExcludedFolders.Select(item => item.FullPath));
            ReplaceExcludedFolders(merged);
        }
        catch (Exception ex)
        {
            var message = ex is ExcludedFolderValidationException validation
                ? L(validation.ResourceKey)
                : ex.Message;
            await _errorDialog.ShowErrorAsync(message, L("CannotAddExcludedFolders"));
        }
    }

    private void RemoveExcludedFolder(ExcludedFolderItem item)
    {
        if (IsEncrypting || !ExcludedFolders.Remove(item))
            return;
        RefreshExcludedState();
    }

    private void ClearExcludedFolders()
    {
        if (ExcludedFolders.Count == 0)
            return;
        ExcludedFolders.Clear();
        RefreshExcludedState();
    }

    private void ReplaceExcludedFolders(IEnumerable<string> paths)
    {
        ExcludedFolders.Clear();
        foreach (var path in paths)
            ExcludedFolders.Add(new ExcludedFolderItem(path, RemoveExcludedFolder));
        RefreshExcludedState();
    }

    private void RefreshExcludedState()
    {
        OnPropertyChanged(nameof(HasExcludedFolders));
        _sourceInfoCts?.Cancel();
        _sourceInfoCts?.Dispose();
        _sourceInfoCts = new CancellationTokenSource();
        _ = RefreshSourceInfoAsync(SourcePath, _sourceInfoCts.Token);
    }

    private async Task EncryptAsync()
    {
        if (IsEncrypting)
            return;

        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            await ShowSubmitErrorAsync(L("SourceRequired"));
            return;
        }
        if (string.IsNullOrEmpty(Password))
        {
            await ShowSubmitErrorAsync(L("PasswordRequired"));
            return;
        }
        if (IsPasswordConfirmationRequired && Password != ConfirmPassword)
        {
            await ShowSubmitErrorAsync(L("PasswordsDoNotMatch"));
            return;
        }

        HasError = false;
        StatusMessage = "";
        Logger.Information(
            "Encryption started for {SourceCount} source(s) using {SourceType} mode {FolderMode}",
            IsFileSource ? _sourcePaths.Count : 1,
            IsFileSource ? "File" : "Folder",
            IsFileSource ? "File" : IsFolderModeZip ? "Zip" : "PerFile");
        IsEncrypting = true;
        IsStatusBarVisible = true;

        var cts = new CancellationTokenSource();
        _activeCts = cts;
        StatusAction = L("Encrypting");
        StatusCurrentFile = IsMultipleFileSource
            ? F("FilesSelected", _sourcePaths.Count)
            : Path.GetFileName(SourcePath.TrimEnd(Path.DirectorySeparatorChar));
        StatusCurrentFilePath = IsMultipleFileSource
            ? FormatNumberedLines(_sourcePaths)
            : SourcePath;
        StatusProgress = 0;
        StatusBytes = "";
        StatusSpeed = "";
        StatusEta = "";

        var sourceMetrics = await Task.Run(() =>
        {
            var size = TryGetSourceSize();
            var fileCount = !IsFileSource && !IsFolderModeZip
                ? TryGetSourceFileCount()
                : 0;
            return (Size: size, FileCount: fileCount);
        });
        long? sourceSize = sourceMetrics.Size;
        long completedBatchBytes = 0;
        long currentBatchFileBytes = 0;
        var perFileTotal = sourceMetrics.FileCount;
        var succeededFileCount = 0;
        var failedFileCount = 0;
        var collisionFileCount = 0;
        var otherFailedFileCount = 0;
        var etaEstimator = new EtaEstimator();
        var completedPerFileBytes = 0L;
        var completedPerFilePaths = new HashSet<string>(PathComparer);

        if (sourceSize is > 0)
            StatusEta = L("CalculatingEta");

        var progress = new Progress<double>(p =>
        {
            if (perFileTotal > 0)
                return;

            var overallProgress = IsMultipleFileSource && sourceSize is > 0
                ? Math.Clamp((completedBatchBytes + currentBatchFileBytes * p) /
                    (double)sourceSize.Value, 0, 1)
                : p;
            StatusProgress = overallProgress;
            if (sourceSize is > 0)
            {
                var bytesProcessed = (long)(sourceSize.Value * overallProgress);
                var estimate = etaEstimator.Update(bytesProcessed, sourceSize.Value);
                StatusBytes = IsMultipleFileSource
                    ? F(
                        "BatchSummary",
                        succeededFileCount,
                        failedFileCount,
                        Math.Max(
                            0,
                            _sourcePaths.Count - succeededFileCount - failedFileCount))
                    : FormatBytes(bytesProcessed) + " / " + FormatBytes(sourceSize.Value);
                StatusSpeed = estimate.BytesPerSecond is > 0
                    ? FormatSpeed(estimate.BytesPerSecond.Value)
                    : "";
                StatusEta = !IsFileSource && IsFolderModeZip &&
                            overallProgress >= 0.99 && overallProgress < 1
                    ? L("FinalizingEncryption")
                    : estimate.RemainingSeconds is { } remaining
                        ? F("EstimatedTimeRemaining", FormatEta(remaining))
                        : L("CalculatingEta");
            }
        });

        var perFileProgress = new Progress<PerFileProgress>(p =>
        {
            switch (p.Result)
            {
                case PerFileResult.Succeeded:
                    succeededFileCount++;
                    break;
                case PerFileResult.DestinationExists:
                    collisionFileCount++;
                    failedFileCount++;
                    break;
                case PerFileResult.Failed:
                    otherFailedFileCount++;
                    failedFileCount++;
                    break;
            }

            StatusCurrentFile = Path.GetFileName(p.SourceFilePath);
            StatusCurrentFilePath = p.SourceFilePath;
            var completed = succeededFileCount + failedFileCount;
            StatusProgress = perFileTotal > 0
                ? Math.Clamp(
                    (completed + (p.Result == PerFileResult.InProgress ? p.Progress : 0)) /
                    perFileTotal,
                    0,
                    1)
                : p.Progress;
            StatusBytes = F(
                "BatchSummary",
                succeededFileCount,
                failedFileCount,
                Math.Max(0, perFileTotal - completed));

            if (sourceSize is > 0)
            {
                var fileSize = TryGetFileSize(p.SourceFilePath);
                if (p.Result != PerFileResult.InProgress &&
                    completedPerFilePaths.Add(p.SourceFilePath))
                    completedPerFileBytes += fileSize;

                var processedBytes = p.Result == PerFileResult.InProgress
                    ? completedPerFileBytes + (long)(fileSize * p.Progress)
                    : completedPerFileBytes;
                var estimate = etaEstimator.Update(
                    processedBytes,
                    sourceSize.Value,
                    includeSample: p.Result == PerFileResult.InProgress);
                StatusSpeed = estimate.BytesPerSecond is > 0
                    ? FormatSpeed(estimate.BytesPerSecond.Value)
                    : "";
                StatusEta = estimate.RemainingSeconds is { } remaining
                    ? F("EstimatedTimeRemaining", FormatEta(remaining))
                    : L("CalculatingEta");
            }
        });

        byte[]? passwordBytes = null;
        try
        {
            passwordBytes = Encoding.UTF8.GetBytes(Password);
            var encryptor = new FileEncryptor(
                consumerThreads: _settings.MaxThreads,
                progress: progress,
                settings: _settings,
                perFileProgress: perFileProgress,
                logger: _fileEncryptorLogger);

            var chunkSizeBytes = _settings.GetChunkSizeBytes();
            var kdfParams = _settings.GetKdfParameters();
            var outputDirectory = Path.GetFullPath(_settings.DefaultOutputPath);
            Directory.CreateDirectory(outputDirectory);

            string actualOutputPath;
            if (IsFileSource)
            {
                var failures = new List<string>();
                actualOutputPath = outputDirectory;
                foreach (var sourceFile in _sourcePaths)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    StatusCurrentFile = Path.GetFileName(sourceFile);
                    StatusCurrentFilePath = sourceFile;
                    currentBatchFileBytes = new FileInfo(sourceFile).Length;
                    var destPath = Path.Combine(outputDirectory, Path.GetFileName(sourceFile) + ".safe");
                    try
                    {
                        actualOutputPath = await encryptor.EncryptFileAsync(
                            sourceFile, destPath, passwordBytes,
                            chunkSizeBytes, kdfParams,
                            outputFileNameMode: EffectiveOutputFileNameMode,
                            cancellationToken: cts.Token,
                            overwriteExisting: OverwriteExisting);
                        succeededFileCount++;
                        Logger.Information("Encrypted batch source {SourcePath} to {OutputPath}", sourceFile, actualOutputPath);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (IsMultipleFileSource)
                    {
                        failedFileCount++;
                        failures.Add($"{Path.GetFileName(sourceFile)}: {ex.Message}");
                        Logger.Error(ex, "Failed to encrypt batch source {SourcePath}", sourceFile);
                    }
                    finally
                    {
                        completedBatchBytes += currentBatchFileBytes;
                        currentBatchFileBytes = 0;
                    }
                }

                if (failures.Count > 0)
                    throw new IOException(string.Join(Environment.NewLine, failures));
            }
            else if (IsFolderModeZip)
            {
                var src = SourcePath.TrimEnd(Path.DirectorySeparatorChar);
                var destPath = Path.Combine(
                    outputDirectory,
                    Path.GetFileName(src) + ".safe");
                actualOutputPath = await encryptor.EncryptFolderZipAsync(
                    SourcePath, destPath, passwordBytes,
                    chunkSizeBytes, kdfParams,
                    outputFileNameMode: EffectiveOutputFileNameMode,
                    cancellationToken: cts.Token,
                    overwriteExisting: OverwriteExisting,
                    excludedFolderPaths: ExcludedFolders.Select(item => item.FullPath).ToArray());
            }
            else
            {
                var src = SourcePath.TrimEnd(Path.DirectorySeparatorChar);
                var destFolder = Path.Combine(
                    outputDirectory,
                    Path.GetFileName(src) + "_encrypted");
                await encryptor.EncryptFolderPerFileAsync(
                    SourcePath, destFolder, passwordBytes,
                    chunkSizeBytes, kdfParams,
                    outputFileNameMode: EffectiveOutputFileNameMode,
                    cancellationToken: cts.Token,
                    overwriteExisting: OverwriteExisting,
                    excludedFolderPaths: ExcludedFolders.Select(item => item.FullPath).ToArray());
                actualOutputPath = destFolder;
            }

            StatusProgress = 1.0;
            StatusAction = L("EncryptionCompleted");
            StatusCurrentFile = IsMultipleFileSource
                ? F("FilesEncrypted", _sourcePaths.Count)
                : Path.GetFileName(actualOutputPath);
            StatusCurrentFilePath = IsMultipleFileSource
                ? outputDirectory
                : actualOutputPath;
            StatusEta = "";
            if (IsMultipleFileSource || perFileTotal > 0)
            {
                StatusBytes = F(
                    "SuccessFailureSummary",
                    succeededFileCount,
                    failedFileCount);
                StatusSpeed = "";
            }
            StatusMessage = IsMultipleFileSource
                ? F("MultipleEncryptionSucceeded", _sourcePaths.Count)
                : F("EncryptionSucceeded", Path.GetFileName(actualOutputPath));
            HasError = false;
            Logger.Information(
                "Encryption completed successfully: {OutputPath}",
                actualOutputPath);
        }
        catch (OperationCanceledException)
        {
            StatusAction = L("EncryptionCancelled");
            StatusEta = "";
            StatusMessage = L("OperationCancelled");
            HasError = false;
            Logger.Warning("Encryption cancelled for {SourcePath}", SourcePath);
        }
        catch (InvalidOperationException ex)
        {
            StatusAction = L("EncryptionFailed");
            StatusEta = "";
            Logger.Error(
                ex,
                "Encryption failed for {SourcePath}",
                SourcePath);
            await ShowSubmitErrorAsync(
                F("CorruptData", ex.Message),
                L("EncryptionFailed"));
        }
        catch (Exception ex)
        {
            StatusAction = succeededFileCount > 0 && failedFileCount > 0
                ? L("EncryptionCompletedWithErrors")
                : L("EncryptionFailed");
            StatusEta = "";
            if (IsMultipleFileSource || perFileTotal > 0)
            {
                StatusBytes = F(
                    "SuccessFailureSummary",
                    succeededFileCount,
                    failedFileCount);
                StatusSpeed = "";
            }
            Logger.Error(
                ex,
                "Encryption failed for {SourcePath}",
                SourcePath);
            if (perFileTotal > 0 && failedFileCount > 0)
            {
                var summaries = new List<string>();
                if (collisionFileCount > 0)
                    summaries.Add(F("PerFileCollisionFailures", collisionFileCount));
                if (otherFailedFileCount > 0)
                    summaries.Add(F("PerFileOtherFailures", otherFailedFileCount));
                await ShowSubmitErrorAsync(
                    string.Join(Environment.NewLine, summaries),
                    L("EncryptionCompletedWithErrors"));
            }
            else
            {
                await ShowSubmitErrorAsync(ex.Message, L("EncryptionFailed"));
            }
        }
        finally
        {
            if (passwordBytes is not null)
                CryptographicOperations.ZeroMemory(passwordBytes);
            IsEncrypting = false;
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }

    private async Task ShowSubmitErrorAsync(
        string message,
        string? title = null)
    {
        StatusMessage = "";
        HasError = false;
        await _errorDialog.ShowErrorAsync(message, title ?? L("CannotEncrypt"));
    }

    private void CloseOrCancelStatus()
    {
        if (IsEncrypting)
        {
            _activeCts?.Cancel();
            return;
        }

        IsStatusBarVisible = false;
    }

    private void Reset()
    {
        SourcePath = "";
        _sourcePaths = [];
        ClearExcludedFolders();
        Password = "";
        ConfirmPassword = "";
        StatusMessage = "";
        HasError = false;
        IsFileSource = true;
        EncryptFileNames = false;
        SelectedOutputFileNameMode = OutputFileNameMode.Md5;
        OverwriteExisting = false;
        IsFolderModeZip = true;
    }

    private void OpenOutputFolder()
    {
        if (string.IsNullOrWhiteSpace(_settings.DefaultOutputPath))
            return;

        try
        {
            Directory.CreateDirectory(_settings.DefaultOutputPath);
            _filePicker.OpenFolder(_settings.DefaultOutputPath);
            Logger.Debug(
                "Opened encrypted output directory {OutputDirectory}",
                _settings.DefaultOutputPath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open encrypted output directory");
            StatusMessage = $"{L("CannotOpenOutput")}: {ex.Message}";
            HasError = true;
        }
    }

    private long? TryGetSourceSize()
    {
        try
        {
            if (IsFileSource && File.Exists(SourcePath))
                return _sourcePaths.Sum(path => new FileInfo(path).Length);
            if (!IsFileSource && Directory.Exists(SourcePath))
                return EnumerateRegularFiles(
                        new DirectoryInfo(SourcePath),
                        ExcludedFolders.Select(item => item.FullPath).ToArray())
                    .Sum(file => file.Length);
        }
        catch { }
        return null;
    }

    private int TryGetSourceFileCount()
    {
        try
        {
            return Directory.Exists(SourcePath)
                ? EnumerateRegularFiles(
                    new DirectoryInfo(SourcePath),
                    ExcludedFolders.Select(item => item.FullPath).ToArray()).Count()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static long TryGetFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static IEnumerable<FileInfo> EnumerateRegularFiles(
        DirectoryInfo directory,
        IReadOnlyCollection<string>? excludedFolderPaths = null)
    {
        foreach (var file in directory.EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) == 0)
                yield return file;
        }

        foreach (var subdirectory in directory.EnumerateDirectories())
        {
            if ((subdirectory.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            if (excludedFolderPaths?.Any(path =>
                    ExcludedFolderPathValidator.IsSameOrDescendant(
                        path, subdirectory.FullName)) == true)
                continue;

            foreach (var file in EnumerateRegularFiles(
                         subdirectory, excludedFolderPaths))
                yield return file;
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576     => $"{bytes / 1_048_576.0:F1} MB",
            >= 1_024         => $"{bytes / 1_024.0:F1} KB",
            _                => $"{bytes} B"
        };

    private static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond switch
        {
            >= 1_000_000 => $"{bytesPerSecond / 1_000_000:F0} MB/s",
            >= 1_000 => $"{bytesPerSecond / 1_000:F0} KB/s",
            _ => $"{bytesPerSecond:F0} B/s"
        };

    private static string FormatEta(double seconds)
    {
        if (seconds < 0) return "";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private sealed class EtaEstimator
    {
        private static readonly TimeSpan MinimumSampleInterval =
            TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan WarmupDuration =
            TimeSpan.FromSeconds(1.5);
        private const double SmoothingFactor = 0.2;

        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private TimeSpan _lastSampleTime;
        private long _lastProcessedBytes;
        private double? _smoothedBytesPerSecond;
        private int _sampleCount;

        public EtaEstimate Update(
            long processedBytes,
            long totalBytes,
            bool includeSample = true)
        {
            processedBytes = Math.Clamp(processedBytes, 0, totalBytes);
            var now = _stopwatch.Elapsed;
            var elapsed = now - _lastSampleTime;
            var deltaBytes = processedBytes - _lastProcessedBytes;

            if (!includeSample)
            {
                _lastSampleTime = now;
                _lastProcessedBytes = processedBytes;
            }
            else if (elapsed >= MinimumSampleInterval && deltaBytes > 0)
            {
                var instantaneousSpeed = deltaBytes / elapsed.TotalSeconds;
                _smoothedBytesPerSecond = _smoothedBytesPerSecond is { } previous
                    ? SmoothingFactor * instantaneousSpeed +
                      (1 - SmoothingFactor) * previous
                    : instantaneousSpeed;
                _sampleCount++;
                _lastSampleTime = now;
                _lastProcessedBytes = processedBytes;
            }

            var isReady = _stopwatch.Elapsed >= WarmupDuration &&
                          _sampleCount >= 2 &&
                          _smoothedBytesPerSecond is > 0;
            double? remainingSeconds = isReady
                ? Math.Max(
                    0,
                    (totalBytes - processedBytes) /
                    _smoothedBytesPerSecond!.Value)
                : null;

            return new EtaEstimate(_smoothedBytesPerSecond, remainingSeconds);
        }
    }

    private readonly record struct EtaEstimate(
        double? BytesPerSecond,
        double? RemainingSeconds);

    private static string L(string key) => LocalizationService.Instance.Get(key);
    private static string F(string key, params object?[] args) =>
        LocalizationService.Instance.Format(key, args);

    private static string FormatNumberedLines(IEnumerable<string> values) =>
        string.Join(
            Environment.NewLine,
            values.Select((value, index) => $"{index + 1}. {value}"));

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
