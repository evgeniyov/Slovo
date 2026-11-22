using Avalonia;
using Avalonia.Headless;
using Slovo.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Slovo.App.Tests;

public static class TestAppBuilder
{
    // Skia drawing instead of the headless stub, so views can be rendered to PNG.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Slovo.App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
