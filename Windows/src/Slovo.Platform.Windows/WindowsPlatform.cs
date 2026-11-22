using System.Runtime.Versioning;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Platform.Windows.Audio;
using Slovo.Platform.Windows.Input;
using Slovo.Platform.Windows.Screen;
using Slovo.Platform.Windows.Shell;

namespace Slovo.Platform.Windows;

/// <summary>Creates the Windows implementations of every platform service.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsPlatform
{
    /// <summary>
    /// Call once, on the UI thread, at start-up. <paramref name="mainThread"/> is where
    /// hotkey triggers and shortcut actions are delivered; <paramref name="uptime"/> is the
    /// monotonic clock of key timing and capture timestamps (SystemUptime in the app).
    /// </summary>
    public static PlatformServices Create(IMainThread mainThread, IUptime uptime) => new(
        new WasapiAudioRecorder(uptime),
        new WindowsAudioDevices(),
        new WindowsMeetingAudioSources(uptime),
        new KeyboardHookMonitor(mainThread, uptime),
        new WindowsGlobalShortcuts(mainThread),
        new WindowsTextInserter(),
        new WindowsForegroundApp(),
        new UiaFocusedInput(),
        new WindowsPermissions(),
        new WindowsSoundPlayer(),
        new RegistryAutostart(),
        new WindowsShell(),
        new MediaFoundationDecoder(),
        new WindowsScreenCapture());
}
