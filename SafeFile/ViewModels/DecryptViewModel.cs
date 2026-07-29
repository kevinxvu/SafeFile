using System;
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

namespace SafeFile.ViewModels;

public sealed partial class DecryptViewModel : ViewModelBase
{
    private static readonly FilePickerFileType SafeFileType = new("SafeFile vault")
    {
        Patterns = ["*.safe"]
    };

    private readonly IFilePickerService _filePicker;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _activeCts;
    private VaultHeader? _header;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceName))]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private string _sourcePath = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private bool _isFolderSource;

    public string SourceName => string.IsNullOrWhiteSpace(SourcePath)
        ? "Chưa chọn file"
        : Path.GetFileName(Path.TrimEndingDirectorySeparator(SourcePath));

    public bool HasSource => !string.IsNullOrWhiteSpace(SourcePath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordChar))]
    private string _password = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasswordCheckSuccess))]
    private string _passwordCheckMessage = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasswordCheckSuccess))]
    private bool _isPasswordCheckError;

    public bool IsPasswordCheckSuccess =>
        !IsPasswordCheckError && !string.IsNullOrWhiteSpace(PasswordCheckMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordChar))]
    private bool _showPassword;

    public char PasswordChar => ShowPassword ? '\0' : '•';

    [ObservableProperty] private string _outputPath = "";
    [ObservableProperty] private bool _overwriteExisting;
    [ObservableProperty] private bool _openFolderWhenDone = true;

    [ObservableProperty] private string _formatText = "—";
    [ObservableProperty] private string _modeText = "—";
    [ObservableProperty] private string _chunkSizeText = "—";
    [ObservableProperty] private string _kdfText = "—";
    [ObservableProperty] private string _algorithmText = "AES-256-GCM";
    [ObservableProperty] private string _analysisMessage = "Chưa chọn file để phân tích";
    [ObservableProperty] private bool _isVaultValid;
    [ObservableProperty] private bool _hasVerifiedMetadata;
    [ObservableProperty] private string _originalFileNameText = "🔒 Đã mã hoá";
    [ObservableProperty] private string _vaultSizeText = "—";
    [ObservableProperty] private string _modifiedTimeText = "—";
    [ObservableProperty] private string _kdfMemoryText = "—";
    [ObservableProperty] private string _kdfParallelismText = "—";

    [ObservableProperty] private bool _isDecrypting;
    [ObservableProperty] private bool _isStatusBarVisible;
    [ObservableProperty] private string _statusAction = "";
    [ObservableProperty] private string _statusCurrentFile = "";
    [ObservableProperty] private double _statusProgress;
    [ObservableProperty] private string _statusDetails = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _hasError;

    public IAsyncRelayCommand BrowseSourceCommand { get; }
    public IAsyncRelayCommand BrowseSourceFolderCommand { get; }
    public IAsyncRelayCommand BrowseOutputCommand { get; }
    public IAsyncRelayCommand CheckPasswordCommand { get; }
    public IAsyncRelayCommand DecryptCommand { get; }
    public IRelayCommand TogglePasswordVisibilityCommand { get; }
    public IRelayCommand OpenOutputFolderCommand { get; }
    public IRelayCommand CloseStatusCommand { get; }

    public DecryptViewModel(IFilePickerService filePicker)
    {
        _filePicker = filePicker;
        _settings = new SettingsService().Load();
        _outputPath = GetDefaultOutputPath();

        BrowseSourceCommand = new AsyncRelayCommand(BrowseSourceAsync);
        BrowseSourceFolderCommand = new AsyncRelayCommand(BrowseSourceFolderAsync);
        BrowseOutputCommand = new AsyncRelayCommand(BrowseOutputAsync);
        CheckPasswordCommand = new AsyncRelayCommand(CheckPasswordAsync);
        DecryptCommand = new AsyncRelayCommand(DecryptAsync);
        TogglePasswordVisibilityCommand = new RelayCommand(() => ShowPassword = !ShowPassword);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        CloseStatusCommand = new RelayCommand(CloseOrCancelStatus);
    }

    partial void OnSourcePathChanged(string value)
    {
        AnalyzeSource();
        PasswordCheckMessage = "";
        StatusMessage = "";
        HasError = false;
    }

    partial void OnPasswordChanged(string value)
    {
        PasswordCheckMessage = "";
        IsPasswordCheckError = false;
        HasVerifiedMetadata = false;
        OriginalFileNameText = "🔒 Chưa xác thực";
        VaultSizeText = ModifiedTimeText = KdfMemoryText = KdfParallelismText = "—";
    }

    public void SelectDroppedSource(string path, bool isFolder)
    {
        IsFolderSource = isFolder;
        SourcePath = path;
    }

    private async Task BrowseSourceAsync()
    {
        var path = await _filePicker.PickFileAsync("Chọn file SafeFile", [SafeFileType]);
        if (path is null)
            return;

        IsFolderSource = false;
        SourcePath = path;
    }

    private async Task BrowseSourceFolderAsync()
    {
        var path = await _filePicker.PickFolderAsync("Chọn thư mục chứa các file .safe");
        if (path is null)
            return;

        IsFolderSource = true;
        SourcePath = path;
    }

    private async Task BrowseOutputAsync()
    {
        var path = await _filePicker.PickFolderAsync("Chọn thư mục đích");
        if (path is not null)
            OutputPath = path;
    }

    private void AnalyzeSource()
    {
        _header = null;
        IsVaultValid = false;
        HasVerifiedMetadata = false;
        OriginalFileNameText = "🔒 Chưa xác thực";
        VaultSizeText = ModifiedTimeText = KdfMemoryText = KdfParallelismText = "—";
        FormatText = ModeText = ChunkSizeText = KdfText = "—";
        AlgorithmText = "AES-256-GCM";

        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            AnalysisMessage = "Chưa chọn file để phân tích";
            return;
        }

        if (IsFolderSource)
        {
            if (!Directory.Exists(SourcePath))
            {
                AnalysisMessage = "Không tìm thấy thư mục đã chọn";
                return;
            }

            var count = Directory.EnumerateFiles(SourcePath, "*.safe", SearchOption.AllDirectories).Count();
            IsVaultValid = count > 0;
            FormatText = "SafeFile folder";
            ModeText = "Per-file";
            AnalysisMessage = count > 0
                ? $"Sẵn sàng • {count:N0} file .safe"
                : "Thư mục không chứa file .safe";
            return;
        }

        try
        {
            using var stream = File.Open(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _header = VaultHeader.ReadFrom(stream);
            IsVaultValid = true;
            FormatText = $"v{_header.Version} (AES-256)";
            ModeText = _header.Mode switch
            {
                VaultMode.File => "File",
                VaultMode.Zip => "Thư mục ZIP",
                VaultMode.PerFile => "Per-file",
                _ => _header.Mode.ToString()
            };
            ChunkSizeText = FormatBytes(_header.ChunkSize);
            KdfText = $"Argon2id • {_header.KdfParameters.Iterations:N0} vòng";
            AnalysisMessage = _header.EncryptFileNames
                ? "Hợp lệ • tên gốc sẽ được khôi phục"
                : "File vault hợp lệ";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnalysisMessage = $"Không thể đọc file: {ex.Message}";
        }
    }

    private async Task CheckPasswordAsync()
    {
        if (!ValidateInputs(requireOutput: false))
        {
            PasswordCheckMessage = StatusMessage;
            IsPasswordCheckError = true;
            StatusMessage = "";
            return;
        }

        if (IsFolderSource)
        {
            PasswordCheckMessage = "Mật khẩu sẽ được kiểm tra khi giải mã thư mục.";
            IsPasswordCheckError = false;
            return;
        }

        byte[]? passwordBytes = null;
        try
        {
            PasswordCheckMessage = "Đang kiểm tra...";
            IsPasswordCheckError = false;
            passwordBytes = Encoding.UTF8.GetBytes(Password);
            var metadata = await new FileEncryptor(
                consumerThreads: _settings.MaxThreads,
                settings: _settings).ReadVaultMetadataAsync(SourcePath, passwordBytes);

            OriginalFileNameText = metadata.OriginalFileName;
            VaultSizeText = FormatBytes(metadata.VaultSizeBytes);
            ModifiedTimeText = metadata.LastModifiedUtc.ToLocalTime()
                .ToString("dd/MM/yyyy HH:mm");
            FormatText = $"v{metadata.Version} (AES-256)";
            ModeText = metadata.Mode switch
            {
                VaultMode.File => "File",
                VaultMode.Zip => "Thư mục ZIP",
                VaultMode.PerFile => "Per-file",
                _ => metadata.Mode.ToString()
            };
            ChunkSizeText = FormatBytes(metadata.ChunkSize);
            KdfText = $"Argon2id • {metadata.KdfParameters.Iterations:N0} vòng";
            KdfMemoryText = FormatBytes(metadata.KdfParameters.MemorySizeKb * 1024L);
            KdfParallelismText = metadata.KdfParameters.Parallelism.ToString("N0");
            AlgorithmText = metadata.EncryptionAlgorithm;
            HasVerifiedMetadata = true;
            AnalysisMessage = "Đã xác thực và đọc metadata thành công";
            PasswordCheckMessage = "✓ Mật khẩu chính xác";
            IsPasswordCheckError = false;
            StatusMessage = "";
            HasError = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or CryptographicException)
        {
            HasVerifiedMetadata = false;
            OriginalFileNameText = "🔒 Không thể giải mã";
            PasswordCheckMessage = "Mật khẩu không đúng hoặc file đã bị thay đổi";
            IsPasswordCheckError = true;
            StatusMessage = "";
            HasError = true;
        }
        catch (Exception ex)
        {
            PasswordCheckMessage = $"Không thể kiểm tra: {ex.Message}";
            IsPasswordCheckError = true;
            StatusMessage = "";
            HasError = true;
        }
        finally
        {
            if (passwordBytes is not null) CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private async Task DecryptAsync()
    {
        if (IsDecrypting || !ValidateInputs(requireOutput: true))
            return;

        var destination = ResolveDestinationPath(GetDestinationPath());
        if (destination is null)
            return;

        HasError = false;
        StatusMessage = "";
        IsDecrypting = true;
        IsStatusBarVisible = true;
        StatusAction = "Đang giải mã";
        StatusCurrentFile = SourceName;
        StatusProgress = 0;
        StatusDetails = "";

        var cts = new CancellationTokenSource();
        _activeCts = cts;
        var startedAt = DateTime.UtcNow;
        var sourceSize = TryGetSourceSize();

        var progress = new Progress<double>(value =>
        {
            StatusProgress = value;
            if (sourceSize is not > 0)
                return;

            var elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;
            var processed = (long)(sourceSize.Value * value);
            var speed = elapsed > 0.5 ? processed / elapsed : 0;
            StatusDetails = speed > 0
                ? $"{FormatBytes(processed)} / {FormatBytes(sourceSize.Value)} • {FormatBytes((long)speed)}/s"
                : $"{FormatBytes(processed)} / {FormatBytes(sourceSize.Value)}";
        });
        var perFileProgress = new Progress<PerFileProgress>(value =>
        {
            StatusCurrentFile = Path.GetFileName(value.SourceFilePath);
            StatusProgress = value.Progress;
        });

        byte[]? passwordBytes = null;
        try
        {
            passwordBytes = Encoding.UTF8.GetBytes(Password);
            var encryptor = new FileEncryptor(
                _settings.MaxThreads, progress, _settings, perFileProgress);

            string actualOutput;
            if (IsFolderSource)
            {
                await encryptor.DecryptFolderPerFileAsync(
                    SourcePath, destination, passwordBytes, cts.Token);
                actualOutput = destination;
            }
            else if (_header?.Mode == VaultMode.Zip)
            {
                await encryptor.DecryptFolderZipAsync(
                    SourcePath, destination, passwordBytes, cts.Token);
                actualOutput = destination;
            }
            else if (_header?.Mode == VaultMode.File)
            {
                actualOutput = await encryptor.DecryptFileAsync(
                    SourcePath,
                    destination,
                    passwordBytes,
                    cancellationToken: cts.Token,
                    overwriteExisting: OverwriteExisting);
            }
            else
            {
                throw new InvalidDataException("Chế độ vault này không được hỗ trợ ở nguồn đã chọn.");
            }

            StatusProgress = 1;
            StatusAction = "Giải mã hoàn tất";
            StatusCurrentFile = Path.GetFileName(actualOutput);
            StatusMessage = $"✓ Giải mã thành công: {Path.GetFileName(actualOutput)}";
            if (OpenFolderWhenDone)
                _filePicker.OpenFolder(Directory.Exists(actualOutput)
                    ? actualOutput
                    : Path.GetDirectoryName(actualOutput) ?? OutputPath);
        }
        catch (OperationCanceledException)
        {
            StatusAction = "Đã huỷ giải mã";
            StatusMessage = "Đã huỷ thao tác.";
            HasError = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or CryptographicException)
        {
            StatusAction = "Giải mã thất bại";
            StatusMessage = "Sai mật khẩu hoặc dữ liệu vault đã bị thay đổi.";
            HasError = true;
        }
        catch (Exception ex)
        {
            StatusAction = "Giải mã thất bại";
            StatusMessage = $"Lỗi: {ex.Message}";
            HasError = true;
        }
        finally
        {
            if (passwordBytes is not null)
                CryptographicOperations.ZeroMemory(passwordBytes);
            IsDecrypting = false;
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }

    private bool ValidateInputs(bool requireOutput)
    {
        if (!HasSource || !IsVaultValid)
        {
            StatusMessage = "Vui lòng chọn file .safe hoặc thư mục vault hợp lệ.";
            HasError = true;
            return false;
        }
        if (string.IsNullOrEmpty(Password))
        {
            StatusMessage = "Vui lòng nhập mật khẩu giải mã.";
            HasError = true;
            return false;
        }
        if (requireOutput && string.IsNullOrWhiteSpace(OutputPath))
        {
            StatusMessage = "Vui lòng chọn thư mục đích.";
            HasError = true;
            return false;
        }
        return true;
    }

    private string GetDestinationPath()
    {
        var root = Path.GetFullPath(OutputPath);
        if (IsFolderSource)
            return Path.Combine(root, SourceName + "_decrypted");

        var stem = Path.GetFileNameWithoutExtension(SourcePath);
        if (_header?.Mode == VaultMode.Zip)
        {
            var folderName = HasVerifiedMetadata
                ? Path.GetFileNameWithoutExtension(OriginalFileNameText)
                : stem;
            return Path.Combine(root, folderName);
        }

        var fileName = _header?.EncryptFileNames == true
            ? HasVerifiedMetadata ? OriginalFileNameText : "decrypted-output"
            : stem;
        return Path.Combine(root, fileName);
    }

    private string? ResolveDestinationPath(string destination)
    {
        Directory.CreateDirectory(Path.GetFullPath(OutputPath));
        var exists = File.Exists(destination) || Directory.Exists(destination);
        if (!exists)
            return destination;

        if (_header?.Mode == VaultMode.File && OverwriteExisting && File.Exists(destination))
            return destination;

        StatusMessage = "Đầu ra đã tồn tại. Hãy chọn thư mục khác" +
                        (_header?.Mode == VaultMode.File ? " hoặc bật ghi đè." : ".");
        HasError = true;
        return null;
    }

    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(OutputPath);
            _filePicker.OpenFolder(OutputPath);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Không thể mở thư mục đầu ra: {ex.Message}";
            HasError = true;
        }
    }

    private void CloseOrCancelStatus()
    {
        if (IsDecrypting)
            _activeCts?.Cancel();
        else
            IsStatusBarVisible = false;
    }

    private long? TryGetSourceSize()
    {
        try
        {
            return IsFolderSource
                ? new DirectoryInfo(SourcePath).EnumerateFiles("*.safe", SearchOption.AllDirectories).Sum(f => f.Length)
                : new FileInfo(SourcePath).Length;
        }
        catch
        {
            return null;
        }
    }

    private string GetDefaultOutputPath()
    {
        if (!string.IsNullOrWhiteSpace(_settings.DefaultOutputPath))
            return Path.Combine(Path.GetFullPath(_settings.DefaultOutputPath), "Decrypted");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Decrypted");
    }

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
}
