using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Slovo.App.FlowBar;

/// <summary>
/// A damped spring as an easing curve: quick, with one small overshoot at the end — the
/// macOS bar's <c>.spring(duration: 0.42, bounce: 0.26)</c>.
/// </summary>
public sealed class SpringEasing : Easing
{
    const double Damping = 0.62;
    const double Frequency = 9.5;

    public override double Ease(double progress)
    {
        if (progress >= 1)
        {
            return 1;
        }
        var damped = Frequency * Math.Sqrt(1 - Damping * Damping);
        var decay = Math.Exp(-Damping * Frequency * progress);
        return 1 - decay * (Math.Cos(damped * progress) + Damping * Frequency / damped * Math.Sin(damped * progress));
    }
}

/// <summary>Whether Windows asks for fewer animations ("Animation effects" off in Settings → Accessibility).</summary>
public static class ReducedMotion
{
    /// <summary>Tests turn animations off so a snapshot shows the final shape.</summary>
    public static bool? Override { get; set; }

    public static bool IsOn => Override ?? (OperatingSystem.IsWindows() && !ClientAreaAnimationEnabled());

    static bool ClientAreaAnimationEnabled()
    {
        try
        {
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0) == 0 || enabled != 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }
    }

    const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    static extern int SystemParametersInfo(uint action, uint param, out int value, uint winIni);
}

/// <summary>
/// Lays each child out at its own desired size, centred, whatever its own size: the bar's
/// content keeps its shape while the capsule around it grows or shrinks, and the capsule
/// clips what does not fit yet — a reveal instead of text reflowing as the bar opens.
/// </summary>
public sealed class CenterOverflowPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size();
        foreach (var child in Children)
        {
            child.Measure(Size.Infinity);
            size = new Size(Math.Max(size.Width, child.DesiredSize.Width), Math.Max(size.Height, child.DesiredSize.Height));
        }
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            var desired = child.DesiredSize;
            child.Arrange(new Rect((finalSize.Width - desired.Width) / 2, (finalSize.Height - desired.Height) / 2, desired.Width, desired.Height));
        }
        return finalSize;
    }
}
