using System.Globalization;
using NAudio.Wave;
using Slovo.Core.Audio;
using Slovo.Core.Model;
using Slovo.Platform.Windows.Audio;

namespace Slovo.Platform.Windows.Tests;

public class CaptureFormatTests
{
    [Fact]
    public void SharedModeMixFormatsAreRead()
    {
        Assert.Equal(new PcmLayout(PcmSampleType.Float, 32, 2), CaptureFormat.LayoutOf(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2)));
        Assert.Equal(new PcmLayout(PcmSampleType.Integer, 16, 1), CaptureFormat.LayoutOf(new WaveFormat(44_100, 16, 1)));
        Assert.Equal(new PcmLayout(PcmSampleType.Integer, 24, 2), CaptureFormat.LayoutOf(new WaveFormat(96_000, 24, 2)));
        // WAVE_FORMAT_EXTENSIBLE, the usual mix format: float or integer by sub-format.
        Assert.Equal(new PcmLayout(PcmSampleType.Float, 32, 2), CaptureFormat.LayoutOf(new WaveFormatExtensible(48_000, 32, 2)));
        Assert.Equal(new PcmLayout(PcmSampleType.Integer, 16, 2), CaptureFormat.LayoutOf(new WaveFormatExtensible(44_100, 16, 2)));
    }

    [Fact]
    public void CompressedFormatsAreRefused()
    {
        Assert.Null(CaptureFormat.LayoutOf(WaveFormat.CreateALawFormat(8_000, 1)));
        Assert.Null(CaptureFormat.LayoutOf(WaveFormat.CreateMuLawFormat(8_000, 1)));
    }
}

public class CaptureClockTests
{
    [Fact]
    public void APacketIsDatedByItsQpcPositionOnTheUptimeClock()
    {
        // Captured 30 ms before it arrived, with both clocks read at arrival.
        var time = CaptureClock.PacketTime(qpcPosition: (long)(99.97 * 10_000_000), timestampError: false,
            arrivalUptime: 500, arrivalQpcSeconds: 100, frames: 480, sampleRate: 48_000);
        Assert.Equal(499.97, time, 1e-6);
    }

    [Fact]
    public void WithoutAUsablePositionThePacketEndedAtArrival()
    {
        // A 100 ms packet that just arrived began 100 ms ago.
        Assert.Equal(499.9, CaptureClock.PacketTime(0, false, 500, 100, 4_800, 48_000), 1e-6);
        Assert.Equal(499.9, CaptureClock.PacketTime(999_700_000, timestampError: true, 500, 100, 4_800, 48_000), 1e-6);
        // A position from the future, or hours ago, is not trusted.
        Assert.Equal(499.9, CaptureClock.PacketTime(1_010_000_000, false, 500, 100, 4_800, 48_000), 1e-6);
        Assert.Equal(499.9, CaptureClock.PacketTime(10_000_000, false, 500, 100, 4_800, 48_000), 1e-6);
    }
}

public class CaptureConverterTests
{
    static byte[] StereoFloat(int frames, int rate, double frequency, double amplitude = 0.3)
    {
        var bytes = new byte[frames * 8];
        for (var i = 0; i < frames; i++)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate));
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 8), value);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 4), value);
        }
        return bytes;
    }

    [Fact]
    public void TurnsStereo48kIntoMono16kWithoutLosingLength()
    {
        var converter = new CaptureConverter(new PcmLayout(PcmSampleType.Float, 32, 2), 48_000);
        var input = StereoFloat(48_000, 48_000, 440);
        var output = new List<float>();
        for (var offset = 0; offset < input.Length; offset += 480 * 8)
        {
            output.AddRange(converter.Convert(input.AsSpan(offset, 480 * 8), silent: false, packetTime: null).Samples);
        }
        output.AddRange(converter.Flush().Samples);
        Assert.Equal(16_000, output.Count);
        Assert.Equal(0.3 / Math.Sqrt(2), LevelMeter.Rms(output.ToArray().AsSpan(2_000, 12_000)), 0.002);
    }

    [Fact]
    public void SilentPacketsBecomeZeros()
    {
        var converter = new CaptureConverter(new PcmLayout(PcmSampleType.Float, 32, 2), 48_000);
        var chunk = converter.Convert(StereoFloat(4_800, 48_000, 440), silent: true, packetTime: 10);
        Assert.NotEmpty(chunk.Samples);
        Assert.All(chunk.Samples, sample => Assert.Equal(0f, sample));
    }

    /// <summary>
    /// Every chunk is dated by its first sample, including the ones the resampler's
    /// look-ahead held back from the previous packet, and a gap in capture shows in the times.
    /// </summary>
    [Fact]
    public void ChunksCarryTheTimeOfTheirFirstSample()
    {
        const int rate = 44_100;
        var converter = new CaptureConverter(new PcmLayout(PcmSampleType.Float, 32, 2), rate);
        var packet = StereoFloat(441, rate, 300);
        var first = converter.Convert(packet, false, packetTime: 100);
        var second = converter.Convert(packet, false, packetTime: 100.01);
        Assert.Equal(100, first.Time!.Value, 1e-9);
        Assert.Equal(100 + first.Samples.Length / 16_000.0, second.Time!.Value, 1e-6);

        // Capture paused for two seconds (loopback while nothing played).
        var third = converter.Convert(packet, false, packetTime: 102.02);
        var expected = 102.02 + ((first.Samples.Length + second.Samples.Length) * (double)rate / 16_000 - 882) / rate;
        Assert.Equal(expected, third.Time!.Value, 1e-6);
        Assert.True(third.Time < 102.02, "the samples held back from the previous packet come first");

        var tail = converter.Flush();
        Assert.NotEmpty(tail.Samples);
        Assert.Equal(third.Time!.Value + third.Samples.Length / 16_000.0, tail.Time!.Value, 1e-9);
    }

    [Fact]
    public void WithoutTimesChunksHaveNone()
    {
        var converter = new CaptureConverter(new PcmLayout(PcmSampleType.Integer, 16, 1), 16_000);
        var chunk = converter.Convert(new byte[320], false, null);
        Assert.Equal(160, chunk.Samples.Length);
        Assert.Null(chunk.Time);
        Assert.Empty(converter.Convert(new byte[1], false, null).Samples);
    }
}

