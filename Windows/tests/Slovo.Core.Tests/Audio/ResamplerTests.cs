using Slovo.Core.Audio;

namespace Slovo.Core.Tests.Audio;

public class ResamplerTests
{
    static float[] Sine(double frequency, int rate, int count, double amplitude = 0.3) =>
        Enumerable.Range(0, count).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate))).ToArray();

    /// <summary>RMS away from both ends, where the filter's start-up and tail live.</summary>
    static double MiddleRms(float[] samples)
    {
        var from = samples.Length / 8;
        var to = samples.Length - from;
        return LevelMeter.Rms(samples.AsSpan(from, to - from));
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(96_000)]
    [InlineData(22_050)]
    [InlineData(32_000)]
    [InlineData(11_025)]
    [InlineData(8_000)]
    [InlineData(192_000)]
    [InlineData(44_056)]
    public void OneSecondBecomesExactlyOneSecondAt16k(int rate)
    {
        var output = Resampler.Resample(Sine(440, rate, rate), rate);
        Assert.Equal(16_000, output.Length);
    }

    [Theory]
    [InlineData(44_100, 1_000)]
    [InlineData(48_000, 4_799)]
    [InlineData(96_000, 12_345)]
    public void LengthIsTheCeilingOfTheScaledInput(int rate, int count)
    {
        var output = Resampler.Resample(new float[count], rate);
        Assert.Equal((int)Math.Ceiling(count * 16_000.0 / rate), output.Length);
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(44_056)]
    [InlineData(8_000)]
    public void StreamingInChunksMatchesOneShot(int rate)
    {
        var input = Sine(700, rate, rate / 2);
        var expected = Resampler.Resample(input, rate);

        var resampler = new Resampler(rate);
        var random = new Random(7);
        var output = new List<float>();
        var position = 0;
        while (position < input.Length)
        {
            var size = Math.Min(random.Next(1, 3_000), input.Length - position);
            output.AddRange(resampler.Process(input.AsSpan(position, size)));
            position += size;
        }
        output.AddRange(resampler.Flush());
        Assert.Equal(expected, output.ToArray());
    }

    [Fact]
    public void FlushStartsANewStream()
    {
        var resampler = new Resampler(48_000);
        var first = resampler.Process(Sine(300, 48_000, 4_800)).Concat(resampler.Flush()).ToArray();
        Assert.Equal(0, resampler.InputCount);
        Assert.Equal(0, resampler.OutputCount);
        var second = resampler.Process(Sine(300, 48_000, 4_800)).Concat(resampler.Flush()).ToArray();
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(96_000)]
    [InlineData(8_000)]
    public void DirectCurrentKeepsItsLevel(int rate)
    {
        var input = Enumerable.Repeat(0.5f, rate).ToArray();
        var output = Resampler.Resample(input, rate);
        for (var i = 1_000; i < output.Length - 1_000; i++)
        {
            Assert.Equal(0.5, output[i], 1e-4);
        }
    }

    [Theory]
    [InlineData(44_100, 1_000)]
    [InlineData(48_000, 1_000)]
    [InlineData(96_000, 3_000)]
    [InlineData(48_000, 6_500)]
    [InlineData(44_100, 7_000)]
    [InlineData(8_000, 1_000)]
    [InlineData(22_050, 5_000)]
    public void PassbandSinesKeepTheirAmplitude(int rate, double frequency)
    {
        var output = Resampler.Resample(Sine(frequency, rate, rate, amplitude: 0.3), rate);
        Assert.Equal(0.3 / Math.Sqrt(2), MiddleRms(output), 1e-3);
    }

    [Fact]
    public void AToneComesOutAtItsFrequency()
    {
        var output = Resampler.Resample(Sine(1_000, 44_100, 44_100), 44_100);
        var crossings = 0;
        for (var i = 1; i < output.Length; i++)
        {
            if (output[i - 1] < 0 && output[i] >= 0)
            {
                crossings++;
            }
        }
        Assert.InRange(crossings, 998, 1_001);
    }

    [Theory]
    [InlineData(48_000, 9_000)]
    [InlineData(48_000, 12_000)]
    [InlineData(48_000, 20_000)]
    [InlineData(44_100, 8_400)]
    [InlineData(44_100, 15_000)]
    [InlineData(96_000, 30_000)]
    public void FrequenciesAboveTheNewNyquistDoNotFoldBack(int rate, double frequency)
    {
        var output = Resampler.Resample(Sine(frequency, rate, rate, amplitude: 1), rate);
        // At least 60 dB down (the design aims at 80): folded tones would sit inside the speech band.
        Assert.True(MiddleRms(output) < 0.707 * 1e-3, $"{frequency} Hz leaked at RMS {MiddleRms(output)}");
    }

    [Fact]
    public void OutputSamplesSitAtTheirSourcePositions()
    {
        // A slow ramp passes a symmetric, unity-gain filter unchanged, so any delay or
        // drift between output index and input position would show directly.
        const int rate = 44_100;
        var input = Enumerable.Range(0, rate).Select(i => (float)(i / (double)rate)).ToArray();
        var resampler = new Resampler(rate);
        var output = resampler.Process(input).Concat(resampler.Flush()).ToArray();
        var check = new Resampler(rate);
        for (var n = 500; n < output.Length - 500; n += 97)
        {
            var expected = check.SourcePosition(n) / rate;
            Assert.Equal(expected, output[n], 1e-4);
        }
        Assert.Equal(3.0, new Resampler(48_000).SourcePosition(1), 9);
        Assert.Equal(441.0 / 160.0, new Resampler(44_100).SourcePosition(1), 9);
    }

    [Fact]
    public void UpsamplingInterpolatesBetweenSamples()
    {
        var output = Resampler.Resample(Sine(1_000, 8_000, 8_000), 8_000);
        Assert.Equal(16_000, output.Length);
        Assert.Equal(0.3 / Math.Sqrt(2), MiddleRms(output), 1e-3);
    }

    [Fact]
    public void MatchingRatesPassThrough()
    {
        var resampler = new Resampler(16_000);
        Assert.Equal([1f, 2f, 3f], resampler.Process([1f, 2f, 3f]));
        Assert.Empty(resampler.Flush());
        Assert.Equal(0, resampler.Latency);
    }

    [Fact]
    public void LookAheadIsHeldBackUntilFlush()
    {
        var resampler = new Resampler(48_000);
        var body = resampler.Process(new float[48_000]);
        Assert.True(body.Length < 16_000);
        Assert.True(body.Length >= 16_000 - resampler.Latency / 3 - 1);
        var tail = resampler.Flush();
        Assert.Equal(16_000, body.Length + tail.Length);
        Assert.Empty(resampler.Process([]));
    }

    [Fact]
    public void RejectsImpossibleRates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Resampler(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Resampler(-44_100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Resampler(48_000, 0));
    }
}
