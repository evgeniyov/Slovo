using Slovo.Core.Audio;
using Slovo.Core.Platform;

namespace Slovo.Core.Tests.Audio;

public class TimeStretcherTests
{
    const int Rate = 16_000;

    /// <summary>A voiced sound: a fundamental and four weaker harmonics.</summary>
    static float[] Voice(double fundamental, int count, double amplitude = 0.3) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var t = (double)i / Rate;
            var sum = 0.0;
            var weights = new[] { 1, 0.6, 0.4, 0.25, 0.15 };
            for (var h = 0; h < weights.Length; h++)
            {
                sum += weights[h] * Math.Sin(2 * Math.PI * fundamental * (h + 1) * t + h);
            }
            return (float)(amplitude * sum / 2.4);
        }).ToArray();

    static float[] Sine(double frequency, int count, double amplitude = 0.5) =>
        Enumerable.Range(0, count).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / Rate))).ToArray();

    static TimeStretcher Stretcher(float[] input)
    {
        var source = new MemoryAudioSource(input, Rate);
        return new TimeStretcher(Rate, source.Read);
    }

    static float[] Stretch(float[] input, double rate, int count, double start = 0, int chunk = 1000)
    {
        var stretcher = Stretcher(input);
        stretcher.Reset(start, rate);
        return ReadAll(stretcher, count, chunk);
    }

    static float[] ReadAll(TimeStretcher stretcher, int count, int chunk)
    {
        var output = new float[count];
        for (var at = 0; at < count; at += chunk)
        {
            stretcher.Read(output.AsSpan(at, Math.Min(chunk, count - at)));
        }
        return output;
    }

    /// <summary>The period, in samples, of the strongest normalised autocorrelation between the lags.</summary>
    static double Period(float[] x, int from, int length, int minLag, int maxLag)
    {
        var scores = new double[maxLag + 2];
        for (var lag = minLag - 1; lag <= maxLag + 1; lag++)
        {
            double product = 0, first = 0, second = 0;
            for (var i = from; i < from + length; i++)
            {
                product += x[i] * x[i + lag];
                first += x[i] * x[i];
                second += x[i + lag] * x[i + lag];
            }
            scores[lag] = product / Math.Sqrt(first * second + 1e-20);
        }
        var best = minLag;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            if (scores[lag] > scores[best])
            {
                best = lag;
            }
        }
        // A parabola through the peak and its neighbours places it between samples.
        var (a, b, c) = (scores[best - 1], scores[best], scores[best + 1]);
        var denominator = a - 2 * b + c;
        return Math.Abs(denominator) < 1e-12 ? best : best + 0.5 * (a - c) / denominator;
    }

    [Fact]
    public void AtOneTimesTheOutputIsTheInputSampleForSample()
    {
        var input = Voice(137, Rate * 2);
        foreach (var start in new[] { 0, 3_217 })
        {
            var output = Stretch(input, 1, Rate, start, chunk: 777);
            for (var i = 0; i < output.Length; i++)
            {
                Assert.Equal(input[start + i], output[i], 1e-6f);
            }
        }
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void KeepsThePitchAtEverySpeed(double rate)
    {
        // 150 Hz: a period of 106.7 samples, not a whole number.
        var input = Voice(150, Rate * 4);
        var output = Stretch(input, rate, Rate);
        var period = Period(output, Rate / 4, Rate / 2, 60, 200);
        Assert.InRange(period, Rate / 150.0 - 1, Rate / 150.0 + 1);
        // Changing the speed by resampling would have moved it to this.
        Assert.True(Math.Abs(period - Rate / 150.0 / rate) > 10);
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void FollowsAVoiceWhosePitchChanges(double rate)
    {
        // Intonation: a voice gliding from 110 to 180 Hz over four seconds. At each moment the
        // output has the pitch the input had at the moment it stands for.
        var input = new float[Rate * 6];
        var phase = 0.0;
        for (var i = 0; i < input.Length; i++)
        {
            phase += 2 * Math.PI * (110 + 70 * Math.Min(1, i / (Rate * 4.0))) / Rate;
            input[i] = (float)(0.3 * (Math.Sin(phase) + 0.5 * Math.Sin(2 * phase + 1) + 0.3 * Math.Sin(3 * phase + 2)) / 1.8);
        }
        var output = Stretch(input, rate, (int)(Rate * 3.5 / rate) + 2_000);
        foreach (var inputSeconds in new[] { 1.0, 2.0, 3.0 })
        {
            var expected = Rate / (110 + 70 * inputSeconds / 4);
            Assert.InRange(Period(output, (int)(inputSeconds / rate * Rate), 800, 60, 200), expected - 1.5, expected + 1.5);
        }
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void MovesThroughTheInputAtTheRate(double rate)
    {
        // Quiet noise with a 1 kHz burst at 1.2 s: it must sound at 1.2 s ÷ rate.
        var random = new Random(7);
        var input = Enumerable.Range(0, Rate * 4).Select(_ => (float)((random.NextDouble() - 0.5) * 0.002)).ToArray();
        var burstStart = (int)(1.2 * Rate);
        for (var i = 0; i < Rate / 20; i++)
        {
            input[burstStart + i] += (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / Rate));
        }
        var stretcher = Stretcher(input);
        stretcher.Reset(0, rate);
        var output = ReadAll(stretcher, (int)(Rate * 3.5 / rate), 512);
        Assert.Equal(output.Length * rate, stretcher.Position, 6);

        var onset = Array.FindIndex(output, sample => Math.Abs(sample) > 0.1f);
        Assert.InRange(onset / (double)Rate, 1.2 / rate - 0.025, 1.2 / rate + 0.025);
        // The burst lasts about as long as before at the new speed, a frame's overlap either way.
        var end = Array.FindLastIndex(output, sample => Math.Abs(sample) > 0.1f);
        Assert.InRange((end - onset) / (double)Rate, 0.05 / rate - 0.035, 0.05 / rate + 0.035);
    }

    [Fact]
    public void KeepsTheLevelWithoutClicks()
    {
        // 190 Hz: a period of 84.2 samples, so frames cannot line up to the sample exactly.
        var input = Sine(190, Rate * 3);
        var steepest = Enumerable.Range(1, input.Length - 1).Max(i => Math.Abs(input[i] - input[i - 1]));
        foreach (var rate in new[] { 0.75, 1.5, 2 })
        {
            var output = Stretch(input, rate, Rate);
            var middle = output.AsSpan(Rate / 10, Rate * 8 / 10);
            Assert.InRange((double)LevelMeter.Rms(middle), 0.95 * 0.5 / Math.Sqrt(2), 1.05 * 0.5 / Math.Sqrt(2));
            for (var i = 1; i < output.Length; i++)
            {
                Assert.True(Math.Abs(output[i] - output[i - 1]) < 1.5 * steepest, $"A jump at {i} at {rate}×");
            }
        }
    }

    [Fact]
    public void StartsExactlyWhereAsked()
    {
        var input = Voice(123, Rate * 2);
        var output = Stretch(input, 1.5, 200, start: 8_000);
        for (var i = 0; i < output.Length; i++)
        {
            Assert.Equal(input[8_000 + i], output[i], 1e-6f);
        }
    }

    [Fact]
    public void HowItIsReadDoesNotChangeWhatComesOut()
    {
        var input = Voice(111, Rate * 3);
        var reference = Stretch(input, 1.75, Rate, chunk: Rate);
        foreach (var chunk in new[] { 1, 7, 480, 4_096 })
        {
            Assert.Equal(reference, Stretch(input, 1.75, Rate, chunk: chunk));
        }
    }

    [Fact]
    public void ResetStartsOverAsIfNew()
    {
        var input = Voice(160, Rate * 3);
        var stretcher = Stretcher(input);
        stretcher.Reset(0, 2);
        ReadAll(stretcher, 5_000, 333);
        stretcher.Reset(12_345.5, 1.25);
        Assert.Equal(12_345.5, stretcher.Position);
        Assert.Equal(1.25, stretcher.Rate);
        Assert.Equal(Stretch(input, 1.25, 8_000, start: 12_345.5), ReadAll(stretcher, 8_000, 1_000));
    }

    [Fact]
    public void SilenceStaysSilentBeforeTheStartAndPastTheEnd()
    {
        var input = Sine(220, Rate / 2);
        var output = Stretch(input, 2, Rate);
        // Half a second of input lasts a quarter of a second at 2×; after it, and a frame to fade, silence.
        Assert.All(output.Skip(Rate / 4 + 600), sample => Assert.Equal(0f, sample));
        Assert.All(Stretch(new float[Rate], 1.5, Rate), sample => Assert.Equal(0f, sample));
        Assert.All(Stretch(input, 1.5, 1_000, start: Rate), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void FramesAreAbout32MillisecondsAndMoveUpTo10()
    {
        var stretcher = new TimeStretcher(Rate, (_, _) => 0);
        Assert.Equal(512, stretcher.FrameLength);
        Assert.Equal(160, stretcher.Tolerance);
        Assert.Throws<ArgumentOutOfRangeException>(() => stretcher.Reset(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeStretcher(0, (_, _) => 0));
    }
}
