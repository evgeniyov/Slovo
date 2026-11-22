import Foundation
import XCTest
@testable import SlovoCore

/// Resources and prompts: list contents, strict URIs, size limits and wording in both languages.
final class MCPResourcesPromptsTests: XCTestCase {
    private var fixture: MCPFixture!
    private var handler: MCPHandler!

    override func setUpWithError() throws {
        fixture = try MCPFixture()
        try fixture.writeStandardSet()
        handler = MCPTestClient.handler(archive: MeetingArchive(directories: fixture.directories), clock: MCPFakeClock(step: 1))
    }

    override func tearDown() {
        fixture.remove()
    }

    private func readText(_ uri: String, handler: MCPHandler? = nil) -> String {
        let result = MCPTestClient.result(handler ?? self.handler, method: "resources/read", params: ["uri": .string(uri)])
        return result["contents"]?.arrayValue?.first?["text"]?.stringValue ?? ""
    }

    private func prompt(_ name: String, _ arguments: JSONValue = .object([:]), handler: MCPHandler? = nil) -> JSONValue {
        MCPTestClient.result(handler ?? self.handler, method: "prompts/get", params: ["name": .string(name), "arguments": arguments])
    }

    private func messages(_ result: JSONValue) -> [JSONValue] {
        result["messages"]?.arrayValue ?? []
    }

    private func transcriptURI(_ id: UUID) -> String {
        "slovo://meetings/\(id.uuidString.lowercased())/transcript.md"
    }

    // MARK: - Resources

    func testResourcesListContents() throws {
        let result = MCPTestClient.result(handler, method: "resources/list")
        XCTAssertNil(result["nextCursor"])
        let resources = result["resources"]?.arrayValue ?? []
        XCTAssertEqual(resources.compactMap { $0["uri"]?.stringValue }, [
            "slovo://meetings", "slovo://meetings/latest/transcript.md",
            transcriptURI(MCPFixture.coworkID), transcriptURI(MCPFixture.recoveredID),
            transcriptURI(MCPFixture.planningID), transcriptURI(MCPFixture.retroID)
        ])
        guard resources.count > 2 else { return }
        XCTAssertEqual(resources[0]["mimeType"], "text/markdown")
        let cowork = resources[2]
        XCTAssertEqual(cowork["name"], "transcript-3f2a9c1e")
        XCTAssertEqual(cowork["title"], "Коворк 15 сентября")
        XCTAssertEqual(cowork["mimeType"], "text/markdown")
        let modified = try XCTUnwrap(
            FileManager.default.attributesOfItem(atPath: fixture.recordURL(MCPFixture.coworkID).path)[.modificationDate] as? Date)
        XCTAssertEqual(cowork["annotations"]?["lastModified"],
                       .string(MCPDates(now: MCPFixture.now, timeZone: MCPFixture.timeZone).iso(modified)))
    }

    func testMeetingsIndex() {
        let text = readText("slovo://meetings")
        XCTAssertTrue(text.hasPrefix("# Slovo meetings\n\n"))
        let entries = text.components(separatedBy: "\n").filter { $0.hasPrefix("- ") }
        XCTAssertEqual(entries.count, 7)
        guard entries.count == 7 else { return }
        let record = MCPFixture.coworkRecord()
        let names = ["s1", "s2", "s3"].map { MCPToolsTests.label(record, $0) }.joined(separator: ", ")
        XCTAssertEqual(entries[1], "- 2026-09-15T11:02:07+03:00 · 1:04:12 · «Коворк 15 сентября» · transcribed · "
                       + "speakers: \(names) · moments: 2 · id 3f2a9c1e")
        XCTAssertTrue(entries[2].contains("«Утренний стендап» · recording · speakers: none · moments: 1"))
    }

    func testTranscriptDocument() throws {
        let archived = try XCTUnwrap(MeetingArchive(directories: fixture.directories).meeting(id: MCPFixture.coworkID))
        let markdown = TranscriptMerger.markdown(meeting: archived.record, language: .en)
        for uri in ["slovo://meetings/latest/transcript.md", transcriptURI(MCPFixture.coworkID),
                    "slovo://meetings/\(MCPFixture.coworkID.uuidString)/transcript.md"] {
            let result = MCPTestClient.result(handler, method: "resources/read", params: ["uri": .string(uri)])
            let content = result["contents"]?.arrayValue?.first
            XCTAssertEqual(result["contents"]?.arrayValue?.count, 1)
            XCTAssertEqual(content?["uri"], .string(uri))
            XCTAssertEqual(content?["mimeType"], "text/markdown")
            let text = content?["text"]?.stringValue ?? ""
            XCTAssertTrue(text.hasPrefix("Slovo meeting «Коворк 15 сентября» · 2026-09-15T11:02:07+03:00 · 1:04:12 · "
                                         + "id 3f2a9c1e-5b7d-4c1a-9e0f-2b6d8a4c7e11\nSpeakers: "), text)
            XCTAssertTrue(text.contains(MCPText.transcriptNotice + "\n\n<transcript source=\"slovo\" untrusted=\"true\">\n" + markdown))
            XCTAssertTrue(text.hasSuffix("</transcript>\n"))
        }
    }

