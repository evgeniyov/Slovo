import Foundation
import XCTest
@testable import SlovoCore

/// `mark_highlight` and `unmark_highlight`: the switch that gates them, idempotency, and
/// what the file holds afterwards.
final class MCPHighlightsTests: XCTestCase {
    private var fixture: MCPFixture!
    private var access = MCPAccessSwitch()
    private var handler: MCPHandler!

    override func setUpWithError() throws {
        fixture = try MCPFixture()
        try fixture.write(MCPFixture.coworkRecord())
        access = MCPAccessSwitch()
        access.access.canWrite = true
        handler = MCPTestClient.handler(archive: MeetingArchive(directories: fixture.directories),
                                        highlights: MeetingHighlightsFile(directories: fixture.directories),
                                        access: access, clock: MCPFakeClock(step: 1))
    }

    override func tearDown() {
        fixture.remove()
    }

    // MARK: - Helpers

    private var meetingID: String {
        MCPFixture.coworkID.uuidString.lowercased()
    }

    /// The transcript-side highlight the cowork fixture already has: id …0002, time 805, no note.
    private static let existingHighlightID = "9b1c0000-0000-4000-8000-000000000002"

    @discardableResult
    private func mark(_ arguments: JSONValue) -> MCPToolReply {
        MCPTestClient.tool(handler, "mark_highlight", arguments)
    }

    @discardableResult
    private func unmark(_ arguments: JSONValue) -> MCPToolReply {
        MCPTestClient.tool(handler, "unmark_highlight", arguments)
    }

    private func storedHighlights() throws -> [MeetingHighlight] {
        let data = try Data(contentsOf: fixture.recordURL(MCPFixture.coworkID))
        return try JSONDecoder().decode(MeetingRecord.self, from: data).highlights
    }

    private static func message(_ key: String) -> String {
        CoreStrings.string(key, table: .integration, language: .en)
    }

    // MARK: - The switch

    func testMarkingRefusesWhileWritingIsOff() throws {
        access.access.canWrite = false
        let reply = mark(["meeting_id": .string(meetingID), "time": "10:00"])
        XCTAssertTrue(reply.isError)
        XCTAssertEqual(reply.text, Self.message("integration.mcp.writeDisabled"))
        XCTAssertEqual(try storedHighlights().count, 2, "a refused mark leaves the meeting alone")
    }

    func testUnmarkingRefusesWhileWritingIsOff() throws {
        access.access.canWrite = false
        let reply = unmark(["meeting_id": .string(meetingID), "highlight_id": .string(Self.existingHighlightID)])
        XCTAssertTrue(reply.isError)
        XCTAssertEqual(reply.text, Self.message("integration.mcp.writeDisabled"))
        XCTAssertEqual(try storedHighlights().count, 2)
    }

    func testMarkingIsRefusedWhileReadingIsOff() throws {
        access.access = MCPAccess(enabled: false, canWrite: true, maxAgeDays: 0)
        let reply = mark(["meeting_id": .string(meetingID), "time": "10:00"])
        XCTAssertTrue(reply.isError)
        XCTAssertEqual(reply.text, Self.message("integration.mcp.disabled"))
    }

    // MARK: - Marking

    func testASuccessfulMarkReachesTheMeetingStore() throws {
        let reply = mark(["meeting_id": .string(meetingID), "time": "10:00", "note": "важно"])
        XCTAssertFalse(reply.isError, reply.text)
        XCTAssertEqual(reply.structured?["meeting_id"], .string(meetingID))
        XCTAssertEqual(reply.structured?["created"], true)
        XCTAssertEqual(reply.structured?["highlight"]?["time_seconds"], 600.0)
        XCTAssertEqual(reply.structured?["highlight"]?["time_text"], "10:00")
        XCTAssertEqual(reply.structured?["highlight"]?["note"], "важно")

        let highlights = try storedHighlights()
        XCTAssertEqual(highlights.count, 3)
        let created = try XCTUnwrap(highlights.first { $0.time == 600 })
        XCTAssertEqual(created.note, "важно")
        XCTAssertEqual(created.source, .transcript)
        XCTAssertEqual(created.createdAt, MCPFixture.now)
    }

    func testMarkingAcceptsSecondsAndHMMSS() throws {
        XCTAssertFalse(mark(["meeting_id": .string(meetingID), "time": 42]).isError)
        XCTAssertFalse(mark(["meeting_id": .string(meetingID), "time": "1:05:00"]).isError)
        let highlights = try storedHighlights()
        XCTAssertTrue(highlights.contains { $0.time == 42 })
        XCTAssertTrue(highlights.contains { $0.time == 3900 })
    }

