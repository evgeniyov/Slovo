using Slovo.Core.Meetings;

namespace Slovo.Core.Tests.Meetings;

public class GigaAMChunkerTests
{
    const int Rate = 16_000;

    /// <summary>A loud tone with silent stretches at the given seconds.</summary>
    static float[] Speech(double seconds, params (double Start, double End)[] pauses)
    {
        var samples = new float[(int)(seconds * Rate)];
        for (var index = 0; index < samples.Length; index++)
        {
            var time = (double)index / Rate;
            samples[index] = pauses.Any(pause => time >= pause.Start && time < pause.End) ? 0 : (float)(0.3 * Math.Sin(index * 2 * Math.PI * 220 / Rate));
        }
        return samples;
    }

    static void AssertCovers(IReadOnlyList<(int Start, int End)> ranges, int length, double maxSeconds = 20)
    {
        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(length, ranges[^1].End);
        for (var index = 1; index < ranges.Count; index++)
        {
            Assert.Equal(ranges[index - 1].End, ranges[index].Start);
        }
        Assert.All(ranges, range => Assert.InRange(range.End - range.Start, 1, (int)(maxSeconds * Rate)));
    }

    [Fact]
    public void EmptyAndShortRecordingsAreOnePieceOrNothing()
    {
        Assert.Empty(GigaAMChunker.Ranges([]));
        Assert.Equal([(0, 5)], GigaAMChunker.Ranges(new float[5]));
        Assert.Equal([(0, 20 * Rate)], GigaAMChunker.Ranges(new float[20 * Rate]));
    }

    [Fact]
    public void LongRecordingsAreCutAtThePauseNearTheEndOfEachWindow()
    {
        // Pauses at 17 s and 36 s: inside the last 5 s of the first and second windows.
        var samples = Speech(50, (17.0, 17.5), (36.0, 36.4));
        var ranges = GigaAMChunker.Ranges(samples);

        AssertCovers(ranges, samples.Length);
        Assert.Equal(3, ranges.Count);
        Assert.InRange(ranges[0].End / (double)Rate, 17.0, 17.5);
        Assert.InRange(ranges[1].End / (double)Rate, 36.0, 36.4);
    }

    [Fact]
    public void WithoutAPauseTheCutStaysInsideTheWindow()
    {
        var samples = Speech(65);
        var ranges = GigaAMChunker.Ranges(samples);

        AssertCovers(ranges, samples.Length);
        Assert.Equal(4, ranges.Count);
    }

    [Fact]
    public void PausesBeforeTheSearchRegionAreIgnored()
    {
        // 10 s is outside the last 5 s of the first 20 s window.
        var samples = Speech(30, (10.0, 11.0));
        var ranges = GigaAMChunker.Ranges(samples);

        AssertCovers(ranges, samples.Length);
        Assert.InRange(ranges[0].End / (double)Rate, 15.0, 20.0);
    }

    [Fact]
    public void AShortStopClosureDoesNotLookLikeAPause()
    {
        // 30 ms of silence (a stop consonant) at 16 s and a real 400 ms pause at 18 s.
        var samples = Speech(30, (16.0, 16.03), (18.0, 18.4));
        var cut = GigaAMChunker.Ranges(samples)[0].End / (double)Rate;
        Assert.InRange(cut, 18.0, 18.4);
    }

    [Fact]
    public void TiesGoToTheLaterFrame()
    {
        var silence = new float[Rate];
        var point = GigaAMChunker.QuietestPoint(silence, 0, Rate, frameLength: 480, smoothingFrames: 5);
        var frames = Rate / 480;
        Assert.Equal((frames - 1) * 480 + 240, point);
        Assert.Equal(100, GigaAMChunker.QuietestPoint(silence, 10, 100, frameLength: 480, smoothingFrames: 5));
    }

    [Fact]
    public void CustomLimitsAreRespected()
    {
        var samples = Speech(12, (4.3, 4.8), (9.0, 9.5));
        var ranges = GigaAMChunker.Ranges(samples, maxSeconds: 5, searchSeconds: 1);

        AssertCovers(ranges, samples.Length, maxSeconds: 5);
        Assert.InRange(ranges[0].End / (double)Rate, 4.3, 4.8);
        Assert.InRange(ranges[1].End / (double)Rate, 9.0, 9.5);
    }
}
