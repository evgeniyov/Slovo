import Foundation
import XCTest
@testable import SlovoCore

/// Meetings held in memory, for protocol tests that need no files.
final class MCPStaticArchive: MeetingArchiveReading {
    var items: [ArchivedMeeting]

    init(_ records: [MeetingRecord] = []) {
        items = records.map { ArchivedMeeting(record: $0, recordModified: MCPFixture.now, recordSize: 100) }
    }

    func meetings() -> [ArchivedMeeting] {
        items.sorted(by: MeetingArchive.isNewer)
    }

    func meeting(id: UUID) -> ArchivedMeeting? {
        items.first { $0.record.id == id }
    }
}

/// Both protocol eras, golden exchanges and every row of the error table.
final class MCPProtocolTests: XCTestCase {
    private var access = MCPAccessSwitch()
    private var clock = MCPFakeClock()
    private var handler: MCPHandler!

    override func setUp() {
        access = MCPAccessSwitch()
        clock = MCPFakeClock()
        handler = MCPTestClient.handler(archive: MCPStaticArchive([MCPFixture.coworkRecord()]), access: access,
                                        clock: clock)
    }

    private func reply(_ raw: String) -> String? {
        handler.handle(line: Data(raw.utf8)).map { String(decoding: $0, as: UTF8.self) }
    }

    private var serverInfo: JSONValue {
        ["name": "slovo", "title": "Slovo", "version": "1.2.0"]
    }

    // MARK: - Legacy

    func testInitializeGoldenExchange() {
        let instructions = JSONValue.string(MCPText.instructions).serializedString
        let expected = #"{"id":1,"jsonrpc":"2.0","result":{"capabilities":{"prompts":{},"resources":{},"tools":{}},"instructions":"#
            + instructions + #","protocolVersion":"2025-06-18","serverInfo":{"name":"slovo","title":"Slovo","version":"1.2.0"}}}"#
        let raw = #"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}"#
        XCTAssertEqual(reply(raw), expected)
    }

    func testInitializeEchoesKnownVersionsAndOtherwiseAnswersTheNewest() {
        for (requested, answered) in [("2025-11-25", "2025-11-25"), ("2025-06-18", "2025-06-18"),
                                      ("2025-03-26", "2025-03-26"), ("2024-11-05", "2025-11-25"),
                                      ("2026-07-28", "2025-11-25")] {
            let result = MCPTestClient.result(handler, method: "initialize", params: ["protocolVersion": .string(requested)])
            XCTAssertEqual(result["protocolVersion"], .string(answered))
            XCTAssertEqual(result["serverInfo"], serverInfo)
            XCTAssertNil(result["resultType"])
        }
        XCTAssertEqual(MCPTestClient.result(handler, method: "initialize")["protocolVersion"], "2025-11-25")
    }

