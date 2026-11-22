import CoreFoundation
import Foundation
import XCTest
@testable import SlovoCore

/// The whole `Slovo` executable started as Claude starts it, `Slovo --mcp`, serving a
/// meeting the app's own `MeetingStore` saved: the headless mode runs before the app,
/// answers only with JSON lines, reads what the app wrote and changes nothing.
@MainActor
final class IntegrationMCPProcessTests: XCTestCase {
    private static var packageRoot: URL {
        URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    }

    /// A binary whose `main()` does not start the headless server first would open the menu
    /// bar app and listen to the dictation key next to the installed Slovo. The test runs only
    /// when the sources call it first thing and the debug binary was built after them.
    private static func binaryStartsTheHeadlessServer(_ binary: URL) -> Bool {
        let app = packageRoot.appendingPathComponent("Sources/Slovo/SlovoApp.swift")
        let mode = packageRoot.appendingPathComponent("Sources/Slovo/Support/MCPServerMode.swift")
        guard let appSource = try? String(contentsOf: app, encoding: .utf8),
              let modeSource = try? String(contentsOf: mode, encoding: .utf8),
              modeSource.contains("MCPServer(input:"),
              let main = appSource.range(of: "static func main()"),
              let brace = appSource.range(of: "{", range: main.upperBound..<appSource.endIndex),
              let call = appSource.range(of: "MCPServerMode.runIfRequested()", range: brace.upperBound..<appSource.endIndex),
              appSource[brace.upperBound..<call.lowerBound].allSatisfy(\.isWhitespace) else { return false }
        func modified(_ url: URL) -> Date? {
            (try? FileManager.default.attributesOfItem(atPath: url.path))?[.modificationDate] as? Date
        }
        guard let binaryDate = modified(binary), let appDate = modified(app), let modeDate = modified(mode),
              binaryDate >= appDate, binaryDate >= modeDate,
              let contents = try? Data(contentsOf: binary, options: .mappedIfSafe) else { return false }
        // Only debug builds accept the test folder and suite; a release binary would read the
        // user's own meetings and settings.
        return contents.range(of: Data("--defaults-suite".utf8)) != nil
    }

