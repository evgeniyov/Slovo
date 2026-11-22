using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;

namespace Slovo.App.Tests.Rendering;

/// <summary>
/// Renders controls to PNG files for looking at the interface without Windows. Files go to
/// SLOVO_SNAPSHOT_DIR when it is set; otherwise rendering still runs (catching binding and
/// layout exceptions) but nothing is written.
/// </summary>
public static class Snapshot
{
    public static string? Folder => Environment.GetEnvironmentVariable("SLOVO_SNAPSHOT_DIR");

    public static void Render(Control content, string name, double width, double height, ThemeVariant? theme = null)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = content,
            RequestedThemeVariant = theme ?? ThemeVariant.Light,
        };
        window.Background = Application.Current?.TryGetResource("Slovo.WindowBackground", window.RequestedThemeVariant, out var brush) == true
            ? brush as Avalonia.Media.IBrush
            : null;
        RenderWindow(window, name, width, height, theme);
    }

    /// <summary>Renders a whole window (its own templates, data context and background).</summary>
    public static void RenderWindow(Window window, string name, double width, double height, ThemeVariant? theme = null)
    {
        window.Width = width;
        window.Height = height;
        window.RequestedThemeVariant = theme ?? ThemeVariant.Light;
        window.Show();
        try
        {
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (Folder is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                var suffix = window.RequestedThemeVariant == ThemeVariant.Dark ? "-dark" : "";
                frame.Save(Path.Combine(folder, name + suffix + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        finally
        {
            window.Close();
        }
    }
}
