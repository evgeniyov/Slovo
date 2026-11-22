using System.Runtime.Versioning;
using Slovo.Core.Localization;
using Slovo.Core.Logging;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Platform.Windows.Interop;

namespace Slovo.Platform.Windows.Screen;

/// <summary>
/// Screen recording on Windows (the platform half of MeetingScreenCapture.swift): pictures of
/// the primary display or of one app's window through Windows.Graphics.Capture, H.264 through
/// Media Foundation, and the session's lock, unlock and wake notifications.
/// </summary>
/// <remarks>
/// Needs Windows 10 version 1903 or later. No permission is asked: Windows lets a desktop app
/// capture the screen, and shows that it does with a border where it cannot be turned off.
/// Slovo's own windows (the Flow bar) are recorded like any other, as on macOS.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsScreenCapture : IScreenCapture, IDisposable
{
    static readonly Logger Logger = Log.For("ScreenCapture");

    readonly Lazy<string?> unavailableReason = new(Evaluate);
    readonly Lock gate = new();
    Action<ScreenSessionEvent>? sessionChanged;
    MessageWindowThread? sessionWatcher;

    public string? UnavailableReason => unavailableReason.Value;

    public (int Width, int Height)? MainDisplaySize()
    {
        var monitor = PrimaryMonitor();
        if (monitor == 0)
        {
            return null;
        }
        var info = new MonitorInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if (User32.GetMonitorInfo(monitor, ref info) == 0 || info.Monitor.Width <= 0 || info.Monitor.Height <= 0)
        {
            return null;
        }
        // Slovo is per-monitor DPI aware, so these are the display's real pixels.
        return (info.Monitor.Width, info.Monitor.Height);
    }

    public IReadOnlyList<AppIdentity> CapturableApps() => CaptureTargets.Apps(CaptureWindows.Enumerate());

    public IScreenCaptureStream Open(ScreenCaptureTarget target) => WindowsScreenCaptureStream.Open(target);

    public IVideoEncoder CreateEncoder(int width, int height) => MediaFoundationH264Encoder.Create(width, height);

    /// <summary>Watched by a hidden window of its own while anyone listens: session notifications and power broadcasts reach only top-level windows.</summary>
    public event Action<ScreenSessionEvent>? SessionChanged
    {
        add
        {
            lock (gate)
            {
                sessionChanged += value;
                if (sessionWatcher is null && sessionChanged is not null)
                {
                    StartWatching();
                }
            }
        }
        remove
        {
            MessageWindowThread? stopping = null;
            lock (gate)
            {
                sessionChanged -= value;
                if (sessionChanged is null)
                {
                    stopping = sessionWatcher;
                    sessionWatcher = null;
                }
            }
            stopping?.Dispose();
        }
    }

    public void Dispose()
    {
        MessageWindowThread? stopping;
        lock (gate)
        {
            stopping = sessionWatcher;
            sessionWatcher = null;
            sessionChanged = null;
        }
        stopping?.Dispose();
    }

    internal static nint PrimaryMonitor() => User32.MonitorFromPoint(new Point { X = 0, Y = 0 }, User32.MONITOR_DEFAULTTOPRIMARY);

    static string? Evaluate()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) || !GraphicsCaptureSource.IsSupported())
        {
            return CoreStrings.Get("meetings.error.screenRecordingUnsupported");
        }
        return null;
    }

    void StartWatching()
    {
        var watcher = new MessageWindowThread("Slovo screen session", messageOnly: false);
        watcher.OnStart = () => WtsApi32.WTSRegisterSessionNotification(watcher.Window, WtsApi32.NOTIFY_FOR_THIS_SESSION);
        watcher.Handler = (uint message, nint wParam, nint lParam, out nint result) =>
        {
            result = 0;
            switch (message)
            {
                case User32.WM_WTSSESSION_CHANGE when wParam == (nint)User32.WTS_SESSION_LOCK:
                    Raise(ScreenSessionEvent.Locked);
                    return false;
                case User32.WM_WTSSESSION_CHANGE when wParam == User32.WTS_SESSION_UNLOCK:
                    Raise(ScreenSessionEvent.Unlocked);
                    return false;
                case User32.WM_POWERBROADCAST when wParam == User32.PBT_APMRESUMEAUTOMATIC:
                    Raise(ScreenSessionEvent.Resumed);
                    return false;
                case User32.WM_DESTROY:
                    WtsApi32.WTSUnRegisterSessionNotification(watcher.Window);
                    return false;
                default:
                    return false;
            }
        };
        if (!watcher.Start(TimeSpan.FromSeconds(3)))
        {
            Logger.Error("The screen lock watcher did not start; a locked screen will not pause the recording");
        }
        sessionWatcher = watcher;
    }

    void Raise(ScreenSessionEvent change)
    {
        Action<ScreenSessionEvent>? handlers;
        lock (gate)
        {
            handlers = sessionChanged;
        }
        Logger.Info($"Screen session: {change}");
        handlers?.Invoke(change);
    }
}

