using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.Core.Tests.Settings;

/// <summary>The two screen-recording settings, under the macOS app's key names.</summary>
public sealed class ScreenSettingsTests
{
    [Fact]
    public void TheKeysAreTheMacAppsAndTheScreenIsOffByDefault()
    {
        Assert.Equal("meetings.recordScreen", SettingsKey.RecordScreen);
        Assert.Equal("meetings.screenCaptureApp", SettingsKey.ScreenCaptureApp);
        var settings = new AppSettings(SettingsStore.InMemory());
        Assert.False(settings.RecordScreen);
        Assert.Equal(ScreenCaptureTarget.WholeDisplay, settings.ScreenCaptureTarget);
    }

    [Fact]
    public void AnAppIsStoredByItsIdAndTheWholeDisplayAsNothing()
    {
        var settings = new AppSettings(SettingsStore.InMemory());
        settings.RecordScreen = true;
        settings.ScreenCaptureTarget = new ScreenCaptureTarget.Application("ms-teams.exe");
        Assert.True(settings.RecordScreen);
        Assert.Equal("ms-teams.exe", settings.Store.GetString(SettingsKey.ScreenCaptureApp));
        Assert.Equal(new ScreenCaptureTarget.Application("ms-teams.exe"), settings.ScreenCaptureTarget);

        settings.ScreenCaptureTarget = ScreenCaptureTarget.WholeDisplay;
        Assert.Equal("", settings.Store.GetString(SettingsKey.ScreenCaptureApp));
        Assert.Equal(ScreenCaptureTarget.WholeDisplay, settings.ScreenCaptureTarget);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyValueMeansTheWholeDisplay(string? stored)
    {
        Assert.Equal(ScreenCaptureTarget.WholeDisplay, ScreenCaptureTarget.FromStoredBundleId(stored));
        Assert.Null(ScreenCaptureTarget.FromStoredBundleId(stored).StoredBundleId);
    }

    [Fact]
    public void StatusesCompareByKindAndMessage()
    {
        Assert.Equal(ScreenRecordingStatus.Unavailable("x"), new ScreenRecordingStatus(ScreenRecordingStatusKind.Unavailable, "x"));
        Assert.NotEqual(ScreenRecordingStatus.Unavailable("x"), ScreenRecordingStatus.Unavailable("y"));
        Assert.Equal(ScreenRecordingStatusKind.Off, default(ScreenRecordingStatus).Kind);
    }
}