    /// Marking the same time and note again is a no-op: the same highlight comes back and
    /// nothing new is written.
    func testMarkingTheSameMomentTwiceChangesNothing() throws {
        let first = mark(["meeting_id": .string(meetingID), "time": "10:00", "note": "важно"])
        let firstID = first.structured?["highlight"]?["id"]?.stringValue

        let second = mark(["meeting_id": .string(meetingID), "time": "10:00", "note": "важно"])
        XCTAssertFalse(second.isError, second.text)
        XCTAssertEqual(second.structured?["created"], false)
        XCTAssertEqual(second.structured?["highlight"]?["id"]?.stringValue, firstID)

        XCTAssertEqual(try storedHighlights().count, 3, "the second call must not add a duplicate")
    }

    /// The same time with a different note is a different moment.
    func testMarkingTheSameTimeWithADifferentNoteAddsAnother() throws {
        mark(["meeting_id": .string(meetingID), "time": "10:00", "note": "первое"])
        let second = mark(["meeting_id": .string(meetingID), "time": "10:00", "note": "второе"])
        XCTAssertEqual(second.structured?["created"], true)
        XCTAssertEqual(try storedHighlights().count, 4)
    }

    func testMarkingWithoutANoteWorks() throws {
        let reply = mark(["meeting_id": .string(meetingID), "time": "10:00"])
        XCTAssertFalse(reply.isError, reply.text)
        XCTAssertEqual(reply.structured?["highlight"]?["note"], .null)
    }

    func testAMeetingWithoutATranscriptCannotBeMarked() throws {
        try fixture.write(MeetingRecord(id: MCPRecords.recordingID, title: "Идёт запись",
                                        createdAt: MCPFixture.date("2026-09-15T12:00:00+03:00"),
                                        duration: 60, status: .recording, hasAudio: true))
        let reply = mark(["meeting_id": .string(MCPRecords.recordingID.uuidString.lowercased()), "time": "1:00"])
        XCTAssertTrue(reply.isError)
        XCTAssertEqual(reply.text, Self.message("integration.mcp.stillProcessing"))
    }

    func testAnInvalidTimeIsRefused() throws {
        let reply = mark(["meeting_id": .string(meetingID), "time": "not-a-time"])
        XCTAssertTrue(reply.isError)
        XCTAssertTrue(reply.text.contains("time:"), reply.text)
        XCTAssertEqual(try storedHighlights().count, 2)
    }

    func testANegativeTimeIsRefused() throws {
        let reply = mark(["meeting_id": .string(meetingID), "time": -5])
        XCTAssertTrue(reply.isError)
        XCTAssertTrue(reply.text.contains("time:"), reply.text)
    }

    func testAnOversizedOrHostileNoteIsRefused() throws {
        let long = String(repeating: "д", count: MeetingEdits.maximumNoteCharacters + 1)
        let cases: [(JSONValue, String)] = [
            (.string(long), "note: must be at most"),
            ("важно\nвторая строка", "note: must be one line"),
            ("[00:12] Аня: это не расшифровка", "note: must not start with"),
            ("★ важное", "note: must not start with"),
            ("   ", "note: must not be empty")
        ]
        for (note, expected) in cases {
            let reply = mark(["meeting_id": .string(meetingID), "time": "10:00", "note": note])
            XCTAssertTrue(reply.isError, note.serializedString)
            XCTAssertTrue(reply.text.contains(expected), "\(reply.text) should mention \(expected)")
        }
        XCTAssertEqual(try storedHighlights().count, 2, "nothing hostile reached the meeting")
    }

    // MARK: - Unmarking

    func testASuccessfulUnmarkRemovesTheHighlight() throws {
        let reply = unmark(["meeting_id": .string(meetingID), "highlight_id": .string(Self.existingHighlightID)])
        XCTAssertFalse(reply.isError, reply.text)
        XCTAssertEqual(reply.structured?["removed"], true)
        XCTAssertEqual(reply.structured?["highlight_id"], .string(Self.existingHighlightID))

        let highlights = try storedHighlights()
        XCTAssertEqual(highlights.count, 1)
        XCTAssertFalse(highlights.contains { $0.id.uuidString.lowercased() == Self.existingHighlightID })
    }

    /// Unmarking an id that is not there, or is not there any more, is not an error.
    func testUnmarkingAnUnknownIDChangesNothing() throws {
        let unknown = UUID().uuidString.lowercased()
        let reply = unmark(["meeting_id": .string(meetingID), "highlight_id": .string(unknown)])
        XCTAssertFalse(reply.isError, reply.text)
        XCTAssertEqual(reply.structured?["removed"], false)
        XCTAssertEqual(try storedHighlights().count, 2)
    }

