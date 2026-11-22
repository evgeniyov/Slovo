import CoreFoundation
import Foundation
import XCTest
@testable import SlovoCore

/// A child process's output, collected as it arrives.
final class MCPStreamCollector: @unchecked Sendable {
    private let condition = NSCondition()
    private var data = Data()
    private var ended = false

    init(_ handle: FileHandle) {
        handle.readabilityHandler = { [weak self] handle in
            let chunk = handle.availableData
            guard let self else { return }
            self.condition.lock()
            if chunk.isEmpty {
                self.ended = true
                handle.readabilityHandler = nil
            } else {
                self.data.append(chunk)
            }
            self.condition.broadcast()
            self.condition.unlock()
        }
    }

    var text: String {
        condition.lock()
        defer { condition.unlock() }
        return String(decoding: data, as: UTF8.self)
    }

    /// Waits until at least `count` complete lines arrived; false on timeout or end of output.
    func waitForLines(_ count: Int, timeout: TimeInterval) -> Bool {
        let deadline = Date(timeIntervalSinceNow: timeout)
        condition.lock()
        defer { condition.unlock() }
        while data.filter({ $0 == UInt8(ascii: "\n") }).count < count {
            if ended || !condition.wait(until: deadline) {
                return data.filter { $0 == UInt8(ascii: "\n") }.count >= count
            }
        }
        return true
    }

    func waitForEnd(timeout: TimeInterval) -> Bool {
        let deadline = Date(timeIntervalSinceNow: timeout)
        condition.lock()
        defer { condition.unlock() }
        while !ended {
            if !condition.wait(until: deadline) { return ended }
        }
        return true
    }
}

/// Runs the built `Slovo --mcp` against a temporary data folder and a throwaway preferences
/// suite, the way Claude starts it.
final class MCPProcessTests: XCTestCase {
    private static var packageRoot: URL {
        URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    }

    /// Launching a binary whose `main()` does not start the headless server first would open
    /// the menu bar app instead, so the test runs only when the sources call it first thing
    /// and the binary is newer than those sources.
    private static func binaryStartsTheHeadlessServer(_ binary: URL) -> Bool {
        let app = packageRoot.appendingPathComponent("Sources/Slovo/SlovoApp.swift")
        let mode = packageRoot.appendingPathComponent("Sources/Slovo/Support/MCPServerMode.swift")
        guard let appSource = try? String(contentsOf: app, encoding: .utf8),
              let modeSource = try? String(contentsOf: mode, encoding: .utf8),
              modeSource.contains("dup2(STDERR_FILENO, STDOUT_FILENO)"),
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
        return contents.range(of: Data("--defaults-suite".utf8)) != nil
    }

    func testHeadlessServerAnswersOverStdioAndExitsOnEndOfInput() throws {
        let binary = Self.packageRoot.appendingPathComponent(".build/debug/Slovo").resolvingSymlinksInPath()
        try XCTSkipUnless(FileManager.default.isExecutableFile(atPath: binary.path),
                          "\(binary.path) is missing; build the package first.")
        try XCTSkipUnless(Self.binaryStartsTheHeadlessServer(binary),
                          "The built Slovo binary does not start with MCPServerMode.runIfRequested() or predates the "
                          + "sources; launching it could open the menu bar app, so the process test is skipped.")

        let fixture = try MCPFixture()
        try fixture.write(MCPFixture.coworkRecord())
        let suite = "local.slovo.tests.mcp-process-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defaults.set(true, forKey: SettingsKey.mcpEnabled)
        defaults.set("en", forKey: SettingsKey.appLanguage)
        CFPreferencesAppSynchronize(suite as CFString)
        defer {
            defaults.removePersistentDomain(forName: suite)
            CFPreferencesAppSynchronize(suite as CFString)
            fixture.remove()
        }
        let before = MCPFileSnapshot.take(fixture.base)

        let process = Process()
        process.executableURL = binary
        process.arguments = ["--mcp", "--verbose", "--data-dir", fixture.base.path, "--defaults-suite", suite]
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

        try send(#"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}"#)
        XCTAssertTrue(stdout.waitForLines(1, timeout: 60), stderr.text)
        let firstResponse = ProcessInfo.processInfo.systemUptime - launched
        XCTContext.runActivity(named: String(format: "First response %.0f ms after launch", firstResponse * 1000)) { _ in }

        try send(#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#)
        try send(#"{"jsonrpc":"2.0","id":2,"method":"tools/list"}"#)
        try send(#"{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_meetings","arguments":{"limit":1}}}"#)
        try send(#"{"jsonrpc":"2.0","id":4,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}"#)
        XCTAssertTrue(stdout.waitForLines(4, timeout: 30))

        // Switching access off in another process applies to the next request.
        defaults.set(false, forKey: SettingsKey.mcpEnabled)
        CFPreferencesAppSynchronize(suite as CFString)
        try send(#"{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"list_meetings","arguments":{}}}"#)
        XCTAssertTrue(stdout.waitForLines(5, timeout: 30))

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
        XCTAssertEqual(replies.count, 5, text)
        XCTAssertTrue(replies.allSatisfy { $0 != nil }, "stdout holds only JSON lines: \(text)")
        XCTAssertEqual(replies.compactMap { $0?["id"] }, [1, 2, 3, 4, 5])
        guard replies.count == 5 else { return }
        XCTAssertEqual(replies[0]?["result"]?["protocolVersion"], "2025-11-25")
        XCTAssertEqual(replies[0]?["result"]?["serverInfo"]?["version"], "dev")
        XCTAssertEqual(replies[1]?["result"]?["tools"]?.arrayValue?.count, 8)
        XCTAssertEqual(replies[2]?["result"]?["structuredContent"]?["meetings"]?.arrayValue?.first?["id"],
                       .string(MCPFixture.coworkID.uuidString.lowercased()))
        XCTAssertEqual(replies[3]?["result"]?["resultType"], "complete")
        XCTAssertEqual(replies[4]?["result"]?["isError"], true)
        XCTAssertEqual(replies[4]?["result"]?["content"]?.arrayValue?.first?["text"],
                       .string(CoreStrings.string("integration.mcp.disabled", table: .integration, language: .en)))

        let log = stderr.text
        XCTAssertTrue(log.contains("tools/call list_meetings"), log)
        XCTAssertFalse(log.contains("Коворк"))
        XCTAssertFalse(log.contains("Всем привет"))
        XCTAssertEqual(MCPFileSnapshot.take(fixture.base), before)
    }
}