/// <summary>
/// One open capture: the primary display, or the frontmost window of an app, followed from
/// window to window as the app brings another one forward (a meeting window over its main
/// window), which is the nearest Windows comes to ScreenCaptureKit's capture of an app's
/// windows. Used from the thread that opened it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsScreenCaptureStream : IScreenCaptureStream
{
    /// <summary>How often the target is looked at again: the primary display, the app's frontmost window.</summary>
    const long CheckMilliseconds = 1_000;
    /// <summary>Another window of the app has to stay in front for this many checks before the recording follows it.</summary>
    const int ChecksBeforeSwitching = 2;

    static readonly Logger Logger = Log.For("ScreenCapture");

    readonly ScreenCaptureTarget target;
    readonly Direct3DDevice device;
    GraphicsCaptureSource? source;
    nint monitor;
    nint window;
    nint candidate;
    int candidateChecks;
    long nextCheck;

    WindowsScreenCaptureStream(ScreenCaptureTarget target, Direct3DDevice device)
    {
        this.target = target;
        this.device = device;
    }

    public string? LostReason { get; private set; }

    public static WindowsScreenCaptureStream Open(ScreenCaptureTarget target)
    {
        WinRT.EnsureInitialized();
        var stream = new WindowsScreenCaptureStream(target, Direct3DDevice.Create());
        try
        {
            switch (target)
            {
                case ScreenCaptureTarget.Application app:
                    var best = CaptureTargets.BestWindow(CaptureWindows.Enumerate(), app.BundleId)
                        ?? throw MeetingErrors.ScreenRecordingAppNotRunning();
                    stream.window = best.Handle;
                    stream.source = GraphicsCaptureSource.ForWindow(stream.device, best.Handle);
                    break;
                default:
                    stream.monitor = WindowsScreenCapture.PrimaryMonitor();
                    if (stream.monitor == 0)
                    {
                        throw MeetingErrors.ScreenRecordingUnavailable();
                    }
                    stream.source = GraphicsCaptureSource.ForMonitor(stream.device, stream.monitor);
                    break;
            }
            stream.nextCheck = Environment.TickCount64 + CheckMilliseconds;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public ScreenFrameResult ReadFrame(BgraFrameHandler handler)
    {
        if (LostReason is not null || source is null)
        {
            return ScreenFrameResult.Lost;
        }
        var now = Environment.TickCount64;
        if (now >= nextCheck)
        {
            nextCheck = now + CheckMilliseconds;
            if (!CheckTarget())
            {
                return ScreenFrameResult.Lost;
            }
        }
        return source.Read(handler);
    }

    /// <summary>False, with <see cref="LostReason"/> set, once the target is gone.</summary>
    bool CheckTarget()
    {
        if (target is not ScreenCaptureTarget.Application app)
        {
            if (WindowsScreenCapture.PrimaryMonitor() != monitor)
            {
                LostReason = "the primary display changed";
                return false;
            }
            return true;
        }
        var best = CaptureTargets.BestWindow(CaptureWindows.Enumerate(), app.BundleId);
        if (best is null)
        {
            LostReason = "the app has no window left";
            return false;
        }
        if (best.Handle == window)
        {
            candidate = 0;
            candidateChecks = 0;
            return true;
        }
        var currentUsable = CaptureWindows.IsUsable(window);
        if (candidate != best.Handle)
        {
            candidate = best.Handle;
            candidateChecks = 0;
        }
        candidateChecks++;
        if (currentUsable && candidateChecks < ChecksBeforeSwitching)
        {
            return true;
        }
        try
        {
            source?.Dispose();
            source = null;
            source = GraphicsCaptureSource.ForWindow(device, best.Handle);
            window = best.Handle;
            candidate = 0;
            candidateChecks = 0;
            Logger.Info("Following the app to another of its windows");
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            LostReason = error.Message;
            return false;
        }
    }

    public void Dispose()
    {
        source?.Dispose();
        source = null;
        device.Dispose();
    }
}
