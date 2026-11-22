using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Slovo.Core;

namespace Slovo.App;

public partial class App : Application
{
    /// <summary>Set by Program before Avalonia starts; tests leave it null.</summary>
    internal static AppPaths? StartupPaths { get; set; }

    internal static SingleInstance? SingleInstanceHandle { get; set; }

    /// <summary>True when Windows started Slovo at sign-in: stay in the tray.</summary>
    internal static bool StartedAtSignIn { get; set; }

    internal Composition.AppController? Controller { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && StartupPaths is { } paths)
        {
            var controller = new Composition.AppController(this, paths, () => desktop.Shutdown());
            Controller = controller;
            desktop.ShutdownRequested += (_, _) => controller.Shutdown();
            controller.Start(StartedAtSignIn);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
