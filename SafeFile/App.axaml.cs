using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SafeFile.Services;
using SafeFile.Core.IO;
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
                var isFirstRun = !File.Exists(settingsService.GetSettingsPath());
                var settings = settingsService.Load();
                if (isFirstRun)
                {
                    settings.Theme = GetInitialTheme();
                    try
                    {
                        settingsService.Save(settings);
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(
                            ex,
                            "Unable to persist first-run application settings");
                    }
                }
                LocalizationService.Instance.SetLanguage(settings.Language);
                RequestedThemeVariant = settings.Theme == "Dark"
                    ? ThemeVariant.Dark
                    : ThemeVariant.Light;
                var logService = LogService.Instance;
                var fileEncryptorLogger =
                    Program.LoggerFactory.CreateLogger<FileEncryptor>();
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(
                        filePicker, settingsService, errorDialog, logService,
                        clipboard, fileEncryptorLogger),
                };
            }

            base.OnFrameworkInitializationCompleted();
        }

        private string GetInitialTheme()
        {
            try
            {
                return PlatformSettings?.GetColorValues().ThemeVariant ==
                       PlatformThemeVariant.Light
                    ? "Light"
                    : "Dark";
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(
                    ex,
                    "Unable to detect the system theme; using Dark");
                return "Dark";
            }
        }
    }
}
