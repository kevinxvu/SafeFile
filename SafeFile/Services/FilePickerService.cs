using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SafeFile.Services;

public sealed class FilePickerService : IFilePickerService
{
    private static Window? GetMainWindow()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerFileType>? filters = null)
    {
        var window = GetMainWindow();
        if (window is null) return null;
        var results = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filters
        });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var window = GetMainWindow();
        if (window is null) return null;
        var results = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    public void OpenFolder(string path)
    {
        if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
    }
}
