import Foundation
import XCTest
@testable import SlovoCore

/// The dictation key logic as a pure function of timestamped events, for every key choice.
final class InputHotkeyStateMachineTests: XCTestCase {
    private static let tabKeyCode: UInt16 = 48
    private static let letterKeyCode: UInt16 = 0

    // MARK: - Push-to-talk

    func testHoldPastMinimumBeginsAndFinishes() {
        for key in DictationKey.allCases {
            var driver = KeyDriver(key: key)
            XCTAssertEqual(driver.press(at: 10), [.begin(.pushToTalk)], "\(key)")
            XCTAssertTrue(driver.machine.isKeyDown, "\(key)")
            XCTAssertEqual(driver.machine.recordingMode, .pushToTalk, "\(key)")
            XCTAssertEqual(driver.release(at: 12.5), [.finish], "\(key)")
            XCTAssertFalse(driver.machine.isKeyDown, "\(key)")
            XCTAssertNil(driver.machine.recordingMode, "\(key)")
            XCTAssertNil(driver.machine.nextDeadline, "\(key)")
        }
    }

    func testHoldOfExactlyMinimumIsADictation() {
        for key in DictationKey.allCases {
            var driver = KeyDriver(key: key)
            XCTAssertEqual(driver.press(at: 1), [.begin(.pushToTalk)], "\(key)")
            XCTAssertEqual(driver.release(at: 1.25), [.finish], "\(key)")
        }
    }

    func testShortTapKeepsCaptureForTheDoubleTapWindowThenDiscards() {
        for key in DictationKey.allCases {
            var driver = KeyDriver(key: key)
            XCTAssertEqual(driver.press(at: 1), [.begin(.pushToTalk)], "\(key)")
            XCTAssertEqual(driver.release(at: 1.1), [], "\(key)")
            XCTAssertEqual(driver.machine.recordingMode, .pushToTalk, "\(key)")
            XCTAssertEqual(try XCTUnwrap(driver.machine.nextDeadline), 1.5, accuracy: 1e-9, "\(key)")
            XCTAssertEqual(driver.machine.handle(.deadline(1.49)), [], "\(key)")
            XCTAssertEqual(driver.machine.handle(.deadline(1.5)), [.discard], "\(key)")
            XCTAssertNil(driver.machine.nextDeadline, "\(key)")
            XCTAssertNil(driver.machine.recordingMode, "\(key)")
            XCTAssertEqual(driver.machine.handle(.deadline(2)), [], "\(key)")
        }
    }

    func testRepeatedDownEventsWhileHeldDoNotRestart() {
        var driver = KeyDriver(key: .rightOption)
        XCTAssertEqual(driver.press(at: 0), [.begin(.pushToTalk)])
        XCTAssertEqual(driver.modifier(61, down: true, at: 0.5), [])
        XCTAssertEqual(driver.release(at: 1), [.finish])
        XCTAssertEqual(driver.modifier(61, down: false, at: 1.1), [])
    }

    func testKeyUpEventsAreIgnored() {
        var driver = KeyDriver(key: .fn)
        XCTAssertEqual(driver.press(at: 0), [.begin(.pushToTalk)])
        let keyUp = RawKeyEvent(kind: .keyUp, keyCode: Self.letterKeyCode, flags: driver.flags, timestamp: 0.1)
        XCTAssertEqual(driver.machine.handle(.key(keyUp)), [])
        XCTAssertEqual(driver.release(at: 1), [.finish])
    }

    // MARK: - Hands-free

    func testDoubleTapPromotesAndNextTapFinishes() {
        for key in DictationKey.allCases {
            var driver = KeyDriver(key: key)
            XCTAssertEqual(driver.press(at: 0), [.begin(.pushToTalk)], "\(key)")
            XCTAssertEqual(driver.release(at: 0.08), [], "\(key)")
            XCTAssertEqual(driver.press(at: 0.3), [.promoteToHandsFree], "\(key)")
            XCTAssertEqual(driver.machine.recordingMode, .handsFree, "\(key)")
            XCTAssertNil(driver.machine.nextDeadline, "\(key)")
            XCTAssertEqual(driver.release(at: 0.4), [], "\(key)")
            XCTAssertEqual(driver.machine.handle(.deadline(10)), [], "\(key)")
            XCTAssertEqual(driver.press(at: 42), [.finish], "\(key)")
            XCTAssertNil(driver.machine.recordingMode, "\(key)")
            XCTAssertEqual(driver.release(at: 42.1), [], "\(key)")
        }
    }

