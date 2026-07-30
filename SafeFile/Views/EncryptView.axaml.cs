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

        var item = e.DataTransfer.TryGetFile();
        var localPath = item?.TryGetLocalPath();
        if (item is null || string.IsNullOrWhiteSpace(localPath))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        if (DataContext is EncryptViewModel viewModel)
        {
            viewModel.IsFileSource = item is IStorageFile;
            viewModel.SourcePath = localPath;
            viewModel.StatusMessage = "";
            viewModel.HasError = false;
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private static void UpdateDragState(Border? border, DragEventArgs e)
    {
        var acceptsDrop = e.DataTransfer.TryGetFile() is IStorageFile or IStorageFolder;
        e.DragEffects = acceptsDrop ? DragDropEffects.Copy : DragDropEffects.None;

        if (border is not null)
        {
            if (acceptsDrop)
            {
                border.Background = FindBrush(border, "AccentHoverBrush");
                border.BorderBrush = FindBrush(border, "AccentBrush");
            }
            else
            {
                ResetDragState(border);
            }
        }

        e.Handled = true;
    }

    private static void ResetDragState(Border? border)
    {
        if (border is null)
            return;

        border.ClearValue(Border.BackgroundProperty);
        border.ClearValue(Border.BorderBrushProperty);
    }

    private static Avalonia.Media.IBrush? FindBrush(
        Control control,
        string resourceKey) =>
        control.TryFindResource(resourceKey, out var resource)
            ? resource as Avalonia.Media.IBrush
            : null;
}
