import Foundation
import XCTest
@testable import SlovoCore

/// A temporary Slovo data folder with meetings of every kind the MCP server must handle,
/// built in code because the test target has no resources.
final class MCPFixture {
    static let timeZone = TimeZone(identifier: "Europe/Moscow") ?? TimeZone(secondsFromGMT: 3 * 3600) ?? .current
    static let now = date("2026-09-15T18:04:00+03:00")

    /// A v2 in-person meeting with separated, renamed speakers and two highlights.
    static let coworkID = uuid("3F2A9C1E-5B7D-4C1A-9E0F-2B6D8A4C7E11")
    /// Newer than the cowork meeting and still transcribing.
    static let callID = uuid("7B1D2E3F-1111-4222-8333-444455556666")
    /// Stuck in `recording`.
    static let standupID = uuid("11111111-2222-4333-8444-555566667777")
    /// A first-format file whose id shares its first 8 characters with the cowork meeting.
    static let planningID = uuid("3F2A9C1E-AAAA-4BBB-8CCC-000000000004")
    /// A damaged `meeting.json` next to a readable `transcript.md`.
    static let recoveredID = uuid("A0A0A0A0-B1B1-4C2C-8D3D-E4E4E4E4E4E4")
    /// 200 days old.
    static let retroID = uuid("C0FFEE00-0000-4000-8000-00000000000F")
    /// Recorded, not transcribed yet.
    static let waitingID = uuid("D00DFEED-0000-4000-8000-000000000001")
    /// A folder with neither a readable record nor a transcript.
    static let brokenID = uuid("DEADBEEF-0000-4000-8000-000000000002")
    /// Not in the standard set: times only a hand-edited file holds (see `writeExtremeTimes`).
    static let extremeTimesID = uuid("E0E0E0E0-0000-4000-8000-000000000003")

    static let coworkTitle = "Коворк 15 сентября"

    let base: URL
    let directories: AppDirectories

    init() throws {
        base = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoMCPTests-\(UUID().uuidString)", isDirectory: true)
        directories = AppDirectories(base: base)
        try FileManager.default.createDirectory(at: directories.meetingsFolder, withIntermediateDirectories: true)
    }

    func remove() {
        try? FileManager.default.removeItem(at: base)
    }

    static func uuid(_ text: String) -> UUID {
        UUID(uuidString: text) ?? UUID()
    }

    static func date(_ text: String) -> Date {
        ISO8601DateFormatter().date(from: text) ?? Date(timeIntervalSinceReferenceDate: 0)
    }

    func folder(_ id: UUID) -> URL {
        directories.meetingFolder(id: id)
    }

    func recordURL(_ id: UUID) -> URL {
        folder(id).appendingPathComponent("meeting.json")
    }