    func testLongSecondPressStaysHandsFree() {
        var driver = KeyDriver(key: .rightCommand)
        _ = driver.press(at: 0)
        _ = driver.release(at: 0.1)
        XCTAssertEqual(driver.press(at: 0.2), [.promoteToHandsFree])
        XCTAssertEqual(driver.release(at: 3), [])
        XCTAssertEqual(driver.machine.recordingMode, .handsFree)
    }

    func testSecondPressAtTheDeadlineStillCounts() {
        var driver = KeyDriver(key: .rightOption)
        _ = driver.press(at: 0)
        _ = driver.release(at: 0.1)
        XCTAssertEqual(driver.press(at: 0.5), [.promoteToHandsFree])
    }

    func testSecondPressAfterTheWindowWithoutTimerDiscardsAndBeginsAgain() {
        for key in DictationKey.allCases {
            var driver = KeyDriver(key: key)
            _ = driver.press(at: 0)
            _ = driver.release(at: 0.1)
            XCTAssertEqual(driver.press(at: 0.51), [.discard, .begin(.pushToTalk)], "\(key)")
            XCTAssertEqual(driver.release(at: 2), [.finish], "\(key)")
        }
    }

    func testTripleTapFinishesHandsFreeOnTheThirdTap() {
        var driver = KeyDriver(key: .fn)
        XCTAssertEqual(driver.press(at: 0), [.begin(.pushToTalk)])
        XCTAssertEqual(driver.release(at: 0.1), [])
        XCTAssertEqual(driver.press(at: 0.2), [.promoteToHandsFree])
        XCTAssertEqual(driver.release(at: 0.3), [])
        XCTAssertEqual(driver.press(at: 0.4), [.finish])
        XCTAssertEqual(driver.release(at: 0.5), [])
        XCTAssertNil(driver.machine.recordingMode)
        XCTAssertNil(driver.machine.nextDeadline)
    }

    func testHandsFreeDisabledDiscardsShortTapAtOnce() {
        for key in DictationKey.allCases {
            var driver = KeyDriver(key: key, handsFree: false)
            XCTAssertEqual(driver.press(at: 0), [.begin(.pushToTalk)], "\(key)")
            XCTAssertEqual(driver.release(at: 0.1), [.discard], "\(key)")
            XCTAssertNil(driver.machine.nextDeadline, "\(key)")
            XCTAssertEqual(driver.press(at: 0.2), [.begin(.pushToTalk)], "\(key)")
            XCTAssertEqual(driver.release(at: 0.3), [.discard], "\(key)")
            XCTAssertEqual(driver.press(at: 1), [.begin(.pushToTalk)], "\(key)")
            XCTAssertEqual(driver.release(at: 2), [.finish], "\(key)")
        }
    }

    func testCustomTimingConfiguration() {
        var driver = KeyDriver(configuration: HotkeyConfiguration(key: .rightOption, minimumHold: 0.5, doubleTapWindow: 1))
        _ = driver.press(at: 0)
        XCTAssertEqual(driver.release(at: 0.4), [])
        XCTAssertEqual(driver.machine.nextDeadline, 1.4)
        XCTAssertEqual(driver.press(at: 1.3), [.promoteToHandsFree])
    }

    // MARK: - Esc

