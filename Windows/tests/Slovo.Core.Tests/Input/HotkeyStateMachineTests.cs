using Slovo.Core.Input;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Input;

/// <summary>
/// The dictation key logic as a pure function of timestamped events, for every key choice
/// (Tests/SlovoCoreTests/InputHotkeyStateMachineTests.swift and FixCoreInputTests.swift,
/// with Windows keys: Right Alt stands in for Right Option, Right Ctrl for Right Command,
/// Ctrl+Win for Fn and Ctrl+Alt for Left Control+Option).
/// </summary>
public class HotkeyStateMachineTests
{
    const int Tab = 0x09;
    const int LetterA = 0x41;
    const int DownArrow = 0x28;
    const int LeftArrow = 0x25;

    static readonly DictationTrigger Begin = DictationTrigger.BeginPushToTalk;
    static readonly DictationTrigger Promote = DictationTrigger.Promote;
    static readonly DictationTrigger Finish = DictationTrigger.FinishRecording;
    static readonly DictationTrigger Discard = DictationTrigger.DiscardRecording;
    static readonly DictationTrigger Cancel = DictationTrigger.CancelRecording;

    public static TheoryData<DictationKey> AllKeys() => new(DictationKeys.All);

    static void Same(IEnumerable<DictationTrigger> expected, IEnumerable<DictationTrigger> actual) =>
        Assert.Equal(expected.ToArray(), actual.ToArray());

    static void None(IEnumerable<DictationTrigger> actual) => Assert.Empty(actual);

    // Push-to-talk

    [Theory, MemberData(nameof(AllKeys))]
    public void HoldPastMinimumBeginsAndFinishes(DictationKey key)
    {
        var driver = new KeyDriver(key);
        Same([Begin], driver.Press(10));
        Assert.True(driver.Machine.IsKeyDown);
        Assert.Equal(DictationMode.PushToTalk, driver.Machine.RecordingMode);
        Same([Finish], driver.Release(12.5));
        Assert.False(driver.Machine.IsKeyDown);
        Assert.Null(driver.Machine.RecordingMode);
        Assert.Null(driver.Machine.NextDeadline);
    }

    [Theory, MemberData(nameof(AllKeys))]
    public void HoldOfExactlyMinimumIsADictation(DictationKey key)
    {
        var driver = new KeyDriver(key);
        Same([Begin], driver.Press(1));
        Same([Finish], driver.Release(1.25));
    }

    [Theory, MemberData(nameof(AllKeys))]
    public void ShortTapKeepsCaptureForTheDoubleTapWindowThenDiscards(DictationKey key)
    {
        var driver = new KeyDriver(key);
        Same([Begin], driver.Press(1));
        None(driver.Release(1.1));
        Assert.Equal(DictationMode.PushToTalk, driver.Machine.RecordingMode);
        Assert.Equal(1.5, driver.Machine.NextDeadline!.Value, 9);
        None(driver.Machine.Handle(new HotkeyInput.Deadline(1.49)));
        Same([Discard], driver.Machine.Handle(new HotkeyInput.Deadline(1.5)));
        Assert.Null(driver.Machine.NextDeadline);
        Assert.Null(driver.Machine.RecordingMode);
        None(driver.Machine.Handle(new HotkeyInput.Deadline(2)));
    }

    [Fact]
    public void RepeatedDownEventsWhileHeldDoNotRestart()
    {
        // Windows auto-repeats a held modifier as more key downs.
        var driver = new KeyDriver(DictationKey.RightAlt);
        Same([Begin], driver.Press(0));
        None(driver.Modifier(VirtualKey.RightAlt, down: true, at: 0.5));
        Same([Finish], driver.Release(1));
        None(driver.Modifier(VirtualKey.RightAlt, down: false, at: 1.1));
    }

    [Fact]
    public void KeyUpEventsAreIgnored()
    {
        var driver = new KeyDriver(DictationKey.CtrlWin);
        Same([Begin], driver.Press(0));
        None(driver.KeyUp(LetterA, 0.1));
        Same([Finish], driver.Release(1));
    }

    // Hands-free

