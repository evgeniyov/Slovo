using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Tests.Video;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// The screen as the meeting recorder's optional third track (MeetingRecorder.swift's
/// startScreenRecording, retireScreenTrack and finalize): the setting, the app's own sound in
/// place of system audio, failures that never touch the audio, and screen.mov with its flag.
/// </summary>
public sealed class RecorderScreenTests : IDisposable
{
    readonly RecorderHarness h = new();

    public void Dispose() => h.Dispose();

    MeetingRecorder Recorder => h.Recorder;

    MeetingScreenCapture Capture => Recorder.ScreenCaptureForTests ?? throw new InvalidOperationException("No screen capture");

    string ScreenPath(Guid id) => h.Store.ScreenRecordingPath(id);

    /// <summary>Moves the clock a frame on and lets the capture take a picture, a few times.</summary>
    void Pictures(int count)
    {
        for (var i = 0; i < count; i++)
        {
            h.Clock.Advance(MeetingScreenCapture.FrameInterval);
            Capture.Tick();
        }
    }

    [Fact]
    public Task TheScreenIsOffByDefault() => h.Run(async () =>
    {
        var meeting = await Recorder.StartAsync();
        Assert.Equal(ScreenRecordingStatus.Off, Recorder.ScreenRecordingStatus);
        Assert.Null(Recorder.ScreenCaptureForTests);
        Assert.Empty(h.Screen.Streams);
        Assert.False(meeting.HasScreenRecording);
        Assert.Equal(1, h.Sources.SystemRequests);
        Assert.Empty(h.Sources.ApplicationRequests);
    });

    [Fact]
    public Task TheWholeDisplayIsRecordedNextToBothAudioTracks() => h.Run(async () =>
    {
        h.Settings.RecordScreen = true;
        var meeting = await Recorder.StartAsync();
        Assert.Equal(ScreenRecordingStatus.Capturing, Recorder.ScreenRecordingStatus);
        Assert.Null(Recorder.ScreenCaptureAppName);
        Assert.True(meeting.HasScreenRecording);
        Assert.Equal(ScreenCaptureTarget.WholeDisplay, h.Screen.Stream.Target);
        Assert.Equal(1, h.Sources.SystemRequests);
        Assert.Empty(h.Sources.ApplicationRequests);
        Assert.Equal(SystemAudioStatus.Capturing, Recorder.SystemAudioStatus);
        // The flag is on disk too, so a copy of the folder says there is a video.
        h.Store.Flush();
        using (var record = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(h.Store.Folder(meeting.Id), MeetingStore.RecordFileName))))
        {
            Assert.True(record.RootElement.GetProperty("hasScreenRecording").GetBoolean());
        }

