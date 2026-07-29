using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SafeFile.ViewModels;

namespace SafeFile.Views;

public partial class DecryptView : UserControl
{
    private static readonly IBrush DefaultBackground = Brush.Parse("#F8FAFC");
    private static readonly IBrush ActiveBackground = Brush.Parse("#DBEAFE");
    private static readonly IBrush DefaultBorder = Brush.Parse("#CBD5E1");
    private static readonly IBrush ActiveBorder = Brush.Parse("#2563EB");

    public DecryptView()
    {
        InitializeComponent();
    }

    private void OnDragEnter(object? sender, DragEventArgs e) => UpdateDragState(e);
    private void OnDragOver(object? sender, DragEventArgs e) => UpdateDragState(e);
    private void OnDragLeave(object? sender, DragEventArgs e) => ResetDragState();

    private void OnDrop(object? sender, DragEventArgs e)
    {
        ResetDragState();
        var item = e.DataTransfer.TryGetFile();
        var path = item?.TryGetLocalPath();
        var accepted = item is IStorageFolder ||
                       item is IStorageFile && string.Equals(
                           System.IO.Path.GetExtension(path), ".safe",
                           System.StringComparison.OrdinalIgnoreCase);

        if (!accepted || string.IsNullOrWhiteSpace(path))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        if (DataContext is DecryptViewModel viewModel)
            viewModel.SelectDroppedSource(path, item is IStorageFolder);

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void UpdateDragState(DragEventArgs e)
    {
        var item = e.DataTransfer.TryGetFile();
        var path = item?.TryGetLocalPath();
        var accepted = item is IStorageFolder ||
                       item is IStorageFile && string.Equals(
                           System.IO.Path.GetExtension(path), ".safe",
                           System.StringComparison.OrdinalIgnoreCase);
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.Background = accepted ? ActiveBackground : DefaultBackground;
        DropZone.BorderBrush = accepted ? ActiveBorder : DefaultBorder;
        e.Handled = true;
    }

    private void ResetDragState()
    {
        DropZone.Background = DefaultBackground;
        DropZone.BorderBrush = DefaultBorder;
    }
}
