using System.Text.Json.Serialization;

namespace Slovo.Core.Model;

/// <summary>
/// The key held for push-to-talk and double-tapped for hands-free dictation on Windows.
/// Windows keyboards have no Fn key the system can see, so the choices differ from the
/// macOS app's; the behaviour (hold, double tap, Esc) is the same.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DictationKey>))]
public enum DictationKey
{
    /// <summary>Left Ctrl and Left Win held together, the Wispr Flow default on Windows.</summary>
    [JsonStringEnumMemberName("ctrlWin")] CtrlWin,
    /// <summary>Right Ctrl alone.</summary>
    [JsonStringEnumMemberName("rightCtrl")] RightCtrl,
    /// <summary>Right Alt alone (AltGr on some layouts).</summary>
    [JsonStringEnumMemberName("rightAlt")] RightAlt,
    /// <summary>Left Ctrl and Left Alt held together.</summary>
    [JsonStringEnumMemberName("leftCtrlAlt")] LeftCtrlAlt,
}

/// <summary>Windows virtual-key codes Slovo refers to.</summary>
public static class VirtualKey
{
    public const int Escape = 0x1B;
    public const int LeftWin = 0x5B;
    public const int RightWin = 0x5C;
    public const int LeftShift = 0xA0;
    public const int RightShift = 0xA1;
    public const int LeftCtrl = 0xA2;
    public const int RightCtrl = 0xA3;
    public const int LeftAlt = 0xA4;
    public const int RightAlt = 0xA5;
    public const int V = 0x56;
    public const int M = 0x4D;
    public const int U = 0x55;

    public static bool IsModifier(int vk) => vk is LeftWin or RightWin or LeftShift or RightShift
        or LeftCtrl or RightCtrl or LeftAlt or RightAlt or 0x10 or 0x11 or 0x12;
}

public static class DictationKeys
{
    public static readonly IReadOnlyList<DictationKey> All = [DictationKey.CtrlWin, DictationKey.RightCtrl, DictationKey.RightAlt, DictationKey.LeftCtrlAlt];

    /// <summary>Virtual-key codes that must all be down for the key to count as held.</summary>
    public static IReadOnlyList<int> KeyCodes(this DictationKey key) => key switch
    {
        DictationKey.CtrlWin => [VirtualKey.LeftCtrl, VirtualKey.LeftWin],
        DictationKey.RightCtrl => [VirtualKey.RightCtrl],
        DictationKey.RightAlt => [VirtualKey.RightAlt],
        DictationKey.LeftCtrlAlt => [VirtualKey.LeftCtrl, VirtualKey.LeftAlt],
        _ => [VirtualKey.RightCtrl],
    };

    /// <summary>Keycap label shown in the interface; the same in every language.</summary>
    public static string Symbol(this DictationKey key) => key switch
    {
        DictationKey.CtrlWin => "Ctrl + Win",
        DictationKey.RightCtrl => "Right Ctrl",
        DictationKey.RightAlt => "Right Alt",
        DictationKey.LeftCtrlAlt => "Ctrl + Alt",
        _ => key.ToString(),
    };

    /// <summary>
    /// Keys whose lone release does something in Windows (Win opens Start, Alt focuses the
    /// menu bar). The keyboard hook masks them with an unassigned key while dictating.
    /// </summary>
    public static bool NeedsMenuMask(this DictationKey key) => key is DictationKey.CtrlWin or DictationKey.RightAlt or DictationKey.LeftCtrlAlt;
}

/// <summary>Modifier set of a global shortcut. Bit values match the macOS app's where the keys match.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    /// <summary>The Windows key; the macOS app stores Command in this bit.</summary>
    Win = 1 << 0,
    Control = 1 << 1,
    /// <summary>Alt; the macOS app stores Option in this bit.</summary>
    Alt = 1 << 2,
    Shift = 1 << 3,
}

/// <summary>A virtual key plus modifiers, such as Ctrl+Alt+V for "Paste last transcript".</summary>
public sealed record HotkeyShortcut(
    [property: JsonPropertyName("keyCode")] int KeyCode,
    [property: JsonPropertyName("modifiers")] HotkeyModifiers Modifiers)
{
    public static readonly HotkeyShortcut PasteLastDefault = new(VirtualKey.V, HotkeyModifiers.Control | HotkeyModifiers.Alt);

    /// <summary>Ctrl+Alt+M: mark an important moment while a meeting records.</summary>
    public static readonly HotkeyShortcut MarkMomentDefault = new(VirtualKey.M, HotkeyModifiers.Control | HotkeyModifiers.Alt);

    public static readonly IReadOnlyList<HotkeyShortcut> MarkMomentChoices =
    [
        MarkMomentDefault,
        new(VirtualKey.M, HotkeyModifiers.Control | HotkeyModifiers.Shift),
        new(VirtualKey.M, HotkeyModifiers.Control | HotkeyModifiers.Win),
    ];

    /// <summary>Ctrl+Alt+U: switch the microphone while a meeting records.</summary>
    public static readonly HotkeyShortcut MicrophoneToggleDefault = new(VirtualKey.U, HotkeyModifiers.Control | HotkeyModifiers.Alt);

    public static readonly IReadOnlyList<HotkeyShortcut> MicrophoneToggleChoices =
    [
        MicrophoneToggleDefault,
        new(VirtualKey.U, HotkeyModifiers.Control | HotkeyModifiers.Shift),
        new(VirtualKey.U, HotkeyModifiers.Control | HotkeyModifiers.Win),
    ];

    public static readonly IReadOnlyList<HotkeyShortcut> PasteLastChoices =
    [
        PasteLastDefault,
        new(VirtualKey.V, HotkeyModifiers.Control | HotkeyModifiers.Shift),
        new(VirtualKey.V, HotkeyModifiers.Control | HotkeyModifiers.Win),
    ];

    /// <summary>"Ctrl+Alt+V".</summary>
    public string DisplayText
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            parts.Add(KeyCode is >= 0x30 and <= 0x5A ? ((char)KeyCode).ToString() : $"0x{KeyCode:X2}");
            return string.Join('+', parts);
        }
    }
}

/// <summary>How the hotkey state machine interprets the dictation key.</summary>
public sealed record HotkeyConfiguration(
    DictationKey Key = DictationKey.CtrlWin,
    bool HandsFreeEnabled = true,
    bool EscapeCancels = true,
    double MinimumHold = 0.25,
    double DoubleTapWindow = 0.4);
