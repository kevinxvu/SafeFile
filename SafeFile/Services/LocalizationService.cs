using System;
using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace SafeFile.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources =
        new("SafeFile.Resources.Strings", typeof(LocalizationService).Assembly);

    public static LocalizationService Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CultureChanged;

    public string CurrentLanguage { get; private set; } = "en";

    public string this[string key] => Get(key);

    public string Get(string key) =>
        Resources.GetString(key, CultureInfo.CurrentUICulture) ?? $"[{key}]";

    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public void SetLanguage(string? language)
    {
        var normalized = language == "vi" ? "vi" : "en";
        if (CurrentLanguage == normalized &&
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == normalized)
            return;

        var culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CurrentLanguage = normalized;
        // Avalonia bindings to the resource indexer can remain alive for the
        // lifetime of the shell. Notify both the indexer and all properties so
        // existing sidebar/page bindings are refreshed without recreating them.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }
}
