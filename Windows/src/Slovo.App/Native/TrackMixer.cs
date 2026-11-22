using NAudio.Wave;
using Slovo.Core.Audio;
using Slovo.Core.Platform;

namespace Slovo.App.Native;

/// <summary>
/// Mixes tracks into an output format, keeping them in step: sums the sources, clamps, and
/// upsamples by linear interpolation to the output rate on every channel. At a speed other
/// than 1× the mix goes through a <see cref="TimeStretcher"/> before it is upsampled, so both
/// tracks change speed together and speech keeps its pitch.
/// </summary>
/// <remarks>
/// <see cref="Read"/> runs on the audio thread; <see cref="Seek"/> and <see cref="Rate"/> come
/// from the interface and take effect with the next read.
/// </remarks>
public sealed class TrackMixer : IWaveProvider
{
    readonly IReadOnlyList<IAudioSource> tracks;
    readonly double sourceRate;
    readonly double durationSeconds;
    readonly long length;
    readonly Lock gate = new();
    /// <summary>The playhead in source samples.</summary>
    double position;
    double rate = 1;
    float[] scratch = new float[4096];
    float[] mixed = new float[4096];
    TimeStretcher? stretcher;
    /// <summary>A seek or a new speed since the stretcher last started.</summary>
    bool stretcherStale = true;
    /// <summary>Stretched samples not yet passed; the next output frame falls <see cref="phase"/> after the first.</summary>
    float[] stretched = new float[4096];
    int carried;
    double phase;

    public TrackMixer(IReadOnlyList<IAudioSource> tracks, WaveFormat format, double startSeconds)
    {
        this.tracks = tracks;
        WaveFormat = format;
        sourceRate = tracks.Count > 0 ? tracks[0].SampleRate : 16_000;
        durationSeconds = tracks.Count == 0 ? 0 : tracks.Max(t => t.Length / t.SampleRate);
        length = tracks.Count == 0 ? 0 : tracks.Max(t => t.Length);
        position = Math.Max(0, startSeconds) * sourceRate;
    }

    public WaveFormat WaveFormat { get; }

    public double PositionSeconds => Volatile.Read(ref position) / sourceRate;

    public double DurationSeconds => durationSeconds;

    /// <summary>Playback speed: 1 plays the tracks as recorded, 1.5 half as fast again, at the same pitch.</summary>
    public double Rate
    {
        get => Volatile.Read(ref rate);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            lock (gate)
            {
                if (value != rate)
                {
                    Volatile.Write(ref rate, value);
                    stretcherStale = true;
                }
            }
        }
    }

    public void Seek(double seconds)
    {
        lock (gate)
        {
            Volatile.Write(ref position, Math.Clamp(seconds, 0, durationSeconds) * sourceRate);
            stretcherStale = true;
        }
    }

    public int Read(Span<byte> buffer)
    {
        lock (gate)
        {
            return rate == 1 ? ReadAsRecorded(buffer) : ReadStretched(buffer);
        }
    }

    int ReadAsRecorded(Span<byte> buffer)
    {
        var channels = WaveFormat.Channels;
        var frames = buffer.Length / (4 * channels);
        var step = sourceRate / WaveFormat.SampleRate;
        var start = position;
        var needed = (int)Math.Ceiling(frames * step) + 2;
        if (mixed.Length < needed)
        {
            mixed = new float[needed];
        }
        if (Mix((long)Math.Floor(start), mixed.AsSpan(0, needed)) == 0)
        {
            return 0;
        }
        var output = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer[..(frames * 4 * channels)]);
        var fraction = start - Math.Floor(start);
        for (var frame = 0; frame < frames; frame++)
        {
            var exact = fraction + frame * step;
            var index = (int)exact;
            var t = (float)(exact - index);
            var a = index < needed ? mixed[index] : 0;
            var b = index + 1 < needed ? mixed[index + 1] : a;
            var sample = Math.Clamp(a + (b - a) * t, -1f, 1f);
            for (var channel = 0; channel < channels; channel++)
            {
                output[frame * channels + channel] = sample;
            }
        }
        Volatile.Write(ref position, start + frames * step);
        return frames * 4 * channels;
    }

    /// <summary>
    /// The mix at the current speed: the stretcher turns it into a stream at the source rate
    /// whose every sample stands for <see cref="rate"/> source samples, and that stream is
    /// upsampled as <see cref="ReadAsRecorded"/> upsamples the mix itself.
    /// </summary>
    int ReadStretched(Span<byte> buffer)
    {
        var channels = WaveFormat.Channels;
        var frames = buffer.Length / (4 * channels);
        if (frames == 0 || position >= length)
        {
            return 0;
        }
        if (stretcherStale)
        {
            stretcher ??= new TimeStretcher(sourceRate, Mix);
            stretcher.Reset(position, rate);
            carried = 0;
            phase = 0;
            stretcherStale = false;
        }
        var step = sourceRate / WaveFormat.SampleRate;
        var end = phase + frames * step;
        var passed = (int)Math.Floor(end);
        var needed = Math.Max((int)Math.Floor(phase + (frames - 1) * step) + 2, passed);
        if (stretched.Length < needed)
        {
            Array.Resize(ref stretched, needed);
        }
        if (needed > carried)
        {
            stretcher!.Read(stretched.AsSpan(carried, needed - carried));
        }
        var output = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer[..(frames * 4 * channels)]);
        for (var frame = 0; frame < frames; frame++)
        {
            var exact = phase + frame * step;
            var index = (int)exact;
            var t = (float)(exact - index);
            var a = stretched[index];
            var b = stretched[index + 1];
            var sample = Math.Clamp(a + (b - a) * t, -1f, 1f);
            for (var channel = 0; channel < channels; channel++)
            {
                output[frame * channels + channel] = sample;
            }
        }
        carried = needed - passed;
        stretched.AsSpan(passed, carried).CopyTo(stretched);
        phase = end - passed;
        Volatile.Write(ref position, position + frames * step * rate);
        return frames * 4 * channels;
    }

    /// <summary>The tracks summed from a source position; returns how many samples the longest read gave (0 past every end).</summary>
    int Mix(long start, Span<float> destination)
    {
        destination.Clear();
        if (scratch.Length < destination.Length)
        {
            scratch = new float[destination.Length];
        }
        var most = 0;
        foreach (var track in tracks)
        {
            var read = track.Read(start, scratch.AsSpan(0, destination.Length));
            for (var i = 0; i < read; i++)
            {
                destination[i] += scratch[i];
            }
            most = Math.Max(most, read);
        }
        return most;
    }
}
