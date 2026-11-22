using Slovo.Core.Audio;
using Slovo.Core.Meetings;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// Port of the writer, meter and silence parts of MeetingsAudioTests.swift: tracks placed on
/// the shared timeline with gap filling, overlap trimming, sleep padding, muting and fills for
/// sources that deliver nothing, the microphone boost and the silence detector.
/// </summary>
public sealed class RecorderTimelineTests : IDisposable
{
    const double Origin = 1_000;
    const int Rate = MeetingAudioFormat.SampleRate;
    readonly string folder = Path.Combine(Path.GetTempPath(), "SlovoRecorderTimelineTests-" + Guid.NewGuid().ToString("N"));

    public RecorderTimelineTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    MeetingTrackWriter Writer(string name) => new(Path.Combine(folder, name), Origin);

    static float[] Tone(double seconds = 1, float value = 0.25f) => RecorderHarness.Constant(seconds, value);

    static float Rms(float[] samples, int from, int to) => RecorderHarness.Rms(samples, from, to);

    float[] Read(string name) => RecorderHarness.Read(Path.Combine(folder, name));

    [Fact]
    public void WriterAndReaderRoundTripAt16kHz()
    {
        var writer = Writer("round.caf");
        foreach (var value in new[] { 0.1f, 0.3f, 0.5f })
        {
            writer.Append(Tone(1, value));
        }
        Assert.Equal(48_000, writer.Finish());
        Assert.Equal(3, CafFile.Duration(Path.Combine(folder, "round.caf")), 3);

        using var reader = CafReader.Open(Path.Combine(folder, "round.caf"));
        var middle = reader.ReadRange(1, 2);
        Assert.Equal(16_000, middle.Length);
        Assert.Equal(0.3, middle[0], 3);
        Assert.Equal(0.3, middle[^1], 3);
        var last = reader.ReadRange(2.5, 3.75);
        Assert.Equal(8_000, last.Length);
        Assert.Equal(0.5, last[0], 3);
    }

    [Fact]
    public void LateChunksAreFilledWithSilenceFromTheirTime()
    {
        var writer = Writer("gap.caf");
        writer.Append(Tone(), Origin);
        // Jitter below the tolerance is not filled.
        writer.Append(Tone(), Origin + 1.05);
        writer.Append(Tone(), Origin + 5);
        var frames = writer.Finish();
        Assert.InRange(frames, 6 * Rate - 20, 6 * Rate + 20);

        var samples = Read("gap.caf");
        Assert.Equal(0.25, samples[8_000], 3);
        Assert.Equal(0.25, samples[24_000], 3);
        Assert.Equal(0, samples[48_000]);
        Assert.Equal(0.25, samples[88_000], 3);
    }

    [Fact]
    public void AnEarlyChunkLosesItsOverlapSoTheTrackNeverRunsAhead()
    {
        var writer = Writer("early.caf");
        writer.Append(Tone(2, 0.25f), Origin);
        // A device whose clock runs fast: this buffer says it began half a second before the
        // track's end. Its first half second is left out, so the rest lands where it was said.
        writer.Append(Tone(1, 0.5f), Origin + 1.5);
        // Within the tolerance it is appended as it is.
        writer.Append(Tone(1, 0.75f), Origin + 2.45);
        Assert.Equal(8_000, writer.DroppedFrames);
        var frames = writer.Finish();
        Assert.Equal(56_000, frames);

        var samples = Read("early.caf");
        Assert.Equal(0.25, Rms(samples, 1_000, 31_000), 3);
        Assert.Equal(0.5, Rms(samples, 33_000, 39_000), 3);
        Assert.Equal(0.75, Rms(samples, 41_000, 55_000), 3);

        // A buffer entirely behind the track is left out whole.
        var behind = Writer("behind.caf");
        behind.Append(Tone(3), Origin);
        behind.Append(Tone(1, 0.9f), Origin + 0.5);
        Assert.Equal(3 * Rate, behind.Finish());
        Assert.Equal(0.25, Rms(Read("behind.caf"), 0, 3 * Rate), 3);
    }

