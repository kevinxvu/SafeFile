using Avalonia.Interactivity;

namespace SafeFile.Views;

public partial class ErrorDialog : Avalonia.Controls.Window
{
    public ErrorDialog()
    {
        InitializeComponent();
    }

    public ErrorDialog(string title, string message)
        : this()
    {
        Title = title;
        DialogTitle.Text = title;
        DialogMessage.Text = message;
    }

    private void CloseDialog(object? sender, RoutedEventArgs e) => Close();
}
