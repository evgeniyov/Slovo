using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace Slovo.App.Native;

/// <summary>Win32 window styles Avalonia does not expose.</summary>
static partial class WindowStyles
{
    const int GwlExStyle = -20;
    const long WsExNoActivate = 0x08000000;
    const long WsExToolWindow = 0x00000080;
    const long WsExTopmost = 0x00000008;
    const long WsExTransparent = 0x00000020;
    const long WsExAppWindow = 0x00040000;

    /// <summary>
    /// Makes a window an overlay that never takes focus and stays off Alt+Tab
    /// (the NSPanel with .nonactivatingPanel on macOS). No-op elsewhere.
    /// </summary>
    public static void MakeNonActivatingOverlay(Window window)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero)
        {
            return;
        }
        SetOverlay(handle);
    }

    [SupportedOSPlatform("windows")]
    static void SetOverlay(IntPtr handle)
    {
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style |= WsExNoActivate | WsExToolWindow | WsExTopmost;
        style &= ~WsExAppWindow;
        style &= ~WsExTransparent;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [SupportedOSPlatform("windows")]
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
