import Foundation
import XCTest
@testable import SlovoCore

/// Every path under a folder with its size, modification and change times, inode and mode.
enum MCPFileSnapshot {
    static func take(_ root: URL) -> [String: String] {
        var paths = [""]
        if let enumerator = FileManager.default.enumerator(atPath: root.path) {
            for case let path as String in enumerator {
                paths.append(path)
            }
        }
        var snapshot: [String: String] = [:]
        for path in paths {
            let full = path.isEmpty ? root.path : root.appendingPathComponent(path).path
            var info = stat()
            guard lstat(full, &info) == 0 else {
                snapshot[path] = "missing"
                continue
            }
            snapshot[path] = "\(info.st_size) \(info.st_mtimespec.tv_sec).\(info.st_mtimespec.tv_nsec) "
                + "\(info.st_ctimespec.tv_sec).\(info.st_ctimespec.tv_nsec) \(info.st_ino) \(info.st_mode)"
        }
        return snapshot
    }
}

private final class MCPDoneFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var done = false

    var isDone: Bool {
        lock.lock()
        defer { lock.unlock() }
        return done
    }

    func finish() {
        lock.lock()
        done = true
        lock.unlock()
    }
}

/// Reading through MCP never changes Slovo's data, and reads stay correct while the app
/// writes. The one tool that does write is `write_meeting_notes`, which is not called here
/// and has its own switch and its own suite.
final class MCPReadOnlyTests: XCTestCase {
    func testNoReadChangesAnythingUnderTheDataFolder() throws {
        let fixture = try MCPFixture()
        defer { fixture.remove() }
        try fixture.writeStandardSet()
        try fixture.writeExtremeTimes()
        try fixture.writeRaw("caff", to: "mic.caf", in: MCPFixture.callID)
        try fixture.writeRaw("caff", to: "system.caf", in: MCPFixture.callID)
        try fixture.writeRaw("{}", to: "meeting.corrupt-20260101-000000.json", in: MCPFixture.coworkID)
        try fixture.writeRaw(TranscriptMerger.markdown(meeting: MCPFixture.coworkRecord(), language: .en),
                             to: "transcript.md", in: MCPFixture.coworkID)
        try FileManager.default.createDirectory(at: fixture.directories.meetingsFolder.appendingPathComponent("not-a-meeting"),
                                                withIntermediateDirectories: true)
        try Data("[]".utf8).write(to: fixture.directories.historyFile)
        let before = MCPFileSnapshot.take(fixture.base)
        XCTAssertGreaterThan(before.count, 20)

        let handler = MCPTestClient.handler(archive: MeetingArchive(directories: fixture.directories),
                                            clock: MCPFakeClock(step: 2))
        let allIDs = [MCPFixture.coworkID, MCPFixture.callID, MCPFixture.standupID, MCPFixture.planningID,
                      MCPFixture.recoveredID, MCPFixture.retroID, MCPFixture.waitingID, MCPFixture.brokenID,
                      MCPFixture.extremeTimesID]
        let references = ["latest", "3f2a9c1e", "3f2a9c1e-5b", "00000000"] + allIDs.map { $0.uuidString.lowercased() }
        var requests: [(String, JSONValue?)] = [
            ("initialize", ["protocolVersion": "2025-11-25"]), ("ping", nil), ("server/discover", nil),
            ("tools/list", nil), ("resources/list", nil), ("resources/templates/list", nil), ("prompts/list", nil),
            ("resources/read", ["uri": "slovo://meetings"]), ("resources/read", ["uri": "slovo://meetings/../../history.json"]),
            ("tools/call", ["name": "list_meetings", "arguments": .object([:])]),
            ("tools/call", ["name": "list_meetings", "arguments": ["since": "7d", "limit": 1, "query": "коворк"]]),
            ("tools/call", ["name": "list_meetings", "arguments": ["limit": "bad"]]),
            ("tools/call", ["name": "search_meetings", "arguments": ["query": "прототип"]]),
            ("tools/call", ["name": "list_highlights", "arguments": .object([:])])
        ]
        for reference in references {
            let id = JSONValue.string(reference)
            requests += [
                ("tools/call", ["name": "get_meeting", "arguments": ["meeting_id": id]]),
                ("tools/call", ["name": "get_transcript", "arguments": ["meeting_id": id, "max_chars": 2000]]),
                ("tools/call", ["name": "search_meetings", "arguments": ["query": "отчёт", "meeting_id": id]]),
                ("tools/call", ["name": "list_highlights", "arguments": ["meeting_id": id]]),
                ("prompts/get", ["name": "meeting_tasks", "arguments": ["meeting_id": id]]),
                ("prompts/get", ["name": "meeting_summary", "arguments": ["meeting_id": id, "detail": "full"]]),
                ("resources/read", ["uri": .string("slovo://meetings/\(reference)/transcript.md")])
            ]
        }
        for (method, params) in requests {
            for modern in [false, true] {
                let reply = MCPTestClient.send(handler, MCPTestClient.request(method: method, params: params, modern: modern))
                XCTAssertNotNil(reply, method)
            }
        }
        _ = MCPTestClient.send(handler, raw: #"{"jsonrpc":"2.0","method":"notifications/initialized"}"#)

        XCTAssertEqual(MCPFileSnapshot.take(fixture.base), before)
    }

    func testSymbolicLinksAreNotFollowed() throws {
        let fixture = try MCPFixture()
        defer { fixture.remove() }
        let outside = fixture.base.appendingPathComponent("outside", isDirectory: true)
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        try encoder.encode(MCPFixture.coworkRecord()).write(to: outside.appendingPathComponent("meeting.json"))

        let linkedFolder = UUID()
        try FileManager.default.createSymbolicLink(at: fixture.folder(linkedFolder), withDestinationURL: outside)
        let linkedFile = UUID()
        try FileManager.default.createDirectory(at: fixture.folder(linkedFile), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: fixture.recordURL(linkedFile),
                                                   withDestinationURL: outside.appendingPathComponent("meeting.json"))

        let archive = MeetingArchive(directories: fixture.directories)
        XCTAssertTrue(archive.meetings().isEmpty)
        XCTAssertNil(archive.meeting(id: linkedFolder))
        XCTAssertNil(archive.meeting(id: linkedFile))
    }

    func testArchiveFollowsChangesSkipsHugeFilesAndForgetsDeletedMeetings() throws {
        let fixture = try MCPFixture()
        defer { fixture.remove() }
        try fixture.write(MCPFixture.coworkRecord())
        let archive = MeetingArchive(directories: fixture.directories)
        XCTAssertEqual(archive.meetings().first?.record.title, MCPFixture.coworkTitle)
        XCTAssertEqual(archive.meetings().first?.record.title, MCPFixture.coworkTitle)
        try fixture.write(MCPFixture.coworkRecord(title: "Новое название"))
        XCTAssertEqual(archive.meetings().first?.record.title, "Новое название")

        let hugeID = UUID()
        try FileManager.default.createDirectory(at: fixture.folder(hugeID), withIntermediateDirectories: true)
        XCTAssertTrue(FileManager.default.createFile(atPath: fixture.recordURL(hugeID).path, contents: nil))
        let handle = try FileHandle(forWritingTo: fixture.recordURL(hugeID))
        try handle.truncate(atOffset: UInt64(MeetingArchive.maximumFileBytes + 1))
        try handle.close()
        XCTAssertNil(archive.meeting(id: hugeID))
        try fixture.writeRaw("# Huge\n\n[00:01] Me: Текст.\n", to: "transcript.md", in: hugeID)
        XCTAssertEqual(archive.meeting(id: hugeID)?.record.title, "Huge")

        let handler = MCPTestClient.handler(archive: archive)
        let id = MCPFixture.coworkID.uuidString.lowercased()
        XCTAssertFalse(MCPTestClient.tool(handler, "get_meeting", ["meeting_id": .string(id)]).isError)
        try FileManager.default.removeItem(at: fixture.folder(MCPFixture.coworkID))
        let missing = MCPTestClient.tool(handler, "get_meeting", ["meeting_id": .string(id)])
        XCTAssertTrue(missing.isError)
        XCTAssertEqual(missing.text, "No meeting matches “\(id)”.")
        XCTAssertEqual(archive.meetings().map(\.record.id), [hugeID])
    }

    func testLinkedMeetingsRootIsNotRead() throws {
        let fixture = try MCPFixture()
        defer { fixture.remove() }
        try fixture.write(MCPFixture.coworkRecord())
        let archive = MeetingArchive(directories: fixture.directories)
        XCTAssertEqual(archive.meetings().count, 1)
        let outside = fixture.base.appendingPathComponent("external-meetings", isDirectory: true)
        try FileManager.default.moveItem(at: fixture.directories.meetingsFolder, to: outside)
        try FileManager.default.createSymbolicLink(at: fixture.directories.meetingsFolder, withDestinationURL: outside)

        XCTAssertTrue(archive.meetings().isEmpty, "Even cached records must not escape through a linked Meetings root")
        XCTAssertNil(archive.meeting(id: MCPFixture.coworkID))
        XCTAssertNil(MeetingRecordFile.read(directories: fixture.directories, id: MCPFixture.coworkID))
    }

    func testReadsNeverFailWhileTheAppRewritesTheRecord() throws {
        let fixture = try MCPFixture()
        defer { fixture.remove() }
        try fixture.write(MCPFixture.coworkRecord())
        let handler = MCPTestClient.handler(archive: MeetingArchive(directories: fixture.directories),
                                            clock: MCPFakeClock(step: 10))
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        var longer = MCPFixture.coworkRecord(title: "Коворк B")
        longer.segments += (0..<50).map {
            TranscriptSegment(start: 2_000 + Double($0) * 10, end: 2_005 + Double($0) * 10, speaker: .me,
                              text: "Дополнительная строка \($0).", speakerID: "s3")
        }
        let variants = [try encoder.encode(MCPFixture.coworkRecord(title: "Коворк A")), try encoder.encode(longer)]
        let url = fixture.recordURL(MCPFixture.coworkID)
        let done = MCPDoneFlag()
        let writer = Thread {
            for index in 0..<200 {
                try? variants[index % 2].write(to: url, options: .atomic)
            }
            done.finish()
        }
        writer.start()

        var iterations = 0
        while !done.isDone || iterations < 20 {
            let list = MCPTestClient.send(handler, MCPTestClient.request(
                method: "tools/call", params: ["name": "list_meetings", "arguments": .object([:])]))
            XCTAssertNil(list?["error"])
            XCTAssertNil(list?["result"]?["isError"])
            XCTAssertEqual(list?["result"]?["structuredContent"]?["meetings"]?.arrayValue?.count, 1)
            let transcript = MCPTestClient.send(handler, MCPTestClient.request(
                method: "tools/call", params: ["name": "get_transcript", "arguments": ["meeting_id": "latest", "max_chars": 2000]]))
            XCTAssertNil(transcript?["error"])
            XCTAssertNil(transcript?["result"]?["isError"], transcript?.serializedString ?? "")
            iterations += 1
        }
        XCTAssertGreaterThanOrEqual(iterations, 20)
    }
}
