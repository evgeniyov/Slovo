using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Slovo.App.Controls;

/// <summary>
/// A line icon from Themes/Icons.axaml: geometry on a 24×24 grid, scaled to the control's
/// size and stroked in the current foreground with round caps.
/// </summary>
public sealed class StrokeIcon : Control
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<StrokeIcon, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<StrokeIcon>();

    /// <summary>Stroke width in grid units (a 16-pixel icon draws 1.8 × 16/24 ≈ 1.2 px lines).</summary>
    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<StrokeIcon, double>(nameof(StrokeWidth), 1.9);

    public static readonly StyledProperty<bool> FilledProperty =
        AvaloniaProperty.Register<StrokeIcon, bool>(nameof(Filled));

    static StrokeIcon()
    {
        AffectsRender<StrokeIcon>(IconProperty, ForegroundProperty, StrokeWidthProperty, FilledProperty);
        WidthProperty.OverrideDefaultValue<StrokeIcon>(16);
        HeightProperty.OverrideDefaultValue<StrokeIcon>(16);
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    /// <summary>Fills the shape too (a starred moment, the play triangle).</summary>
    public bool Filled
    {
        get => GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Icon is not { } geometry || Foreground is not { } brush)
        {
            return;
        }
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        if (scale <= 0)
        {
            return;
        }
        var offsetX = (Bounds.Width - 24 * scale) / 2;
        var offsetY = (Bounds.Height - 24 * scale) / 2;
        var pen = new Pen(brush, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            context.DrawGeometry(Filled ? brush : null, pen, geometry);
        }
    }
}
