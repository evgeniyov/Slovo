using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Slovo.App.Views;

/// <summary>Slovo's mark, drawn like the app icon: a warm dark tile with seven sound bars, the middle one orange.</summary>
public sealed class AppLogo : Control
{
    static readonly IBrush Tile = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = [new GradientStop(Color.Parse("#2A231F"), 0), new GradientStop(Color.Parse("#4A3A30"), 1)],
    };

    static readonly IBrush Bar = new SolidColorBrush(Color.Parse("#F2EDE4"));
    static readonly IBrush AccentBar = new SolidColorBrush(Color.Parse("#F4A259"));

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var radius = size * 0.225;
        context.DrawRectangle(Tile, null, new Rect(0, 0, size, size), radius, radius);
        double[] heights = size >= 32 ? [0.20, 0.36, 0.54, 0.70, 0.54, 0.36, 0.20] : [0.30, 0.55, 0.78, 0.55, 0.30];
        var barWidth = size * (heights.Length == 7 ? 0.074 : 0.12);
        var gap = size * (heights.Length == 7 ? 0.046 : 0.07);
        var total = heights.Length * barWidth + (heights.Length - 1) * gap;
        var x = (size - total) / 2;
        for (var index = 0; index < heights.Length; index++)
        {
            var height = size * heights[index];
            var brush = index == heights.Length / 2 ? AccentBar : Bar;
            context.DrawRectangle(brush, null, new Rect(x, (size - height) / 2, barWidth, height), barWidth / 2, barWidth / 2);
            x += barWidth + gap;
        }
    }
}
