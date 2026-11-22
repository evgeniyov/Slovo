using Avalonia.Controls;
using Avalonia.Threading;

namespace Slovo.App.Views.Meetings;

public partial class SpeakerEditorView : UserControl
{
    public SpeakerEditorView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        });
    }
}
