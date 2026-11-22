import Foundation
import XCTest
@testable import SlovoCore

/// The five tools against a data folder with every kind of meeting. Expected transcript
/// lines are built with `TranscriptMerger` itself, so these tests hold whatever line
/// merging the meetings module chooses.
final class MCPToolsTests: XCTestCase {
    static let longID = MCPFixture.uuid("10000000-0000-4000-8000-00000000000A")

    private var fixture: MCPFixture!
    private var access = MCPAccessSwitch()
    private var handler: MCPHandler!

    override func setUpWithError() throws {
        fixture = try MCPFixture()
        try fixture.writeStandardSet()
        access = MCPAccessSwitch()
        handler = MCPTestClient.handler(archive: MeetingArchive(directories: fixture.directories), access: access,
                                        clock: MCPFakeClock(step: 1))
    }

    override func tearDown() {
        fixture.remove()
    }

    // MARK: - Helpers

    private func tool(_ name: String, _ arguments: JSONValue = .object([:]), file: StaticString = #filePath,
                      line: UInt = #line) -> MCPToolReply {
        MCPTestClient.tool(handler, name, arguments, file: file, line: line)
    }

    private func ids(_ reply: MCPToolReply) -> [String] {
        reply.structured?["meetings"]?.arrayValue?.compactMap { $0["id"]?.stringValue } ?? []
    }

    private func item(_ value: JSONValue?, _ index: Int) -> JSONValue? {
        guard let array = value?.arrayValue, array.indices.contains(index) else { return nil }
        return array[index]
    }

    static func lower(_ id: UUID) -> String {
        id.uuidString.lowercased()
    }

    static func label(_ record: MeetingRecord, _ id: String, _ language: AppLanguage = .en) -> String {
        record.speaker(id: id).map { TranscriptMerger.speakerLabel($0, language: language) } ?? id
    }

    static func message(_ key: String, _ language: AppLanguage = .en) -> String {
        CoreStrings.string(key, table: .integration, language: language)
    }

    /// The transcript lines as `get_transcript` should show them.
    static func expectedLines(_ record: MeetingRecord, language: AppLanguage = .en, speakers: Set<String>? = nil,
                              from: TimeInterval? = nil, to: TimeInterval? = nil) -> [String] {
        let lines = TranscriptMerger.lines(for: record)
        var anchored: [Int: [MeetingHighlight]] = [:]
        let highlights = record.highlights.sorted {
            ($0.time, $0.createdAt.timeIntervalSinceReferenceDate, $0.id.uuidString)
                < ($1.time, $1.createdAt.timeIntervalSinceReferenceDate, $1.id.uuidString)
        }
        for highlight in highlights {
            if let index = TranscriptMerger.highlightLineIndex(highlight, in: lines) {
                anchored[index, default: []].append(highlight)
            }
        }
        return lines.enumerated().compactMap { index, line -> String? in
            if let speakers, !speakers.contains(line.speakerID) { return nil }
            if let from, line.start < from { return nil }
            if let to, line.start >= to { return nil }
            var text = "[\(TranscriptMerger.timestamp(line.start))] " + (anchored[index] == nil ? "" : "★ ")
                + label(record, line.speakerID, language) + ": " + line.text
            for highlight in anchored[index] ?? [] {
                if let note = highlight.note, !note.isEmpty {
                    text += "\n  ↳ note: " + note
                }
            }
            return text
        }
    }

    /// The text between the transcript tags of one page.
    static func body(of page: String) -> String? {
        let open = MCPText.transcriptOpenTag + "\n"
        guard let openRange = page.range(of: open),
              let closeRange = page.range(of: MCPText.transcriptCloseTag, range: openRange.upperBound..<page.endIndex)
        else { return nil }
        var body = String(page[openRange.upperBound..<closeRange.lowerBound])
        if body.hasSuffix("\n") {
            body.removeLast()
        }
        return body
    }

    static func nextCursor(in page: String) -> String? {
        page.components(separatedBy: "\n").first { $0.hasPrefix("next_cursor: ") }
            .map { String($0.dropFirst("next_cursor: ".count)) }
    }

    /// Joins page bodies, continuing a cut line without a line break.
    static func reassemble(_ pages: [String]) -> String {
        var text = ""
        for (index, page) in pages.enumerated() {
            let body = body(of: page) ?? ""
            if index == 0 {
                text = body
            } else if page.contains(" continues from the previous page.") {
                text += body
            } else {
                text += "\n" + body
            }
        }
        return text
    }

    private func pages(_ arguments: [String: JSONValue], limit: Int = 1_000) -> [String] {
        var pages: [String] = []
        var cursor: String?
        repeat {
            var paged = arguments
            if let cursor {
                paged["cursor"] = .string(cursor)
            }
            let reply = tool("get_transcript", .object(paged))
            XCTAssertFalse(reply.isError, reply.text)
            guard !reply.isError else { break }
            pages.append(reply.text)
            cursor = Self.nextCursor(in: reply.text)
        } while cursor != nil && pages.count < limit
        return pages
    }

