using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using SafeFile.Views;

namespace SafeFile.Services;

public sealed class ErrorDialogService : IErrorDialogService
{
    public async Task ShowErrorAsync(string message, string title = "Đã xảy ra lỗi")
    {
        var owner = (Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        var dialog = new ErrorDialog(title, message);
        if (owner is not null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }
}