    [Fact]
    public void ChunksWithoutATimeFollowTheOnesBefore()
    {
        var writer = Writer("untimed.caf");
        writer.Append(Tone(1, 0.25f), Origin + 2);
        writer.Append(Tone(0.5, 0.5f));
        Assert.Equal((long)(3.5 * Rate), writer.Finish());
        var samples = Read("untimed.caf");
        Assert.Equal(0, Rms(samples, 0, 2 * Rate));
        Assert.Equal(0.5, Rms(samples, 3 * Rate, (int)(3.5 * Rate)), 3);
    }

    [Fact]
    public void ASleepPadKeepsLaterBuffersOnTheTimeline()
    {
        var writer = Writer("sleep.caf");
        writer.Append(Tone(), Origin);
        // Five seconds of sleep the uptime clock did not count: the buffer after the wake is
        // stamped one second past the origin; without the pad the track would lose the five seconds.
        Assert.Equal(5 * Rate, writer.PadSilence(5));
        writer.Append(Tone(), Origin + 1);
        var frames = writer.Finish();

        Assert.Equal(7 * Rate, frames);
        var samples = Read("sleep.caf");
        Assert.Equal(0.25, Rms(samples, 1_000, 15_000), 3);
        Assert.Equal(0, Rms(samples, 17_000, 95_000));
        Assert.Equal(0.25, Rms(samples, 97_000, 111_000), 3);
    }

    /// <summary>
    /// A pad longer than the machine has been awake. Taking it off the origin would wrap the
    /// origin below zero, and every later buffer would land at frame 0.
    /// </summary>
    [Fact]
    public void APadLongerThanTheAwakeUptimeKeepsLaterBuffersOnTheTimeline()
    {
        var writer = new MeetingTrackWriter(Path.Combine(folder, "longsleep.caf"), origin: 10);
        writer.Append(Tone(), 10);
        Assert.Equal(30 * Rate, writer.PadSilence(30));
        writer.Append(Tone(), 12);
        Assert.Equal(33 * Rate, writer.Finish());

        var samples = Read("longsleep.caf");
        Assert.Equal(0.25, Rms(samples, 1_000, 15_000), 3);
        Assert.Equal(0, Rms(samples, 17_000, 510_000));
        Assert.Equal(0.25, Rms(samples, 513_000, 527_000), 3);
    }

    /// <summary>
    /// Muting is how a meeting's microphone is switched off: every buffer still lands with its
    /// own length and at its own time, only as silence.
    /// </summary>
    [Fact]
    public void AMutedWriterWritesSilenceOfTheSameLengthAtTheSameTime()
    {
        var writer = Writer("muted.caf");
        writer.Append(Tone(), Origin);
        writer.IsMuted = true;
        writer.Append(Tone(), Origin + 1);
        Assert.Equal(2 * Rate, writer.FrameCount);
        // A capture gap while muted is filled like any other.
        writer.Append(Tone(), Origin + 4);
        Assert.Equal(5 * Rate, writer.FrameCount);
        // So is a sleep.
        Assert.Equal(2 * Rate, writer.PadSilence(2));
        // A buffer without a capture time, like a resampler's tail, is gated too.
        writer.Append(Tone(0.01));
        writer.IsMuted = false;
        writer.Append(Tone(), Origin + 5);
        var frames = writer.Finish();

        Assert.Equal(8 * Rate + 160, frames);
        var samples = Read("muted.caf");
        Assert.Equal(0.25, Rms(samples, 1_000, 15_000), 3);
        Assert.Equal(0, Rms(samples, 16_000, 7 * Rate + 160));
        Assert.Equal(0.25, Rms(samples, 7 * Rate + 1_000, 8 * Rate - 1_000), 3);
        var unused = Writer("unused.caf");
        Assert.False(unused.IsMuted);
        unused.Finish();
    }

    [Fact]
    public void SleepPadIsCappedByTheGapSafeguard()
    {
        Assert.Equal(12, MeetingTimeline.PadSeconds(12));
        Assert.Equal(0, MeetingTimeline.PadSeconds(0));
        Assert.Equal(0, MeetingTimeline.PadSeconds(-30));
        Assert.Equal(0, MeetingTimeline.PadSeconds(double.NaN));
        Assert.Equal(MeetingTimeline.MaximumGap, MeetingTimeline.PadSeconds(MeetingTimeline.MaximumGap + 600));
        Assert.True(MeetingTimeline.MaximumSleepPad > MeetingTimeline.MaximumGap);
    }