    /// 400 short lines, one 38,000-character line with a 5,000-character word, and highlights.
    static func longRecord(highlightNoteLength: Int = 0, highlightCount: Int = 10) -> MeetingRecord {
        let speakerIDs = ["s1", "s2", "s3"]
        var segments: [TranscriptSegment] = []
        for index in 0..<400 {
            let start = Double(index) * 12
            var text = (0..<25).map { "слово\(index)x\($0)" }.joined(separator: " ")
            if index == 200 {
                text = (0..<3_000).map { "длинное\($0)" }.joined(separator: " ") + " "
                    + String(repeating: "ы", count: 5_000) + " конец"
            }
            segments.append(TranscriptSegment(start: start, end: start + 8, speaker: .me, text: text,
                                              speakerID: speakerIDs[index % 3]))
        }
        let highlights = (0..<highlightCount).map { index in
            MeetingHighlight(id: UUID(uuidString: String(format: "20000000-0000-4000-8000-%012ld", index)) ?? UUID(),
                             time: Double(index * 40 % 400) * 12 + 1,
                             note: highlightNoteLength > 0 ? String(repeating: "н", count: highlightNoteLength) : "заметка \(index)",
                             source: .transcript, createdAt: MCPFixture.date("2026-09-01T12:00:00+03:00"))
        }
        let speakers = speakerIDs.enumerated().map { offset, id in
            MeetingSpeaker(id: id, track: .microphone, number: offset + 1, colorIndex: offset)
        }
        return MeetingRecord(id: longID, title: "Длинная встреча", createdAt: MCPFixture.date("2026-09-01T10:00:00+03:00"),
                             duration: 4_800, status: .transcribed, hasAudio: false, segments: segments,
                             speakers: speakers, highlights: highlights)
    }

    // MARK: - Hand-edited files

    func testTimesNoRecordingCanHaveBreakNoTool() throws {
        try fixture.writeExtremeTimes()
        let id = JSONValue.string(Self.lower(MCPFixture.extremeTimesID))

        let listed = tool("list_meetings", ["query": "Extreme"])
        XCTAssertFalse(listed.isError, listed.text)
        XCTAssertEqual(item(listed.structured?["meetings"], 0)?["duration_text"], "99999:59:59")

        let details = tool("get_meeting", ["meeting_id": id])
        XCTAssertFalse(details.isError, details.text)
        XCTAssertEqual(details.structured?["meeting"]?["duration_seconds"]?.doubleValue, MeetingTime.maximumSeconds)
        XCTAssertEqual(item(details.structured?["highlights"], 0)?["time_text"], "99999:59:59")

        let transcript = tool("get_transcript", ["meeting_id": id])
        XCTAssertFalse(transcript.isError, transcript.text)
        XCTAssertEqual(Self.body(of: transcript.text), "[00:00] Me: Before the start.\n[99999:59:59] ★ Me: After the end of time.")

        let search = tool("search_meetings", ["query": "end", "meeting_id": id])
        XCTAssertFalse(search.isError, search.text)
        XCTAssertEqual(item(search.structured?["hits"], 0)?["time_text"], "99999:59:59")
        XCTAssertFalse(tool("list_highlights", ["meeting_id": id]).isError)

        let index = MCPTestClient.result(handler, method: "resources/read", params: ["uri": "slovo://meetings"])
        XCTAssertTrue(index.serializedString.contains("99999:59:59"), index.serializedString)
        let document = MCPTestClient.result(handler, method: "resources/read",
                                            params: ["uri": .string("slovo://meetings/\(Self.lower(MCPFixture.extremeTimesID))/transcript.md")])
        XCTAssertNotNil(document["contents"]?.arrayValue?.first?["text"]?.stringValue)
    }

    // MARK: - list_meetings

    func testListMeetingsNewestFirstWithEveryField() throws {
        let reply = tool("list_meetings")
        XCTAssertFalse(reply.isError)
        let structured = try XCTUnwrap(reply.structured)
        XCTAssertEqual(structured["now"], "2026-09-15T18:04:00+03:00")
        XCTAssertEqual(structured["time_zone"], "Europe/Moscow")
        XCTAssertNil(structured["next_cursor"])
        XCTAssertNil(structured["message"])
        XCTAssertEqual(ids(reply), [MCPFixture.callID, MCPFixture.coworkID, MCPFixture.standupID, MCPFixture.waitingID,
                                    MCPFixture.recoveredID, MCPFixture.planningID, MCPFixture.retroID].map(Self.lower))

        let record = MCPFixture.coworkRecord()
        let expected: JSONValue = [
            "id": "3f2a9c1e-5b7d-4c1a-9e0f-2b6d8a4c7e11", "short_id": "3f2a9c1e", "title": "Коворк 15 сентября",
            "created_at": "2026-09-15T11:02:07+03:00", "duration_seconds": 3852.4, "duration_text": "1:04:12",
            "status": "transcribed", "has_transcript": true,
            "speakers": [
                ["id": "s1", "name": .string(Self.label(record, "s1")), "is_me": true],
                ["id": "s2", "name": "Аня", "is_me": false],
                ["id": "s3", "name": .string(Self.label(record, "s3")), "is_me": false]
            ],
            "highlight_count": 2, "engine": "parakeet-v3", "speaker_separation": "applied"
        ]
        XCTAssertEqual(item(structured["meetings"], 1), expected)

        let call = item(structured["meetings"], 0)
        XCTAssertEqual(call?["status"], "transcribing")
        XCTAssertEqual(call?["has_transcript"], false)
        XCTAssertEqual(call?["engine"], .null)
        let planning = item(structured["meetings"], 5)
        XCTAssertEqual(planning?["speaker_separation"], "none")
        XCTAssertEqual(planning?["engine"], "gigaam-v3-rnnt")
        XCTAssertEqual(planning?["speakers"]?.arrayValue?.compactMap { $0["id"]?.stringValue }, ["me", "others"])
    }

