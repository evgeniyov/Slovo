using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Slovo.Core.Logging;
using Slovo.Platform.Windows.Interop;

namespace Slovo.Platform.Windows.Input;

/// <summary>Sends planned keystrokes through SendInput and reads which modifiers are held.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class KeyboardInjector
{
    static readonly Logger log = Log.For("TextInserter");

    /// <summary>Injects the strokes in one SendInput call; false when Windows took fewer than all of them.</summary>
    public static bool Send(IReadOnlyList<KeyStroke> strokes)
    {
        if (strokes.Count == 0)
        {
            return true;
        }
        var inputs = stackalloc NativeInput[strokes.Count];
        for (var i = 0; i < strokes.Count; i++)
        {
            var stroke = strokes[i];
            // The scan code travels along for apps that read it (games, remote desktops);
            // without KEYEVENTF_SCANCODE Windows still routes by the virtual key.
            var scan = (ushort)User32.MapVirtualKey((uint)stroke.VirtualKey, User32.MAPVK_VK_TO_VSC);
            inputs[i] = KeySequence.KeyInput(stroke.VirtualKey, scan, stroke.Up, stroke.Extended);
        }
        var sent = User32.SendInput((uint)strokes.Count, inputs, sizeof(NativeInput));
        if (sent != strokes.Count)
        {
            log.Error($"SendInput injected {sent} of {strokes.Count} key events: {Marshal.GetLastPInvokeError()}");
            return false;
        }
        return true;
    }

    /// <summary>The modifiers physically or logically down right now.</summary>
    public static IReadOnlyCollection<int> HeldModifiers()
    {
        var held = new List<int>();
        foreach (var code in KeySequence.ReleasableModifiers)
        {
            if ((User32.GetAsyncKeyState(code) & 0x8000) != 0)
            {
                held.Add(code);
            }
        }
        return held;
    }
}
