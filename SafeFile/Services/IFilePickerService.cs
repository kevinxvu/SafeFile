using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SafeFile.Services;

public interface IFilePickerService
{
    Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerFileType>? filters = null);
    Task<IReadOnlyList<string>> PickFilesAsync(
        string title,
        IReadOnlyList<FilePickerFileType>? filters = null);
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickSaveFileAsync(
        string title,
        string suggestedFileName,
        IReadOnlyList<FilePickerFileType>? filters = null);
    void OpenFolder(string path);
}
