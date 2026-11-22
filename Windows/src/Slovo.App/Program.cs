using Avalonia;
using Slovo.Core;
using Slovo.Core.Logging;

namespace Slovo.App;

static class Program
{
    /// <summary>
    /// <c>Slovo.exe</c> starts the tray app; <c>Slovo.exe --mcp</c> runs the headless MCP
    /// server Claude starts over stdio, before anything of the interface exists (no window,
    /// tray icon, store, engine or capture), and never writes to Slovo's data folder or
    /// settings.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppPaths.Standard;
        if (args.Contains("--mcp"))
        {
            return McpMode.Run(paths);
        }

        Log.Configure(paths.LogsFolder);
        var logger = Log.For("App");
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // Another Slovo is running: ask it to show its window and leave.
            SingleInstance.SignalRunningInstance();
            return 0;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            logger.Error("Unhandled exception: " + e.ExceptionObject);
            Log.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        logger.Info($"Slovo {AppInfo.Version} starting");
        try
        {
            App.StartupPaths = paths;
            App.StartedAtSignIn = args.Contains(Slovo.Platform.Windows.Shell.AutostartEntry.Argument);
            App.SingleInstanceHandle = instance;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            logger.Info("Slovo stopped");
            Log.Flush();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
