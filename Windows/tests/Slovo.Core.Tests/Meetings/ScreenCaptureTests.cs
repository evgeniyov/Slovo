using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Tests.Video;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// The screen track's own behaviour (MeetingScreenCapture.swift): pacing, the timeline, key
/// frames, one reconnect after a loss, a lock that pauses, a wake that rebuilds, an encoder that
/// fails — over a fake platform, with the capture thread's work run by the test.
/// </summary>
public sealed class ScreenCaptureTests : IDisposable
{
    const double Origin = 500;
    readonly FakeScreenCapture platform = new();
    readonly ManualUptime uptime = new(Origin + 0.4);
    readonly TemporaryMeetingsFolder folder = new();
    readonly List<string> lost = [];
    readonly List<bool> pauses = [];
    readonly List<Exception> videoFailures = [];
    MeetingScreenWriter? writer;

    public void Dispose() => folder.Dispose();

    string VideoPath => Path.Combine(folder.Paths.MeetingsFolder, "screen.mov");

    MeetingScreenCapture Started(ScreenCaptureTarget? target = null)
    {
        var capture = new MeetingScreenCapture(platform, uptime, Origin, ownThread: false)
        {
            OnLost = lost.Add,
            OnPauseChanged = pauses.Add,
            OnVideoFailed = videoFailures.Add,
        };
        capture.Start(target ?? ScreenCaptureTarget.WholeDisplay, (width, height) => writer = new MeetingScreenWriter(VideoPath, width, height));
        return capture;
    }

    /// <summary>Moves the clock one frame on and takes a picture.</summary>
    void Step(MeetingScreenCapture capture, double seconds = MeetingScreenCapture.FrameInterval)
    {
        uptime.Now += seconds;
        capture.Tick();
    }

    [Fact]
    public void StartSizesTheVideoFromTheMainDisplayAndOpensTheTarget()
    {
        platform.DisplaySize = (2880, 1800);
        var capture = Started(new ScreenCaptureTarget.Application("teams.exe"));
        Assert.Equal((1920, 1200), capture.CanvasSize);
        Assert.Equal((1920, 1200), (platform.Encoder.Width, platform.Encoder.Height));
        Assert.Equal(new ScreenCaptureTarget.Application("teams.exe"), platform.Stream.Target);
        Assert.Equal(1, platform.Subscribers);
        capture.Stop();
        Assert.True(platform.Stream.IsDisposed);
        Assert.True(platform.Encoder.IsDisposed);
        Assert.Equal(0, platform.Subscribers);
    }

    [Fact]
    public void ASystemThatCannotRecordTheScreenSaysSoAndStartsNothing()
    {
        platform.UnavailableReason = "Screen recording needs Windows 10 version 1903 or later";
        var capture = new MeetingScreenCapture(platform, uptime, Origin, ownThread: false);
        var error = Assert.Throws<SlovoException>(() => capture.Start(ScreenCaptureTarget.WholeDisplay, (w, h) => new MeetingScreenWriter(VideoPath, w, h)));
        Assert.Equal(platform.UnavailableReason, error.Message);
        Assert.Empty(platform.Streams);
        Assert.Empty(platform.Encoders);

        platform.UnavailableReason = null;
        platform.DisplaySize = null;
        Assert.Throws<SlovoException>(() => capture.Start(ScreenCaptureTarget.WholeDisplay, (w, h) => new MeetingScreenWriter(VideoPath, w, h)));
    }

    [Fact]
    public void AMissingEncoderOrAnAppThatIsNotRunningFailsTheStartAndReleasesWhatItMade()
    {
        platform.EncoderError = new SlovoException("Screen recording needs the Windows video encoder.");
        var capture = new MeetingScreenCapture(platform, uptime, Origin, ownThread: false);
        var error = Assert.Throws<SlovoException>(() => capture.Start(ScreenCaptureTarget.WholeDisplay, (w, h) => new MeetingScreenWriter(VideoPath, w, h)));
        Assert.Equal("Screen recording needs the Windows video encoder.", error.Message);
        Assert.Empty(platform.Streams);

        platform.EncoderError = null;
        platform.OpenError = MeetingErrors.ScreenRecordingAppNotRunning();
        capture = new MeetingScreenCapture(platform, uptime, Origin, ownThread: false);
        Assert.Throws<SlovoException>(() => capture.Start(new ScreenCaptureTarget.Application("zoom.exe"), (w, h) => new MeetingScreenWriter(VideoPath, w, h)));
        Assert.True(platform.Encoder.IsDisposed);
        Assert.Equal(0, platform.Subscribers);
    }

