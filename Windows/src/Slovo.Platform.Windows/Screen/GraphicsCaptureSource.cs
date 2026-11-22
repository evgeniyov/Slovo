using System.Runtime.Versioning;
using Slovo.Core.Logging;
using Slovo.Core.Platform;
using Slovo.Platform.Windows.Interop;

namespace Slovo.Platform.Windows.Screen;

/// <summary>
/// One Windows.Graphics.Capture session of a monitor or a window, through the raw WinRT ABI
/// (no projection assembly): a capture item from <c>IGraphicsCaptureItemInterop</c>, a
/// free-threaded frame pool that is polled rather than listened to, and a session.
/// </summary>
/// <remarks>
/// <para>
/// Interface ids and slots (windows.graphics.capture.h; WinRT methods start at 6):
/// IGraphicsCaptureItemInterop (IUnknown) CreateForWindow 3, CreateForMonitor 4;
/// IGraphicsCaptureItem Size 7; IDirect3D11CaptureFramePoolStatics2 CreateFreeThreaded 6;
/// IDirect3D11CaptureFramePool Recreate 6, TryGetNextFrame 7, CreateCaptureSession 10;
/// IDirect3D11CaptureFrame Surface 6, ContentSize 8; IGraphicsCaptureSession StartCapture 6;
/// IGraphicsCaptureSession2 put_IsCursorCaptureEnabled 7; IGraphicsCaptureSession3
/// put_IsBorderRequired 7; IGraphicsCaptureSession5 put_MinUpdateInterval 7;
/// IGraphicsCaptureSession6 put_IncludeSecondaryWindows 7; IClosable Close 6;
/// IDirect3DDxgiInterfaceAccess (IUnknown) GetInterface 3.
/// </para>
/// <para>
/// The yellow border Windows draws around what is captured is turned off where Windows lets a
/// desktop app do so (borderless access requested once, then IsBorderRequired, Windows 11);
/// elsewhere it stays, which is harmless: it is not part of the recorded picture. The cursor is
/// recorded, as on macOS. GraphicsCaptureAccess.RequestAccessAsync is IGraphicsCaptureAccessStatics
/// slot 6; its IAsyncInfo.Status is slot 7.
/// </para>
/// <para>
/// Pictures are taken off the pool by a timer every 40 ms and only the newest is kept, so the
/// pool always has a free buffer: a pool without one drops the next change, and a screen that
/// then stays still would keep showing the picture before it.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class GraphicsCaptureSource : IDisposable
{
    const string ItemClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
    const string FramePoolClass = "Windows.Graphics.Capture.Direct3D11CaptureFramePool";
    const string SessionClass = "Windows.Graphics.Capture.GraphicsCaptureSession";

    static readonly Guid IidItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    static readonly Guid IidItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid IidFramePoolStatics2 = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    static readonly Guid IidSessionStatics = new("2224A540-5974-49AA-B232-0882536F4CB5");
    static readonly Guid IidSession2 = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    static readonly Guid IidSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
    static readonly Guid IidSession5 = new("67C0EA62-1F85-5061-925A-239BE0AC09CB");
    static readonly Guid IidSession6 = new("D7419236-BE20-5E9F-BCD6-C4E98FD6AFDC");
    static readonly Guid IidClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    static readonly Guid IidDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    static readonly Guid IidTexture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    const int CreateForWindowSlot = 3;
    const int CreateForMonitorSlot = 4;
    const int ItemSizeSlot = Com.InspectableSlots + 1;
    const int CreateFreeThreadedSlot = Com.InspectableSlots;
    const int RecreateSlot = Com.InspectableSlots;
    const int TryGetNextFrameSlot = Com.InspectableSlots + 1;
    const int CreateCaptureSessionSlot = Com.InspectableSlots + 4;
    const int FrameSurfaceSlot = Com.InspectableSlots;
    const int FrameContentSizeSlot = Com.InspectableSlots + 2;
    const int StartCaptureSlot = Com.InspectableSlots;
    const int PutPropertySlot = Com.InspectableSlots + 1;
    const int IsSupportedSlot = Com.InspectableSlots;
    const int CloseSlot = Com.InspectableSlots;
    const int GetInterfaceSlot = 3;

    /// <summary>
    /// Three buffers: the newest picture held until it is read, and two for Windows to compose
    /// into meanwhile. A pool with no free buffer drops what changes next — and when nothing
    /// changes after that, the dropped picture would be the one missing for good.
    /// </summary>
    const int BufferCount = 3;
    /// <summary>How often the pool is emptied into the one picture kept, well within its buffers.</summary>
    const int PumpMilliseconds = 40;
    /// <summary>TimeSpan ticks between pictures Windows needs to compose (IGraphicsCaptureSession5).</summary>
    const long MinimumUpdateInterval = 2_500_000;

    const string AccessClass = "Windows.Graphics.Capture.GraphicsCaptureAccess";
    static readonly Guid IidAccessStatics = new("743ED370-06EC-5040-A58A-901F0F757095");
    static readonly Guid IidAsyncInfo = new("00000036-0000-0000-C000-000000000046");
    /// <summary>IAsyncInfo: Id 6, Status 7.</summary>
    const int AsyncStatusSlot = Com.InspectableSlots + 1;
    const int RequestAccessSlot = Com.InspectableSlots;
    /// <summary>GraphicsCaptureAccessKind.Borderless.</summary>
    const int BorderlessAccess = 0;
    /// <summary>AsyncStatus.Started: anything else is an answer.</summary>
    const int AsyncStarted = 0;
    static int borderlessRequested;

    static readonly Logger Logger = Log.For("ScreenCapture");

    readonly Direct3DDevice device;
    readonly Lock frameGate = new();
    nint item;
    nint pool;
    nint session;
    SizeInt32 poolSize;
    /// <summary>The newest picture from the pool, under <see cref="frameGate"/>; closed when a newer one comes.</summary>
    nint heldFrame;
    Timer? pump;
    bool disposed;
    string? pumpError;

    GraphicsCaptureSource(Direct3DDevice device, nint item)
    {
        this.device = device;
        this.item = item;
        RequestBorderlessAccess();
        try
        {
            SizeInt32 size;
            Com.Check(((delegate* unmanaged[Stdcall]<nint, SizeInt32*, int>)Com.VTable(item)[ItemSizeSlot])(item, &size), "GraphicsCaptureItem.Size");
            poolSize = new SizeInt32 { Width = Math.Max(1, size.Width), Height = Math.Max(1, size.Height) };
            var statics = WinRT.ActivationFactory(FramePoolClass, IidFramePoolStatics2);
            try
            {
                nint created = 0;
                Com.Check(((delegate* unmanaged[Stdcall]<nint, nint, int, int, SizeInt32, nint*, int>)Com.VTable(statics)[CreateFreeThreadedSlot])(
                    statics, device.WinRTDevice, (int)D3D11.DXGI_FORMAT_B8G8R8A8_UNORM, BufferCount, poolSize, &created),
                    "Direct3D11CaptureFramePool.CreateFreeThreaded");
                pool = created;
            }
            finally
            {
                Com.Release(statics);
            }
            nint newSession = 0;
            Com.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Com.VTable(pool)[CreateCaptureSessionSlot])(pool, item, &newSession),
                "Direct3D11CaptureFramePool.CreateCaptureSession");
            session = newSession;
            // Optional refinements of later Windows versions; a system without them captures anyway.
            SetBool(IidSession2, true, "IsCursorCaptureEnabled");
            SetBool(IidSession3, false, "IsBorderRequired");
            SetBool(IidSession6, true, "IncludeSecondaryWindows");
            SetTimeSpan(IidSession5, MinimumUpdateInterval, "MinUpdateInterval");
            Com.Check(((delegate* unmanaged[Stdcall]<nint, int>)Com.VTable(session)[StartCaptureSlot])(session), "GraphicsCaptureSession.StartCapture");
            // The pool is free-threaded, so a timer thread may take its pictures as they come.
            pump = new Timer(_ => Pump(), null, PumpMilliseconds, PumpMilliseconds);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Asks Windows once per run for capture without the yellow border (GraphicsCaptureAccess,
    /// Windows 11), the documented step before IsBorderRequired may be turned off. A desktop app
    /// is usually allowed without a prompt; the answer is waited for briefly and not needed,
    /// since the border only shows on screen and never in the recording.
    /// </summary>
    static void RequestBorderlessAccess()
    {
        if (Interlocked.Exchange(ref borderlessRequested, 1) != 0)
        {
            return;
        }
        nint statics = 0;
        nint operation = 0;
        nint info = 0;
        try
        {
            statics = WinRT.ActivationFactory(AccessClass, IidAccessStatics);
            if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Com.VTable(statics)[RequestAccessSlot])(statics, BorderlessAccess, &operation) < 0
                || operation == 0)
            {
                return;
            }
            info = Com.TryQuery(operation, IidAsyncInfo);
            var status = AsyncStarted;
            for (var waited = 0; info != 0 && status == AsyncStarted && waited < 2_000; waited += 50)
            {
                if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Com.VTable(info)[AsyncStatusSlot])(info, &status) < 0)
                {
                    break;
                }
                if (status == AsyncStarted)
                {
                    Thread.Sleep(50);
                }
            }
            Logger.Info($"Borderless capture requested (status {status})");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Windows 10 has no such request; the border stays there.
            Logger.Info($"Borderless capture cannot be requested here: {error.Message}");
        }
        finally
        {
            Com.Release(info);
            Com.Release(operation);
            Com.Release(statics);
        }
    }

    /// <summary>Empties the pool into <see cref="heldFrame"/>, so Windows always has buffers to compose into.</summary>
    void Pump()
    {
        lock (frameGate)
        {
            if (disposed || pool == 0)
            {
                return;
            }
            try
            {
                while (true)
                {
                    nint frame = 0;
                    Com.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Com.VTable(pool)[TryGetNextFrameSlot])(pool, &frame),
                        "Direct3D11CaptureFramePool.TryGetNextFrame");
                    if (frame == 0)
                    {
                        return;
                    }
                    CloseAndRelease(ref heldFrame);
                    heldFrame = frame;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                pumpError ??= error.Message;
            }
        }
    }

    /// <summary>Whether this Windows can capture at all (GraphicsCaptureSession.IsSupported, Windows 10 1903 and later).</summary>
    public static bool IsSupported()
    {
        try
        {
            var statics = WinRT.ActivationFactory(SessionClass, IidSessionStatics);
            try
            {
                byte supported = 0;
                return ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Com.VTable(statics)[IsSupportedSlot])(statics, &supported) >= 0 && supported != 0;
            }
            finally
            {
                Com.Release(statics);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Logger.Warning($"Windows.Graphics.Capture is not available: {error.Message}");
            return false;
        }
    }

    public static GraphicsCaptureSource ForMonitor(Direct3DDevice device, nint monitor) =>
        new(device, CreateItem(CreateForMonitorSlot, monitor, "CreateForMonitor"));

    public static GraphicsCaptureSource ForWindow(Direct3DDevice device, nint window) =>
        new(device, CreateItem(CreateForWindowSlot, window, "CreateForWindow"));

    static nint CreateItem(int slot, nint handle, string what)
    {
        var interop = WinRT.ActivationFactory(ItemClass, IidItemInterop);
        try
        {
            nint created = 0;
            var iid = IidItem;
            Com.Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Com.VTable(interop)[slot])(interop, handle, &iid, &created),
                "IGraphicsCaptureItemInterop." + what);
            return created;
        }
        finally
        {
            Com.Release(interop);
        }
    }

    /// <summary>
    /// Hands the newest picture since the last call to <paramref name="handler"/>; older ones
    /// were let go as newer ones came. Unchanged when Windows composed nothing new. Throws when
    /// the pool failed (the device was reset), which ends the capture.
    /// </summary>
    public ScreenFrameResult Read(BgraFrameHandler handler)
    {
        Pump();
        nint latest;
        lock (frameGate)
        {
            if (pumpError is { } failure)
            {
                throw new InvalidOperationException(failure);
            }
            latest = heldFrame;
            heldFrame = 0;
        }
        if (latest == 0)
        {
            return ScreenFrameResult.Unchanged;
        }
        nint surface = 0;
        nint access = 0;
        nint texture = 0;
        try
        {
            SizeInt32 content;
            var frameTable = Com.VTable(latest);
            Com.Check(((delegate* unmanaged[Stdcall]<nint, SizeInt32*, int>)frameTable[FrameContentSizeSlot])(latest, &content),
                "Direct3D11CaptureFrame.ContentSize");
            Com.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)frameTable[FrameSurfaceSlot])(latest, &surface),
                "Direct3D11CaptureFrame.Surface");
            access = Com.Query(surface, IidDxgiInterfaceAccess, "IDirect3DDxgiInterfaceAccess");
            var iid = IidTexture2D;
            Com.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Com.VTable(access)[GetInterfaceSlot])(access, &iid, &texture),
                "IDirect3DDxgiInterfaceAccess.GetInterface");
            device.Read(texture, Math.Min(content.Width, poolSize.Width), Math.Min(content.Height, poolSize.Height), handler);
            if (content.Width > 0 && content.Height > 0 && (content.Width != poolSize.Width || content.Height != poolSize.Height))
            {
                // The window was resized: later pictures need buffers of its new size.
                lock (frameGate)
                {
                    Com.Check(((delegate* unmanaged[Stdcall]<nint, nint, int, int, SizeInt32, int>)Com.VTable(pool)[RecreateSlot])(
                        pool, device.WinRTDevice, (int)D3D11.DXGI_FORMAT_B8G8R8A8_UNORM, BufferCount, content), "Direct3D11CaptureFramePool.Recreate");
                    poolSize = content;
                }
            }
            return ScreenFrameResult.Frame;
        }
        finally
        {
            Com.Release(texture);
            Com.Release(access);
            Com.Release(surface);
            CloseAndRelease(ref latest);
        }
    }

    public void Dispose()
    {
        Timer? timer;
        lock (frameGate)
        {
            // A pump running now holds the lock; none runs once this is set.
            disposed = true;
            timer = pump;
            pump = null;
            CloseAndRelease(ref heldFrame);
        }
        timer?.Dispose();
        CloseAndRelease(ref session);
        CloseAndRelease(ref pool);
        Com.Release(ref item);
    }

    void SetBool(in Guid iid, bool value, string what)
    {
        var face = Com.TryQuery(session, iid);
        if (face == 0)
        {
            return;
        }
        try
        {
            var hr = ((delegate* unmanaged[Stdcall]<nint, byte, int>)Com.VTable(face)[PutPropertySlot])(face, value ? (byte)1 : (byte)0);
            if (hr < 0)
            {
                Logger.Info($"GraphicsCaptureSession.{what} was refused (0x{hr:X8})");
            }
        }
        finally
        {
            Com.Release(face);
        }
    }

    void SetTimeSpan(in Guid iid, long ticks, string what)
    {
        var face = Com.TryQuery(session, iid);
        if (face == 0)
        {
            return;
        }
        try
        {
            var hr = ((delegate* unmanaged[Stdcall]<nint, long, int>)Com.VTable(face)[PutPropertySlot])(face, ticks);
            if (hr < 0)
            {
                Logger.Info($"GraphicsCaptureSession.{what} was refused (0x{hr:X8})");
            }
        }
        finally
        {
            Com.Release(face);
        }
    }

    /// <summary>Closes a WinRT object (IClosable) so its resources go back at once, then releases it.</summary>
    static void CloseAndRelease(ref nint instance)
    {
        var held = instance;
        instance = 0;
        if (held == 0)
        {
            return;
        }
        var closable = Com.TryQuery(held, IidClosable);
        if (closable != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, int>)Com.VTable(closable)[CloseSlot])(closable);
            Com.Release(closable);
        }
        Com.Release(held);
    }
}
