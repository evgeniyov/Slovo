using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Platform.Windows.Inert;
using Slovo.Platform.Windows.Shell;

namespace Slovo.Platform.Windows;

/// <summary>
/// Every operating-system service the app needs, created once by the composition root:
/// <see cref="WindowsPlatform.Create"/> on Windows, <see cref="CreateInert"/> elsewhere, or
/// <see cref="CreateForCurrentSystem"/> to pick. Disposing it stops the hook, the capture
/// queues and the helper threads.
/// </summary>
public sealed record PlatformServices(
    IAudioRecorder Recorder,
    IAudioDevices AudioDevices,
    IMeetingAudioSources MeetingAudio,
    IHotkeyMonitor Hotkey,
    IGlobalShortcuts Shortcuts,
    ITextInserter TextInserter,
    IForegroundApp ForegroundApp,
    IFocusedInput FocusedInput,
    IPermissions Permissions,
    ISoundPlayer Sounds,
    IAutostart Autostart,
    IShell Shell,
    IAudioDecoder Decoder,
    IScreenCapture ScreenCapture) : IDisposable
{
    /// <summary>True for the stand-ins that do nothing.</summary>
    public bool IsInert { get; init; }

    /// <summary>The Windows services on Windows, the inert stand-ins anywhere else.</summary>
    public static PlatformServices CreateForCurrentSystem(IMainThread mainThread, IUptime uptime) =>
        OperatingSystem.IsWindows() ? WindowsPlatform.Create(mainThread, uptime) : CreateInert(mainThread, uptime);

    /// <summary>
    /// Stand-ins for systems other than Windows, so the interface can be developed on a Mac:
    /// the recorder fails with a clear message, the hook never fires, inserting reports
    /// "copied", every permission is granted. Opening folders and links still works.
    /// </summary>
    public static PlatformServices CreateInert(IMainThread mainThread, IUptime uptime) => new(
        new InertAudioRecorder(),
        new InertAudioDevices(),
        new InertMeetingAudioSources(),
        new InertHotkeyMonitor(),
        new InertGlobalShortcuts(),
        new InertTextInserter(),
        new InertForegroundApp(),
        new InertFocusedInput(),
        new InertPermissions(),
        new InertSoundPlayer(),
        new InertAutostart(),
        new WindowsShell(),
        new InertAudioDecoder(),
        new InertScreenCapture())
    {
        IsInert = true,
    };

    public void Dispose()
    {
        // The hook first, so no trigger arrives while the rest shuts down.
        object[] services = [Hotkey, Shortcuts, Recorder, MeetingAudio, ScreenCapture, AudioDevices, TextInserter, ForegroundApp, FocusedInput, Sounds, Decoder];
        foreach (var service in services.OfType<IDisposable>())
        {
            try
            {
                service.Dispose();
            }
            catch (Exception exception)
            {
                Log.For("Platform").Error($"Disposing {service.GetType().Name} failed", exception);
            }
        }
    }
}