    /// What Claude wrote is not speech, and the wrapper's notice says its contents are.
    /// So the notes stay out of the document altogether, and only a line above the notice
    /// says they exist: running a prompt twice must not feed Claude its own summary as the
    /// recording, where it could quote words nobody said.
    func testClaudesOwnNotesAreNeverServedAsTranscript() throws {
        let notes = MeetingNotes(summary: "Договорились о сроках прототипа.",
                                 decisions: ["Прототип показываем в пятницу."],
                                 tasks: ["Аня собирает прототип."], author: "Claude",
                                 writtenAt: MCPFixture.date("2026-09-15T17:00:00+03:00"))
        var record = MCPFixture.coworkRecord()
        record.claudeNotes = notes
        try fixture.write(record)
        var withoutNotes = record
        withoutNotes.claudeNotes = nil
        let spoken = TranscriptMerger.markdown(meeting: withoutNotes, language: .en)
        let claudeText = [notes.summary] + notes.decisions + notes.tasks

        let document = readText(transcriptURI(MCPFixture.coworkID))

        for text in claudeText {
            XCTAssertFalse(document.contains(text), "«\(text)» reached the document")
        }
        XCTAssertFalse(document.contains(CoreStrings.string("meetings.transcript.claudeHeading", table: .meetings,
                                                            language: .en)), document)
        XCTAssertTrue(document.contains(MCPText.transcriptOpenTag + "\n" + spoken + MCPText.transcriptCloseTag), document)
        // The notes are named above the notice, so outside the wrapper entirely.
        let line = MCPText.claudeNotesLine(author: "Claude", written: "2026-09-15T17:00:00+03:00")
        XCTAssertTrue(document.contains(line + "\n" + MCPText.transcriptNotice), document)

        // Both prompts embed that same document, so neither hands earlier notes back.
        for name in ["meeting_tasks", "meeting_summary"] {
            XCTAssertEqual(messages(prompt(name)).last?["content"]?["resource"]?["text"]?.stringValue, document, name)
        }
        // get_transcript was already clean and stays so.
        let page = MCPTestClient.tool(handler, "get_transcript", ["meeting_id": "latest", "max_chars": 8000])
        for text in claudeText {
            XCTAssertFalse(page.text.contains(text), "«\(text)» reached get_transcript")
        }
    }

    func testResourceURIsAreParsedStrictly() {
        let cowork = MCPFixture.coworkID.uuidString.lowercased()
        let invalid = [
            "slovo://meetings/", "slovo://meetings/latest", "slovo://meetings/latest/transcript.md?x=1",
            "slovo://meetings/latest/transcript.md#top", "slovo://Meetings/latest/transcript.md",
            "SLOVO://meetings/latest/transcript.md", "slovo://meetings/../transcript.md", "slovo://meetings/transcript.md",
            "slovo://meetings/3f2a9c1e/transcript.md", "slovo://meetings/latest/latest/transcript.md",
            "slovo://meetings/\(cowork)/transcript.md/", "slovo://meetings/\(cowork)/meeting.json",
            "slovo://meetings/%33f2a9c1e-5b7d-4c1a-9e0f-2b6d8a4c7e11/transcript.md", "file:///etc/passwd",
            "slovo://meetings/../../history.json", " slovo://meetings",
            transcriptURI(MCPFixture.standupID), transcriptURI(MCPFixture.callID), transcriptURI(MCPFixture.waitingID),
            transcriptURI(MCPFixture.brokenID)
        ]
        for uri in invalid {
            let reply = MCPTestClient.send(handler, MCPTestClient.request(method: "resources/read", params: ["uri": .string(uri)]))
            XCTAssertEqual(MCPTestClient.errorCode(reply), -32002, uri)
        }
    }

    func testLongDocumentIsCutOnALineAt100000Characters() throws {
        let record = MCPToolsTests.longRecord()
        try fixture.write(record)
        let id = MCPToolsTests.longID.uuidString.lowercased()
        let text = readText(transcriptURI(MCPToolsTests.longID))
        XCTAssertLessThanOrEqual(text.utf16.count, 100_000)
        let tail = "</transcript>\n\nTruncated. Use the get_transcript tool with meeting_id \(id) to read the rest.\n"
        XCTAssertTrue(text.hasSuffix(tail))
        let open = "<transcript source=\"slovo\" untrusted=\"true\">\n"
        guard let openRange = text.range(of: open) else { return XCTFail("no wrapper") }
        let kept = String(text[openRange.upperBound...].dropLast(tail.count))
        let markdown = TranscriptMerger.markdown(meeting: record, language: .en)
        XCTAssertTrue(markdown.hasPrefix(kept))
        XCTAssertTrue(kept.hasSuffix("\n"))
        XCTAssertGreaterThan(kept.utf16.count, 50_000)
    }

    // MARK: - Prompts

