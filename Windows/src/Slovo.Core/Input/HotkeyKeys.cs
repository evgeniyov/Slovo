using Slovo.Core.Model;

namespace Slovo.Core.Input;

/// <summary>
/// Key identity for the Windows keyboard hook, kept apart from the Win32 calls so it can
/// be tested: telling left from right, the codes that are not keys, and when the Win or
/// Alt key needs masking.
/// </summary>
public static class HotkeyKeys
{
    /// <summary>
    /// An unassigned virtual key (vkE8). Pressing and releasing it while Win or Alt is
    /// held makes their release no longer "lone", so Windows does not open Start or put
    /// the focus on the menu bar; AutoHotkey's MenuMaskKey does the same.
    /// </summary>
    public const int MenuMaskKey = 0xE8;

    /// <summary>What some keyboards send for Fn, and the reserved code Windows uses for "no key".</summary>
    public const int ReservedKey = 0xFF;

    const int GenericShift = 0x10;
    const int GenericControl = 0x11;
    const int GenericAlt = 0x12;
    /// <summary>The scan code of the right Shift key; both Shifts are non-extended keys.</summary>
    const int RightShiftScanCode = 0x36;

    /// <summary>
    /// Turns a key the hook reported with a side-independent code (VK_SHIFT, VK_CONTROL,
    /// VK_MENU, as injected input sometimes does) into its left or right code. Right
    /// Ctrl and Right Alt carry the extended-key flag; the Shifts differ in scan code.
    /// Every other code is returned unchanged.
    /// </summary>
    public static int Normalize(int virtualKey, int scanCode, bool extended) => virtualKey switch
    {
        GenericControl => extended ? VirtualKey.RightCtrl : VirtualKey.LeftCtrl,
        GenericAlt => extended ? VirtualKey.RightAlt : VirtualKey.LeftAlt,
        GenericShift => (scanCode & 0xFF) == RightShiftScanCode ? VirtualKey.RightShift : VirtualKey.LeftShift,
        _ => virtualKey,
    };

    /// <summary>
    /// Codes that stand for no real key: mask keys injected by tools such as AutoHotkey and
    /// the Fn code of some keyboards. A key down with one of them is never a shortcut.
    /// </summary>
    public static bool IsNonKey(int virtualKey) => virtualKey is MenuMaskKey or ReservedKey or 0;

    /// <summary>
    /// Whether to inject the mask key now: once, when a dictation key that involves Win or
    /// Alt becomes fully held. Its release is then not a lone Win or Alt release.
    /// </summary>
    public static bool ShouldMaskMenu(DictationKey key, bool wasDown, bool isDown) =>
        !wasDown && isDown && key.NeedsMenuMask();
}