    [Theory, MemberData(nameof(AllKeys))]
    public void DoubleTapPromotesAndNextTapFinishes(DictationKey key)
    {
        var driver = new KeyDriver(key);
        Same([Begin], driver.Press(0));
        None(driver.Release(0.08));
        Same([Promote], driver.Press(0.3));
        Assert.Equal(DictationMode.HandsFree, driver.Machine.RecordingMode);
        Assert.Null(driver.Machine.NextDeadline);
        None(driver.Release(0.4));
        None(driver.Machine.Handle(new HotkeyInput.Deadline(10)));
        Same([Finish], driver.Press(42));
        Assert.Null(driver.Machine.RecordingMode);
        None(driver.Release(42.1));
    }

    [Fact]
    public void LongSecondPressStaysHandsFree()
    {
        var driver = new KeyDriver(DictationKey.RightCtrl);
        driver.Press(0);
        driver.Release(0.1);
        Same([Promote], driver.Press(0.2));
        None(driver.Release(3));
        Assert.Equal(DictationMode.HandsFree, driver.Machine.RecordingMode);
    }

    [Fact]
    public void SecondPressAtTheDeadlineStillCounts()
    {
        var driver = new KeyDriver(DictationKey.RightAlt);
        driver.Press(0);
        driver.Release(0.1);
        Same([Promote], driver.Press(0.5));
    }

    [Theory, MemberData(nameof(AllKeys))]
    public void SecondPressAfterTheWindowWithoutTimerDiscardsAndBeginsAgain(DictationKey key)
    {
        var driver = new KeyDriver(key);
        driver.Press(0);
        driver.Release(0.1);
        Same([Discard, Begin], driver.Press(0.51));
        Same([Finish], driver.Release(2));
    }

    [Fact]
    public void TripleTapFinishesHandsFreeOnTheThirdTap()
    {
        var driver = new KeyDriver(DictationKey.CtrlWin);
        Same([Begin], driver.Press(0));
        None(driver.Release(0.1));
        Same([Promote], driver.Press(0.2));
        None(driver.Release(0.3));
        Same([Finish], driver.Press(0.4));
        None(driver.Release(0.5));
        Assert.Null(driver.Machine.RecordingMode);
        Assert.Null(driver.Machine.NextDeadline);
    }

    [Theory, MemberData(nameof(AllKeys))]
    public void HandsFreeDisabledDiscardsShortTapAtOnce(DictationKey key)
    {
        var driver = new KeyDriver(key, handsFree: false);
        Same([Begin], driver.Press(0));
        Same([Discard], driver.Release(0.1));
        Assert.Null(driver.Machine.NextDeadline);
        Same([Begin], driver.Press(0.2));
        Same([Discard], driver.Release(0.3));
        Same([Begin], driver.Press(1));
        Same([Finish], driver.Release(2));
    }

    [Fact]
    public void CustomTimingConfiguration()
    {
        var driver = new KeyDriver(new HotkeyConfiguration(DictationKey.RightAlt, MinimumHold: 0.5, DoubleTapWindow: 1));
        driver.Press(0);
        None(driver.Release(0.4));
        Assert.Equal(1.4, driver.Machine.NextDeadline!.Value, 9);
        Same([Promote], driver.Press(1.3));
    }

    // Esc

    [Theory, MemberData(nameof(AllKeys))]
    public void EscapeCancelsPushToTalkAndTheReleaseIsIgnored(DictationKey key)
    {
        var driver = new KeyDriver(key);
        driver.Press(0);
        Same([Cancel], driver.KeyDown(VirtualKey.Escape, 1));
        Assert.Null(driver.Machine.RecordingMode);
        Assert.True(driver.Machine.IsKeyDown);
        None(driver.Release(2));
        Same([Begin], driver.Press(3));
    }

    [Fact]
    public void EscapeCancelsADictationStillBeingTranscribed()
    {
        var driver = new KeyDriver(DictationKey.RightCtrl);
        Assert.Empty(driver.KeyDown(VirtualKey.Escape, 0));
        driver.Machine.IsProcessing = true;
        Same([Cancel], driver.KeyDown(VirtualKey.Escape, 1));
        driver.Machine.IsProcessing = false;
        Assert.Empty(driver.KeyDown(VirtualKey.Escape, 2));
    }

