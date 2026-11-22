using Avalonia.Controls;
using Avalonia.Threading;

namespace Slovo.App.Views.Meetings;

public partial class HighlightNoteView : UserControl
{
    public HighlightNoteView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => NoteBox.Focus());
    }
}