    func testNotificationsProduceNothing() {
        XCTAssertNil(reply(#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#))
        XCTAssertNil(reply(#"{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":3,"reason":"x"}}"#))
        XCTAssertNil(reply(#"{"jsonrpc":"2.0","method":"notifications/unknown"}"#))
        XCTAssertNil(reply(#"{"jsonrpc":"2.0","method":"tools/call","params":{"name":"list_meetings"}}"#))
    }

    func testPingGoldenExchange() {
        XCTAssertEqual(reply(#"{"jsonrpc":"2.0","id":7,"method":"ping"}"#), #"{"id":7,"jsonrpc":"2.0","result":{}}"#)
    }

    func testDiscoverNeedsModernMeta() {
        XCTAssertEqual(reply(#"{"jsonrpc":"2.0","id":2,"method":"server/discover"}"#),
                       #"{"error":{"code":-32602,"message":"server/discover requires _meta io.modelcontextprotocol/protocolVersion"},"id":2,"jsonrpc":"2.0"}"#)
    }

    // MARK: - Modern

    func testDiscoverGoldenExchange() {
        let result = MCPTestClient.result(handler, method: "server/discover", modern: true)
        let expected: JSONValue = [
            "resultType": "complete",
            "supportedVersions": ["2026-07-28", "2025-11-25", "2025-06-18", "2025-03-26"],
            "capabilities": ["tools": .object([:]), "resources": .object([:]), "prompts": .object([:])],
            "_meta": ["io.modelcontextprotocol/serverInfo": serverInfo],
            "instructions": .string(MCPText.instructions),
            "ttlMs": 3_600_000,
            "cacheScope": "public"
        ]
        XCTAssertEqual(result, expected)
    }

    func testUnsupportedModernVersionGoldenError() {
        let raw = #"{"jsonrpc":"2.0","id":"v","method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2099-01-01","io.modelcontextprotocol/clientCapabilities":{}}}}"#
        XCTAssertEqual(reply(raw),
                       #"{"error":{"code":-32022,"data":{"requested":"2099-01-01","supported":["2026-07-28","2025-11-25","2025-06-18","2025-03-26"]},"message":"Unsupported protocol version"},"id":"v","jsonrpc":"2.0"}"#)
    }

    func testMissingClientCapabilitiesIsInvalidParams() {
        for capabilities in ["", #","io.modelcontextprotocol/clientCapabilities":[]"#] {
            let raw = #"{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28""#
                + capabilities + "}}}"
            XCTAssertEqual(MCPTestClient.errorCode(MCPTestClient.send(handler, raw: raw)), -32602)
        }
    }

    func testInitializeAndPingDoNotExistInTheModernEra() {
        for method in ["initialize", "ping"] {
            let reply = MCPTestClient.send(handler, MCPTestClient.request(method: method, modern: true))
            XCTAssertEqual(MCPTestClient.errorCode(reply), -32601, method)
        }
    }

    func testEveryFeatureMethodInBothEras() {
        let calls: [(method: String, params: JSONValue?, cache: (Int, String)?)] = [
            ("tools/list", nil, (3_600_000, "public")),
            ("tools/call", ["name": "list_meetings", "arguments": ["limit": 1]], nil),
            ("resources/list", nil, (0, "private")),
            ("resources/templates/list", nil, (3_600_000, "public")),
            ("resources/read", ["uri": "slovo://meetings"], (0, "private")),
            ("prompts/list", nil, (3_600_000, "public")),
            ("prompts/get", ["name": "meeting_tasks"], nil)
        ]
        for call in calls {
            let legacy = MCPTestClient.result(handler, method: call.method, params: call.params)
            let modern = MCPTestClient.result(handler, method: call.method, params: call.params, modern: true)
            for key in ["resultType", "_meta", "ttlMs", "cacheScope"] {
                XCTAssertNil(legacy[key], "\(call.method) legacy \(key)")
            }
            XCTAssertEqual(modern["resultType"], "complete", call.method)
            XCTAssertEqual(modern["_meta"]?["io.modelcontextprotocol/serverInfo"], serverInfo, call.method)
            XCTAssertEqual(modern["ttlMs"]?.intValue, call.cache?.0, call.method)
            XCTAssertEqual(modern["cacheScope"]?.stringValue, call.cache?.1, call.method)
            var stripped = modern.objectValue ?? [:]
            for key in ["resultType", "_meta", "ttlMs", "cacheScope"] {
                stripped[key] = nil
            }
            XCTAssertEqual(JSONValue.object(stripped), legacy, call.method)
        }
    }

    func testErasInterleaveInOneHandlerAndFeaturesWorkBeforeInitialize() {
        XCTAssertFalse(MCPTestClient.tool(handler, "list_meetings").isError)
        XCTAssertNotNil(MCPTestClient.result(handler, method: "tools/list", modern: true)["resultType"])
        XCTAssertEqual(MCPTestClient.result(handler, method: "initialize")["protocolVersion"], "2025-11-25")
        XCTAssertFalse(MCPTestClient.tool(handler, "get_meeting", modern: true).isError)
        XCTAssertNil(MCPTestClient.result(handler, method: "resources/list")["resultType"])
        XCTAssertEqual(MCPTestClient.result(handler, method: "ping"), .object([:]))
    }

    // MARK: - Error table

    func testEnvelopeErrors() {
        XCTAssertEqual(reply("{not json"), #"{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}"#)
        XCTAssertEqual(reply(#"[{"jsonrpc":"2.0","id":1,"method":"ping"}]"#),
                       #"{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}"#)
        XCTAssertEqual(reply("42"), #"{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}"#)
        XCTAssertEqual(reply(#"{"id":3,"method":"ping"}"#),
                       #"{"error":{"code":-32600,"message":"Invalid Request"},"id":3,"jsonrpc":"2.0"}"#)
        XCTAssertEqual(reply(#"{"jsonrpc":"1.0","id":3,"method":"ping"}"#),
                       #"{"error":{"code":-32600,"message":"Invalid Request"},"id":3,"jsonrpc":"2.0"}"#)
        XCTAssertEqual(reply(#"{"jsonrpc":"2.0","id":"x"}"#),
                       #"{"error":{"code":-32600,"message":"Invalid Request"},"id":"x","jsonrpc":"2.0"}"#)
        XCTAssertEqual(reply(#"{"jsonrpc":"2.0","id":4,"method":5}"#),
                       #"{"error":{"code":-32600,"message":"Invalid Request"},"id":4,"jsonrpc":"2.0"}"#)
        for badID in ["null", "true", "[1]", "{}"] {
            XCTAssertEqual(reply(#"{"jsonrpc":"2.0","method":"ping","id":"# + badID + "}"),
                           #"{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}"#)
        }
        XCTAssertEqual(reply(#"{"jsonrpc":"2.0","id":5,"method":"resources/subscribe"}"#),
                       #"{"error":{"code":-32601,"message":"Method not found"},"id":5,"jsonrpc":"2.0"}"#)
        XCTAssertEqual(MCPTestClient.errorCode(MCPTestClient.send(handler, MCPTestClient.request(method: "logging/setLevel", modern: true))),
                       -32601)
        XCTAssertEqual(MCPTestClient.errorCode(MCPTestClient.send(handler, raw: #"{"jsonrpc":"2.0","id":6,"method":"tools/list","params":[1]}"#)),
                       -32602)
    }

    func testRequestIDsAreEchoedExactly() {
        XCTAssertEqual(reply(#"{"jsonrpc":"2.0","id":9007199254740993,"method":"ping"}"#),
                       #"{"id":9007199254740993,"jsonrpc":"2.0","result":{}}"#)
        XCTAssertEqual(reply(#"{"jsonrpc":"2.0","id":"req-1","method":"ping"}"#), #"{"id":"req-1","jsonrpc":"2.0","result":{}}"#)
        XCTAssertEqual(reply(#"{"jsonrpc":"2.0","id":-3,"method":"ping"}"#), #"{"id":-3,"jsonrpc":"2.0","result":{}}"#)
        XCTAssertEqual(reply(#"{"jsonrpc":"2.0","id":1.5,"method":"ping"}"#), #"{"id":1.5,"jsonrpc":"2.0","result":{}}"#)
        XCTAssertEqual(reply(#"{"jsonrpc":"2.0","id":"a\nb","method":"ping"}"#), #"{"id":"a\nb","jsonrpc":"2.0","result":{}}"#)
        // Numbers come back as they were written, even past what an `Int` or a `Double` holds.
        XCTAssertEqual(reply(#"{"jsonrpc":"2.0","id":12345678901234567890,"method":"ping"}"#),
                       #"{"id":12345678901234567890,"jsonrpc":"2.0","result":{}}"#)
        XCTAssertEqual(reply(#"{"jsonrpc":"2.0","id":1.50,"method":"ping"}"#), #"{"id":1.50,"jsonrpc":"2.0","result":{}}"#)
        XCTAssertEqual(reply(#"{"jsonrpc":"2.0","id":-0,"method":"ping"}"#), #"{"id":-0,"jsonrpc":"2.0","result":{}}"#)
        XCTAssertEqual(reply(#"{"jsonrpc":"2.0","id":99999999999999999999,"method":"nope"}"#),
                       #"{"error":{"code":-32601,"message":"Method not found"},"id":99999999999999999999,"jsonrpc":"2.0"}"#)
    }

    /// A request whose text holds half a surrogate pair is still answered, under its own id.
    func testALoneSurrogateStillGetsAnAnswerToItsID() throws {
        let raw = #"{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"search_meetings","arguments":{"query":"ab\ud83d"}}}"#
        let answer = try XCTUnwrap(MCPTestClient.send(handler, raw: raw))
        XCTAssertEqual(answer["id"], .integer(7))
        XCTAssertNil(answer["error"])
        XCTAssertNotNil(answer["result"])
    }

    func testToolCallProtocolErrors() {
        for modern in [false, true] {
            let unknown = MCPTestClient.send(handler, MCPTestClient.request(method: "tools/call", params: ["name": "delete_meeting"], modern: modern))
            XCTAssertEqual(MCPTestClient.errorCode(unknown), -32602)
            let nameless = MCPTestClient.send(handler, MCPTestClient.request(method: "tools/call", params: ["arguments": .object([:])], modern: modern))
            XCTAssertEqual(MCPTestClient.errorCode(nameless), -32602)
        }
        let result = MCPTestClient.result(handler, method: "tools/call", params: ["name": "list_meetings", "arguments": [1]])
        XCTAssertEqual(result["isError"], true)
    }

    func testListMethodsRejectCursors() {
        for method in ["tools/list", "prompts/list", "resources/list", "resources/templates/list"] {
            for modern in [false, true] {
                let reply = MCPTestClient.send(handler, MCPTestClient.request(method: method, params: ["cursor": "abc"], modern: modern))
                XCTAssertEqual(MCPTestClient.errorCode(reply), -32602, method)
            }
        }
    }

    func testResourceNotFoundCodeDependsOnTheEra() {
        let missing = "slovo://meetings/\(UUID().uuidString.lowercased())/transcript.md"
        for uri in ["slovo://meetings/nope", missing] {
            let legacy = MCPTestClient.send(handler, MCPTestClient.request(method: "resources/read", params: ["uri": .string(uri)]))
            XCTAssertEqual(MCPTestClient.errorCode(legacy), -32002)
            XCTAssertEqual(legacy?["error"]?["data"]?["uri"], .string(uri))
            let modern = MCPTestClient.send(handler, MCPTestClient.request(method: "resources/read", params: ["uri": .string(uri)], modern: true))
            XCTAssertEqual(MCPTestClient.errorCode(modern), -32602)
            XCTAssertEqual(modern?["error"]?["data"]?["uri"], .string(uri))
        }
        let noURI = MCPTestClient.send(handler, MCPTestClient.request(method: "resources/read", params: .object([:])))
        XCTAssertEqual(MCPTestClient.errorCode(noURI), -32602)
    }

    func testPromptErrors() {
        let cases: [JSONValue] = [
            ["name": "meeting_minutes"],
            .object([:]),
            ["name": "meeting_summary", "arguments": ["detail": "long"]],
            ["name": "meeting_summary", "arguments": ["meeting_id": "00000000"]],
            ["name": "meeting_tasks", "arguments": ["meeting_id": 5]],
            ["name": "meeting_tasks", "arguments": ["detail": "full"]],
            ["name": "meeting_tasks", "arguments": ["meeting_id": "../etc"]]
        ]
        for params in cases {
            for modern in [false, true] {
                let reply = MCPTestClient.send(handler, MCPTestClient.request(method: "prompts/get", params: params, modern: modern))
                XCTAssertEqual(MCPTestClient.errorCode(reply), -32602, params.serializedString)
            }
        }
    }

    func testInvalidToolArgumentsAreToolErrors() {
        let cases: [(String, JSONValue, String)] = [
            ("list_meetings", ["limit": 0], "limit: must be at least 1"),
            ("list_meetings", ["limit": "5"], "limit: must be an integer"),
            ("list_meetings", ["limit": 2.5], "limit: must be an integer"),
            ("list_meetings", ["colour": "red"], "colour: unknown argument"),
            ("list_meetings", ["since": "tomorrow"], "since: has an invalid format"),
            ("list_meetings", ["query": ""], "query: must be at least 1 character long"),
            ("get_meeting", ["meeting_id": "xyz"], "meeting_id: has an invalid format"),
            ("get_meeting", ["meeting_id": "latest\n"], "meeting_id: has an invalid format"),
            ("get_transcript", .object([:]), "meeting_id: is required"),
            ("get_transcript", ["meeting_id": "latest", "speaker_ids": ["s1", "s1"]], "speaker_ids: must not repeat items"),
            ("get_transcript", ["meeting_id": "latest", "speaker_ids": ["bob"]], "speaker_ids: has an invalid format"),
            ("get_transcript", ["meeting_id": "latest", "max_chars": 100], "max_chars: must be at least 2000"),
            ("get_transcript", ["meeting_id": "latest", "from": true], "from: must be a string or a number"),
            ("search_meetings", ["query": "a"], "query: must be at least 2 characters long"),
            ("list_highlights", ["limit": 201], "limit: must be at most 200")
        ]
        for (tool, arguments, detail) in cases {
            let reply = MCPTestClient.tool(handler, tool, arguments)
            XCTAssertTrue(reply.isError, "\(tool) \(arguments.serializedString)")
            XCTAssertTrue(reply.text.hasPrefix("Invalid argument: \(detail)"), "\(reply.text) should start with \(detail)")
        }
    }

    // MARK: - Lists

    func testToolsListMatchesTheSpecification() throws {
        let specification = try JSONValue(parsing: Data(Self.specificationTools.utf8))
        let tools = MCPTestClient.result(handler, method: "tools/list")["tools"]?.arrayValue ?? []
        XCTAssertEqual(tools.count, 8)
        let writingTools: Set<String> = [MCPText.markHighlight.name, MCPText.unmarkHighlight.name,
                                         MCPText.writeMeetingNotes.name]
        for (tool, expected) in zip(tools, specification.arrayValue ?? []) {
            var declared = tool.objectValue ?? [:]
            let title = declared["title"] ?? .null
            // The tools that write declare themselves as writing, and as replacing.
            let writes = tool["name"]?.stringValue.map(writingTools.contains) ?? false
            XCTAssertEqual(declared.removeValue(forKey: "annotations"), [
                "title": title, "readOnlyHint": .bool(!writes), "destructiveHint": .bool(writes),
                "idempotentHint": true, "openWorldHint": false
            ])
            XCTAssertEqual(declared.removeValue(forKey: "_meta"), ["anthropic/maxResultSizeChars": 50_000])
            XCTAssertEqual(JSONValue.object(declared), expected)
            XCTAssertEqual(tool["inputSchema"]?["type"], "object")
            XCTAssertEqual(tool["inputSchema"]?["additionalProperties"], false)
            XCTAssertLessThan(tool["description"]?.stringValue?.utf8.count ?? .max, 2_048)
        }
        XCTAssertLessThan(MCPText.instructions.utf8.count, 1_500)
    }

    func testListsAreTheSameWhateverTheAccessSetting() {
        let methods = ["tools/list", "prompts/list", "resources/templates/list"]
        let enabled = methods.map { MCPTestClient.result(handler, method: $0) }
        access.access = MCPAccess(enabled: false, maxAgeDays: 7)
        XCTAssertEqual(methods.map { MCPTestClient.result(handler, method: $0) }, enabled)
        XCTAssertEqual(MCPTestClient.result(handler, method: "tools/list", modern: true)["tools"], enabled[0]["tools"])
    }

    func testPromptsAndTemplatesListGolden() throws {
        let prompts = try JSONValue(parsing: Data(Self.specificationPrompts.utf8))
        XCTAssertEqual(MCPTestClient.result(handler, method: "prompts/list"), ["prompts": prompts])
        let templates = MCPTestClient.result(handler, method: "resources/templates/list")
        XCTAssertEqual(templates["resourceTemplates"]?.arrayValue?.count, 1)
        let template = templates["resourceTemplates"]?.arrayValue?.first
        XCTAssertEqual(template?["uriTemplate"], "slovo://meetings/{meeting_id}/transcript.md")
        XCTAssertEqual(template?["name"], "meeting-transcript")
        XCTAssertEqual(template?["mimeType"], "text/markdown")
    }

    // MARK: - Access and rate limit

    func testAccessOffAndOnBetweenRequests() {
        access.access.enabled = false
        let disabled = CoreStrings.string("integration.mcp.disabled", table: .integration, language: .en)
        XCTAssertEqual(MCPTestClient.result(handler, method: "initialize")["serverInfo"], serverInfo)
        XCTAssertNotNil(MCPTestClient.result(handler, method: "server/discover", modern: true)["capabilities"])
        XCTAssertEqual(MCPTestClient.result(handler, method: "ping"), .object([:]))
        XCTAssertEqual(MCPTestClient.result(handler, method: "tools/list")["tools"]?.arrayValue?.count, 8)
        XCTAssertEqual(MCPTestClient.result(handler, method: "prompts/list")["prompts"]?.arrayValue?.count, 2)
        for tool in MCPText.tools.map(\.name) {
            let reply = MCPTestClient.tool(handler, tool, tool == "search_meetings" ? ["query": "релиз"] : .object([:]))
            XCTAssertTrue(reply.isError)
            XCTAssertEqual(reply.text, disabled)
        }
        XCTAssertEqual(MCPTestClient.result(handler, method: "resources/list"), ["resources": .array([])])
        for (method, params) in [("resources/read", ["uri": "slovo://meetings"]), ("prompts/get", ["name": "meeting_tasks"])] as [(String, JSONValue)] {
            let reply = MCPTestClient.send(handler, MCPTestClient.request(method: method, params: params))
            XCTAssertEqual(MCPTestClient.errorCode(reply), -32602)
            XCTAssertEqual(reply?["error"]?["message"]?.stringValue, disabled)
        }

        access.access.enabled = true
        XCTAssertFalse(MCPTestClient.tool(handler, "list_meetings").isError)
        XCTAssertEqual(MCPTestClient.result(handler, method: "resources/list")["resources"]?.arrayValue?.count, 3)
        access.access.enabled = false
        XCTAssertTrue(MCPTestClient.tool(handler, "list_meetings").isError)
    }

    func testDisabledMessageFollowsTheInterfaceLanguage() {
        access.access.enabled = false
        let russian = MCPTestClient.handler(archive: MCPStaticArchive(), access: access, language: .ru)
        XCTAssertEqual(MCPTestClient.tool(russian, "list_meetings").text,
                       "Доступ Claude к Slovo выключен. Включите его: Slovo → Настройки → Интеграции.")
    }

    func testRateLimitOverAMinuteWindow() {
        for _ in 0..<60 {
            XCTAssertFalse(MCPTestClient.tool(handler, "list_meetings").isError)
        }
        let limited = MCPTestClient.tool(handler, "list_meetings")
        XCTAssertTrue(limited.isError)
        XCTAssertEqual(limited.text, "Too many requests. Try again in 60 s.")

        clock.advance(30)
        let read = MCPTestClient.send(handler, MCPTestClient.request(method: "resources/read", params: ["uri": "slovo://meetings"]))
        XCTAssertEqual(MCPTestClient.errorCode(read), -32603)
        XCTAssertEqual(read?["error"]?["message"], "Too many requests. Try again in 30 s.")
        let prompt = MCPTestClient.send(handler, MCPTestClient.request(method: "prompts/get", params: ["name": "meeting_tasks"]))
        XCTAssertEqual(MCPTestClient.errorCode(prompt), -32603)
        // Lists are not limited.
        XCTAssertNotNil(MCPTestClient.result(handler, method: "resources/list")["resources"])

        clock.advance(30)
        XCTAssertFalse(MCPTestClient.tool(handler, "list_meetings").isError)
        XCTAssertNotNil(MCPTestClient.result(handler, method: "resources/read", params: ["uri": "slovo://meetings"])["contents"])
    }

    func testVerboseSummaryHoldsNoContent() {
        var summaries: [MCPRequestSummary] = []
        handler.requestObserver = { summaries.append($0) }
        _ = MCPTestClient.tool(handler, "search_meetings", ["query": "прототип"])
        _ = MCPTestClient.send(handler, raw: "garbage")
        _ = MCPTestClient.send(handler, raw: #"{"jsonrpc":"2.0","method":"notifications/initialized"}"#)
        _ = MCPTestClient.send(handler, MCPTestClient.request(method: "секретный/метод"))
        XCTAssertEqual(summaries.count, 3)
        XCTAssertEqual(summaries[0].method, "tools/call")
        XCTAssertEqual(summaries[0].tool, "search_meetings")
        XCTAssertEqual(summaries[1].errorCode, -32700)
        XCTAssertNil(summaries[1].method)
        XCTAssertEqual(summaries[2].method, "unknown")
        XCTAssertEqual(summaries[2].errorCode, -32601)
    }

    // MARK: - The specification's JSON, verbatim

    static let specificationTools = #"""
        [
          {
            "name": "list_meetings",
            "title": "List Slovo meetings",
            "description": "List meetings recorded in Slovo (встречи, записи), newest first, with title, date, duration, status, speakers and the number of important moments. Use since/until for date ranges and query for title words. Returns the current local time.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "query": {"type": "string", "minLength": 1, "maxLength": 200, "description": "Words that must all appear in the title; case-insensitive, ё matches е."},
                "since": {"type": "string", "pattern": "^(today|yesterday|[1-9][0-9]{0,2}d|[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}(:[0-9]{2})?(Z|[+-][0-9]{2}:[0-9]{2})?)?)$", "description": "Start (inclusive): today, yesterday, 7d, 2026-09-01 or an ISO 8601 date-time in local time."},
                "until": {"type": "string", "pattern": "^(today|yesterday|[1-9][0-9]{0,2}d|[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}(:[0-9]{2})?(Z|[+-][0-9]{2}:[0-9]{2})?)?)$", "description": "End (exclusive), same formats as since."},
                "limit": {"type": "integer", "minimum": 1, "maximum": 100, "default": 20},
                "cursor": {"type": "string", "maxLength": 512, "description": "next_cursor from a previous call."}
              },
              "additionalProperties": false
            }
          },
          {
            "name": "get_meeting",
            "title": "Get a Slovo meeting",
            "description": "Details of one meeting: title, date, duration, speakers with talk time and which one is the user, important moments with their lines, and transcript size in pages. meeting_id \"latest\" is the newest transcribed meeting. Read the text with get_transcript.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "meeting_id": {"type": "string", "pattern": "^(latest|[0-9A-Fa-f][0-9A-Fa-f-]{7,35})$", "default": "latest"},
                "include_highlights": {"type": "boolean", "default": true}
              },
              "additionalProperties": false
            }
          },
          {
            "name": "get_transcript",
            "title": "Read a Slovo transcript",
            "description": "One page of a meeting transcript (расшифровка) as [mm:ss] Speaker: text lines, optionally limited to speakers or a time range. Call again with next_cursor until it is absent to read everything.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "meeting_id": {"type": "string", "pattern": "^(latest|[0-9A-Fa-f][0-9A-Fa-f-]{7,35})$"},
                "speaker_ids": {"type": "array", "items": {"type": "string", "pattern": "^(me|others|s[0-9]{1,3})$"}, "minItems": 1, "maxItems": 20, "uniqueItems": true, "description": "Speaker ids from get_meeting."},
                "from": {"type": ["string", "number"], "description": "Start of the range: seconds, mm:ss or h:mm:ss."},
                "to": {"type": ["string", "number"], "description": "End of the range (exclusive): seconds, mm:ss or h:mm:ss."},
                "max_chars": {"type": "integer", "minimum": 2000, "maximum": 48000, "default": 16000},
                "cursor": {"type": "string", "maxLength": 512}
              },
              "required": ["meeting_id"],
              "additionalProperties": false
            }
          },
          {
            "name": "search_meetings",
            "title": "Search Slovo transcripts",
            "description": "Find words in meeting transcripts, speaker names and titles (поиск по расшифровкам). Returns the meeting, time, speaker and a snippet for each hit, newest meetings first.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "query": {"type": "string", "minLength": 2, "maxLength": 200, "description": "Case-insensitive; ё matches е; all words must appear in one line."},
                "meeting_id": {"type": "string", "pattern": "^(latest|[0-9A-Fa-f][0-9A-Fa-f-]{7,35})$"},
                "since": {"type": "string", "pattern": "^(today|yesterday|[1-9][0-9]{0,2}d|[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}(:[0-9]{2})?(Z|[+-][0-9]{2}:[0-9]{2})?)?)$"},
                "until": {"type": "string", "pattern": "^(today|yesterday|[1-9][0-9]{0,2}d|[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}(:[0-9]{2})?(Z|[+-][0-9]{2}:[0-9]{2})?)?)$"},
                "limit": {"type": "integer", "minimum": 1, "maximum": 50, "default": 20}
              },
              "required": ["query"],
              "additionalProperties": false
            }
          },
          {
            "name": "list_highlights",
            "title": "List important moments",
            "description": "Moments marked as important (важные моменты) with time, speaker, note and the transcript line, for one meeting or across meetings, newest first.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "meeting_id": {"type": "string", "pattern": "^(latest|[0-9A-Fa-f][0-9A-Fa-f-]{7,35})$"},
                "since": {"type": "string", "pattern": "^(today|yesterday|[1-9][0-9]{0,2}d|[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}(:[0-9]{2})?(Z|[+-][0-9]{2}:[0-9]{2})?)?)$"},
                "until": {"type": "string", "pattern": "^(today|yesterday|[1-9][0-9]{0,2}d|[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}(:[0-9]{2})?(Z|[+-][0-9]{2}:[0-9]{2})?)?)$"},
                "limit": {"type": "integer", "minimum": 1, "maximum": 200, "default": 50}
              },
              "additionalProperties": false
            }
          },
          {
            "name": "mark_highlight",
            "title": "Mark an important moment",
            "description": "Mark a moment in a transcribed Slovo meeting as important (важный момент), the way a star on a transcript line does. time is a position from get_transcript or search_meetings: seconds, mm:ss or h:mm:ss. Marking the same time and note again changes nothing. Needs its own switch in Settings → Integrations.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "meeting_id": {"type": "string", "pattern": "^(latest|[0-9A-Fa-f][0-9A-Fa-f-]{7,35})$", "default": "latest"},
                "time": {"type": ["string", "number"], "description": "Position to mark: seconds, mm:ss or h:mm:ss, from get_transcript or search_meetings."},
                "note": {"type": "string", "minLength": 1, "maxLength": 500, "description": "Why this moment matters, in the user's language. One line, no line breaks."}
              },
              "required": ["time"],
              "additionalProperties": false
            }
          },
          {
            "name": "unmark_highlight",
            "title": "Remove an important moment",
            "description": "Remove one important moment (важный момент) by its id, from get_meeting, list_highlights or mark_highlight. Any moment can be removed, including ones the user marked in Slovo or on the phone, so remove only what the user asked you to. Removing an id that is already gone changes nothing. Needs its own switch in Settings → Integrations.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "meeting_id": {"type": "string", "pattern": "^(latest|[0-9A-Fa-f][0-9A-Fa-f-]{7,35})$", "default": "latest"},
                "highlight_id": {"type": "string", "pattern": "^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$", "description": "A highlight id from get_meeting, list_highlights or mark_highlight."}
              },
              "required": ["highlight_id"],
              "additionalProperties": false
            }
          },
          {
            "name": "write_meeting_notes",
            "title": "Write notes into a Slovo meeting",
            "description": "Save your own summary, decisions and tasks (итоги, решения, задачи) into a Slovo meeting, so the user finds them in the meeting card weeks later without asking again. Replaces the notes written before, never the transcript. Needs its own switch in Settings → Integrations.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "meeting_id": {"type": "string", "pattern": "^(latest|[0-9A-Fa-f][0-9A-Fa-f-]{7,35})$", "default": "latest"},
                "summary": {"type": "string", "minLength": 1, "maxLength": 2000, "description": "What the meeting was about, in the user's language. One line, no line breaks."},
                "decisions": {"type": "array", "items": {"type": "string", "minLength": 1, "maxLength": 500}, "maxItems": 50, "description": "What was decided, one decision per item."},
                "tasks": {"type": "array", "items": {"type": "string", "minLength": 1, "maxLength": 500}, "maxItems": 50, "description": "What someone is to do, one task per item; name the owner when the meeting did."}
              },
              "required": ["meeting_id"],
              "additionalProperties": false
            }
          }
        ]
        """#

    static let specificationPrompts = #"""
        [
          {"name": "meeting_tasks", "title": "Tasks from a meeting / Задачи из встречи",
           "description": "Turn a Slovo meeting into a task list with owners, due dates and timecodes.",
           "arguments": [{"name": "meeting_id", "description": "latest (default), a UUID or its first 8 characters", "required": false}]},
          {"name": "meeting_summary", "title": "Meeting summary / Итоги встречи",
           "description": "Summarize a Slovo meeting: decisions, tasks, open questions and important moments.",
           "arguments": [{"name": "meeting_id", "description": "latest (default), a UUID or its first 8 characters", "required": false},
                         {"name": "detail", "description": "short (default) or full", "required": false}]}
        ]
        """#
}
