using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Slovo.Platform.Windows.Interop;

namespace Slovo.Platform.Windows.Tests;

/// <summary>
/// The Win32 structures must match the SDK headers byte for byte: SendInput refuses a
/// wrong cbSize, and a misplaced dwExtraInfo would hide Slovo's own events from the
/// marker check. The expected sizes are those of a 64-bit process (x64 and ARM64 alike),
/// which is what this machine and every Windows build of Slovo run.
/// </summary>
public unsafe class InteropLayoutTests
{
    [Fact]
    public void RunsAsA64BitProcess() => Assert.Equal(8, IntPtr.Size);

    [Fact]
    public void MessageLayout()
    {
        Assert.Equal(48, sizeof(Msg));
        Assert.Equal(8, Marshal.OffsetOf<Msg>(nameof(Msg.Message)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<Msg>(nameof(Msg.WParam)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<Msg>(nameof(Msg.Time)).ToInt32());
        Assert.Equal(36, Marshal.OffsetOf<Msg>(nameof(Msg.PointX)).ToInt32());
    }

    [Fact]
    public void WindowClass()
    {
        Assert.Equal(80, sizeof(WndClassEx));
        Assert.Equal(8, Marshal.OffsetOf<WndClassEx>(nameof(WndClassEx.WndProc)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<WndClassEx>(nameof(WndClassEx.Instance)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<WndClassEx>(nameof(WndClassEx.ClassName)).ToInt32());
    }

    [Fact]
    public void KeyboardHookDataLayout()
    {
        Assert.Equal(24, sizeof(KeyboardHookData));
        Assert.Equal(8, Marshal.OffsetOf<KeyboardHookData>(nameof(KeyboardHookData.Flags)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<KeyboardHookData>(nameof(KeyboardHookData.ExtraInfo)).ToInt32());
    }

    [Fact]
    public void SendInputStructures()
    {
        Assert.Equal(32, sizeof(MouseInput));
        Assert.Equal(24, sizeof(KeyboardInput));
        Assert.Equal(8, sizeof(HardwareInput));
        Assert.Equal(32, sizeof(InputUnion));
        Assert.Equal(40, sizeof(NativeInput));
        Assert.Equal(8, Marshal.OffsetOf<NativeInput>(nameof(NativeInput.Data)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<KeyboardInput>(nameof(KeyboardInput.ExtraInfo)).ToInt32());

        var input = default(NativeInput);
        var start = (byte*)&input;
        Assert.Equal(8, (byte*)&input.Data.Keyboard.VirtualKey - start);
        Assert.Equal(10, (byte*)&input.Data.Keyboard.ScanCode - start);
        Assert.Equal(12, (byte*)&input.Data.Keyboard.Flags - start);
        Assert.Equal(24, (byte*)&input.Data.Keyboard.ExtraInfo - start);
    }

    [Fact]
    public void RawInput()
    {
        Assert.Equal(16, sizeof(RawInputDevice));
        Assert.Equal(24, sizeof(RawInputHeader));
        Assert.Equal(8, Marshal.OffsetOf<RawInputHeader>(nameof(RawInputHeader.Device)).ToInt32());
    }

    [Fact]
    public void VariantHoldsItsValueAtOffsetEight()
    {
        Assert.Equal(24, sizeof(Variant));
        Assert.Equal(8, Marshal.OffsetOf<Variant>(nameof(Variant.Value)).ToInt32());

        var variantTrue = new Variant { Type = Variant.Boolean, Value = unchecked((short)-1) & 0xFFFF };
        Assert.True(variantTrue.AsBoolean);
        var variantFalse = new Variant { Type = Variant.Boolean, Value = 0 };
        Assert.False(variantFalse.AsBoolean);
        var number = new Variant { Type = Variant.Int32, Value = 50004 };
        Assert.Equal(50004, number.AsInt32);
        Assert.Null(number.AsBoolean);
        var negative = new Variant { Type = Variant.Int32, Value = unchecked((nint)0x7777_7777_FFFF_FFFF) };
        Assert.Equal(-1, negative.AsInt32);
        Assert.Null(new Variant { Type = Variant.Empty }.AsInt32);
    }

    [Fact]
    public void StructuresAreBlittable()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<NativeInput>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<Msg>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<Variant>());
    }
}