    [Fact]
    public void EscapeWhileProcessingRespectsTheSetting()
    {
        var driver = new KeyDriver(DictationKey.RightCtrl);
        driver.Machine.Configuration = driver.Machine.Configuration with { EscapeCancels = false };
        driver.Machine.IsProcessing = true;
        Assert.Empty(driver.KeyDown(VirtualKey.Escape, 1));
    }

    [Fact]
    public void EscapeCancelsHandsFree()
    {
        var driver = new KeyDriver(DictationKey.RightAlt);
        driver.Press(0);
        driver.Release(0.1);
        driver.Press(0.2);
        driver.Release(0.3);
        Same([Cancel], driver.KeyDown(VirtualKey.Escape, 5));
        Same([Begin], driver.Press(6));
    }

    [Fact]
    public void EscapeCancelsPendingTap()
    {
        var driver = new KeyDriver(DictationKey.RightAlt);
        driver.Press(0);
        driver.Release(0.1);
        Same([Cancel], driver.KeyDown(VirtualKey.Escape, 0.2));
        Assert.Null(driver.Machine.NextDeadline);
        None(driver.Machine.Handle(new HotkeyInput.Deadline(1)));
    }

    [Fact]
    public void EscapeDoesNothingWhenIdleOrDisabled()
    {
        var idle = new KeyDriver(DictationKey.RightAlt);
        None(idle.KeyDown(VirtualKey.Escape, 0));

        var disabled = new KeyDriver(new HotkeyConfiguration(DictationKey.RightAlt, EscapeCancels: false));
        disabled.Press(0);
        None(disabled.KeyDown(VirtualKey.Escape, 1));
        Same([Finish], disabled.Release(2));
    }

    // Other keys

    [Theory, MemberData(nameof(AllKeys))]
    public void ShortcutDuringShortHoldIsNotADictation(DictationKey key)
    {
        var driver = new KeyDriver(key);
        Same([Begin], driver.Press(0));
        Same([Discard], driver.KeyDown(Tab, 0.1));
        None(driver.KeyDown(Tab, 0.6));
        None(driver.Release(1));
        Assert.Null(driver.Machine.RecordingMode);
    }

    /// <summary>
    /// A key pressed while the dictation key is held makes it a shortcut however long the
    /// hold was: Right Ctrl+Tab switches tabs, Ctrl+Win+Left switches desktops. Finishing
    /// would paste whatever the microphone picked up meanwhile.
    /// </summary>
    [Theory, MemberData(nameof(AllKeys))]
    public void KeyPressedDuringALongHoldIsAShortcut(DictationKey key)
    {
        var driver = new KeyDriver(key);
        Same([Begin], driver.Press(0));
        Same([Discard], driver.KeyDown(LetterA, 2));
        Assert.Null(driver.Machine.RecordingMode);
        None(driver.KeyDown(LetterA, 2.5));
        None(driver.Release(3));
        Same([Begin], driver.Press(4));
        Same([Finish], driver.Release(5));
    }

    [Theory]
    [InlineData(HotkeyKeys.MenuMaskKey)]
    [InlineData(HotkeyKeys.ReservedKey)]
    public void MaskAndFnCodesAreNotShortcuts(int code)
    {
        var driver = new KeyDriver(DictationKey.CtrlWin);
        driver.Press(0);
        None(driver.KeyDown(code, 0.01));
        Same([Finish], driver.Release(1));
    }

    [Fact]
    public void TypingAfterATapDiscardsAtOnce()
    {
        var driver = new KeyDriver(DictationKey.RightAlt);
        driver.Press(0);
        driver.Release(0.1);
        Same([Discard], driver.KeyDown(LetterA, 0.2));
        Assert.Null(driver.Machine.NextDeadline);
    }

