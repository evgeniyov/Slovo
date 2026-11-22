using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Slovo.App.FlowBar;
using Slovo.Core.Model;

namespace Slovo.App.Tests.Rendering;

public class FlowBarSnapshotTests
{
    public static TheoryData<string> States => ["idle", "pointer", "hint", "push", "handsfree", "processing", "loading", "copied", "nothingHeard", "failure", "moment"];

    [AvaloniaTheory]
    [MemberData(nameof(States))]
    public void RendersEveryState(string state)
    {
        // A snapshot shows the shape the bar settles into, not the first frame of its spring.
        ReducedMotion.Override = true;
        var model = new FlowBarViewModel();
        var info = new RecordingInfo(DictationMode.PushToTalk, 0, DictationTarget.Foreground);
        switch (state)
        {
            case "idle": model.Apply(DictationState.IdleState, null, null, true, true, DictationKey.CtrlWin); break;
            case "pointer": model.Apply(DictationState.IdleState, null, null, true, true, DictationKey.CtrlWin); model.SetHoveringAfterDwell(true); break;
            case "hint": model.Apply(DictationState.IdleState, null, null, true, true, DictationKey.CtrlWin); model.SetHovering(true); break;
            case "push": model.Apply(new DictationState.Recording(info), null, null, true, true, DictationKey.CtrlWin); model.Level = 0.08f; break;
            case "handsfree": model.Apply(new DictationState.Recording(info with { Mode = DictationMode.HandsFree }), 24, null, true, true, DictationKey.CtrlWin); model.Level = 0.05f; break;
            case "processing": model.Apply(new DictationState.Processing(false), null, null, true, true, DictationKey.CtrlWin); break;
            case "loading": model.Apply(new DictationState.Processing(true), null, null, true, true, DictationKey.CtrlWin); break;
            case "copied": model.Apply(new DictationState.Message(FlowMessage.Copied), null, null, true, true, DictationKey.CtrlWin); break;
            case "nothingHeard": model.Apply(new DictationState.Message(FlowMessage.NothingHeard), null, "Микрофон (Realtek Audio)", true, true, DictationKey.CtrlWin); break;
            case "failure": model.Apply(new DictationState.Message(FlowMessage.For(DictationFailure.ModelMissing(EngineId.GigaamRnnt))), null, null, true, true, DictationKey.CtrlWin); break;
            case "moment": model.ShowMarkedMoment(760); break;
        }
        var view = new FlowBarView { DataContext = model, Margin = new Avalonia.Thickness(12) };
        Snapshot.Render(view, "flowbar-" + state, 380, 70);
        Snapshot.Render(new FlowBarView { DataContext = model, Margin = new Avalonia.Thickness(12) }, "flowbar-" + state, 380, 70, ThemeVariant.Dark);
    }

    [AvaloniaFact]
    public void ThePointerNudgesAtOnceAndTheHintOpensAfterTheDwell()
    {
        var model = new FlowBarViewModel();
        model.Apply(DictationState.IdleState, null, null, true, true, DictationKey.CtrlWin);
        model.SetHoveringAfterDwell(true);
        Assert.True(model.IsPointerInside);
        Assert.Equal(FlowBarShape.Idle, model.Shape);
        // Let the dispatcher run its timers for a little longer than the dwell.
        using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(FlowBarViewModel.HoverDwell + 0.3)))
        {
            Avalonia.Threading.Dispatcher.UIThread.MainLoop(wait.Token);
        }
        Assert.Equal(FlowBarShape.Hint, model.Shape);
        model.SetHoveringAfterDwell(false);
        Assert.False(model.IsPointerInside);
        Assert.Equal(FlowBarShape.Idle, model.Shape);
    }
}