    func testEscapeCancelsPushToTalkAndTheReleaseIsIgnored() {
        for key in DictationKey.allCases {
            var driver = KeyDriver(key: key)
            _ = driver.press(at: 0)
            XCTAssertEqual(driver.keyDown(RawKeyEvent.escapeKeyCode, at: 1), [.cancel], "\(key)")
            XCTAssertNil(driver.machine.recordingMode, "\(key)")
            XCTAssertTrue(driver.machine.isKeyDown, "\(key)")
            XCTAssertEqual(driver.release(at: 2), [], "\(key)")
            XCTAssertEqual(driver.press(at: 3), [.begin(.pushToTalk)], "\(key)")
        }
    }

    func testEscapeCancelsHandsFree() {
        var driver = KeyDriver(key: .rightOption)
        _ = driver.press(at: 0)
        _ = driver.release(at: 0.1)
        _ = driver.press(at: 0.2)
        _ = driver.release(at: 0.3)
        XCTAssertEqual(driver.keyDown(RawKeyEvent.escapeKeyCode, at: 5), [.cancel])
        XCTAssertEqual(driver.press(at: 6), [.begin(.pushToTalk)])
    }

    func testEscapeCancelsPendingTap() {
        var driver = KeyDriver(key: .rightOption)
        _ = driver.press(at: 0)
        _ = driver.release(at: 0.1)
        XCTAssertEqual(driver.keyDown(RawKeyEvent.escapeKeyCode, at: 0.2), [.cancel])
        XCTAssertNil(driver.machine.nextDeadline)
        XCTAssertEqual(driver.machine.handle(.deadline(1)), [])
    }

    func testEscapeDoesNothingWhenIdleOrDisabled() {
        var idle = KeyDriver(key: .rightOption)
        XCTAssertEqual(idle.keyDown(RawKeyEvent.escapeKeyCode, at: 0), [])

        var disabled = KeyDriver(configuration: HotkeyConfiguration(key: .rightOption, escapeCancels: false))
        _ = disabled.press(at: 0)
        XCTAssertEqual(disabled.keyDown(RawKeyEvent.escapeKeyCode, at: 1), [])
        XCTAssertEqual(disabled.release(at: 2), [.finish])
    }

    // MARK: - Other keys

    func testShortcutDuringShortHoldIsNotADictation() {
        for key in DictationKey.allCases {
            var driver = KeyDriver(key: key)
            XCTAssertEqual(driver.press(at: 0), [.begin(.pushToTalk)], "\(key)")
            XCTAssertEqual(driver.keyDown(Self.tabKeyCode, at: 0.1), [.discard], "\(key)")
            XCTAssertEqual(driver.keyDown(Self.tabKeyCode, at: 0.6), [], "\(key)")
            XCTAssertEqual(driver.release(at: 1), [], "\(key)")
            XCTAssertNil(driver.machine.recordingMode, "\(key)")
        }
    }

    /// A key pressed while the dictation key is held makes it a shortcut however long
    /// the hold was: Fn+Down is Page Down, Right Command+Tab switches apps. Finishing
    /// would paste whatever the microphone picked up meanwhile.
    func testKeyPressedDuringALongHoldIsAShortcut() {
        for key in DictationKey.allCases {
            var driver = KeyDriver(key: key)
            XCTAssertEqual(driver.press(at: 0), [.begin(.pushToTalk)], "\(key)")
            XCTAssertEqual(driver.keyDown(Self.letterKeyCode, at: 2), [.discard], "\(key)")
            XCTAssertNil(driver.machine.recordingMode, "\(key)")
            XCTAssertEqual(driver.keyDown(Self.letterKeyCode, at: 2.5), [], "\(key)")
            XCTAssertEqual(driver.release(at: 3), [], "\(key)")
            XCTAssertEqual(driver.press(at: 4), [.begin(.pushToTalk)], "\(key)")
            XCTAssertEqual(driver.release(at: 5), [.finish], "\(key)")
        }
    }

    func testGlobeKeyDownIsNotAShortcut() {
        var driver = KeyDriver(key: .fn)
        _ = driver.press(at: 0)
        XCTAssertEqual(driver.keyDown(HotkeyStateMachine.globeKeyCode, at: 0.01), [])
        XCTAssertEqual(driver.release(at: 1), [.finish])
    }