    [Fact]
    public void KeysTypedDuringHandsFreeAreIgnored()
    {
        var driver = new KeyDriver(DictationKey.RightAlt);
        None(driver.Machine.Handle(new HotkeyInput.RecordingChanged(DictationMode.HandsFree, 0)));
        None(driver.KeyDown(LetterA, 1));
        Assert.Equal(DictationMode.HandsFree, driver.Machine.RecordingMode);
    }

    [Theory]
    [InlineData(VirtualKey.LeftShift)]
    [InlineData(VirtualKey.RightShift)]
    [InlineData(VirtualKey.RightWin)]
    [InlineData(VirtualKey.RightAlt)]
    public void ModifierKeyDownDuringShortHoldIsNotAShortcut(int modifier)
    {
        var driver = new KeyDriver(DictationKey.CtrlWin);
        driver.Press(0);
        None(driver.KeyDown(modifier, 0.05));
        Same([Finish], driver.Release(1));
    }

    [Fact]
    public void KeysWhileIdleDoNothing()
    {
        var driver = new KeyDriver(DictationKey.RightCtrl);
        None(driver.KeyDown(LetterA, 0));
        Assert.Null(driver.Machine.RecordingMode);
    }

    // Key identity

    [Fact]
    public void OtherSideOfTheModifierIsIgnored()
    {
        var alt = new KeyDriver(DictationKey.RightAlt);
        None(alt.Modifier(VirtualKey.LeftAlt, down: true, at: 0));
        None(alt.Modifier(VirtualKey.LeftAlt, down: false, at: 1));
        Assert.False(alt.Machine.IsKeyDown);

        var control = new KeyDriver(DictationKey.RightCtrl);
        None(control.Modifier(VirtualKey.LeftCtrl, down: true, at: 0));
        None(control.Modifier(VirtualKey.LeftCtrl, down: false, at: 1));
        Assert.Null(control.Machine.RecordingMode);
    }

    [Fact]
    public void OtherSideHeldDoesNotHideTheRelease()
    {
        var driver = new KeyDriver(DictationKey.RightAlt);
        Same([Begin], driver.Modifier(VirtualKey.RightAlt, down: true, at: 0));
        None(driver.Modifier(VirtualKey.LeftAlt, down: true, at: 0.5));
        Same([Finish], driver.Modifier(VirtualKey.RightAlt, down: false, at: 1));
        None(driver.Modifier(VirtualKey.LeftAlt, down: false, at: 1.5));
    }

    /// <summary>AltGr layouts send a Left Ctrl down with Right Alt; it must not spoil the key.</summary>
    [Fact]
    public void AltGrLeftControlDoesNotSpoilRightAlt()
    {
        var driver = new KeyDriver(DictationKey.RightAlt);
        None(driver.Modifier(VirtualKey.LeftCtrl, down: true, at: 0));
        Same([Begin], driver.Modifier(VirtualKey.RightAlt, down: true, at: 0));
        None(driver.Modifier(VirtualKey.LeftCtrl, down: false, at: 1));
        Same([Finish], driver.Modifier(VirtualKey.RightAlt, down: false, at: 1));
    }

    /// <summary>Injected input can use the side-independent codes; they are normalised first.</summary>
    [Fact]
    public void SideIndependentCodesAreNormalisedBeforeTheMachine()
    {
        var machine = new HotkeyStateMachine(new HotkeyConfiguration(DictationKey.RightAlt));
        var down = HotkeyKeys.Normalize(0x12, 0x38, extended: true);
        Same([Begin], machine.Handle(new HotkeyInput.Key(new RawKeyEvent(RawKeyEventKind.KeyDown, down, 0))));
        var up = HotkeyKeys.Normalize(0x12, 0x38, extended: true);
        Same([Finish], machine.Handle(new HotkeyInput.Key(new RawKeyEvent(RawKeyEventKind.KeyUp, up, 1))));

        var left = HotkeyKeys.Normalize(0x12, 0x38, extended: false);
        None(machine.Handle(new HotkeyInput.Key(new RawKeyEvent(RawKeyEventKind.KeyDown, left, 2))));
        Assert.False(machine.IsKeyDown);
    }

