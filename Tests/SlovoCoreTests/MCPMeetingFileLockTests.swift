import Foundation
import XCTest
@testable import SlovoCore

/// The app and the MCP server both read, change and replace one `meeting.json`. These tests
/// start one writer while the other is inside its read-modify-write and check that the
/// second waits for the first, so neither change is undone.
final class MCPMeetingFileLockTests: XCTestCase {
    private var fixture: MCPFixture!

    override func setUpWithError() throws {
        fixture = try MCPFixture()
        try fixture.write(MCPFixture.coworkRecord())
    }

    override func tearDown() {
        MeetingFileLock.afterLockingForTests = nil
        fixture.remove()
    }

    private func storedRecord() throws -> MeetingRecord {
        try JSONDecoder().decode(MeetingRecord.self, from: Data(contentsOf: fixture.recordURL(MCPFixture.coworkID)))
    }

    private static let notes = MeetingNotes(summary: "Итоги встречи.", decisions: ["Выпуск в пятницу"],
                                            writtenAt: MCPFixture.now)

    /// Claude writes notes while the app is inside a save: the notes wait for the save to
    /// finish, then land on top of what it wrote.
    @MainActor
    func testANotesWriteStartedInsideAStoreSaveWaitsForIt() throws {
        let store = MeetingStore(directories: fixture.directories)
        var renamed = MCPFixture.coworkRecord()
        renamed.title = "Коворк, переименован"
        store.update(renamed)

        let folder = fixture.folder(MCPFixture.coworkID).standardizedFileURL
        let writer = MeetingNotesFile(directories: fixture.directories)
        let finished = DispatchSemaphore(value: 0)
        var started = false
        var notesDuringSave: MeetingNotes?
        MeetingFileLock.afterLockingForTests = { [weak self] locked in
            // Only the store's save, which runs on the main thread, starts the other writer.
            guard Thread.isMainThread, !started, locked.standardizedFileURL == folder else { return }
            started = true
            Thread {
                _ = try? writer.write(Self.notes, to: MCPFixture.coworkID)
                finished.signal()
            }.start()
            // Long enough for the notes writer to reach the lock; it must not get past it.
            Thread.sleep(forTimeInterval: 0.3)
            notesDuringSave = try? self?.storedRecord().claudeNotes
        }

        try store.flush()
        XCTAssertTrue(started, "the store's save took the meeting's lock")
        XCTAssertNil(notesDuringSave, "the notes did not land while the app was saving")
        XCTAssertEqual(finished.wait(timeout: .now() + 10), .success)

        let stored = try storedRecord()
        XCTAssertEqual(stored.title, "Коворк, переименован", "the notes write started from the app's save")
        XCTAssertEqual(stored.claudeNotes?.summary, Self.notes.summary)
    }

    /// The app saves a rename while the MCP server is between reading the record and writing
    /// its notes back. Without a shared lock the server's write would restore the old title.
    @MainActor
    func testAStoreSaveStartedInsideANotesWriteWaitsForIt() throws {
        let store = MeetingStore(directories: fixture.directories)
        let directories = fixture.directories
        let insideWrite = DispatchSemaphore(value: 0)
        let finished = DispatchSemaphore(value: 0)
        Thread {
            _ = try? MeetingRecordFile.update(directories: directories, id: MCPFixture.coworkID) { record -> (result: Void, changed: Bool) in
                insideWrite.signal()
                // The app saves now, while this writer holds the record it read.
                Thread.sleep(forTimeInterval: 0.3)
                record.claudeNotes = Self.notes
                return ((), true)
            }
            finished.signal()
        }.start()
        XCTAssertEqual(insideWrite.wait(timeout: .now() + 10), .success)

        var renamed = MCPFixture.coworkRecord()
        renamed.title = "Коворк, переименован"
        store.update(renamed)
        try store.flush()
        XCTAssertEqual(finished.wait(timeout: .now() + 10), .success)

        let stored = try storedRecord()
        XCTAssertEqual(stored.title, "Коворк, переименован", "the rename was saved after the notes, not under them")
        XCTAssertEqual(stored.claudeNotes?.summary, Self.notes.summary, "the save kept the notes it waited for")
        XCTAssertEqual(store.meeting(id: MCPFixture.coworkID)?.claudeNotes?.summary, Self.notes.summary)
    }