    /// Writes a record the way `MeetingStore` does: sorted, pretty-printed, atomically.
    func write(_ record: MeetingRecord) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try FileManager.default.createDirectory(at: folder(record.id), withIntermediateDirectories: true)
        try encoder.encode(record).write(to: recordURL(record.id), options: .atomic)
    }

    func writeRaw(_ text: String, to name: String, in id: UUID) throws {
        try FileManager.default.createDirectory(at: folder(id), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: folder(id).appendingPathComponent(name), options: .atomic)
    }

    // MARK: - The standard set

    static func coworkRecord(title: String = coworkTitle) -> MeetingRecord {
        let speakers = [
            MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0, isMe: true),
            MeetingSpeaker(id: "s2", track: .microphone, number: 2, displayName: "Аня", colorIndex: 1),
            MeetingSpeaker(id: "s3", track: .microphone, number: 3, colorIndex: 2)
        ]
        let segments = [
            TranscriptSegment(start: 4, end: 9, speaker: .me, text: "Всем привет, начнём с планов на неделю.", speakerID: "s1"),
            TranscriptSegment(start: 20, end: 30, speaker: .me, text: "Коллеги, давайте начнём с релиза.", speakerID: "s2"),
            TranscriptSegment(start: 758, end: 765, speaker: .me,
                              text: "Давайте к пятнице соберём прототип и покажем его команде.", speakerID: "s2"),
            TranscriptSegment(start: 800, end: 812, speaker: .me, text: "Согласен, ёлка на ресепшене тоже нужна.",
                              speakerID: "s3"),
            TranscriptSegment(start: 1200, end: 1210, speaker: .me, text: "Проверим дизайн в понедельник.", speakerID: "s1")
        ]
        let highlights = [
            MeetingHighlight(id: uuid("9B1C0000-0000-4000-8000-000000000001"), time: 760.2, note: "дедлайн",
                             source: .recording, createdAt: date("2026-09-15T11:15:00+03:00")),
            MeetingHighlight(id: uuid("9B1C0000-0000-4000-8000-000000000002"), time: 805, source: .transcript,
                             createdAt: date("2026-09-15T13:00:00+03:00"))
        ]
        let diarization = MeetingDiarization(state: .applied, speakersPerTrack: ["microphone": 3],
                                             model: "pyannote-community-1-coreml@1ed7a66")
        return MeetingRecord(id: coworkID, title: title, createdAt: date("2026-09-15T11:02:07+03:00"),
                             duration: 3852.4, status: .transcribed, hasSystemAudio: false, hasAudio: false,
                             engineID: .parakeet, segments: segments, speakers: speakers, highlights: highlights,
                             diarization: diarization)
    }

    /// The first format exactly as builds before speaker separation wrote it.
    static func planningJSON() -> String {
        let created = date("2026-09-10T10:00:00+03:00").timeIntervalSinceReferenceDate
        return """
            {
              "createdAt" : \(created),
              "duration" : 95.5,
              "engineID" : "gigaam-v3-rnnt",
              "hasAudio" : false,
              "hasSystemAudio" : true,
              "id" : "\(planningID.uuidString)",
              "segments" : [
                {
                  "end" : 6,
                  "speaker" : "me",
                  "start" : 1,
                  "text" : "Let us plan the release."
                },
                {
                  "end" : 12,
                  "speaker" : "others",
                  "start" : 7,
                  "text" : "The prototype is due on Friday."
                }
              ],
              "status" : "transcribed",
              "title" : "Planning"
            }
            """
    }

    /// A transcribed meeting whose duration, segment times and highlight are far outside
    /// Int or below zero, as only a hand-edited `meeting.json` holds them.
    static func extremeTimesJSON() -> String {
        let created = date("2026-09-11T09:00:00+03:00").timeIntervalSinceReferenceDate
        return """
            {
              "createdAt" : \(created),
              "duration" : 1e20,
              "formatVersion" : 2,
              "hasAudio" : false,
              "hasSystemAudio" : false,
              "highlights" : [
                {
                  "createdAt" : \(created),
                  "id" : "9B1C0000-0000-4000-8000-000000000009",
                  "source" : "transcript",
                  "time" : 1e300
                }
              ],
              "id" : "\(extremeTimesID.uuidString)",
              "segments" : [
                {
                  "end" : -1,
                  "speaker" : "me",
                  "start" : -5,
                  "text" : "Before the start."
                },
                {
                  "end" : 1e19,
                  "speaker" : "me",
                  "start" : 9.3e18,
                  "text" : "After the end of time."
                }
              ],
              "status" : "transcribed",
              "title" : "Extreme times"
            }
            """
    }

    func writeExtremeTimes() throws {
        try writeRaw(Self.extremeTimesJSON(), to: "meeting.json", in: Self.extremeTimesID)
    }

    /// Writes the standard set of meetings.
    func writeStandardSet() throws {
        try write(Self.coworkRecord())
        try write(MeetingRecord(id: Self.callID, title: "Созвон с дизайнерами", createdAt: Self.date("2026-09-15T17:30:00+03:00"),
                                duration: 1800, status: .transcribing, hasSystemAudio: true, hasAudio: true))
        try write(MeetingRecord(id: Self.standupID, title: "Утренний стендап", createdAt: Self.date("2026-09-14T09:00:00+03:00"),
                                duration: 60, status: .recording, hasSystemAudio: false, hasAudio: true,
                                highlights: [MeetingHighlight(id: Self.uuid("9B1C0000-0000-4000-8000-000000000003"),
                                                              time: 12, source: .recording)]))
        try writeRaw(Self.planningJSON(), to: "meeting.json", in: Self.planningID)
        try writeRaw("{ \"id\": broken", to: "meeting.json", in: Self.recoveredID)
        // This is an existing export the archive recovers. Its text must be identical
        // on every host, regardless of the time zone used to run the tests.
        let recoveredDocument = """
        # Recovered call

        September 12, 2026 at 1:00\u{202F}PM

        [00:03] Me: Где отчёт по продажам?

        [01:05] Others: Отчёт будет завтра.

        """
        try writeRaw(recoveredDocument, to: "transcript.md", in: Self.recoveredID)
        try FileManager.default.setAttributes(
            [.creationDate: Self.date("2026-09-12T10:00:00+03:00")],
            ofItemAtPath: folder(Self.recoveredID).appendingPathComponent("transcript.md").path)
        try write(MeetingRecord(id: Self.retroID, title: "Old retro", createdAt: Self.date("2026-02-27T15:00:00+03:00"),
                                duration: 600, status: .transcribed, hasAudio: false,
                                segments: [TranscriptSegment(start: 0, end: 5, speaker: .me, text: "Ретро прошлого года.")]))
        try write(MeetingRecord(id: Self.waitingID, title: "Waiting for transcription",
                                createdAt: Self.date("2026-09-13T12:00:00+03:00"), duration: 300, status: .recorded,
                                hasAudio: true))
        try writeRaw("{ broken", to: "meeting.json", in: Self.brokenID)
    }
}

