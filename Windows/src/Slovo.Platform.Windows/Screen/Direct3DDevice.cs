using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Slovo.Core.Platform;
using Slovo.Platform.Windows.Interop;

namespace Slovo.Platform.Windows.Screen;

/// <summary>
/// A Direct3D 11 device for Windows.Graphics.Capture, its WinRT face (<c>IDirect3DDevice</c>),
/// and the copy of a captured texture into memory the CPU can read.
/// </summary>
/// <remarks>
/// Slots (d3d11.h): ID3D11Device.CreateTexture2D 5; ID3D11DeviceContext.Map 14, Unmap 15,
/// CopySubresourceRegion 46; ID3D11Texture2D.GetDesc 10. One device per capture, used from the
/// thread that made it; a device lost to a driver reset makes the next read throw, which ends
/// the capture and lets it reconnect on a new device.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class Direct3DDevice : IDisposable
{
    static readonly Guid IidDxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    static readonly Guid IidMultithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");
    /// <summary>Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice.</summary>
    static readonly Guid IidWinRTDevice = new("A37624AB-8D5F-4650-9D3E-9EAE3D9BC670");
    /// <summary>ID3D11Multithread: Enter 3, Leave 4, SetMultithreadProtected 5.</summary>
    const int SetMultithreadProtectedSlot = 5;

    const int CreateTexture2DSlot = 5;
    const int MapSlot = 14;
    const int UnmapSlot = 15;
    const int CopySubresourceRegionSlot = 46;
    const int GetDescSlot = 10;

    nint device;
    nint context;
    nint winrtDevice;
    nint staging;
    int stagingWidth;
    int stagingHeight;

    Direct3DDevice(nint device, nint context, nint winrtDevice)
    {
        this.device = device;
        this.context = context;
        this.winrtDevice = winrtDevice;
    }

    /// <summary>The <c>Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice</c> a frame pool is made with.</summary>
    public nint WinRTDevice => winrtDevice;

    /// <summary>A hardware device, or the WARP software rasterizer where there is none (a remote session, a basic display driver).</summary>
    public static Direct3DDevice Create()
    {
        var hr = D3D11.D3D11CreateDevice(0, D3D11.D3D_DRIVER_TYPE_HARDWARE, 0, D3D11.D3D11_CREATE_DEVICE_BGRA_SUPPORT, null, 0,
            D3D11.D3D11_SDK_VERSION, out var device, out _, out var context);
        if (hr < 0)
        {
            hr = D3D11.D3D11CreateDevice(0, D3D11.D3D_DRIVER_TYPE_WARP, 0, D3D11.D3D11_CREATE_DEVICE_BGRA_SUPPORT, null, 0,
                D3D11.D3D11_SDK_VERSION, out device, out _, out context);
        }
        Com.Check(hr, "D3D11CreateDevice");
        nint dxgi = 0;
        try
        {
            // Windows.Graphics.Capture works with the device from threads of its own; a
            // multithread-protected context keeps those calls and ours from overlapping.
            var multithread = Com.TryQuery(context, IidMultithread);
            if (multithread != 0)
            {
                ((delegate* unmanaged[Stdcall]<nint, int, int>)Com.VTable(multithread)[SetMultithreadProtectedSlot])(multithread, 1);
                Com.Release(multithread);
            }
            dxgi = Com.Query(device, IidDxgiDevice, "IDXGIDevice");
            Com.Check(D3D11.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable), "CreateDirect3D11DeviceFromDXGIDevice");
            try
            {
                // The frame pool takes the IDirect3DDevice interface itself, not the IInspectable returned.
                return new Direct3DDevice(device, context, Com.Query(inspectable, IidWinRTDevice, "IDirect3DDevice"));
            }
            finally
            {
                Com.Release(inspectable);
            }
        }
        catch
        {
            Com.Release(context);
            Com.Release(device);
            throw;
        }
        finally
        {
            Com.Release(dxgi);
        }
    }

    /// <summary>
    /// Copies the top-left <paramref name="width"/> × <paramref name="height"/> of a BGRA texture
    /// into a staging texture, maps it and hands its rows to <paramref name="handler"/>.
    /// </summary>
    public void Read(nint texture, int width, int height, BgraFrameHandler handler)
    {
        var description = default(Texture2DDescription);
        ((delegate* unmanaged[Stdcall]<nint, Texture2DDescription*, void>)Com.VTable(texture)[GetDescSlot])(texture, &description);
        width = Math.Min(width, (int)description.Width);
        height = Math.Min(height, (int)description.Height);
        if (width <= 0 || height <= 0)
        {
            return;
        }
        EnsureStaging(width, height, description.Format);
        var box = new Box3D { Left = 0, Top = 0, Front = 0, Right = (uint)width, Bottom = (uint)height, Back = 1 };
        var vtable = Com.VTable(context);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, Box3D*, void>)vtable[CopySubresourceRegionSlot])(
            context, staging, 0, 0, 0, 0, texture, 0, &box);
        var mapped = default(MappedSubresource);
        Com.Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedSubresource*, int>)vtable[MapSlot])(
            context, staging, 0, D3D11.D3D11_MAP_READ, 0, &mapped), "ID3D11DeviceContext.Map");
        try
        {
            var stride = (int)mapped.RowPitch;
            var length = stride * (height - 1) + width * 4;
            handler(new ReadOnlySpan<byte>(mapped.Data, length), width, height, stride);
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)vtable[UnmapSlot])(context, staging, 0);
        }
    }

    void EnsureStaging(int width, int height, uint format)
    {
        if (staging != 0 && stagingWidth == width && stagingHeight == height)
        {
            return;
        }
        Com.Release(ref staging);
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format == 0 ? D3D11.DXGI_FORMAT_B8G8R8A8_UNORM : format,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = D3D11.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CpuAccessFlags = D3D11.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0,
        };
        nint texture = 0;
        Com.Check(((delegate* unmanaged[Stdcall]<nint, Texture2DDescription*, void*, nint*, int>)Com.VTable(device)[CreateTexture2DSlot])(
            device, &description, null, &texture), "ID3D11Device.CreateTexture2D");
        staging = texture;
        stagingWidth = width;
        stagingHeight = height;
    }

    public void Dispose()
    {
        Com.Release(ref staging);
        Com.Release(ref winrtDevice);
        Com.Release(ref context);
        Com.Release(ref device);
    }
}