    [Fact]
    public void NormalizeTellsLeftFromRight()
    {
        Assert.Equal(VirtualKey.RightCtrl, HotkeyKeys.Normalize(0x11, 0x1D, extended: true));
        Assert.Equal(VirtualKey.LeftCtrl, HotkeyKeys.Normalize(0x11, 0x1D, extended: false));
        Assert.Equal(VirtualKey.RightAlt, HotkeyKeys.Normalize(0x12, 0x38, extended: true));
        Assert.Equal(VirtualKey.LeftAlt, HotkeyKeys.Normalize(0x12, 0x38, extended: false));
        Assert.Equal(VirtualKey.RightShift, HotkeyKeys.Normalize(0x10, 0x36, extended: false));
        Assert.Equal(VirtualKey.LeftShift, HotkeyKeys.Normalize(0x10, 0x2A, extended: false));
        // Sided codes and other keys pass through, whatever the flags say.
        Assert.Equal(VirtualKey.RightCtrl, HotkeyKeys.Normalize(VirtualKey.RightCtrl, 0x1D, extended: false));
        Assert.Equal(VirtualKey.LeftWin, HotkeyKeys.Normalize(VirtualKey.LeftWin, 0x5B, extended: true));
        Assert.Equal(LetterA, HotkeyKeys.Normalize(LetterA, 0x1E, extended: true));
        Assert.Equal(VirtualKey.Escape, HotkeyKeys.Normalize(VirtualKey.Escape, 0x01, extended: false));
    }

    [Fact]
    public void NonKeys()
    {
        Assert.True(HotkeyKeys.IsNonKey(0xE8));
        Assert.True(HotkeyKeys.IsNonKey(0xFF));
        Assert.True(HotkeyKeys.IsNonKey(0));
        Assert.False(HotkeyKeys.IsNonKey(LetterA));
        Assert.False(HotkeyKeys.IsNonKey(VirtualKey.Escape));
    }

    [Fact]
    public void MenuMaskOnlyWhenWinOrAltBecomesHeld()
    {
        Assert.True(HotkeyKeys.ShouldMaskMenu(DictationKey.CtrlWin, wasDown: false, isDown: true));
        Assert.True(HotkeyKeys.ShouldMaskMenu(DictationKey.RightAlt, wasDown: false, isDown: true));
        Assert.True(HotkeyKeys.ShouldMaskMenu(DictationKey.LeftCtrlAlt, wasDown: false, isDown: true));
        Assert.False(HotkeyKeys.ShouldMaskMenu(DictationKey.RightCtrl, wasDown: false, isDown: true));
        // Once per hold, and never on release.
        Assert.False(HotkeyKeys.ShouldMaskMenu(DictationKey.CtrlWin, wasDown: true, isDown: true));
        Assert.False(HotkeyKeys.ShouldMaskMenu(DictationKey.CtrlWin, wasDown: true, isDown: false));
        Assert.False(HotkeyKeys.ShouldMaskMenu(DictationKey.CtrlWin, wasDown: false, isDown: false));
    }

    // Two-key combinations

    [Fact]
    public void CombinationNeedsBothKeysInEitherOrder()
    {
        var driver = new KeyDriver(DictationKey.LeftCtrlAlt);
        None(driver.Modifier(VirtualKey.LeftCtrl, down: true, at: 0));
        Assert.False(driver.Machine.IsKeyDown);
        Same([Begin], driver.Modifier(VirtualKey.LeftAlt, down: true, at: 0.05));
        Assert.True(driver.Machine.IsKeyDown);
        Same([Finish], driver.Modifier(VirtualKey.LeftCtrl, down: false, at: 1));
        None(driver.Modifier(VirtualKey.LeftAlt, down: false, at: 1.05));

        None(driver.Modifier(VirtualKey.LeftAlt, down: true, at: 5));
        Same([Begin], driver.Modifier(VirtualKey.LeftCtrl, down: true, at: 5.05));
        Same([Finish], driver.Modifier(VirtualKey.LeftAlt, down: false, at: 6));
        None(driver.Modifier(VirtualKey.LeftCtrl, down: false, at: 6.05));
    }