        Pictures(6);
        var stopped = await Recorder.StopAsync();
        Assert.True(stopped!.HasScreenRecording);
        Assert.Equal(ScreenRecordingStatus.Off, Recorder.ScreenRecordingStatus);
        Assert.True(h.Screen.Stream.IsDisposed);
        Assert.True(h.Screen.Encoder.IsDisposed);
        var samples = Mp4Reader.Samples(File.ReadAllBytes(ScreenPath(meeting.Id)));
        Assert.Equal(6, samples.Count);
        // The pictures sit on the audio tracks' timeline: a frame a quarter second apart.
        Assert.Equal(Slovo.Core.Video.FragmentedMp4Writer.Ticks(0.5), samples[1].DecodeTime);
    });

    [Fact]
    public Task AVideoWithoutAPictureIsNotKept() => h.Run(async () =>
    {
        h.Settings.RecordScreen = true;
        var meeting = await Recorder.StartAsync();
        var stopped = await Recorder.StopAsync();
        Assert.False(stopped!.HasScreenRecording);
        Assert.False(File.Exists(ScreenPath(meeting.Id)));
        Assert.True(stopped.HasAudio);
    });

    [Fact]
    public Task AScreenThatCannotBeRecordedLeavesTheMeetingRecordingItsAudio() => h.Run(async () =>
    {
        h.Settings.RecordScreen = true;
        h.Screen.UnavailableReason = "Screen recording needs Windows 10 version 1903 or later";
        var meeting = await Recorder.StartAsync();
        Assert.Equal(ScreenRecordingStatus.Unavailable("Screen recording needs Windows 10 version 1903 or later"), Recorder.ScreenRecordingStatus);
        Assert.False(meeting.HasScreenRecording);
        Assert.True(h.Microphone.IsRunning);
        Assert.True(h.System.IsRunning);
        Assert.False(File.Exists(ScreenPath(meeting.Id)));
        Assert.IsType<MeetingRecorderState.Recording>(Recorder.State);
    });

    [Fact]
    public Task AMissingEncoderSaysWhyAndKeepsTheAudio() => h.Run(async () =>
    {
        h.Settings.RecordScreen = true;
        h.Screen.EncoderError = new SlovoException("Screen recording needs the Windows video encoder.");
        await Recorder.StartAsync();
        Assert.Equal(ScreenRecordingStatus.Unavailable("Screen recording needs the Windows video encoder."), Recorder.ScreenRecordingStatus);
        Assert.Equal(SystemAudioStatus.Capturing, Recorder.SystemAudioStatus);
    });

    [Fact]
    public Task OneAppsWindowBringsThatAppsSoundInPlaceOfSystemAudio() => h.Run(async () =>
    {
        h.Settings.RecordScreen = true;
        h.Settings.RecordSystemAudio = false;
        h.Settings.ScreenCaptureTarget = new ScreenCaptureTarget.Application("teams.exe");
        var meeting = await Recorder.StartAsync();
        Assert.Equal(new ScreenCaptureTarget.Application("teams.exe"), h.Screen.Stream.Target);
        Assert.Equal(["teams.exe"], h.Sources.ApplicationRequests);
        Assert.Equal(0, h.Sources.SystemRequests);
        Assert.True(h.Sources.Application!.IsRunning);
        Assert.Equal("Microsoft Teams", Recorder.ScreenCaptureAppName);
        Assert.Equal(SystemAudioStatus.Capturing, Recorder.SystemAudioStatus);
        Assert.True(meeting.HasSystemAudio);

        // An app that stays quiet is no sign of a missing permission.
        h.Clock.Advance(MeetingRecorder.SilenceTimeout + 5);
        Assert.Equal(SystemAudioStatus.Capturing, Recorder.SystemAudioStatus);

        h.Sources.Application.Deliver(RecorderHarness.Constant(1, 0.3f), h.Now - 1);
        var stopped = await Recorder.StopAsync();
        Assert.True(stopped!.HasSystemAudio);
        Assert.True(File.Exists(h.SystemPath(meeting.Id)));
    });

    [Fact]
    public Task AnAppWhoseSoundCannotBeRecordedFallsBackToSystemAudio() => h.Run(async () =>
    {
        h.Settings.RecordScreen = true;
        h.Settings.ScreenCaptureTarget = new ScreenCaptureTarget.Application("teams.exe");
        h.Sources.Application!.StartError = new SlovoException("The app’s sound could not be recorded: timeout");
        await Recorder.StartAsync();
        Assert.Equal(1, h.Sources.SystemRequests);
        Assert.True(h.System.IsRunning);
        Assert.Equal(SystemAudioStatus.Capturing, Recorder.SystemAudioStatus);
        Assert.Equal(ScreenRecordingStatus.Capturing, Recorder.ScreenRecordingStatus);
    });

    [Fact]
    public Task AnAppThatIsNotRunningRecordsSystemAudioAndSaysSo() => h.Run(async () =>
    {
        h.Settings.RecordScreen = true;
        h.Settings.ScreenCaptureTarget = new ScreenCaptureTarget.Application("zoom.exe");
        h.Screen.OpenError = MeetingErrors.ScreenRecordingAppNotRunning();
        var meeting = await Recorder.StartAsync();
        Assert.Equal(ScreenRecordingStatus.Unavailable(MeetingErrors.ScreenRecordingAppNotRunning().Message), Recorder.ScreenRecordingStatus);
        Assert.Null(Recorder.ScreenCaptureAppName);
        Assert.Empty(h.Sources.ApplicationRequests);
        Assert.Equal(1, h.Sources.SystemRequests);
        Assert.True(meeting.HasSystemAudio);
        Assert.False(meeting.HasScreenRecording);
    });

    [Fact]
    public Task ALostScreenKeepsWhatItRecordedAndTheAudioGoesOn() => h.Run(async () =>
    {
        h.Settings.RecordScreen = true;
        var meeting = await Recorder.StartAsync();
        Pictures(3);
        var capture = Capture;
        h.Screen.Stream.IsLost = true;
        h.Screen.KeepOpenError = true;
        h.Screen.OpenError = new SlovoException("no display");
        capture.Tick();
        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.ScreenRecordingStatus == ScreenRecordingStatus.Lost));
        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.ScreenCaptureForTests is null));
        Assert.IsType<MeetingRecorderState.Recording>(Recorder.State);
        Assert.True(h.Microphone.IsRunning);
        Assert.True(h.Store.Meeting(meeting.Id)!.HasScreenRecording);

        var stopped = await Recorder.StopAsync();
        Assert.True(stopped!.HasScreenRecording);
        Assert.Equal(3, Mp4Reader.Samples(File.ReadAllBytes(ScreenPath(meeting.Id))).Count);
    });

    [Fact]
    public Task ALostScreenWithoutAPictureLeavesNoFileAndNoFlag() => h.Run(async () =>
    {
        h.Settings.RecordScreen = true;
        var meeting = await Recorder.StartAsync();
        h.Screen.Stream.IsLost = true;
        h.Screen.KeepOpenError = true;
        h.Screen.OpenError = new SlovoException("no display");
        Capture.Tick();
        Assert.True(await RecorderHarness.WaitUntil(() => h.Store.Meeting(meeting.Id)!.HasScreenRecording == false));
        Assert.False(File.Exists(ScreenPath(meeting.Id)));
        Assert.Equal(ScreenRecordingStatus.Lost, Recorder.ScreenRecordingStatus);
    });

    [Fact]
    public Task AnEncoderThatFailsEndsTheVideoWithTheReason() => h.Run(async () =>
    {
        h.Settings.RecordScreen = true;
        await Recorder.StartAsync();
        Pictures(2);
        h.Screen.Encoder.EncodeError = new InvalidOperationException("the encoder stopped");
        Pictures(1);
        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.ScreenRecordingStatus.Kind == ScreenRecordingStatusKind.Unavailable));
        Assert.Contains("the encoder stopped", Recorder.ScreenRecordingStatus.Message);
        Assert.Equal(SystemAudioStatus.Capturing, Recorder.SystemAudioStatus);
    });

    [Fact]
    public Task ALockedScreenPausesTheVideoAndTheUnlockResumesIt() => h.Run(async () =>
    {
        h.Settings.RecordScreen = true;
        await Recorder.StartAsync();
        h.Screen.Raise(ScreenSessionEvent.Locked);
        Assert.Equal(ScreenRecordingStatus.PausedScreenLocked, Recorder.ScreenRecordingStatus);
        h.Screen.Raise(ScreenSessionEvent.Unlocked);
        Assert.Equal(ScreenRecordingStatus.Capturing, Recorder.ScreenRecordingStatus);
    });

    [Fact]
    public Task ADiskShortOfRoomForVideoLetsTheVideoGoAndKeepsRecording() => h.Run(async () =>
    {
        h.Settings.RecordScreen = true;
        var meeting = await Recorder.StartAsync();
        Pictures(2);
        h.FreeBytes = MeetingRecorder.MinimumFreeBytes - 1;
        Recorder.CheckRecordingHealth();
        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.ScreenCaptureForTests is null));
        Assert.Equal(ScreenRecordingStatus.Unavailable(MeetingErrors.LowDiskSpace(MeetingRecorder.MinimumFreeBytes - 1).Message), Recorder.ScreenRecordingStatus);
        Assert.IsType<MeetingRecorderState.Recording>(Recorder.State);
        Assert.True(h.Store.Meeting(meeting.Id)!.HasScreenRecording);
    });

    [Fact]
    public Task PicturesAfterASleepLandWhereThePaddedAudioDoes() => h.Run(async () =>
    {
        h.Settings.RecordScreen = true;
        await Recorder.StartAsync();
        Recorder.PadForSleep(60);
        Assert.Equal(60, Capture.SleptSeconds);
    });
}