    [Fact]
    public void PicturesAreTakenFourTimesASecondOnTheMeetingsTimelineAndRepeatedWhenNothingChanges()
    {
        var capture = Started();
        capture.SleptSeconds = 0;
        capture.Tick();
        Step(capture);
        Step(capture);
        // Too soon after the one before: a late timer does not squeeze in a frame.
        Step(capture, 0.05);
        Step(capture);
        var times = platform.Encoder.Encoded.Select(e => Math.Round(e.Time, 3)).ToList();
        Assert.Equal([0.4, 0.65, 0.9, 1.2], times);
        // One picture was captured (white); the ones after are the same picture again.
        Assert.Equal(5, platform.Stream.Reads);
        Assert.All(platform.Encoder.FirstLuma, luma => Assert.Equal(235, luma));
    }

    [Fact]
    public void NothingIsEncodedBeforeTheFirstPicture()
    {
        platform.NewStream = target => new FakeScreenStream(target);
        var capture = Started();
        capture.Tick();
        Step(capture);
        Assert.Empty(platform.Encoder.Encoded);
        platform.Stream.Show(320, 200, 0, 0, 0);
        Step(capture);
        Assert.Single(platform.Encoder.Encoded);
        Assert.Equal(16, platform.Encoder.FirstLuma[0]);
    }

    [Fact]
    public void KeyFramesComeFirstThenEveryTenSecondsAndAfterEveryReopen()
    {
        var capture = Started();
        capture.Tick();
        for (var i = 0; i < 44; i++)
        {
            Step(capture);
        }
        var keys = platform.Encoder.Encoded.Select((e, i) => (e.KeyFrame, i)).Where(p => p.KeyFrame).Select(p => p.i).ToList();
        Assert.Equal([0, 40], keys);

        capture.Handle(ScreenSessionEvent.Resumed);
        Step(capture);
        Assert.True(platform.Encoder.Encoded[^1].KeyFrame);
    }

    [Fact]
    public void SilenceAddedForASleepMovesThePicturesAlongWithTheAudio()
    {
        var capture = Started();
        capture.Tick();
        capture.SleptSeconds = 30;
        Step(capture);
        Assert.Equal(30.65, Math.Round(platform.Encoder.Encoded[^1].Time, 3));
    }

    [Fact]
    public void ALostCaptureReconnectsOnceAndTheSecondLossEndsTheTrack()
    {
        var capture = Started();
        capture.Tick();
        platform.Stream.IsLost = true;
        Step(capture);
        Assert.Equal(2, platform.Streams.Count);
        Assert.True(platform.Streams[0].IsDisposed);
        Assert.Empty(lost);
        Step(capture);
        Assert.True(capture.IsCapturing);

        platform.Stream.ReadError = new InvalidOperationException("device removed");
        Step(capture);
        Assert.Equal(["device removed"], lost);
        Assert.False(capture.IsCapturing);
        Assert.True(platform.Encoder.IsDisposed);
        var encoded = platform.Encoder.Encoded.Count;
        Step(capture);
        Assert.Equal(encoded, platform.Encoder.Encoded.Count);
        // A lost track stays lost: an unlock or a wake does not bring it back.
        capture.Handle(ScreenSessionEvent.Unlocked);
        capture.Handle(ScreenSessionEvent.Resumed);
        Assert.Equal(2, platform.Streams.Count);
    }

    [Fact]
    public void AReconnectThatFailsEndsTheTrackAtOnce()
    {
        var capture = Started(new ScreenCaptureTarget.Application("zoom.exe"));
        capture.Tick();
        platform.Stream.IsLost = true;
        platform.OpenError = MeetingErrors.ScreenRecordingAppNotRunning();
        Step(capture);
        Assert.Equal([MeetingErrors.ScreenRecordingAppNotRunning().Message], lost);
    }

    [Fact]
    public void ALockPausesAndAnUnlockResumesWithoutUsingTheReconnect()
    {
        var capture = Started();
        capture.Tick();
        capture.Handle(ScreenSessionEvent.Locked);
        Assert.Equal([true], pauses);
        Assert.False(capture.IsCapturing);
        var encoded = platform.Encoder.Encoded.Count;
        Step(capture);
        Step(capture);
        Assert.Equal(encoded, platform.Encoder.Encoded.Count);
        // A wake while locked waits for the unlock.
        capture.Handle(ScreenSessionEvent.Resumed);
        Assert.False(capture.IsCapturing);

        capture.Handle(ScreenSessionEvent.Unlocked);
        Assert.Equal([true, false], pauses);
        Assert.True(capture.IsCapturing);
        Step(capture);
        Assert.Equal(encoded + 1, platform.Encoder.Encoded.Count);

        // The reconnect is still there for a real loss.
        platform.Stream.IsLost = true;
        Step(capture);
        Assert.Empty(lost);
        Assert.True(capture.IsCapturing);
    }

