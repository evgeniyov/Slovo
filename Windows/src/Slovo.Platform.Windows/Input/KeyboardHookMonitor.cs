using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Slovo.Core.Input;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Platform.Windows.Interop;

namespace Slovo.Platform.Windows.Input;

/// <summary>
/// A listen-only <c>WH_KEYBOARD_LL</c> hook that drives <see cref="HotkeyStateMachine"/>
/// (the Windows counterpart of HotkeyMonitor.swift's CGEventTap).
/// </summary>
/// <remarks>
/// <para>
/// Threading: the hook lives on a dedicated thread with its own message loop (a low-level
/// hook is called on the thread that installed it, from inside its GetMessage). The hook
/// callback only copies the event into a queue, posts a message to the same thread and
/// calls <c>CallNextHookEx</c>; it never swallows a key. Windows removes a low-level hook
/// whose callback overruns <c>LowLevelHooksTimeout</c>, so everything else — the state
/// machine, timers, the menu mask — runs after the callback has returned. Triggers and
/// key-state changes reach the UI thread through <see cref="IMainThread"/>.
/// </para>
/// <para>
/// Recovery: Windows removes a timed-out hook without telling anyone. Raw keyboard input
/// (WM_INPUT, which is never removed) serves as the witness: when physical keys arrive as
/// raw input but not through the hook, the hook is installed again. The hook is also
/// reinstalled after resume from sleep and on session unlock, when releases may have been
/// missed, and while the dictation key is believed held, its keys are checked with
/// GetAsyncKeyState so a release the hook never saw (the secure desktop after
/// Ctrl+Alt+Del, an elevated window in front) still ends the hold.
/// </para>
/// <para>
/// Win and Alt: when the dictation key involves them (DictationKeys.NeedsMenuMask), an
/// unassigned key (vkE8) is pressed and released through SendInput as soon as the key is
/// held, so its release is not "lone" and does not open Start or activate a menu bar.
/// Everything Slovo injects carries <see cref="InjectedMarker"/> in dwExtraInfo and is
/// ignored here.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class KeyboardHookMonitor : IHotkeyMonitor, Slovo.Core.Dictation.IProcessingAwareHotkeys, IDisposable
{
    /// <summary>'SLVO' in dwExtraInfo of every key event Slovo injects.</summary>
    public const nuint InjectedMarker = KeySequence.InjectedMarkerValue;

    const uint ProcessKeysMessage = User32.WM_APP + 2;
    const nuint DeadlineTimer = 1;
    const nuint LivenessTimer = 2;
    const nuint HeldKeyTimer = 3;
    const uint LivenessIntervalMilliseconds = 2_000;
    const uint HeldKeyIntervalMilliseconds = 250;
    /// <summary>A key is only called released by GetAsyncKeyState once no event for it came in this long.</summary>
    const double HeldKeyGrace = 0.5;
    const double MinimumReinstallInterval = 5;

    [ThreadStatic] static KeyboardHookMonitor? hookOwner;

    readonly IMainThread mainThread;
    readonly IUptime uptime;
    readonly Logger log = Log.For("Hotkey");
    readonly Lock gate = new();
    readonly ConcurrentQueue<RawKeyEvent> pending = new();

    // Guarded by gate.
    HotkeyStateMachine machine = new(new HotkeyConfiguration());
    MessageWindowThread? thread;
    int generation;

    // Hook thread only.
    nint hookWindow;
    nint hook;
    bool publishedKeyDown;
    int hookEvents;
    int rawKeyboardEvents;
    double lastDictationKeyEvent;
    double lastReinstall;

    // UI thread only.
    bool isRunning;
    bool isKeyDown;

    public KeyboardHookMonitor(IMainThread mainThread, IUptime uptime)
    {
        this.mainThread = mainThread;
        this.uptime = uptime;
    }

    public bool IsRunning => isRunning;
    public bool IsKeyDown => isKeyDown;
    public event Action<DictationTrigger>? Triggered;
    public event Action<bool>? KeyStateChanged;

    public bool Start(HotkeyConfiguration configuration)
    {
        MessageWindowThread started;
        lock (gate)
        {
            machine.Configuration = configuration;
            if (thread is { IsRunning: true })
            {
                started = thread;
            }
            else
            {
                generation++;
                started = new MessageWindowThread("Slovo keyboard hook", messageOnly: false)
                {
                    Handler = HandleMessage,
                };
                started.OnStart = () => OnHookThreadStart(started);
                started.OnStop = OnHookThreadStop;
                thread = started;
                if (!started.Start(TimeSpan.FromSeconds(3)))
                {
                    log.Error("The keyboard hook thread did not start");
                }
            }
        }
        isRunning = started.Invoke(() => hook != 0, TimeSpan.FromSeconds(2), false);
        return isRunning;
    }

    public void Update(HotkeyConfiguration configuration) => Step(m =>
    {
        m.Configuration = configuration;
        return [];
    });

    public void SyncRecording(DictationMode? mode)
    {
        var time = uptime.Now;
        Step(m => m.Handle(new HotkeyInput.RecordingChanged(mode, time)), publishKeyState: false);
    }

    /// <summary>Esc cancels a dictation that is still being transcribed.</summary>
    public void SyncProcessing(bool isProcessing) => Step(m =>
    {
        m.IsProcessing = isProcessing;
        return [];
    }, publishKeyState: false);

    public void Stop()
    {
        MessageWindowThread? stopping;
        lock (gate)
        {
            stopping = thread;
            thread = null;
            generation++;
            machine = new HotkeyStateMachine(machine.Configuration);
        }
        stopping?.Dispose();
        isRunning = false;
        if (isKeyDown)
        {
            isKeyDown = false;
            KeyStateChanged?.Invoke(false);
        }
    }

    public void Dispose() => Stop();

    // Machine steps

    /// <summary>Runs a step of the machine on the hook thread, or right here when the hook is not running.</summary>
    void Step(Func<HotkeyStateMachine, IReadOnlyList<DictationTrigger>> step, bool publishKeyState = true)
    {
        MessageWindowThread? running;
        lock (gate)
        {
            running = thread is { IsRunning: true } ? thread : null;
            if (running is null)
            {
                step(machine);
                return;
            }
        }
        running.Post(() => Deliver(step, publishKeyState));
    }

    /// <summary>On the hook thread: advances the machine, then publishes what changed.</summary>
    void Deliver(Func<HotkeyStateMachine, IReadOnlyList<DictationTrigger>> step, bool publishKeyState = true)
    {
        IReadOnlyList<DictationTrigger> triggers;
        bool keyDown;
        DictationKey key;
        double? deadline;
        int currentGeneration;
        lock (gate)
        {
            triggers = step(machine);
            keyDown = machine.IsKeyDown;
            key = machine.Configuration.Key;
            deadline = machine.NextDeadline;
            currentGeneration = generation;
        }
        var keyChanged = publishKeyState && keyDown != publishedKeyDown;
        if (keyChanged)
        {
            if (HotkeyKeys.ShouldMaskMenu(key, publishedKeyDown, keyDown))
            {
                InjectMenuMask();
            }
            publishedKeyDown = keyDown;
            var window = hookWindow;
            if (window != 0)
            {
                if (keyDown)
                {
                    User32.SetTimer(window, HeldKeyTimer, HeldKeyIntervalMilliseconds, 0);
                }
                else
                {
                    User32.KillTimer(window, HeldKeyTimer);
                }
            }
        }
        ScheduleDeadline(deadline);
        if (!keyChanged && triggers.Count == 0)
        {
            return;
        }
        mainThread.Post(() =>
        {
            if (currentGeneration != Volatile.Read(ref generation))
            {
                return;
            }
            if (keyChanged && isKeyDown != keyDown)
            {
                isKeyDown = keyDown;
                KeyStateChanged?.Invoke(keyDown);
            }
            foreach (var trigger in triggers)
            {
                Triggered?.Invoke(trigger);
            }
        });
    }

    void ScheduleDeadline(double? deadline)
    {
        var window = hookWindow;
        if (window == 0)
        {
            return;
        }
        User32.KillTimer(window, DeadlineTimer);
        if (deadline is not { } time)
        {
            return;
        }
        var delay = Math.Max(0, time - uptime.Now);
        // USER timers are no finer than about 10 ms; a millisecond more makes the deadline
        // pass by the time the timer fires.
        var milliseconds = (uint)Math.Min(int.MaxValue, Math.Ceiling(delay * 1_000) + 1);
        User32.SetTimer(window, DeadlineTimer, Math.Max(10u, milliseconds), 0);
    }

    // Hook thread

    void OnHookThreadStart(MessageWindowThread owner)
    {
        hookOwner = this;
        hookWindow = owner.Window;
        publishedKeyDown = false;
        InstallHook();
        RegisterRawKeyboard(owner.Window, remove: false);
        WtsApi32.WTSRegisterSessionNotification(owner.Window, WtsApi32.NOTIFY_FOR_THIS_SESSION);
        User32.SetTimer(owner.Window, LivenessTimer, LivenessIntervalMilliseconds, 0);
    }

    void OnHookThreadStop()
    {
        UninstallHook();
        RegisterRawKeyboard(0, remove: true);
        hookWindow = 0;
        hookOwner = null;
    }

    void InstallHook()
    {
        if (hook != 0)
        {
            return;
        }
        hook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL,
            (nint)(delegate* unmanaged[Stdcall]<int, nint, nint, nint>)&HookProcedure,
            Kernel32.GetModuleHandle(null), 0);
        if (hook == 0)
        {
            log.Error($"SetWindowsHookEx failed: {Marshal.GetLastPInvokeError()}");
        }
        else
        {
            lastReinstall = uptime.Now;
            log.Info("Dictation key hook installed");
        }
    }

    void UninstallHook()
    {
        if (hook != 0)
        {
            User32.UnhookWindowsHookEx(hook);
            hook = 0;
        }
    }

    /// <summary>Installs the hook again; with <paramref name="releaseKeys"/> the keys are treated as released first.</summary>
    void Reinstall(bool releaseKeys, string reason)
    {
        log.Info($"Reinstalling the dictation key hook: {reason}");
        UninstallHook();
        pending.Clear();
        if (releaseKeys)
        {
            var now = uptime.Now;
            Deliver(m => m.ReleaseKeys(now));
        }
        InstallHook();
    }

    bool HandleMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        switch (message)
        {
            case ProcessKeysMessage:
                ProcessPendingKeys();
                return true;
            case User32.WM_TIMER when (nuint)wParam == DeadlineTimer:
                FireDeadline();
                return true;
            case User32.WM_TIMER when (nuint)wParam == LivenessTimer:
                CheckLiveness();
                return true;
            case User32.WM_TIMER when (nuint)wParam == HeldKeyTimer:
                CheckHeldKeys();
                return true;
            case User32.WM_INPUT:
                CountRawInput(lParam);
                // DefWindowProc must still see WM_INPUT to release the input data.
                return false;
            case User32.WM_POWERBROADCAST when wParam is User32.PBT_APMRESUMEAUTOMATIC or User32.PBT_APMRESUMESUSPEND:
                Reinstall(releaseKeys: true, "resumed from sleep");
                return false;
            case User32.WM_WTSSESSION_CHANGE when wParam is User32.WTS_SESSION_UNLOCK or User32.WTS_CONSOLE_CONNECT or User32.WTS_REMOTE_CONNECT:
                Reinstall(releaseKeys: true, "session became active");
                return false;
            case User32.WM_DESTROY:
                // Registrations tied to the window go before it does; the default handling
                // then ends the loop.
                var window = hookWindow;
                if (window != 0)
                {
                    WtsApi32.WTSUnRegisterSessionNotification(window);
                    User32.KillTimer(window, DeadlineTimer);
                    User32.KillTimer(window, LivenessTimer);
                    User32.KillTimer(window, HeldKeyTimer);
                }
                return false;
            default:
                return false;
        }
    }

    void ProcessPendingKeys()
    {
        var keyCodes = CurrentKeyCodes();
        while (pending.TryDequeue(out var keyEvent))
        {
            if (keyCodes.Contains(keyEvent.VirtualKey))
            {
                lastDictationKeyEvent = keyEvent.Timestamp;
            }
            Deliver(m => m.Handle(new HotkeyInput.Key(keyEvent)));
        }
    }

    void FireDeadline()
    {
        var window = hookWindow;
        if (window != 0)
        {
            User32.KillTimer(window, DeadlineTimer);
        }
        var now = uptime.Now;
        double? deadline;
        lock (gate)
        {
            deadline = machine.NextDeadline;
        }
        if (deadline is { } time && now < time)
        {
            ScheduleDeadline(time);
            return;
        }
        Deliver(m => m.Handle(new HotkeyInput.Deadline(now)));
    }

    void CheckLiveness()
    {
        var now = uptime.Now;
        if (hook == 0)
        {
            InstallHook();
        }
        else if (rawKeyboardEvents >= 2 && hookEvents == 0 && now - lastReinstall > MinimumReinstallInterval)
        {
            // Releases may have been missed too; the held-key check settles those without
            // ending a hold that is really still going.
            Reinstall(releaseKeys: false, "keys arrived as raw input but not through the hook");
        }
        rawKeyboardEvents = 0;
        hookEvents = 0;
    }

    void CheckHeldKeys()
    {
        var now = uptime.Now;
        if (!publishedKeyDown || now - lastDictationKeyEvent < HeldKeyGrace)
        {
            return;
        }
        foreach (var code in CurrentKeyCodes())
        {
            if ((User32.GetAsyncKeyState(code) & 0x8000) == 0)
            {
                log.Info("A dictation key release was missed; treating the key as released");
                var released = new RawKeyEvent(RawKeyEventKind.KeyUp, code, now);
                Deliver(m => m.Handle(new HotkeyInput.Key(released)));
            }
        }
    }

    IReadOnlyList<int> CurrentKeyCodes()
    {
        lock (gate)
        {
            return machine.Configuration.Key.KeyCodes();
        }
    }

    void CountRawInput(nint rawInput)
    {
        RawInputHeader header;
        var size = (uint)sizeof(RawInputHeader);
        var copied = User32.GetRawInputData(rawInput, User32.RID_HEADER, &header, ref size, (uint)sizeof(RawInputHeader));
        // Injected input has no device; only physical keys are expected to reach the hook too.
        if (copied == (uint)sizeof(RawInputHeader) && header.Type == User32.RIM_TYPEKEYBOARD && header.Device != 0)
        {
            rawKeyboardEvents++;
        }
    }

    void RegisterRawKeyboard(nint window, bool remove)
    {
        var device = new RawInputDevice
        {
            UsagePage = 0x01,
            Usage = 0x06,
            Flags = remove ? User32.RIDEV_REMOVE : User32.RIDEV_INPUTSINK,
            Target = remove ? 0 : window,
        };
        if (User32.RegisterRawInputDevices(&device, 1, (uint)sizeof(RawInputDevice)) == 0 && !remove)
        {
            log.Warning($"Raw keyboard input is unavailable ({Marshal.GetLastPInvokeError()}); a removed hook is only noticed after sleep or unlock");
        }
    }

    /// <summary>
    /// Presses and releases vkE8 from a pool thread. Never from the hook thread itself: the
    /// system calls this very hook for the injected keys, and a hook thread busy in
    /// SendInput cannot answer it.
    /// </summary>
    void InjectMenuMask() => ThreadPool.UnsafeQueueUserWorkItem(static logger =>
    {
        var inputs = stackalloc NativeInput[2];
        inputs[0] = KeySequence.KeyInput(HotkeyKeys.MenuMaskKey, 0, keyUp: false, extended: false);
        inputs[1] = KeySequence.KeyInput(HotkeyKeys.MenuMaskKey, 0, keyUp: true, extended: false);
        if (User32.SendInput(2, inputs, sizeof(NativeInput)) != 2)
        {
            logger.Warning($"Could not inject the menu mask key: {Marshal.GetLastPInvokeError()}");
        }
    }, log, preferLocal: false);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint HookProcedure(int code, nint wParam, nint lParam)
    {
        var owner = hookOwner;
        try
        {
            if (code == User32.HC_ACTION && owner is not null && lParam != 0)
            {
                var data = (KeyboardHookData*)lParam;
                var ours = (data->Flags & User32.LLKHF_INJECTED) != 0 && data->ExtraInfo == InjectedMarker;
                if (!ours)
                {
                    owner.hookEvents++;
                    var up = (data->Flags & User32.LLKHF_UP) != 0;
                    var virtualKey = HotkeyKeys.Normalize((int)data->VirtualKey, (int)data->ScanCode,
                        (data->Flags & User32.LLKHF_EXTENDED) != 0);
                    owner.pending.Enqueue(new RawKeyEvent(up ? RawKeyEventKind.KeyUp : RawKeyEventKind.KeyDown,
                        virtualKey, owner.uptime.Now));
                    var window = owner.hookWindow;
                    if (window != 0)
                    {
                        User32.PostMessage(window, ProcessKeysMessage, 0, 0);
                    }
                }
            }
        }
        catch
        {
            // Never let an exception reach the system's input thread; the key still passes on.
        }
        return User32.CallNextHookEx(owner?.hook ?? 0, code, wParam, lParam);
    }
}
