using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SafeFile.ViewModels;

namespace SafeFile.Views;

public partial class EncryptView : UserControl
{
    private static readonly IBrush DefaultDropBackground = Brush.Parse("#F5F5F5");
    private static readonly IBrush ActiveDropBackground = Brush.Parse("#DBEAFE");
    private static readonly IBrush DefaultDropBorder = Brush.Parse("#BDBDBD");
    private static readonly IBrush ActiveDropBorder = Brush.Parse("#2563EB");

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
            border.Background = acceptsDrop ? ActiveDropBackground : DefaultDropBackground;
            border.BorderBrush = acceptsDrop ? ActiveDropBorder : DefaultDropBorder;
        }

        e.Handled = true;
    }

    private static void ResetDragState(Border? border)
    {
        if (border is null)
            return;

        border.Background = DefaultDropBackground;
        border.BorderBrush = DefaultDropBorder;
    }
}
