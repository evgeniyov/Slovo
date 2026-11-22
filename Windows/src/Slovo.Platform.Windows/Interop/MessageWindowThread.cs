using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Slovo.Core.Logging;

namespace Slovo.Platform.Windows.Interop;

/// <summary>
/// A thread of its own with a hidden window and a <c>GetMessage</c> loop: what low-level
/// hooks, <c>RegisterHotKey</c>, WinEvent hooks, timers and the clipboard owner need.
/// Work is posted to it as delegates; window messages go to <see cref="Handler"/>.
/// </summary>
/// <remarks>
/// A message-only window (HWND_MESSAGE) is enough for posted messages, hot keys and raw
/// input. Broadcasts such as WM_POWERBROADCAST reach only top-level windows, so the
/// keyboard hook uses a hidden top-level one instead.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class MessageWindowThread : IDisposable
{
    /// <summary>Handles one message on the thread; returns true when it was handled, with its result.</summary>
    public delegate bool MessageHandler(uint message, nint wParam, nint lParam, out nint result);

    const uint RunQueuedWork = User32.WM_APP + 1;
    const string ClassName = "SlovoMessageWindow";
    static readonly Lock ClassGate = new();
    static bool classRegistered;

    [ThreadStatic] static MessageWindowThread? current;

    readonly string name;
    readonly bool messageOnly;
    readonly ApartmentState apartment;
    readonly ConcurrentQueue<Action> work = new();
    readonly ManualResetEventSlim started = new();
    readonly Logger log;
    Thread? thread;
    volatile bool stopped;

    public MessageWindowThread(string name, bool messageOnly = true, ApartmentState apartment = ApartmentState.MTA)
    {
        this.name = name;
        this.messageOnly = messageOnly;
        this.apartment = apartment;
        log = Log.For(name);
    }

    /// <summary>Window messages other than the posted work; runs on the thread. Set before <see cref="Start"/>.</summary>
    public MessageHandler? Handler { get; set; }

    /// <summary>Runs on the thread once the window exists, before the loop. Set before <see cref="Start"/>.</summary>
    public Action? OnStart { get; set; }

    /// <summary>Runs on the thread after the loop ends. Set before <see cref="Start"/>.</summary>
    public Action? OnStop { get; set; }

    public nint Window { get; private set; }

    public bool IsCurrentThread => current == this;

    public bool IsRunning => Window != 0 && !stopped;

    /// <summary>Starts the thread and waits for its window. False when the window could not be created.</summary>
    public bool Start(TimeSpan timeout)
    {
        if (thread is not null)
        {
            return IsRunning;
        }
        thread = new Thread(Run) { IsBackground = true, Name = name };
        thread.SetApartmentState(apartment);
        thread.Start();
        started.Wait(timeout);
        return IsRunning;
    }

    /// <summary>Runs an action on the thread, after whatever was posted before it.</summary>
    public bool Post(Action action)
    {
        if (!IsRunning)
        {
            return false;
        }
        work.Enqueue(action);
        return User32.PostMessage(Window, RunQueuedWork, 0, 0) != 0;
    }

    /// <summary>Runs a function on the thread and waits for it; the fallback when it did not finish in time.</summary>
    public T Invoke<T>(Func<T> function, TimeSpan timeout, T fallback)
    {
        if (IsCurrentThread)
        {
            return function();
        }
        var result = fallback;
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        var posted = Post(() =>
        {
            try
            {
                result = function();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                done.Set();
            }
        });
        if (!posted || !done.Wait(timeout))
        {
            return fallback;
        }
        if (failure is not null)
        {
            ExceptionDispatchInfoThrow(failure);
        }
        return result;
    }

    static void ExceptionDispatchInfoThrow(Exception exception) =>
        global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();

    /// <summary>Closes the window, ends the loop and waits briefly for the thread.</summary>
    public void Dispose()
    {
        if (thread is null || stopped)
        {
            return;
        }
        var window = Window;
        if (window != 0)
        {
            work.Enqueue(() => User32.DestroyWindow(window));
            User32.PostMessage(window, RunQueuedWork, 0, 0);
        }
        if (!IsCurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
        started.Dispose();
    }

    void Run()
    {
        current = this;
        try
        {
            if (!RegisterClass())
            {
                return;
            }
            Window = User32.CreateWindowEx(messageOnly ? 0 : User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE,
                ClassName, name, messageOnly ? 0 : User32.WS_POPUP, 0, 0, 0, 0,
                messageOnly ? User32.HWND_MESSAGE : 0, 0, Kernel32.GetModuleHandle(null), 0);
            if (Window == 0)
            {
                log.Error($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
                return;
            }
            try
            {
                OnStart?.Invoke();
            }
            catch (Exception exception)
            {
                log.Error("Starting the message thread failed", exception);
            }
            started.Set();
            while (true)
            {
                var result = User32.GetMessage(out var message, 0, 0, 0);
                if (result == 0)
                {
                    break;
                }
                if (result == -1)
                {
                    log.Error($"GetMessage failed: {Marshal.GetLastPInvokeError()}");
                    break;
                }
                User32.DispatchMessage(in message);
            }
        }
        finally
        {
            stopped = true;
            try
            {
                OnStop?.Invoke();
            }
            catch (Exception exception)
            {
                log.Error("Stopping the message thread failed", exception);
            }
            Window = 0;
            try
            {
                started.Set();
            }
            catch (ObjectDisposedException)
            {
            }
            current = null;
        }
    }

    void RunQueued()
    {
        while (work.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                log.Error("Work on the message thread failed", exception);
            }
        }
    }

    static bool RegisterClass()
    {
        lock (ClassGate)
        {
            if (classRegistered)
            {
                return true;
            }
            var windowClass = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProcedure,
                Instance = Kernel32.GetModuleHandle(null),
                // Registered once and never unregistered, so the name lives as long as the process.
                ClassName = Marshal.StringToHGlobalUni(ClassName),
            };
            if (User32.RegisterClassEx(in windowClass) == 0)
            {
                Log.For("MessageWindow").Error($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}");
                return false;
            }
            classRegistered = true;
            return true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            var self = current;
            if (self is not null && self.Window == window)
            {
                if (message == RunQueuedWork)
                {
                    self.RunQueued();
                    return 0;
                }
                if (self.Handler is { } handler && handler(message, wParam, lParam, out var result))
                {
                    return result;
                }
                if (message == User32.WM_DESTROY)
                {
                    User32.PostQuitMessage(0);
                    return 0;
                }
            }
            else if (self is not null && message == User32.WM_DESTROY)
            {
                User32.PostQuitMessage(0);
                return 0;
            }
        }
        catch (Exception exception)
        {
            // An exception must never cross back into user32.
            Log.For("MessageWindow").Error("Window procedure failed", exception);
        }
        return User32.DefWindowProc(window, message, wParam, lParam);
    }
}