    [Fact]
    public void CtrlWinNeedsBothKeysInEitherOrder()
    {
        var driver = new KeyDriver(DictationKey.CtrlWin);
        None(driver.Modifier(VirtualKey.LeftWin, down: true, at: 0));
        Same([Begin], driver.Modifier(VirtualKey.LeftCtrl, down: true, at: 0.05));
        None(driver.Modifier(VirtualKey.LeftCtrl, down: true, at: 0.5));
        Same([Finish], driver.Modifier(VirtualKey.LeftWin, down: false, at: 1));
        None(driver.Modifier(VirtualKey.LeftCtrl, down: false, at: 1.05));
    }

    [Fact]
    public void CombinationHoldTimeStartsWhenBothAreDown()
    {
        var driver = new KeyDriver(DictationKey.LeftCtrlAlt);
        None(driver.Modifier(VirtualKey.LeftCtrl, down: true, at: 0));
        Same([Begin], driver.Modifier(VirtualKey.LeftAlt, down: true, at: 2));
        // 0.1 s with both held is a tap even though Ctrl was down for 2 s.
        None(driver.Modifier(VirtualKey.LeftAlt, down: false, at: 2.1));
        Assert.Equal(2.5, driver.Machine.NextDeadline!.Value, 9);
        Same([Promote], driver.Modifier(VirtualKey.LeftAlt, down: true, at: 2.3));
    }

    [Fact]
    public void CombinationIgnoresRightSideKeys()
    {
        var driver = new KeyDriver(DictationKey.LeftCtrlAlt);
        None(driver.Modifier(VirtualKey.RightCtrl, down: true, at: 0));
        None(driver.Modifier(VirtualKey.RightAlt, down: true, at: 0.1));
        Assert.False(driver.Machine.IsKeyDown);

        var win = new KeyDriver(DictationKey.CtrlWin);
        None(win.Modifier(VirtualKey.RightCtrl, down: true, at: 0));
        None(win.Modifier(VirtualKey.RightWin, down: true, at: 0.1));
        Assert.False(win.Machine.IsKeyDown);
    }

    // Controller state

    [Theory, MemberData(nameof(AllKeys))]
    public void HandsFreeStartedElsewhereEndsOnTap(DictationKey key)
    {
        var driver = new KeyDriver(key);
        None(driver.Machine.Handle(new HotkeyInput.RecordingChanged(DictationMode.HandsFree, 0)));
        Assert.Equal(DictationMode.HandsFree, driver.Machine.RecordingMode);
        Same([Finish], driver.Press(3));
        None(driver.Release(3.1));
    }

    [Fact]
    public void HandsFreeStartedElsewhereWorksWithHandsFreeDisabled()
    {
        var driver = new KeyDriver(DictationKey.RightAlt, handsFree: false);
        driver.Machine.Handle(new HotkeyInput.RecordingChanged(DictationMode.HandsFree, 0));
        Same([Finish], driver.Press(1));
    }

    [Fact]
    public void StopElsewhereDuringHoldIgnoresTheRelease()
    {
        var driver = new KeyDriver(DictationKey.CtrlWin);
        driver.Press(0);
        None(driver.Machine.Handle(new HotkeyInput.RecordingChanged(null, 1)));
        Assert.Null(driver.Machine.RecordingMode);
        None(driver.Release(2));
        Same([Begin], driver.Press(3));
    }

    [Fact]
    public void StopElsewhereClearsAPendingTap()
    {
        var driver = new KeyDriver(DictationKey.RightAlt);
        driver.Press(0);
        driver.Release(0.1);
        Assert.NotNull(driver.Machine.NextDeadline);
        driver.Machine.Handle(new HotkeyInput.RecordingChanged(null, 0.2));
        Assert.Null(driver.Machine.NextDeadline);
        None(driver.Machine.Handle(new HotkeyInput.Deadline(1)));
        Same([Begin], driver.Press(0.3));
    }