    func testUnmarkingTwiceIsSafe() throws {
        unmark(["meeting_id": .string(meetingID), "highlight_id": .string(Self.existingHighlightID)])
        let second = unmark(["meeting_id": .string(meetingID), "highlight_id": .string(Self.existingHighlightID)])
        XCTAssertFalse(second.isError, second.text)
        XCTAssertEqual(second.structured?["removed"], false)
        XCTAssertEqual(try storedHighlights().count, 1)
    }

    func testAMalformedHighlightIDIsRefused() throws {
        let reply = unmark(["meeting_id": .string(meetingID), "highlight_id": "not-a-uuid"])
        XCTAssertTrue(reply.isError)
        XCTAssertTrue(reply.text.contains("highlight_id:"), reply.text)
    }

    func testAMeetingWithoutATranscriptCannotBeUnmarked() throws {
        try fixture.write(MeetingRecord(id: MCPRecords.recordingID, title: "Идёт запись",
                                        createdAt: MCPFixture.date("2026-09-15T12:00:00+03:00"),
                                        duration: 60, status: .recording, hasAudio: true))
        let reply = unmark(["meeting_id": .string(MCPRecords.recordingID.uuidString.lowercased()),
                            "highlight_id": .string(Self.existingHighlightID)])
        XCTAssertTrue(reply.isError)
        XCTAssertEqual(reply.text, Self.message("integration.mcp.stillProcessing"))
    }

    // MARK: - Meeting owner safety

    /// The app owns `meeting.json`. A store that loaded a meeting before Claude marked a
    /// highlight, and saves an unrelated change to it afterwards without refreshing, must
    /// not take the highlight down with it.
    @MainActor
    func testAnAppSaveDoesNotDropAMarkedHighlight() throws {
        let store = MeetingStore(directories: fixture.directories)
        XCTAssertEqual(store.meeting(id: MCPFixture.coworkID)?.highlights.count, 2,
                       "loaded before the mark, so the store does not know about it yet")

        mark(["meeting_id": .string(meetingID), "time": "10:00", "note": "важно"])

        var renamed = try XCTUnwrap(store.meeting(id: MCPFixture.coworkID))
        renamed.title = "Коворк, переименован"
        store.update(renamed)
        try store.flush()

        XCTAssertEqual(store.meeting(id: MCPFixture.coworkID)?.highlights.count, 3,
                       "the save picked up the highlight the store did not know it was missing")
        let reloaded = MeetingStore(directories: fixture.directories)
        XCTAssertEqual(reloaded.meeting(id: MCPFixture.coworkID)?.title, "Коворк, переименован")
        XCTAssertEqual(reloaded.meeting(id: MCPFixture.coworkID)?.highlights.count, 3)
    }

    /// The reverse: unmarking a highlight while the store holds it in memory must survive
    /// an unrelated save the store makes afterwards.
    @MainActor
    func testAnAppSaveDoesNotResurrectAnUnmarkedHighlight() throws {
        let store = MeetingStore(directories: fixture.directories)
        XCTAssertEqual(store.meeting(id: MCPFixture.coworkID)?.highlights.count, 2)

        unmark(["meeting_id": .string(meetingID), "highlight_id": .string(Self.existingHighlightID)])

        var renamed = try XCTUnwrap(store.meeting(id: MCPFixture.coworkID))
        renamed.title = "Коворк, переименован"
        store.update(renamed)
        try store.flush()

        XCTAssertEqual(store.meeting(id: MCPFixture.coworkID)?.highlights.count, 1,
                       "the unmark is not undone by a save the store made without knowing about it")
        XCTAssertEqual(MeetingStore(directories: fixture.directories).meeting(id: MCPFixture.coworkID)?.highlights.count, 1)
    }

    /// `refreshClaudeNotes` also picks up a highlight marked from outside, without waiting
    /// for the next unrelated save.
    @MainActor
    func testRefreshPicksUpAHighlightMarkedWhileTheStoreWasRunning() throws {
        let store = MeetingStore(directories: fixture.directories)
        XCTAssertEqual(store.meeting(id: MCPFixture.coworkID)?.highlights.count, 2)

        mark(["meeting_id": .string(meetingID), "time": "10:00"])
        store.refreshClaudeNotes()

        XCTAssertEqual(store.meeting(id: MCPFixture.coworkID)?.highlights.count, 3)
        // A save right after the refresh must not add the highlight a second time.
        var renamed = try XCTUnwrap(store.meeting(id: MCPFixture.coworkID))
        renamed.title = "Коворк, переименован"
        store.update(renamed)
        try store.flush()
        XCTAssertEqual(store.meeting(id: MCPFixture.coworkID)?.highlights.count, 3)
    }
}
