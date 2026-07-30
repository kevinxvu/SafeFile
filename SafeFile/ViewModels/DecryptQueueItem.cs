using System;
using CommunityToolkit.Mvvm.ComponentModel;
using SafeFile.Services;
using CommunityToolkit.Mvvm.Input;
using SafeFile.Core.Format;

namespace SafeFile.ViewModels;

public sealed partial class DecryptQueueItem : ObservableObject
{
    private readonly Action<DecryptQueueItem> _remove;

    public string SourcePath { get; }
    public string SourceRoot { get; }
    public string RelativeDirectory { get; }
    public string VaultName { get; }
    public string VaultSizeText { get; }
    public DateTime LastModifiedUtc { get; }
    public VaultHeader? Header { get; }
    public bool IsValid => Header is not null;

    [ObservableProperty] private string _originalFileName = L("NotVerified");
    [ObservableProperty] private string _status = L("Ready");
    [ObservableProperty] private string _statusForeground = "#4B5563";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _hasVerifiedMetadata;
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private bool _isLocked;

    public string ProgressText => $"{Progress:P0}";
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public IRelayCommand RemoveCommand { get; }

    public DecryptQueueItem(
        string sourcePath,
        string sourceRoot,
        string relativeDirectory,
        string vaultSizeText,
        DateTime lastModifiedUtc,
        VaultHeader? header,
        string initialStatus,
        string initialError,
        Action<DecryptQueueItem> remove)
    {
        SourcePath = sourcePath;
        VaultName = System.IO.Path.GetFileName(
            System.IO.Path.TrimEndingDirectorySeparator(sourcePath));
        SourceRoot = sourceRoot;
        RelativeDirectory = relativeDirectory;
        VaultSizeText = vaultSizeText;
        LastModifiedUtc = lastModifiedUtc;
        Header = header;
        Status = initialStatus;
        ErrorMessage = initialError;
        OriginalFileName = header is null ? "—" : L("NotVerified");
        StatusForeground = header is null ? "#DC2626" : "#4B5563";
        _remove = remove;
        RemoveCommand = new RelayCommand(() => _remove(this));
    }

    partial void OnProgressChanged(double value) =>
        OnPropertyChanged(nameof(ProgressText));

    partial void OnErrorMessageChanged(string value) =>
        OnPropertyChanged(nameof(HasError));

    public void ResetAuthentication()
    {
        if (!IsValid)
            return;

        OriginalFileName = L("NotVerified");
        HasVerifiedMetadata = false;
        ErrorMessage = "";
        Progress = 0;
        Status = L("Ready");
        StatusForeground = "#4B5563";
    }

    private static string L(string key) => LocalizationService.Instance.Get(key);
}
