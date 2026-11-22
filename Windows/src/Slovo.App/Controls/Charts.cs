using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Slovo.App.Localization;
using Slovo.App.ViewModels;
using Slovo.Core.Model;

namespace Slovo.App.Controls;

/// <summary>A semicircle gauge for words per minute: the track, then the value in the accent.</summary>
public sealed class WpmGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<WpmGauge, double>(nameof(Value));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<WpmGauge, double>(nameof(Maximum), 200);
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<WpmGauge, IBrush?>(nameof(Track));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<WpmGauge, IBrush?>(nameof(Fill));

    static WpmGauge() => AffectsRender<WpmGauge>(ValueProperty, MaximumProperty, TrackProperty, FillProperty);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public override void Render(DrawingContext context)
    {
        const double thickness = 12;
        var radius = Math.Min(Bounds.Width / 2, Bounds.Height) - thickness / 2;
        if (radius <= 0)
        {
            return;
        }
        var center = new Point(Bounds.Width / 2, Bounds.Height - thickness / 2);
        DrawArc(context, center, radius, 0, 1, Track, thickness);
        var fraction = Maximum > 0 ? Math.Clamp(Value / Maximum, 0, 1) : 0;
        if (fraction > 0)
        {
            DrawArc(context, center, radius, 0, fraction, Fill, thickness);
        }
    }

    static void DrawArc(DrawingContext context, Point center, double radius, double from, double to, IBrush? brush, double thickness)
    {
        if (brush is null)
        {
            return;
        }
        Point At(double t) => new(center.X - radius * Math.Cos(Math.PI * t), center.Y - radius * Math.Sin(Math.PI * t));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(At(from), false);
            g.ArcTo(At(to), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(brush, thickness, lineCap: PenLineCap.Round), geometry);
    }
}

/// <summary>A GitHub-style calendar: one column per week, one cell per day, darker for more words.</summary>
public sealed class ActivityCalendar : Control
{
    public static readonly StyledProperty<IReadOnlyList<DailyWordCount>?> DaysProperty =
        AvaloniaProperty.Register<ActivityCalendar, IReadOnlyList<DailyWordCount>?>(nameof(Days));

    static ActivityCalendar() => AffectsRender<ActivityCalendar>(DaysProperty);

    public IReadOnlyList<DailyWordCount>? Days { get => GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    public const double Cell = 12;
    public const double Gap = 3;

    protected override Size MeasureOverride(Size availableSize)
    {
        var weeks = Math.Max(1, (int)Math.Ceiling((Days?.Count ?? 140) / 7.0));
        return new Size(weeks * (Cell + Gap) - Gap, 7 * (Cell + Gap) - Gap);
    }

    public override void Render(DrawingContext context)
    {
        var days = Days ?? [];
        var levels = new[] { "Slovo.CalendarEmpty", "Slovo.Calendar1", "Slovo.Calendar2", "Slovo.Calendar3", "Slovo.Calendar4" }
            .Select(key => this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null).ToArray();
        var max = days.Count == 0 ? 0 : days.Max(d => d.Words);
        for (var index = 0; index < days.Count; index++)
        {
            var day = days[index];
            var level = day.Words <= 0 || max <= 0 ? 0 : 1 + Math.Min(3, (int)(4.0 * day.Words / (max + 1)));
            var brush = levels[level];
            if (brush is not null)
            {
                context.DrawRectangle(brush, null, CellRect(days, index), 3, 3);
            }
        }
    }

    /// <summary>Where a day's cell is drawn.</summary>
    static Rect CellRect(IReadOnlyList<DailyWordCount> days, int index)
    {
        // Rows follow the weekday so a week reads down one column; Monday on top.
        var row = ((int)days[index].Day.DayOfWeek + 6) % 7;
        var column = (index + ((int)days[0].Day.DayOfWeek + 6) % 7) / 7;
        return new Rect(column * (Cell + Gap), row * (Cell + Gap), Cell, Cell);
    }

    /// <summary>The day whose cell is under a point, or null over a gap or outside the days.</summary>
    public DailyWordCount? DayAt(Point point)
    {
        var days = Days ?? [];
        for (var index = 0; index < days.Count; index++)
        {
            if (CellRect(days, index).Contains(point))
            {
                return days[index];
            }
        }
        return null;
    }

    /// <summary>"412 words on September 15, 2026" / «412 слов · 15 сентября 2026 г.», on hover over a day.</summary>
    public static string Describe(DailyWordCount day)
    {
        var loc = Loc.Instance;
        return loc.Format("insights.calendar.tooltip", loc.Plural("plural.words", day.Words), Formatting.LongDate(day.Day));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var tip = DayAt(e.GetPosition(this)) is { } day ? Describe(day) : null;
        if (!Equals(ToolTip.GetTip(this), tip))
        {
            ToolTip.SetTip(this, tip);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetTip(this, null);
    }
}

/// <summary>A thin horizontal bar filled to a fraction.</summary>
public sealed class ShareBar : Control
{
    public static readonly StyledProperty<double> FractionProperty = AvaloniaProperty.Register<ShareBar, double>(nameof(Fraction));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<ShareBar, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<ShareBar, IBrush?>(nameof(Track));

    static ShareBar() => AffectsRender<ShareBar>(FractionProperty, FillProperty, TrackProperty);

    public double Fraction { get => GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public ShareBar() => Height = 6;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Track is not null)
        {
            context.DrawRectangle(Track, null, bounds, 3, 3);
        }
        var width = bounds.Width * Math.Clamp(Fraction, 0, 1);
        if (Fill is not null && width > 0)
        {
            context.DrawRectangle(Fill, null, new Rect(0, 0, Math.Max(width, 6), bounds.Height), 3, 3);
        }
    }
}
