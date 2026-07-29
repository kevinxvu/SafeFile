using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeFile.Core.IO;
using SafeFile.Core.Models;
using SafeFile.Core.Services;
using SafeFile.Services;

namespace SafeFile.ViewModels;

public sealed partial class EncryptViewModel : ViewModelBase
{
    private readonly IFilePickerService _filePicker;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _activeCts;

    // ── Source ────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedOutputName))]
    [NotifyPropertyChangedFor(nameof(IsFolderSource))]
    [NotifyPropertyChangedFor(nameof(IsSingleVaultOutput))]
    private bool _isFileSource = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedOutputName))]
    private string _sourcePath = "";

    public bool IsFolderSource => !IsFileSource;

    partial void OnIsFileSourceChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFolderSource));
        SourcePath = "";
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
    public bool IsPasswordConfirmationRequired { get; }

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
            < 30 => "Yếu",
            < 60 => "Trung bình",
            < 80 => "Mạnh",
            _ => "Rất mạnh"
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
    [NotifyPropertyChangedFor(nameof(IsFolderModePerFile))]
    [NotifyPropertyChangedFor(nameof(EstimatedOutputName))]
    [NotifyPropertyChangedFor(nameof(IsSingleVaultOutput))]
    private bool _isFolderModeZip = true;

    [ObservableProperty]
    private bool _overwriteExisting;

    public bool IsSingleVaultOutput => IsFileSource || IsFolderModeZip;

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

            var sourceName = Path.GetFileName(
                SourcePath.TrimEnd(Path.DirectorySeparatorChar));

            if (IsFolderSource && !IsFolderModeZip)
            {
                var folderName = sourceName + "_encrypted";
                return EncryptFileNames
                    ? $"{folderName} (tên các tập tin sẽ được mã hoá)"
                    : folderName;
            }

            return EncryptFileNames
                ? IsFileSource
                    ? "[Tên tập tin đã mã hoá].safe"
                    : "[Tên thư mục đã mã hoá].safe"
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

    [ObservableProperty] private double _statusProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDetails))]
    private string _statusBytes = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDetails))]
    private string _statusSpeed = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDetails))]
    private string _statusEta = "";

    public string StatusDetails => string.Join(
        " \u2022 ",
        new[] { StatusBytes, StatusSpeed, StatusEta }
            .Where(s => !string.IsNullOrEmpty(s)));

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _hasError;

    // ── Commands ──────────────────────────────────────────────────
    public IAsyncRelayCommand BrowseSourceCommand { get; }
    public IAsyncRelayCommand EncryptCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public IRelayCommand OpenOutputFolderCommand { get; }
    public IRelayCommand CloseStatusCommand { get; }
    public IRelayCommand TogglePasswordVisibilityCommand { get; }
    public IRelayCommand ToggleConfirmPasswordVisibilityCommand { get; }

    public EncryptViewModel(IFilePickerService filePicker)
    {
        _filePicker = filePicker;
        var settingsService = new SettingsService();
        _settings = settingsService.Load();
        _encryptFileNames = false;
        IsPasswordConfirmationRequired = _settings.ConfirmPasswordToggle;

        BrowseSourceCommand = new AsyncRelayCommand(BrowseAsync);
        EncryptCommand = new AsyncRelayCommand(EncryptAsync);
        ResetCommand = new RelayCommand(Reset);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        CloseStatusCommand = new RelayCommand(CloseOrCancelStatus);
        TogglePasswordVisibilityCommand = new RelayCommand(
            () => ShowPassword = !ShowPassword);
        ToggleConfirmPasswordVisibilityCommand = new RelayCommand(
            () => ShowConfirmPassword = !ShowConfirmPassword);
    }

    private async Task BrowseAsync()
    {
        string? path = IsFileSource
            ? await _filePicker.PickFileAsync("Chọn tập tin nguồn")
            : await _filePicker.PickFolderAsync("Chọn thư mục nguồn");

        if (path is not null)
            SourcePath = path;
    }

    private async Task EncryptAsync()
    {
        if (IsEncrypting)
            return;

        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            StatusMessage = "Vui lòng chọn tập tin hoặc thư mục nguồn.";
            HasError = true;
            return;
        }
        if (string.IsNullOrEmpty(Password))
        {
            StatusMessage = "Vui lòng nhập mật khẩu.";
            HasError = true;
            return;
        }
        if (IsPasswordConfirmationRequired && Password != ConfirmPassword)
        {
            StatusMessage = "Mật khẩu xác nhận không khớp.";
            HasError = true;
            return;
        }

        HasError = false;
        StatusMessage = "";
        IsEncrypting = true;
        IsStatusBarVisible = true;

        var cts = new CancellationTokenSource();
        _activeCts = cts;
        StatusAction = "Đang mã hoá";
        StatusCurrentFile = Path.GetFileName(SourcePath.TrimEnd(Path.DirectorySeparatorChar));
        StatusProgress = 0;
        StatusBytes = "";
        StatusSpeed = "";
        StatusEta = "";

        var startTime = DateTime.UtcNow;
        long? sourceSize = TryGetSourceSize();

        var progress = new Progress<double>(p =>
        {
            StatusProgress = p;
            var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
            if (elapsed > 0.5 && sourceSize is > 0)
            {
                var bytesProcessed = (long)(sourceSize.Value * p);
                var speed = bytesProcessed / elapsed;
                StatusBytes = FormatBytes(bytesProcessed) + " / " + FormatBytes(sourceSize.Value);
                StatusSpeed = FormatSpeed(speed);
                if (p > 0.001)
                {
                    var remaining = elapsed / p - elapsed;
                    StatusEta = "ETA: " + FormatEta(remaining);
                }
            }
        });

        var perFileProgress = new Progress<PerFileProgress>(p =>
        {
            StatusCurrentFile = Path.GetFileName(p.SourceFilePath);
            StatusProgress = p.Progress;
        });

        byte[]? passwordBytes = null;
        try
        {
            passwordBytes = Encoding.UTF8.GetBytes(Password);
            var encryptor = new FileEncryptor(
                consumerThreads: _settings.MaxThreads,
                progress: progress,
                settings: _settings,
                perFileProgress: perFileProgress);

            var chunkSizeBytes = _settings.GetChunkSizeBytes();
            var kdfParams = _settings.GetKdfParameters();
            var outputDirectory = Path.GetFullPath(_settings.DefaultOutputPath);
            Directory.CreateDirectory(outputDirectory);

            string actualOutputPath;
            if (IsFileSource)
            {
                var destPath = Path.Combine(
                    outputDirectory,
                    Path.GetFileName(SourcePath) + ".safe");
                actualOutputPath = await encryptor.EncryptFileAsync(
                    SourcePath, destPath, passwordBytes,
                    chunkSizeBytes, kdfParams,
                    encryptFileName: EncryptFileNames,
                    cancellationToken: cts.Token,
                    overwriteExisting: OverwriteExisting);
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
                    encryptFileName: EncryptFileNames,
                    cancellationToken: cts.Token,
                    overwriteExisting: OverwriteExisting);
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
                    encryptFileNames: EncryptFileNames,
                    cancellationToken: cts.Token);
                actualOutputPath = destFolder;
            }

            StatusProgress = 1.0;
            StatusAction = "Mã hoá hoàn tất";
            StatusCurrentFile = Path.GetFileName(actualOutputPath);
            StatusEta = "";
            StatusMessage = $"✓ Mã hoá thành công: {Path.GetFileName(actualOutputPath)}";
            HasError = false;
        }
        catch (OperationCanceledException)
        {
            StatusAction = "Đã huỷ mã hoá";
            StatusEta = "";
            StatusMessage = "Đã huỷ thao tác.";
            HasError = false;
        }
        catch (InvalidOperationException ex)
        {
            StatusAction = "Mã hoá thất bại";
            StatusEta = "";
            StatusMessage = $"Sai mật khẩu hoặc dữ liệu bị hỏng: {ex.Message}";
            HasError = true;
        }
        catch (Exception ex)
        {
            StatusAction = "Mã hoá thất bại";
            StatusEta = "";
            StatusMessage = $"Lỗi: {ex.Message}";
            HasError = true;
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
        Password = "";
        ConfirmPassword = "";
        StatusMessage = "";
        HasError = false;
        IsFileSource = true;
        EncryptFileNames = false;
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
        }
        catch (Exception ex)
        {
            StatusMessage = $"Không thể mở thư mục đầu ra: {ex.Message}";
            HasError = true;
        }
    }

    private long? TryGetSourceSize()
    {
        try
        {
            if (IsFileSource && File.Exists(SourcePath))
                return new FileInfo(SourcePath).Length;
            if (!IsFileSource && Directory.Exists(SourcePath))
                return new DirectoryInfo(SourcePath)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
        }
        catch { }
        return null;
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
}
