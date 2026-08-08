using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeFile.Core.IO;
using SafeFile.Core.Services;
using SafeFile.Services;
using Serilog;

namespace SafeFile.ViewModels;

public sealed partial class ToolsViewModel : ViewModelBase
{
    private static readonly ILogger Logger = Log.ForContext<ToolsViewModel>();
    private static readonly FilePickerFileType TextFileType = new("Text file")
    {
        Patterns = ["*.txt"]
    };

    private readonly IFilePickerService _filePicker;
    private readonly IClipboardService _clipboard;
    private readonly IErrorDialogService _errorDialog;
    private readonly SettingsService _settingsService;
    private readonly Microsoft.Extensions.Logging.ILogger<FileEncryptor> _fileEncryptorLogger;
    private readonly TextCryptoService _textCrypto = new();

    [ObservableProperty] private string _encryptInput = "";
    [ObservableProperty] private string _encryptPassword = "";
    [ObservableProperty] private string _encryptResult = "";
    [ObservableProperty] private bool _showEncryptPassword;
    [ObservableProperty] private bool _isEncryptingText;
    [ObservableProperty] private string _encryptStatus = "";

    [ObservableProperty] private string _decryptInput = "";
    [ObservableProperty] private string _decryptPassword = "";
    [ObservableProperty] private string _decryptResult = "";
    [ObservableProperty] private bool _showDecryptPassword;
    [ObservableProperty] private bool _isDecryptingText;
    [ObservableProperty] private bool _decryptResultIsFileName;
    [ObservableProperty] private string _decryptStatus = "";

    [ObservableProperty] private string _shaInput = "";
    [ObservableProperty] private string _shaResult = "";
    [ObservableProperty] private bool _shaUseHex = true;
    [ObservableProperty] private string _shaStatus = "";

    [ObservableProperty] private int _passwordLength = 10;
    [ObservableProperty] private bool _includeUppercase = true;
    [ObservableProperty] private bool _includeLowercase = true;
    [ObservableProperty] private bool _includeNumbers = true;
    [ObservableProperty] private bool _includeSpecialCharacters = true;
    [ObservableProperty] private bool _excludeAmbiguousCharacters;
    [ObservableProperty] private string _generatedPassword = "";
    [ObservableProperty] private string _generatorStatus = "";

    private string _encryptSuggestedFileName = "encrypted-text.txt";
    private string _decryptSuggestedFileName = "decrypted-text.txt";

    public int MaximumTextCharacters => TextCryptoService.MaximumTextCharacters;
    public int MaximumEncryptedInputCharacters => TextCryptoService.MaximumTextCharacters * 6;
    public char EncryptPasswordChar => ShowEncryptPassword ? '\0' : '●';
    public char DecryptPasswordChar => ShowDecryptPassword ? '\0' : '●';
    public bool HasEncryptResult => EncryptResult.Length > 0;
    public bool HasDecryptResult => DecryptResult.Length > 0;
    public bool CanSaveDecryptResult => HasDecryptResult && !DecryptResultIsFileName;
    public bool HasShaResult => ShaResult.Length > 0;
    public bool HasGeneratedPassword => GeneratedPassword.Length > 0;
    public bool ShaUseBase64
    {
        get => !ShaUseHex;
        set
        {
            if (value)
                ShaUseHex = false;
        }
    }
    public string EncryptCharacterCount => F("ToolsCharacterCount", EncryptInput.Length, MaximumTextCharacters);
    public string ShaCharacterCount => F("ToolsCharacterCount", ShaInput.Length, MaximumTextCharacters);

    public IAsyncRelayCommand EncryptTextCommand { get; }
    public IAsyncRelayCommand DecryptTextCommand { get; }
    public IRelayCommand CalculateShaCommand { get; }
    public IRelayCommand GeneratePasswordCommand { get; }
    public IRelayCommand ToggleEncryptPasswordCommand { get; }
    public IRelayCommand ToggleDecryptPasswordCommand { get; }
    public IRelayCommand ClearEncryptCommand { get; }
    public IRelayCommand ClearDecryptCommand { get; }
    public IRelayCommand ClearShaCommand { get; }
    public IAsyncRelayCommand CopyEncryptResultCommand { get; }
    public IAsyncRelayCommand CopyDecryptResultCommand { get; }
    public IAsyncRelayCommand CopyShaResultCommand { get; }
    public IAsyncRelayCommand CopyGeneratedPasswordCommand { get; }
    public IAsyncRelayCommand SaveEncryptResultCommand { get; }
    public IAsyncRelayCommand SaveDecryptResultCommand { get; }
    public IAsyncRelayCommand SaveShaResultCommand { get; }

