using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using SafeFile.ViewModels;

namespace SafeFile.Views;

public partial class EncryptView : UserControl
{
    public EncryptView()
    {
        InitializeComponent();
    }

    private void OnSourceDragEnter(object? sender, DragEventArgs e)
    {
        UpdateDragState(sender as Border, e);
    }

    private void OnSourceDragOver(object? sender, DragEventArgs e)
    {
        UpdateDragState(sender as Border, e);
    }

    private void OnSourceDragLeave(object? sender, DragEventArgs e)
    {
        ResetDragState(sender as Border);
    }

    private void OnSourceDrop(object? sender, DragEventArgs e)
    {
        ResetDragState(sender as Border);

        var paths = e.DataTransfer.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray() ?? [];
        if (paths.Length == 0)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        if (DataContext is EncryptViewModel viewModel)
        {
            var files = paths.Where(File.Exists).ToArray();
            var folders = paths.Where(Directory.Exists).ToArray();
            if (files.Length == paths.Length)
                viewModel.SelectSourceFiles(files);
            else if (folders.Length == 1 && paths.Length == 1)
                viewModel.SelectSourceFolder(folders[0]);
            else
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }
            viewModel.StatusMessage = "";
            viewModel.HasError = false;
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private static void UpdateDragState(Border? border, DragEventArgs e)
    {
        var acceptsDrop = e.DataTransfer.TryGetFiles()?
            .Any(item => !string.IsNullOrWhiteSpace(item.TryGetLocalPath())) == true;
        e.DragEffects = acceptsDrop ? DragDropEffects.Copy : DragDropEffects.None;

        if (border is not null)
            border.Classes.Set("drag-active", acceptsDrop);

        e.Handled = true;
    }

    private static void ResetDragState(Border? border)
    {
        if (border is null)
            return;

        border.Classes.Set("drag-active", false);
    }
}