public class CapturePolicyTests
{
    [Fact]
    public void ChosenMicrophoneComesFirstThenTheDefaultThenEveryOtherInput()
    {
        string[] connected = ["Headset", "Webcam"];
        IReadOnlyList<string> Preferred(string? chosen, string? defaultId) =>
            CapturePolicy.PreferredDeviceIds(chosen, connected.Contains, defaultId);

        Assert.Equal(["Headset", "Webcam"], Preferred("Headset", "Webcam"));
        Assert.Equal(["Webcam"], Preferred("Missing", "Webcam"));
        Assert.Equal(["Webcam"], Preferred(null, "Webcam"));
        Assert.Equal(["Webcam"], Preferred("", "Webcam"));
        Assert.Equal(["Headset"], Preferred("Headset", "Headset"));
        Assert.Equal(["Headset"], Preferred("Headset", null));
        Assert.Empty(Preferred("Missing", null));

        // A default that will not open must not take every attempt while another input sits unused.
        string[] inputs = ["Built-in", "Headset", "Scarlett"];
        Assert.Equal(["Headset", "Scarlett", "Built-in"],
            CapturePolicy.PreferredDeviceIds(null, inputs.Contains, "Headset", inputs, id => id == "Built-in"));
        Assert.Equal(["Scarlett", "Headset", "Built-in"],
            CapturePolicy.PreferredDeviceIds("Scarlett", inputs.Contains, "Headset", inputs, id => id == "Built-in"));
        Assert.Equal(["Built-in"], CapturePolicy.PreferredDeviceIds(null, _ => true, null, ["Built-in"]));
    }

    /// <summary>A Bluetooth handoff can leave the input missing for many seconds; the budget covers that.</summary>
    [Fact]
    public void TheRestartBudgetOutlastsADeviceHandoff()
    {
        var elapsed = 0.0;
        var delay = CapturePolicy.RestartRetryDelay;
        var attempts = 1;
        while (CapturePolicy.NextRetryDelay(elapsed, delay) is { } next)
        {
            elapsed += delay;
            delay = next;
            attempts++;
        }
        Assert.True(elapsed > 28, "the device is given about half a minute to come back");
        Assert.True(elapsed <= CapturePolicy.RestartBudget);
        Assert.True(attempts > 15);
        Assert.Equal(CapturePolicy.MaximumRetryDelay, delay);
        Assert.Equal(0.45, CapturePolicy.NextRetryDelay(0, 0.3)!.Value, 1e-9);
        Assert.Null(CapturePolicy.NextRetryDelay(CapturePolicy.RestartBudget, 0.3));
    }

    [Fact]
    public void DictationRetriesBriefly()
    {
        Assert.Equal(5, CapturePolicy.DictationRestartAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(200), CapturePolicy.DictationRestartInterval);
    }
}

public class CueTests
{
    [Theory]
    [InlineData(SoundCue.Start)]
    [InlineData(SoundCue.Stop)]
    [InlineData(SoundCue.Cancel)]
    public void CuesAreShortQuietAndClickFree(SoundCue cue)
    {
        foreach (var rate in new[] { 44_100, 48_000, 96_000 })
        {
            var samples = CueSynthesizer.Render(cue, rate);
            var seconds = samples.Length / (double)rate;
            Assert.InRange(seconds, 0.1, 0.3);
            Assert.Equal(CueSynthesizer.Peak, samples.Max(Math.Abs), 3);
            // Starts and ends at silence, so there is no click.
            Assert.True(Math.Abs(samples[0]) < 0.01);
            Assert.True(Math.Abs(samples[^1]) < 0.01);
            Assert.True(LevelMeter.Rms(samples) > 0.03, "audible");
        }
    }

    [Fact]
    public void StartRisesStopFallsCancelIsLow()
    {
        static double Pitch(float[] samples, int from, int to, int rate)
        {
            var crossings = 0;
            for (var i = from + 1; i < to; i++)
            {
                if (samples[i - 1] < 0 && samples[i] >= 0)
                {
                    crossings++;
                }
            }
            return crossings / ((to - from) / (double)rate);
        }

        const int rate = 48_000;
        var start = CueSynthesizer.Render(SoundCue.Start, rate);
        var stop = CueSynthesizer.Render(SoundCue.Stop, rate);
        var cancel = CueSynthesizer.Render(SoundCue.Cancel, rate);
        Assert.True(Pitch(start, 0, 2_400, rate) < Pitch(start, 4_000, 7_000, rate));
        Assert.True(Pitch(stop, 0, 2_400, rate) > Pitch(stop, 4_000, 7_000, rate));
        Assert.True(Pitch(cancel, 0, cancel.Length, rate) < 500);
    }
}

public class AudioDeviceListTests
{
    [Fact]
    public void SortsByNameAsPeopleReadIt()
    {
        var sorted = AudioDeviceList.Sorted(
        [
            new AudioInputDevice("c", "Microphone 10"),
            new AudioInputDevice("a", "microphone 2"),
            new AudioInputDevice("b", "Headset"),
        ], CultureInfo.InvariantCulture);
        Assert.Equal(["b", "a", "c"], sorted.Select(device => device.Uid));
    }
}