    func testTypingAfterATapDiscardsAtOnce() {
        var driver = KeyDriver(key: .rightOption)
        _ = driver.press(at: 0)
        _ = driver.release(at: 0.1)
        XCTAssertEqual(driver.keyDown(Self.letterKeyCode, at: 0.2), [.discard])
        XCTAssertNil(driver.machine.nextDeadline)
    }

    func testKeysTypedDuringHandsFreeAreIgnored() {
        var driver = KeyDriver(key: .rightOption)
        XCTAssertEqual(driver.machine.handle(.recordingChanged(.handsFree, at: 0)), [])
        XCTAssertEqual(driver.keyDown(Self.letterKeyCode, at: 1), [])
        XCTAssertEqual(driver.machine.recordingMode, .handsFree)
    }

    func testModifierKeyDownDuringShortHoldIsNotAShortcut() {
        var driver = KeyDriver(key: .fn)
        _ = driver.press(at: 0)
        XCTAssertEqual(driver.keyDown(63, at: 0.05), [])
        XCTAssertEqual(driver.release(at: 1), [.finish])
    }

    func testKeysWhileIdleDoNothing() {
        var driver = KeyDriver(key: .rightCommand)
        XCTAssertEqual(driver.keyDown(Self.letterKeyCode, at: 0), [])
        XCTAssertNil(driver.machine.recordingMode)
    }

    // MARK: - Key identity

    func testFnNeedsTheFunctionFlag() {
        var machine = HotkeyStateMachine(configuration: HotkeyConfiguration(key: .fn))
        let withoutFlag = RawKeyEvent(kind: .flagsChanged, keyCode: 63, flags: 0x100, timestamp: 0)
        XCTAssertEqual(machine.handle(.key(withoutFlag)), [])
        XCTAssertFalse(machine.isKeyDown)
        let withFlag = RawKeyEvent(kind: .flagsChanged, keyCode: 63, flags: 0x100 | KeyFlagMask.function, timestamp: 0.1)
        XCTAssertEqual(machine.handle(.key(withFlag)), [.begin(.pushToTalk)])
    }

    func testFunctionFlagOnOtherKeysIsNotFn() {
        var machine = HotkeyStateMachine(configuration: HotkeyConfiguration(key: .fn))
        // Arrow keys carry the function flag on their key down.
        let arrow = RawKeyEvent(kind: .keyDown, keyCode: 123, flags: KeyFlagMask.function, timestamp: 0)
        XCTAssertEqual(machine.handle(.key(arrow)), [])
        let shift = RawKeyEvent(kind: .flagsChanged, keyCode: 56, flags: KeyFlagMask.function | 0x2_0002, timestamp: 0.1)
        XCTAssertEqual(machine.handle(.key(shift)), [])
        XCTAssertFalse(machine.isKeyDown)
    }

    func testOtherSideOfTheModifierIsIgnored() {
        var option = KeyDriver(key: .rightOption)
        XCTAssertEqual(option.modifier(58, down: true, at: 0), [])
        XCTAssertEqual(option.modifier(58, down: false, at: 1), [])
        XCTAssertFalse(option.machine.isKeyDown)

        var command = KeyDriver(key: .rightCommand)
        XCTAssertEqual(command.modifier(55, down: true, at: 0), [])
        XCTAssertEqual(command.modifier(55, down: false, at: 1), [])
        XCTAssertNil(command.machine.recordingMode)
    }

    func testOtherSideHeldDoesNotHideTheRelease() {
        var driver = KeyDriver(key: .rightOption)
        XCTAssertEqual(driver.modifier(61, down: true, at: 0), [.begin(.pushToTalk)])
        XCTAssertEqual(driver.modifier(58, down: true, at: 0.5), [])
        // The option flag stays set because left Option is still held.
        XCTAssertNotEqual(driver.flags & KeyFlagMask.option, 0)
        XCTAssertEqual(driver.modifier(61, down: false, at: 1), [.finish])
        XCTAssertEqual(driver.modifier(58, down: false, at: 1.5), [])
    }

