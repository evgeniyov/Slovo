using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Platform.Windows.Interop;

namespace Slovo.Platform.Windows.Input;

/// <summary>Maps shortcut modifiers to RegisterHotKey's MOD_ flags.</summary>
public static class ShortcutModifiers
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Win = 0x0008;
    /// <summary>A held shortcut fires once, not at the keyboard's repeat rate.</summary>
    public const uint NoRepeat = 0x4000;

    public static uint ToWin32(HotkeyModifiers modifiers)
    {
        var result = NoRepeat;
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) result |= Alt;
        if (modifiers.HasFlag(HotkeyModifiers.Control)) result |= Control;
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) result |= Shift;
        if (modifiers.HasFlag(HotkeyModifiers.Win)) result |= Win;
        return result;
    }
}

/// <summary>
/// System-wide shortcuts through RegisterHotKey (PasteLastShortcut.swift's Carbon hot key),
/// which needs no permission. Hot keys belong to a message-only window on a thread of its
/// own; WM_HOTKEY arrives there and the action is posted to the UI thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGlobalShortcuts : IGlobalShortcuts, IDisposable
{
    readonly IMainThread mainThread;
    readonly MessageWindowThread thread = new("Slovo shortcuts");
    readonly Dictionary<int, Action> actions = [];
    readonly Logger log = Log.For("Shortcuts");
    /// <summary>Hot key ids are unique per process so a stale WM_HOTKEY never runs a newer action (0x0000–0xBFFF).</summary>
    int nextId = 1;

    public WindowsGlobalShortcuts(IMainThread mainThread)
    {
        this.mainThread = mainThread;
        thread.Handler = HandleMessage;
        if (!thread.Start(TimeSpan.FromSeconds(3)))
        {
            log.Error("The shortcut thread did not start");
        }
    }

    public IDisposable? Register(HotkeyShortcut shortcut, Action action)
    {
        var id = thread.Invoke(() => RegisterOnThread(shortcut, action), TimeSpan.FromSeconds(2), 0);
        return id == 0 ? null : new Registration(this, id);
    }

    public void Dispose()
    {
        thread.Invoke(() =>
        {
            foreach (var id in actions.Keys)
            {
                User32.UnregisterHotKey(thread.Window, id);
            }
            actions.Clear();
            return true;
        }, TimeSpan.FromSeconds(2), false);
        thread.Dispose();
    }

    int RegisterOnThread(HotkeyShortcut shortcut, Action action)
    {
        var id = nextId;
        nextId = nextId >= 0xBFFF ? 1 : nextId + 1;
        if (User32.RegisterHotKey(thread.Window, id, ShortcutModifiers.ToWin32(shortcut.Modifiers), (uint)shortcut.KeyCode) == 0)
        {
            log.Info($"RegisterHotKey({shortcut.DisplayText}) was refused: {Marshal.GetLastPInvokeError()}");
            return 0;
        }
        actions[id] = action;
        return id;
    }

    void Unregister(int id) => thread.Post(() =>
    {
        if (actions.Remove(id))
        {
            User32.UnregisterHotKey(thread.Window, id);
        }
    });

    bool HandleMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (message != User32.WM_HOTKEY)
        {
            return false;
        }
        if (actions.TryGetValue((int)wParam, out var action))
        {
            mainThread.Post(action);
        }
        return true;
    }

    sealed class Registration(WindowsGlobalShortcuts owner, int id) : IDisposable
    {
        int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Unregister(id);
            }
        }
    }
}
