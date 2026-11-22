using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Slovo.App.Localization;
using Slovo.App.Tray;
using Slovo.Core.Localization;
using Slovo.Core.Model;

namespace Slovo.App.Tests.Tray;

/// <summary>The tray menu against the macOS menu bar menu (StatusItemController).</summary>
public class TrayMenuTests
{
    public TrayMenuTests() => CoreStrings.SetLanguage(AppLanguage.En);

    sealed class FakeTrayActions : ITrayActions
    {
        public List<string> Calls { get; } = [];
        public void StartHandsFree() => Calls.Add(nameof(StartHandsFree));
        public void FinishDictation() => Calls.Add(nameof(FinishDictation));
        public void CancelDictation() => Calls.Add(nameof(CancelDictation));
        public void PasteLast() => Calls.Add(nameof(PasteLast));
        public void NewMeeting() => Calls.Add(nameof(NewMeeting));
        public void StopMeeting() => Calls.Add(nameof(StopMeeting));
        public void MarkMoment() => Calls.Add(nameof(MarkMoment));
        public void ToggleMeetingMicrophone() => Calls.Add(nameof(ToggleMeetingMicrophone));
        public void OpenMainWindow() => Calls.Add(nameof(OpenMainWindow));
        public void OpenSettings() => Calls.Add(nameof(OpenSettings));
        public void ShowShortcuts() => Calls.Add(nameof(ShowShortcuts));
        public void ShowTroubleshooting() => Calls.Add(nameof(ShowTroubleshooting));
        public void OpenModelsFolder() => Calls.Add(nameof(OpenModelsFolder));
        public void OpenLogs() => Calls.Add(nameof(OpenLogs));
        public void ShowOnboarding() => Calls.Add(nameof(ShowOnboarding));
        public void Quit() => Calls.Add(nameof(Quit));
    }

    static NativeMenu Menu(TrayState state, ITrayActions? actions = null)
    {
        var menu = new NativeMenu();
        TrayController.Fill(menu, state, actions ?? new FakeTrayActions(), Loc.Instance);
        return menu;
    }

    static string[] Titles(NativeMenu menu) =>
        menu.Items.Select(item => item is NativeMenuItemSeparator ? "-" : (item as NativeMenuItem)?.Header ?? "").ToArray();

    static string T(string key) => Loc.Instance.Get(key);

    /// <summary>Clicks an item the way the notification area's menu does.</summary>
    static void Click(NativeMenuItem item)
    {
        var raise = item.GetType().GetInterfaces()
            .Select(i => i.GetMethod("RaiseClicked"))
            .First(m => m is not null)!;
        raise.Invoke(item, null);
    }

    [AvaloniaFact]
    public void TheFirstLineSaysHowToDictateOrWhatIsGoingOn()
    {
        var loc = Loc.Instance;
        Assert.Equal("Hold Ctrl + Win to dictate", TrayController.StatusLine(new TrayState(), loc));
        Assert.Equal("Hold Right Alt to dictate", TrayController.StatusLine(new TrayState(DictationKey: DictationKey.RightAlt), loc));
        Assert.Equal(T("flowbar.meetingInProgress"), TrayController.StatusLine(new TrayState(IsMeetingRecording: true), loc));
        Assert.Equal(T("menu.status.recording"), TrayController.StatusLine(new TrayState(IsRecording: true), loc));
        Assert.Equal(T("menu.status.processing"), TrayController.StatusLine(new TrayState(IsProcessing: true), loc));

        var first = (NativeMenuItem)Menu(new TrayState()).Items[0];
        Assert.False(first.IsEnabled);
    }

    [AvaloniaFact]
    public void IdleFollowsTheMacOrder()
    {
        Assert.Equal(
            ["Hold Ctrl + Win to dictate", "-", T("menu.startHandsFree"), T("menu.pasteLast"), "-", T("menu.newMeeting"), "-",
             T("menu.openSlovo"), T("menu.settings"), T("sidebar.help"), "-", T("menu.quit")],
            Titles(Menu(new TrayState())));
    }

    [AvaloniaFact]
    public void HelpHasTheModelsFolderAndOnboarding()
    {
        var actions = new FakeTrayActions();
        var menu = Menu(new TrayState(), actions);
        var help = menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == T("sidebar.help")).Menu!;
        Assert.Equal(
            [T("help.shortcuts"), T("help.troubleshooting"), T("help.openModels"), T("help.openLogs"), "-", T("help.showOnboarding")],
            Titles(help));
        foreach (var item in help.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator))
        {
            Click(item);
        }
        Assert.Equal(["ShowShortcuts", "ShowTroubleshooting", "OpenModelsFolder", "OpenLogs", "ShowOnboarding"], actions.Calls);
    }

    [AvaloniaFact]
    public void AMeetingPutsStopFirstAndShowsTheMicrophoneSwitch()
    {
        var titles = Titles(Menu(new TrayState(IsMeetingRecording: true, IsMeetingMicrophoneOn: false,
            MarkMomentShortcut: "Ctrl+Alt+M", MicrophoneShortcut: "Ctrl+Alt+U")));
        Assert.Equal(
            [T("flowbar.meetingInProgress"), "-", T("menu.startHandsFree"), T("menu.pasteLast"), "-",
             T("menu.stopMeeting"), T("menu.recordMicrophone") + "\tCtrl+Alt+U", T("menu.markMoment") + "\tCtrl+Alt+M", "-",
             T("menu.openSlovo"), T("menu.settings"), T("sidebar.help"), "-", T("menu.quit")],
            titles);
        var menu = Menu(new TrayState(IsMeetingRecording: true, IsMeetingMicrophoneOn: false));
        Assert.False(menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == T("menu.startHandsFree")).IsEnabled);
        Assert.False(menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == T("menu.recordMicrophone")).IsChecked);
    }

    [AvaloniaFact]
    public void RecordingOffersToFinishOrCancel()
    {
        Assert.Equal(
            [T("menu.status.recording"), "-", T("menu.finishDictation"), T("menu.cancelDictation"), T("menu.pasteLast"), "-"],
            Titles(Menu(new TrayState(IsRecording: true, IsHandsFree: true)))[..6]);
        Assert.Equal(
            [T("menu.status.processing"), "-", T("menu.cancelDictation"), T("menu.pasteLast"), "-"],
            Titles(Menu(new TrayState(IsProcessing: true)))[..5]);
    }
}