/// Test doubles and request helpers shared by the MCP tests.
final class MCPFakeClock: UptimeProviding, @unchecked Sendable {
    private let lock = NSLock()
    private var current: TimeInterval = 1_000
    /// Added after every reading, so tests that page through many calls stay under the rate limit.
    private let step: TimeInterval

    init(step: TimeInterval = 0) {
        self.step = step
    }

    func uptime() -> TimeInterval {
        lock.lock()
        defer { lock.unlock() }
        let value = current
        current += step
        return value
    }

    func advance(_ seconds: TimeInterval) {
        lock.lock()
        current += seconds
        lock.unlock()
    }
}

final class MCPAccessSwitch {
    var access = MCPAccess(enabled: true, maxAgeDays: 0)
}

struct MCPToolReply {
    let text: String
    let structured: JSONValue?
    let isError: Bool
}

enum MCPTestClient {
    static let modernMeta: JSONValue = [
        "io.modelcontextprotocol/protocolVersion": "2026-07-28",
        "io.modelcontextprotocol/clientCapabilities": .object([:])
    ]

    static func handler(archive: MeetingArchiveReading, notes: MeetingNotesWriting? = nil,
                        highlights: MeetingHighlightsWriting? = nil,
                        access: MCPAccessSwitch = MCPAccessSwitch(), language: AppLanguage = .en,
                        clock: MCPFakeClock = MCPFakeClock()) -> MCPHandler {
        MCPHandler(archive: archive, notes: notes, highlights: highlights, access: { access.access },
                   language: { language }, now: { MCPFixture.now }, timeZone: MCPFixture.timeZone,
                   serverVersion: "1.2.0", clock: clock)
    }

    static func request(id: JSONValue = 1, method: String, params: JSONValue? = nil, modern: Bool = false) -> JSONValue {
        var fields: [String: JSONValue] = ["jsonrpc": "2.0", "id": id, "method": .string(method)]
        var paramFields = params?.objectValue ?? [:]
        if modern {
            paramFields["_meta"] = modernMeta
        }
        if params != nil || modern {
            fields["params"] = .object(paramFields)
        }
        return .object(fields)
    }

    /// Sends one request and parses the reply; nil when there was none.
    static func send(_ handler: MCPHandler, _ message: JSONValue) -> JSONValue? {
        send(handler, raw: message.serializedString)
    }

    static func send(_ handler: MCPHandler, raw: String, file: StaticString = #filePath, line: UInt = #line) -> JSONValue? {
        guard let reply = handler.handle(line: Data(raw.utf8)) else { return nil }
        XCTAssertFalse(reply.contains(UInt8(ascii: "\n")), "a reply must stay on one line", file: file, line: line)
        return try? JSONValue(parsing: reply)
    }

    static func result(_ handler: MCPHandler, method: String, params: JSONValue? = nil, modern: Bool = false,
                       file: StaticString = #filePath, line: UInt = #line) -> JSONValue {
        let reply = send(handler, request(method: method, params: params, modern: modern))
        XCTAssertNil(reply?["error"], "unexpected error \(reply?["error"]?.serializedString ?? "")", file: file, line: line)
        return reply?["result"] ?? .null
    }

    static func errorCode(_ reply: JSONValue?) -> Int? {
        reply?["error"]?["code"]?.intValue
    }

