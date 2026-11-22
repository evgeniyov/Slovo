using System.Runtime.Versioning;
using Slovo.Core.Logging;
using Slovo.Core.Platform;
using Slovo.Platform.Windows.Interop;
using Slovo.Platform.Windows.Threading;

namespace Slovo.Platform.Windows.Input;

/// <summary>What the focused element is, as far as UI Automation can tell (InputFocus in FocusedInput.swift).</summary>
public enum InputFocus
{
    /// <summary>A field that takes typed text, so dictation has somewhere to go.</summary>
    TextInput,
    /// <summary>Something is focused and it takes no typing.</summary>
    Other,
    /// <summary>Nothing could be read: nothing focused, or the app did not answer.</summary>
    Unknown,
}

/// <summary>
/// The UI Automation properties the rule reads. They describe the element, never its
/// value: the focused element can be a password box or a private message.
/// </summary>
public sealed record FocusedElement(
    int? ControlType,
    bool IsPassword,
    bool IsKeyboardFocusable,
    bool IsValuePatternAvailable,
    bool ValueIsReadOnly,
    bool IsTextPatternAvailable);

/// <summary>The rule of FocusedInputWatcher.classify, with UI Automation's control types for AX roles.</summary>
public static class FocusClassifier
{
    public const int ButtonControlType = 50000;
    public const int ComboBoxControlType = 50003;
    public const int EditControlType = 50004;
    public const int ListControlType = 50008;
    public const int SliderControlType = 50015;
    public const int SpinnerControlType = 50016;
    public const int CustomControlType = 50025;
    public const int GroupControlType = 50026;
    public const int DocumentControlType = 50030;
    public const int PaneControlType = 50033;

    /// <summary>
    /// <list type="bullet">
    /// <item>No control type: unknown.</item>
    /// <item>A password field: not a place to dictate (UIPI aside, pasting there is refused on macOS too).</item>
    /// <item>An edit box takes text unless it is read-only (AXTextField/AXTextArea).</item>
    /// <item>A combo box takes text when its value can be set (an editable one; AXComboBox).</item>
    /// <item>A document takes text when its value is settable, or, without a value pattern, when it
    /// is focusable and exposes text (Word, rich editors); a web page reports a read-only value.</item>
    /// <item>Anything else — custom and web editors with generic roles — takes text when it is
    /// focusable with a settable value and a text pattern (macOS: value settable and a
    /// selection). A settable value alone is not enough: spinners have one too.</item>
    /// </list>
    /// </summary>
    public static InputFocus Classify(FocusedElement? element)
    {
        if (element?.ControlType is not { } controlType)
        {
            return InputFocus.Unknown;
        }
        if (element.IsPassword)
        {
            return InputFocus.Other;
        }
        var settableValue = element.IsValuePatternAvailable && !element.ValueIsReadOnly;
        switch (controlType)
        {
            case EditControlType:
                return element.IsValuePatternAvailable && element.ValueIsReadOnly ? InputFocus.Other : InputFocus.TextInput;
            case ComboBoxControlType:
                return settableValue ? InputFocus.TextInput : InputFocus.Other;
            case DocumentControlType:
                if (element.IsValuePatternAvailable)
                {
                    return settableValue ? InputFocus.TextInput : InputFocus.Other;
                }
                return element.IsKeyboardFocusable && element.IsTextPatternAvailable ? InputFocus.TextInput : InputFocus.Other;
            default:
                return element.IsKeyboardFocusable && settableValue && element.IsTextPatternAvailable
                    ? InputFocus.TextInput
                    : InputFocus.Other;
        }
    }

    /// <summary>
    /// What an app's failure to name a focused element means (FocusProbe.focusWithoutElement):
    /// a successful answer of "nothing" has no field to dictate into, as in a music player;
    /// a failed call — the app did not answer — may still have one.
    /// </summary>
    public static InputFocus FocusWithoutElement(int hresult) => hresult >= 0 ? InputFocus.Other : InputFocus.Unknown;

    public static bool? ToTextInputFocused(InputFocus focus) => focus switch
    {
        InputFocus.TextInput => true,
        InputFocus.Other => false,
        _ => null,
    };
}