    public ToolsViewModel(
        IFilePickerService filePicker,
        IClipboardService clipboard,
        IErrorDialogService errorDialog,
        SettingsService settingsService,
        Microsoft.Extensions.Logging.ILogger<FileEncryptor> fileEncryptorLogger)
    {
        _filePicker = filePicker;
        _clipboard = clipboard;
        _errorDialog = errorDialog;
        _settingsService = settingsService;
        _fileEncryptorLogger = fileEncryptorLogger;

        EncryptTextCommand = new AsyncRelayCommand(EncryptTextAsync);
        DecryptTextCommand = new AsyncRelayCommand(DecryptTextAsync);
        CalculateShaCommand = new RelayCommand(CalculateSha);
        GeneratePasswordCommand = new RelayCommand(GeneratePassword);
        ToggleEncryptPasswordCommand = new RelayCommand(() => ShowEncryptPassword = !ShowEncryptPassword);
        ToggleDecryptPasswordCommand = new RelayCommand(() => ShowDecryptPassword = !ShowDecryptPassword);
        ClearEncryptCommand = new RelayCommand(ClearEncrypt);
        ClearDecryptCommand = new RelayCommand(ClearDecrypt);
        ClearShaCommand = new RelayCommand(ClearSha);
        CopyEncryptResultCommand = new AsyncRelayCommand(() => CopyAsync(EncryptResult, status => EncryptStatus = status));
        CopyDecryptResultCommand = new AsyncRelayCommand(() => CopyAsync(DecryptResult, status => DecryptStatus = status));
        CopyShaResultCommand = new AsyncRelayCommand(() => CopyAsync(ShaResult, status => ShaStatus = status));
        CopyGeneratedPasswordCommand = new AsyncRelayCommand(() => CopyAsync(GeneratedPassword, status => GeneratorStatus = status));
        SaveEncryptResultCommand = new AsyncRelayCommand(() => SaveAsync(EncryptResult, _encryptSuggestedFileName, status => EncryptStatus = status));
        SaveDecryptResultCommand = new AsyncRelayCommand(() => SaveAsync(DecryptResult, _decryptSuggestedFileName, status => DecryptStatus = status));
        SaveShaResultCommand = new AsyncRelayCommand(() => SaveAsync(ShaResult, TextCryptoService.ComputeSha256Hex(ShaInput) + ".txt", status => ShaStatus = status));
    }

    partial void OnShowEncryptPasswordChanged(bool value) => OnPropertyChanged(nameof(EncryptPasswordChar));
    partial void OnShowDecryptPasswordChanged(bool value) => OnPropertyChanged(nameof(DecryptPasswordChar));
    partial void OnEncryptInputChanged(string value) => OnPropertyChanged(nameof(EncryptCharacterCount));
    partial void OnShaInputChanged(string value) => OnPropertyChanged(nameof(ShaCharacterCount));
    partial void OnEncryptResultChanged(string value) => OnPropertyChanged(nameof(HasEncryptResult));
    partial void OnDecryptResultChanged(string value)
    {
        OnPropertyChanged(nameof(HasDecryptResult));
        OnPropertyChanged(nameof(CanSaveDecryptResult));
    }
    partial void OnDecryptResultIsFileNameChanged(bool value) => OnPropertyChanged(nameof(CanSaveDecryptResult));
    partial void OnShaResultChanged(string value) => OnPropertyChanged(nameof(HasShaResult));
    partial void OnGeneratedPasswordChanged(string value) => OnPropertyChanged(nameof(HasGeneratedPassword));
    partial void OnShaUseHexChanged(bool value)
    {
        OnPropertyChanged(nameof(ShaUseBase64));
        if (ShaResult.Length > 0)
            CalculateSha();
    }

    private async Task EncryptTextAsync()
    {
        if (IsEncryptingText)
            return;
        if (!ValidateTextLength(EncryptInput, "CannotEncryptText"))
            return;
        var settings = _settingsService.Load();
        if (string.IsNullOrEmpty(EncryptPassword))
        {
            await ShowErrorAsync(L("PasswordRequired"), L("CannotEncryptText"));
            return;
        }
        if (EncryptPassword.Length < settings.MinPasswordLength)
        {
            await ShowErrorAsync(F("PasswordTooShort", settings.MinPasswordLength), L("CannotEncryptText"));
            return;
        }

        byte[]? passwordBytes = null;
        try
        {
            IsEncryptingText = true;
            EncryptStatus = L("ToolsEncryptingText");
            passwordBytes = Encoding.UTF8.GetBytes(EncryptPassword);
            EncryptResult = await _textCrypto.EncryptAsync(EncryptInput, passwordBytes);
            _encryptSuggestedFileName = TextCryptoService.ComputeSha256Hex(EncryptInput) + ".txt";
            EncryptStatus = L("ToolsTextEncrypted");
            Logger.Information("Text encryption completed");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Text encryption failed");
            await ShowErrorAsync(ex.Message, L("CannotEncryptText"));
            EncryptStatus = L("ToolsOperationFailed");
        }
        finally
        {
            if (passwordBytes is not null)
                CryptographicOperations.ZeroMemory(passwordBytes);
            IsEncryptingText = false;
        }
    }

