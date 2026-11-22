using Slovo.Core.Audio;
using Slovo.Core.Meetings;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// <see cref="SpeechSegmenter"/> on synthetic signals: pauses, long speech, silence, clicks and
/// the streaming file path (port of MeetingsSegmenterTests.swift).
/// </summary>
public sealed class SpeechSegmenterTests : IDisposable
{
    const int Rate = 16_000;
    readonly string temporary;

    public SpeechSegmenterTests()
    {
        temporary = Path.Combine(Path.GetTempPath(), "SlovoMeetingsSegmenterTests-" + Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(temporary);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(temporary, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best effort, as the Swift tearDown's try?.
        }
    }

    [Fact]
    public void SilenceAndQuietNoiseHaveNoSegments()
    {
        var segmenter = new SpeechSegmenter();
        Assert.Empty(segmenter.Segments(Array.Empty<float>()));
        Assert.Empty(segmenter.Segments(Silence(30)));
        // RMS 0.004 stays under the default 0.005 threshold.
        var hum = Enumerable.Range(0, Rate * 5).Select(i => i % 2 == 0 ? 0.004f : -0.004f).ToArray();
        Assert.Empty(segmenter.Segments(hum));
        Assert.Empty(new SpeechSegmenter(silenceRms: 0.5f).Segments(Tone(3)));
    }

    [Fact]
    public void PausesSeparateSegmentsWithPadding()
    {
        float[] signal = [.. Silence(2), .. Tone(3), .. Silence(4), .. Tone(2), .. Silence(1)];
        var segments = new SpeechSegmenter().Segments(signal);
        Assert.Equal(2, segments.Count);
        var first = segments[0];
        var second = segments[^1];
        Assert.Equal(1.8, first.Start, 0.001);
        Assert.Equal(5.2, first.End, 0.001);
        Assert.Equal(8.8, second.Start, 0.001);
        Assert.Equal(11.2, second.End, 0.001);
        Assert.Equal(new SampleRange(28_800, 83_200), first.Samples);
        Assert.Equal(second.Start, (double)second.Samples.Lower / Rate, 1e-9);
    }

    [Fact]
    public void ShortPauseKeepsOneSegment()
    {
        float[] signal = [.. Tone(2), .. Silence(0.5), .. Tone(2)];
        var segments = new SpeechSegmenter().Segments(signal);
        var segment = Assert.Single(segments);
        Assert.Equal(new SampleRange(0, signal.Length), segment.Samples);
    }

    [Fact]
    public void LongSpeechIsCutAtQuietMomentsIntoPiecesOfAtMostMaxSeconds()
    {
        // Continuous speech with two short dips, each shorter than a pause.
        float[] signal = [.. Tone(17), .. Silence(0.3), .. Tone(17.7), .. Silence(0.3), .. Tone(14.7)];
        var segments = new SpeechSegmenter().Segments(signal);
        Assert.Equal(3, segments.Count);
        foreach (var segment in segments)
        {
            Assert.True(segment.End - segment.Start <= 20 + 1e-9, $"{segment}");
        }
        Assert.Equal(0L, segments[0].Samples.Lower);
        Assert.Equal(17.15, segments[0].End, 0.1);
        Assert.Equal(35.15, segments[1].End, 0.1);
        Assert.Equal(signal.Length, segments[^1].Samples.Upper);
        foreach (var (left, right) in segments.Zip(segments.Skip(1)))
        {
            Assert.Equal(left.Samples.Upper, right.Samples.Lower);
        }
    }

    [Fact]
    public void SteadySoundWithoutPausesIsChunkedCompletely()
    {
        var signal = Tone(45);
        var segments = new SpeechSegmenter().Segments(signal);
        Assert.Equal(3, segments.Count);
        Assert.Equal(0L, segments[0].Samples.Lower);
        Assert.Equal(signal.Length, segments[^1].Samples.Upper);
        Assert.All(segments, segment => Assert.True(segment.Samples.Count <= 20 * Rate, $"{segment}"));
    }

    [Fact]
    public void MaxSecondsIsConfigurable()
    {
        var segments = new SpeechSegmenter(maxSeconds: 5).Segments(Tone(12));
        Assert.Equal(3, segments.Count);
        Assert.All(segments, segment => Assert.True(segment.End - segment.Start <= 5 + 1e-9, $"{segment}"));
    }

    [Fact]
    public void ClicksAreNotSpeech()
    {
        float[] signal = [.. Silence(2), .. Tone(0.05, amplitude: 0.8f), .. Silence(2)];
        Assert.Empty(new SpeechSegmenter().Segments(signal));
    }

    [Fact]
    public void OtherSampleRatesUseTheirOwnTimeline()
    {
        const int rate44 = 44_100;
        float[] signal =
        [
            .. new float[rate44 * 2],
            .. Enumerable.Range(0, rate44 * 2).Select(i => (float)(0.2 * Math.Sin(2 * Math.PI * 220 * i / rate44))),
        ];
        var segments = new SpeechSegmenter().Segments(signal, rate44);
        Assert.NotEmpty(segments);
        var segment = segments[0];
        Assert.Equal(1.8, segment.Start, 0.03);
        Assert.Equal(4, segment.End, 0.03);
    }

    [Fact]
    public void FileSegmentsMatchInMemorySegments()
    {
        float[] signal = [.. Silence(1.5), .. Tone(4), .. Silence(3), .. Tone(25), .. Silence(2)];
        var path = Path.Combine(temporary, "track.caf");
        using (var writer = CafWriter.Create(path))
        {
            for (var start = 0; start < signal.Length; start += 4_000)
            {
                writer.Append(signal.AsSpan(start, Math.Min(signal.Length, start + 4_000) - start));
            }
            writer.Close();
        }

        var fromFile = new SpeechSegmenter().SegmentsInFile(path);
        var inMemory = new SpeechSegmenter().Segments(signal);
        Assert.Equal(inMemory, fromFile);
        Assert.Equal(3, fromFile.Count);
    }

    // MARK: - Signals

    static float[] Tone(double seconds, float amplitude = 0.2f, double frequency = 220) =>
        Enumerable.Range(0, (int)(seconds * Rate))
            .Select(i => amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / Rate))
            .ToArray();

    static float[] Silence(double seconds) => new float[(int)(seconds * Rate)];
}
