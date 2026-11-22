using System.Diagnostics;
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
/// A meeting track's capture that keeps going for the whole meeting: the microphone or the
/// system's output. The recovery of MeetingMicrophoneCapture.swift and SystemAudioTap.swift
/// lives here; subclasses say which endpoint to open and which device events matter.
/// </summary>
/// <remarks>
/// <para>
/// Threading: device work runs on a control queue (an MTA thread); packets arrive on
/// NAudio's capture thread, are converted there and handed to the callback on a delivery
/// thread of their own, in order, so a slow consumer never makes WASAPI drop audio.
/// <see cref="Stop"/> waits until every converted packet has been delivered.
/// </para>
/// <para>
/// Timing: every chunk carries the <see cref="IUptime"/> time of its first sample, taken
/// from the packet's QPC position. Capture can pause — a device is switched, a lost
/// device is retried, and loopback delivers no packets at all while nothing plays — and
/// the gap is not filled here: the next chunk's time tells the meeting recorder how much
/// silence to put on the track, so both tracks stay on one timeline.
/// </para>
/// <para>
/// Recovery: when the stream fails (AUDCLNT_E_DEVICE_INVALIDATED and the like) it is
/// reopened with a growing delay. Past <see cref="CapturePolicy.RestartBudget"/> the owner
/// is told once through <see cref="Failed"/>; the retries carry on at the capped delay for
/// the life of the recording, and a device that comes back rejoins by itself and is
/// announced through <see cref="Resumed"/>. Both events are raised on the delivery thread,
/// so they reach the owner in the order they happened.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal abstract class ContinuousCaptureStream : IAudioStream
{
    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    readonly WorkerQueue delivery;
    readonly IUptime uptime;
    Action<float[], double?>? onSamples;
    volatile string? deviceName;

    // Queue only.
    MMDeviceNotificationClient? notifications;
    OpenStream? current;
    int sessionToken;
    int epoch;
    int switchToken;
    bool reportedLost;

    protected ContinuousCaptureStream(string name, IUptime uptime)
    {
        Queue = new WorkerQueue(name);
        delivery = new WorkerQueue(name + " samples");
        this.uptime = uptime;
        Log = Core.Logging.Log.For(name);
    }

    public string? DeviceName => deviceName;

    public event Action<string>? Failed;

    public event Action? Resumed;

    protected WorkerQueue Queue { get; }
    protected Logger Log { get; }
    /// <summary>Queue only.</summary>
    protected MMDeviceEnumerator? Enumerator { get; private set; }
    /// <summary>Queue only: whether the stream should be running.</summary>
    protected bool Running { get; private set; }
    /// <summary>Queue only: whether a stream is open right now.</summary>
    protected bool IsOpen => current is not null;

    /// <summary>A recorder built but not started, the endpoint it owns (disposed with it) and a name to show.</summary>
    protected sealed record Source(WasapiRecorder Recorder, IDisposable? Owned, string? Name, string? DeviceId);

    sealed record OpenStream(Source Source, CaptureConverter Converter, int Token);

    /// <summary>
    /// Queue only: builds the best recorder available now, skipping the device ids that
    /// already failed to start in this attempt. Throws a SlovoException when there is none.
    /// </summary>
    protected abstract Source Build(IReadOnlySet<string> failedIds);

    /// <summary>The message when no source would start, with the last reason.</summary>
    protected abstract string StartFailedMessage(Exception lastError);

    /// <summary>The message for <see cref="Failed"/> once the budget is spent.</summary>
    protected abstract string LostMessage(Exception lastError);

    /// <summary>Queue only: device notifications worth reacting to; see <see cref="Restart"/> and <see cref="ScheduleSwitch"/>.</summary>
    protected virtual void OnDeviceStateChanged(string deviceId, DeviceState state)
    {
    }

    protected virtual void OnDeviceAdded(string deviceId)
    {
    }

    protected virtual void OnDefaultDeviceChanged(DataFlow flow, Role role, string? deviceId)
    {
    }

    /// <summary>Queue only: the device id of the open stream.</summary>
    protected string? CurrentDeviceId => current?.Source.DeviceId;

    public void Start(Action<float[], double?> onSamples)
    {
        var started = Queue.Invoke(() =>
        {
            if (Running)
            {
                return true;
            }
            this.onSamples = onSamples;
            Enumerator ??= new MMDeviceEnumerator();
            Open();
            Running = true;
            reportedLost = false;
            Watch();
            return true;
        }, StartTimeout, false);
        if (!started)
        {
            throw new SlovoException(StartTimeoutMessage());
        }
    }

    /// <summary>The message when opening took longer than anyone should wait.</summary>
    protected abstract string StartTimeoutMessage();

    public void Stop()
    {
        Queue.Invoke(() =>
        {
            Running = false;
            epoch++;
            switchToken++;
            Unwatch();
            Close();
        }, StopTimeout);
        // Every packet converted before the stream closed reaches the callback before Stop returns.
        delivery.Invoke(() => { }, StopTimeout);
    }

    public void Dispose()
    {
        Stop();
        Queue.Invoke(() =>
        {
            Enumerator?.Dispose();
            Enumerator = null;
        }, TimeSpan.FromSeconds(1));
        Queue.Dispose();
        delivery.Dispose();
    }

    // Opening and closing (queue)

    /// <summary>
    /// Opens the best source; when one builds but will not start (a microphone another app
    /// holds exclusively, a format the engine refuses), the next one is tried.
    /// </summary>
    void Open()
    {
        var failed = new HashSet<string>(StringComparer.Ordinal);
        Exception? lastStartError = null;
        while (true)
        {
            Source source;
            try
            {
                source = Build(failed);
            }
            catch (SlovoException) when (lastStartError is not null)
            {
                throw new SlovoException(StartFailedMessage(lastStartError), lastStartError);
            }
            try
            {
                StartSource(source);
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                source.Recorder.Dispose();
                source.Owned?.Dispose();
                Log.Warning($"A source did not start: {AudioEndpoints.Describe(exception)}");
                lastStartError = exception;
                if (source.DeviceId is null || !failed.Add(source.DeviceId))
                {
                    throw new SlovoException(StartFailedMessage(exception), exception);
                }
            }
        }
    }

    void StartSource(Source source)
    {
        var layout = AudioEndpoints.LayoutOf(source.Recorder);
        var converter = new CaptureConverter(layout, source.Recorder.WaveFormat.SampleRate);
        var token = ++sessionToken;
        source.Recorder.DataAvailable += (buffer, flags, _, qpcPosition) => Receive(converter, buffer, flags, qpcPosition);
        source.Recorder.RecordingStopped += (_, arguments) => StreamStopped(token, arguments.Exception);
        source.Recorder.StartRecording();
        current = new OpenStream(source, converter, token);
        deviceName = source.Name ?? deviceName;
        Log.Info($"Capture running at {source.Recorder.WaveFormat.SampleRate} Hz, {layout.Channels} channel(s)");
    }

    /// <summary>Stops the open stream and delivers what its resampler still held.</summary>
    void Close()
    {
        if (current is not { } open)
        {
            return;
        }
        current = null;
        sessionToken++;
        // Joins NAudio's capture thread: no packet is converted after this.
        open.Source.Recorder.Dispose();
        open.Source.Owned?.Dispose();
        var tail = open.Converter.Flush();
        if (tail.Samples.Length > 0)
        {
            Deliver(tail);
        }
    }

    /// <summary>Queue only: closes the stream and reopens it, with retries.</summary>
    protected void Restart(string reason)
    {
        if (!Running)
        {
            return;
        }
        Log.Info($"Capture {reason}; restarting");
        epoch++;
        switchToken++;
        Close();
        Reopen(uptime.Now, CapturePolicy.RestartRetryDelay, epoch);
    }

    void Reopen(double startedAt, double delay, int attemptEpoch)
    {
        if (!Running || attemptEpoch != epoch)
        {
            return;
        }
        try
        {
            Open();
            if (reportedLost)
            {
                reportedLost = false;
                Log.Info("Capture is back");
                Raise(() => Resumed?.Invoke());
            }
        }
        catch (Exception exception)
        {
            var next = CapturePolicy.NextRetryDelay(uptime.Now - startedAt, delay);
            if (next is null && !reportedLost)
            {
                reportedLost = true;
                Log.Error($"Capture still unavailable, retrying: {AudioEndpoints.Describe(exception)}");
                var message = LostMessage(exception);
                Raise(() => Failed?.Invoke(message));
            }
            Queue.PostAfter(TimeSpan.FromSeconds(delay), () => Reopen(startedAt, next ?? CapturePolicy.MaximumRetryDelay, attemptEpoch));
        }
    }

    /// <summary>
    /// Queue only: switches to the preferred device once notifications settle, when it differs
    /// from the open one (<paramref name="preferredId"/> is asked again after the delay).
    /// </summary>
    protected void ScheduleSwitch(Func<string?> preferredId, string reason)
    {
        var token = ++switchToken;
        Queue.PostAfter(TimeSpan.FromSeconds(CapturePolicy.SwitchDelay), () =>
        {
            if (!Running || token != switchToken)
            {
                return;
            }
            var preferred = preferredId();
            if (preferred is not null && (preferred != CurrentDeviceId || current is null))
            {
                Restart(reason);
            }
        });
    }

    // Packets (capture thread)

    void Receive(CaptureConverter converter, ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long qpcPosition)
    {
        try
        {
            var arrival = uptime.Now;
            var counter = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var frames = buffer.Length / converter.Layout.BlockAlign;
            var time = CaptureClock.PacketTime(qpcPosition, flags.HasFlag(AudioClientBufferFlags.TimestampError),
                arrival, counter, frames, converter.SampleRate);
            var chunk = converter.Convert(buffer, flags.HasFlag(AudioClientBufferFlags.Silent), time);
            if (chunk.Samples.Length > 0)
            {
                Deliver(chunk);
            }
        }
        catch (Exception exception)
        {
            Log.Error("Converting captured audio failed", exception);
        }
    }

    /// <summary>Raises an event on the delivery thread, after the samples delivered before it.</summary>
    void Raise(Action raise) => delivery.Post(() =>
    {
        try
        {
            raise();
        }
        catch (Exception exception)
        {
            Log.Error("A stream event handler failed", exception);
        }
    });

    void Deliver(ConvertedChunk chunk) => delivery.Post(() =>
    {
        try
        {
            onSamples?.Invoke(chunk.Samples, chunk.Time);
        }
        catch (Exception exception)
        {
            Log.Error("The sample consumer failed", exception);
        }
    });

    /// <summary>On NAudio's capture thread when its stream ended; only failures matter.</summary>
    void StreamStopped(int token, Exception? exception)
    {
        if (exception is null)
        {
            return;
        }
        var reason = exception is COMException { HResult: AudioEndpoints.DeviceInvalidated }
            ? "lost its device"
            : $"failed ({AudioEndpoints.Describe(exception)})";
        Queue.Post(() =>
        {
            if (Running && current?.Token == token)
            {
                Restart(reason);
            }
        });
    }

    // Device notifications

    void Watch()
    {
        if (Enumerator is null || notifications is not null)
        {
            return;
        }
        try
        {
            notifications = Enumerator.CreateNotificationClient(useSynchronizationContext: false);
            // Raised on Windows' notification thread; the handlers only hop to the queue.
            notifications.DeviceStateChanged += (_, arguments) =>
                Queue.Post(() => Notify(() => OnDeviceStateChanged(arguments.DeviceId, arguments.NewState)));
            notifications.DeviceRemoved += (_, arguments) =>
                Queue.Post(() => Notify(() => OnDeviceStateChanged(arguments.DeviceId, DeviceState.NotPresent)));
            notifications.DeviceAdded += (_, arguments) =>
                Queue.Post(() => Notify(() => OnDeviceAdded(arguments.DeviceId)));
            notifications.DefaultDeviceChanged += (_, arguments) =>
                Queue.Post(() => Notify(() => OnDefaultDeviceChanged(arguments.Flow, arguments.Role, arguments.DeviceId)));
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            Log.Warning($"Device changes will not be followed: {AudioEndpoints.Describe(exception)}");
        }
    }

    void Notify(Action handler)
    {
        if (Running)
        {
            handler();
        }
    }

    void Unwatch()
    {
        notifications?.Dispose();
        notifications = null;
    }
}
