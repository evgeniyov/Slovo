using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Slovo.Core.Audio;
using Slovo.Core.Localization;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Platform.Windows.Threading;

namespace Slovo.Platform.Windows.Audio;

/// <summary>
/// Dictation capture through WASAPI shared mode (NAudio's event-driven WasapiRecorder),
/// down-mixed, resampled to 16 kHz mono float and kept in memory until <see cref="Stop"/>
/// (MicrophoneRecorder.swift).
/// </summary>
/// <remarks>
/// <para>
/// The device is opened on a queue of this recorder, never on the UI thread: opening
/// can block, most of all while a Bluetooth headset switches profiles, and the dictation
/// key is timed on the UI thread. So <see cref="Start"/> returns at once and throws only
/// when no microphone exists; when opening fails later, <see cref="IsRecording"/> turns
/// false by itself. Samples arrive on NAudio's capture thread in the device's own mix
/// format, whatever its rate.
/// </para>
/// <para>
/// A device that disconnects mid-recording (AUDCLNT_E_DEVICE_INVALIDATED) is replaced by
/// the system default, re-resolved on each of up to five attempts 200 ms apart; the
/// samples captured so far are kept.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioRecorder : IAudioRecorder, IDisposable
{
    public const int SampleRate = Resampler.DefaultTargetRate;
    /// <summary>About 30 level updates per second.</summary>
    static readonly TimeSpan MeterInterval = TimeSpan.FromMilliseconds(33);
    static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(3);
    static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    readonly IUptime uptime;
    readonly WorkerQueue queue = new("Slovo microphone");
    readonly Logger log = Log.For("Microphone");
    /// <summary>Used on the queue only.</summary>
    MMDeviceEnumerator? enumerator;
    /// <summary>The capture from Start to its end; replaced only on the calling (UI) thread.</summary>
    Capture? session;
    volatile bool isRecording;
    volatile string? lastDeviceName;
    double startedAt;

    public WasapiAudioRecorder(IUptime uptime)
    {
        this.uptime = uptime;
    }

    public bool IsRecording => isRecording;
    public string? LastDeviceName => lastDeviceName;
    public event Action<float>? LevelChanged;

    public void Start(string? deviceUid)
    {
        if (session is not null)
        {
            Cancel();
        }
        // A quick lookup, unlike opening the device, so it can answer at once.
        var lookup = queue.Invoke<Lookup>(() => Resolve(deviceUid), LookupTimeout, new Lookup.TimedOut());
        var device = lookup switch
        {
            Lookup.Found found => found,
            Lookup.TimedOut => throw new SlovoException(CoreStrings.Format("audio.error.engineStartFailed", "timeout")),
            _ => throw new SlovoException(CoreStrings.Get("audio.error.noInputDevice")),
        };
        var capture = new Capture();
        Volatile.Write(ref session, capture);
        lastDeviceName = device.Name;
        isRecording = true;
        startedAt = uptime.Now;
        capture.Meter = new Timer(_ => PublishLevel(capture), null, MeterInterval, MeterInterval);
        var fallback = device.Id == device.DefaultId ? null : device.DefaultId;
        queue.Post(() => Open(capture, device.Id, fallback));
    }

    public RecordedAudio Stop()
    {
        var capture = session;
        if (!isRecording || capture is null)
        {
            if (capture is not null)
            {
                EndCapture(capture, waitForDevice: false, keep: false);
            }
            return RecordedAudio.Empty;
        }
        EndCapture(capture, waitForDevice: true, keep: true);
        float[] samples;
        lock (capture.Gate)
        {
            samples = [.. capture.Samples];
            capture.Samples.Clear();
        }
        log.Debug($"Captured {samples.Length / (double)SampleRate:F2} s of audio in {uptime.Now - startedAt:F2} s");
        return new RecordedAudio(samples, SampleRate);
    }

    public void Cancel()
    {
        if (session is { } capture)
        {
            // Nothing is kept, so a discarded tap never waits for the device.
            EndCapture(capture, waitForDevice: false, keep: false);
        }
    }

    public void Dispose()
    {
        Cancel();
        queue.Invoke(() =>
        {
            enumerator?.Dispose();
            enumerator = null;
        }, TimeSpan.FromSeconds(1));
        queue.Dispose();
    }

    // Capture lifecycle

    /// <summary>
    /// Ends the capture. With <paramref name="waitForDevice"/>, the device is stopped and its
    /// last packets converted before this returns, so the samples are complete; otherwise
    /// the device closes in the background.
    /// </summary>
    void EndCapture(Capture capture, bool waitForDevice, bool keep)
    {
        if (ReferenceEquals(session, capture))
        {
            Volatile.Write(ref session, null);
        }
        isRecording = false;
        capture.Closing = true;
        capture.Discard = !keep;
        capture.Meter?.Dispose();
        capture.Meter = null;
        if (waitForDevice)
        {
            if (!queue.Invoke(() => Close(capture), CloseTimeout))
            {
                log.Warning("The microphone did not stop in time; returning what was captured");
            }
        }
        else
        {
            queue.Post(() => Close(capture));
        }
    }

    /// <summary>On the queue.</summary>
    void Close(Capture capture)
    {
        var recorder = capture.Recorder;
        capture.Recorder = null;
        // Disposing stops the stream and joins NAudio's capture thread, so no packet is
        // converted after this.
        recorder?.Dispose();
        capture.Device?.Dispose();
        capture.Device = null;
        if (capture.Converter is { } converter)
        {
            var tail = converter.Flush();
            capture.Converter = null;
            if (!capture.Discard && tail.Samples.Length > 0)
            {
                lock (capture.Gate)
                {
                    capture.Samples.AddRange(tail.Samples);
                }
            }
        }
    }

    /// <summary>On the queue: opens the device, then the default when it will not open.</summary>
    void Open(Capture capture, string deviceId, string? fallbackId)
    {
        if (capture.Closing)
        {
            return;
        }
        try
        {
            OpenDevice(capture, deviceId);
        }
        catch (Exception exception)
        {
            log.Error($"Could not start the microphone: {AudioEndpoints.Describe(exception)}");
            if (fallbackId is not null && !capture.Closing)
            {
                log.Info("Trying the system default microphone instead");
                Open(capture, fallbackId, null);
                return;
            }
            OpenFailed(capture);
        }
    }

    void OpenFailed(Capture capture)
    {
        if (ReferenceEquals(Volatile.Read(ref session), capture))
        {
            isRecording = false;
        }
    }

    /// <summary>On the queue: builds and starts a recorder on one endpoint. Throws.</summary>
    void OpenDevice(Capture capture, string? deviceId)
    {
        enumerator ??= new MMDeviceEnumerator();
        var device = AudioEndpoints.ActiveCapture(enumerator, deviceId) ?? AudioEndpoints.Default(enumerator, DataFlow.Capture)
            ?? throw new SlovoException(CoreStrings.Get("audio.error.noInputDevice"));
        WasapiRecorder? recorder = null;
        try
        {
            recorder = AudioEndpoints.MicrophoneRecorder(device);
            var layout = AudioEndpoints.LayoutOf(recorder);
            var rate = recorder.WaveFormat.SampleRate;
            if (capture.Converter is not { } converter || converter.SampleRate != rate || converter.Layout != layout)
            {
                // A different device format: whatever the old resampler held goes first.
                if (capture.Converter is { } old)
                {
                    var tail = old.Flush();
                    lock (capture.Gate)
                    {
                        capture.Samples.AddRange(tail.Samples);
                    }
                }
                converter = new CaptureConverter(layout, rate);
                capture.Converter = converter;
            }
            var owned = recorder;
            recorder.DataAvailable += (buffer, flags, _, _) => Receive(capture, converter, buffer, flags);
            recorder.RecordingStopped += (_, arguments) => Stopped(capture, owned, arguments.Exception);
            recorder.StartRecording();
            capture.Recorder = recorder;
            capture.Device = device;
            capture.DeviceId = device.ID;
            lastDeviceName = AudioEndpoints.NameOf(device) ?? lastDeviceName;
            log.Info($"Microphone capture running at {rate} Hz, {layout.Channels} channel(s)");
        }
        catch
        {
            recorder?.Dispose();
            device.Dispose();
            throw;
        }
    }

    /// <summary>On NAudio's capture thread.</summary>
    static void Receive(Capture capture, CaptureConverter converter, ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags)
    {
        if (capture.Discard)
        {
            return;
        }
        try
        {
            var chunk = converter.Convert(buffer, flags.HasFlag(AudioClientBufferFlags.Silent), null);
            if (chunk.Samples.Length == 0)
            {
                return;
            }
            var window = chunk.Samples.AsSpan(Math.Max(0, chunk.Samples.Length - LevelMeter.DictationWindow));
            var rms = LevelMeter.Rms(window);
            lock (capture.Gate)
            {
                capture.Samples.AddRange(chunk.Samples);
                capture.RecentRms = rms;
            }
        }
        catch (Exception exception)
        {
            Log.For("Microphone").Error("Converting microphone audio failed", exception);
        }
    }

    /// <summary>On NAudio's capture thread, when its stream ended.</summary>
    void Stopped(Capture capture, WasapiRecorder recorder, Exception? exception)
    {
        if (exception is null || capture.Closing)
        {
            return;
        }
        var deviceLost = exception is COMException { HResult: AudioEndpoints.DeviceInvalidated };
        log.Info($"Microphone {(deviceLost ? "disconnected" : "reported an error")}: {AudioEndpoints.Describe(exception)}; restarting capture");
        queue.Post(() => Recover(capture, recorder, deviceLost));
    }

    /// <summary>On the queue: keeps the samples and continues on a fresh stream.</summary>
    void Recover(Capture capture, WasapiRecorder failed, bool deviceLost)
    {
        if (capture.Closing || !ReferenceEquals(capture.Recorder, failed))
        {
            return;
        }
        capture.Recorder = null;
        failed.Dispose();
        capture.Device?.Dispose();
        capture.Device = null;
        // A lost device is replaced by the default, looked up afresh on every attempt.
        Restart(capture, deviceLost ? null : capture.DeviceId, attempt: 1);
    }

    void Restart(Capture capture, string? deviceId, int attempt)
    {
        if (capture.Closing)
        {
            return;
        }
        try
        {
            OpenDevice(capture, deviceId);
        }
        catch (Exception exception)
        {
            if (attempt >= CapturePolicy.DictationRestartAttempts)
            {
                log.Error($"Gave up restarting capture: {AudioEndpoints.Describe(exception)}");
                return;
            }
            queue.PostAfter(CapturePolicy.DictationRestartInterval, () => Restart(capture, null, attempt + 1));
        }
    }

    // Level meter

    void PublishLevel(Capture capture)
    {
        if (capture.Closing)
        {
            return;
        }
        float level;
        lock (capture.Gate)
        {
            capture.Level = LevelMeter.Smoothed(capture.Level, LevelMeter.DictationLevel(capture.RecentRms));
            level = capture.Level;
        }
        LevelChanged?.Invoke(level);
    }

    // Lookup

    abstract record Lookup
    {
        public sealed record Found(string Id, string Name, string? DefaultId) : Lookup;
        public sealed record Missing : Lookup;
        public sealed record TimedOut : Lookup;
    }

    /// <summary>On the queue: the chosen microphone when it is connected, otherwise the default.</summary>
    Lookup Resolve(string? deviceUid)
    {
        try
        {
            return ResolveDevice(deviceUid);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            log.Error($"Could not list microphones: {AudioEndpoints.Describe(exception)}");
            return new Lookup.Missing();
        }
    }

    Lookup ResolveDevice(string? deviceUid)
    {
        enumerator ??= new MMDeviceEnumerator();
        var defaultId = AudioEndpoints.DefaultId(enumerator, DataFlow.Capture);
        using (var chosen = AudioEndpoints.ActiveCapture(enumerator, deviceUid))
        {
            if (chosen is not null)
            {
                return new Lookup.Found(chosen.ID, AudioEndpoints.NameOf(chosen) ?? chosen.ID, defaultId);
            }
        }
        if (!string.IsNullOrEmpty(deviceUid))
        {
            log.Info("The chosen microphone is not connected; using the system default");
        }
        using var fallback = AudioEndpoints.Default(enumerator, DataFlow.Capture);
        return fallback is null
            ? new Lookup.Missing()
            : new Lookup.Found(fallback.ID, AudioEndpoints.NameOf(fallback) ?? fallback.ID, defaultId);
    }

    /// <summary>One capture from Start to its end.</summary>
    sealed class Capture
    {
        public readonly Lock Gate = new();
        /// <summary>Guarded by Gate.</summary>
        public readonly List<float> Samples = new(SampleRate * 30);
        /// <summary>RMS of the last ~50 ms; guarded by Gate.</summary>
        public float RecentRms;
        /// <summary>The smoothed meter value; guarded by Gate.</summary>
        public float Level;
        public volatile bool Closing;
        public volatile bool Discard;
        public Timer? Meter;
        /// <summary>Queue only (the converter itself runs on the capture thread while a recorder runs).</summary>
        public WasapiRecorder? Recorder;
        public MMDevice? Device;
        public string? DeviceId;
        public CaptureConverter? Converter;
    }
}