    /// <summary>A queued pad lands where the sleep was, before any buffer appended after it.</summary>
    [Fact]
    public void AQueuedSleepPadKeepsLaterBuffersOnTheTimelineAndSoundIsTracked()
    {
        var writer = Writer("queued.caf");
        writer.Append(new float[Rate], Origin);
        Assert.False(writer.HasSound, "silence is not sound");
        writer.EnqueueSilence(5);
        writer.Append(Tone(), Origin + 1);
        Assert.True(writer.HasSound);
        Assert.Equal(7 * Rate, writer.Finish());

        var samples = Read("queued.caf");
        Assert.Equal(0, Rms(samples, 0, 96_000));
        Assert.Equal(0.25, Rms(samples, 97_000, 111_000), 3);
    }

    /// <summary>
    /// Loopback delivers nothing while nothing plays: the track is filled up to a time without
    /// ever running past it, and a later buffer still lands at its own time.
    /// </summary>
    [Fact]
    public void AFillBringsAQuietTrackUpToATimeAndNoFurther()
    {
        var writer = Writer("fill.caf");
        writer.EnqueueFillTo(Origin + 3);
        Assert.Equal(3 * Rate, writer.FrameCount);
        // A fill to a time the track has passed changes nothing.
        writer.EnqueueFillTo(Origin + 1);
        Assert.Equal(3 * Rate, writer.FrameCount);
        writer.Append(Tone(), Origin + 5);
        writer.EnqueueFillTo(Origin + 8);
        // The fill counts sleeps like every other position.
        writer.PadSilence(10);
        writer.EnqueueFillTo(Origin + 9);
        Assert.Equal(19 * Rate, writer.Finish());

        var samples = Read("fill.caf");
        Assert.Equal(0, Rms(samples, 0, 5 * Rate));
        Assert.Equal(0.25, Rms(samples, 5 * Rate + 100, 6 * Rate - 100), 3);
        Assert.Equal(0, Rms(samples, 6 * Rate, 19 * Rate));
    }

    [Fact]
    public void TheFirstWriteFailureStopsTheTrackAndIsReportedOnce()
    {
        var writer = Writer("full.caf");
        var failures = new List<string>();
        writer.OnWriteFailure = error =>
        {
            lock (failures)
            {
                failures.Add(error.Message);
            }
        };
        writer.WriteHook = _ => throw new IOException("the disk is full");

        writer.Append(Tone(), Origin);
        // A gap worth a hundred seconds of silence: once writing has failed the fill must not
        // run at all, rather than do ever more work for nothing.
        writer.Append(Tone(), Origin + 100);
        Assert.Equal(0, writer.PadSilence(30));
        writer.EnqueueFillTo(Origin + 200);
        Assert.Equal(0, writer.Finish());

        Assert.Equal(new[] { "the disk is full" }, failures);
        // Closing is idempotent, and appending after it is ignored.
        writer.Append(Tone());
        Assert.Equal(0, writer.Finish());
    }

    [Fact]
    public void ATrackThatFailsLaterKeepsWhatItWrote()
    {
        var writer = Writer("partial.caf");
        writer.Append(Tone(1, 0.3f), Origin);
        Assert.Equal(Rate, writer.FrameCount);
        writer.WriteHook = _ => throw new IOException("the disk is full");
        writer.Append(Tone(1, 0.3f), Origin + 1);
        Assert.Equal(Rate, writer.Finish());
        var samples = Read("partial.caf");
        Assert.Equal(Rate, samples.Length);
        Assert.Equal(0.3, Rms(samples, 0, Rate), 3);
    }

