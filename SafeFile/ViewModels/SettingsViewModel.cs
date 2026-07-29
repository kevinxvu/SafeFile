using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeFile.Core.Crypto;
using SafeFile.Core.Models;
using SafeFile.Core.Services;
using SafeFile.Services;

namespace SafeFile.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly IFilePickerService _filePicker;

    // ── Performance ───────────────────────────────────────────────────────────
    [ObservableProperty] private int _defaultChunkSizeMb;
    [ObservableProperty] private int _maxThreads;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCpuLow))]
    [NotifyPropertyChangedFor(nameof(IsCpuNormal))]
    [NotifyPropertyChangedFor(nameof(IsCpuHigh))]
    private string _cpuPriority = "Normal";

    // ── Password ──────────────────────────────────────────────────────────────
    [ObservableProperty] private int _minPasswordLength;
    [ObservableProperty] private bool _confirmPasswordToggle;

    // ── Argon2 KDF ────────────────────────────────────────────────────────────
    [ObservableProperty] private int _argon2MemoryMb;   // displayed in MB, saved as KB
    [ObservableProperty] private int _argon2Iterations;
    [ObservableProperty] private int _argon2Parallelism;

    // ── Output ────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _defaultOutputPath = "";

    // ── UI state ──────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusForeground))]
    private bool _hasError;

    [ObservableProperty] private string _statusMessage = "";

    public string StatusForeground => HasError ? "#DC2626" : "#16A34A";

    // ── Radio-button helpers: CPU priority ────────────────────────────────────
    public bool IsCpuLow    { get => CpuPriority == "Low";    set { if (value) CpuPriority = "Low"; } }
    public bool IsCpuNormal { get => CpuPriority == "Normal"; set { if (value) CpuPriority = "Normal"; } }
    public bool IsCpuHigh   { get => CpuPriority == "High";   set { if (value) CpuPriority = "High"; } }

    // ── Slider limits ─────────────────────────────────────────────────────────
    public int MaxThreadsLimit      { get; } = Math.Max(1, Environment.ProcessorCount);
    public int[] ChunkSizeOptions   { get; } = [1, 2, 4, 8, 16];
    public int Argon2MemoryMinMb    { get; } = 16;
    public int Argon2MemoryMaxMb    { get; } = Argon2Kdf.MaximumMemorySizeKb / 1_024;
    public int Argon2IterationsMax  { get; } = Argon2Kdf.MaximumIterations;
    public int Argon2ParallelismMax { get; } = Argon2Kdf.MaximumParallelism;

    // ── Commands ──────────────────────────────────────────────────────────────
    public IAsyncRelayCommand BrowseOutputPathCommand { get; }
    public IRelayCommand SaveCommand { get; }
    public IRelayCommand RestoreDefaultsCommand { get; }

    public SettingsViewModel(SettingsService settingsService, IFilePickerService filePicker)
    {
        _settingsService = settingsService;
        _filePicker = filePicker;

        BrowseOutputPathCommand = new AsyncRelayCommand(BrowseOutputPathAsync);
        SaveCommand             = new RelayCommand(Save);
        RestoreDefaultsCommand  = new RelayCommand(RestoreDefaults);

        LoadFromService();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void LoadFromService()
    {
        var s = _settingsService.Load();
        DefaultChunkSizeMb    = s.DefaultChunkSizeMb;
        MaxThreads            = Math.Clamp(s.MaxThreads, 1, MaxThreadsLimit);
        CpuPriority           = s.CpuPriority;
        Argon2MemoryMb        = s.Argon2MemorySizeKb / 1_024;
        Argon2Iterations      = s.Argon2Iterations;
        Argon2Parallelism     = s.Argon2Parallelism;
        MinPasswordLength     = s.MinPasswordLength;
        ConfirmPasswordToggle = s.ConfirmPasswordToggle;
        DefaultOutputPath     = s.DefaultOutputPath;
        StatusMessage         = "";
        HasError              = false;
    }

    private void Save()
    {
        try
        {
            var s = new AppSettings
            {
                DefaultChunkSizeMb    = DefaultChunkSizeMb,
                MaxThreads            = MaxThreads,
                CpuPriority           = CpuPriority,
                Argon2MemorySizeKb    = Argon2MemoryMb * 1_024,
                Argon2Iterations      = Argon2Iterations,
                Argon2Parallelism     = Argon2Parallelism,
                MinPasswordLength     = MinPasswordLength,
                ConfirmPasswordToggle = ConfirmPasswordToggle,
                DefaultOutputPath     = DefaultOutputPath,
            };
            _settingsService.Save(s);
            StatusMessage = "✓ Đã lưu cài đặt thành công.";
            HasError = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi: {ex.Message}";
            HasError = true;
        }
    }

    private void RestoreDefaults()
    {
        _settingsService.RestoreDefaults();
        LoadFromService();
        StatusMessage = "✓ Đã khôi phục cài đặt mặc định.";
        HasError = false;
    }

    private async Task BrowseOutputPathAsync()
    {
        var path = await _filePicker.PickFolderAsync("Chọn thư mục đầu ra mặc định");
        if (path is not null)
            DefaultOutputPath = path;
    }
}
