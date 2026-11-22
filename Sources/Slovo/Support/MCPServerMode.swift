import Foundation
import SlovoCore

/// `Slovo --mcp`: a headless MCP server over stdio for Claude, started before
/// `NSApplication` so no window, menu bar item, store, engine or capture session is
/// created. Reading changes nothing under Slovo's data folder; the only things it can write
/// are what Claude concluded about a meeting and which of its moments are marked important,
/// and only while that separate switch is on.
@MainActor
enum MCPServerMode {
    static let argument = "--mcp"

    /// Serves MCP and exits when `--mcp` is among the arguments; returns otherwise.
    static func runIfRequested() {
        let arguments = Array(CommandLine.arguments.dropFirst())
        guard arguments.contains(argument) else { return }

        // A client that closes its end first must not kill the process before it sees EOF.
        signal(SIGPIPE, SIG_IGN)
        // The protocol keeps the real stdout; descriptor 1 then points at stderr, so a stray
        // print from a library lands in the log instead of corrupting a JSON line.
        let protocolOutput = dup(STDOUT_FILENO)
        guard protocolOutput >= 0, dup2(STDERR_FILENO, STDOUT_FILENO) >= 0 else { exit(1) }

        var directories = AppDirectories.standard
        var preferences = SlovoPreferences()
        #if DEBUG
        // Tests point the server at a temporary folder and a throwaway preferences suite,
        // so they never read the user's meetings or settings.
        if let path = value(after: "--data-dir", in: arguments) {
            directories = AppDirectories(base: URL(fileURLWithPath: path, isDirectory: true))
        }
        if let suite = value(after: "--defaults-suite", in: arguments) {
            preferences = SlovoPreferences(domain: suite)
        }
        #endif

        let settings = preferences
        let handler = MCPHandler(archive: MeetingArchive(directories: directories),
                                 notes: MeetingNotesFile(directories: directories),
                                 highlights: MeetingHighlightsFile(directories: directories),
                                 access: { settings.access() },
                                 language: { settings.language() }, now: { Date() }, serverVersion: version)
        if arguments.contains("--verbose") {
            handler.requestObserver = { summary in
                writeVerboseLine(summary)
            }
        }
        MCPServer(input: STDIN_FILENO, output: protocolOutput, handler: handler).run()
        exit(0)
    }

    /// `CFBundleShortVersionString`, or `dev` for a binary outside the app bundle.
    private static var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
    }

    private static func value(after flag: String, in arguments: [String]) -> String? {
        guard let index = arguments.firstIndex(of: flag), index + 1 < arguments.count else { return nil }
        return arguments[index + 1]
    }

    /// Method, tool, duration and error code only: never arguments or meeting content.
    private nonisolated static func writeVerboseLine(_ summary: MCPRequestSummary) {
        var parts = ["slovo mcp:", summary.method ?? "invalid request"]
        if let tool = summary.tool {
            parts.append(tool)
        }
        parts.append(String(format: "%.1f ms", summary.duration * 1000))
        if let code = summary.errorCode {
            parts.append("error \(code)")
        }
        if summary.isToolError {
            parts.append("tool error")
        }
        FileHandle.standardError.write(Data((parts.joined(separator: " ") + "\n").utf8))
    }
}
