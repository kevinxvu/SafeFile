using Avalonia;
using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SafeFile.Services;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace SafeFile
{
    internal sealed class Program
    {
        internal static ILoggerFactory LoggerFactory { get; private set; } =
            NullLoggerFactory.Instance;

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            ConfigureLogging();
            try
            {
                Log.Information(
                    "SafeFile application starting on {OperatingSystem}",
                    Environment.OSVersion);
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                Log.Information("SafeFile application stopped normally");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "SafeFile application terminated unexpectedly");
                throw;
            }
            finally
            {
                LoggerFactory.Dispose();
                Log.CloseAndFlush();
            }
        }

        private static void ConfigureLogging()
        {
            var logService = LogService.Instance;
            SelfLog.Enable(message => Debug.WriteLine($"Serilog: {message}"));

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Avalonia", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "SafeFile")
                .WriteTo.Async(sink => sink.File(
                    logService.LogFilePattern,
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    retainedFileCountLimit: 30,
                    outputTemplate:
                        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] " +
                        "[{Level:u3}] {Message:lj}{NewLine}{Exception}"))
                .WriteTo.Sink(new UiLogSink(logService))
                .CreateLogger();

            LoggerFactory = new SerilogLoggerFactory(
                Log.Logger,
                dispose: false);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
#if DEBUG
                .WithDeveloperTools()
#endif
                .WithInterFont()
                .LogToTrace();
    }
}
