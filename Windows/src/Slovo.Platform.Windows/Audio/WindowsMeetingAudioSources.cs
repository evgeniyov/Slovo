using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Platform.Windows.Audio;

/// <summary>The two tracks of a meeting on Windows: the microphone and everything the computer plays.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMeetingAudioSources(IUptime uptime) : IMeetingAudioSources
{
    public IAudioStream Microphone(string? deviceUid) => new MeetingMicrophoneStream(deviceUid, uptime);

    /// <summary>Loopback works on every supported Windows, so this is never null; failures come from Start.</summary>
    public IAudioStream? SystemAudio() => new SystemAudioStream(uptime);

    /// <summary>Process loopback of one app's process tree; null before Windows 10 2004, which cannot record one app.</summary>
    public IAudioStream? ApplicationAudio(string bundleId) =>
        SystemAudioStream.SupportsProcessLoopback ? new ApplicationAudioStream(bundleId, uptime) : null;
}

/// <summary>
/// The sound of the app a screen recording is scoped to, in place of the system audio track
/// (the app audio MeetingScreenCapture.swift takes from ScreenCaptureKit): WASAPI process
/// loopback that includes the app's process tree (Windows 10 2004 and later), so a browser's or
/// a meeting app's audio helper process is heard with it.
/// </summary>
/// <remarks>
/// The process is looked up when the capture is built and again whenever it is rebuilt (a new
/// default output device, a failure). Process loopback reports nothing when its process exits:
/// the track simply goes quiet, so an app that quits and starts again mid-meeting is heard again
/// only after such a rebuild.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed class ApplicationAudioStream(string bundleId, IUptime uptime) : ContinuousCaptureStream("Slovo app audio", uptime)
{
    static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);

    protected override Source Build(IReadOnlySet<string> failedIds)
    {
        var process = Screen.CaptureWindows.SoundProcess(bundleId)
            ?? throw new SlovoException(CoreStrings.Get("meetings.error.screenRecordingAppNotRunning"));
        var id = "app-loopback:" + process;
        if (failedIds.Contains(id))
        {
            throw new SlovoException(CoreStrings.Format("meetings.error.appAudioFailed", "loopback"));
        }
        var build = new WasapiRecorderBuilder()
            .WithProcessLoopback(process, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithBufferLength(AudioEndpoints.BufferMilliseconds)
            .WithMmcssThreadPriority("Audio")
            .BuildAsync();
        // The activation completes on a system thread; this queue has no synchronization
        // context, so waiting here cannot deadlock.
        if (!build.Wait(ActivationTimeout))
        {
            throw new SlovoException(CoreStrings.Format("meetings.error.appAudioFailed", "timeout"));
        }
        return new Source(build.GetAwaiter().GetResult(), null, bundleId, id);
    }

    protected override string LostMessage(Exception lastError) =>
        CoreStrings.Format("meetings.error.appAudioFailed", AudioEndpoints.Describe(lastError));

    protected override string StartFailedMessage(Exception lastError) =>
        CoreStrings.Format("meetings.error.appAudioFailed", AudioEndpoints.Describe(lastError));

    protected override string StartTimeoutMessage() => CoreStrings.Format("meetings.error.appAudioFailed", "timeout");

    /// <summary>A new default output device can stop loopback; rebuild once it settles, as system audio does.</summary>
    protected override void OnDefaultDeviceChanged(DataFlow flow, Role role, string? deviceId)
    {
        if (flow == DataFlow.Render && role == AudioEndpoints.DefaultRole)
        {
            ScheduleSwitch(() => deviceId ?? "none", "following the new output device");
        }
    }
}

