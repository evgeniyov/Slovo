using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Slovo.Platform.Windows.Interop;

// Win32 imports. Every BOOL is returned as int (non-zero is TRUE) so the signatures stay
// blittable; callbacks are passed as nint holding an [UnmanagedCallersOnly] function
// pointer. Strings are UTF-16 (the W functions).

[SupportedOSPlatform("windows")]
internal static unsafe partial class User32
{
    const string Dll = "user32.dll";

    public const int WH_KEYBOARD_LL = 13;
    public const int HC_ACTION = 0;

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_INPUT = 0x00FF;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_POWERBROADCAST = 0x0218;
    public const uint WM_WTSSESSION_CHANGE = 0x02B1;
    public const uint WM_APP = 0x8000;

    public const nint PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const nint PBT_APMRESUMESUSPEND = 0x0007;
    public const nint WTS_SESSION_UNLOCK = 0x8;
    public const nint WTS_CONSOLE_CONNECT = 0x1;
    public const nint WTS_REMOTE_CONNECT = 0x3;

    public const uint LLKHF_EXTENDED = 0x01;
    public const uint LLKHF_LOWER_IL_INJECTED = 0x02;
    public const uint LLKHF_INJECTED = 0x10;
    public const uint LLKHF_UP = 0x80;

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint MAPVK_VK_TO_VSC = 0;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const uint RID_HEADER = 0x10000005;
    public const uint RIM_TYPEKEYBOARD = 1;
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RIDEV_REMOVE = 0x00000001;

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public static readonly nint HWND_MESSAGE = -3;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const int UOI_NAME = 2;
    public const uint DESKTOP_READOBJECTS = 0x0001;

    [LibraryImport(Dll, EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static partial nint SetWindowsHookEx(int idHook, nint lpfn, nint hmod, uint dwThreadId);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial int UnhookWindowsHookEx(nint hhk);

    [LibraryImport(Dll)]
    public static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport(Dll, EntryPoint = "GetMessageW", SetLastError = true)]
    public static partial int GetMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport(Dll, EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(in Msg lpMsg);

    [LibraryImport(Dll, EntryPoint = "PostMessageW", SetLastError = true)]
    public static partial int PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport(Dll)]
    public static partial void PostQuitMessage(int nExitCode);

    [LibraryImport(Dll, EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(in WndClassEx lpwcx);

    [LibraryImport(Dll, EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial int DestroyWindow(nint hWnd);

    [LibraryImport(Dll, EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nuint SetTimer(nint hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);

    [LibraryImport(Dll)]
    public static partial int KillTimer(nint hWnd, nuint uIDEvent);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial uint SendInput(uint cInputs, NativeInput* pInputs, int cbSize);

    [LibraryImport(Dll)]
    public static partial short GetAsyncKeyState(int vKey);

    [LibraryImport(Dll, EntryPoint = "MapVirtualKeyW")]
    public static partial uint MapVirtualKey(uint uCode, uint uMapType);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial int RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport(Dll)]
    public static partial int UnregisterHotKey(nint hWnd, int id);

    [LibraryImport(Dll)]
    public static partial nint GetForegroundWindow();

    [LibraryImport(Dll)]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport(Dll)]
    public static partial int EnumChildWindows(nint hWndParent, nint lpEnumFunc, nint lParam);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint OpenInputDesktop(uint dwFlags, int fInherit, uint dwDesiredAccess);

    [LibraryImport(Dll)]
    public static partial int CloseDesktop(nint hDesktop);

    [LibraryImport(Dll, EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    public static partial int GetUserObjectInformation(nint hObj, int nIndex, void* pvInfo, uint nLength, out uint lpnLengthNeeded);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial int OpenClipboard(nint hWndNewOwner);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial int CloseClipboard();

    [LibraryImport(Dll, SetLastError = true)]
    public static partial int EmptyClipboard();

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint GetClipboardData(uint uFormat);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint SetClipboardData(uint uFormat, nint hMem);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial uint EnumClipboardFormats(uint format);

    [LibraryImport(Dll, EntryPoint = "RegisterClipboardFormatW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormat(string lpszFormat);

    [LibraryImport(Dll)]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport(Dll, SetLastError = true)]
    public static partial int RegisterRawInputDevices(RawInputDevice* pRawInputDevices, uint uiNumDevices, uint cbSize);

    [LibraryImport(Dll)]
    public static partial uint GetRawInputData(nint hRawInput, uint uiCommand, void* pData, ref uint pcbSize, uint cbSizeHeader);

    [LibraryImport(Dll)]
    public static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, nint pfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [LibraryImport(Dll)]
    public static partial int UnhookWinEvent(nint hWinEventHook);
}

[SupportedOSPlatform("windows")]
internal static unsafe partial class Kernel32
{
    const string Dll = "kernel32.dll";

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint GMEM_MOVEABLE = 0x0002;
    public const int ERROR_ACCESS_DENIED = 5;

    [LibraryImport(Dll, EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? lpModuleName);

    [LibraryImport(Dll)]
    public static partial uint GetCurrentThreadId();

    [LibraryImport(Dll)]
    public static partial nint GetCurrentProcess();

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint OpenProcess(uint dwDesiredAccess, int bInheritHandle, uint dwProcessId);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial int CloseHandle(nint hObject);

    [LibraryImport(Dll, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    public static partial int QueryFullProcessImageName(nint hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial void* GlobalLock(nint hMem);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial int GlobalUnlock(nint hMem);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nuint GlobalSize(nint hMem);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint GlobalFree(nint hMem);
}

[SupportedOSPlatform("windows")]
internal static unsafe partial class Advapi32
{
    const string Dll = "advapi32.dll";

    public const uint TOKEN_QUERY = 0x0008;
    /// <summary>TOKEN_INFORMATION_CLASS.TokenIntegrityLevel.</summary>
    public const int TokenIntegrityLevel = 25;

    [LibraryImport(Dll, SetLastError = true)]
    public static partial int OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial int GetTokenInformation(nint tokenHandle, int tokenInformationClass, void* tokenInformation,
        uint tokenInformationLength, out uint returnLength);

    [LibraryImport(Dll)]
    public static partial byte* GetSidSubAuthorityCount(nint pSid);

    [LibraryImport(Dll)]
    public static partial uint* GetSidSubAuthority(nint pSid, uint nSubAuthority);
}

[SupportedOSPlatform("windows")]
internal static unsafe partial class Ole32
{
    public const uint COINIT_MULTITHREADED = 0x0;
    public const uint COINIT_APARTMENTTHREADED = 0x2;
    public const uint CLSCTX_INPROC_SERVER = 0x1;

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    [LibraryImport("oleaut32.dll")]
    public static partial int VariantClear(Variant* pvarg);
}

[SupportedOSPlatform("windows")]
internal static partial class WtsApi32
{
    public const uint NOTIFY_FOR_THIS_SESSION = 0;

    [LibraryImport("wtsapi32.dll")]
    public static partial int WTSRegisterSessionNotification(nint hWnd, uint dwFlags);

    [LibraryImport("wtsapi32.dll")]
    public static partial int WTSUnRegisterSessionNotification(nint hWnd);
}
