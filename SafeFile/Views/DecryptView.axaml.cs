using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using SafeFile.ViewModels;
using System;
using System.IO;
using System.Linq;

namespace SafeFile.Views;

public partial class DecryptView : UserControl
{
    public DecryptView()
    {
        InitializeComponent();
    }

    private void OnDragEnter(object? sender, DragEventArgs e) => UpdateDragState(e);
    private void OnDragOver(object? sender, DragEventArgs e) => UpdateDragState(e);
    private void OnDragLeave(object? sender, DragEventArgs e) => ResetDragState();

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        ResetDragState();
        if (DataContext is DecryptViewModel { IsDecrypting: true })
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var paths = e.DataTransfer.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .Where(path => IsAcceptedPath(path))
            .Cast<string>()
            .ToArray() ?? [];

        if (paths.Length == 0)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        if (DataContext is DecryptViewModel viewModel)
            await viewModel.AddDroppedSourcesAsync(paths);

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void UpdateDragState(DragEventArgs e)
    {
        if (DataContext is DecryptViewModel { IsDecrypting: true })
        {
            e.DragEffects = DragDropEffects.None;
            ResetDragState();
            e.Handled = true;
            return;
        }

        var accepted = e.DataTransfer.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .Any(IsAcceptedPath) == true;
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        if (accepted)
        {
            DropZone.Background = FindBrush("AccentHoverBrush");
            DropZone.BorderBrush = FindBrush("AccentBrush");
        }
        else
        {
            ResetDragState();
        }
        e.Handled = true;
    }

    private static bool IsAcceptedPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (Directory.Exists(path) ||
         string.Equals(
             Path.GetExtension(path),
             ".safe",
             StringComparison.OrdinalIgnoreCase));

    private void ResetDragState()
    {
        DropZone.ClearValue(Border.BackgroundProperty);
        DropZone.ClearValue(Border.BorderBrushProperty);
    }

    private Avalonia.Media.IBrush? FindBrush(string resourceKey) =>
        DropZone.TryFindResource(resourceKey, out var resource)
            ? resource as Avalonia.Media.IBrush
            : null;
}
