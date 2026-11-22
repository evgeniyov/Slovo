using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Slovo.App.Tests.Fakes;
using Slovo.App.ViewModels;
using Slovo.App.ViewModels.Meetings;
using Slovo.App.ViewModels.Settings;
using Slovo.Core;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.App.Tests.Rendering;

/// <summary>
/// Screen recording on the Meetings screen (MeetingsScreen.swift's ScreenCaptureButton,
/// ScreenCapturePopover and RecordingPanel, and ScreenRecordingPlayer on the card), and its
/// switch in Settings → Meetings.
/// </summary>
public class ScreenRecordingTests
{
    static readonly AppIdentity[] RunningApps = [new("ms-teams.exe", "Microsoft Teams"), new("zoom.exe", "Zoom")];

    public ScreenRecordingTests() => CoreStrings.SetLanguage(AppLanguage.En);

    static (Views.MainWindow Window, MainWindowViewModel Shell, MeetingsViewModel Model) Screen(MeetingFixture fixture, IReadOnlyList<AppIdentity>? apps = null)
    {
        var context = fixture.Context with { CapturableApps = () => apps ?? RunningApps };
        var (window, shell) = ScreenSnapshotTests.Shell(ViewModels.Screen.Meetings, host => new MeetingsViewModel(context, host));
        return (window, shell, (MeetingsViewModel)shell.CurrentScreen!);
    }

    [AvaloniaFact]
    public void TheScreenButtonSwitchesTheSettingAndListsTheRunningApps()
    {
        using var fixture = new MeetingFixture("format2-cowork");
        var (_, _, model) = Screen(fixture);
        var choice = model.ScreenChoice;
        Assert.False(choice.RecordScreen);
        Assert.Equal("With screen", choice.ButtonTip);

        choice.RecordScreen = true;
        Assert.True(fixture.Settings.RecordScreen);
        Assert.Equal(["Whole screen", "Microsoft Teams", "Zoom"], choice.Targets.Select(t => t.Title));
        Assert.Equal([true, false, false], choice.Targets.Select(t => t.IsChecked));
        Assert.Equal("Record the screen · Whole screen", choice.ButtonTip);

        choice.ChooseCommand.Execute(choice.Targets[2]);
        Assert.Equal(new ScreenCaptureTarget.Application("zoom.exe"), fixture.Settings.ScreenCaptureTarget);
        Assert.Equal([false, false, true], choice.Targets.Select(t => t.IsChecked));
        Assert.Equal("Zoom", choice.ChosenAppName);
        Assert.Equal("Record the screen · Zoom", choice.ButtonTip);

        choice.ChooseCommand.Execute(choice.Targets[0]);
        Assert.Equal(ScreenCaptureTarget.WholeDisplay, fixture.Settings.ScreenCaptureTarget);
        Assert.Null(choice.ChosenAppName);
    }

    [AvaloniaFact]
    public void AChosenAppThatIsNotRunningStaysChosen()
    {
        using var fixture = new MeetingFixture("format2-cowork");
        fixture.Settings.RecordScreen = true;
        fixture.Settings.ScreenCaptureTarget = new ScreenCaptureTarget.Application("slack.exe");
        var (_, _, model) = Screen(fixture);
        model.ScreenChoice.Refresh();
        Assert.Equal(["Whole screen", "slack.exe", "Microsoft Teams", "Zoom"], model.ScreenChoice.Targets.Select(t => t.Title));
        Assert.True(model.ScreenChoice.Targets[1].IsChecked);
    }

    [AvaloniaFact]
    public void SettingsChangedElsewhereReachTheButton()
    {
        using var fixture = new MeetingFixture("format2-cowork");
        var (_, _, model) = Screen(fixture);
        var changed = new List<string?>();
        model.ScreenChoice.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        fixture.Settings.RecordScreen = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(model.ScreenChoice.RecordScreen);
        Assert.Contains(nameof(ScreenChoiceViewModel.RecordScreen), changed);
    }

