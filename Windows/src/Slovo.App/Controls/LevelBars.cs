using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Slovo.App.Controls;

/// <summary>
/// Eleven bars that follow the input level with a gentle ripple (the macOS LevelBars):
/// the square root spreads speech RMS over the height, an envelope keeps the middle tallest.
/// </summary>
public sealed class LevelBars : Control
{
    public static readonly StyledProperty<float> LevelProperty =
        AvaloniaProperty.Register<LevelBars, float>(nameof(Level));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<LevelBars>();

    /// <summary>Freezes the ripple, for screenshots and reduced motion.</summary>
    public static readonly StyledProperty<bool> IsStillProperty =
        AvaloniaProperty.Register<LevelBars, bool>(nameof(IsStill));

    const int BarCount = 11;
    const double BarWidth = 3;
    const double Spacing = 2.5;
    readonly DispatcherTimer timer;
    double shownLevel;

    static LevelBars()
    {
        AffectsRender<LevelBars>(LevelProperty, ForegroundProperty);
    }

    public LevelBars()
    {
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1.0 / 30), DispatcherPriority.Render, (_, _) => InvalidateVisual());
        Width = BarCount * BarWidth + (BarCount - 1) * Spacing;
        Height = 20;
    }

    public float Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public bool IsStill
    {
        get => GetValue(IsStillProperty);
        set => SetValue(IsStillProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!IsStill)
        {
            timer.Start();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        var brush = Foreground ?? Brushes.White;
        // Ease towards the level so the bars don't jump between meter updates.
        shownLevel += (Math.Max(0, Level) - shownLevel) * 0.45;
        var amplitude = Math.Min(1, Math.Sqrt(shownLevel) * 1.3);
        var phase = IsStill ? 0.9 : Environment.TickCount64 / 1000.0;
        for (var index = 0; index < BarCount; index++)
        {
            var distance = Math.Abs(index - 5) / 5.0;
            var envelope = 1 - 0.62 * distance * distance;
            var wobble = 0.7 + 0.3 * Math.Sin(phase * 7.5 + index * 1.35);
            var height = Math.Max(3, 3 + 17 * amplitude * envelope * wobble);
            var x = index * (BarWidth + Spacing);
            var y = (Bounds.Height - height) / 2;
            context.DrawRectangle(brush, null, new Rect(x, y, BarWidth, height), BarWidth / 2, BarWidth / 2);
        }
    }
}

/// <summary>Three pulsing dots while a dictation is being transcribed.</summary>
public sealed class ProcessingDots : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<ProcessingDots>();

    public static readonly StyledProperty<bool> IsStillProperty =
        AvaloniaProperty.Register<ProcessingDots, bool>(nameof(IsStill));

    readonly DispatcherTimer timer;

    public ProcessingDots()
    {
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1.0 / 30), DispatcherPriority.Render, (_, _) => InvalidateVisual());
        Width = 25;
        Height = 6;
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public bool IsStill
    {
        get => GetValue(IsStillProperty);
        set => SetValue(IsStillProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!IsStill)
        {
            timer.Start();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        var phase = IsStill ? 0.4 : Environment.TickCount64 / 1000.0;
        for (var index = 0; index < 3; index++)
        {
            var pulse = 0.35 + 0.65 * Math.Max(0, Math.Sin(phase * 4.5 - index * 0.9));
            var brush = Foreground ?? Brushes.White;
            using (context.PushOpacity(pulse))
            {
                context.DrawEllipse(brush, null, new Point(2.5 + index * 10, 3), 2.5, 2.5);
            }
        }
    }
}
