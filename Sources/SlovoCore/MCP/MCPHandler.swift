import Foundation

/// What Claude may read and write right now, as the Settings → Integrations switches say.
public struct MCPAccess: Equatable, Sendable {
    public var enabled: Bool
    /// Whether Claude may write its conclusions into a meeting. Its own switch, and it
    /// means nothing while `enabled` is off.
    public var canWrite: Bool
    /// 0 = all meetings, otherwise meetings created within this many days.
    public var maxAgeDays: Int

    public init(enabled: Bool, canWrite: Bool = false, maxAgeDays: Int) {
        self.enabled = enabled
        self.canWrite = canWrite
        self.maxAgeDays = maxAgeDays
    }
}

/// What `Slovo --mcp --verbose` reports about one answered request. It never holds
/// arguments, ids or meeting content, because the Claude app keeps the server's stderr
/// in a log file.
public struct MCPRequestSummary: Equatable, Sendable {
    /// The JSON-RPC method, "unknown" for a method Slovo does not serve, nil when the
    /// line was not a valid request.
    public var method: String?
    /// The tool of a `tools/call` naming a known tool.
    public var tool: String?
    public var duration: TimeInterval
    /// The JSON-RPC error code of the reply, if it was an error.
    public var errorCode: Int?
    /// True when a tool answered with `isError`.
    public var isToolError: Bool

    public init(method: String? = nil, tool: String? = nil, duration: TimeInterval = 0, errorCode: Int? = nil,
                isToolError: Bool = false) {
        self.method = method
        self.tool = tool
        self.duration = duration
        self.errorCode = errorCode
        self.isToolError = isToolError
    }
}

/// The protocol revision family a request belongs to.
enum MCPEra: Equatable {
    /// 2025-11-25 and older: the `initialize` handshake.
    case legacy
    /// 2026-07-28: stateless requests carrying their version in `_meta`.
    case modern
}

/// A JSON-RPC error answer.
struct MCPRPCError: Error, Equatable {
    let code: Int
    let message: String
    let data: JSONValue?

    init(code: Int, message: String, data: JSONValue? = nil) {
        self.code = code
        self.message = message
        self.data = data
    }

    static let parseError = MCPRPCError(code: -32700, message: "Parse error")
    static let invalidRequest = MCPRPCError(code: -32600, message: "Invalid Request")
    static let methodNotFound = MCPRPCError(code: -32601, message: "Method not found")
    static let internalError = MCPRPCError(code: -32603, message: "Internal error")

    static func invalidParams(_ message: String, data: JSONValue? = nil) -> MCPRPCError {
        MCPRPCError(code: -32602, message: message, data: data)
    }

    static func unsupportedProtocolVersion(_ requested: JSONValue) -> MCPRPCError {
        let supported = JSONValue.array(MCPText.supportedProtocolVersions.map(JSONValue.string))
        return MCPRPCError(code: -32022, message: "Unsupported protocol version",
                           data: .object(["supported": supported, "requested": requested]))
    }

    /// Revision 2026-07-28 reports a missing resource as invalid params and forbids the
    /// older −32002, which legacy clients still expect.
    static func resourceNotFound(uri: String, era: MCPEra) -> MCPRPCError {
        MCPRPCError(code: era == .modern ? -32602 : -32002, message: "Resource not found",
                    data: .object(["uri": .string(String(uri.prefix(2048)))]))
    }
}

/// What one request reads at most once: the preferences, the clock and the meeting list.
final class MCPRequestContext {
    let era: MCPEra
    let now: Date
    let dates: MCPDates
    private let languageSource: () -> AppLanguage
    private let accessSource: () -> MCPAccess
    private let archive: MeetingArchiveReading

    init(era: MCPEra, now: Date, timeZone: TimeZone, language: @escaping () -> AppLanguage,
         access: @escaping () -> MCPAccess, archive: MeetingArchiveReading) {
        self.era = era
        self.now = now
        self.dates = MCPDates(now: now, timeZone: timeZone)
        self.languageSource = language
        self.accessSource = access
        self.archive = archive
    }

    lazy var language: AppLanguage = languageSource()
    lazy var access: MCPAccess = accessSource()

