import Foundation
import XCTest
@testable import SlovoCore

/// Key presses the tap cannot see (Secure Event Input), and Esc while a finished
/// dictation is still being transcribed.
final class InputHotkeySecureInputTests: XCTestCase {
    // MARK: - Unseen key presses

    func testUnseenKeyWhileHeldMakesThePressAShortcut() {
        for key in DictationKey.allCases {
            var driver = FlagsDriver(key: key)
            XCTAssertEqual(driver.press(at: 0), [.begin(.pushToTalk)], "\(key)")
            // Fn+↑ in a secure terminal: only the system counter saw the arrow key.
            XCTAssertEqual(driver.machine.handle(.unseenKeyDown(at: 0.8)), [.discard], "\(key)")
            XCTAssertNil(driver.machine.recordingMode, "\(key)")
            XCTAssertEqual(driver.release(at: 1), [], "a long hold that was a shortcut must not finish")
        }
    }

    func testUnseenKeyDuringPendingTapDiscardsIt() {
        var driver = FlagsDriver(key: .fn)
        XCTAssertEqual(driver.press(at: 0), [.begin(.pushToTalk)])
        XCTAssertEqual(driver.release(at: 0.1), [])
        XCTAssertEqual(driver.machine.handle(.unseenKeyDown(at: 0.2)), [.discard])
        XCTAssertNil(driver.machine.nextDeadline)
        XCTAssertEqual(driver.press(at: 0.3), [.begin(.pushToTalk)], "the next press starts afresh, not hands-free")
    }

    func testUnseenKeyAfterTheTapWindowOnlyExpiresTheTap() {
        var driver = FlagsDriver(key: .fn)
        XCTAssertEqual(driver.press(at: 0), [.begin(.pushToTalk)])
        XCTAssertEqual(driver.release(at: 0.1), [])
        XCTAssertEqual(driver.machine.handle(.unseenKeyDown(at: 5)), [.discard])
        XCTAssertNil(driver.machine.recordingMode)
    }

    func testUnseenKeyIsIgnoredWhenIdleOrHandsFree() {
        var driver = FlagsDriver(key: .rightOption)
        XCTAssertEqual(driver.machine.handle(.unseenKeyDown(at: 0)), [])

        XCTAssertEqual(driver.press(at: 1), [.begin(.pushToTalk)])
        XCTAssertEqual(driver.release(at: 1.1), [])
        XCTAssertEqual(driver.press(at: 1.2), [.promoteToHandsFree])
        XCTAssertEqual(driver.release(at: 1.3), [])
        XCTAssertEqual(driver.machine.handle(.unseenKeyDown(at: 2)), [], "typing during hands-free is fine")
        XCTAssertEqual(driver.machine.recordingMode, .handsFree)
        XCTAssertEqual(driver.press(at: 3), [.finish])
    }

    // MARK: - Esc while processing

    func testEscapeCancelsADictationBeingTranscribed() {
        var driver = FlagsDriver(key: .fn)
        XCTAssertEqual(driver.escape(at: 0), [], "nothing to cancel")
        driver.machine.isProcessing = true
        XCTAssertEqual(driver.escape(at: 1), [.cancel])
        XCTAssertNil(driver.machine.recordingMode)
        driver.machine.isProcessing = false
        XCTAssertEqual(driver.escape(at: 2), [])
    }

    func testEscapeDoesNotCancelProcessingWhenTheSettingIsOff() {
        var driver = FlagsDriver(configuration: HotkeyConfiguration(key: .fn, escapeCancels: false))
        driver.machine.isProcessing = true
        XCTAssertEqual(driver.escape(at: 0), [])
    }

    func testOtherKeysDoNotCancelProcessing() {
        var driver = FlagsDriver(key: .fn)
        driver.machine.isProcessing = true
        XCTAssertEqual(driver.keyDown(0, at: 0), [])
        XCTAssertEqual(driver.machine.handle(.unseenKeyDown(at: 0.1)), [])
    }

    func testProcessingSurvivesAKeyChange() {
        var driver = FlagsDriver(key: .fn)
        driver.machine.isProcessing = true
        driver.machine.configuration = HotkeyConfiguration(key: .rightCommand)
        XCTAssertEqual(driver.escape(at: 0), [.cancel])
    }
}

/// Feeds modifier changes for a dictation key with the flags macOS would report.
private struct FlagsDriver {
    var machine: HotkeyStateMachine
    let key: DictationKey
    private var held: Set<UInt16> = []

    init(key: DictationKey) {
        self.init(configuration: HotkeyConfiguration(key: key))
    }

    init(configuration: HotkeyConfiguration) {
        machine = HotkeyStateMachine(configuration: configuration)
        key = configuration.key
    }

    mutating func press(at time: TimeInterval) -> [DictationTrigger] {
        key.keyCodes.flatMap { change($0, down: true, at: time) }
    }

    mutating func release(at time: TimeInterval) -> [DictationTrigger] {
        key.keyCodes.flatMap { change($0, down: false, at: time) }
    }

    mutating func escape(at time: TimeInterval) -> [DictationTrigger] {
        keyDown(RawKeyEvent.escapeKeyCode, at: time)
    }

    mutating func keyDown(_ keyCode: UInt16, at time: TimeInterval) -> [DictationTrigger] {
        machine.handle(.key(RawKeyEvent(kind: .keyDown, keyCode: keyCode, flags: flags, timestamp: time)))
    }

    private mutating func change(_ keyCode: UInt16, down: Bool, at time: TimeInterval) -> [DictationTrigger] {
        if down {
            held.insert(keyCode)
        } else {
            held.remove(keyCode)
        }
        return machine.handle(.key(RawKeyEvent(kind: .flagsChanged, keyCode: keyCode, flags: flags, timestamp: time)))
    }

    private var flags: UInt64 {
        var value: UInt64 = 0x100
        for keyCode in held {
            switch keyCode {
            case 63: value |= KeyFlagMask.function
            case 61: value |= KeyFlagMask.option | KeyFlagMask.rightOption
            case 58: value |= KeyFlagMask.option | KeyFlagMask.leftOption
            case 54: value |= KeyFlagMask.command | KeyFlagMask.rightCommand
            case 59: value |= KeyFlagMask.control | KeyFlagMask.leftControl
            default: break
            }
        }
        return value
    }
}
