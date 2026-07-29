using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SafeFile.Services;
using SafeFile.Core.Services;
using SafeFile.ViewModels;
using SafeFile.Views;

namespace SafeFile
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var filePicker = new FilePickerService();
                var settingsService = new SettingsService();
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(filePicker, settingsService),
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
