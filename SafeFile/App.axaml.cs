using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
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
                var errorDialog = new ErrorDialogService();
                var clipboard = new ClipboardService();
                var settingsService = new SettingsService();
                var settings = settingsService.Load();
                LocalizationService.Instance.SetLanguage(settings.Language);
                RequestedThemeVariant = settings.Theme == "Dark"
                    ? ThemeVariant.Dark
                    : ThemeVariant.Light;
                var logService = LogService.Instance;
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(
                        filePicker, settingsService, errorDialog, logService,
                        clipboard),
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
