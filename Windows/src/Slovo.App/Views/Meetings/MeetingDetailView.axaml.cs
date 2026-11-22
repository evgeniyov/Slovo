using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Slovo.App.ViewModels.Meetings;

namespace Slovo.App.Views.Meetings;

public partial class MeetingDetailView : UserControl
{
    public MeetingDetailView()
    {
        InitializeComponent();
        // The card holds keyboard focus so the left/right arrows reach playback without a
        // click first, as on the Mac; the title field takes it while renaming and hands it back.
        Focusable = true;
        FocusAdorner = null;
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Focus());
        // Tunnelling, so the transcript list and the buttons in the card do not get the arrows first.
        AddHandler(KeyDownEvent, OnArrowKey, RoutingStrategies.Tunnel);
        // The title is saved when editing ends: Enter or leaving the field (as on macOS).
        TitleBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                (DataContext as MeetingDetailViewModel)?.CommitTitleCommand.Execute(null);
                Focus();
                e.Handled = true;
            }
        };
        TitleBox.LostFocus += (_, _) => (DataContext as MeetingDetailViewModel)?.CommitTitleCommand.Execute(null);
        TranscriptList.SelectionChanged += (_, _) =>
        {
            if (TranscriptList.SelectedItem is { } item)
            {
                TranscriptList.ScrollIntoView(item);
            }
        };
    }

    /// <summary>Left/right move the playhead 5 seconds, unless a text field wants them for its caret.</summary>
    void OnArrowKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right) || e.KeyModifiers != KeyModifiers.None
            || (e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) is not null
            || DataContext is not MeetingDetailViewModel model)
        {
            return;
        }
        e.Handled = model.Playback.Nudge(e.Key == Key.Left ? -PlaybackBarViewModel.SeekStep : PlaybackBarViewModel.SeekStep);
    }
}
