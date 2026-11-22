using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform;
using Slovo.App.Native;

namespace Slovo.App.FlowBar;

/// <summary>
/// The borderless, top-most window that carries the Flow bar. It is sized to the pill, so
/// its transparent rest never swallows clicks meant for the windows below, and sits a few
/// pixels above the taskbar of the screen the user works on.
/// </summary>
public sealed class FlowBarWindow : Window
{
    /// <summary>Distance above the taskbar (or the bottom of the screen when it hides).</summary>
    const double TaskbarGap = 3;
    /// <summary>Room around the pill for its shadow.</summary>
    const double PillMargin = 8;

    readonly FlowBarViewModel model;
    Func<Screen?>? screenProvider;

    public FlowBarWindow(FlowBarViewModel model)
    {
        this.model = model;
        DataContext = model;
        Content = new FlowBarView { Margin = new Thickness(PillMargin) };
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Title = "Slovo";
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlowBarViewModel.Shape))
            {
                UpdateVisibility();
            }
        };
        Opened += (_, _) => WindowStyles.MakeNonActivatingOverlay(this);
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>The screen to sit on; the primary screen when not set or unknown.</summary>
    public void SetScreenProvider(Func<Screen?> provider) => screenProvider = provider;

    public void UpdateVisibility()
    {
        hideTimer?.Stop();
        hideTimer = null;
        if (model.Shape == FlowBarShape.Hidden)
        {
            if (!IsVisible)
            {
                return;
            }
            if (ReducedMotion.IsOn)
            {
                Hide();
                return;
            }
            // The capsule shrinks away first; the window goes once it has.
            hideTimer = new DispatcherTimer { Interval = FlowBarView.ShrinkDelay };
            hideTimer.Tick += (_, _) =>
            {
                hideTimer?.Stop();
                hideTimer = null;
                if (model.Shape == FlowBarShape.Hidden)
                {
                    Hide();
                }
            };
            hideTimer.Start();
            return;
        }
        if (!IsVisible)
        {
            Show();
        }
        Reposition();
    }

    DispatcherTimer? hideTimer;

    public void Reposition()
    {
        var screen = screenProvider?.Invoke() ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }
        var scaling = screen.Scaling;
        var area = screen.WorkingArea;
        var width = (int)Math.Ceiling(Bounds.Width * scaling);
        var height = (int)Math.Ceiling(Bounds.Height * scaling);
        var x = area.X + (area.Width - width) / 2;
        var y = area.Bottom - height - (int)Math.Round((TaskbarGap - PillMargin) * scaling);
        Position = new PixelPoint(x, Math.Min(y, area.Bottom - height));
    }
}
