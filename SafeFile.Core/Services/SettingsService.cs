using System.Text.Json;
using SafeFile.Core.Crypto;
using SafeFile.Core.Models;

namespace SafeFile.Core.Services;

public sealed class SettingsService
{
    private const string SettingsFileName = "settings.json";
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
        _cachedSettings = AppSettings.GetDefaults();
        Save(_cachedSettings);
    }

    public string GetSettingsPath() => _settingsPath;

    private static void ValidateSettings(AppSettings settings)
    {
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

        if (string.IsNullOrWhiteSpace(settings.DefaultOutputPath))
            settings.DefaultOutputPath = AppSettings.GetDefaults().DefaultOutputPath;

        var validPriorities = new[] { "Low", "Normal", "High" };
        if (!validPriorities.Contains(settings.CpuPriority))
            settings.CpuPriority = "Normal";
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
