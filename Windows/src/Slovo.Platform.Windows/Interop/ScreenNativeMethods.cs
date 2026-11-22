using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Slovo.Platform.Windows.Interop;

// Imports for screen recording: windows and monitors, processes, DWM, Direct3D 11 and
// Media Foundation. Same conventions as NativeMethods.cs: BOOL as int, UTF-16 strings,
// callbacks as function pointers.

internal static unsafe partial class User32
{
    public const uint WTS_SESSION_LOCK = 0x7;
    public const int GWL_EXSTYLE = -20;
    public const uint GW_OWNER = 4;
    public const uint WS_EX_APPWINDOW = 0x00040000;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [LibraryImport(Dll)]
    public static partial int EnumWindows(nint lpEnumFunc, nint lParam);

    [LibraryImport(Dll)]
    public static partial int IsWindow(nint hWnd);

    [LibraryImport(Dll)]
    public static partial int IsWindowVisible(nint hWnd);

    [LibraryImport(Dll)]
    public static partial int IsIconic(nint hWnd);

    [LibraryImport(Dll)]
    public static partial nint GetWindow(nint hWnd, uint uCmd);

    [LibraryImport(Dll, EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport(Dll, EntryPoint = "GetWindowTextLengthW")]
    public static partial int GetWindowTextLength(nint hWnd);

    [LibraryImport(Dll, EntryPoint = "GetClassNameW")]
    public static partial int GetClassName(nint hWnd, char* lpClassName, int nMaxCount);

    [LibraryImport(Dll)]
    public static partial int GetWindowRect(nint hWnd, out Rect lpRect);

    [LibraryImport(Dll)]
    public static partial nint MonitorFromPoint(Point pt, uint dwFlags);

    [LibraryImport(Dll, EntryPoint = "GetMonitorInfoW")]
    public static partial int GetMonitorInfo(nint hMonitor, ref MonitorInfo lpmi);
}

internal static unsafe partial class Kernel32
{
    public const uint TH32CS_SNAPPROCESS = 0x2;
    public static readonly nint InvalidHandle = -1;

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [LibraryImport(Dll, EntryPoint = "Process32FirstW", SetLastError = true)]
    public static partial int Process32First(nint hSnapshot, ProcessEntry32* lppe);

    [LibraryImport(Dll, EntryPoint = "Process32NextW", SetLastError = true)]
    public static partial int Process32Next(nint hSnapshot, ProcessEntry32* lppe);
}

[SupportedOSPlatform("windows")]
internal static unsafe partial class Dwmapi
{
    /// <summary>DWMWA_CLOAKED: a window hidden by the shell (another virtual desktop, a suspended Store app).</summary>
    public const uint DWMWA_CLOAKED = 14;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, uint dwAttribute, void* pvAttribute, uint cbAttribute);
}

[SupportedOSPlatform("windows")]
internal static unsafe partial class D3D11
{
    const string Dll = "d3d11.dll";

    public const int D3D_DRIVER_TYPE_HARDWARE = 1;
    public const int D3D_DRIVER_TYPE_WARP = 5;
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    public const uint D3D11_SDK_VERSION = 7;
    /// <summary>DXGI_FORMAT_B8G8R8A8_UNORM, also DirectXPixelFormat.B8G8R8A8UIntNormalized.</summary>
    public const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    public const uint D3D11_USAGE_STAGING = 3;
    public const uint D3D11_CPU_ACCESS_READ = 0x20000;
    public const uint D3D11_MAP_READ = 1;

    [LibraryImport(Dll)]
    public static partial int D3D11CreateDevice(nint pAdapter, int driverType, nint software, uint flags, int* pFeatureLevels,
        uint featureLevels, uint sdkVersion, out nint ppDevice, out int pFeatureLevel, out nint ppImmediateContext);

    [LibraryImport(Dll)]
    public static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}

[SupportedOSPlatform("windows")]
internal static unsafe partial class Mfplat
{
    const string Dll = "mfplat.dll";

    /// <summary>MF_VERSION of the Windows 7 and later SDK: MF_SDK_VERSION 2, MF_API_VERSION 0x70.</summary>
    public const uint MF_VERSION = 0x0002_0070;
    public const uint MFSTARTUP_LITE = 1;

    [LibraryImport(Dll)]
    public static partial int MFStartup(uint version, uint dwFlags);

    [LibraryImport(Dll)]
    public static partial int MFCreateMediaType(out nint ppMFType);

    [LibraryImport(Dll)]
    public static partial int MFCreateSample(out nint ppIMFSample);

    [LibraryImport(Dll)]
    public static partial int MFCreateMemoryBuffer(uint cbMaxLength, out nint ppBuffer);
}

[StructLayout(LayoutKind.Sequential)]
internal struct Point
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MonitorInfo
{
    public uint Size;
    public Rect Monitor;
    public Rect Work;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ProcessEntry32
{
    public uint Size;
    public uint Usage;
    public uint ProcessId;
    public nint DefaultHeapId;
    public uint ModuleId;
    public uint Threads;
    public uint ParentProcessId;
    public int PriorityClassBase;
    public uint Flags;
    public fixed char ExeFile[260];
}

/// <summary>D3D11_TEXTURE2D_DESC.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Texture2DDescription
{
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public uint ArraySize;
    public uint Format;
    public uint SampleCount;
    public uint SampleQuality;
    public uint Usage;
    public uint BindFlags;
    public uint CpuAccessFlags;
    public uint MiscFlags;
}

/// <summary>D3D11_MAPPED_SUBRESOURCE.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MappedSubresource
{
    public void* Data;
    public uint RowPitch;
    public uint DepthPitch;
}

/// <summary>D3D11_BOX.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Box3D
{
    public uint Left;
    public uint Top;
    public uint Front;
    public uint Right;
    public uint Bottom;
    public uint Back;
}

/// <summary>Windows.Graphics.SizeInt32.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SizeInt32
{
    public int Width;
    public int Height;
}

/// <summary>MFT_OUTPUT_DATA_BUFFER.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputDataBuffer
{
    public uint StreamId;
    public nint Sample;
    public uint Status;
    public nint Events;
}

/// <summary>MFT_OUTPUT_STREAM_INFO.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputStreamInfo
{
    public uint Flags;
    public uint Size;
    public uint Alignment;
}