    func testListMeetingsPagesWithACursor() throws {
        var collected: [String] = []
        var cursor: String?
        var pageCount = 0
        repeat {
            var arguments: [String: JSONValue] = ["limit": 2]
            if let cursor {
                arguments["cursor"] = .string(cursor)
            }
            let reply = tool("list_meetings", .object(arguments))
            XCTAssertFalse(reply.isError)
            collected += ids(reply)
            cursor = reply.structured?["next_cursor"]?.stringValue
            pageCount += 1
        } while cursor != nil && pageCount < 10
        XCTAssertEqual(pageCount, 4)
        XCTAssertEqual(collected, ids(tool("list_meetings")))

        let first = try XCTUnwrap(tool("list_meetings", ["limit": 2]).structured?["next_cursor"]?.stringValue)
        let badCursor = Self.message("integration.mcp.badCursor")
        for arguments in [
            ["limit": 2, "cursor": .string(first), "query": "Planning"],
            ["cursor": .string(first), "since": "7d"],
            ["cursor": "!!!"],
            ["cursor": .string(MCPBase64URL.encode(Data(#"{"k":"m"}"#.utf8)))]
        ] as [JSONValue] {
            let reply = tool("list_meetings", arguments)
            XCTAssertTrue(reply.isError)
            XCTAssertEqual(reply.text, badCursor)
        }
        // Another limit continues the same listing.
        XCTAssertEqual(ids(tool("list_meetings", ["limit": 50, "cursor": .string(first)])), Array(collected.dropFirst(2)))
    }

    func testQueryAndDateFilters() {
        let call = Self.lower(MCPFixture.callID)
        let cowork = Self.lower(MCPFixture.coworkID)
        let standup = Self.lower(MCPFixture.standupID)
        let waiting = Self.lower(MCPFixture.waitingID)
        let recovered = Self.lower(MCPFixture.recoveredID)
        let planning = Self.lower(MCPFixture.planningID)
        let retro = Self.lower(MCPFixture.retroID)
        let cases: [(JSONValue, [String])] = [
            (["query": "КОВОРК"], [cowork]),
            (["query": "сентября  коворк"], [cowork]),
            (["since": "today"], [call, cowork]),
            (["since": "yesterday", "until": "today"], [standup]),
            (["since": "2026-09-12", "until": "2026-09-14"], [waiting, recovered]),
            (["since": "2026-09-15T12:00"], [call]),
            (["since": "2026-09-15T09:00:00Z"], [call]),
            (["until": "2026-09-15T11:02:07+03:00"], [standup, waiting, recovered, planning, retro]),
            (["since": "7d"], [call, cowork, standup, waiting, recovered, planning]),
            (["since": "1d"], [call, cowork]),
            (["since": "365d", "until": "2026-09-01"], [retro])
        ]
        for (arguments, expected) in cases {
            let reply = tool("list_meetings", arguments)
            XCTAssertFalse(reply.isError, reply.text)
            XCTAssertEqual(ids(reply), expected, arguments.serializedString)
        }

        for arguments in [["since": "2026-02-30"], ["since": "366d"], ["since": "0d"], ["until": "2026-13-01"],
                          ["since": "2026-09-15T24:00"], ["since": "today", "until": "yesterday"]] as [JSONValue] {
            XCTAssertTrue(tool("list_meetings", arguments).isError, arguments.serializedString)
        }
        let empty = tool("list_meetings", ["query": "nothing here"])
        XCTAssertFalse(empty.isError)
        XCTAssertEqual(empty.structured?["meetings"], .array([]))
        XCTAssertEqual(empty.structured?["message"], .string(Self.message("integration.mcp.noMeetings")))
    }

    func testAgeLimitHidesOlderMeetingsEverywhere() {
        let retro = Self.lower(MCPFixture.retroID)
        access.access.maxAgeDays = 30
        XCTAssertFalse(ids(tool("list_meetings")).contains(retro))
        let missing = tool("get_meeting", ["meeting_id": .string(retro)])
        XCTAssertTrue(missing.isError)
        XCTAssertEqual(missing.text, "No meeting matches “\(retro)”.")
        XCTAssertEqual(tool("search_meetings", ["query": "Ретро"]).structured?["hits"], .array([]))
        let read = MCPTestClient.send(handler, MCPTestClient.request(
            method: "resources/read", params: ["uri": .string("slovo://meetings/\(retro)/transcript.md")]))
        XCTAssertEqual(MCPTestClient.errorCode(read), -32002)
        let prompt = MCPTestClient.send(handler, MCPTestClient.request(
            method: "prompts/get", params: ["name": "meeting_tasks", "arguments": ["meeting_id": .string(retro)]]))
        XCTAssertEqual(MCPTestClient.errorCode(prompt), -32602)

        access.access.maxAgeDays = 7
        let visible = ids(tool("list_meetings"))
        XCTAssertTrue(visible.contains(Self.lower(MCPFixture.planningID)))
        XCTAssertFalse(visible.contains(retro))
        access.access.maxAgeDays = 0
        XCTAssertTrue(ids(tool("list_meetings")).contains(retro))
    }

    // MARK: - get_meeting

    func testLatestSkipsAMeetingInProgressAndSaysSo() throws {
        let reply = tool("get_meeting")
        XCTAssertEqual(reply.structured?["meeting"]?["id"], .string(Self.lower(MCPFixture.coworkID)))
        let note = reply.structured?["note"]?.stringValue ?? ""
        XCTAssertTrue(note.contains("«Созвон с дизайнерами»"), note)
        XCTAssertTrue(note.contains("2026-09-15T17:30:00+03:00"), note)

        try FileManager.default.removeItem(at: fixture.folder(MCPFixture.callID))
        let quiet = tool("get_meeting", ["meeting_id": "latest"])
        XCTAssertEqual(quiet.structured?["meeting"]?["id"], .string(Self.lower(MCPFixture.coworkID)))
        XCTAssertNil(quiet.structured?["note"])
    }

    func testGetMeetingDetails() throws {
        let record = MCPFixture.coworkRecord()
        let reply = tool("get_meeting", ["meeting_id": "3F2A9C1E-5B"])
        let structured = try XCTUnwrap(reply.structured)
        XCTAssertEqual(structured["meeting"], [
            "id": "3f2a9c1e-5b7d-4c1a-9e0f-2b6d8a4c7e11", "short_id": "3f2a9c1e", "title": "Коворк 15 сентября",
            "created_at": "2026-09-15T11:02:07+03:00", "duration_seconds": 3852.4, "duration_text": "1:04:12",
            "status": "transcribed", "engine": "parakeet-v3", "speaker_separation": "applied", "has_system_audio": false
        ])
        XCTAssertEqual(structured["speakers"], .array(["s1", "s2", "s3"].map { id -> JSONValue in
            [
                "id": .string(id), "name": .string(Self.label(record, id)), "is_me": .bool(id == "s1"),
                "source": "microphone", "talk_seconds": .seconds(record.talkTime(of: id))
            ]
        }))
        let lines = TranscriptMerger.lines(for: record)
        let highlights = record.highlights.map { highlight -> JSONValue in
            let line = TranscriptMerger.highlightLineIndex(highlight, in: lines).map { lines[$0] }
            return [
                "id": .string(highlight.id.uuidString.lowercased()), "time_seconds": .seconds(highlight.time),
                "time_text": .string(TranscriptMerger.timestamp(highlight.time)), "note": .optional(highlight.note),
                "speaker_id": .optional(line?.speakerID), "speaker": .optional(line.map { Self.label(record, $0.speakerID) }),
                "line": .optional(line?.text)
            ]
        }
        XCTAssertEqual(structured["highlights"], .array(highlights))
        XCTAssertEqual(item(structured["highlights"], 0)?["time_text"], "12:40")
        XCTAssertEqual(structured["transcript"]?["lines"], .integer(lines.count))
        XCTAssertEqual(structured["transcript"]?["pages_at_default_size"], 1)
        XCTAssertGreaterThan(structured["transcript"]?["characters"]?.intValue ?? 0, 100)
        XCTAssertNil(structured["note"])

        let without = tool("get_meeting", ["meeting_id": "3f2a9c1e-5b", "include_highlights": false])
        XCTAssertNil(without.structured?["highlights"])
    }

    func testMeetingsWithoutTranscripts() {
        let standup = tool("get_meeting", ["meeting_id": "11111111"])
        XCTAssertEqual(standup.structured?["meeting"]?["status"], "recording")
        XCTAssertEqual(standup.structured?["transcript"], .null)
        XCTAssertTrue(standup.structured?["note"]?.stringValue?.contains("still being recorded") ?? false)
        let waiting = tool("get_meeting", ["meeting_id": "d00dfeed"])
        XCTAssertTrue(waiting.structured?["note"]?.stringValue?.contains("not transcribed yet") ?? false)

        let stillProcessing = Self.message("integration.mcp.stillProcessing")
        XCTAssertEqual(tool("get_transcript", ["meeting_id": "11111111"]).text, stillProcessing)
        XCTAssertEqual(tool("get_transcript", ["meeting_id": "7b1d2e3f"]).text, stillProcessing)
        XCTAssertEqual(tool("get_transcript", ["meeting_id": "d00dfeed"]).text, Self.message("integration.mcp.noTranscript"))
    }

    func testMeetingIDResolution() {
        let cowork = Self.lower(MCPFixture.coworkID)
        let ambiguous = tool("get_meeting", ["meeting_id": "3f2a9c1e"])
        XCTAssertTrue(ambiguous.isError)
        XCTAssertTrue(ambiguous.text.hasPrefix(Self.message("integration.mcp.ambiguous")))
        XCTAssertTrue(ambiguous.text.contains(cowork))
        XCTAssertTrue(ambiguous.text.contains(Self.lower(MCPFixture.planningID)))

        for reference in ["3F2A9C1E-5B", "3f2a9c1e5b7d", MCPFixture.coworkID.uuidString, cowork] {
            XCTAssertEqual(tool("get_meeting", ["meeting_id": .string(reference)]).structured?["meeting"]?["id"],
                           .string(cowork), reference)
        }
        let missing = tool("get_meeting", ["meeting_id": "00000000"])
        XCTAssertTrue(missing.isError)
        XCTAssertEqual(missing.text, "No meeting matches “00000000”.")
        XCTAssertEqual(tool("get_meeting", ["meeting_id": .string(UUID().uuidString)]).isError, true)
        let broken = tool("get_meeting", ["meeting_id": .string(Self.lower(MCPFixture.brokenID))])
        XCTAssertEqual(broken.text, Self.message("integration.mcp.damaged"))
    }

    func testDamagedRecordIsReadFromItsTranscript() {
        let meeting = tool("get_meeting", ["meeting_id": "a0a0a0a0"])
        XCTAssertEqual(meeting.structured?["meeting"]?["title"], "Recovered call")
        XCTAssertEqual(meeting.structured?["meeting"]?["status"], "transcribed")
        XCTAssertEqual(meeting.structured?["meeting"]?["created_at"], "2026-09-12T10:00:00+03:00")
        let transcript = tool("get_transcript", ["meeting_id": "a0a0a0a0"])
        XCTAssertFalse(transcript.isError)
        XCTAssertTrue(transcript.text.contains("Где отчёт по продажам?"))
        XCTAssertTrue(transcript.text.contains("Отчёт будет завтра."))
    }

    // MARK: - get_transcript

    func testTranscriptPageFormat() throws {
        let record = MCPFixture.coworkRecord()
        let reply = tool("get_transcript", ["meeting_id": "latest"])
        XCTAssertNil(reply.structured)
        let lines = reply.text.components(separatedBy: "\n")
        guard lines.count > 6 else { return XCTFail(reply.text) }
        XCTAssertEqual(lines[0], "Slovo meeting «Коворк 15 сентября» · 2026-09-15T11:02:07+03:00 · 1:04:12 · id 3f2a9c1e-5b7d-4c1a-9e0f-2b6d8a4c7e11")
        XCTAssertEqual(lines[1], "Speakers: s1 = \(Self.label(record, "s1")) (the Slovo user) · s2 = Аня · s3 = \(Self.label(record, "s3"))")
        let expected = Self.expectedLines(record)
        XCTAssertTrue(lines[2].hasPrefix("Lines 1–\(expected.count) of \(expected.count) · 00:04–"), lines[2])
        XCTAssertEqual(lines[3], MCPText.transcriptNotice)
        XCTAssertEqual(lines[4], "")
        XCTAssertEqual(lines[5], "<transcript source=\"slovo\" untrusted=\"true\">")
        XCTAssertEqual(Self.body(of: reply.text), expected.joined(separator: "\n"))
        XCTAssertTrue(reply.text.hasSuffix("</transcript>\n\nEnd of transcript."))
    }

    func testPagesConcatenateToTheWholeTranscript() throws {
        let record = Self.longRecord()
        try fixture.write(record)
        let expected = Self.expectedLines(record).joined(separator: "\n")
        for maxChars in [2_000, 3_500, 16_000, 48_000] {
            let pages = pages(["meeting_id": .string(Self.lower(Self.longID)), "max_chars": .integer(maxChars)])
            XCTAssertGreaterThan(pages.count, 2, "max_chars \(maxChars)")
            for page in pages {
                XCTAssertLessThanOrEqual(page.utf16.count, maxChars)
            }
            XCTAssertTrue(pages.last?.hasSuffix("End of transcript.") ?? false)
            XCTAssertEqual(Self.reassemble(pages), expected, "max_chars \(maxChars)")
        }
    }

    /// A 200-character title and a thousand characters of speaker names used to push the
    /// smallest pages past `max_chars`; the speakers line now gives way instead.
    func testALongHeaderNeverPushesAPagePastMaxChars() throws {
        let speakers = (1...29).map { (number: Int) -> MeetingSpeaker in
            MeetingSpeaker(id: "s\(number)", track: .microphone, number: number,
                           displayName: "Участник номер \(number) с очень длинным именем 👩‍💻 и фамилией", colorIndex: 0)
        }
        // Every speaker says something, so every one of them is listed above the transcript.
        let segments = (0..<58).map { index in
            TranscriptSegment(start: Double(index) * 10, end: Double(index) * 10 + 8, speaker: .me,
                              text: "Реплика \(index): " + String(repeating: "слово ", count: 20), speakerID: "s\(index % 29 + 1)")
        }
        let record = MeetingRecord(id: Self.longID, title: String(repeating: "Т", count: 200),
                                   createdAt: MCPFixture.date("2026-09-01T10:00:00+03:00"), duration: 600,
                                   status: .transcribed, hasAudio: false, segments: segments, speakers: speakers)
        try fixture.write(record)
        let expected = Self.expectedLines(record).joined(separator: "\n")
        let pages = pages(["meeting_id": .string(Self.lower(Self.longID)), "max_chars": 2_000])
        XCTAssertGreaterThan(pages.count, 2)
        for page in pages {
            XCTAssertLessThanOrEqual(page.utf16.count, 2_000)
            XCTAssertTrue(page.contains("\nSpeakers: s"), "the speakers line is shortened, not dropped")
        }
        XCTAssertEqual(Self.reassemble(pages), expected)
        // A page with room to spare keeps the speakers line at its usual length.
        let roomy = tool("get_transcript", ["meeting_id": .string(Self.lower(Self.longID)), "max_chars": 48_000]).text
        let speakersLine = roomy.components(separatedBy: "\n").first { $0.hasPrefix("Speakers: ") } ?? ""
        XCTAssertGreaterThan(speakersLine.count, 900)
    }

    func testSpeakerAndTimeFilters() {
        let record = MCPFixture.coworkRecord()
        let cases: [(JSONValue, [String])] = [
            (["meeting_id": "latest", "speaker_ids": ["s2"]], Self.expectedLines(record, speakers: ["s2"])),
            (["meeting_id": "latest", "from": "12:00", "to": "13:30"], Self.expectedLines(record, from: 720, to: 810)),
            (["meeting_id": "latest", "from": 758, "to": "0:13:20"], Self.expectedLines(record, from: 758, to: 800)),
            (["meeting_id": "latest", "from": "700.5", "speaker_ids": ["s1", "s2"]],
             Self.expectedLines(record, speakers: ["s1", "s2"], from: 700.5))
        ]
        for (arguments, expected) in cases {
            let reply = tool("get_transcript", arguments)
            XCTAssertFalse(reply.isError, reply.text)
            XCTAssertFalse(expected.isEmpty)
            XCTAssertEqual(Self.body(of: reply.text), expected.joined(separator: "\n"), arguments.serializedString)
        }
        for arguments in [
            ["meeting_id": "latest", "from": "13:00", "to": "12:00"], ["meeting_id": "latest", "from": "1:2:3"],
            ["meeting_id": "latest", "from": -5], ["meeting_id": "latest", "to": "12:75"],
            ["meeting_id": "latest", "speaker_ids": ["s9"]]
        ] as [JSONValue] {
            XCTAssertTrue(tool("get_transcript", arguments).isError, arguments.serializedString)
        }
        XCTAssertTrue(tool("get_transcript", ["meeting_id": "latest", "speaker_ids": ["s9"]]).text.contains("s9"))
        let none = tool("get_transcript", ["meeting_id": "latest", "from": "59:00"])
        XCTAssertTrue(none.text.contains(MCPText.noLinesMatch))
        XCTAssertTrue(none.text.hasSuffix("End of transcript."))
    }

    func testCursorsOnlyContinueTheirOwnArguments() throws {
        try fixture.write(Self.longRecord())
        let long = Self.lower(Self.longID)
        let first = tool("get_transcript", ["meeting_id": .string(long), "max_chars": 2000])
        let cursor = try XCTUnwrap(Self.nextCursor(in: first.text))
        let badCursor = Self.message("integration.mcp.badCursor")
        var tampered = try XCTUnwrap(MCPTranscriptCursor(encoded: cursor))
        tampered.index = 100_000
        for arguments in [
            ["meeting_id": .string(long), "max_chars": 3000, "cursor": .string(cursor)],
            ["meeting_id": .string(long), "cursor": .string(cursor)],
            ["meeting_id": "3f2a9c1e-5b", "max_chars": 2000, "cursor": .string(cursor)],
            ["meeting_id": .string(long), "max_chars": 2000, "speaker_ids": ["s1"], "cursor": .string(cursor)],
            ["meeting_id": .string(long), "max_chars": 2000, "cursor": "not-a-cursor!"],
            ["meeting_id": .string(long), "max_chars": 2000, "cursor": .string(MCPBase64URL.encode(Data("{}".utf8)))],
            ["meeting_id": .string(long), "max_chars": 2000, "cursor": .string(tampered.encoded)]
        ] as [JSONValue] {
            let reply = tool("get_transcript", arguments)
            XCTAssertTrue(reply.isError, arguments.serializedString)
            XCTAssertEqual(reply.text, badCursor)
        }
        let second = tool("get_transcript", ["meeting_id": .string(long), "max_chars": 2000, "cursor": .string(cursor)])
        XCTAssertFalse(second.isError)
        XCTAssertFalse(second.text.contains(MCPText.meetingChanged))
    }

    func testChangedMeetingContinuesWithANote() throws {
        var record = Self.longRecord()
        try fixture.write(record)
        let long = Self.lower(Self.longID)
        let first = tool("get_transcript", ["meeting_id": .string(long), "max_chars": 2000])
        let cursorText = try XCTUnwrap(Self.nextCursor(in: first.text))
        let cursor = try XCTUnwrap(MCPTranscriptCursor(encoded: cursorText))

        record.title = "Длинная встреча, переименована"
        try fixture.write(record)
        try FileManager.default.setAttributes([.modificationDate: MCPFixture.date("2030-01-01T00:00:00Z")],
                                              ofItemAtPath: fixture.recordURL(Self.longID).path)
        let second = tool("get_transcript", ["meeting_id": .string(long), "max_chars": 2000, "cursor": .string(cursorText)])
        XCTAssertFalse(second.isError)
        XCTAssertTrue(second.text.contains(MCPText.meetingChanged))
        XCTAssertTrue(second.text.contains("\nLines \(cursor.index + 1)–"))
    }

    func testNoResultIsLongerThan48000Characters() throws {
        try fixture.write(Self.longRecord(highlightNoteLength: 500, highlightCount: 400))
        let long = Self.lower(Self.longID)
        let meeting = tool("get_meeting", ["meeting_id": .string(long)])
        XCTAssertEqual(meeting.structured?["highlights_truncated"], true)
        XCTAssertTrue(meeting.structured?["note"]?.stringValue?.contains("list_highlights") ?? false)
        XCTAssertGreaterThan(meeting.structured?["highlights"]?.arrayValue?.count ?? 0, 10)
        let highlights = tool("list_highlights", ["meeting_id": .string(long), "limit": 200])
        XCTAssertEqual(highlights.structured?["truncated"], true)
        let search = tool("search_meetings", ["query": "слово", "limit": 50])
        XCTAssertEqual(search.structured?["hits"]?.arrayValue?.count, 50)
        XCTAssertEqual(search.structured?["truncated"], true)
        XCTAssertFalse(tool("list_meetings", ["limit": 100]).isError)
        // The tool helper checks every text against 48,000 characters.
    }

    // MARK: - search_meetings

    func testSearchMatchesLinesSpeakersAndTitles() {
        let record = MCPFixture.coworkRecord()
        let fir = tool("search_meetings", ["query": "ЕЛКА"])
        let hits = fir.structured?["hits"]?.arrayValue ?? []
        XCTAssertEqual(hits.count, 1)
        let hit = hits.first
        XCTAssertEqual(hit?["meeting_id"], .string(Self.lower(MCPFixture.coworkID)))
        XCTAssertEqual(hit?["short_id"], "3f2a9c1e")
        XCTAssertEqual(hit?["title"], "Коворк 15 сентября")
        XCTAssertEqual(hit?["created_at"], "2026-09-15T11:02:07+03:00")
        XCTAssertEqual(hit?["time_seconds"], 800.0)
        XCTAssertEqual(hit?["time_text"], "13:20")
        XCTAssertEqual(hit?["speaker_id"], "s3")
        XCTAssertEqual(hit?["speaker"], .string(Self.label(record, "s3")))
        XCTAssertTrue(hit?["snippet"]?.stringValue?.contains("ёлка") ?? false)
        XCTAssertEqual(fir.structured?["truncated"], false)
        // The short id is shared with another meeting, so the step names the full id.
        XCTAssertEqual(fir.structured?["next_step"],
                       "get_transcript meeting_id=3f2a9c1e-5b7d-4c1a-9e0f-2b6d8a4c7e11 from=12:50 to=14:20")

        let spaced = tool("search_meetings", ["query": "  ПРОТОТИП   пятнице, "])
        XCTAssertEqual(item(spaced.structured?["hits"], 0)?["time_seconds"], 758.0)
        XCTAssertEqual(spaced.structured?["next_step"],
                       "get_transcript meeting_id=3f2a9c1e-5b7d-4c1a-9e0f-2b6d8a4c7e11 from=12:08 to=13:38")

        let title = tool("search_meetings", ["query": "дизайнерами"])
        XCTAssertEqual(title.structured?["hits"]?.arrayValue?.count, 1)
        XCTAssertEqual(item(title.structured?["hits"], 0)?["time_seconds"], .null)
        XCTAssertEqual(item(title.structured?["hits"], 0)?["snippet"], "Созвон с дизайнерами")
        XCTAssertEqual(title.structured?["next_step"], "get_meeting meeting_id=7b1d2e3f")

        let speaker = tool("search_meetings", ["query": "Аня"])
        XCTAssertEqual(speaker.structured?["hits"]?.arrayValue?.count, 1)
        XCTAssertEqual(item(speaker.structured?["hits"], 0)?["speaker_id"], "s2")
        XCTAssertEqual(item(speaker.structured?["hits"], 0)?["time_seconds"], 20.0)

        XCTAssertEqual(tool("search_meetings", ["query": "начнём", "meeting_id": "3f2a9c1e-5b"]).structured?["hits"]?.arrayValue?.count, 2)
        let limited = tool("search_meetings", ["query": "Отчёт", "limit": 1])
        XCTAssertEqual(limited.structured?["hits"]?.arrayValue?.count, 1)
        XCTAssertEqual(limited.structured?["truncated"], true)
        let none = tool("search_meetings", ["query": "zzzz"])
        XCTAssertEqual(none.structured?["hits"], .array([]))
        XCTAssertNil(none.structured?["next_step"])
        XCTAssertEqual(none.structured?["message"], .string(Self.message("integration.mcp.noMeetings")))
        XCTAssertEqual(tool("search_meetings", ["query": "начнём", "since": "2026-09-16"]).structured?["hits"], .array([]))
        XCTAssertTrue(tool("search_meetings", ["query": "!!"]).isError)
    }

    func testSearchNormalization() {
        XCTAssertEqual(MCPSearch.normalized("  Café  ЁЖИК\tЙод "), "cafe ежик иод")
        let sample = "Привет, Мир! ЁЛКА «кавычки» — тире… A-Z 0-9 йогурт\u{00A0}nbsp ёжик"
        XCTAssertEqual(MCPSearch.fastNormalized(sample), MCPSearch.slowNormalized(sample))
        XCTAssertNil(MCPSearch.fastNormalized("café"))
        XCTAssertEqual(MCPSearch.words("«Прототип», к пятнице!"), ["прототип", "к", "пятнице"])

        let text = String(repeating: "а ", count: 200) + "Нужная Ёлка" + String(repeating: " б", count: 200)
        let snippet = MCPSearch.snippet(text, words: ["елка"])
        XCTAssertTrue(snippet.hasPrefix("…"))
        XCTAssertTrue(snippet.hasSuffix("…"))
        XCTAssertTrue(snippet.contains("Ёлка"))
        XCTAssertLessThanOrEqual(snippet.count, 2 + 120 * 2 + 4)
    }

    // MARK: - list_highlights

    func testListHighlights() {
        let reply = tool("list_highlights")
        let items = reply.structured?["highlights"]?.arrayValue ?? []
        XCTAssertEqual(items.compactMap { $0["id"]?.stringValue }, [
            "9b1c0000-0000-4000-8000-000000000001", "9b1c0000-0000-4000-8000-000000000002",
            "9b1c0000-0000-4000-8000-000000000003"
        ])
        XCTAssertEqual(reply.structured?["truncated"], false)
        let first = item(reply.structured?["highlights"], 0)
        XCTAssertEqual(first?["meeting_id"], .string(Self.lower(MCPFixture.coworkID)))
        XCTAssertEqual(first?["short_id"], "3f2a9c1e")
        XCTAssertEqual(first?["meeting_title"], "Коворк 15 сентября")
        XCTAssertEqual(first?["time_seconds"], 760.2)
        XCTAssertEqual(first?["time_text"], "12:40")
        XCTAssertEqual(first?["note"], "дедлайн")
        XCTAssertEqual(first?["source"], "recording")
        let standup = item(reply.structured?["highlights"], 2)
        XCTAssertEqual(standup?["meeting_id"], .string(Self.lower(MCPFixture.standupID)))
        XCTAssertEqual(standup?["line"], .null)
        XCTAssertEqual(standup?["note"], .null)

        let limited = tool("list_highlights", ["limit": 2])
        XCTAssertEqual(limited.structured?["highlights"]?.arrayValue?.count, 2)
        XCTAssertEqual(limited.structured?["truncated"], true)
        XCTAssertEqual(tool("list_highlights", ["meeting_id": "11111111"]).structured?["highlights"]?.arrayValue?.count, 1)
        XCTAssertEqual(tool("list_highlights", ["since": "today"]).structured?["highlights"]?.arrayValue?.count, 2)
    }

    // MARK: - Language

    func testLabelsAndMessagesInRussian() {
        let russian = MCPTestClient.handler(archive: MeetingArchive(directories: fixture.directories), language: .ru,
                                            clock: MCPFakeClock(step: 1))
        let record = MCPFixture.coworkRecord()
        let reply = MCPTestClient.tool(russian, "get_meeting", ["meeting_id": "3f2a9c1e-5b"])
        XCTAssertEqual(reply.structured?["speakers"]?.arrayValue?.compactMap { $0["name"]?.stringValue },
                       ["s1", "s2", "s3"].map { Self.label(record, $0, .ru) })
        XCTAssertEqual(Self.label(record, "s3", .ru),
                       CoreStrings.format("meetings.speaker.numbered", table: .meetings, language: .ru, 3))
        XCTAssertEqual(MCPTestClient.tool(russian, "get_meeting", ["meeting_id": "00000000"]).text,
                       "Встреча «00000000» не найдена.")
        XCTAssertEqual(MCPTestClient.tool(russian, "list_meetings", ["limit": 0]).text.hasPrefix("Неверный аргумент: "), true)
    }
}
