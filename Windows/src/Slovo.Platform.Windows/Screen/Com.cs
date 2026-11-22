using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Slovo.Platform.Windows.Screen;

/// <summary>
/// Raw COM calls through vtables, the way <c>UiaFocusedInput</c> talks to UI Automation: no
/// interop layer, no projection assembly. Slot numbers come from the SDK headers (and the
/// Windows metadata the <c>windows</c> crate is generated from); every interface starts with
/// IUnknown's QueryInterface (0), AddRef (1) and Release (2), and a WinRT interface continues
/// with IInspectable's GetIids (3), GetRuntimeClassName (4) and GetTrustLevel (5), so its own
/// methods start at 6.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class Com
{
    public const int QueryInterfaceSlot = 0;
    public const int ReleaseSlot = 2;
    /// <summary>The first slot of a WinRT interface's own methods.</summary>
    public const int InspectableSlots = 6;

    public const int S_OK = 0;
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);

    public static nint* VTable(nint instance) => *(nint**)instance;

    public static void Release(nint instance)
    {
        if (instance != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)VTable(instance)[ReleaseSlot])(instance);
        }
    }

    /// <summary>Releases and clears the pointer.</summary>
    public static void Release(ref nint instance)
    {
        var held = instance;
        instance = 0;
        Release(held);
    }

    /// <summary>The interface, or 0 when the object does not have it.</summary>
    public static nint TryQuery(nint instance, in Guid iid)
    {
        if (instance == 0)
        {
            return 0;
        }
        nint result = 0;
        var id = iid;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)VTable(instance)[QueryInterfaceSlot])(instance, &id, &result);
        return hr < 0 ? 0 : result;
    }

    public static nint Query(nint instance, in Guid iid, string what)
    {
        var result = TryQuery(instance, iid);
        if (result == 0)
        {
            throw new COMException($"{what}: the interface is not available", E_NOINTERFACE);
        }
        return result;
    }

    /// <summary>Throws a COMException naming the call when <paramref name="hr"/> is a failure.</summary>
    public static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new COMException(what, hr);
        }
    }
}

/// <summary>An HSTRING and the WinRT activation calls that take one (combase.dll).</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WinRT
{
    const string Combase = "combase.dll";
    /// <summary>RO_INIT_MULTITHREADED.</summary>
    const int MultiThreaded = 1;
    /// <summary>RPC_E_CHANGED_MODE: the thread is already a single-threaded apartment, which works too.</summary>
    const int ChangedMode = unchecked((int)0x80010106);

    [ThreadStatic] static bool initialized;

    [LibraryImport(Combase)]
    private static partial int RoInitialize(int initType);

    [LibraryImport(Combase)]
    private static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [LibraryImport(Combase, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string sourceString, uint length, out nint hstring);

    [LibraryImport(Combase)]
    private static partial int WindowsDeleteString(nint hstring);

    /// <summary>Makes the calling thread part of the multithreaded apartment once; later calls do nothing.</summary>
    public static void EnsureInitialized()
    {
        if (initialized)
        {
            return;
        }
        var hr = RoInitialize(MultiThreaded);
        if (hr < 0 && hr != ChangedMode)
        {
            Com.Check(hr, "RoInitialize");
        }
        // Kept for the life of the thread: the capture thread is Slovo's own.
        initialized = true;
    }

    /// <summary>The activation factory of a runtime class, as the interface asked for.</summary>
    public static nint ActivationFactory(string className, in Guid iid)
    {
        EnsureInitialized();
        Com.Check(WindowsCreateString(className, (uint)className.Length, out var name), "WindowsCreateString");
        try
        {
            Com.Check(RoGetActivationFactory(name, iid, out var factory), $"RoGetActivationFactory({className})");
            return factory;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }
}
