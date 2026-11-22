using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Platform.Windows.Threading;

namespace Slovo.Platform.Windows.Audio;

/// <summary>Sorting of the microphone list, apart from the devices.</summary>
public static class AudioDeviceList
{
    /// <summary>By name as a person reads it ("Mic 2" before "Mic 10"), like Finder's order on macOS.</summary>
    public static List<AudioInputDevice> Sorted(IEnumerable<AudioInputDevice> devices, CultureInfo? culture = null)
    {
        var compare = (culture ?? CultureInfo.CurrentCulture).CompareInfo;
        var list = devices.ToList();
        list.Sort((a, b) =>
        {
            var byName = compare.Compare(a.Name, b.Name, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
            return byName != 0 ? byName : string.CompareOrdinal(a.Uid, b.Uid);
        });
        return list;
    }
}

/// <summary>
/// Audio input devices from the MMDevice API (AudioDevices.swift and AudioDeviceObserver),
/// with IMMNotificationClient reporting devices that come, go or become the default.
/// </summary>
/// <remarks>
/// Lookups run on a queue of their own (an MTA thread) and the caller waits for them;
/// <see cref="DevicesChanged"/> is raised on the queue, which the interface allows
/// ("any thread").
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsAudioDevices : IAudioDevices, IDisposable
{
    readonly WorkerQueue queue = new("Slovo audio devices");
    readonly Logger log = Log.For("AudioDevices");
    MMDeviceEnumerator? enumerator;
    MMDeviceNotificationClient? notifications;

    public WindowsAudioDevices()
    {
        queue.Post(() =>
        {
            try
            {
                enumerator = new MMDeviceEnumerator();
                notifications = enumerator.CreateNotificationClient(useSynchronizationContext: false);
                // Raised on Windows' audio notification thread, where handlers must not block.
                notifications.DeviceAdded += (_, _) => Changed();
                notifications.DeviceRemoved += (_, _) => Changed();
                notifications.DeviceStateChanged += (_, _) => Changed();
                notifications.DefaultDeviceChanged += (_, arguments) =>
                {
                    if (arguments.Flow == DataFlow.Capture && arguments.Role == AudioEndpoints.DefaultRole)
                    {
                        Changed();
                    }
                };
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException)
            {
                log.Error("Could not watch audio devices", exception);
            }
        });
    }

    public event Action? DevicesChanged;

    public IReadOnlyList<AudioInputDevice> InputDevices() => queue.Invoke<IReadOnlyList<AudioInputDevice>>(() =>
    {
        if (enumerator is null)
        {
            return [];
        }
        try
        {
            return AudioDeviceList.Sorted(AudioEndpoints.ActiveCaptureDevices(enumerator));
        }
        catch (COMException exception)
        {
            log.Error("Could not list microphones", exception);
            return [];
        }
    }, TimeSpan.FromSeconds(3), []);

    public void Dispose()
    {
        queue.Invoke(() =>
        {
            notifications?.Dispose();
            notifications = null;
            enumerator?.Dispose();
            enumerator = null;
        }, TimeSpan.FromSeconds(1));
        queue.Dispose();
    }

    /// <summary>Hops off the notification thread before anyone's handler runs.</summary>
    void Changed() => queue.Post(() => DevicesChanged?.Invoke());
}