    func testKeyboardWithoutDeviceBitsFallsBackToTheGeneralFlag() {
        var machine = HotkeyStateMachine(configuration: HotkeyConfiguration(key: .rightOption))
        let down = RawKeyEvent(kind: .flagsChanged, keyCode: 61, flags: KeyFlagMask.option, timestamp: 0)
        XCTAssertEqual(machine.handle(.key(down)), [.begin(.pushToTalk)])
        let up = RawKeyEvent(kind: .flagsChanged, keyCode: 61, flags: 0, timestamp: 1)
        XCTAssertEqual(machine.handle(.key(up)), [.finish])
    }

    func testModifierDownDecoding() {
        XCTAssertTrue(HotkeyStateMachine.isModifierDown(keyCode: 61, flags: KeyFlagMask.option | KeyFlagMask.rightOption))
        XCTAssertFalse(HotkeyStateMachine.isModifierDown(keyCode: 61, flags: KeyFlagMask.option | KeyFlagMask.leftOption))
        XCTAssertTrue(HotkeyStateMachine.isModifierDown(keyCode: 54, flags: KeyFlagMask.command | KeyFlagMask.rightCommand))
        XCTAssertFalse(HotkeyStateMachine.isModifierDown(keyCode: 54, flags: KeyFlagMask.command | KeyFlagMask.leftCommand))
        XCTAssertTrue(HotkeyStateMachine.isModifierDown(keyCode: 59, flags: KeyFlagMask.control | KeyFlagMask.leftControl))
        XCTAssertFalse(HotkeyStateMachine.isModifierDown(keyCode: 59, flags: KeyFlagMask.control | KeyFlagMask.rightControl))
        XCTAssertTrue(HotkeyStateMachine.isModifierDown(keyCode: 58, flags: KeyFlagMask.option | KeyFlagMask.leftOption))
        XCTAssertTrue(HotkeyStateMachine.isModifierDown(keyCode: 63, flags: KeyFlagMask.function))
        XCTAssertFalse(HotkeyStateMachine.isModifierDown(keyCode: 63, flags: KeyFlagMask.option))
        XCTAssertFalse(HotkeyStateMachine.isModifierDown(keyCode: 0, flags: UInt64.max))
    }

    // MARK: - Left Control + Left Option

    func testCombinationNeedsBothKeysInEitherOrder() {
        var driver = KeyDriver(key: .leftControlOption)
        XCTAssertEqual(driver.modifier(59, down: true, at: 0), [])
        XCTAssertFalse(driver.machine.isKeyDown)
        XCTAssertEqual(driver.modifier(58, down: true, at: 0.05), [.begin(.pushToTalk)])
        XCTAssertTrue(driver.machine.isKeyDown)
        XCTAssertEqual(driver.modifier(59, down: false, at: 1), [.finish])
        XCTAssertEqual(driver.modifier(58, down: false, at: 1.05), [])

        XCTAssertEqual(driver.modifier(58, down: true, at: 5), [])
        XCTAssertEqual(driver.modifier(59, down: true, at: 5.05), [.begin(.pushToTalk)])
        XCTAssertEqual(driver.modifier(58, down: false, at: 6), [.finish])
        XCTAssertEqual(driver.modifier(59, down: false, at: 6.05), [])
    }

    func testCombinationHoldTimeStartsWhenBothAreDown() {
        var driver = KeyDriver(key: .leftControlOption)
        XCTAssertEqual(driver.modifier(59, down: true, at: 0), [])
        XCTAssertEqual(driver.modifier(58, down: true, at: 2), [.begin(.pushToTalk)])
        // 0.1 s with both held is a tap even though Control was down for 2 s.
        XCTAssertEqual(driver.modifier(58, down: false, at: 2.1), [])
        XCTAssertEqual(driver.machine.nextDeadline, 2.5)
        XCTAssertEqual(driver.modifier(58, down: true, at: 2.3), [.promoteToHandsFree])
    }

