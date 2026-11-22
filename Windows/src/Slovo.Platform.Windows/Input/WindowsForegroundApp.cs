using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Platform.Windows.Interop;

namespace Slovo.Platform.Windows.Input;

/// <summary>How a foreground process becomes an <see cref="AppIdentity"/> (FrontmostApp.identity on macOS).</summary>
public static class ForegroundAppIdentity
{
    /// <summary>The UWP frame that hosts Store apps; the app itself runs in a child window's process.</summary>
    public const string FrameHost = "applicationframehost.exe";

    /// <summary>
    /// The identity of a process, or null for Slovo itself and for a process without an
    /// image path. The id is the lower-case executable name ("telegram.exe"); the name is
    /// the file description, else the product name, else the executable name without
    /// its extension.
    /// </summary>
    public static AppIdentity? Identity(string? imagePath, string? description, string? productName, bool isOwnProcess)
    {
        if (isOwnProcess || string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }
        var fileName = FileName(imagePath.Trim());
        if (fileName.Length == 0)
        {
            return null;
        }
        var bundleId = fileName.ToLowerInvariant();
        var dot = fileName.LastIndexOf('.');
        var name = FirstNonEmpty(description, productName) ?? (dot > 0 ? fileName[..dot] : fileName);
        return new AppIdentity(bundleId, name);
    }

    /// <summary>The last component of a Windows path, whatever system reads it.</summary>
    public static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                return trimmed;
            }
        }
        return null;
    }
}

/// <summary>
/// Tracks the foreground app other than Slovo, so clicking Slovo's own windows does not
/// make Slovo the dictation target (FrontmostApp.swift). A WinEvent hook for
/// EVENT_SYSTEM_FOREGROUND, on a thread with a message loop, remembers every other app
/// that comes to the front; <see cref="CurrentApp"/> also reads the foreground window
/// directly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsForegroundApp : IForegroundApp, IDisposable
{
    [ThreadStatic] static WindowsForegroundApp? hookOwner;

    readonly MessageWindowThread thread = new("Slovo foreground app");
    readonly ConcurrentDictionary<string, (string? Description, string? Product)> versionInfo = new(StringComparer.OrdinalIgnoreCase);
    readonly Logger log = Log.For("ForegroundApp");
    readonly uint ownProcessId = (uint)Environment.ProcessId;
    nint winEventHook;
    volatile AppIdentity? lastApp;

    public WindowsForegroundApp()
    {
        thread.OnStart = () =>
        {
            hookOwner = this;
            winEventHook = User32.SetWinEventHook(User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND, 0,
                (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void>)&ForegroundChanged,
                0, 0, User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);
            if (winEventHook == 0)
            {
                log.Warning("SetWinEventHook failed; the foreground app is read only when dictation starts");
            }
        };
        thread.OnStop = () =>
        {
            if (winEventHook != 0)
            {
                User32.UnhookWinEvent(winEventHook);
                winEventHook = 0;
            }
            hookOwner = null;
        };
        thread.Start(TimeSpan.FromSeconds(3));
        Record(User32.GetForegroundWindow());
    }

    /// <summary>The foreground app when it is not Slovo, else the last other app that was.</summary>
    public AppIdentity? CurrentApp()
    {
        Record(User32.GetForegroundWindow());
        return lastApp;
    }

    public void Dispose() => thread.Dispose();

    void Record(nint window)
    {
        try
        {
            if (Resolve(window) is { } identity)
            {
                lastApp = identity;
            }
        }
        catch (Exception exception)
        {
            log.Error("Reading the foreground app failed", exception);
        }
    }

    AppIdentity? Resolve(nint window)
    {
        if (window == 0)
        {
            return null;
        }
        User32.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return null;
        }
        var path = ImagePath(processId);
        if (path is not null && ForegroundAppIdentity.FileName(path).Equals(ForegroundAppIdentity.FrameHost, StringComparison.OrdinalIgnoreCase))
        {
            // A Store app: its frame belongs to ApplicationFrameHost, its content to the app.
            var hosted = HostedProcess(window, processId);
            if (hosted != 0)
            {
                processId = hosted;
                path = ImagePath(processId);
            }
        }
        if (path is null)
        {
            return null;
        }
        var (description, product) = versionInfo.GetOrAdd(path, static file =>
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
        return ForegroundAppIdentity.Identity(path, description, product, processId == ownProcessId);
    }

    internal static string? ImagePath(uint processId)
    {
        var process = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, 0, processId);
        if (process == 0)
        {
            return null;
        }
        try
        {
            const int capacity = 1024;
            var buffer = stackalloc char[capacity];
            var size = (uint)capacity;
            return Kernel32.QueryFullProcessImageName(process, 0, buffer, ref size) != 0 ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            Kernel32.CloseHandle(process);
        }
    }

    struct HostSearch
    {
        public uint HostProcessId;
        public uint FoundProcessId;
    }

    internal static uint HostedProcess(nint frame, uint hostProcessId)
    {
        var search = new HostSearch { HostProcessId = hostProcessId };
        User32.EnumChildWindows(frame, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&FindHostedChild, (nint)(&search));
        return search.FoundProcessId;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int FindHostedChild(nint child, nint parameter)
    {
        var search = (HostSearch*)parameter;
        User32.GetWindowThreadProcessId(child, out var processId);
        if (processId != 0 && processId != search->HostProcessId)
        {
            search->FoundProcessId = processId;
            return 0;
        }
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static void ForegroundChanged(nint hook, uint eventType, nint window, int objectId, int childId, uint eventThread, uint eventTime)
    {
        try
        {
            hookOwner?.Record(window);
        }
        catch
        {
            // Never let an exception reach user32.
        }
    }
}