    func testAppExecutableServesAMeetingTheStoreSaved() throws {
        let binary = Self.packageRoot.appendingPathComponent(".build/debug/Slovo").resolvingSymlinksInPath()
        try XCTSkipUnless(FileManager.default.isExecutableFile(atPath: binary.path),
                          "\(binary.path) is missing; run swift build first.")
        try XCTSkipUnless(Self.binaryStartsTheHeadlessServer(binary),
                          "The debug Slovo binary does not start with MCPServerMode.runIfRequested() or is older than "
                          + "its sources; launching it could open the menu bar app, so the test is skipped.")

        let base = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoIntegrationMCP-\(UUID().uuidString)", isDirectory: true)
        let directories = AppDirectories(base: base)
        try directories.createFolders()
        defer { try? FileManager.default.removeItem(at: base) }
        let saved = try saveMeeting(in: directories)

        let suite = "local.slovo.tests.integration-mcp-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defaults.set(true, forKey: SettingsKey.mcpEnabled)
        defaults.set("ru", forKey: SettingsKey.appLanguage)
        CFPreferencesAppSynchronize(suite as CFString)
        defer {
            defaults.removePersistentDomain(forName: suite)
            CFPreferencesAppSynchronize(suite as CFString)
        }
        let before = MCPFileSnapshot.take(base)

        let process = Process()
        process.executableURL = binary
        process.arguments = ["--mcp", "--data-dir", base.path, "--defaults-suite", suite]
        let input = Pipe()
        let output = Pipe()
        let errors = Pipe()
        process.standardInput = input
        process.standardOutput = output
        process.standardError = errors
        let stdout = MCPStreamCollector(output.fileHandleForReading)
        let stderr = MCPStreamCollector(errors.fileHandleForReading)
        // A crashed child must not take the test runner down with SIGPIPE.
        _ = fcntl(input.fileHandleForWriting.fileDescriptor, F_SETNOSIGPIPE, 1)

        let launched = ProcessInfo.processInfo.systemUptime
        try process.run()
        defer {
            if process.isRunning {
                process.terminate()
            }
        }
        func send(_ line: String) throws {
            try input.fileHandleForWriting.write(contentsOf: Data((line + "\n").utf8))
        }

        try send(#"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"integration","version":"0"}}}"#)
        XCTAssertTrue(stdout.waitForLines(1, timeout: 60), stderr.text)
        let firstResponse = ProcessInfo.processInfo.systemUptime - launched
        FileHandle.standardError.write(Data(String(format: "Slovo --mcp answered its first request %.0f ms after launch\n",
                                                   firstResponse * 1000).utf8))

        try send(#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#)
        try send(#"{"jsonrpc":"2.0","id":2,"method":"tools/list"}"#)
        try send(#"{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_meetings","arguments":{"limit":1}}}"#)
        try send(#"{"jsonrpc":"2.0","id":4,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}"#)
        try send(#"{"jsonrpc":"2.0","id":5,"method":"resources/read","params":{"uri":"slovo://meetings/latest/transcript.md"}}"#)
        try send(#"{"jsonrpc":"2.0","id":6,"method":"prompts/get","params":{"name":"meeting_tasks"}}"#)
        XCTAssertTrue(stdout.waitForLines(6, timeout: 30), stderr.text)

        try input.fileHandleForWriting.close()
        let deadline = ProcessInfo.processInfo.systemUptime + 10
        while process.isRunning, ProcessInfo.processInfo.systemUptime < deadline {
            Thread.sleep(forTimeInterval: 0.02)
        }
        XCTAssertFalse(process.isRunning, "the server must exit when its input ends")
        guard !process.isRunning else { return }
        XCTAssertEqual(process.terminationStatus, 0)
        XCTAssertTrue(stdout.waitForEnd(timeout: 10))
        _ = stderr.waitForEnd(timeout: 10)

        let text = stdout.text
        XCTAssertTrue(text.hasSuffix("\n"))
        let replies = text.split(separator: "\n").map { try? JSONValue(parsing: Data($0.utf8)) }
        XCTAssertTrue(replies.allSatisfy { $0 != nil }, "stdout holds only JSON lines: \(text)")
        XCTAssertEqual(replies.compactMap { $0?["id"] }, [1, 2, 3, 4, 5, 6], text)
        guard replies.count == 6 else { return }

        let id = saved.id.uuidString.lowercased()
        XCTAssertEqual(replies[0]?["result"]?["serverInfo"]?["name"], "slovo")
        XCTAssertEqual(replies[1]?["result"]?["tools"]?.arrayValue?.compactMap { $0["name"]?.stringValue },
                       ["list_meetings", "get_meeting", "get_transcript", "search_meetings", "list_highlights",
                        "mark_highlight", "unmark_highlight", "write_meeting_notes"])
        let listed = replies[2]?["result"]?["structuredContent"]?["meetings"]?.arrayValue?.first
        XCTAssertEqual(listed?["id"], .string(id))
        XCTAssertEqual(listed?["title"], .string(saved.title))
        XCTAssertEqual(replies[3]?["result"]?["resultType"], "complete")

        let document = replies[4]?["result"]?["contents"]?.arrayValue?.first?["text"]?.stringValue ?? ""
        XCTAssertTrue(document.contains(#"<transcript source="slovo" untrusted="true">"#), document)
        XCTAssertTrue(document.contains("Аня"), document)
        XCTAssertTrue(document.contains("Давайте к пятнице соберём прототип."), document)
        XCTAssertTrue(document.contains("дедлайн"), document)

        let messages = replies[5]?["result"]?["messages"]?.arrayValue ?? []
        XCTAssertEqual(messages.count, 2)
        XCTAssertEqual(messages.first?["content"]?["type"], "text")
        XCTAssertTrue(messages.first?["content"]?["text"]?.stringValue?.contains(saved.title) ?? false)
        XCTAssertEqual(messages.last?["content"]?["type"], "resource")
        XCTAssertEqual(messages.last?["content"]?["resource"]?["uri"]?.stringValue?.lowercased(),
                       "slovo://meetings/\(id)/transcript.md")

        let log = stderr.text
        XCTAssertFalse(log.contains("Аня"), log)
        XCTAssertFalse(log.contains("прототип"), log)
        XCTAssertEqual(MCPFileSnapshot.take(base), before, "the server must not change the data folder")
    }

    /// A transcribed meeting with renamed speakers and a marked moment, saved by the app's
    /// store, which also writes `transcript.md`.
    private func saveMeeting(in directories: AppDirectories) throws -> MeetingRecord {
        let store = MeetingStore(directories: directories)
        store.transcriptLanguage = { .ru }
        var meeting = try store.create(title: "Коворк 15 сентября")
        meeting.status = .transcribed
        meeting.hasAudio = false
        meeting.duration = 812
        meeting.engineID = .parakeet
        meeting.speakers = [
            MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0, isMe: true),
            MeetingSpeaker(id: "s2", track: .microphone, number: 2, displayName: "Аня", colorIndex: 1)
        ]
        meeting.segments = [
            TranscriptSegment(start: 4, end: 9, speaker: .me, text: "Всем привет, начнём с планов на неделю.", speakerID: "s1"),
            TranscriptSegment(start: 758, end: 765, speaker: .me, text: "Давайте к пятнице соберём прототип.", speakerID: "s2")
        ]
        meeting.highlights = [MeetingHighlight(time: 760, note: "дедлайн", source: .recording)]
        meeting.diarization = MeetingDiarization(state: .applied, speakersPerTrack: ["microphone": 2],
                                                 model: DiarizationService.modelIdentifier)
        store.update(meeting)
        try store.flush()
        XCTAssertTrue(FileManager.default.fileExists(atPath: store.transcriptURL(for: meeting.id).path))
        return try XCTUnwrap(store.meeting(id: meeting.id))
    }
}