    func testTasksPromptEmbedsASmallDocument() {
        let record = MCPFixture.coworkRecord()
        let result = prompt("meeting_tasks")
        XCTAssertEqual(result["description"], .string(MCPText.meetingTasksDescription))
        let messages = messages(result)
        XCTAssertEqual(messages.count, 2)
        XCTAssertEqual(messages.compactMap { $0["role"]?.stringValue }, ["user", "user"])

        let speakers = "\(MCPToolsTests.label(record, "s1")) (the Slovo user), Аня, \(MCPToolsTests.label(record, "s3"))"
        let date = MCPDates(now: MCPFixture.now, timeZone: MCPFixture.timeZone).localized(record.createdAt, language: .en)
        let expected = CoreStrings.string("integration.prompt.tasks", table: .integration, language: .en)
            .replacingOccurrences(of: "{title}", with: "Коворк 15 сентября")
            .replacingOccurrences(of: "{date}", with: date)
            .replacingOccurrences(of: "{duration}", with: "1:04:12")
            .replacingOccurrences(of: "{speakers}", with: speakers)
        XCTAssertEqual(messages.first?["content"]?["type"], "text")
        XCTAssertEqual(messages.first?["content"]?["text"], .string(expected))
        XCTAssertTrue(expected.hasSuffix("Do not invent anything."))

        let attachment = messages.last?["content"]
        XCTAssertEqual(attachment?["type"], "resource")
        XCTAssertEqual(attachment?["resource"]?["uri"], .string(transcriptURI(MCPFixture.coworkID)))
        XCTAssertEqual(attachment?["resource"]?["mimeType"], "text/markdown")
        XCTAssertEqual(attachment?["resource"]?["text"]?.stringValue, readText("slovo://meetings/latest/transcript.md"))
    }

    func testLargeMeetingPromptPointsToPaging() throws {
        try fixture.write(MCPToolsTests.longRecord())
        let id = MCPToolsTests.longID.uuidString.lowercased()
        let result = prompt("meeting_summary", ["meeting_id": .string(id), "detail": "full"])
        let attachment = messages(result).last?["content"]
        XCTAssertEqual(attachment?["type"], "text")
        let paging = CoreStrings.string("integration.prompt.paging", table: .integration, language: .en)
            .replacingOccurrences(of: "{meeting_id}", with: id)
        XCTAssertEqual(attachment?["text"], .string(paging))
    }

    func testPromptWordingInBothLanguages() {
        let russian = MCPTestClient.handler(archive: MeetingArchive(directories: fixture.directories), language: .ru,
                                            clock: MCPFakeClock(step: 1))
        let tasks = messages(prompt("meeting_tasks", handler: russian)).first?["content"]?["text"]?.stringValue ?? ""
        XCTAssertTrue(tasks.hasPrefix("Ниже расшифровка встречи из Slovo «Коворк 15 сентября» ("), tasks)
        XCTAssertTrue(tasks.contains("(это пользователь Slovo)"))
        XCTAssertTrue(tasks.hasSuffix("Ничего не придумывай."))
        XCTAssertFalse(tasks.contains("{"))

        let shortRussian = messages(prompt("meeting_summary", handler: russian)).first?["content"]?["text"]?.stringValue ?? ""
        XCTAssertTrue(shortRussian.hasPrefix("Сделай краткие итоги встречи «Коворк 15 сентября»"), shortRussian)
        let full = messages(prompt("meeting_summary", ["detail": "full"])).first?["content"]?["text"]?.stringValue ?? ""
        XCTAssertTrue(full.hasPrefix("Write a detailed summary of the meeting «Коворк 15 сентября»"), full)
        let defaulted = messages(prompt("meeting_summary", ["meeting_id": "", "detail": ""])).first?["content"]?["text"]?.stringValue ?? ""
        XCTAssertTrue(defaulted.hasPrefix("Write a short summary of the meeting «Коворк 15 сентября»"), defaulted)
        let planning = messages(prompt("meeting_tasks", ["meeting_id": "3f2a9c1e-aa"])).first?["content"]?["text"]?.stringValue ?? ""
        XCTAssertTrue(planning.contains("«Planning»"))
    }

    func testPromptForAMeetingWithoutTranscript() {
        for (reference, key) in [("11111111", "integration.mcp.stillProcessing"), ("d00dfeed", "integration.mcp.noTranscript")] {
            let reply = MCPTestClient.send(handler, MCPTestClient.request(
                method: "prompts/get", params: ["name": "meeting_tasks", "arguments": ["meeting_id": .string(reference)]]))
            XCTAssertEqual(MCPTestClient.errorCode(reply), -32602)
            XCTAssertEqual(reply?["error"]?["message"]?.stringValue,
                           CoreStrings.string(key, table: .integration, language: .en))
        }
    }

    func testTemplateFillIsSinglePass() {
        XCTAssertEqual(MCPText.fill("{title} · {speakers}", ["title": "{speakers}", "speakers": "Аня"]), "{speakers} · Аня")
        XCTAssertEqual(MCPText.fill("{unknown} {title", ["title": "x"]), "{unknown} {title")
        XCTAssertEqual(MCPText.fill("", ["title": "x"]), "")
    }
}