    /// Meetings within the age limit, newest first. Older meetings do not exist for any
    /// tool, resource or prompt.
    lazy var visibleMeetings: [ArchivedMeeting] = archive.meetings().filter { isVisible(createdAt: $0.record.createdAt) }

    func isVisible(createdAt: Date) -> Bool {
        let days = access.maxAgeDays
        guard days > 0 else { return true }
        return createdAt >= now.addingTimeInterval(-TimeInterval(days) * 86_400)
    }
}

/// Answers MCP requests from both protocol eras: the legacy `initialize` handshake and
/// stateless 2026-07-28 requests. Every read goes through `MeetingArchiveReading`, which
/// changes nothing; the only writes are `write_meeting_notes`, through `MeetingNotesWriting`,
/// and `mark_highlight`/`unmark_highlight`, through `MeetingHighlightsWriting` — both only
/// while the same switch is on.
public final class MCPHandler {
    let archive: MeetingArchiveReading
    /// Nil when this handler cannot write at all, whatever the switch says.
    let notes: MeetingNotesWriting?
    /// Nil when this handler cannot write at all, whatever the switch says.
    let highlights: MeetingHighlightsWriting?
    let access: () -> MCPAccess
    let language: () -> AppLanguage
    let now: () -> Date
    let timeZone: TimeZone
    let serverVersion: String
    let clock: UptimeProviding
    let validator = MCPSchemaValidator()
    let logger = SlovoLog.logger("MCP")
    private var rateLimitedRequestTimes: [TimeInterval] = []

    /// Called after every answered request with what `--verbose` may print.
    public var requestObserver: ((MCPRequestSummary) -> Void)?

    /// At most this many tool calls, resource reads and prompts in any window.
    static let rateLimitRequests = 60
    static let rateLimitWindow: TimeInterval = 60
    /// No tool result text is longer; each tool declares 50,000 to Claude Code.
    static let maximumResultCharacters = 48_000

    private static let knownMethods: Set<String> = [
        "initialize", "ping", "server/discover", "tools/list", "tools/call", "resources/list",
        "resources/templates/list", "resources/read", "prompts/list", "prompts/get",
        "notifications/initialized", "notifications/cancelled"
    ]

    private enum CacheHint {
        /// Fixed per build: tool, prompt and template lists.
        case shared
        /// Depends on the user's meetings and settings.
        case perUser
    }

    /// - Parameters:
    ///   - access: read before every tool call, resource read and prompt, so switching
    ///     access off in the running app applies to the next request.
    ///   - language: the interface language for error messages, labels and prompts.
    ///   - clock: monotonic time for the rate limit.
    public init(archive: MeetingArchiveReading, notes: MeetingNotesWriting? = nil,
                highlights: MeetingHighlightsWriting? = nil, access: @escaping () -> MCPAccess,
                language: @escaping () -> AppLanguage, now: @escaping () -> Date,
                timeZone: TimeZone = .current, serverVersion: String, clock: UptimeProviding = SystemUptime()) {
        self.archive = archive
        self.notes = notes
        self.highlights = highlights
        self.access = access
        self.language = language
        self.now = now
        self.timeZone = timeZone
        self.serverVersion = serverVersion
        self.clock = clock
    }

    /// One request line in, one reply line out (nil for notifications).
    public func handle(line: Data) -> Data? {
        let started = clock.uptime()
        var summary = MCPRequestSummary()
        var numericID: String?
        guard let reply = reply(to: line, numericID: &numericID, summary: &summary) else { return nil }
        summary.duration = max(0, clock.uptime() - started)
        requestObserver?(summary)
        return Self.serialized(reply, numericID: numericID)
    }

    /// The reply as one line, with a numeric id written exactly as the request wrote it: a
    /// client matches replies by id, and `12345678901234567890` must not come back as
    /// `1.2345678901234567e+19`.
    static func serialized(_ reply: JSONValue, numericID: String?) -> Data {
        guard let numericID, case let .object(fields) = reply, fields["id"] != nil, fields["id"] != .null else {
            return reply.serialized()
        }
        var output: [UInt8] = [UInt8(ascii: "{")]
        for (index, key) in fields.keys.sorted().enumerated() {
            if index > 0 {
                output.append(UInt8(ascii: ","))
            }
            output += Array(JSONValue.string(key).serialized())
            output.append(UInt8(ascii: ":"))
            if key == "id" {
                output += Array(numericID.utf8)
            } else if let value = fields[key] {
                output += Array(value.serialized())
            }
        }
        output.append(UInt8(ascii: "}"))
        return Data(output)
    }