    [AvaloniaFact]
    public void ThePickerOpensAndPickingCloses()
    {
        using var fixture = new MeetingFixture("format2-cowork");
        fixture.Settings.RecordScreen = true;
        var (window, _, model) = Screen(fixture);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var button = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "ScreenButton");
        Assert.Contains("On", button.Classes);
        var flyout = Assert.IsType<Flyout>(button.Flyout);
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, model.ScreenChoice.Targets.Count);
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();
        window.Close();

        // The picker's content on a page of its own, to look at.
        var content = Assert.IsAssignableFrom<Control>(flyout.Content);
        flyout.Content = null;
        var page = new Border { Padding = new Avalonia.Thickness(16), Child = content, DataContext = model };
        Snapshot.Render(page, "meetings-screen-picker", 330, 260);
    }

    [AvaloniaFact]
    public void TheRecordingPanelSaysWhatTheScreenTrackIsDoing()
    {
        using var fixture = new MeetingFixture("format2-cowork");
        var (window, _, model) = Screen(fixture);
        model.NewRecordingCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var panel = model.Recording;
        Assert.Equal("System audio", panel.SystemMeterTitle);
        Assert.Null(panel.ScreenStatusText);
        Assert.Equal(Loc("meetings.consent"), model.ConsentText);

        fixture.Recorder.SetScreen(ScreenRecordingStatus.Capturing, "Microsoft Teams");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Microsoft Teams", panel.SystemMeterTitle);
        Assert.Null(panel.ScreenStatusText);
        Assert.Equal(Loc("meetings.consent.screen"), model.ConsentText);
        Snapshot.RenderWindow(window, "meetings-recording-screen-app", 1180, 760);

        fixture.Recorder.SetScreen(ScreenRecordingStatus.PausedScreenLocked);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Loc("meetings.screen.locked"), panel.ScreenStatusText);
        Assert.False(panel.ScreenStatusIsWarning);
        Assert.Equal(Loc("meetings.consent"), model.ConsentText);

        fixture.Recorder.SetScreen(ScreenRecordingStatus.Lost);
        Assert.Equal(Loc("meetings.screen.lost"), panel.ScreenStatusText);
        Assert.True(panel.ScreenStatusIsWarning);

        fixture.Recorder.SetScreen(ScreenRecordingStatus.Unavailable("Screen recording needs the Windows video encoder."));
        Assert.Equal("Screen recording needs the Windows video encoder.", panel.ScreenStatusText);
        var (failedWindow, _, _) = Screen(fixture);
        Dispatcher.UIThread.RunJobs();
        Snapshot.RenderWindow(failedWindow, "meetings-recording-screen-failed", 1180, 760, ThemeVariant.Dark);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public void AMeetingWithAScreenRecordingOpensItInTheVideoPlayer(string language)
    {
        CoreStrings.SetLanguage(language == "ru" ? AppLanguage.Ru : AppLanguage.En);
        try
        {
            using var fixture = new MeetingFixture("format2-cowork");
            var id = fixture.Ids["format2-cowork"];
            fixture.Store.Update(fixture.Store.Meeting(id)! with { HasScreenRecording = true });
            var (window, shell, model) = Screen(fixture);
            model.Select(id);
            var detail = model.Detail!;
            Assert.True(detail.HasScreenRecording);
            Snapshot.RenderWindow(window, "meetings-screen-recording-" + language, 1180, 760);

            // The file went missing (copied without it, cleaned up by hand): say so.
            detail.OpenScreenRecordingCommand.Execute(null);
            Assert.True(shell.HasSheet);
            Assert.Empty(fixture.Shell.Opened);

            var path = Path.Combine(fixture.Store.Folder(id), "screen.mov");
            File.WriteAllBytes(path, [0, 0, 0, 8]);
            var (_, _, again) = Screen(fixture);
            again.Select(id);
            again.Detail!.OpenScreenRecordingCommand.Execute(null);
            Assert.Equal([path], fixture.Shell.Opened);
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }

    [AvaloniaFact]
    public void AMeetingStillRecordingOrWithoutVideoOffersNoPlayer()
    {
        using var fixture = new MeetingFixture("format2-cowork", "format2-microphone");
        var (_, _, model) = Screen(fixture);
        model.Select(fixture.Ids["format2-microphone"]);
        Assert.False(model.Detail!.HasScreenRecording);
        var id = fixture.Ids["format2-cowork"];
        fixture.Store.Update(fixture.Store.Meeting(id)! with { HasScreenRecording = true, Status = MeetingStatus.Recording });
        model.Select(id);
        Assert.False(model.Detail!.HasScreenRecording);
    }

    [AvaloniaFact]
    public void TheSettingsSwitchIsTheSameSetting()
    {
        var settings = new AppSettings(SettingsStore.InMemory());
        var model = new MeetingsSettingsViewModel(settings, new FakeModels(), new FakeSpeakerModel(), new FakeSpeakerModel(),
            new FakeSheetHost(), new FakeShell(), new AppPaths("/tmp/slovo"), _ => true, _ => true);
        Assert.False(model.RecordScreen);
        model.RecordScreen = true;
        Assert.True(settings.RecordScreen);
    }

    static string Loc(string key) => Slovo.App.Localization.Loc.Instance.Get(key);

    sealed class FakeSheetHost : ISheetHost
    {
        public void ShowSheet(SheetViewModel sheet) { }
        public void CloseSheet() { }
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = true) => Task.FromResult(false);
        public void ShowAlert(string title, string message) { }
    }
}
