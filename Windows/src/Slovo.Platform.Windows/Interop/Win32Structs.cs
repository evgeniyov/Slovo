using System.Runtime.InteropServices;

namespace Slovo.Platform.Windows.Interop;

// Win32 structures, laid out field for field as in the Windows SDK headers. Sizes are
// for 64-bit processes (x64 and ARM64 share the layout rules); the unit tests check them
// on the build machine, which is 64-bit as well. Pointer-sized members are nint/nuint,
// so the sequential layout also stays right on a 32-bit process.

/// <summary>MSG (winuser.h): 48 bytes on 64-bit.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    public nint Hwnd;
    public uint Message;
    public nint WParam;
    public nint LParam;
    public uint Time;
    public int PointX;
    public int PointY;
    public uint Private;
}

/// <summary>WNDCLASSEXW (winuser.h): 80 bytes on 64-bit.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WndClassEx
{
    public uint Size;
    public uint Style;
    public nint WndProc;
    public int ClassExtra;
    public int WindowExtra;
    public nint Instance;
    public nint Icon;
    public nint Cursor;
    public nint Background;
    public nint MenuName;
    public nint ClassName;
    public nint SmallIcon;
}

/// <summary>KBDLLHOOKSTRUCT (winuser.h): 24 bytes on 64-bit, dwExtraInfo at offset 16.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardHookData
{
    public uint VirtualKey;
    public uint ScanCode;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

/// <summary>MOUSEINPUT (winuser.h): 32 bytes on 64-bit. Only here so the INPUT union has its real size.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MouseInput
{
    public int Dx;
    public int Dy;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

/// <summary>KEYBDINPUT (winuser.h): 24 bytes on 64-bit.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardInput
{
    public ushort VirtualKey;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

/// <summary>HARDWAREINPUT (winuser.h): 8 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct HardwareInput
{
    public uint Message;
    public ushort ParamLow;
    public ushort ParamHigh;
}

/// <summary>The union inside INPUT; as large and as aligned as MOUSEINPUT.</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct InputUnion
{
    [FieldOffset(0)] public MouseInput Mouse;
    [FieldOffset(0)] public KeyboardInput Keyboard;
    [FieldOffset(0)] public HardwareInput Hardware;
}

/// <summary>INPUT (winuser.h): 40 bytes on 64-bit, the union at offset 8. SendInput checks cbSize against it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeInput
{
    public uint Type;
    public InputUnion Data;
}

/// <summary>RAWINPUTDEVICE (winuser.h): 16 bytes on 64-bit.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RawInputDevice
{
    public ushort UsagePage;
    public ushort Usage;
    public uint Flags;
    public nint Target;
}

/// <summary>RAWINPUTHEADER (winuser.h): 24 bytes on 64-bit.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RawInputHeader
{
    public uint Type;
    public uint Size;
    public nint Device;
    public nint WParam;
}

/// <summary>
/// VARIANT (oaidl.h): the type tag, three reserved words, then a union as large as two
/// pointers (BRECORD) — 24 bytes on 64-bit, the value at offset 8.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Variant
{
    public ushort Type;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public nint Value;
    public nint Value2;

    public const ushort Empty = 0;
    public const ushort Int32 = 3;
    public const ushort String = 8;
    public const ushort Boolean = 11;
    public const ushort UInt32 = 19;
    public const ushort Unknown = 13;

    /// <summary>VT_BOOL holds a VARIANT_BOOL (a 16-bit −1 or 0) in the low bytes of the union.</summary>
    public readonly bool? AsBoolean => Type == Boolean ? (short)(Value & 0xFFFF) != 0 : null;

    /// <summary>VT_I4 holds a 32-bit integer in the low bytes of the union.</summary>
    public readonly int? AsInt32 => Type == Int32 ? unchecked((int)(Value & 0xFFFF_FFFF)) : null;
}
