using System.ComponentModel;
using Slovo.Core.Audio;
using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// Port of MeetingsRecorderTests.swift over fake capture streams and a clock the test moves:
/// starting and stopping, the shared timeline of both tracks, loopback silence, the
/// microphone switch, sleeps, marked moments, track failures, lost devices and the disk.
/// </summary>
public sealed class RecorderTests : IDisposable
{
    const int Rate = MeetingAudioFormat.SampleRate;
    readonly RecorderHarness h = new();

    public void Dispose() => h.Dispose();

    MeetingRecorder Recorder => h.Recorder;

    static float[] Constant(double seconds, float value) => RecorderHarness.Constant(seconds, value);

    static float Rms(float[] samples, double from, double to) =>
        RecorderHarness.Rms(samples, (int)(from * Rate), (int)(to * Rate));

    // MARK: - Starting and stopping

    [Fact]
    public Task MarkMomentIsNullUnlessRecording() => h.Run(() =>
    {
        Assert.Null(Recorder.MarkMoment());
        Assert.Null(Recorder.MarkMoment("важно"));
        Assert.Null(Recorder.LastMarkedMoment);
        Assert.Null(Recorder.RecordingTracks);
        Assert.False(Recorder.ToggleMicrophone());
        Assert.Equal(MeetingRecorderState.IdleState, Recorder.State);
    });

    [Fact]
    public Task StartCreatesTheMeetingAndBothTracksOnOneTimeline() => h.Run(async () =>
    {
        h.Settings.MicrophoneUid = "usb-microphone";
        var origin = h.Now;
        var meeting = await Recorder.StartAsync();

        Assert.Equal(new MeetingRecorderState.Recording(meeting.Id, origin), Recorder.State);
        Assert.True(Recorder.IsMeetingRecording);
        Assert.Equal(meeting.Id, Recorder.CurrentMeetingId);
        Assert.Equal(MeetingStatus.Recording, meeting.Status);
        Assert.True(meeting.HasSystemAudio);
        // Stored the way the macOS store stores it: normalized to plain spaces.
        Assert.Equal(TranscriptMerger.NormalizedText(MeetingTitles.DefaultTitle(h.Clock.Wall.GetUtcNow(), h.Settings.AppLanguage)),
            meeting.Title);
        Assert.True(File.Exists(h.MicrophonePath(meeting.Id)));
        Assert.True(File.Exists(h.SystemPath(meeting.Id)));
        Assert.Equal(new[] { "usb-microphone" }, h.Sources.MicrophoneUids);
        Assert.True(h.Microphone.IsRunning);
        Assert.True(h.System.IsRunning);
        Assert.Equal(SystemAudioStatus.Capturing, Recorder.SystemAudioStatus);
        Assert.Equal(MicrophoneStatus.Capturing, Recorder.MicrophoneStatus);
        Assert.True(Recorder.IsMicrophoneOn);
        Assert.Equal(MeetingRecordingTracks.MicrophoneAndSystem, Recorder.RecordingTracks);
        Assert.False(Recorder.IsStarting);

        var again = await Assert.ThrowsAsync<SlovoException>(Recorder.StartAsync);
        Assert.Equal(MeetingErrors.AlreadyRecording().Message, again.Message);

        h.Clock.Advance(1);
        Assert.Equal(1, Recorder.Elapsed, 6);

        var stopped = await Recorder.StopAsync();
        Assert.NotNull(stopped);
        Assert.Equal(MeetingRecorderState.IdleState, Recorder.State);
        Assert.False(Recorder.IsMeetingRecording);
        Assert.Null(Recorder.CurrentMeetingId);
        Assert.Null(Recorder.RecordingTracks);
        Assert.Equal(SystemAudioStatus.Off, Recorder.SystemAudioStatus);
        Assert.Equal(1, h.Microphone.StopCount);
        Assert.True(h.Microphone.IsDisposed);
        Assert.True(h.System.IsDisposed);
        Assert.Equal(MeetingStatus.Recorded, stopped.Status);
        Assert.Equal(1, stopped.Duration, 6);
        Assert.True(stopped.HasAudio);
        Assert.Equal(stopped, h.Store.Meeting(meeting.Id));
        Assert.Null(await Recorder.StopAsync());
    });

