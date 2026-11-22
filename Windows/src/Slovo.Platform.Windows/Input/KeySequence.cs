using Slovo.Core.Input;
using Slovo.Core.Model;
using Slovo.Platform.Windows.Interop;

namespace Slovo.Platform.Windows.Input;

/// <summary>One synthesized key transition.</summary>
public readonly record struct KeyStroke(int VirtualKey, bool Up, bool Extended);

/// <summary>
/// The keystrokes Slovo injects, planned apart from SendInput so the plan can be tested:
/// the Ctrl+V chord that pastes, with the modifiers the user still holds released first.
/// </summary>
public static class KeySequence
{
    /// <summary>Modifiers a paste releases first, in this order: Win and Alt would turn Ctrl+V into Win+V (clipboard history) or Ctrl+Alt+V, Shift into "paste special".</summary>
    public static readonly IReadOnlyList<int> ReleasableModifiers =
    [
        VirtualKey.LeftWin, VirtualKey.RightWin,
        VirtualKey.LeftAlt, VirtualKey.RightAlt,
        VirtualKey.LeftShift, VirtualKey.RightShift,
        VirtualKey.LeftCtrl, VirtualKey.RightCtrl,
    ];

    /// <summary>
    /// Keys whose scan code carries the E0 prefix. SendInput needs KEYEVENTF_EXTENDEDKEY for
    /// them, or a released Right Ctrl reads as Left Ctrl.
    /// </summary>
    public static bool IsExtended(int virtualKey) => virtualKey is VirtualKey.RightCtrl or VirtualKey.RightAlt
        or VirtualKey.LeftWin or VirtualKey.RightWin
        // Insert, Delete, Home, End, Page Up/Down and the arrows outside the numeric pad.
        or >= 0x21 and <= 0x28 or 0x2D or 0x2E;

    /// <summary>
    /// Ctrl+V as two halves around a short hold (some apps — Electron, remote desktops —
    /// miss a chord shorter than about 60 ms). Modifiers the user still holds are released
    /// first; when Win or Alt is among them, the mask key goes first so their release does
    /// not open Start or a menu bar. V is the virtual key 0x56 on every layout, Cyrillic
    /// ones included, which is what applications match Ctrl+V against.
    /// </summary>
    public static (IReadOnlyList<KeyStroke> Press, IReadOnlyList<KeyStroke> Release) PasteChord(IReadOnlyCollection<int> heldModifiers)
    {
        var press = new List<KeyStroke>();
        var held = ReleasableModifiers.Where(heldModifiers.Contains).ToList();
        if (held.Any(code => code is VirtualKey.LeftWin or VirtualKey.RightWin or VirtualKey.LeftAlt or VirtualKey.RightAlt))
        {
            press.Add(new KeyStroke(HotkeyKeys.MenuMaskKey, Up: false, Extended: false));
            press.Add(new KeyStroke(HotkeyKeys.MenuMaskKey, Up: true, Extended: false));
        }
        foreach (var code in held)
        {
            press.Add(new KeyStroke(code, Up: true, IsExtended(code)));
        }
        press.Add(new KeyStroke(VirtualKey.LeftCtrl, Up: false, Extended: false));
        press.Add(new KeyStroke(VirtualKey.V, Up: false, Extended: false));
        IReadOnlyList<KeyStroke> release =
        [
            new KeyStroke(VirtualKey.V, Up: true, Extended: false),
            new KeyStroke(VirtualKey.LeftCtrl, Up: true, Extended: false),
        ];
        return (press, release);
    }

    /// <summary>A keyboard INPUT for SendInput, marked so Slovo's own hook ignores it.</summary>
    internal static NativeInput KeyInput(int virtualKey, ushort scanCode, bool keyUp, bool extended)
    {
        var input = new NativeInput { Type = 1 /* INPUT_KEYBOARD */ };
        input.Data.Keyboard = new KeyboardInput
        {
            VirtualKey = (ushort)virtualKey,
            ScanCode = scanCode,
            Flags = (keyUp ? 0x0002u /* KEYEVENTF_KEYUP */ : 0) | (extended ? 0x0001u /* KEYEVENTF_EXTENDEDKEY */ : 0),
            ExtraInfo = InjectedMarkerValue,
        };
        return input;
    }

    /// <summary>'SLVO' in dwExtraInfo (<c>KeyboardHookMonitor.InjectedMarker</c>).</summary>
    internal const nuint InjectedMarkerValue = 0x534C_564F;
}
