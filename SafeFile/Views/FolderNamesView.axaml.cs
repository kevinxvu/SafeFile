using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using SafeFile.ViewModels;

namespace SafeFile.Views;
public partial class FolderNamesView : UserControl
{
    public FolderNamesView() => InitializeComponent();
    private void OnDragEnter(object? s, DragEventArgs e) => Update(s as Border, e);
    private void OnDragOver(object? s, DragEventArgs e) => Update(s as Border, e);
    private void OnDragLeave(object? s, DragEventArgs e) => Reset(s as Border);
    private async void OnDrop(object? s, DragEventArgs e)
    {
        Reset(s as Border);
        var paths = e.DataTransfer.TryGetFiles()?.Select(x => x.TryGetLocalPath()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray() ?? [];
        if (paths.Length == 1 && Directory.Exists(paths[0]) && DataContext is FolderNamesViewModel vm) { await vm.SelectFolderAsync(paths[0]); e.DragEffects = DragDropEffects.Copy; }
        else e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }
    private static void Update(Border? b, DragEventArgs e) { var ok = e.DataTransfer.TryGetFiles()?.Count(x => Directory.Exists(x.TryGetLocalPath())) == 1; e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None; if (b is not null) b.Classes.Set("drag-active", ok); e.Handled = true; }
    private static void Reset(Border? b) { if (b is not null) b.Classes.Set("drag-active", false); }
}