    [Fact]
    public Task StateGoesThroughStoppingAndIsSavedToDisk() => h.Run(async () =>
    {
        var states = new List<MeetingRecorderState>();
        Recorder.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MeetingRecorder.State))
            {
                states.Add(Recorder.State);
            }
        };
        var meeting = await Recorder.StartAsync();
        h.Microphone.Deliver(Constant(1, 0.2f), h.Now);
        h.Clock.Advance(2);
        var stopped = Recorder.StopImmediately();

        Assert.Equal(3, states.Count);
        Assert.IsType<MeetingRecorderState.Recording>(states[0]);
        Assert.Equal(MeetingRecorderState.StoppingState, states[1]);
        Assert.Equal(MeetingRecorderState.IdleState, states[2]);
        var reloaded = new MeetingStore(h.Folder.Paths, h.MainThread, new ManualTimeProvider()).Meeting(meeting.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(MeetingStatus.Recorded, reloaded.Status);
        Assert.Equal(2, reloaded.Duration, 6);
        Assert.Equal(stopped, reloaded);
    });

    /// <summary>
    /// A microphone that does not start fails the start: the meeting is removed, nothing else is
    /// opened, and the reason shows.
    /// </summary>
    [Fact]
    public Task AMicrophoneThatDoesNotStartFailsTheStart() => h.Run(async () =>
    {
        h.Sources.NewMicrophone = () => new FakeAudioStream { StartError = new SlovoException("no microphone here") };

        var error = await Assert.ThrowsAsync<SlovoException>(Recorder.StartAsync);

        Assert.Equal("no microphone here", error.Message);
        Assert.Equal(new MeetingRecorderState.Failed("no microphone here"), Recorder.State);
        Assert.False(Recorder.IsMeetingRecording);
        Assert.Empty(h.Store.Meetings);
        Assert.Empty(Directory.GetDirectories(h.Folder.Paths.MeetingsFolder));
        Assert.Equal(0, h.Sources.SystemRequests);
        Assert.True(await RecorderHarness.WaitUntil(() => h.Microphone.IsDisposed));
        Assert.False(Recorder.IsStarting);
    });

    [Fact]
    public Task MicrophoneAccessIsCheckedOnlyWhenTheMicrophoneStartsOn() => h.Run(async () =>
    {
        h.Permissions.Statuses[PermissionKind.Microphone] = PermissionStatus.Denied;
        var error = await Assert.ThrowsAsync<SlovoException>(Recorder.StartAsync);
        Assert.Equal(MeetingErrors.MicrophoneDenied().Message, error.Message);
        Assert.Empty(h.Store.Meetings);

        h.Settings.RecordMicrophone = false;
        var meeting = await Recorder.StartAsync();
        Assert.Equal(MeetingRecordingTracks.SystemOnly, Recorder.RecordingTracks);
        Assert.NotNull(await Recorder.StopAsync());
        Assert.Equal(meeting.Id, Assert.Single(h.Store.Meetings).Id);
    });

    [Fact]
    public Task RecordingIsRefusedWithTooLittleFreeSpace() => h.Run(async () =>
    {
        Assert.Null(MeetingRecorder.LowSpaceError(null));
        Assert.Null(MeetingRecorder.LowSpaceError(MeetingRecorder.MinimumFreeBytes));
        Assert.Null(MeetingRecorder.LowSpaceError(40L * 1024 * 1024 * 1024));
        var refusal = MeetingRecorder.LowSpaceError(300L * 1024 * 1024);
        Assert.NotNull(refusal);
        Assert.NotEqual("meetings.error.lowDiskSpace", refusal.Message);
        Assert.NotEmpty(refusal.Message);

        h.FreeBytes = 300L * 1024 * 1024;
        var error = await Assert.ThrowsAsync<SlovoException>(Recorder.StartAsync);
        Assert.Equal(refusal.Message, error.Message);
        Assert.Equal(new MeetingRecorderState.Failed(refusal.Message), Recorder.State);
        Assert.Empty(h.Store.Meetings);
        Assert.Empty(h.Sources.Microphones);

        // A capacity that cannot be read never refuses a recording.
        h.FreeBytes = null;
        await Recorder.StartAsync();
        Assert.True(Recorder.IsMeetingRecording);
    });

    [Fact]
    public void TheFreeSpaceOfARealFolderCanBeRead()
    {
        var available = MeetingRecorder.AvailableBytes(h.Folder.Paths.MeetingsFolder);
        Assert.NotNull(available);
        Assert.True(available > 0);
    }

    // MARK: - The shared timeline

    /// <summary>
    /// Every buffer lands at its capture time on one timeline shared by both tracks: jitter is
    /// left alone, a gap after a device switch is filled, a buffer from a fast device clock
    /// loses its overlap, loopback that delivers nothing leaves silence, and both files end
    /// together.
    /// </summary>
    [Fact]
    public Task BothTracksKeepOneTimelineThroughGapsAndSkewedTimestamps() => h.Run(async () =>
    {
        var t0 = h.Now;
        var meeting = await Recorder.StartAsync();

        h.Clock.Advance(1);
        h.Microphone.Deliver(Constant(1, 0.2f), t0);
        h.Clock.Advance(1);
        h.Microphone.Deliver(Constant(1, 0.2f), t0 + 1.03);
        // Two seconds without a microphone buffer (a device switch) and no system audio at all.
        h.Clock.Advance(2.1);
        h.System.Deliver(Constant(1, 0.3f), t0 + 3);
        h.Microphone.Deliver(Constant(1, 0.2f), t0 + 4);
        h.Clock.Advance(1.5);
        // A device clock running fast: this buffer claims to start 0.4 s before the track's end.
        h.Microphone.Deliver(Constant(1, 0.4f), t0 + 4.6);
        h.Clock.Advance(1.4);
        var stopped = await Recorder.StopAsync();

        Assert.NotNull(stopped);
        Assert.Equal(7, stopped.Duration, 6);
        Assert.True(stopped.HasSystemAudio);
        var microphone = RecorderHarness.Read(h.MicrophonePath(meeting.Id));
        var system = RecorderHarness.Read(h.SystemPath(meeting.Id));
        Assert.Equal(7 * Rate, microphone.Length);
        Assert.Equal(7 * Rate, system.Length);

        var speech = RecorderHarness.Boosted(0.2f);
        Assert.Equal(speech, Rms(microphone, 0, 2), 4);
        Assert.Equal(0, Rms(microphone, 2, 4));
        Assert.Equal(speech, Rms(microphone, 4, 5), 4);
        Assert.Equal(RecorderHarness.Boosted(0.4f), Rms(microphone, 5, 5.6), 4);
        Assert.Equal(0, Rms(microphone, 5.6, 7));

        Assert.Equal(0, Rms(system, 0, 3));
        Assert.Equal(RecorderHarness.Stored(0.3f), Rms(system, 3, 4), 4);
        Assert.Equal(0, Rms(system, 4, 7));
    });

    /// <summary>The microphone track is boosted by 9 dB on its way to disk, as on macOS; system audio is not.</summary>
    [Fact]
    public Task TheMicrophoneIsBoostedAndSystemAudioIsNot() => h.Run(async () =>
    {
        var t0 = h.Now;
        var meeting = await Recorder.StartAsync();
        h.Microphone.Deliver(Constant(1, 0.05f), t0);
        h.System.Deliver(Constant(1, 0.05f), t0);
        h.Clock.Advance(1);
        await Recorder.StopAsync();

        var microphone = RecorderHarness.Read(h.MicrophonePath(meeting.Id));
        var system = RecorderHarness.Read(h.SystemPath(meeting.Id));
        Assert.Equal(0.05 * 2.818, microphone[100], 0.002);
        Assert.Equal(RecorderHarness.Stored(0.05f), system[100]);
    });

    /// <summary>
    /// Loopback delivers nothing while nothing plays. That is silence on the track, not a
    /// failure; after the silence window the status says so, and sound brings it back.
    /// </summary>
    [Fact]
    public Task LoopbackThatStaysQuietIsSilentThenCapturingOnceSomethingPlays() => h.Run(async () =>
    {
        var t0 = h.Now;
        var meeting = await Recorder.StartAsync();

        h.Clock.Advance(MeetingRecorder.SilenceTimeout - 0.5);
        Assert.Equal(SystemAudioStatus.Capturing, Recorder.SystemAudioStatus);
        h.Clock.Advance(0.5);
        Assert.Equal(SystemAudioStatusKind.Silent, Recorder.SystemAudioStatus.Kind);
        Assert.Equal(MeetingErrors.SystemAudioDenied().Message, Recorder.SystemAudioStatus.Message);
        Assert.Equal(MeetingRecordingTracks.MicrophoneAndSystem, Recorder.RecordingTracks);

        // The track grew with the meeting meanwhile, a little behind it.
        h.Drain();
        Assert.Equal((long)((MeetingRecorder.SilenceTimeout - MeetingRecorder.LiveFillLag) * Rate), h.Writers.System!.FrameCount);

        h.System.Deliver(Constant(1, 0.3f), t0 + 8.5);
        Assert.Equal(SystemAudioStatus.Capturing, Recorder.SystemAudioStatus);
        h.Clock.Advance(2);
        var stopped = await Recorder.StopAsync();

        Assert.NotNull(stopped);
        Assert.True(stopped.HasSystemAudio);
        var system = RecorderHarness.Read(h.SystemPath(meeting.Id));
        Assert.Equal(10 * Rate, system.Length);
        Assert.Equal(0, Rms(system, 0, 8.5));
        Assert.Equal(RecorderHarness.Stored(0.3f), Rms(system, 8.5, 9.5), 4);
    });

    /// <summary>At an in-person meeting nothing plays: system.caf holds only silence and is removed.</summary>
    [Fact]
    public Task ASystemTrackThatNeverHeldSoundIsRemoved() => h.Run(async () =>
    {
        var t0 = h.Now;
        var meeting = await Recorder.StartAsync();
        h.Microphone.Deliver(Constant(1, 0.2f), t0);
        h.System.Deliver(new float[Rate], t0);
        h.Clock.Advance(10);
        var stopped = await Recorder.StopAsync();

        Assert.NotNull(stopped);
        Assert.False(stopped.HasSystemAudio);
        Assert.False(File.Exists(h.SystemPath(meeting.Id)));
        Assert.Equal(10 * Rate, RecorderHarness.Read(h.MicrophonePath(meeting.Id)).Length);
        Assert.False(h.Store.Meeting(meeting.Id)!.HasSystemAudio);
    });

    [Fact]
    public Task SystemAudioSwitchedOffStillKeepsTheTimelineAndLeavesNoFile() => h.Run(async () =>
    {
        h.Settings.RecordSystemAudio = false;
        var meeting = await Recorder.StartAsync();
        Assert.Equal(SystemAudioStatus.Off, Recorder.SystemAudioStatus);
        Assert.Equal(0, h.Sources.SystemRequests);
        Assert.False(meeting.HasSystemAudio);
        Assert.Equal(MeetingRecordingTracks.MicrophoneOnly, Recorder.RecordingTracks);
        Assert.True(File.Exists(h.SystemPath(meeting.Id)));

        h.Clock.Advance(3);
        var stopped = await Recorder.StopAsync();
        Assert.False(stopped!.HasSystemAudio);
        Assert.False(File.Exists(h.SystemPath(meeting.Id)));
        Assert.Equal(3 * Rate, RecorderHarness.Read(h.MicrophonePath(meeting.Id)).Length);
    });

    [Fact]
    public Task SystemAudioThatCannotBeCapturedLeavesTheMicrophoneRecording() => h.Run(async () =>
    {
        h.Sources.System = null;
        var meeting = await Recorder.StartAsync();
        Assert.Equal(SystemAudioStatusKind.Unavailable, Recorder.SystemAudioStatus.Kind);
        Assert.Equal(MeetingErrors.SystemAudioUnsupported().Message, Recorder.SystemAudioStatus.Message);
        Assert.False(meeting.HasSystemAudio);
        Assert.Equal(MeetingRecordingTracks.MicrophoneOnly, Recorder.RecordingTracks);
        await Recorder.StopAsync();

        var failing = new FakeAudioStream { StartError = new SlovoException("loopback refused") };
        h.Sources.System = failing;
        meeting = await Recorder.StartAsync();
        Assert.Equal(new SystemAudioStatus(SystemAudioStatusKind.Unavailable, "loopback refused"), Recorder.SystemAudioStatus);
        Assert.False(meeting.HasSystemAudio);
        Assert.True(Recorder.IsMeetingRecording);
        Assert.True(await RecorderHarness.WaitUntil(() => failing.IsDisposed));
    });

    // MARK: - The microphone switch

    /// <summary>
    /// Off, on and off again: the capture never stops, the microphone track gets silence of
    /// exactly the length it would have got sound, both tracks stay on one timeline, moments
    /// keep their place, and each switch is heard and published.
    /// </summary>
    [Fact]
    public Task SwitchingTheMicrophoneMidRecordingKeepsTheTimeline() => h.Run(async () =>
    {
        var t0 = h.Now;
        var meeting = await Recorder.StartAsync();
        var speech = Constant(1, 0.25f);
        void Deliver(double second)
        {
            h.Microphone.Deliver(speech, t0 + second);
            h.System.Deliver(speech, t0 + second);
        }

        Deliver(0);
        Assert.True(Recorder.MicrophoneLevel > 0, "the level reaches the main thread");
        Assert.True(Recorder.SystemLevel > 0);

        Assert.True(Recorder.ToggleMicrophone());
        Assert.False(Recorder.IsMicrophoneOn);
        Assert.Equal(0, Recorder.MicrophoneLevel);
        Recorder.ReceiveMicrophoneLevel(0.8f);
        Assert.Equal(0, Recorder.MicrophoneLevel);
        Assert.Equal(MeetingRecordingTracks.SystemOnly, Recorder.RecordingTracks);
        Assert.False(Recorder.LastMicrophoneSwitch!.IsOn);
        Assert.False(Recorder.SetMicrophoneOn(false), "switching to the state it is in does nothing");
        Assert.Equal(new[] { SoundCue.Stop }, h.Sounds.Played);

        // The capture keeps delivering; one buffer is late, as after a device switch.
        Deliver(1);
        Deliver(3);
        Assert.Equal(0, Recorder.MicrophoneLevel);
        h.Clock.Advance(4);
        var marked = Recorder.MarkMoment();
        Assert.Equal(4, marked!.Time, 6);

        Assert.True(Recorder.SetMicrophoneOn(true));
        var switchedOn = Recorder.LastMicrophoneSwitch!;
        Assert.True(switchedOn.IsOn);
        Assert.Equal(MeetingRecordingTracks.MicrophoneAndSystem, Recorder.RecordingTracks);
        Deliver(4);

        Assert.True(Recorder.ToggleMicrophone());
        Assert.NotEqual(switchedOn, Recorder.LastMicrophoneSwitch);
        Assert.False(Recorder.LastMicrophoneSwitch!.IsOn);
        Assert.Equal(new[] { SoundCue.Stop, SoundCue.Start, SoundCue.Stop }, h.Sounds.Played);
        Assert.True(h.Settings.RecordMicrophone, "the switch changes the recording, not the setting");

        h.Drain();
        Assert.Equal(h.Writers.Microphone.FrameCount, h.Writers.System!.FrameCount);
        Assert.Equal(5 * Rate, h.Writers.Microphone.FrameCount);
        Assert.NotNull(Recorder.StopImmediately());
        Assert.False(Recorder.ToggleMicrophone());
        Assert.Equal(3, h.Sounds.Played.Count);
        Assert.Single(h.Sources.Microphones);

        var samples = RecorderHarness.Read(h.MicrophonePath(meeting.Id));
        Assert.Equal(RecorderHarness.Boosted(0.25f), Rms(samples, 0.05, 0.95), 4);
        Assert.Equal(0, Rms(samples, 1, 4));
        Assert.Equal(RecorderHarness.Boosted(0.25f), Rms(samples, 4.05, 4.95), 4);
    });

    [Fact]
    public Task SwitchSoundsFollowTheSoundsSetting() => h.Run(async () =>
    {
        h.Settings.SoundsEnabled = false;
        await Recorder.StartAsync();
        Assert.True(Recorder.ToggleMicrophone());
        h.Microphone.Fail("gone");
        Assert.Empty(h.Sounds.Played);
    });

    /// <summary>
    /// «Record the microphone in meetings» decides how each recording starts. A recording that
    /// starts with it off does not open the microphone at all, keeps its track silent on the
    /// timeline, and opens it once it is switched on.
    /// </summary>
    [Fact]
    public Task TheMicrophoneStartsAsTheSettingSaysAndOpensWhenSwitchedOn() => h.Run(async () =>
    {
        Assert.True(h.Settings.RecordMicrophone, "on unless it was turned off");
        h.Settings.RecordMicrophone = false;
        var t0 = h.Now;
        var meeting = await Recorder.StartAsync();
        Assert.False(Recorder.IsMicrophoneOn);
        Assert.Empty(h.Sources.Microphones);
        Assert.Equal(MeetingRecordingTracks.SystemOnly, Recorder.RecordingTracks);
        Assert.Null(Recorder.LastMicrophoneSwitch);

        h.System.Deliver(Constant(3, 0.3f), t0);
        h.Clock.Advance(3);
        Assert.True(Recorder.SetMicrophoneOn(true));
        Assert.True(await RecorderHarness.WaitUntil(() => h.Sources.Microphones.Count == 1 && h.Microphone.IsRunning));
        Assert.Equal(MeetingRecordingTracks.MicrophoneAndSystem, Recorder.RecordingTracks);
        h.Microphone.Deliver(Constant(1, 0.2f), t0 + 3);
        h.Clock.Advance(1);
        var stopped = await Recorder.StopAsync();

        Assert.False(h.Settings.RecordMicrophone);
        Assert.True(await RecorderHarness.WaitUntil(() => h.Microphone.IsDisposed));
        var microphone = RecorderHarness.Read(h.MicrophonePath(meeting.Id));
        Assert.Equal(4 * Rate, microphone.Length);
        Assert.Equal(0, Rms(microphone, 0, 3));
        Assert.Equal(RecorderHarness.Boosted(0.2f), Rms(microphone, 3, 4), 4);
        Assert.Equal(4, stopped!.Duration, 6);

        // The next recording starts from the setting again.
        await Recorder.StartAsync();
        Assert.False(Recorder.IsMicrophoneOn);
        Assert.Single(h.Sources.Microphones);
    });

    [Fact]
    public Task AMicrophoneThatDoesNotOpenWhenSwitchedOnIsLostAndTriedAgain() => h.Run(async () =>
    {
        h.Settings.RecordMicrophone = false;
        await Recorder.StartAsync();
        h.Sources.NewMicrophone = () => new FakeAudioStream { StartError = new SlovoException("busy") };

        Assert.True(Recorder.SetMicrophoneOn(true));
        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.MicrophoneStatus == MicrophoneStatus.Lost));
        Assert.Equal(new[] { SoundCue.Start, SoundCue.Cancel }, h.Sounds.Played);
        Assert.True(Recorder.IsMeetingRecording);
        Assert.True(await RecorderHarness.WaitUntil(() => h.Sources.Microphones[0].IsDisposed));

        // Switched off, it is neither tried nor missing.
        Assert.True(Recorder.SetMicrophoneOn(false));
        Assert.Equal(MicrophoneStatus.Capturing, Recorder.MicrophoneStatus);
        h.Clock.Advance(MeetingRecorder.MicrophoneRetrySeconds);
        Assert.Single(h.Sources.Microphones);

        // Switched on again it is tried at once, and every few seconds while it stays missing.
        Assert.True(Recorder.SetMicrophoneOn(true));
        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.MicrophoneStatus == MicrophoneStatus.Lost));
        Assert.Equal(2, h.Sources.Microphones.Count);
        h.Sources.NewMicrophone = () => new FakeAudioStream();
        h.Clock.Advance(MeetingRecorder.MicrophoneRetrySeconds);
        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.MicrophoneStatus == MicrophoneStatus.Capturing));
        Assert.Equal(3, h.Sources.Microphones.Count);
        Assert.True(h.Microphone.IsRunning);
        Assert.Equal(new[] { SoundCue.Start, SoundCue.Cancel, SoundCue.Stop, SoundCue.Start, SoundCue.Cancel }, h.Sounds.Played);
    });

    // MARK: - Lost devices

    /// <summary>
    /// A Bluetooth handoff that takes minutes, or an interface left unplugged, costs the
    /// microphone track and nothing else, and is heard once per outage.
    /// </summary>
    [Fact]
    public Task ALostMicrophoneKeepsTheMeetingAndIsHeardOncePerOutage() => h.Run(async () =>
    {
        var t0 = h.Now;
        var meeting = await Recorder.StartAsync();
        Assert.Empty(h.Sounds.Played);
        h.Microphone.Deliver(Constant(1, 0.2f), t0);
        Assert.True(Recorder.MicrophoneLevel > 0);

        h.Microphone.Fail("The microphone is unavailable");
        Assert.Equal(MicrophoneStatus.Lost, Recorder.MicrophoneStatus);
        Assert.Equal(0, Recorder.MicrophoneLevel);
        Assert.True(Recorder.IsMeetingRecording, "an outage on one track must not end the meeting");
        Assert.Null(Recorder.LastFailure);
        Assert.Equal(MeetingRecordingTracks.SystemOnly, Recorder.RecordingTracks);
        h.Microphone.Fail("The microphone is unavailable");
        Assert.Equal(new[] { SoundCue.Cancel }, h.Sounds.Played);

        // System audio keeps being written for the whole outage.
        h.System.Deliver(Constant(1, 0.3f), t0 + 1);
        h.Drain();
        Assert.Equal(2 * Rate, h.Writers.System!.FrameCount);

        h.Clock.Advance(30);
        h.Microphone.Resume();
        Assert.Equal(MicrophoneStatus.Capturing, Recorder.MicrophoneStatus);
        Assert.Single(h.Sounds.Played);
        h.Microphone.Deliver(Constant(1, 0.2f), t0 + 30);
        var moment = Recorder.MarkMoment();
        Assert.Equal(30, moment!.Time, 6);
        h.Microphone.Fail("The microphone is unavailable");
        Assert.Equal(new[] { SoundCue.Cancel, SoundCue.Cancel }, h.Sounds.Played);

        h.Clock.Advance(1);
        Assert.NotNull(Recorder.StopImmediately());
        Assert.Equal(MicrophoneStatus.Capturing, Recorder.MicrophoneStatus);
        Assert.Equal(2, h.Sounds.Played.Count);
        var microphone = RecorderHarness.Read(h.MicrophonePath(meeting.Id));
        Assert.Equal(31 * Rate, microphone.Length);
        Assert.Equal(0, Rms(microphone, 1, 30));
        Assert.Equal(RecorderHarness.Boosted(0.2f), Rms(microphone, 30, 31), 4);
    });

    [Fact]
    public Task SystemAudioThatStopsArrivingIsUnavailableUntilItIsBack() => h.Run(async () =>
    {
        await Recorder.StartAsync();
        h.System.Fail("System audio is unavailable");
        Assert.Equal(new SystemAudioStatus(SystemAudioStatusKind.Unavailable, "System audio is unavailable"), Recorder.SystemAudioStatus);
        Assert.Equal(MeetingRecordingTracks.MicrophoneOnly, Recorder.RecordingTracks);
        Assert.True(Recorder.IsMeetingRecording);
        h.System.Resume();
        Assert.Equal(SystemAudioStatus.Capturing, Recorder.SystemAudioStatus);
    });

    /// <summary>Failures raised while the other tracks are still opening are acted on once the recording exists.</summary>
    [Fact]
    public Task FailuresDuringTheStartAreAppliedOnceItFinishes() => h.Run(async () =>
    {
        h.System.DuringStart = () => h.System.Fail("System audio is unavailable");
        h.Sources.NewMicrophone = () =>
        {
            var stream = new FakeAudioStream();
            stream.DuringStart = () => stream.Fail("The microphone is unavailable");
            return stream;
        };

        await Recorder.StartAsync();

        Assert.True(Recorder.IsMeetingRecording);
        Assert.Equal(SystemAudioStatusKind.Unavailable, Recorder.SystemAudioStatus.Kind);
        Assert.Equal(MicrophoneStatus.Lost, Recorder.MicrophoneStatus);
        Assert.Equal(new[] { SoundCue.Cancel }, h.Sounds.Played);
        Assert.Equal(MeetingRecordingTracks.Neither, Recorder.RecordingTracks);
    });

    // MARK: - Track failures

    /// <summary>
    /// What the system track recorded before a failed write is the other side of the call up to
    /// that moment: it stays on disk and in the meeting, and the microphone keeps recording.
    /// </summary>
    [Fact]
    public Task ASystemTrackThatFailsLaterKeepsWhatItRecorded() => h.Run(async () =>
    {
        var t0 = h.Now;
        var meeting = await Recorder.StartAsync();
        h.System.Deliver(Constant(1, 0.3f), t0);
        h.Drain();
        h.Writers.System!.WriteHook = _ => throw new IOException("the disk is full");
        h.System.Deliver(Constant(1, 0.3f), t0 + 1);

        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.SystemAudioStatus.Kind == SystemAudioStatusKind.Unavailable));
        Assert.Contains("the disk is full", Recorder.SystemAudioStatus.Message);
        Assert.True(Recorder.IsMeetingRecording);
        Assert.Null(Recorder.LastFailure);
        Assert.Equal(MicrophoneStatus.Capturing, Recorder.MicrophoneStatus);
        Assert.True(await RecorderHarness.WaitUntil(() => h.System.IsDisposed));
        Assert.Null(h.Writers.System);

        h.Microphone.Deliver(Constant(2, 0.2f), t0);
        h.Clock.Advance(2);
        var stopped = await Recorder.StopAsync();
        Assert.True(stopped!.HasSystemAudio, "the partial track is kept");
        var system = RecorderHarness.Read(h.SystemPath(meeting.Id));
        Assert.Equal(Rate, system.Length);
        Assert.Equal(RecorderHarness.Stored(0.3f), Rms(system, 0, 1), 4);
        Assert.Equal(2 * Rate, RecorderHarness.Read(h.MicrophonePath(meeting.Id)).Length);
    });

    /// <summary>System audio is the optional track: a failed write to it must not cost the microphone.</summary>
    [Fact]
    public Task ASystemTrackFailureBeforeAnySoundDropsTheFileAndLeavesTheMicrophoneRecording() => h.Run(async () =>
    {
        var t0 = h.Now;
        var meeting = await Recorder.StartAsync();
        h.Writers.System!.WriteHook = _ => throw new IOException("the disk is full");
        h.System.Deliver(Constant(1, 0.2f), t0);

        Assert.True(await RecorderHarness.WaitUntil(() => h.Store.Meeting(meeting.Id)?.HasSystemAudio == false));
        Assert.Equal(SystemAudioStatusKind.Unavailable, Recorder.SystemAudioStatus.Kind);
        Assert.True(Recorder.IsMeetingRecording);
        Assert.Null(Recorder.LastFailure);
        Assert.False(File.Exists(h.SystemPath(meeting.Id)), "the empty system track is dropped");

        h.Microphone.Deliver(Constant(1, 0.2f), t0);
        h.Drain();
        Assert.Equal(Rate, h.Writers.Microphone.FrameCount);
        Assert.False(Recorder.StopImmediately()!.HasSystemAudio);
    });

    /// <summary>
    /// Both tracks can go, one after the other. The meeting must not outlive the second one: a
    /// session with no writers left counts time and claims a recording over two files that
    /// stopped growing.
    /// </summary>
    [Fact]
    public Task TheMicrophoneThenTheSystemTrackFailingEndsTheMeeting() => h.Run(async () =>
    {
        var t0 = h.Now;
        var meeting = await Recorder.StartAsync();
        var system = h.Writers.System!;
        h.Writers.Microphone.WriteHook = _ => throw new IOException("the disk is full");
        h.Microphone.Deliver(Constant(1, 0.2f), t0);

        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.MicrophoneStatus == MicrophoneStatus.Lost));
        Assert.True(Recorder.IsMeetingRecording, "system audio alone still keeps the meeting");
        Assert.Null(Recorder.LastFailure);
        Assert.True(await RecorderHarness.WaitUntil(() => h.Microphone.IsDisposed));

        system.WriteHook = _ => throw new IOException("the disk is full");
        h.System.Deliver(Constant(1, 0.2f), t0);

        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.LastFailure is not null));
        Assert.Contains("the disk is full", Recorder.LastFailure);
        Assert.False(Recorder.IsMeetingRecording);
        Assert.IsType<MeetingRecorderState.Failed>(Recorder.State);
        Assert.Null(Recorder.MarkMoment());
        Assert.Equal(MeetingStatus.Recorded, h.Store.Meeting(meeting.Id)!.Status);
        Assert.False(h.Store.Meeting(meeting.Id)!.HasSystemAudio);
        Assert.Equal(meeting.Id, Recorder.AutoStoppedMeeting?.Id);
    });

    [Fact]
    public Task TheSystemThenTheMicrophoneTrackFailingEndsTheMeeting() => h.Run(async () =>
    {
        var t0 = h.Now;
        var meeting = await Recorder.StartAsync();
        h.Writers.System!.WriteHook = _ => throw new IOException("the disk is full");
        h.System.Deliver(Constant(1, 0.2f), t0);
        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.SystemAudioStatus.Kind == SystemAudioStatusKind.Unavailable));
        Assert.True(Recorder.IsMeetingRecording, "the microphone was never in trouble");

        h.Writers.Microphone.WriteHook = _ => throw new IOException("the disk is full");
        h.Microphone.Deliver(Constant(1, 0.2f), t0);

        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.LastFailure is not null));
        Assert.False(Recorder.IsMeetingRecording);
        Assert.Null(Recorder.MarkMoment());
        Assert.Equal(MeetingStatus.Recorded, h.Store.Meeting(meeting.Id)!.Status);
        Assert.Equal(MeetingStatus.Recorded, Recorder.AutoStoppedMeeting?.Status);
    });

    /// <summary>Without system audio a microphone track that cannot be written leaves nothing to record.</summary>
    [Fact]
    public Task AMicrophoneWriteFailureWithoutSystemAudioEndsTheRecordingSaved() => h.Run(async () =>
    {
        h.Settings.RecordSystemAudio = false;
        var t0 = h.Now;
        var meeting = await Recorder.StartAsync();
        h.Microphone.Deliver(Constant(1, 0.2f), t0);
        h.Drain();
        h.Writers.Microphone.WriteHook = _ => throw new IOException("the disk is full");
        h.Microphone.Deliver(Constant(1, 0.2f), t0 + 1);

        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.LastFailure is not null));
        var failed = Assert.IsType<MeetingRecorderState.Failed>(Recorder.State);
        Assert.Contains("the disk is full", failed.Message);
        Assert.False(Recorder.IsMeetingRecording);
        Assert.Equal(MeetingStatus.Recorded, h.Store.Meeting(meeting.Id)!.Status);
        Assert.Equal(meeting.Id, Recorder.AutoStoppedMeeting?.Id);
        Assert.Equal(Rate, RecorderHarness.Read(h.MicrophonePath(meeting.Id)).Length);
    });

    /// <summary>
    /// A device that only went away is not a closed track: the system track failing during the
    /// outage leaves a meeting that is still recording and picks the microphone back up.
    /// </summary>
    [Fact]
    public Task ASystemTrackFailureDuringALostMicrophoneKeepsTheMeeting() => h.Run(async () =>
    {
        var t0 = h.Now;
        await Recorder.StartAsync();
        h.Microphone.Fail("The microphone is unavailable");
        h.Writers.System!.WriteHook = _ => throw new IOException("the disk is full");
        h.System.Deliver(Constant(1, 0.2f), t0);

        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.SystemAudioStatus.Kind == SystemAudioStatusKind.Unavailable));
        Assert.True(Recorder.IsMeetingRecording, "a recoverable outage keeps the meeting");
        Assert.Null(Recorder.LastFailure);

        h.Microphone.Resume();
        Assert.Equal(MicrophoneStatus.Capturing, Recorder.MicrophoneStatus);
        h.Microphone.Deliver(Constant(1, 0.2f), t0);
        h.Drain();
        Assert.Equal(Rate, h.Writers.Microphone.FrameCount);
        Assert.Equal(MeetingStatus.Recorded, Recorder.StopImmediately()!.Status);
    });

    // MARK: - The disk

    /// <summary>
    /// A disk that is nearly full ends the recording before the writes start failing: what was
    /// recorded is saved, the failure is published, and the meeting is handed on.
    /// </summary>
    [Fact]
    public Task ANearlyFullDiskEndsTheRecordingSavedAndHandsItOn() => h.Run(async () =>
    {
        var t0 = h.Now;
        var meeting = await Recorder.StartAsync();
        h.Microphone.Deliver(Constant(1, 0.2f), t0);
        h.Clock.Advance(29);
        Assert.True(Recorder.IsMeetingRecording);
        h.FreeBytes = 40L * 1024 * 1024;
        h.Clock.Advance(1);

        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.AutoStoppedMeeting is not null));
        Assert.Equal(meeting.Id, Recorder.AutoStoppedMeeting!.Id);
        Assert.Equal(MeetingStatus.Recorded, Recorder.AutoStoppedMeeting.Status);
        Assert.Equal(30, Recorder.AutoStoppedMeeting.Duration, 6);
        Assert.False(Recorder.IsMeetingRecording);
        Assert.Equal(MeetingErrors.LowDiskSpaceWhileRecording(40L * 1024 * 1024).Message, Recorder.LastFailure);
        Assert.Equal(MeetingStatus.Recorded, h.Store.Meeting(meeting.Id)!.Status);
        var microphone = RecorderHarness.Read(h.MicrophonePath(meeting.Id));
        Assert.Equal(RecorderHarness.Boosted(0.2f), Rms(microphone, 0, 1), 4);
        Assert.Equal((30 - MeetingRecorder.LiveFillLag) * Rate, microphone.Length);

        // The next start clears the failure.
        h.FreeBytes = null;
        await Recorder.StartAsync();
        Assert.Null(Recorder.LastFailure);
    });

    // MARK: - Sleep

    /// <summary>
    /// The uptime clock stands still while the computer sleeps; the wall clock does not. The
    /// tracks are padded for the difference as soon as it is read — by a mark right after the
    /// wake, without waiting for the ticker — and every later position counts it.
    /// </summary>
    [Fact]
    public Task ASleepPadsBothTracksAndKeepsMarkedMomentsTrue() => h.Run(async () =>
    {
        var t0 = h.Now;
        var meeting = await Recorder.StartAsync();
        h.Microphone.Deliver(Constant(1, 0.2f), t0);
        h.System.Deliver(Constant(1, 0.3f), t0);
        h.Clock.Advance(10);
        var before = Recorder.MarkMoment("до сна");
        Assert.Equal(10, before!.Time, 6);

        // Two minutes of sleep, and not a single notification about it.
        h.Clock.Sleep(120);
        var afterWake = Recorder.MarkMoment("после сна");
        Assert.Equal(130, afterWake!.Time, 6);
        h.Microphone.Deliver(Constant(1, 0.2f), t0 + 10);
        h.System.Deliver(Constant(1, 0.3f), t0 + 10);
        // Reading the clocks again without another sleep pads nothing.
        h.Clock.Advance(5);
        var later = Recorder.MarkMoment();
        Assert.Equal(135, later!.Time, 6);
        Assert.Equal(135, Recorder.Elapsed, 6);

        var stopped = await Recorder.StopAsync();
        Assert.Equal(135, stopped!.Duration, 6);
        Assert.Equal(new[] { 10.0, 130, 135 }, stopped.Highlights.Select(m => Math.Round(m.Time, 6)));
        foreach (var path in new[] { h.MicrophonePath(meeting.Id), h.SystemPath(meeting.Id) })
        {
            var samples = RecorderHarness.Read(path);
            Assert.Equal(135 * Rate, samples.Length);
            Assert.True(Rms(samples, 0.05, 0.95) > 0.1);
            Assert.Equal(0, Rms(samples, 1, 130));
            Assert.True(Rms(samples, 130.05, 130.95) > 0.1, "speech after the wake keeps its true position");
            Assert.Equal(0, Rms(samples, 131, 135));
        }
    });

    [Fact]
    public Task TheTickerNoticesASleepWithoutAnyMark() => h.Run(async () =>
    {
        await Recorder.StartAsync();
        h.Clock.Advance(3);
        h.Clock.Sleep(60);
        h.Clock.Advance(MeetingRecorder.TickSeconds);
        Assert.Equal(63.25, Recorder.Elapsed, 6);
        h.Drain();
        Assert.Equal((long)((1 + 60) * Rate), h.Writers.Microphone.FrameCount);
        h.Clock.Advance(1);
        Assert.Equal(64.25, Recorder.Elapsed, 6);
    });

    /// <summary>
    /// A time sync that sets the wall clock back, or forward by less than a sleep takes, is not
    /// a sleep, and does not change how a later real sleep is measured.
    /// </summary>
    [Fact]
    public Task WallClockStepsAreNotSleeps() => h.Run(async () =>
    {
        await Recorder.StartAsync();
        h.Clock.Advance(2);
        h.Clock.StepWallClock(-30);
        h.Clock.Advance(1);
        h.Clock.StepWallClock(1.5);
        h.Clock.Advance(1);
        Assert.Equal(4, Recorder.MarkMoment()!.Time, 6);

        h.Clock.Sleep(60);
        h.Clock.Advance(1);
        Assert.Equal(65, Recorder.MarkMoment()!.Time, 6);
    });

    /// <summary>
    /// A sleep is padded with silence of its own length, queued on the writers; a pad the disk
    /// cannot hold ends the recording instead, with the unpadded sleep left out of the length.
    /// </summary>
    [Fact]
    public Task ASleepTheDiskCannotHoldEndsTheRecordingInsteadOfPadding() => h.Run(async () =>
    {
        Assert.True(MeetingRecorder.SleepPadFits(3600, 2, 10L * 1024 * 1024 * 1024));
        Assert.False(MeetingRecorder.SleepPadFits(3600, 1, 150L * 1024 * 1024));
        Assert.True(MeetingRecorder.SleepPadFits(0, 2, MeetingRecorder.MinimumFreeBytesToContinue));

        var meeting = await Recorder.StartAsync();
        h.Clock.Advance(1);
        h.FreeBytes = 150L * 1024 * 1024;
        h.Clock.Sleep(3600);
        h.Clock.Advance(MeetingRecorder.TickSeconds);

        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.AutoStoppedMeeting is not null));
        Assert.False(Recorder.IsMeetingRecording);
        var stopped = h.Store.Meeting(meeting.Id)!;
        Assert.Equal(1.25, stopped.Duration, 6);
        Assert.True(RecorderHarness.Read(h.MicrophonePath(meeting.Id)).Length < 2 * Rate, "no hour of silence on a disk that cannot hold it");
    });

    /// <summary>
    /// When the uptime clock counts the sleep itself, the buffers after the wake carry their
    /// true times and the tracks fill up to them; nothing is padded twice.
    /// </summary>
    [Fact]
    public Task AnUptimeClockThatCountsSleepNeedsNoPad() => h.Run(async () =>
    {
        var t0 = h.Now;
        var meeting = await Recorder.StartAsync();
        h.Microphone.Deliver(Constant(1, 0.2f), t0);
        h.Clock.Advance(5);
        h.Clock.Sleep(300, uptimeCounts: true);
        h.Microphone.Deliver(Constant(1, 0.2f), t0 + 305);
        Assert.Equal(305, Recorder.MarkMoment()!.Time, 6);
        h.Clock.Advance(1);
        var stopped = await Recorder.StopAsync();

        Assert.Equal(306, stopped!.Duration, 6);
        var microphone = RecorderHarness.Read(h.MicrophonePath(meeting.Id));
        Assert.Equal(306 * Rate, microphone.Length);
        Assert.Equal(0, Rms(microphone, 1, 305));
        Assert.Equal(RecorderHarness.Boosted(0.2f), Rms(microphone, 305, 306), 4);
    });

    [Fact]
    public Task AnUptimeJumpTheDiskCannotHoldEndsTheRecordingWhereTheComputerLeft() => h.Run(async () =>
    {
        var meeting = await Recorder.StartAsync();
        h.Clock.Advance(5);
        h.FreeBytes = 150L * 1024 * 1024;
        h.Clock.Sleep(3600, uptimeCounts: true);

        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.AutoStoppedMeeting is not null));
        Assert.Equal(5, h.Store.Meeting(meeting.Id)!.Duration, 6);
        Assert.True(RecorderHarness.Read(h.MicrophonePath(meeting.Id)).Length <= 5 * Rate);
    });

    // MARK: - Marked moments

    /// <summary>
    /// The time of a moment comes from the origin both tracks share, not from when the start
    /// finished: capture starts a moment later, and would place the mark that much late.
    /// </summary>
    [Fact]
    public Task MomentTimeComesFromTheTracksOrigin() => h.Run(async () =>
    {
        var origin = h.Now;
        h.Sources.NewMicrophone = () => new FakeAudioStream { DuringStart = () => h.Clock.Advance(0.4) };
        var meeting = await Recorder.StartAsync();
        Assert.Equal(new MeetingRecorderState.Recording(meeting.Id, origin + 0.4), Recorder.State);

        h.Clock.Advance(12.1);
        var moment = Recorder.MarkMoment("  Решение\nпринято ");

        Assert.NotNull(moment);
        Assert.Equal(12.5, moment.Time, 6);
        Assert.Equal("Решение принято", moment.Note);
        Assert.Equal(HighlightSource.Recording, moment.Source);
        Assert.Equal(h.Clock.Wall.GetUtcNow(), moment.CreatedAt);
        Assert.Equal(moment, Recorder.LastMarkedMoment);
        Assert.Equal(1, Recorder.MarkedCount);
        Assert.Equal(new[] { moment }, h.Store.Meeting(meeting.Id)!.Highlights);
    });

    [Fact]
    public Task MarksWithinTwoSecondsCountAsOne() => h.Run(async () =>
    {
        var meeting = await Recorder.StartAsync();
        h.Clock.Advance(12.5);
        var first = Recorder.MarkMoment()!;
        h.Clock.Advance(1.4);
        Assert.Equal(first, Recorder.MarkMoment("повтор"));
        h.Clock.Advance(1.1);
        var second = Recorder.MarkMoment()!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(15, second.Time, 6);
        h.Clock.Advance(1.9);
        Assert.Equal(second, Recorder.MarkMoment());

        Assert.Equal(new[] { first.Id, second.Id }, h.Store.Meeting(meeting.Id)!.Highlights.Select(m => m.Id));
        Assert.Equal(second, Recorder.LastMarkedMoment);
        Assert.Equal(2, Recorder.MarkedCount);

        var stopped = Recorder.StopImmediately()!;
        Assert.Equal(MeetingStatus.Recorded, stopped.Status);
        Assert.Equal(new[] { first.Id, second.Id }, stopped.Highlights.Select(m => m.Id));
        Assert.Null(Recorder.MarkMoment());

        // A new recording starts without the previous one's last moment.
        var next = await Recorder.StartAsync();
        Assert.Equal(0, Recorder.MarkedCount);
        h.Clock.Advance(0.5);
        var fresh = Recorder.MarkMoment()!;
        Assert.Equal(0.5, fresh.Time, 6);
        Assert.Equal(new[] { fresh }, h.Store.Meeting(next.Id)!.Highlights);
        Assert.Equal(1, Recorder.MarkedCount);
    });

    // MARK: - Levels and elapsed time

    [Fact]
    public Task LevelsReachTheMainThreadAndClearOnStop() => h.Run(async () =>
    {
        var t0 = h.Now;
        await Recorder.StartAsync();
        var changed = new List<string?>();
        Recorder.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // From capture threads, as the platform delivers them.
        await Task.Run(() =>
        {
            h.Microphone.Deliver(Constant(0.5, 0.3f), t0);
            h.System.Deliver(Constant(0.5, 0.3f), t0);
        });
        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.MicrophoneLevel > 0 && Recorder.SystemLevel > 0));
        Assert.Contains(nameof(MeetingRecorder.MicrophoneLevel), changed);
        Assert.True(Recorder.MicrophoneLevel <= 1);

        h.Clock.Advance(0.75);
        Assert.Equal(0.75, Recorder.Elapsed, 6);
        await Recorder.StopAsync();
        Assert.Equal(0, Recorder.MicrophoneLevel);
        Assert.Equal(0, Recorder.SystemLevel);
        Assert.Equal(0.75, Recorder.Elapsed, 6);
    });

    [Fact]
    public Task RecordingTracksFollowTheSwitchAndBothStatuses()
    {
        static MeetingRecordingTracks Tracks(bool on, MicrophoneStatus microphone, SystemAudioStatus system) =>
            MeetingRecordingTracksExtensions.From(on, microphone, system);
        Assert.Equal(MeetingRecordingTracks.MicrophoneAndSystem, Tracks(true, MicrophoneStatus.Capturing, SystemAudioStatus.Capturing));
        Assert.Equal(MeetingRecordingTracks.MicrophoneAndSystem,
            Tracks(true, MicrophoneStatus.Capturing, new SystemAudioStatus(SystemAudioStatusKind.Silent)));
        Assert.Equal(MeetingRecordingTracks.MicrophoneOnly, Tracks(true, MicrophoneStatus.Capturing, SystemAudioStatus.Off));
        Assert.Equal(MeetingRecordingTracks.MicrophoneOnly,
            Tracks(true, MicrophoneStatus.Capturing, new SystemAudioStatus(SystemAudioStatusKind.Unavailable, "нет")));
        Assert.Equal(MeetingRecordingTracks.SystemOnly, Tracks(false, MicrophoneStatus.Capturing, SystemAudioStatus.Capturing));
        Assert.Equal(MeetingRecordingTracks.SystemOnly, Tracks(true, MicrophoneStatus.Lost, SystemAudioStatus.Capturing));
        Assert.Equal(MeetingRecordingTracks.Neither, Tracks(false, MicrophoneStatus.Capturing, SystemAudioStatus.Off));
        return Task.CompletedTask;
    }

    [Fact]
    public Task RecordingTracksChangesAreAnnounced() => h.Run(async () =>
    {
        var changed = new List<string?>();
        ((INotifyPropertyChanged)Recorder).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        await Recorder.StartAsync();
        Assert.Contains(nameof(MeetingRecorder.RecordingTracks), changed);
        changed.Clear();
        Recorder.ToggleMicrophone();
        Assert.Contains(nameof(MeetingRecorder.RecordingTracks), changed);
        Assert.Contains(nameof(MeetingRecorder.LastMicrophoneSwitch), changed);
    });
}