    func testCombinationIgnoresRightSideKeys() {
        var driver = KeyDriver(key: .leftControlOption)
        XCTAssertEqual(driver.modifier(62, down: true, at: 0), [])
        XCTAssertEqual(driver.modifier(61, down: true, at: 0.1), [])
        XCTAssertFalse(driver.machine.isKeyDown)
    }

    // MARK: - Controller state

    func testHandsFreeStartedElsewhereEndsOnTap() {
        for key in DictationKey.allCases {
            var driver = KeyDriver(key: key)
            XCTAssertEqual(driver.machine.handle(.recordingChanged(.handsFree, at: 0)), [], "\(key)")
            XCTAssertEqual(driver.machine.recordingMode, .handsFree, "\(key)")
            XCTAssertEqual(driver.press(at: 3), [.finish], "\(key)")
            XCTAssertEqual(driver.release(at: 3.1), [], "\(key)")
        }
    }

    func testHandsFreeStartedElsewhereWorksWithHandsFreeDisabled() {
        var driver = KeyDriver(key: .rightOption, handsFree: false)
        _ = driver.machine.handle(.recordingChanged(.handsFree, at: 0))
        XCTAssertEqual(driver.press(at: 1), [.finish])
    }

    func testStopElsewhereDuringHoldIgnoresTheRelease() {
        var driver = KeyDriver(key: .fn)
        _ = driver.press(at: 0)
        XCTAssertEqual(driver.machine.handle(.recordingChanged(nil, at: 1)), [])
        XCTAssertNil(driver.machine.recordingMode)
        XCTAssertEqual(driver.release(at: 2), [])
        XCTAssertEqual(driver.press(at: 3), [.begin(.pushToTalk)])
    }

    func testStopElsewhereClearsAPendingTap() {
        var driver = KeyDriver(key: .rightOption)
        _ = driver.press(at: 0)
        _ = driver.release(at: 0.1)
        XCTAssertNotNil(driver.machine.nextDeadline)
        _ = driver.machine.handle(.recordingChanged(nil, at: 0.2))
        XCTAssertNil(driver.machine.nextDeadline)
        XCTAssertEqual(driver.machine.handle(.deadline(1)), [])
        XCTAssertEqual(driver.press(at: 0.3), [.begin(.pushToTalk)])
    }

    func testStopElsewhereDuringHandsFree() {
        var driver = KeyDriver(key: .rightCommand)
        _ = driver.machine.handle(.recordingChanged(.handsFree, at: 0))
        _ = driver.machine.handle(.recordingChanged(nil, at: 360))
        XCTAssertEqual(driver.press(at: 361), [.begin(.pushToTalk)])
    }

    func testPushToTalkReportedWhileKeyHeldEndsOnRelease() {
        var driver = KeyDriver(key: .rightOption)
        _ = driver.press(at: 0)
        _ = driver.machine.handle(.recordingChanged(nil, at: 0.5))
        _ = driver.machine.handle(.recordingChanged(.pushToTalk, at: 1))
        XCTAssertEqual(driver.machine.recordingMode, .pushToTalk)
        XCTAssertEqual(driver.release(at: 2), [.finish])
    }

    func testPushToTalkReportedWithoutKeyEndsOnTap() {
        var driver = KeyDriver(key: .rightOption)
        _ = driver.machine.handle(.recordingChanged(.pushToTalk, at: 0))
        XCTAssertEqual(driver.press(at: 1), [.finish])
    }

    func testRecordingChangedIsIdempotent() {
        var driver = KeyDriver(key: .rightOption)
        _ = driver.press(at: 0)
        _ = driver.machine.handle(.recordingChanged(.pushToTalk, at: 0.01))
        XCTAssertEqual(driver.release(at: 1), [.finish])

        _ = driver.press(at: 2)
        _ = driver.release(at: 2.1)
        _ = driver.press(at: 2.2)
        _ = driver.machine.handle(.recordingChanged(.handsFree, at: 2.25))
        XCTAssertEqual(driver.release(at: 2.3), [])
        XCTAssertEqual(driver.press(at: 4), [.finish])
    }

    // MARK: - Recovery

