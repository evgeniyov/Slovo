using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Slovo.Core.Localization;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.App.Native;

/// <summary>
/// Playback through WASAPI shared mode on the default output. NAudio's player does not
/// convert sample rates, so the mixer renders straight into the device's own format:
/// 16 kHz speech is upsampled by linear interpolation, which is inaudible for speech. At a
/// speed other than 1× the mixer time-stretches the speech first, keeping its pitch.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAudioPlayer : IAudioPlayer
{
    static readonly Logger Logger = Log.For("Playback");
    readonly Lock gate = new();
    WasapiPlayer? output;
    TrackMixer? mixer;
    double rate = 1;

    public event Action? StateChanged;

    public bool IsPlaying { get; private set; }

    public double Position => mixer?.PositionSeconds ?? 0;

    public double Duration => mixer?.DurationSeconds ?? 0;

    public double Rate
    {
        get => rate;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            lock (gate)
            {
                rate = value;
                if (mixer is not null)
                {
                    mixer.Rate = value;
                }
            }
        }
    }

    public void Play(IReadOnlyList<IAudioSource> tracks, double startSeconds = 0)
    {
        Stop();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            WaveFormat mix;
            using (var client = device.CreateAudioClient())
            {
                mix = client.MixFormat;
            }
            var format = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, Math.Max(1, mix.Channels));
            var newMixer = new TrackMixer(tracks, format, startSeconds) { Rate = rate };
            var newOutput = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithEventSync().WithLatency(120).Build();
            newOutput.Init(newMixer);
            newOutput.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is { } error)
                {
                    Logger.Error("Playback stopped", error);
                }
                lock (gate)
                {
                    if (ReferenceEquals(output, newOutput))
                    {
                        IsPlaying = false;
                    }
                }
                StateChanged?.Invoke();
            };
            lock (gate)
            {
                output = newOutput;
                mixer = newMixer;
            }
            newOutput.Play();
            IsPlaying = true;
            StateChanged?.Invoke();
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            Logger.Error("Could not start playback", error);
            throw new SlovoException(CoreStrings.Format("audio.error.playbackFailed", error.Message), error);
        }
    }

    public void Pause()
    {
        output?.Pause();
        IsPlaying = false;
        StateChanged?.Invoke();
    }

    public void Resume()
    {
        if (output is null)
        {
            return;
        }
        output.Play();
        IsPlaying = true;
        StateChanged?.Invoke();
    }

    public void Seek(double seconds) => mixer?.Seek(seconds);

    public void Stop()
    {
        WasapiPlayer? old;
        lock (gate)
        {
            old = output;
            output = null;
            mixer = null;
        }
        if (old is null)
        {
            return;
        }
        IsPlaying = false;
        old.Stop();
        old.Dispose();
        StateChanged?.Invoke();
    }

    public void Dispose() => Stop();
}

/// <summary>Playback outside Windows (developing on a Mac): refuses with a clear message.</summary>
public sealed class UnavailableAudioPlayer : IAudioPlayer
{
    public bool IsPlaying => false;
    public double Position => 0;
    public double Duration => 0;
    public double Rate { get; set; } = 1;
    public event Action? StateChanged { add { } remove { } }

    public void Play(IReadOnlyList<IAudioSource> tracks, double startSeconds = 0) =>
        throw new SlovoException("Playback is only available on Windows.");

    public void Pause() { }
    public void Resume() { }
    public void Stop() { }
    public void Seek(double seconds) { }
    public void Dispose() { }

    /// <summary>The WASAPI player on Windows, this stand-in elsewhere.</summary>
    public static IAudioPlayer ForCurrentSystem() => OperatingSystem.IsWindows() ? new WindowsAudioPlayer() : new UnavailableAudioPlayer();
}