    static func tool(_ handler: MCPHandler, _ name: String, _ arguments: JSONValue = .object([:]), modern: Bool = false,
                     file: StaticString = #filePath, line: UInt = #line) -> MCPToolReply {
        let result = self.result(handler, method: "tools/call", params: ["name": .string(name), "arguments": arguments],
                                 modern: modern, file: file, line: line)
        let content = result["content"]?.arrayValue ?? []
        XCTAssertEqual(content.count, 1, "a tool result has one text block", file: file, line: line)
        let text = content.first?["text"]?.stringValue ?? ""
        let structured = result["structuredContent"]
        if let structured {
            XCTAssertEqual(try? JSONValue(parsing: Data(text.utf8)), structured,
                           "text and structured content must hold the same JSON", file: file, line: line)
        }
        XCTAssertLessThanOrEqual(text.utf16.count, 48_000, file: file, line: line)
        return MCPToolReply(text: text, structured: structured, isError: result["isError"] == .bool(true))
    }
}

/// Checks that the fixture itself decodes as intended, so failures elsewhere point at the server.
final class MCPFixtureTests: XCTestCase {
    private var fixture: MCPFixture!

    override func setUpWithError() throws {
        fixture = try MCPFixture()
        try fixture.writeStandardSet()
    }

    override func tearDown() {
        fixture.remove()
    }

    func testStandardSetDecodesAsIntended() throws {
        let archive = MeetingArchive(directories: fixture.directories)
        let meetings = archive.meetings()
        XCTAssertEqual(meetings.map(\.record.id), [
            MCPFixture.callID, MCPFixture.coworkID, MCPFixture.standupID, MCPFixture.waitingID, MCPFixture.recoveredID,
            MCPFixture.planningID, MCPFixture.retroID
        ])
        let planning = try XCTUnwrap(archive.meeting(id: MCPFixture.planningID))
        XCTAssertEqual(planning.record.formatVersion, 1)
        XCTAssertEqual(planning.record.effectiveSpeakers.map(\.id), ["me", "others"])
        let recovered = try XCTUnwrap(archive.meeting(id: MCPFixture.recoveredID))
        XCTAssertEqual(recovered.record.title, "Recovered call")
        XCTAssertEqual(recovered.record.status, .transcribed)
        XCTAssertEqual(recovered.record.segments.map(\.text), ["Где отчёт по продажам?", "Отчёт будет завтра."])
        XCTAssertEqual(recovered.record.createdAt, MCPFixture.date("2026-09-12T10:00:00+03:00"))
        XCTAssertNil(archive.meeting(id: MCPFixture.brokenID))
        XCTAssertNotNil(archive.unreadableMeetingDate(id: MCPFixture.brokenID))
        XCTAssertNil(archive.unreadableMeetingDate(id: MCPFixture.coworkID))
        XCTAssertNil(archive.unreadableMeetingDate(id: UUID()))
    }

    /// The app rewrites `transcript.md` as a new file on every save, so an old meeting whose
    /// record is damaged must not take the transcript's fresh date and pass the age limit.
    func testARecoveredMeetingIsDatedByItsFolderRatherThanItsRewrittenTranscript() throws {
        let id = MCPFixture.uuid("A1A1A1A1-0000-4000-8000-000000000001")
        try fixture.writeRaw("{ \"id\": broken", to: "meeting.json", in: id)
        let document = TranscriptMerger.markdown(
            title: "Old call", date: MCPFixture.date("2026-03-01T10:00:00+03:00"),
            segments: [TranscriptSegment(start: 3, end: 3, speaker: .me, text: "Старая встреча.")], language: .en)
        try fixture.writeRaw(document, to: "transcript.md", in: id)
        try FileManager.default.setAttributes([.creationDate: MCPFixture.date("2026-09-15T12:00:00+03:00")],
                                              ofItemAtPath: fixture.folder(id).appendingPathComponent("transcript.md").path)
        let folderCreated = MCPFixture.date("2026-03-01T10:00:00+03:00")
        try FileManager.default.setAttributes([.creationDate: folderCreated], ofItemAtPath: fixture.folder(id).path)

        let archive = MeetingArchive(directories: fixture.directories)
        XCTAssertEqual(archive.meeting(id: id)?.record.createdAt, folderCreated)

        let access = MCPAccessSwitch()
        access.access.maxAgeDays = 90
        let handler = MCPTestClient.handler(archive: archive, access: access)
        let listed = MCPTestClient.tool(handler, "list_meetings", ["limit": 100]).structured?["meetings"]?.arrayValue ?? []
        XCTAssertFalse(listed.contains { $0["id"] == .string(id.uuidString.lowercased()) },
                       "a meeting from March is outside a 90-day limit")
        XCTAssertTrue(MCPTestClient.tool(handler, "get_meeting", ["meeting_id": .string(id.uuidString.lowercased())]).isError)
    }
}
