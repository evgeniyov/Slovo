using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Slovo.Core.Localization;
using Slovo.Core.Model;

namespace Slovo.Platform.Windows.Audio;

/// <summary>Endpoint lookups shared by the recorder, the device list and the meeting streams.</summary>
[SupportedOSPlatform("windows")]
internal static class AudioEndpoints
{
    /// <summary>AUDCLNT_E_DEVICE_INVALIDATED: the endpoint was unplugged, disabled or reconfigured.</summary>
    public const int DeviceInvalidated = unchecked((int)0x88890004);

    /// <summary>The default device for recording and playback ("Default Device" in Sound settings).</summary>
    public const Role DefaultRole = Role.Console;

    /// <summary>Event-driven shared-mode buffer: large enough to ride out a busy moment on the capture thread.</summary>
    public const int BufferMilliseconds = 100;

    /// <summary>An active capture endpoint by id, or null when it is missing, disabled or unplugged.</summary>
    public static MMDevice? ActiveCapture(MMDeviceEnumerator enumerator, string? id) => Active(enumerator, id, DataFlow.Capture);

    public static MMDevice? Active(MMDeviceEnumerator enumerator, string? id, DataFlow flow)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }
        MMDevice? device = null;
        try
        {
            device = enumerator.GetDevice(id);
            if (device.State == DeviceState.Active && device.DataFlow == flow)
            {
                return device;
            }
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or InvalidOperationException)
        {
            // Not found (E_NOTFOUND) or gone meanwhile.
        }
        device?.Dispose();
        return null;
    }

    public static MMDevice? Default(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try
        {
            return enumerator.TryGetDefaultAudioEndpoint(flow, DefaultRole, out var device) ? device : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static string? DefaultId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        using var device = Default(enumerator, flow);
        return device?.ID;
    }

    /// <summary>Every active capture endpoint, as ids and names.</summary>
    public static List<AudioInputDevice> ActiveCaptureDevices(MMDeviceEnumerator enumerator)
    {
        var result = new List<AudioInputDevice>();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        foreach (var device in devices)
        {
            using (device)
            {
                try
                {
                    var id = device.ID;
                    result.Add(new AudioInputDevice(id, NameOf(device) ?? id));
                }
                catch (COMException)
                {
                    // Removed while listing.
                }
            }
        }
        return result;
    }

    public static string? NameOf(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>A shared-mode, event-driven recorder on a capture endpoint, in the device's own mix format.</summary>
    public static WasapiRecorder MicrophoneRecorder(MMDevice device) => new WasapiRecorderBuilder()
        .WithDevice(device)
        .WithSharedMode()
        .WithEventSync()
        .WithBufferLength(BufferMilliseconds)
        .WithMmcssThreadPriority("Audio")
        .Build();

    /// <summary>The layout of a recorder's format, or a localized error for one Slovo cannot read.</summary>
    public static Core.Audio.PcmLayout LayoutOf(WasapiRecorder recorder) =>
        CaptureFormat.LayoutOf(recorder.WaveFormat)
        ?? throw new SlovoException(CoreStrings.Get("audio.error.formatUnsupported"));

    /// <summary>A short, untranslated reason for logs and the "%@" of an error message.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        SlovoException slovo => slovo.Message,
        COMException com => $"{com.Message.Trim()} (0x{com.HResult:X8})",
        _ => exception.Message,
    };
}