/// <summary>
/// The microphone track of a meeting (MeetingMicrophoneCapture.swift): the chosen
/// microphone, or the default when it is missing or will not open, then any other input.
/// The stream moves back to the chosen microphone when it is connected again and, while
/// the default is in use, to a new default input. Samples are raw 16 kHz mono; the meeting
/// recorder applies the microphone boost as on macOS.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MeetingMicrophoneStream(string? deviceUid, IUptime uptime)
    : ContinuousCaptureStream("Slovo meeting microphone", uptime)
{
    readonly string? chosen = string.IsNullOrEmpty(deviceUid) ? null : deviceUid;

    protected override Source Build(IReadOnlySet<string> failedIds)
    {
        var candidates = Candidates().Where(id => !failedIds.Contains(id)).ToList();
        if (candidates.Count == 0)
        {
            throw new SlovoException(CoreStrings.Get("meetings.error.noMicrophone"));
        }
        if (chosen is not null && candidates[0] != chosen)
        {
            Log.Info("The chosen microphone is not connected; recording from the system default");
        }
        Exception? lastError = null;
        foreach (var id in candidates)
        {
            var device = AudioEndpoints.ActiveCapture(Enumerator!, id);
            if (device is null)
            {
                continue;
            }
            try
            {
                var recorder = AudioEndpoints.MicrophoneRecorder(device);
                return new Source(recorder, device, AudioEndpoints.NameOf(device), id);
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
            {
                Log.Warning($"A microphone did not open: {AudioEndpoints.Describe(exception)}");
                device.Dispose();
                lastError = exception;
            }
        }
        throw lastError is null
            ? new SlovoException(CoreStrings.Get("meetings.error.noMicrophone"))
            : new SlovoException(CoreStrings.Format("meetings.error.microphoneFailed", AudioEndpoints.Describe(lastError)), lastError);
    }

    /// <summary>The chosen microphone when connected, then the default, then every other input.</summary>
    List<string> Candidates()
    {
        var connected = AudioEndpoints.ActiveCaptureDevices(Enumerator!).Select(device => device.Uid).ToList();
        var defaultId = AudioEndpoints.DefaultId(Enumerator!, DataFlow.Capture);
        return [.. CapturePolicy.PreferredDeviceIds(chosen, connected.Contains, defaultId, connected)];
    }

    protected override string LostMessage(Exception lastError) => CoreStrings.Get("audio.error.microphoneLost");

    protected override string StartFailedMessage(Exception lastError) =>
        CoreStrings.Format("meetings.error.microphoneFailed", AudioEndpoints.Describe(lastError));

    protected override string StartTimeoutMessage() => CoreStrings.Format("meetings.error.microphoneFailed", "timeout");

    protected override void OnDeviceStateChanged(string deviceId, DeviceState state)
    {
        if (state != DeviceState.Active)
        {
            if (deviceId == CurrentDeviceId)
            {
                Restart("lost its microphone");
            }
            return;
        }
        OnDeviceAdded(deviceId);
    }

    /// <summary>The chosen microphone coming back replaces the one the recording fell back to.</summary>
    protected override void OnDeviceAdded(string deviceId)
    {
        if (chosen is not null && deviceId == chosen && CurrentDeviceId != chosen)
        {
            ScheduleSwitch(PreferredId, "switching back to the chosen microphone");
        }
    }

    /// <summary>A new default input matters only while the recording is not on the chosen microphone.</summary>
    protected override void OnDefaultDeviceChanged(DataFlow flow, Role role, string? deviceId)
    {
        if (flow == DataFlow.Capture && role == AudioEndpoints.DefaultRole && (CurrentDeviceId is null || CurrentDeviceId != chosen))
        {
            ScheduleSwitch(PreferredId, "following the new default microphone");
        }
    }

    string? PreferredId()
    {
        var candidates = Candidates();
        return candidates.Count > 0 ? candidates[0] : null;
    }
}

/// <summary>
/// The system audio track (SystemAudioTap.swift): everything the computer plays except
/// Slovo's own sounds, through WASAPI process loopback that excludes Slovo's process tree
/// (Windows 10 2004 and later). Where that is unavailable or fails, endpoint loopback of the
/// default output device is used instead, which includes Slovo's cues.
/// </summary>
/// <remarks>
/// Loopback delivers no packets while nothing plays. The stream reports the uptime of every
/// chunk's first sample, so the meeting recorder pads the silence in between itself. The
/// stream is rebuilt when the default output device changes (headphones plugged in), and
/// when it fails, with the retries of <see cref="ContinuousCaptureStream"/>.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class SystemAudioStream(IUptime uptime) : ContinuousCaptureStream("Slovo system audio", uptime)
{
    static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);

    /// <summary>ActivateAudioInterfaceAsync with AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS needs build 19041.</summary>
    [SupportedOSPlatformGuard("windows10.0.19041")]
    public static bool SupportsProcessLoopback => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    const string ProcessLoopbackId = "process-loopback";

    protected override Source Build(IReadOnlySet<string> failedIds)
    {
        var render = AudioEndpoints.Default(Enumerator!, DataFlow.Render);
        var name = render is null ? null : AudioEndpoints.NameOf(render);
        if (SupportsProcessLoopback && !failedIds.Contains(ProcessLoopbackId))
        {
            try
            {
                var recorder = ProcessLoopback();
                render?.Dispose();
                return new Source(recorder, null, name, ProcessLoopbackId);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log.Warning($"Process loopback failed, recording the output device instead: {AudioEndpoints.Describe(exception)}");
            }
        }
        if (render is null)
        {
            throw new SlovoException(CoreStrings.Format("meetings.error.tapFailed", CoreStrings.Get("audio.error.noOutputDevice")));
        }
        if (failedIds.Contains(render.ID))
        {
            render.Dispose();
            throw new SlovoException(CoreStrings.Format("meetings.error.tapFailed", "loopback"));
        }
        try
        {
            var recorder = new WasapiRecorderBuilder()
                .WithDevice(render)
                .WithLoopbackCapture()
                .WithSharedMode()
                .WithEventSync()
                .WithBufferLength(AudioEndpoints.BufferMilliseconds)
                .WithMmcssThreadPriority("Audio")
                .Build();
            return new Source(recorder, render, name, render.ID);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
        {
            render.Dispose();
            throw new SlovoException(CoreStrings.Format("meetings.error.tapFailed", AudioEndpoints.Describe(exception)), exception);
        }
    }

    /// <summary>
    /// Everything except Slovo and its children. The virtual device has no mix format, so
    /// NAudio asks for 44.1 kHz stereo float, which the engine converts to.
    /// </summary>
    [SupportedOSPlatform("windows10.0.19041")]
    static WasapiRecorder ProcessLoopback()
    {
        var build = new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree)
            .WithBufferLength(AudioEndpoints.BufferMilliseconds)
            .WithMmcssThreadPriority("Audio")
            .BuildAsync();
        // The activation completes on a system thread; this queue has no synchronization
        // context, so waiting here cannot deadlock.
        if (!build.Wait(ActivationTimeout))
        {
            throw new TimeoutException("Process loopback activation did not complete");
        }
        return build.GetAwaiter().GetResult();
    }

    protected override string LostMessage(Exception lastError) => CoreStrings.Get("audio.error.systemAudioLost");

    protected override string StartFailedMessage(Exception lastError) =>
        CoreStrings.Format("meetings.error.tapFailed", AudioEndpoints.Describe(lastError));

    protected override string StartTimeoutMessage() => CoreStrings.Format("meetings.error.tapFailed", "timeout");

    protected override void OnDeviceStateChanged(string deviceId, DeviceState state)
    {
        if (state != DeviceState.Active && deviceId == CurrentDeviceId)
        {
            Restart("lost its output device");
        }
    }

    /// <summary>A new default output device (headphones plugged in) can stop loopback; rebuild once it settles.</summary>
    protected override void OnDefaultDeviceChanged(DataFlow flow, Role role, string? deviceId)
    {
        if (flow == DataFlow.Render && role == AudioEndpoints.DefaultRole)
        {
            ScheduleSwitch(() => deviceId ?? "none", "following the new output device");
        }
    }
}
