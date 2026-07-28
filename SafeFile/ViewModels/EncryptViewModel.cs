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
    private readonly MainWindowViewModel _mainVm;
    private readonly AppSettings _settings;

    // ── Source ────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedOutputName))]
    [NotifyPropertyChangedFor(nameof(IsFolderSource))]
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

    // ── Options ───────────────────────────────────────────────────
    [ObservableProperty] private int _chunkSizeMb;
    [ObservableProperty] private int _maxThreads;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolderModePerFile))]
    private bool _isFolderModeZip = true;

    public bool IsFolderModePerFile
    {
        get => !IsFolderModeZip;
        set => IsFolderModeZip = !value;
    }

    public int[] ChunkSizeOptions { get; } = [1, 2, 4, 8, 16];
    public int MaxThreadsLimit { get; } = Math.Max(1, Environment.ProcessorCount);

    // ── Output preview ────────────────────────────────────────────
    public string EstimatedOutputName
    {
        get
        {
            if (string.IsNullOrEmpty(SourcePath)) return "";
            return Path.GetFileName(SourcePath.TrimEnd(Path.DirectorySeparatorChar)) + ".safe";
        }
    }

    // ── Operation state ───────────────────────────────────────────
    [ObservableProperty]
    private bool _isEncrypting;

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _hasError;

    // ── Commands ──────────────────────────────────────────────────
    public IAsyncRelayCommand BrowseSourceCommand { get; }
    public IAsyncRelayCommand EncryptCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public IRelayCommand OpenOutputFolderCommand { get; }

    public EncryptViewModel(IFilePickerService filePicker, MainWindowViewModel mainVm)
    {
        _filePicker = filePicker;
        _mainVm = mainVm;
        var settingsService = new SettingsService();
        _settings = settingsService.Load();
        _maxThreads = Math.Clamp(_settings.MaxThreads, 1, MaxThreadsLimit);
        _chunkSizeMb = _settings.DefaultChunkSizeMb;

        BrowseSourceCommand = new AsyncRelayCommand(BrowseAsync);
        EncryptCommand = new AsyncRelayCommand(EncryptAsync);
        ResetCommand = new RelayCommand(Reset);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
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
        if (Password != ConfirmPassword)
        {
            StatusMessage = "Mật khẩu xác nhận không khớp.";
            HasError = true;
            return;
        }

        HasError = false;
        StatusMessage = "";
        IsEncrypting = true;

        var cts = new CancellationTokenSource();
        _mainVm.SetActiveCts(cts);
        _mainVm.IsOperationActive = true;
        _mainVm.StatusCurrentFile = Path.GetFileName(SourcePath.TrimEnd(Path.DirectorySeparatorChar));
        _mainVm.StatusProgress = 0;
        _mainVm.StatusSpeed = "";
        _mainVm.StatusEta = "";

        var startTime = DateTime.UtcNow;
        long? sourceSize = TryGetSourceSize();

        var progress = new Progress<double>(p =>
        {
            _mainVm.StatusProgress = p;
            var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
            if (elapsed > 0.5 && sourceSize is > 0)
            {
                var bytesProcessed = sourceSize.Value * p;
                var speed = bytesProcessed / elapsed;
                _mainVm.StatusSpeed = FormatSpeed(speed);
                if (p > 0.001)
                {
                    var remaining = elapsed / p - elapsed;
                    _mainVm.StatusEta = "ETA: " + FormatEta(remaining);
                }
            }
        });

        var perFileProgress = new Progress<PerFileProgress>(p =>
        {
            _mainVm.StatusCurrentFile = Path.GetFileName(p.SourceFilePath);
            _mainVm.StatusProgress = p.Progress;
        });

        byte[]? passwordBytes = null;
        try
        {
            passwordBytes = Encoding.UTF8.GetBytes(Password);
            var encryptor = new FileEncryptor(
                consumerThreads: MaxThreads,
                progress: progress,
                settings: _settings,
                perFileProgress: perFileProgress);

            var chunkSizeBytes = ChunkSizeMb * 1_048_576;
            var kdfParams = _settings.GetKdfParameters();

            string actualOutputPath;
            if (IsFileSource)
            {
                var destPath = Path.Combine(
                    Path.GetDirectoryName(SourcePath)!,
                    Path.GetFileName(SourcePath) + ".safe");
                actualOutputPath = await encryptor.EncryptFileAsync(
                    SourcePath, destPath, passwordBytes,
                    chunkSizeBytes, kdfParams,
                    cancellationToken: cts.Token);
            }
            else if (IsFolderModeZip)
            {
                var src = SourcePath.TrimEnd(Path.DirectorySeparatorChar);
                var destPath = Path.Combine(
                    Path.GetDirectoryName(src)!,
                    Path.GetFileName(src) + ".safe");
                actualOutputPath = await encryptor.EncryptFolderZipAsync(
                    SourcePath, destPath, passwordBytes,
                    chunkSizeBytes, kdfParams,
                    cancellationToken: cts.Token);
            }
            else
            {
                var src = SourcePath.TrimEnd(Path.DirectorySeparatorChar);
                var destFolder = Path.Combine(
                    Path.GetDirectoryName(src)!,
                    Path.GetFileName(src) + "_encrypted");
                await encryptor.EncryptFolderPerFileAsync(
                    SourcePath, destFolder, passwordBytes,
                    chunkSizeBytes, kdfParams,
                    cancellationToken: cts.Token);
                actualOutputPath = destFolder;
            }

            _mainVm.StatusProgress = 1.0;
            StatusMessage = $"✓ Mã hoá thành công: {Path.GetFileName(actualOutputPath)}";
            HasError = false;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Đã huỷ thao tác.";
            HasError = false;
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Sai mật khẩu hoặc dữ liệu bị hỏng: {ex.Message}";
            HasError = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi: {ex.Message}";
            HasError = true;
        }
        finally
        {
            if (passwordBytes is not null)
                CryptographicOperations.ZeroMemory(passwordBytes);
            IsEncrypting = false;
            _mainVm.IsOperationActive = false;
            _mainVm.SetActiveCts(null);
        }
    }

    private void Reset()
    {
        SourcePath = "";
        Password = "";
        ConfirmPassword = "";
        StatusMessage = "";
        HasError = false;
        IsFileSource = true;
        ChunkSizeMb = _settings.DefaultChunkSizeMb;
        MaxThreads = Math.Clamp(_settings.MaxThreads, 1, MaxThreadsLimit);
        IsFolderModeZip = true;
    }

    private void OpenOutputFolder()
    {
        if (string.IsNullOrEmpty(SourcePath)) return;
        var folder = IsFileSource
            ? Path.GetDirectoryName(SourcePath)
            : Path.GetDirectoryName(SourcePath.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrEmpty(folder))
            _filePicker.OpenFolder(folder);
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
