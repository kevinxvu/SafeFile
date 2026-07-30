using System.Text.Json;
using SafeFile.Core.Crypto;
using SafeFile.Core.Models;

namespace SafeFile.Core.Services;

public sealed class SettingsService
{
    private const string SettingsFileName = "settings.json";
    private const string EncryptedOutputFolderName = "Encrypted";
    private const string DecryptedOutputFolderName = "Decrypted";
    private readonly string _settingsDirectory;
    private readonly string _settingsPath;
    private AppSettings _cachedSettings;

    public SettingsService()
    {
        _settingsDirectory = GetAppDataPath();
        _settingsPath = Path.Combine(_settingsDirectory, SettingsFileName);
        _cachedSettings = AppSettings.GetDefaults();
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null)
                {
                    _cachedSettings = loaded;
                    ValidateSettings(_cachedSettings);
                    return _cachedSettings;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
        }

        return _cachedSettings;
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            if (!Directory.Exists(_settingsDirectory))
                Directory.CreateDirectory(_settingsDirectory);

            ValidateSettings(settings);

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);

            _cachedSettings = settings;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            throw;
        }
    }

    public void RestoreDefaults()
    {
        var currentLanguage = _cachedSettings.Language;
        _cachedSettings = AppSettings.GetDefaults();
        _cachedSettings.Language = currentLanguage;
        Save(_cachedSettings);
    }

    public string GetSettingsPath() => _settingsPath;

    private static void ValidateSettings(AppSettings settings)
    {
        if (settings.Language is not ("en" or "vi"))
            settings.Language = "en";

        if (settings.Theme is not ("Light" or "Dark"))
            settings.Theme = "Light";

        if (settings.DefaultChunkSizeMb < 1 || settings.DefaultChunkSizeMb > 16)
            settings.DefaultChunkSizeMb = 1;

        if (settings.MaxThreads < 1)
            settings.MaxThreads = 1;

        if (settings.MaxThreads > Environment.ProcessorCount * 2)
            settings.MaxThreads = Environment.ProcessorCount;

        if (settings.Argon2MemorySizeKb < 16_384)
            settings.Argon2MemorySizeKb = 16_384;

        if (settings.Argon2MemorySizeKb > Argon2Kdf.MaximumMemorySizeKb)
            settings.Argon2MemorySizeKb = Argon2Kdf.MaximumMemorySizeKb;

        if (settings.Argon2Iterations < 1)
            settings.Argon2Iterations = 1;

        if (settings.Argon2Iterations > Argon2Kdf.MaximumIterations)
            settings.Argon2Iterations = Argon2Kdf.MaximumIterations;

        if (settings.Argon2Parallelism < 1)
            settings.Argon2Parallelism = 1;

        if (settings.Argon2Parallelism > Argon2Kdf.MaximumParallelism)
            settings.Argon2Parallelism = Argon2Kdf.MaximumParallelism;

        if (settings.MinPasswordLength < 6)
            settings.MinPasswordLength = 6;

        if (settings.MinPasswordLength > 128)
            settings.MinPasswordLength = 128;

        if (IsLegacyDefaultOutputPath(settings.DefaultOutputPath) ||
            IsLegacyDefaultOutputPath(settings.DefaultOutputPath, "Encrypt"))
            settings.DefaultOutputPath = GetDefaultOutputPath(EncryptedOutputFolderName);
        else if (string.IsNullOrWhiteSpace(settings.DefaultOutputPath))
            settings.DefaultOutputPath = GetDefaultOutputPath(EncryptedOutputFolderName);

        if (IsLegacyDefaultOutputPath(settings.DefaultDecryptOutputPath, "Decrypt"))
            settings.DefaultDecryptOutputPath = GetDefaultOutputPath(DecryptedOutputFolderName);
        else if (string.IsNullOrWhiteSpace(settings.DefaultDecryptOutputPath))
            settings.DefaultDecryptOutputPath = GetDefaultOutputPath(DecryptedOutputFolderName);

        var validPriorities = new[] { "Low", "Normal", "High" };
        if (!validPriorities.Contains(settings.CpuPriority))
            settings.CpuPriority = "Normal";
    }

    private static bool IsLegacyDefaultOutputPath(string path, string? legacySubfolder = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var legacyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "SafeFile");
        if (!string.IsNullOrWhiteSpace(legacySubfolder))
            legacyPath = Path.Combine(legacyPath, legacySubfolder);

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyPath)),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string GetDefaultOutputPath(string outputFolderName)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "SafeFile",
            outputFolderName);
    }

    private static string GetAppDataPath()
    {
        return Environment.OSVersion.Platform switch
        {
            PlatformID.Win32NT => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SafeFile"),

            _ => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".SafeFile")
        };
    }
}