/// <summary>
/// Whether keyboard focus is in a text field, through UI Automation (FocusedInput.swift's
/// Accessibility probe).
/// </summary>
/// <remarks>
/// <para>
/// Raw COM: CoCreateInstance(CUIAutomation) and calls through the vtables, so no COM
/// interop layer is needed. The slot numbers come from UIAutomationClient.h; each interface
/// starts with IUnknown's QueryInterface (0), AddRef (1) and Release (2).
/// </para>
/// <para>
/// Threading: every call runs on a dedicated MTA thread, the apartment UI Automation
/// clients should use. A cross-process query can stall on an app that does not answer,
/// so the caller waits at most <see cref="Timeout"/> and gets null (unknown); while a
/// stalled query is still running, further calls return null at once.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class UiaFocusedInput : IFocusedInput, IDisposable
{
    /// <summary>An app that stops answering must not stall the caller (FocusedInputWatcher.messagingTimeout).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(150);

    static readonly Guid ClsidCuiAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    static readonly Guid IidIuiAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");

    // IUnknown.
    const int ReleaseSlot = 2;
    // IUIAutomation: CompareElements 3, CompareRuntimeIds 4, GetRootElement 5,
    // ElementFromHandle 6, ElementFromPoint 7, GetFocusedElement 8.
    const int GetFocusedElementSlot = 8;
    // IUIAutomationElement: SetFocus 3, GetRuntimeId 4, FindFirst 5, FindAll 6,
    // FindFirstBuildCache 7, FindAllBuildCache 8, BuildUpdatedCache 9,
    // GetCurrentPropertyValue 10.
    const int GetCurrentPropertyValueSlot = 10;

    // UIA_*PropertyId (UIAutomationClient.h).
    const int ControlTypeProperty = 30003;
    const int IsKeyboardFocusableProperty = 30009;
    const int IsPasswordProperty = 30019;
    const int IsTextPatternAvailableProperty = 30040;
    const int IsValuePatternAvailableProperty = 30043;
    const int ValueIsReadOnlyProperty = 30046;

    readonly WorkerQueue queue = new("Slovo focused input");
    readonly Logger log = Log.For("FocusedInput");
    nint automation;
    bool comInitialized;
    int busy;
    FocusedElement? lastLogged;

    public bool? IsTextInputFocused()
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        {
            return null;
        }
        var focus = queue.Invoke(() =>
        {
            try
            {
                return Read();
            }
            finally
            {
                Volatile.Write(ref busy, 0);
            }
        }, Timeout, InputFocus.Unknown);
        return FocusClassifier.ToTextInputFocused(focus);
    }

    public void Dispose()
    {
        queue.Invoke(() =>
        {
            if (automation != 0)
            {
                Release(automation);
                automation = 0;
            }
            if (comInitialized)
            {
                Ole32.CoUninitialize();
                comInitialized = false;
            }
        }, TimeSpan.FromSeconds(1));
        queue.Dispose();
    }

    /// <summary>On the queue.</summary>
    InputFocus Read()
    {
        if (!EnsureAutomation())
        {
            return InputFocus.Unknown;
        }
        nint element = 0;
        var vtable = *(nint**)automation;
        var getFocused = (delegate* unmanaged[Stdcall]<nint, nint*, int>)vtable[GetFocusedElementSlot];
        var result = getFocused(automation, &element);
        if (result < 0 || element == 0)
        {
            return FocusClassifier.FocusWithoutElement(result);
        }
        try
        {
            var properties = new FocusedElement(
                Int32Property(element, ControlTypeProperty),
                BooleanProperty(element, IsPasswordProperty) ?? false,
                BooleanProperty(element, IsKeyboardFocusableProperty) ?? false,
                BooleanProperty(element, IsValuePatternAvailableProperty) ?? false,
                BooleanProperty(element, ValueIsReadOnlyProperty) ?? false,
                BooleanProperty(element, IsTextPatternAvailableProperty) ?? false);
            var focus = FocusClassifier.Classify(properties);
            if (properties != lastLogged)
            {
                lastLogged = properties;
                log.Debug($"Focused element {properties} → {focus}");
            }
            return focus;
        }
        finally
        {
            Release(element);
        }
    }

    bool EnsureAutomation()
    {
        if (automation != 0)
        {
            return true;
        }
        if (!comInitialized)
        {
            // S_OK or S_FALSE (already initialized): either way it needs a matching CoUninitialize.
            comInitialized = Ole32.CoInitializeEx(0, Ole32.COINIT_MULTITHREADED) >= 0;
        }
        var result = Ole32.CoCreateInstance(in ClsidCuiAutomation, 0, Ole32.CLSCTX_INPROC_SERVER, in IidIuiAutomation, out var instance);
        if (result < 0 || instance == 0)
        {
            log.Error($"CoCreateInstance(CUIAutomation) failed: 0x{result:X8}");
            return false;
        }
        automation = instance;
        return true;
    }

    static Variant PropertyValue(nint element, int propertyId, out int result)
    {
        var value = default(Variant);
        var vtable = *(nint**)element;
        var getProperty = (delegate* unmanaged[Stdcall]<nint, int, Variant*, int>)vtable[GetCurrentPropertyValueSlot];
        result = getProperty(element, propertyId, &value);
        return value;
    }

    static bool? BooleanProperty(nint element, int propertyId)
    {
        var value = PropertyValue(element, propertyId, out var result);
        try
        {
            return result < 0 ? null : value.AsBoolean;
        }
        finally
        {
            Ole32.VariantClear(&value);
        }
    }

    static int? Int32Property(nint element, int propertyId)
    {
        var value = PropertyValue(element, propertyId, out var result);
        try
        {
            return result < 0 ? null : value.AsInt32;
        }
        finally
        {
            Ole32.VariantClear(&value);
        }
    }

    static void Release(nint unknown)
    {
        var vtable = *(nint**)unknown;
        ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[ReleaseSlot])(unknown);
    }
}
