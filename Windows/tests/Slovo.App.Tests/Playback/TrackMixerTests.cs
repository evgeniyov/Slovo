using NAudio.Wave;
using Slovo.App.Native;
using Slovo.Core.Platform;

namespace Slovo.App.Tests.Playback;

public class TrackMixerTests
{
    [Fact]
    public void MixesTwoTracksAndUpsamplesToTheDeviceRate()
    {
        var one = new MemoryAudioSource(Enumerable.Repeat(0.25f, 16_000).ToArray(), 16_000);
        var two = new MemoryAudioSource(Enumerable.Repeat(0.5f, 8_000).ToArray(), 16_000);
        var mixer = new TrackMixer([one, two], WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2), 0);
        Assert.Equal(1.0, mixer.DurationSeconds, 3);

        var buffer = new byte[48_000 * 2 * 4 / 10];
        Assert.Equal(buffer.Length, mixer.Read(buffer));
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
        Assert.Equal(0.75f, samples[100], 3);
        Assert.Equal(samples[100], samples[101]);
        Assert.Equal(0.1, mixer.PositionSeconds, 3);
    }

    [Fact]
    public void ClampsAndEndsWithTheLongestTrack()
    {
        var loud = new MemoryAudioSource(Enumerable.Repeat(0.9f, 1_600).ToArray(), 16_000);
        var mixer = new TrackMixer([loud, loud], WaveFormat.CreateIeeeFloatWaveFormat(16_000, 1), 0);
        var buffer = new byte[1_600 * 4];
        mixer.Read(buffer);
        Assert.Equal(1f, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer)[10]);
        Assert.Equal(0, mixer.Read(buffer));
    }

    [Fact]
    public void SeeksWithinTheTracks()
    {
        var ramp = new MemoryAudioSource(Enumerable.Range(0, 16_000).Select(i => i / 16_000f).ToArray(), 16_000);
        var mixer = new TrackMixer([ramp], WaveFormat.CreateIeeeFloatWaveFormat(16_000, 1), 0);
        mixer.Seek(0.5);
        var buffer = new byte[4 * 4];
        mixer.Read(buffer);
        Assert.Equal(0.5f, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer)[0], 3);
        mixer.Seek(99);
        Assert.Equal(1.0, mixer.PositionSeconds, 3);
    }

    static MemoryAudioSource Tone(double frequency, double seconds, double amplitude = 0.3) =>
        new(Enumerable.Range(0, (int)(seconds * 16_000)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / 16_000))).ToArray(), 16_000);

    static float[] ReadSeconds(TrackMixer mixer, double seconds)
    {
        var buffer = new byte[(int)(seconds * mixer.WaveFormat.SampleRate) * mixer.WaveFormat.Channels * 4];
        Assert.Equal(buffer.Length, mixer.Read(buffer));
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer).ToArray();
    }

    /// <summary>Upward zero crossings per second of one channel.</summary>
    static double Frequency(float[] interleaved, int channels, int sampleRate)
    {
        var crossings = 0;
        for (var i = channels; i < interleaved.Length; i += channels)
        {
            if (interleaved[i - channels] < 0 && interleaved[i] >= 0)
            {
                crossings++;
            }
        }
        return crossings / (interleaved.Length / (double)channels / sampleRate);
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void AtASpeedThePlayheadMovesFasterAndThePitchStays(double rate)
    {
        var mixer = new TrackMixer([Tone(200, 4), Tone(200, 3, 0.2)], WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2), 0) { Rate = rate };
        var output = ReadSeconds(mixer, 1);
        Assert.Equal(rate, mixer.PositionSeconds, 3);
        Assert.InRange(Frequency(output, 2, 48_000), 197, 203);
        Assert.Equal(output[4_000], output[4_001]);
        // Both tracks are in it, in step: their sum, not either alone.
        Assert.InRange(output.Max(), 0.47f, 0.51f);
    }

    [Fact]
    public void ChangingTheSpeedCarriesOnFromThePlayhead()
    {
        var ramp = new MemoryAudioSource(Enumerable.Range(0, 48_000).Select(i => i / 48_000f).ToArray(), 16_000);
        var mixer = new TrackMixer([ramp], WaveFormat.CreateIeeeFloatWaveFormat(16_000, 1), 0) { Rate = 2 };
        ReadSeconds(mixer, 0.25);
        Assert.Equal(0.5, mixer.PositionSeconds, 3);

        mixer.Rate = 1;
        var plain = ReadSeconds(mixer, 0.1);
        Assert.Equal(8_000 / 48_000f, plain[0], 4);
        Assert.Equal(0.6, mixer.PositionSeconds, 3);

        // Back to a speed: it starts exactly where 1× left off.
        mixer.Rate = 1.5;
        Assert.Equal(9_600 / 48_000f, ReadSeconds(mixer, 0.01)[0], 4);
    }

    [Fact]
    public void SeekingAtASpeedStartsThere()
    {
        var ramp = new MemoryAudioSource(Enumerable.Range(0, 48_000).Select(i => i / 48_000f).ToArray(), 16_000);
        var mixer = new TrackMixer([ramp], WaveFormat.CreateIeeeFloatWaveFormat(16_000, 1), 0) { Rate = 1.5 };
        ReadSeconds(mixer, 0.2);
        mixer.Seek(2);
        Assert.Equal(2, mixer.PositionSeconds, 3);
        var output = ReadSeconds(mixer, 0.01);
        Assert.Equal(32_000 / 48_000f, output[0], 4);
        Assert.Equal(32_050 / 48_000f, output[50], 4);
    }

    [Fact]
    public void AtASpeedItEndsWithTheTracks()
    {
        var mixer = new TrackMixer([Tone(300, 1)], WaveFormat.CreateIeeeFloatWaveFormat(16_000, 1), 0) { Rate = 2 };
        var buffer = new byte[1_600 * 4];
        var reads = 0;
        while (mixer.Read(buffer) > 0)
        {
            reads++;
            Assert.True(reads < 100);
        }
        Assert.Equal(5, reads);
        Assert.True(mixer.PositionSeconds >= mixer.DurationSeconds);
    }
}