    func testTheLockIsExclusiveAndReleasedAfterward() throws {
        let folder = fixture.folder(MCPFixture.coworkID)
        try MeetingFileLock.withLock(meetingFolder: folder) {
            XCTAssertThrowsError(try MeetingFileLock.withLock(meetingFolder: folder, timeout: 0.05) {}) { error in
                XCTAssertEqual(error as? MeetingFileLock.LockError, .timedOut)
            }
        }
        XCTAssertNoThrow(try MeetingFileLock.withLock(meetingFolder: folder, timeout: 0.05) {})
    }

    func testAMissingMeetingIsUnreadableRatherThanALockFailure() {
        let missing = MCPFixture.uuid("0BADF00D-0000-4000-8000-000000000000")
        let writer = MeetingNotesFile(directories: fixture.directories)
        XCTAssertThrowsError(try writer.write(Self.notes, to: missing)) { error in
            XCTAssertEqual(error as? MeetingNotesWriteError, .unreadable)
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: fixture.folder(missing).path),
                       "taking the lock never creates a meeting folder")
    }

    func testReplacingTheMeetingDirectoryCannotRedirectAnInFlightWrite() throws {
        let original = fixture.folder(MCPFixture.coworkID)
        let moved = fixture.base.appendingPathComponent("original-meeting", isDirectory: true)
        let outside = fixture.base.appendingPathComponent("external-meeting", isDirectory: true)
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
        let externalRecord = outside.appendingPathComponent("meeting.json")
        let externalData = try JSONEncoder().encode(MCPFixture.coworkRecord(title: "Must stay untouched"))
        try externalData.write(to: externalRecord)

        try MeetingRecordFile.update(directories: fixture.directories, id: MCPFixture.coworkID) {
            record -> (result: Void, changed: Bool) in
            // Simulate another process swapping the path after the read and lock.
            try? FileManager.default.moveItem(at: original, to: moved)
            try? FileManager.default.createSymbolicLink(at: original, withDestinationURL: outside)
            record.title = "Updated original"
            return ((), true)
        }
        XCTAssertEqual(try Data(contentsOf: externalRecord), externalData)
        let updated = try JSONDecoder().decode(MeetingRecord.self,
                                               from: Data(contentsOf: moved.appendingPathComponent("meeting.json")))
        XCTAssertEqual(updated.title, "Updated original")
    }

    func testALinkedMeetingDirectoryIsRefusedBeforeCreatingALock() throws {
        let original = fixture.folder(MCPFixture.coworkID)
        let outside = fixture.base.appendingPathComponent("external-meeting", isDirectory: true)
        try FileManager.default.moveItem(at: original, to: outside)
        try FileManager.default.createSymbolicLink(at: original, withDestinationURL: outside)
        let snapshot = MCPFileSnapshot.take(outside)
        XCTAssertThrowsError(try MeetingNotesFile(directories: fixture.directories).write(Self.notes, to: MCPFixture.coworkID))
        XCTAssertEqual(MCPFileSnapshot.take(outside), snapshot)
    }

    /// A writer that cannot get the lock in time refuses rather than risking the race.
    func testAWriterThatCannotGetTheLockFails() throws {
        let folder = fixture.folder(MCPFixture.coworkID)
        let holding = DispatchSemaphore(value: 0)
        let release = DispatchSemaphore(value: 0)
        let finished = DispatchSemaphore(value: 0)
        Thread {
            try? MeetingFileLock.withLock(meetingFolder: folder) {
                holding.signal()
                _ = release.wait(timeout: .now() + 20)
            }
            finished.signal()
        }.start()
        XCTAssertEqual(holding.wait(timeout: .now() + 10), .success)
        XCTAssertThrowsError(try MeetingRecordFile.update(directories: fixture.directories, id: MCPFixture.coworkID,
                                                          timeout: 0.1) { record -> (result: Void, changed: Bool) in
            record.title = "Не должно попасть в файл"
            return ((), true)
        }) { error in
            XCTAssertEqual(error as? MeetingRecordFile.UpdateError, .failed)
        }
        release.signal()
        XCTAssertEqual(finished.wait(timeout: .now() + 10), .success)
        XCTAssertEqual(try storedRecord().title, MCPFixture.coworkTitle, "nothing was written")
    }
}