    /// The answer to a line longer than `MCPServer.maximumLineBytes`, which is never parsed.
    static func oversizedLineReply() -> Data {
        errorMessage(id: .null, error: .parseError).serialized()
    }

    static func errorMessage(id: JSONValue, error: MCPRPCError) -> JSONValue {
        var body: [String: JSONValue] = ["code": .integer(error.code), "message": .string(error.message)]
        if let data = error.data {
            body["data"] = data
        }
        return .object(["jsonrpc": .string("2.0"), "id": id, "error": .object(body)])
    }

    var serverInfo: JSONValue {
        .object([
            "name": .string(MCPText.serverName), "title": .string(MCPText.serverTitle),
            "version": .string(serverVersion)
        ])
    }

    /// Seconds to wait when this request is over the rate limit; otherwise the request
    /// is counted and nil is returned.
    func consumeRateLimit() -> Int? {
        let current = clock.uptime()
        rateLimitedRequestTimes.removeAll { current - $0 >= Self.rateLimitWindow }
        guard rateLimitedRequestTimes.count < Self.rateLimitRequests else {
            let oldest = rateLimitedRequestTimes.first ?? current
            return max(1, Int((oldest + Self.rateLimitWindow - current).rounded(.up)))
        }
        rateLimitedRequestTimes.append(current)
        return nil
    }

    // MARK: - Envelope

    private func reply(to line: Data, numericID: inout String?, summary: inout MCPRequestSummary) -> JSONValue? {
        guard let (message, rawID) = try? JSONValue.parsingMessage(line) else {
            return failure(.parseError, id: .null, summary: &summary)
        }
        numericID = rawID
        // Batches are not part of MCP.
        guard case let .object(fields) = message else {
            return failure(.invalidRequest, id: .null, summary: &summary)
        }
        var id: JSONValue?
        if let rawID = fields["id"] {
            switch rawID {
            case .string, .integer, .number: id = rawID
            case .null, .bool, .array, .object: return failure(.invalidRequest, id: .null, summary: &summary)
            }
        }
        guard fields["jsonrpc"] == .string("2.0"), let method = fields["method"]?.stringValue else {
            return failure(.invalidRequest, id: id ?? .null, summary: &summary)
        }
        summary.method = Self.knownMethods.contains(method) ? method : "unknown"
        // Notifications get no reply; notifications/cancelled names a request that was
        // already answered, because requests are handled one at a time.
        guard let id else { return nil }

        do {
            let params: [String: JSONValue]
            switch fields["params"] {
            case .none, .some(.null): params = [:]
            case let .some(.object(object)): params = object
            case .some: throw MCPRPCError.invalidParams("params must be an object")
            }
            let context = MCPRequestContext(era: try era(of: params), now: now(), timeZone: timeZone,
                                            language: language, access: access, archive: archive)
            let result = try dispatch(method: method, params: params, context: context, summary: &summary)
            return .object(["jsonrpc": .string("2.0"), "id": id, "result": result])
        } catch let error as MCPRPCError {
            return failure(error, id: id, summary: &summary)
        } catch {
            logger.error("An MCP request failed unexpectedly")
            return failure(.internalError, id: id, summary: &summary)
        }
    }

    private func failure(_ error: MCPRPCError, id: JSONValue, summary: inout MCPRequestSummary) -> JSONValue {
        summary.errorCode = error.code
        return Self.errorMessage(id: id, error: error)
    }

    /// A request is modern when it carries the protocol version in `_meta`.
    private func era(of params: [String: JSONValue]) throws -> MCPEra {
        let meta = params["_meta"]?.objectValue
        guard let requested = meta?[MCPText.protocolVersionMetaKey] else { return .legacy }
        guard requested == .string(MCPText.modernProtocolVersion) else {
            throw MCPRPCError.unsupportedProtocolVersion(requested)
        }
        guard case .object? = meta?[MCPText.clientCapabilitiesMetaKey] else {
            throw MCPRPCError.invalidParams("Missing _meta \(MCPText.clientCapabilitiesMetaKey)")
        }
        return .modern
    }

