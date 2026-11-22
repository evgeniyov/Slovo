using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Slovo.Platform.Windows.Input;
using Slovo.Platform.Windows.Interop;

namespace Slovo.Platform.Windows.Screen;

/// <summary>
/// Reads the desktop's top-level windows and processes for screen recording: which apps can be
/// recorded, the window an app is recorded through, and the process tree its sound comes from.
/// The rules themselves are <see cref="CaptureTargets"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class CaptureWindows
{
    static readonly ConcurrentDictionary<string, (string? Description, string? Product)> VersionInfo = new(StringComparer.OrdinalIgnoreCase);
    static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    /// <summary>The windows that can be recorded, frontmost first, Slovo's own left out.</summary>
    public static List<WindowCandidate> Enumerate()
    {
        var handles = new List<nint>();
        var gcHandle = GCHandle.Alloc(handles);
        try
        {
            User32.EnumWindows((nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&Collect, GCHandle.ToIntPtr(gcHandle));
        }
        finally
        {
            gcHandle.Free();
        }
        var identities = new Dictionary<uint, (string BundleId, string Name)?>();
        var result = new List<WindowCandidate>();
        foreach (var handle in handles)
        {
            if (Candidate(handle, identities) is { } candidate)
            {
                result.Add(candidate);
            }
        }
        return result;
    }

    /// <summary>Whether the window still exists and is on screen in a form that can be recorded.</summary>
    public static bool IsUsable(nint handle) => User32.IsWindow(handle) != 0 && User32.IsWindowVisible(handle) != 0 && !IsCloaked(handle);

    /// <summary>
    /// The process whose tree plays the app's sound: its frontmost window's process, walked up to
    /// the first ancestor that is the same program; else any process of the app. Null when the
    /// app is not running.
    /// </summary>
    public static uint? SoundProcess(string bundleId)
    {
        var processes = Processes();
        uint? start = CaptureTargets.BestWindow(Enumerate(), bundleId)?.ProcessId ?? CaptureTargets.AnyProcess(bundleId, processes);
        return start is { } found ? CaptureTargets.RootProcess(found, processes) : null;
    }

    /// <summary>Every process with its parent and its lower-case executable name.</summary>
    public static Dictionary<uint, (uint Parent, string Executable)> Processes()
    {
        var result = new Dictionary<uint, (uint, string)>();
        var snapshot = Kernel32.CreateToolhelp32Snapshot(Kernel32.TH32CS_SNAPPROCESS, 0);
        if (snapshot == Kernel32.InvalidHandle || snapshot == 0)
        {
            return result;
        }
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)sizeof(ProcessEntry32) };
            if (Kernel32.Process32First(snapshot, &entry) == 0)
            {
                return result;
            }
            do
            {
                var name = new string(entry.ExeFile).ToLowerInvariant();
                result[entry.ProcessId] = (entry.ParentProcessId, name);
                entry.Size = (uint)sizeof(ProcessEntry32);
            }
            while (Kernel32.Process32Next(snapshot, &entry) != 0);
        }
        finally
        {
            Kernel32.CloseHandle(snapshot);
        }
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int Collect(nint window, nint parameter)
    {
        try
        {
            if (GCHandle.FromIntPtr(parameter).Target is List<nint> list)
            {
                list.Add(window);
            }
        }
        catch
        {
            // Never let an exception reach user32.
        }
        return 1;
    }

    static WindowCandidate? Candidate(nint handle, Dictionary<uint, (string BundleId, string Name)?> identities)
    {
        if (User32.IsWindowVisible(handle) == 0 || User32.GetWindow(handle, User32.GW_OWNER) != 0 || IsCloaked(handle))
        {
            return null;
        }
        var exStyle = (uint)(long)User32.GetWindowLongPtr(handle, User32.GWL_EXSTYLE);
        if ((exStyle & User32.WS_EX_TOOLWINDOW) != 0 && (exStyle & User32.WS_EX_APPWINDOW) == 0)
        {
            return null;
        }
        if (User32.GetWindowTextLength(handle) == 0 || CaptureTargets.IsShellWindowClass(ClassName(handle)))
        {
            return null;
        }
        var minimized = User32.IsIconic(handle) != 0;
        User32.GetWindowRect(handle, out var rect);
        if (!CaptureTargets.IsLargeEnough(rect.Width, rect.Height, minimized))
        {
            return null;
        }
        User32.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0 || processId == OwnProcessId)
        {
            return null;
        }
        var path = WindowsForegroundApp.ImagePath(processId);
        if (path is not null && ForegroundAppIdentity.FileName(path).Equals(ForegroundAppIdentity.FrameHost, StringComparison.OrdinalIgnoreCase))
        {
            // A Store app: its frame belongs to ApplicationFrameHost, which hosts every Store app,
            // so the app — its name and the process its sound comes from — is the hosted one.
            processId = WindowsForegroundApp.HostedProcess(handle, processId);
            if (processId == 0 || processId == OwnProcessId)
            {
                // Suspended: nothing of it is on screen to record.
                return null;
            }
            path = null;
        }
        if (!identities.TryGetValue(processId, out var identity))
        {
            identity = Identity(path ?? WindowsForegroundApp.ImagePath(processId));
            identities[processId] = identity;
        }
        return identity is { } known
            ? new WindowCandidate(handle, processId, known.BundleId, known.Name, rect.Width, rect.Height, minimized)
            : null;
    }

    /// <summary>The app an executable is, as the foreground-app tracking names it.</summary>
    static (string BundleId, string Name)? Identity(string? path)
    {
        if (path is null)
        {
            return null;
        }
        var (description, product) = VersionInfo.GetOrAdd(path, static file =>
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(file);
                return (info.FileDescription, info.ProductName);
            }
            catch (Exception)
            {
                return (null, null);
            }
        });
        return ForegroundAppIdentity.Identity(path, description, product, isOwnProcess: false) is { } app ? (app.BundleId, app.Name) : null;
    }

    static bool IsCloaked(nint handle)
    {
        uint cloaked = 0;
        return Dwmapi.DwmGetWindowAttribute(handle, Dwmapi.DWMWA_CLOAKED, &cloaked, sizeof(uint)) >= 0 && cloaked != 0;
    }

    static string ClassName(nint handle)
    {
        const int capacity = 256;
        var buffer = stackalloc char[capacity];
        var length = User32.GetClassName(handle, buffer, capacity);
        return length > 0 ? new string(buffer, 0, length) : "";
    }
}
