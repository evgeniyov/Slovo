using Avalonia.Controls;
using Avalonia.Interactivity;
using Slovo.App.ViewModels.Meetings;

namespace Slovo.App.Views.Meetings;

public partial class MeetingsView : UserControl
{
    public MeetingsView() => InitializeComponent();

    /// <summary>The screen picker lists the apps running now, every time it opens.</summary>
    void OnScreenChoiceOpening(object? sender, EventArgs e) => (DataContext as MeetingsViewModel)?.ScreenChoice.Refresh();

    /// <summary>Picking what to record closes the picker, as on macOS.</summary>
    void OnScreenTargetClick(object? sender, RoutedEventArgs e) => this.FindControl<Button>("ScreenButton")?.Flyout?.Hide();
}
