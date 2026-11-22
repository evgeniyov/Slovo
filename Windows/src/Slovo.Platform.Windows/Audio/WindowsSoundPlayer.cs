using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Platform.Windows.Threading;

namespace Slovo.Platform.Windows.Audio;

/// <summary>
/// The start, stop and cancel cues, drawn in code: soft sine notes with a short raised-cosine
/// attack and an exponential fade, quiet enough to confirm an action without covering
/// speech or music (the macOS app plays Tink, Pop and Bottle at 35 % volume).
/// </summary>
public static class CueSynthesizer
{
    /// <summary>The loudest a cue gets, on a 0…1 scale.</summary>
    public const float Peak = 0.3f;

    readonly record struct Note(double Start, double Duration, double Frequency);

    /// <summary>Start rises (G5 → D6), stop falls (D6 → G5), cancel is two low notes (A4 → E4).</summary>
    static Note[] Notes(SoundCue cue) => cue switch
    {
        SoundCue.Start => [new(0, 0.07, 783.99), new(0.055, 0.11, 1174.66)],
        SoundCue.Stop => [new(0, 0.07, 1174.66), new(0.055, 0.12, 783.99)],
        _ => [new(0, 0.09, 440.0), new(0.08, 0.15, 329.63)],
    };

    /// <summary>The cue as mono float samples at <paramref name="sampleRate"/>.</summary>
    public static float[] Render(SoundCue cue, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        var notes = Notes(cue);
        var length = notes.Max(note => note.Start + note.Duration);
        var samples = new float[(int)Math.Ceiling(length * sampleRate)];
        foreach (var note in notes)
        {
            var first = (int)(note.Start * sampleRate);
            var count = (int)(note.Duration * sampleRate);
            var attack = 0.006 * sampleRate;
            var release = 0.012 * sampleRate;
            for (var i = 0; i < count && first + i < samples.Length; i++)
            {
                var t = i / (double)sampleRate;
                var envelope = Math.Exp(-t / (note.Duration / 3.5));
                if (i < attack)
                {
                    envelope *= 0.5 - 0.5 * Math.Cos(Math.PI * i / attack);
                }
                var remaining = count - i;
                if (remaining < release)
                {
                    envelope *= 0.5 - 0.5 * Math.Cos(Math.PI * remaining / release);
                }
                // A little second harmonic keeps a pure sine from sounding like a test tone.
                var wave = Math.Sin(2 * Math.PI * note.Frequency * t) + 0.12 * Math.Sin(4 * Math.PI * note.Frequency * t);
                samples[first + i] += (float)(0.62 * envelope * wave);
            }
        }
        var peak = samples.Max(Math.Abs);
        if (peak > 0)
        {
            var scale = Peak / peak;
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] *= scale;
            }
        }
        return samples;
    }
}

/// <summary>
/// Plays the cues through WASAPI shared mode (NAudio's WasapiPlayer) on the default output
/// device, at the device's own rate. <see cref="Play"/> returns at once; cues play one after
/// another on a queue of their own, and one arriving while another plays waits at most
/// that cue's few hundred milliseconds.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSoundPlayer : ISoundPlayer, IDisposable
{
    readonly WorkerQueue queue = new("Slovo sounds");
    readonly Logger log = Log.For("Sounds");
    readonly Dictionary<(SoundCue, int), float[]> rendered = [];
    int waiting;

    public void Play(SoundCue cue)
    {
        // Two cues in line are plenty; more would only play late.
        if (Interlocked.Increment(ref waiting) > 2)
        {
            Interlocked.Decrement(ref waiting);
            return;
        }
        queue.Post(() =>
        {
            try
            {
                PlayNow(cue);
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException or NotSupportedException)
            {
                log.Warning($"Could not play a sound: {exception.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref waiting);
            }
        });
    }

    public void Dispose() => queue.Dispose();

    /// <summary>On the queue: plays one cue to its end.</summary>
    void PlayNow(SoundCue cue)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = AudioEndpoints.Default(enumerator, DataFlow.Render);
        if (device is null)
        {
            return;
        }
        // Built here, on a thread without a synchronization context, so PlaybackStopped is
        // raised on NAudio's playback thread and only signals this one.
        using var player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(50)
            .WithCategory(AudioStreamCategory.SoundEffects)
            .Build();
        var mix = player.DeviceMixFormat;
        var rate = mix is { SampleRate: > 0 } ? mix.SampleRate : 48_000;
        // Stereo at most: shared mode converts to more channels itself (AUTOCONVERTPCM).
        var channels = Math.Clamp(mix?.Channels ?? 2, 1, 2);
        if (!rendered.TryGetValue((cue, rate), out var samples))
        {
            samples = CueSynthesizer.Render(cue, rate);
            rendered[(cue, rate)] = samples;
        }
        using var finished = new ManualResetEventSlim();
        player.PlaybackStopped += (_, _) => finished.Set();
        player.Init(new FloatSamples(samples, rate, channels));
        player.Play();
        finished.Wait(TimeSpan.FromSeconds(samples.Length / (double)rate + 1));
    }

    /// <summary>Mono samples served as interleaved IEEE float, the same value on every channel.</summary>
    sealed class FloatSamples(float[] samples, int rate, int channels) : IWaveProvider
    {
        int position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);

        public int Read(Span<byte> buffer)
        {
            var frameBytes = sizeof(float) * channels;
            var frames = Math.Min(buffer.Length / frameBytes, samples.Length - position);
            if (frames <= 0)
            {
                return 0;
            }
            var output = MemoryMarshal.Cast<byte, float>(buffer[..(frames * frameBytes)]);
            for (var frame = 0; frame < frames; frame++)
            {
                var value = samples[position + frame];
                for (var channel = 0; channel < channels; channel++)
                {
                    output[frame * channels + channel] = value;
                }
            }
            position += frames;
            return frames * frameBytes;
        }
    }
}