    func testReleaseKeysFinishesAHoldAfterWake() {
        var driver = KeyDriver(key: .rightOption)
        _ = driver.press(at: 0)
        XCTAssertEqual(driver.machine.releaseKeys(at: 30), [.finish])
        XCTAssertFalse(driver.machine.isKeyDown)
        XCTAssertEqual(driver.machine.releaseKeys(at: 31), [])
    }

    func testReleaseKeysExpiresAStalePendingTap() {
        var driver = KeyDriver(key: .rightOption)
        _ = driver.press(at: 0)
        _ = driver.release(at: 0.1)
        XCTAssertEqual(driver.machine.releaseKeys(at: 30), [.discard])
        XCTAssertNil(driver.machine.nextDeadline)
    }

    func testChangingTheKeyDuringAHoldLetsTheNewKeyEndIt() {
        var driver = KeyDriver(key: .rightOption)
        _ = driver.press(at: 0)
        driver.machine.configuration.key = .rightCommand
        driver.key = .rightCommand
        XCTAssertFalse(driver.machine.isKeyDown)
        XCTAssertEqual(driver.modifier(61, down: false, at: 1), [])
        XCTAssertEqual(driver.machine.recordingMode, .handsFree)
        XCTAssertEqual(driver.press(at: 2), [.finish])
    }

    func testChangingOtherSettingsKeepsState() {
        var driver = KeyDriver(key: .rightOption)
        _ = driver.press(at: 0)
        driver.machine.configuration.escapeCancels = false
        XCTAssertTrue(driver.machine.isKeyDown)
        XCTAssertEqual(driver.release(at: 1), [.finish])
    }
}

/// Produces realistic `flagsChanged`/`keyDown` events with left/right modifier bits.
private struct KeyDriver {
    var machine: HotkeyStateMachine
    var key: DictationKey
    private(set) var flags: UInt64 = 0x100
    private var held: Set<UInt16> = []

    init(key: DictationKey, handsFree: Bool = true) {
        self.init(configuration: HotkeyConfiguration(key: key, handsFreeEnabled: handsFree))
    }

    init(configuration: HotkeyConfiguration) {
        machine = HotkeyStateMachine(configuration: configuration)
        key = configuration.key
    }

    /// Presses every key of the dictation key, in order, at the same time.
    mutating func press(at time: TimeInterval) -> [DictationTrigger] {
        key.keyCodes.flatMap { modifier($0, down: true, at: time) }
    }

    mutating func release(at time: TimeInterval) -> [DictationTrigger] {
        key.keyCodes.flatMap { modifier($0, down: false, at: time) }
    }

    mutating func modifier(_ keyCode: UInt16, down: Bool, at time: TimeInterval) -> [DictationTrigger] {
        if down {
            held.insert(keyCode)
        } else {
            held.remove(keyCode)
        }
        flags = Self.flags(for: held)
        return machine.handle(.key(RawKeyEvent(kind: .flagsChanged, keyCode: keyCode, flags: flags, timestamp: time)))
    }

    mutating func keyDown(_ keyCode: UInt16, at time: TimeInterval) -> [DictationTrigger] {
        machine.handle(.key(RawKeyEvent(kind: .keyDown, keyCode: keyCode, flags: flags, timestamp: time)))
    }

    private static func flags(for held: Set<UInt16>) -> UInt64 {
        var value: UInt64 = 0x100
        for keyCode in held {
            switch keyCode {
            case 63: value |= KeyFlagMask.function
            case 61: value |= KeyFlagMask.option | KeyFlagMask.rightOption
            case 58: value |= KeyFlagMask.option | KeyFlagMask.leftOption
            case 54: value |= KeyFlagMask.command | KeyFlagMask.rightCommand
            case 55: value |= KeyFlagMask.command | KeyFlagMask.leftCommand
            case 59: value |= KeyFlagMask.control | KeyFlagMask.leftControl
            case 62: value |= KeyFlagMask.control | KeyFlagMask.rightControl
            default: break
            }
        }
        return value
    }
}
