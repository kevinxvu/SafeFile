using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using SafeFile.Views;

namespace SafeFile.Services;

public sealed class ErrorDialogService : IErrorDialogService
{
    public async Task ShowErrorAsync(string message, string? title = null)
    {
        var owner = (Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        var dialog = new ErrorDialog(
            title ?? LocalizationService.Instance.Get("ErrorTitle"), message);
        if (owner is not null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }
}
