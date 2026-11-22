using Avalonia;
using Avalonia.Controls;
using Slovo.App.ViewModels.Settings;

namespace Slovo.App.Views.Settings;

public partial class PrivacySettingsView : UserControl
{
    Window? window;

    public PrivacySettingsView() => InitializeComponent();

    // The microphone switch changes in Windows Settings while this page waits; look again on return.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        window = TopLevel.GetTopLevel(this) as Window;
        if (window is not null)
        {
            window.Activated += OnWindowActivated;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (window is not null)
        {
            window.Activated -= OnWindowActivated;
            window = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    void OnWindowActivated(object? sender, EventArgs e) => (DataContext as PrivacySettingsViewModel)?.RefreshPermissions();
}
