import Darwin
import Foundation
import SlovoCore

/// Whether Slovo appears in Claude's configuration files.
struct ClaudeRegistration: Equatable, Sendable {
    var claudeCode: Bool
    var claudeApp: Bool
}

/// Registration snippets for Claude Code and the Claude app, and read-only detection of
/// an existing registration. Slovo never writes Claude's files: they also hold other
/// servers and settings, so the user adds the entry themselves.
enum ClaudeIntegration {
    static let serverName = "slovo"
    /// Where the snippets should point, so the path survives rebuilds.
    static let installedExecutablePath = "/Applications/Slovo.app/Contents/MacOS/Slovo"
    /// `~/.claude.json` is usually small, but it keeps project history; a larger file is
    /// not something a registration check should load.
    private static let maximumConfigBytes = 64 * 1024 * 1024

    /// The running executable; a bare `.build` binary reports its own path.
    static var currentExecutablePath: String {
        Bundle.main.executablePath ?? installedExecutablePath
    }

    static func isInApplications(_ executablePath: String) -> Bool {
        executablePath.hasPrefix("/Applications/Slovo.app/")
    }

    /// `claude mcp add` in user scope, so every project sees it and no repository's
    /// `.mcp.json` ever contains it.
    static func claudeCodeCommand(executablePath: String) -> String {
        "claude mcp add --scope user --transport stdio \(serverName) -- \(shellQuoted(executablePath)) --mcp"
    }

    /// The same command split before the path with a shell line continuation, so a narrow
    /// view never breaks the path itself and text selected from it still runs.
    static func claudeCodeCommandForDisplay(executablePath: String) -> String {
        "claude mcp add --scope user --transport stdio \(serverName) -- \\\n  \(shellQuoted(executablePath)) --mcp"
    }

    /// The entry to merge into `mcpServers` of `claude_desktop_config.json`.
    static func claudeAppSnippet(executablePath: String) -> String {
        """
        "\(serverName)": {
          "command": \(jsonQuoted(executablePath)),
          "args": ["--mcp"]
        }
        """
    }

    static var claudeCodeConfigURL: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude.json")
    }

    static var claudeAppConfigURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Claude/claude_desktop_config.json")
    }

    static var claudeAppConfigExists: Bool {
        FileManager.default.fileExists(atPath: claudeAppConfigURL.path)
    }

    /// Reads both files away from the main actor and keeps only whether each lists a
    /// `slovo` server. Missing or unreadable files count as not registered.
    static func detectRegistration() async -> ClaudeRegistration {
        let codeURL = claudeCodeConfigURL
        let appURL = claudeAppConfigURL
        return await Task.detached(priority: .utility) {
            ClaudeRegistration(claudeCode: readJSON(codeURL).map(claudeCodeConfigListsSlovo) ?? false,
                               claudeApp: readJSON(appURL).map(claudeAppConfigListsSlovo) ?? false)
        }.value
    }

    /// Claude Code keeps user-scope servers in the top-level `mcpServers` and local-scope
    /// servers under `projects.<path>.mcpServers`.
    static func claudeCodeConfigListsSlovo(_ json: Any) -> Bool {
        guard let root = json as? [String: Any] else { return false }
        if hasSlovo(root["mcpServers"]) {
            return true
        }
        guard let projects = root["projects"] as? [String: Any] else { return false }
        return projects.values.contains { project in
            hasSlovo((project as? [String: Any])?["mcpServers"])
        }
    }

    static func claudeAppConfigListsSlovo(_ json: Any) -> Bool {
        hasSlovo((json as? [String: Any])?["mcpServers"])
    }

    private static func hasSlovo(_ servers: Any?) -> Bool {
        (servers as? [String: Any])?[serverName] != nil
    }

    private static func readJSON(_ url: URL) -> Any? {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              let size = attributes[.size] as? NSNumber, size.intValue <= maximumConfigBytes,
              let data = try? Data(contentsOf: url) else {
            return nil
        }
        return try? JSONSerialization.jsonObject(with: data)
    }

    /// Paths with spaces or quotes stay one argument when pasted into a shell.
    static func shellQuoted(_ text: String) -> String {
        let safe = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789/._-+")
        if !text.isEmpty, text.unicodeScalars.allSatisfy(safe.contains) {
            return text
        }
        return "'" + text.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    static func jsonQuoted(_ text: String) -> String {
        var result = "\""
        for scalar in text.unicodeScalars {
            switch scalar {
            case "\"": result += "\\\""
            case "\\": result += "\\\\"
            case "\n": result += "\\n"
            case "\t": result += "\\t"
            default:
                if scalar.value < 0x20 {
                    result += String(format: "\\u%04x", scalar.value)
                } else {
                    result.unicodeScalars.append(scalar)
                }
            }
        }
        return result + "\""
    }
}

