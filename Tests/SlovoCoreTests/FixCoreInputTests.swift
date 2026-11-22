import AppKit
import Foundation
import XCTest
@testable import SlovoCore

/// Keys pressed together with the dictation key, and pastes that overlap.
final class FixCoreHotkeyChordTests: XCTestCase {
    private static let fnDown: UInt64 = 0x100 | KeyFlagMask.function
    private static let fnUp: UInt64 = 0x100
    private static let rightCommandDown: UInt64 = 0x100 | KeyFlagMask.command | KeyFlagMask.rightCommand
    /// macOS sets the numeric pad flag on arrow keys.
    private static let numericPad: UInt64 = 0x20_0000
    /// kVK_DownArrow.
    private static let downArrow: UInt16 = 125
    /// kVK_Tab.
    private static let tab: UInt16 = 48

    private func flags(_ keyCode: UInt16, _ flags: UInt64, at time: TimeInterval) -> HotkeyInput {
        .key(RawKeyEvent(kind: .flagsChanged, keyCode: keyCode, flags: flags, timestamp: time))
    }

    private func keyDown(_ keyCode: UInt16, _ flags: UInt64, at time: TimeInterval) -> HotkeyInput {
        .key(RawKeyEvent(kind: .keyDown, keyCode: keyCode, flags: flags, timestamp: time))
    }

    /// Fn+Down (Page Down) on a MacBook keyboard, with Fn held a moment before the arrow.
    func testFnNavigationChordIsNotADictation() {
        var machine = HotkeyStateMachine(configuration: HotkeyConfiguration())
        XCTAssertEqual(machine.configuration.key, .fn, "Fn is the default dictation key")
        var triggers: [DictationTrigger] = []
        triggers += machine.handle(flags(63, Self.fnDown, at: 0))
        triggers += machine.handle(keyDown(Self.downArrow, Self.fnDown | Self.numericPad, at: 0.4))
        triggers += machine.handle(keyDown(Self.downArrow, Self.fnDown | Self.numericPad, at: 0.6))
        triggers += machine.handle(flags(63, Self.fnUp, at: 0.9))
        XCTAssertEqual(triggers, [.begin(.pushToTalk), .discard])
        XCTAssertNil(machine.recordingMode)

        // The next ordinary hold dictates again.
        XCTAssertEqual(machine.handle(flags(63, Self.fnDown, at: 2)), [.begin(.pushToTalk)])
        XCTAssertEqual(machine.handle(flags(63, Self.fnUp, at: 3)), [.finish])
    }

    /// Right Command held for a moment, then Tab to switch apps.
    func testRightCommandTabAfterALongHoldIsNotADictation() {
        var machine = HotkeyStateMachine(configuration: HotkeyConfiguration(key: .rightCommand))
        var triggers = machine.handle(flags(54, Self.rightCommandDown, at: 0))
        triggers += machine.handle(keyDown(Self.tab, Self.rightCommandDown, at: 0.7))
        triggers += machine.handle(keyDown(Self.tab, Self.rightCommandDown, at: 1.2))
        triggers += machine.handle(flags(54, 0x100, at: 1.5))
        XCTAssertEqual(triggers, [.begin(.pushToTalk), .discard])
    }

    func testChordDuringHandsFreeKeepsRecording() {
        var machine = HotkeyStateMachine(configuration: HotkeyConfiguration())
        _ = machine.handle(.recordingChanged(.handsFree, at: 0))
        XCTAssertEqual(machine.handle(keyDown(Self.downArrow, Self.fnDown, at: 1)), [])
        XCTAssertEqual(machine.recordingMode, .handsFree)
    }

    func testDefaultConfigurationMatchesTheSpec() {
        let configuration = HotkeyConfiguration()
        XCTAssertEqual(configuration.key, .fn)
        XCTAssertEqual(configuration.minimumHold, 0.25)
        XCTAssertEqual(configuration.doubleTapWindow, 0.4)
        XCTAssertTrue(configuration.handsFreeEnabled)
        XCTAssertTrue(configuration.escapeCancels)
    }
}

@MainActor
final class FixCoreTextInsertionTests: XCTestCase {
    /// Every test runs on a fresh instance, so each gets a pasteboard of its own.
    private let pasteboard = NSPasteboard.withUniqueName()

    override func setUp() async throws {
        pasteboard.clearContents()
        pasteboard.setString("USER CLIPBOARD", forType: .string)
    }

    override func tearDown() async throws {
        pasteboard.releaseGlobally()
    }

    /// A dictation's paste and "Paste last transcript" overlapping inside the 60 ms chord.
    func testOverlappingPastesRestoreTheUsersClipboard() async throws {
        let inserter = TextInserter(permissions: DictationFakes.Permissions(), pasteboard: pasteboard,
                                    restoreDelay: .milliseconds(300)) {
            try? await Task.sleep(for: .milliseconds(60))
            return true
        }
        let first = Task { await inserter.insert("first dictation") }
        try await Task.sleep(for: .milliseconds(15))
        let second = Task { await inserter.insert("pasted again") }
        let firstResult = await first.value
        let secondResult = await second.value
        XCTAssertEqual(firstResult, .pasted)
        XCTAssertEqual(secondResult, .pasted)
        XCTAssertEqual(pasteboard.string(forType: .string), "pasted again")

        try await Task.sleep(for: .milliseconds(700))
        XCTAssertEqual(pasteboard.string(forType: .string), "USER CLIPBOARD")
    }

    /// The second paste writes the pasteboard only after the first chord was released, so
    /// the first Command+V never pastes the second text.
    func testOverlappingPastesEachPasteTheirOwnText() async throws {
        let seen = SeenTexts()
        let pasteboard = self.pasteboard
        let inserter = TextInserter(permissions: DictationFakes.Permissions(), pasteboard: pasteboard,
                                    restoreDelay: .milliseconds(100)) {
            seen.texts.append(pasteboard.string(forType: .string) ?? "")
            try? await Task.sleep(for: .milliseconds(60))
            seen.texts.append(pasteboard.string(forType: .string) ?? "")
            return true
        }
        let first = Task { await inserter.insert("first dictation") }
        try await Task.sleep(for: .milliseconds(15))
        let second = Task { await inserter.insert("pasted again") }
        _ = await first.value
        _ = await second.value
        XCTAssertEqual(seen.texts, ["first dictation", "first dictation", "pasted again", "pasted again"])

        try await Task.sleep(for: .milliseconds(400))
        XCTAssertEqual(pasteboard.string(forType: .string), "USER CLIPBOARD")
    }

    @MainActor
    private final class SeenTexts {
        var texts: [String] = []
    }
}
