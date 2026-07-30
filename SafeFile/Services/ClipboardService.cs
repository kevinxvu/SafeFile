using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace SafeFile.Services;

public sealed class ClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        var window = (Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var clipboard = window?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }
}
