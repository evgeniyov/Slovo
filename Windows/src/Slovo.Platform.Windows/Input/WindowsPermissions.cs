using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Platform.Windows.Input;

/// <summary>The privacy switches for the microphone, read from the capability consent store.</summary>
public static class MicrophoneConsent
{
    /// <summary>Under HKLM (the device-wide switch) and HKCU (the user's switches).</summary>
    public const string ConsentStoreKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    /// <summary>"Let desktop apps access your microphone", the switch that covers Slovo.</summary>
    public const string NonPackagedSubkey = "NonPackaged";

    /// <summary>
    /// Denied when any switch that applies to a desktop app says "Deny": the device-wide
    /// one, the user's "Microphone access", or "Let desktop apps access your microphone".
    /// A missing value is the default, which allows.
    /// </summary>
    public static PermissionStatus Decide(string? machine, string? user, string? userDesktopApps, string? machineDesktopApps = null)
    {
        foreach (var value in new[] { machine, machineDesktopApps, user, userDesktopApps })
        {
            if (string.Equals(value?.Trim(), "Deny", StringComparison.OrdinalIgnoreCase))
            {
                return PermissionStatus.Denied;
            }
        }
        return PermissionStatus.Granted;
    }

    /// <summary>The Windows Settings page for a permission (the counterpart of the macOS privacy panes).</summary>
    public static string SettingsUri(PermissionKind kind) => kind switch
    {
        PermissionKind.Microphone => "ms-settings:privacy-microphone",
        PermissionKind.ScreenRecording => "ms-settings:privacy-graphicscaptureprogrammatic",
        PermissionKind.SystemAudioRecording => "ms-settings:sound",
        // Windows has no switch for reading or sending keys; the keyboard page is the nearest.
        PermissionKind.Accessibility or PermissionKind.InputMonitoring => "ms-settings:easeofaccess-keyboard",
        _ => "ms-settings:privacy",
    };
}

/// <summary>
/// Permission status on Windows (Permissions.swift). Only the microphone has a switch a
/// desktop app must respect; keyboard hooks, SendInput and loopback capture need no grant,
/// so those are always granted. Desktop apps are never prompted: a denied microphone can
/// only be changed in Settings.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPermissions : IPermissions
{
    readonly Logger log = Log.For("Permissions");

    public PermissionStatus Status(PermissionKind kind) => kind switch
    {
        PermissionKind.Microphone => MicrophoneStatus(),
        _ => PermissionStatus.Granted,
    };

    public Task<PermissionStatus> RequestAsync(PermissionKind kind)
    {
        var status = Status(kind);
        if (status == PermissionStatus.Denied)
        {
            OpenSystemSettings(kind);
        }
        return Task.FromResult(status);
    }

    public void OpenSystemSettings(PermissionKind kind)
    {
        try
        {
            Process.Start(new ProcessStartInfo(MicrophoneConsent.SettingsUri(kind)) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception)
        {
            log.Error($"Could not open Settings for {EnumText.Raw(kind)}", exception);
        }
    }

    public bool IsSecureInputActive => SecureInput.IsActive();

    PermissionStatus MicrophoneStatus()
    {
        try
        {
            return MicrophoneConsent.Decide(
                Read(Registry.LocalMachine, MicrophoneConsent.ConsentStoreKey),
                Read(Registry.CurrentUser, MicrophoneConsent.ConsentStoreKey),
                Read(Registry.CurrentUser, $@"{MicrophoneConsent.ConsentStoreKey}\{MicrophoneConsent.NonPackagedSubkey}"),
                Read(Registry.LocalMachine, $@"{MicrophoneConsent.ConsentStoreKey}\{MicrophoneConsent.NonPackagedSubkey}"));
        }
        catch (Exception exception)
        {
            log.Warning($"Could not read the microphone privacy settings: {exception.Message}");
            return PermissionStatus.Unknown;
        }
    }

    static string? Read(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue("Value") as string;
    }
}
