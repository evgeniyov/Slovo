using System.Runtime.Versioning;
using Microsoft.Win32;
using Slovo.Core.Logging;
using Slovo.Core.Platform;

namespace Slovo.Platform.Windows.Shell;

/// <summary>The Run entry that starts Slovo at sign-in, and Task Manager's switch for it.</summary>
public static class AutostartEntry
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>Where Task Manager's Startup tab records an entry the user turned off.</summary>
    public const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "Slovo";

    /// <summary>Passed by the Run entry so a start at sign-in stays in the tray instead of opening the window.</summary>
    public const string Argument = "--autostart";

    /// <summary>The Run value: the executable path in quotes, so a path with spaces starts, and <see cref="Argument"/>.</summary>
    public static string Command(string executablePath) => $"\"{executablePath}\" {Argument}";

    /// <summary>
    /// Whether Task Manager lets the entry run: no record, or one whose first byte is even
    /// (02 or 06 enabled, 03 or 07 disabled, followed by the time it was switched).
    /// </summary>
    public static bool IsApproved(byte[]? startupApproved) =>
        startupApproved is not { Length: > 0 } || (startupApproved[0] & 1) == 0;
}

/// <summary>Launch at sign-in through <c>HKCU\…\Run</c> (SMAppService on macOS).</summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryAutostart : IAutostart
{
    readonly string executablePath;
    readonly Logger log = Log.For("Autostart");

    public RegistryAutostart(string? executablePath = null)
    {
        this.executablePath = executablePath ?? Environment.ProcessPath ?? "Slovo.exe";
    }

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(AutostartEntry.RunKey);
                if (run?.GetValue(AutostartEntry.ValueName) is not string command || string.IsNullOrWhiteSpace(command))
                {
                    return false;
                }
                using var approved = Registry.CurrentUser.OpenSubKey(AutostartEntry.StartupApprovedKey);
                return AutostartEntry.IsApproved(approved?.GetValue(AutostartEntry.ValueName) as byte[]);
            }
            catch (Exception exception)
            {
                log.Warning($"Could not read the Run key: {exception.Message}");
                return false;
            }
        }
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(AutostartEntry.RunKey, writable: true);
            if (enabled)
            {
                run.SetValue(AutostartEntry.ValueName, AutostartEntry.Command(executablePath), RegistryValueKind.String);
            }
            else
            {
                run.DeleteValue(AutostartEntry.ValueName, throwOnMissingValue: false);
            }
            // A switch-off made in Task Manager would keep a re-enabled entry from running.
            using var approved = Registry.CurrentUser.OpenSubKey(AutostartEntry.StartupApprovedKey, writable: true);
            approved?.DeleteValue(AutostartEntry.ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            log.Error("Could not change the Run key", exception);
            return false;
        }
    }
}