    [Fact]
    public void StopElsewhereDuringHandsFree()
    {
        var driver = new KeyDriver(DictationKey.RightCtrl);
        driver.Machine.Handle(new HotkeyInput.RecordingChanged(DictationMode.HandsFree, 0));
        driver.Machine.Handle(new HotkeyInput.RecordingChanged(null, 360));
        Same([Begin], driver.Press(361));
    }

    [Fact]
    public void PushToTalkReportedWhileKeyHeldEndsOnRelease()
    {
        var driver = new KeyDriver(DictationKey.RightAlt);
        driver.Press(0);
        driver.Machine.Handle(new HotkeyInput.RecordingChanged(null, 0.5));
        driver.Machine.Handle(new HotkeyInput.RecordingChanged(DictationMode.PushToTalk, 1));
        Assert.Equal(DictationMode.PushToTalk, driver.Machine.RecordingMode);
        Same([Finish], driver.Release(2));
    }

    [Fact]
    public void PushToTalkReportedWithoutKeyEndsOnTap()
    {
        var driver = new KeyDriver(DictationKey.RightAlt);
        driver.Machine.Handle(new HotkeyInput.RecordingChanged(DictationMode.PushToTalk, 0));
        Same([Finish], driver.Press(1));
    }

    [Fact]
    public void RecordingChangedIsIdempotent()
    {
        var driver = new KeyDriver(DictationKey.RightAlt);
        driver.Press(0);
        driver.Machine.Handle(new HotkeyInput.RecordingChanged(DictationMode.PushToTalk, 0.01));
        Same([Finish], driver.Release(1));

        driver.Press(2);
        driver.Release(2.1);
        driver.Press(2.2);
        driver.Machine.Handle(new HotkeyInput.RecordingChanged(DictationMode.HandsFree, 2.25));
        None(driver.Release(2.3));
        Same([Finish], driver.Press(4));
    }

    // Recovery

    [Fact]
    public void ReleaseKeysFinishesAHoldAfterWake()
    {
        var driver = new KeyDriver(DictationKey.RightAlt);
        driver.Press(0);
        Same([Finish], driver.Machine.ReleaseKeys(30));
        Assert.False(driver.Machine.IsKeyDown);
        None(driver.Machine.ReleaseKeys(31));
    }

    [Fact]
    public void ReleaseKeysExpiresAStalePendingTap()
    {
        var driver = new KeyDriver(DictationKey.RightAlt);
        driver.Press(0);
        driver.Release(0.1);
        Same([Discard], driver.Machine.ReleaseKeys(30));
        Assert.Null(driver.Machine.NextDeadline);
    }

    [Fact]
    public void ChangingTheKeyDuringAHoldLetsTheNewKeyEndIt()
    {
        var driver = new KeyDriver(DictationKey.RightAlt);
        driver.Press(0);
        driver.Machine.Configuration = driver.Machine.Configuration with { Key = DictationKey.RightCtrl };
        driver.Key = DictationKey.RightCtrl;
        Assert.False(driver.Machine.IsKeyDown);
        None(driver.Modifier(VirtualKey.RightAlt, down: false, at: 1));
        Assert.Equal(DictationMode.HandsFree, driver.Machine.RecordingMode);
        Same([Finish], driver.Press(2));
    }

    [Fact]
    public void ChangingOtherSettingsKeepsState()
    {
        var driver = new KeyDriver(DictationKey.RightAlt);
        driver.Press(0);
        driver.Machine.Configuration = driver.Machine.Configuration with { EscapeCancels = false };
        Assert.True(driver.Machine.IsKeyDown);
        Same([Finish], driver.Release(1));
    }

    // Chords (FixCoreInputTests.swift)