/// Settings → Integrations → Check: starts the executable with `--mcp` the way Claude
/// does, sends `initialize`, `notifications/initialized` and one `list_meetings` call,
/// closes its input and waits at most five seconds for the answer.
enum MCPSelfCheck {
    enum Outcome: Equatable, Sendable {
        /// The `tools/call` reply arrived; `message` is its first text block or error message.
        case answered(isError: Bool, message: String?)
        case timedOut
        case exitedWithoutAnswer
        /// The process could not be started; the message is the system's.
        case couldNotStart(String)
    }

    static let timeout: TimeInterval = 5
    private static let callID = 2

    static func run(executable: URL, timeout: TimeInterval = MCPSelfCheck.timeout) async -> Outcome {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                continuation.resume(returning: runBlocking(executable: executable, timeout: timeout))
            }
        }
    }

    static var requestLines: [String] {
        [
            #"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"slovo-settings-check","version":"1"}}}"#,
            #"{"jsonrpc":"2.0","method":"notifications/initialized"}"#,
            #"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_meetings","arguments":{"limit":1}}}"#
        ]
    }

    private static func runBlocking(executable: URL, timeout: TimeInterval) -> Outcome {
        let process = Process()
        process.executableURL = executable
        process.arguments = ["--mcp"]
        let input = Pipe()
        let output = Pipe()
        process.standardInput = input
        process.standardOutput = output
        // Nothing reads stderr, so a chatty child must not fill a pipe and stall.
        process.standardError = FileHandle.nullDevice

        let collector = OutputCollector()
        output.fileHandleForReading.readabilityHandler = { handle in
            collector.append(handle.availableData)
        }
        defer {
            output.fileHandleForReading.readabilityHandler = nil
        }

        do {
            try process.run()
        } catch {
            return .couldNotStart(error.localizedDescription)
        }

        // A child that exits early would turn the write into SIGPIPE, which ends the
        // whole app; with the flag set the write fails with EPIPE instead.
        let writeHandle = input.fileHandleForWriting
        _ = fcntl(writeHandle.fileDescriptor, F_SETNOSIGPIPE, 1)
        let payload = Data((requestLines.joined(separator: "\n") + "\n").utf8)
        try? writeHandle.write(contentsOf: payload)
        try? writeHandle.close()

        let finished = collector.wait(timeout: timeout)
        if process.isRunning {
            process.terminate()
        }
        if let reply = collector.reply {
            return reply
        }
        return finished ? .exitedWithoutAnswer : .timedOut
    }

    /// Parses one output line; nil unless it is the reply to the `tools/call` request.
    static func parseReply(_ line: Data) -> Outcome? {
        guard let object = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
              (object["id"] as? NSNumber)?.intValue == callID else {
            return nil
        }
        if let error = object["error"] as? [String: Any] {
            return .answered(isError: true, message: error["message"] as? String)
        }
        let result = object["result"] as? [String: Any]
        let isError = (result?["isError"] as? Bool) ?? false
        let text = (result?["content"] as? [[String: Any]])?.first?["text"] as? String
        return .answered(isError: isError, message: text)
    }

    /// Collects the child's output from the pipe's handler thread and wakes the waiting
    /// thread once the reply or end of output arrives.
    private final class OutputCollector: @unchecked Sendable {
        private let lock = NSLock()
        private let done = DispatchSemaphore(value: 0)
        private var buffer = Data()
        private var signalled = false
        private var parsedReply: Outcome?

        var reply: Outcome? {
            lock.lock()
            defer { lock.unlock() }
            return parsedReply
        }

        func append(_ data: Data) {
            lock.lock()
            defer { lock.unlock() }
            guard !signalled else { return }
            if data.isEmpty {
                finish()
                return
            }
            buffer.append(data)
            while let newline = buffer.firstIndex(of: UInt8(ascii: "\n")) {
                let line = buffer[buffer.startIndex..<newline]
                buffer.removeSubrange(buffer.startIndex...newline)
                if let outcome = MCPSelfCheck.parseReply(Data(line)) {
                    parsedReply = outcome
                    finish()
                    return
                }
            }
        }

        /// True when the reply or end of output arrived before the timeout.
        func wait(timeout: TimeInterval) -> Bool {
            done.wait(timeout: .now() + timeout) == .success
        }

        private func finish() {
            signalled = true
            done.signal()
        }
    }
}
