using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Slovo.Platform.Windows.Interop;

namespace Slovo.Platform.Windows.Input;

/// <summary>
/// The Windows counterpart of macOS Secure Event Input: when typed input would not reach
/// the foreground window. User Interface Privilege Isolation silently drops SendInput aimed
/// at a window of a process with a higher integrity level (an app run as administrator),
/// and nothing reaches any app while the secure desktop (UAC prompt, Ctrl+Alt+Del, the lock
/// screen) is the input desktop.
/// </summary>
public static class SecureInputRule
{
    /// <summary>Mandatory integrity levels (the last sub-authority of the label SID).</summary>
    public const int MediumIntegrity = 0x2000;
    public const int HighIntegrity = 0x3000;
    public const int SystemIntegrity = 0x4000;

    /// <summary>
    /// Whether input to the foreground process would be dropped. A process whose level
    /// cannot even be queried (access denied) is a protected or higher one, so it counts
    /// as above Slovo; other failures say nothing.
    /// </summary>
    public static bool IsBlocked(bool inputDesktopIsDefault, int? foregroundLevel, bool foregroundAccessDenied, int ownLevel)
    {
        if (!inputDesktopIsDefault)
        {
            return true;
        }
        if (foregroundLevel is { } level)
        {
            return level > ownLevel;
        }
        return foregroundAccessDenied;
    }
}

[SupportedOSPlatform("windows")]
internal static unsafe class SecureInput
{
    static readonly int OwnLevel = IntegrityLevel((uint)Environment.ProcessId, out _) ?? SecureInputRule.MediumIntegrity;

    /// <summary>True when input would not reach the foreground window.</summary>
    public static bool IsActive()
    {
        var onDefaultDesktop = IsInputDesktopDefault();
        if (!onDefaultDesktop)
        {
            return true;
        }
        var window = User32.GetForegroundWindow();
        if (window == 0)
        {
            return false;
        }
        User32.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId)
        {
            return false;
        }
        var level = IntegrityLevel(processId, out var accessDenied);
        return SecureInputRule.IsBlocked(onDefaultDesktop, level, accessDenied, OwnLevel);
    }

    /// <summary>Whether the desktop receiving input is the user's "Default" one, not Winlogon's secure desktop.</summary>
    static bool IsInputDesktopDefault()
    {
        var desktop = User32.OpenInputDesktop(0, 0, User32.DESKTOP_READOBJECTS);
        if (desktop == 0)
        {
            // Only the secure desktop refuses a normal process.
            return false;
        }
        try
        {
            var buffer = stackalloc char[64];
            if (User32.GetUserObjectInformation(desktop, User32.UOI_NAME, buffer, 64 * sizeof(char), out _) == 0)
            {
                return true;
            }
            var text = new ReadOnlySpan<char>(buffer, 64);
            var end = text.IndexOf('\0');
            var name = end >= 0 ? text[..end] : text;
            return name.Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            User32.CloseDesktop(desktop);
        }
    }

    /// <summary>The mandatory integrity level of a process, or null with the reason.</summary>
    internal static int? IntegrityLevel(uint processId, out bool accessDenied)
    {
        accessDenied = false;
        var process = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, 0, processId);
        if (process == 0)
        {
            accessDenied = Marshal.GetLastPInvokeError() == Kernel32.ERROR_ACCESS_DENIED;
            return null;
        }
        try
        {
            if (Advapi32.OpenProcessToken(process, Advapi32.TOKEN_QUERY, out var token) == 0)
            {
                accessDenied = Marshal.GetLastPInvokeError() == Kernel32.ERROR_ACCESS_DENIED;
                return null;
            }
            try
            {
                // TOKEN_MANDATORY_LABEL: a SID_AND_ATTRIBUTES whose first member points at the label SID.
                var buffer = stackalloc byte[256];
                if (Advapi32.GetTokenInformation(token, Advapi32.TokenIntegrityLevel, buffer, 256, out _) == 0)
                {
                    return null;
                }
                var sid = *(nint*)buffer;
                if (sid == 0)
                {
                    return null;
                }
                var count = *Advapi32.GetSidSubAuthorityCount(sid);
                if (count == 0)
                {
                    return null;
                }
                return (int)*Advapi32.GetSidSubAuthority(sid, (uint)(count - 1));
            }
            finally
            {
                Kernel32.CloseHandle(token);
            }
        }
        finally
        {
            Kernel32.CloseHandle(process);
        }
    }
}