    [Fact]
    public void AnUnlockThatCannotReopenEndsTheTrack()
    {
        var capture = Started();
        capture.Handle(ScreenSessionEvent.Locked);
        platform.OpenError = new SlovoException("no display");
        capture.Handle(ScreenSessionEvent.Unlocked);
        Assert.Equal(["no display"], lost);
        Assert.Equal([true], pauses);
    }

    [Fact]
    public void AWakeRebuildsTheCaptureAndKeepsTheFile()
    {
        var capture = Started();
        capture.Tick();
        capture.Handle(ScreenSessionEvent.Resumed);
        Assert.Equal(2, platform.Streams.Count);
        Assert.True(platform.Streams[0].IsDisposed);
        Assert.Single(platform.Encoders);
        Step(capture);
        capture.Stop();
        Assert.True(writer!.Finish());
        Assert.Equal(2, Mp4Reader.Samples(File.ReadAllBytes(VideoPath)).Count);
    }

    [Fact]
    public void AWakeThatCannotRebuildEndsTheTrack()
    {
        var capture = Started();
        platform.OpenError = new SlovoException("no display");
        capture.Handle(ScreenSessionEvent.Resumed);
        Assert.Equal(["no display"], lost);
    }

    [Fact]
    public void AnEncoderThatFailsEndsTheVideoOnce()
    {
        var capture = Started();
        capture.Tick();
        platform.Encoder.EncodeError = new InvalidOperationException("MF_E_UNEXPECTED");
        Step(capture);
        Step(capture);
        Assert.Single(videoFailures);
        Assert.False(capture.IsCapturing);
        Assert.True(platform.Encoder.IsDisposed);
    }

    [Fact]
    public void SessionEventsFromThePlatformReachTheCaptureAndStopWhenItStops()
    {
        var capture = Started();
        platform.Raise(ScreenSessionEvent.Locked);
        Assert.Equal([true], pauses);
        capture.Stop();
        platform.Raise(ScreenSessionEvent.Unlocked);
        Assert.Equal([true], pauses);
        Assert.Single(platform.Streams);
    }

    [Fact]
    public void PicturesReachTheFileThroughAnEncoderThatHoldsThemBack()
    {
        platform.EncoderDelays = true;
        var capture = Started();
        capture.Tick();
        for (var i = 0; i < 12; i++)
        {
            Step(capture);
        }
        capture.Stop();
        Assert.True(writer!.Finish());
        var samples = Mp4Reader.Samples(File.ReadAllBytes(VideoPath));
        // The last picture was still inside the encoder when the capture stopped.
        Assert.Equal(12, samples.Count);
        Assert.True(samples[0].IsSync);
        Assert.Equal(0, samples[0].DecodeTime);
        Assert.Equal(Slovo.Core.Video.FragmentedMp4Writer.Ticks(0.65), samples[1].DecodeTime);
    }

    [Fact]
    public void TheRealThreadTakesPicturesUntilStopped()
    {
        var clock = SystemUptime.Shared;
        var capture = new MeetingScreenCapture(platform, clock, clock.Now);
        capture.Start(ScreenCaptureTarget.WholeDisplay, (width, height) => writer = new MeetingScreenWriter(VideoPath, width, height));
        var deadline = Environment.TickCount64 + 5_000;
        while (platform.Encoders.Count == 0 || platform.Encoder.Encoded.Count < 3)
        {
            Assert.True(Environment.TickCount64 < deadline, "The capture thread took no pictures");
            Thread.Sleep(20);
        }
        platform.Raise(ScreenSessionEvent.Locked);
        deadline = Environment.TickCount64 + 5_000;
        while (!platform.Stream.IsDisposed)
        {
            Assert.True(Environment.TickCount64 < deadline, "The lock did not reach the capture thread");
            Thread.Sleep(20);
        }
        capture.Stop();
        Assert.True(platform.Encoder.IsDisposed);
        Assert.True(writer!.Finish());
    }
}