    private async Task DecryptTextAsync()
    {
        if (IsDecryptingText)
            return;
        var input = DecryptInput.Trim();
        if (input.Length == 0 || string.IsNullOrEmpty(DecryptPassword))
        {
            await ShowErrorAsync(L(input.Length == 0 ? "ToolsEncryptedInputRequired" : "PasswordRequired"), L("CannotDecryptText"));
            return;
        }
        if (input.Length > MaximumEncryptedInputCharacters)
        {
            await ShowErrorAsync(F("ToolsEncryptedInputTooLong", MaximumEncryptedInputCharacters), L("CannotDecryptText"));
            return;
        }

        byte[]? passwordBytes = null;
        try
        {
            IsDecryptingText = true;
            DecryptStatus = L("ToolsDecryptingText");
            passwordBytes = Encoding.UTF8.GetBytes(DecryptPassword);
            if (TextCryptoService.IsEncryptedText(input))
            {
                DecryptResult = await _textCrypto.DecryptAsync(input, passwordBytes);
                DecryptResultIsFileName = false;
                _decryptSuggestedFileName = TextCryptoService.ComputeSha256Hex(DecryptResult) + ".txt";
            }
            else
            {
                var normalized = NormalizeEncryptedFileName(input);
                DecryptResult = await new FileEncryptor(
                        settings: _settingsService.Load(),
                        logger: _fileEncryptorLogger)
                    .DecryptOutputFileNameAsync(normalized, passwordBytes);
                DecryptResultIsFileName = true;
            }
            DecryptStatus = DecryptResultIsFileName
                ? L("ToolsFileNameDecrypted")
                : L("ToolsTextDecrypted");
            Logger.Information("Text tool decryption completed for {ResultType}", DecryptResultIsFileName ? "filename" : "text");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Text tool decryption failed");
            await ShowErrorAsync(ex.Message, L("CannotDecryptText"));
            DecryptStatus = L("ToolsOperationFailed");
        }
        finally
        {
            if (passwordBytes is not null)
                CryptographicOperations.ZeroMemory(passwordBytes);
            IsDecryptingText = false;
        }
    }

    private static string NormalizeEncryptedFileName(string input)
    {
        string fileName;
        try { fileName = Path.GetFileName(input); }
        catch (ArgumentException) { fileName = input; }
        if (fileName.EndsWith(".safe", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^5];
        if ((fileName.Length is 32 or 64) && fileName.All(Uri.IsHexDigit))
            throw new InvalidDataException("A hashed filename cannot be reversed.");
        return fileName + ".safe";
    }

    private void CalculateSha()
    {
        if (!ValidateTextLength(ShaInput, "CannotCalculateSha"))
            return;
        try
        {
            ShaResult = ShaUseHex
                ? TextCryptoService.ComputeSha256Hex(ShaInput)
                : TextCryptoService.ComputeSha256Base64(ShaInput);
            ShaStatus = L("ToolsHashCalculated");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SHA-256 calculation failed");
            _ = ShowErrorAsync(ex.Message, L("CannotCalculateSha"));
        }
    }

    private void GeneratePassword()
    {
        try
        {
            GeneratedPassword = PasswordGenerator.Generate(new PasswordGeneratorOptions(
                PasswordLength, IncludeUppercase, IncludeLowercase, IncludeNumbers,
                IncludeSpecialCharacters, ExcludeAmbiguousCharacters));
            GeneratorStatus = L("ToolsPasswordGenerated");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Password generation failed");
            _ = ShowErrorAsync(ex.Message, L("CannotGeneratePassword"));
        }
    }

    private bool ValidateTextLength(string text, string titleKey)
    {
        if (text.Length <= MaximumTextCharacters)
            return true;
        _ = ShowErrorAsync(F("ToolsTextTooLong", MaximumTextCharacters), L(titleKey));
        return false;
    }

    private async Task CopyAsync(string value, Action<string> setStatus)
    {
        if (value.Length == 0)
            return;
        try
        {
            await _clipboard.SetTextAsync(value);
            setStatus(L("ToolsCopied"));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Tools result copy failed");
            await ShowErrorAsync(ex.Message, L("CannotCopyResult"));
        }
    }

    private async Task SaveAsync(string value, string suggestedName, Action<string> setStatus)
    {
        if (value.Length == 0)
            return;
        try
        {
            var path = await _filePicker.PickSaveFileAsync(L("ToolsSaveResult"), suggestedName, [TextFileType]);
            if (path is null)
                return;
            await File.WriteAllTextAsync(path, value, new UTF8Encoding(false));
            setStatus(L("ToolsSaved"));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Tools result save failed");
            await ShowErrorAsync(ex.Message, L("CannotSaveResult"));
        }
    }

    private void ClearEncrypt() { EncryptInput = ""; EncryptPassword = ""; EncryptResult = ""; EncryptStatus = ""; }
    private void ClearDecrypt() { DecryptInput = ""; DecryptPassword = ""; DecryptResult = ""; DecryptResultIsFileName = false; DecryptStatus = ""; }
    private void ClearSha() { ShaInput = ""; ShaResult = ""; ShaStatus = ""; }
    private Task ShowErrorAsync(string message, string title) => _errorDialog.ShowErrorAsync(message, title);
    private static string L(string key) => LocalizationService.Instance.Get(key);
    private static string F(string key, params object[] args) =>
        LocalizationService.Instance.Format(key, args);
}