    [Fact]
    public void FramesAreCountedOnTheTimelineFromTheOrigin()
    {
        Assert.Equal(0, MeetingTimeline.Frame(999, 1_000));
        Assert.Equal(0, MeetingTimeline.Frame(1_000, 1_000));
        Assert.Equal(200_000, MeetingTimeline.Frame(1_012.5, 1_000));
        // 3.3 s is 3.29999… as a double; it is still frame 52 800.
        Assert.Equal(52_800, MeetingTimeline.Frame(1_003.3, 1_000));
        Assert.Equal(0, MeetingTimeline.Frame(double.NaN, 1_000));
        Assert.Equal(1_600, MeetingTimeline.Frames(0.1));
        Assert.Equal(0, MeetingTimeline.Frames(-1));
    }

    [Fact]
    public void MicrophoneBoostRaisesQuietSpeechWithoutHardClippingLoudSpeech()
    {
        Assert.Equal(new[] { 0f }, MeetingAudioMath.MicrophoneBoost([0f]));
        // A quiet voice comes out close to the full +9 dB (×2.82) boost.
        Assert.Equal(0.05 * 2.818, MeetingAudioMath.MicrophoneBoost([0.05f])[0], 0.001);
        // A voice already near full scale is compressed, not clipped flat, and stays within ±1.
        var loud = MeetingAudioMath.MicrophoneBoost([0.9f, 1f, -1f]);
        Assert.True(loud[0] < 0.9f * 2.818f);
        Assert.All(loud, sample => Assert.True(Math.Abs(sample) <= 1));
        Assert.Equal(-MeetingAudioMath.MicrophoneBoost([0.2f])[0], MeetingAudioMath.MicrophoneBoost([-0.2f])[0], 6);
    }

    [Fact]
    public void TheMeetingMeterRisesQuicklyAndFallsSlowly()
    {
        Assert.Equal(0.6f, MeetingAudioMath.SmoothedLevel(0, 1), 5);
        Assert.Equal(0.75f, MeetingAudioMath.SmoothedLevel(1, 0), 5);
        Assert.Equal(0.5f, MeetingAudioMath.SmoothedLevel(0.5f, 0.5f));
        Assert.True(MeetingAudioMath.IsSilent([0f, 0f, -0f]));
        Assert.False(MeetingAudioMath.IsSilent([0f, 1e-6f]));
        Assert.True(MeetingAudioMath.IsSilent([]));
    }

    [Fact]
    public void SilenceDetectorReportsOnlyZerosOnceAndRecovers()
    {
        var detector = new SilenceDetector(3);
        Assert.Null(detector.Consume(139_200, 48_000, allZero: true)); // 2.9 s
        Assert.Equal(SilenceEvent.Silence, detector.Consume(4_800, 48_000, allZero: true));
        Assert.Null(detector.Consume(480_000, 48_000, allZero: true));
        Assert.Equal(SilenceEvent.SignalAfterSilence, detector.Consume(480, 48_000, allZero: false));
        Assert.Null(detector.Consume(480, 48_000, allZero: false));
        Assert.True(detector.HeardSignal);
    }

    [Fact]
    public void SilenceDetectorIgnoresQuietAfterRealAudio()
    {
        var detector = new SilenceDetector(3);
        Assert.Null(detector.Consume(480, 48_000, allZero: false));
        Assert.Null(detector.Consume(480_000, 48_000, allZero: true));
        Assert.Null(detector.NoAudio(10));

        var starved = new SilenceDetector(3);
        Assert.Null(starved.NoAudio(2));
        Assert.Equal(SilenceEvent.Silence, starved.NoAudio(3.5));
        Assert.Null(starved.Consume(480_000, 48_000, allZero: true));
        Assert.Null(starved.Consume(0, 48_000, allZero: false));
    }

    [Fact]
    public void LevelMeterReadsAboutThirtyTimesPerSecond()
    {
        var meter = new LevelMeter();
        Assert.Equal(533, meter.WindowLength);
        Assert.Null(meter.Consume(Tone(100.0 / Rate, 0.5f)));
        Assert.Equal((float?)0, meter.Consume(new float[2_000]));
        var loud = Enumerable.Range(0, 1_600).Select(i => (float)Math.Sin(2 * Math.PI * 400 * i / Rate)).ToArray();
        Assert.Equal(0.94, meter.Consume(loud)!.Value, 0.02);
    }
}