    private func dispatch(method: String, params: [String: JSONValue], context: MCPRequestContext,
                          summary: inout MCPRequestSummary) throws -> JSONValue {
        let modern = context.era == .modern
        switch method {
        case "initialize":
            guard !modern else { throw MCPRPCError.methodNotFound }
            return initializeResult(params)
        case "ping":
            guard !modern else { throw MCPRPCError.methodNotFound }
            return .object([:])
        case "server/discover":
            guard modern else {
                throw MCPRPCError.invalidParams("server/discover requires _meta \(MCPText.protocolVersionMetaKey)")
            }
            let result: JSONValue = [
                "supportedVersions": .array(MCPText.supportedProtocolVersions.map(JSONValue.string)),
                "capabilities": Self.capabilities, "instructions": .string(MCPText.instructions)
            ]
            return decorate(result, context: context, cache: .shared)
        case "tools/list":
            try rejectCursor(params)
            return decorate(.object(["tools": .array(MCPSchemas.toolEntries)]), context: context, cache: .shared)
        case "tools/call":
            return decorate(try callTool(params: params, context: context, summary: &summary), context: context, cache: nil)
        case "resources/list":
            try rejectCursor(params)
            return decorate(listResources(context: context), context: context, cache: .perUser)
        case "resources/templates/list":
            try rejectCursor(params)
            return decorate(.object(["resourceTemplates": .array(MCPSchemas.resourceTemplates)]),
                            context: context, cache: .shared)
        case "resources/read":
            return decorate(try readResource(params: params, context: context), context: context, cache: .perUser)
        case "prompts/list":
            try rejectCursor(params)
            return decorate(.object(["prompts": .array(MCPSchemas.prompts)]), context: context, cache: .shared)
        case "prompts/get":
            return decorate(try getPrompt(params: params, context: context), context: context, cache: nil)
        default:
            throw MCPRPCError.methodNotFound
        }
    }

    private static let capabilities: JSONValue = [
        "tools": .object([:]), "resources": .object([:]), "prompts": .object([:])
    ]

    private func initializeResult(_ params: [String: JSONValue]) -> JSONValue {
        let requested = params["protocolVersion"]?.stringValue
        let version = requested.flatMap { MCPText.legacyProtocolVersions.contains($0) ? $0 : nil }
            ?? MCPText.defaultLegacyProtocolVersion
        return [
            "protocolVersion": .string(version), "capabilities": Self.capabilities, "serverInfo": serverInfo,
            "instructions": .string(MCPText.instructions)
        ]
    }

    /// Modern results carry `resultType`, the server info and cache hints; legacy results
    /// carry none of them.
    private func decorate(_ result: JSONValue, context: MCPRequestContext, cache: CacheHint?) -> JSONValue {
        guard context.era == .modern, case var .object(fields) = result else { return result }
        fields["resultType"] = .string("complete")
        var meta = fields["_meta"]?.objectValue ?? [:]
        meta[MCPText.serverInfoMetaKey] = serverInfo
        fields["_meta"] = .object(meta)
        switch cache {
        case .shared?:
            fields["ttlMs"] = .integer(3_600_000)
            fields["cacheScope"] = .string("public")
        case .perUser?:
            fields["ttlMs"] = .integer(0)
            fields["cacheScope"] = .string("private")
        case nil:
            break
        }
        return .object(fields)
    }

    /// Lists are returned whole, so no cursor Slovo gave out exists.
    private func rejectCursor(_ params: [String: JSONValue]) throws {
        if let cursor = params["cursor"], cursor != .null {
            throw MCPRPCError.invalidParams("Invalid cursor")
        }
    }

    private func callTool(params: [String: JSONValue], context: MCPRequestContext,
                          summary: inout MCPRequestSummary) throws -> JSONValue {
        guard let name = params["name"]?.stringValue else {
            throw MCPRPCError.invalidParams("tools/call requires a tool name")
        }
        guard MCPText.tools.contains(where: { $0.name == name }) else {
            throw MCPRPCError.invalidParams("Unknown tool")
        }
        summary.tool = name
        let outcome = try runTool(named: name, arguments: params["arguments"], context: context)
        if case .failure = outcome {
            summary.isToolError = true
        }
        return outcome.result
    }
}
