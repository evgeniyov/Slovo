using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Slovo.App.Localization;
using Slovo.App.Tests.Fakes;
using Slovo.App.Tray;
using Slovo.App.ViewModels.Settings;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.App.Tests.Tray;

public class TrayIconVisibilityTests
{
    [Fact]
    public void TheIconFollowsTheSettingExceptWhileAMeetingRecords()
    {
        Assert.True(TrayController.IsIconVisible(new TrayState()));
        Assert.False(TrayController.IsIconVisible(new TrayState(ShowIcon: false)));
        // Hidden or not, a meeting recording shows the icon so nobody is recorded unnoticed.
        Assert.True(TrayController.IsIconVisible(new TrayState(ShowIcon: false, IsMeetingRecording: true)));
    }

    [Fact]
    public void AMeetingShowsAMicrophoneSwitchedOffOrLostOnTheIcon()
    {
        Assert.Equal(TrayPicture.Idle, TrayController.Picture(new TrayState()));
        Assert.Equal(TrayPicture.Recording, TrayController.Picture(new TrayState(IsRecording: true)));
        Assert.Equal(TrayPicture.Recording, TrayController.Picture(new TrayState(IsProcessing: true)));
        Assert.Equal(TrayPicture.Meeting, TrayController.Picture(new TrayState(IsMeetingRecording: true)));
        // A dictation during a call still shows the meeting, as the menu bar item does.
        Assert.Equal(TrayPicture.Meeting, TrayController.Picture(new TrayState(IsMeetingRecording: true, IsRecording: true)));
        Assert.Equal(TrayPicture.MeetingMicrophoneOff,
            TrayController.Picture(new TrayState(IsMeetingRecording: true, IsMeetingMicrophoneOn: false)));
        // A microphone that went away warns whether or not it was switched on.
        Assert.Equal(TrayPicture.MeetingMicrophoneLost,
            TrayController.Picture(new TrayState(IsMeetingRecording: true, MeetingMicrophoneStatus: MicrophoneStatus.Lost)));
        Assert.Equal(TrayPicture.MeetingMicrophoneLost,
            TrayController.Picture(new TrayState(IsMeetingRecording: true, IsMeetingMicrophoneOn: false, MeetingMicrophoneStatus: MicrophoneStatus.Lost)));
        // Without a meeting the microphone status says nothing.
        Assert.Equal(TrayPicture.Idle, TrayController.Picture(new TrayState(IsMeetingMicrophoneOn: false, MeetingMicrophoneStatus: MicrophoneStatus.Lost)));
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.En)]
    [InlineData(AppLanguage.Ru)]
    public void EveryPictureHasItsOwnIconFileAndTooltip(AppLanguage language)
    {
        CoreStrings.SetLanguage(language);
        try
        {
            TrayState[] states =
            [
                new(),
                new(IsRecording: true),
                new(IsMeetingRecording: true),
                new(IsMeetingRecording: true, IsMeetingMicrophoneOn: false),
                new(IsMeetingRecording: true, MeetingMicrophoneStatus: MicrophoneStatus.Lost),
            ];
            Assert.Equal(Enum.GetValues<TrayPicture>().Length, states.Select(TrayController.Picture).Distinct().Count());
            var tips = states.Select(state => TrayController.ToolTip(state, Loc.Instance)).ToList();
            Assert.Equal(tips.Count, tips.Distinct().Count());
            Assert.All(tips, tip => Assert.StartsWith("Slovo", tip));
            foreach (var name in new[] { "tray-meeting-microphone-off.ico", "tray-meeting-microphone-lost.ico" })
            {
                Assert.True(AssetLoader.Exists(new Uri($"avares://Slovo/Assets/{name}")), name);
            }
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }

    [AvaloniaFact]
    public void TheSystemSettingSwitchesTheIconAndIsSaved()
    {
        var store = SettingsStore.InMemory();
        var settings = new AppSettings(store);
        var model = new SystemSettingsViewModel(settings, new FakeAutostart());
        Assert.True(model.ShowMenuBarIcon);
        var changed = new List<string>();
        store.Changed += changed.Add;

        model.ShowMenuBarIcon = false;

        Assert.False(settings.ShowMenuBarIcon);
        Assert.Contains(SettingsKey.ShowMenuBarIcon, changed);
        Assert.Equal("system.showMenuBarIcon", SettingsKey.ShowMenuBarIcon);
    }
}
