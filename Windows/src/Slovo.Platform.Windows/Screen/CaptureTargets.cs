using Slovo.Core.Model;

namespace Slovo.Platform.Windows.Screen;

/// <summary>A top-level window that could be recorded, as the window list reads it.</summary>
/// <param name="ProcessId">The app's process: for a Store app the one ApplicationFrameHost hosts, whose sound it is.</param>
/// <param name="BundleId">The app's id as <see cref="AppIdentity.BundleId"/> names it (the lower-case executable name).</param>
public sealed record WindowCandidate(nint Handle, uint ProcessId, string BundleId, string AppName, int Width, int Height, bool IsMinimized);

/// <summary>
/// The rules that turn the desktop's windows into the app picker and a recording's target
/// (the counterpart of ScreenCaptureKit's <c>SCShareableContent.applications</c>). Pure, so the
/// rules are tested on any system; <see cref="CaptureWindows"/> reads the real windows.
/// </summary>
public static class CaptureTargets
{
    /// <summary>A window smaller than this is a helper or a notification, not something a meeting is in.</summary>
    public const int MinimumWidth = 120;
    public const int MinimumHeight = 80;

    /// <summary>The desktop, the taskbar and their helpers: windows every user has and nobody means.</summary>
    static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    public static bool IsShellWindowClass(string className) => ShellClasses.Contains(className);

    /// <summary>Whether a visible, uncloaked, unowned window of this size may be listed.</summary>
    public static bool IsLargeEnough(int width, int height, bool isMinimized) =>
        isMinimized || (width >= MinimumWidth && height >= MinimumHeight);

    /// <summary>One entry per app, sorted by name as people read it, then by id.</summary>
    public static IReadOnlyList<AppIdentity> Apps(IEnumerable<WindowCandidate> windows) =>
        windows.GroupBy(window => window.BundleId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AppIdentity(group.Key, group.First().AppName))
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.BundleId, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The window of an app a recording follows: the frontmost one that is not minimized, since
    /// the window list is in z-order and the one on top is the one in use (a meeting window
    /// brought forward over the app's main window); a minimized one only when that is all
    /// there is. Null when the app has no window.
    /// </summary>
    public static WindowCandidate? BestWindow(IEnumerable<WindowCandidate> windowsInZOrder, string bundleId)
    {
        WindowCandidate? minimized = null;
        foreach (var window in windowsInZOrder)
        {
            if (!string.Equals(window.BundleId, bundleId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!window.IsMinimized)
            {
                return window;
            }
            minimized ??= window;
        }
        return minimized;
    }

    /// <summary>
    /// The process whose tree holds all of an app's sound: from <paramref name="start"/> up
    /// through parents with the same executable, so a browser's or a meeting app's helper
    /// processes are included when the app's own sound is recorded. A parent that is a
    /// different program (Explorer that launched it) is where the walk stops.
    /// </summary>
    public static uint RootProcess(uint start, IReadOnlyDictionary<uint, (uint Parent, string Executable)> processes)
    {
        if (!processes.TryGetValue(start, out var current))
        {
            return start;
        }
        var root = start;
        var seen = new HashSet<uint> { start };
        while (processes.TryGetValue(current.Parent, out var parent)
               && string.Equals(parent.Executable, current.Executable, StringComparison.OrdinalIgnoreCase)
               && seen.Add(current.Parent))
        {
            root = current.Parent;
            current = parent;
        }
        return root;
    }

    /// <summary>A process of the app when it has no window: any process with its executable name, the lowest id first.</summary>
    public static uint? AnyProcess(string bundleId, IReadOnlyDictionary<uint, (uint Parent, string Executable)> processes) =>
        processes.Where(pair => string.Equals(pair.Value.Executable, bundleId, StringComparison.OrdinalIgnoreCase))
            .Select(pair => (uint?)pair.Key)
            .OrderBy(id => id)
            .FirstOrDefault();
}
