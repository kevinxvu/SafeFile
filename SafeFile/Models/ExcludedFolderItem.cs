using System;
using CommunityToolkit.Mvvm.Input;

namespace SafeFile.Models;

public sealed class ExcludedFolderItem
{
    public ExcludedFolderItem(string fullPath, Action<ExcludedFolderItem> remove)
    {
        FullPath = fullPath;
        DisplayName = System.IO.Path.GetFileName(
            System.IO.Path.TrimEndingDirectorySeparator(fullPath));
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string FullPath { get; }
    public string DisplayName { get; }
    public IRelayCommand RemoveCommand { get; }
}
