using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeFile.Services;
using System.Threading;

namespace SafeFile.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private CancellationTokenSource? _activeCts;

    // ── Navigation ────────────────────────────────────────────────
    [ObservableProperty] private ViewModelBase _currentPage;
    [ObservableProperty] private string _activePage = "Mã hoá dữ liệu";

    // ── Status bar ────────────────────────────────────────────────
    [ObservableProperty] private bool _isOperationActive;
    [ObservableProperty] private string _statusCurrentFile = "";
    [ObservableProperty] private double _statusProgress;
    [ObservableProperty] private string _statusSpeed = "";
    [ObservableProperty] private string _statusEta = "";

    // ── Commands ──────────────────────────────────────────────────
    public IRelayCommand NavigateToEncryptCommand { get; }
    public IRelayCommand NavigateToDecryptCommand { get; }
    public IRelayCommand NavigateToLogsCommand { get; }
    public IRelayCommand NavigateToSettingsCommand { get; }
    public IRelayCommand NavigateToAboutCommand { get; }
    public IRelayCommand CancelOperationCommand { get; }

    public MainWindowViewModel(IFilePickerService filePicker)
    {
        _currentPage = new EncryptViewModel(filePicker, this);

        NavigateToEncryptCommand = new RelayCommand(() =>
        {
            ActivePage = "Mã hoá dữ liệu";
            CurrentPage = new EncryptViewModel(filePicker, this);
        });
        NavigateToDecryptCommand = new RelayCommand(() =>
        {
            ActivePage = "Giải mã dữ liệu";
            CurrentPage = new PlaceholderViewModel("Giải mã");
        });
        NavigateToLogsCommand = new RelayCommand(() =>
        {
            ActivePage = "Nhật ký";
            CurrentPage = new PlaceholderViewModel("Nhật ký");
        });
        NavigateToSettingsCommand = new RelayCommand(() =>
        {
            ActivePage = "Thiết lập";
            CurrentPage = new PlaceholderViewModel("Thiết lập");
        });
        NavigateToAboutCommand = new RelayCommand(() =>
        {
            ActivePage = "Về ứng dụng";
            CurrentPage = new PlaceholderViewModel("Về ứng dụng");
        });

        CancelOperationCommand = new RelayCommand(
            () => _activeCts?.Cancel(),
            () => IsOperationActive);
    }

    partial void OnIsOperationActiveChanged(bool value)
        => CancelOperationCommand.NotifyCanExecuteChanged();

    public void SetActiveCts(CancellationTokenSource? cts)
    {
        _activeCts = cts;
        CancelOperationCommand.NotifyCanExecuteChanged();
    }
}
