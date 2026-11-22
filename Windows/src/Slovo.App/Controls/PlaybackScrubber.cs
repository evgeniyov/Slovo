using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Slovo.App.Controls;

/// <summary>
/// A draggable, click-to-seek progress bar (the macOS PlaybackScrubber), drawn the way
/// <see cref="ShareBar"/> is. While the pointer is down the bar follows it; letting go runs
/// <see cref="Command"/> with the fraction (0–1) it was let go at. The bar is 6 px tall, in a
/// taller strip that takes the clicks.
/// </summary>
public sealed class PlaybackScrubber : Control
{
    public static readonly StyledProperty<double> FractionProperty = AvaloniaProperty.Register<PlaybackScrubber, double>(nameof(Fraction));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<PlaybackScrubber, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<PlaybackScrubber, IBrush?>(nameof(Track));
    public static readonly StyledProperty<ICommand?> CommandProperty = AvaloniaProperty.Register<PlaybackScrubber, ICommand?>(nameof(Command));

    const double BarHeight = 6;
    double? dragFraction;

    static PlaybackScrubber() => AffectsRender<PlaybackScrubber>(FractionProperty, FillProperty, TrackProperty);

    public PlaybackScrubber()
    {
        Height = 16;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public double Fraction { get => GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    /// <summary>Runs with the fraction (a double) where the pointer was let go.</summary>
    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    /// <summary>What the bar shows: where it is being dragged, else the playhead.</summary>
    public double ShownFraction => Math.Clamp(dragFraction ?? Fraction, 0, 1);

    public override void Render(DrawingContext context)
    {
        var top = Math.Max(0, (Bounds.Height - BarHeight) / 2);
        var height = Math.Min(BarHeight, Bounds.Height);
        var radius = height / 2;
        // Transparent, not nothing: the whole strip takes clicks, not only the thin bar.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Track is not null)
        {
            context.DrawRectangle(Track, null, new Rect(0, top, Bounds.Width, height), radius, radius);
        }
        if (Fill is not null && Bounds.Width > 0)
        {
            var width = Math.Min(Bounds.Width, Math.Max(height, Bounds.Width * ShownFraction));
            context.DrawRectangle(Fill, null, new Rect(0, top, width, height), radius, radius);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        e.Pointer.Capture(this);
        dragFraction = FractionAt(e.GetPosition(this).X);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragFraction is null)
        {
            return;
        }
        dragFraction = FractionAt(e.GetPosition(this).X);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragFraction is null)
        {
            return;
        }
        var result = FractionAt(e.GetPosition(this).X);
        dragFraction = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
        e.Handled = true;
        if (Command?.CanExecute(result) == true)
        {
            Command.Execute(result);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (dragFraction is not null)
        {
            dragFraction = null;
            InvalidateVisual();
        }
    }

    double FractionAt(double x) => Bounds.Width > 0 ? Math.Clamp(x / Bounds.Width, 0, 1) : 0;
}
