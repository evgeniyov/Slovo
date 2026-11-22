using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Platform.Windows.Audio;
using Slovo.Platform.Windows.Input;

namespace Slovo.Platform.Windows.Tests;

// These Windows classes are constructed without being started: nothing below reaches a
// Win32 or Core Audio call, so they run on any OS (hence the platform warning is off).
#pragma warning disable CA1416

/// <summary>InputSystemTests.testMonitorSyncWithoutTapIsHarmless and DictationAudioTests.testRecorderThatNeverStartedReturnsNothing.</summary>
public class IdleServicesTests
{
    [Fact]
    public void MonitorSyncWithoutAHookIsHarmless()
    {
        var events = 0;
        using var monitor = new KeyboardHookMonitor(ImmediateMainThread.Shared, SystemUptime.Shared);
        monitor.Triggered += _ => events++;
        monitor.KeyStateChanged += _ => events++;
        monitor.SyncRecording(DictationMode.HandsFree);
        monitor.Update(new HotkeyConfiguration(DictationKey.RightAlt));
        monitor.SyncRecording(null);
        monitor.Stop();
        Assert.False(monitor.IsRunning);
        Assert.False(monitor.IsKeyDown);
        Assert.Equal(0, events);
    }

    [Fact]
    public void RecorderThatNeverStartedReturnsNothing()
    {
        using var recorder = new WasapiAudioRecorder(SystemUptime.Shared);
        Assert.False(recorder.IsRecording);
        Assert.Equal(RecordedAudio.Empty, recorder.Stop());
        recorder.Cancel();
        Assert.False(recorder.IsRecording);
        Assert.Null(recorder.LastDeviceName);
    }
}