    /// <summary>Right Ctrl held a moment, then an arrow: Ctrl+Down is a shortcut, not a dictation.</summary>
    [Fact]
    public void NavigationChordIsNotADictation()
    {
        var machine = new HotkeyStateMachine(new HotkeyConfiguration(DictationKey.RightCtrl));
        var triggers = new List<DictationTrigger>();
        triggers.AddRange(machine.Handle(Key(RawKeyEventKind.KeyDown, VirtualKey.RightCtrl, 0)));
        triggers.AddRange(machine.Handle(Key(RawKeyEventKind.KeyDown, DownArrow, 0.4)));
        triggers.AddRange(machine.Handle(Key(RawKeyEventKind.KeyDown, DownArrow, 0.6)));
        triggers.AddRange(machine.Handle(Key(RawKeyEventKind.KeyUp, VirtualKey.RightCtrl, 0.9)));
        Same([Begin, Discard], triggers);
        Assert.Null(machine.RecordingMode);

        // The next ordinary hold dictates again.
        Same([Begin], machine.Handle(Key(RawKeyEventKind.KeyDown, VirtualKey.RightCtrl, 2)));
        Same([Finish], machine.Handle(Key(RawKeyEventKind.KeyUp, VirtualKey.RightCtrl, 3)));
    }

    /// <summary>Ctrl+Win held for a moment, then Left to switch desktops.</summary>
    [Fact]
    public void DesktopSwitchAfterALongHoldIsNotADictation()
    {
        var driver = new KeyDriver(DictationKey.CtrlWin);
        var triggers = new List<DictationTrigger>();
        triggers.AddRange(driver.Press(0));
        triggers.AddRange(driver.KeyDown(LeftArrow, 0.7));
        triggers.AddRange(driver.KeyDown(LeftArrow, 1.2));
        triggers.AddRange(driver.Release(1.5));
        Same([Begin, Discard], triggers);
    }

    [Fact]
    public void ChordDuringHandsFreeKeepsRecording()
    {
        var machine = new HotkeyStateMachine(new HotkeyConfiguration());
        machine.Handle(new HotkeyInput.RecordingChanged(DictationMode.HandsFree, 0));
        None(machine.Handle(Key(RawKeyEventKind.KeyDown, DownArrow, 1)));
        Assert.Equal(DictationMode.HandsFree, machine.RecordingMode);
    }

    [Fact]
    public void DefaultConfigurationMatchesTheSpec()
    {
        var configuration = new HotkeyConfiguration();
        Assert.Equal(DictationKey.CtrlWin, configuration.Key);
        Assert.Equal(0.25, configuration.MinimumHold);
        Assert.Equal(0.4, configuration.DoubleTapWindow);
        Assert.True(configuration.HandsFreeEnabled);
        Assert.True(configuration.EscapeCancels);
    }

    static HotkeyInput Key(RawKeyEventKind kind, int virtualKey, double at) =>
        new HotkeyInput.Key(new RawKeyEvent(kind, virtualKey, at));

    /// <summary>Produces the key downs and ups the Windows hook reports for the dictation key.</summary>
    sealed class KeyDriver
    {
        public KeyDriver(DictationKey key, bool handsFree = true)
            : this(new HotkeyConfiguration(key, HandsFreeEnabled: handsFree))
        {
        }

        public KeyDriver(HotkeyConfiguration configuration)
        {
            Machine = new HotkeyStateMachine(configuration);
            Key = configuration.Key;
        }

        public HotkeyStateMachine Machine { get; }
        public DictationKey Key { get; set; }

        /// <summary>Presses every key of the dictation key, in order, at the same time.</summary>
        public List<DictationTrigger> Press(double at) =>
            Key.KeyCodes().SelectMany(code => Modifier(code, down: true, at)).ToList();

        public List<DictationTrigger> Release(double at) =>
            Key.KeyCodes().SelectMany(code => Modifier(code, down: false, at)).ToList();

        public IReadOnlyList<DictationTrigger> Modifier(int virtualKey, bool down, double at) =>
            Machine.Handle(HotkeyStateMachineTests.Key(down ? RawKeyEventKind.KeyDown : RawKeyEventKind.KeyUp, virtualKey, at));

        public IReadOnlyList<DictationTrigger> KeyDown(int virtualKey, double at) =>
            Machine.Handle(HotkeyStateMachineTests.Key(RawKeyEventKind.KeyDown, virtualKey, at));

        public IReadOnlyList<DictationTrigger> KeyUp(int virtualKey, double at) =>
            Machine.Handle(HotkeyStateMachineTests.Key(RawKeyEventKind.KeyUp, virtualKey, at));
    }
}
