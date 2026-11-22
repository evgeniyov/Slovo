using System.Text;
using Slovo.Core.Mcp;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Mcp;

/// <summary>Both protocol eras, golden exchanges and every row of the error table (port of MCPProtocolTests).</summary>
public sealed class McpProtocolTests
{
    readonly AccessSwitch access = new();
    readonly FakeClock clock = new();
    readonly McpHandler handler;

    public McpProtocolTests()
    {
        handler = new McpHandler(new StaticArchive(), () => access.Access, () => AppLanguage.En, () => McpFixture.Now, "1.2.0",
            timeZone: McpFixture.TimeZone, clock: clock);
    }

    string? Reply(string raw) => McpClient.Raw(handler, raw);

    static JsonValue ServerInfo => JsonValue.Object(("name", "slovo"), ("title", "Slovo"), ("version", "1.2.0"));

    [Fact]
    public void InitializeGoldenExchange()
    {
        var instructions = JsonValue.Str(McpText.Instructions).SerializedString;
        var expected = """{"id":1,"jsonrpc":"2.0","result":{"capabilities":{"prompts":{},"resources":{},"tools":{}},"instructions":"""
            + instructions + ""","protocolVersion":"2025-06-18","serverInfo":{"name":"slovo","title":"Slovo","version":"1.2.0"}}}""";
        Assert.Equal(expected, Reply("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}"""));
    }

    [Theory]
    [InlineData("2025-11-25", "2025-11-25")]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2025-03-26", "2025-03-26")]
    [InlineData("2024-11-05", "2025-11-25")]
    [InlineData("2026-07-28", "2025-11-25")]
    public void InitializeEchoesKnownVersionsAndOtherwiseAnswersTheNewest(string requested, string answered)
    {
        var result = McpClient.Result(handler, "initialize", JsonValue.Object(("protocolVersion", requested)));
        Assert.Equal(JsonValue.Str(answered), result["protocolVersion"]);
        Assert.Equal(ServerInfo, result["serverInfo"]);
        Assert.Null(result["resultType"]);
        Assert.Equal(JsonValue.Str("2025-11-25"), McpClient.Result(handler, "initialize")["protocolVersion"]);
    }

    [Fact]
    public void NotificationsProduceNothing()
    {
        Assert.Null(Reply("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Null(Reply("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":3,"reason":"x"}}"""));
        Assert.Null(Reply("""{"jsonrpc":"2.0","method":"notifications/unknown"}"""));
        Assert.Null(Reply("""{"jsonrpc":"2.0","method":"tools/call","params":{"name":"list_meetings"}}"""));
    }

    [Fact]
    public void PingAndDiscoverGoldenExchanges()
    {
        Assert.Equal("""{"id":7,"jsonrpc":"2.0","result":{}}""", Reply("""{"jsonrpc":"2.0","id":7,"method":"ping"}"""));
        Assert.Equal("""{"error":{"code":-32602,"message":"server/discover requires _meta io.modelcontextprotocol/protocolVersion"},"id":2,"jsonrpc":"2.0"}""",
            Reply("""{"jsonrpc":"2.0","id":2,"method":"server/discover"}"""));
        var result = McpClient.Result(handler, "server/discover", modern: true);
        var expected = JsonValue.Object(
            ("resultType", "complete"),
            ("supportedVersions", JsonValue.Array("2026-07-28", "2025-11-25", "2025-06-18", "2025-03-26")),
            ("capabilities", JsonValue.Object(("tools", JsonValue.Object()), ("resources", JsonValue.Object()), ("prompts", JsonValue.Object()))),
            ("_meta", JsonValue.Object(("io.modelcontextprotocol/serverInfo", ServerInfo))),
            ("instructions", McpText.Instructions),
            ("ttlMs", 3_600_000),
            ("cacheScope", "public"));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ModernEraErrors()
    {
        Assert.Equal("""{"error":{"code":-32022,"data":{"requested":"2099-01-01","supported":["2026-07-28","2025-11-25","2025-06-18","2025-03-26"]},"message":"Unsupported protocol version"},"id":"v","jsonrpc":"2.0"}""",
            Reply("""{"jsonrpc":"2.0","id":"v","method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2099-01-01","io.modelcontextprotocol/clientCapabilities":{}}}}"""));
        foreach (var capabilities in new[] { "", ""","io.modelcontextprotocol/clientCapabilities":[]""" })
        {
            var raw = """{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28" """.TrimEnd()
                + capabilities + "}}}";
            Assert.Equal(-32602, McpClient.ErrorCode(McpClient.Send(handler, raw)));
        }
        foreach (var method in new[] { "initialize", "ping" })
        {
            Assert.Equal(-32601, McpClient.ErrorCode(McpClient.Send(handler, McpClient.Request(method, modern: true))));
        }
    }

    [Fact]
    public void EnvelopeErrors()
    {
        Assert.Equal("""{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}""", Reply("{not json"));
        Assert.Equal("""{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}""",
            Reply("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]"""));
        Assert.Equal("""{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}""", Reply("42"));
        Assert.Equal("""{"error":{"code":-32600,"message":"Invalid Request"},"id":3,"jsonrpc":"2.0"}""", Reply("""{"id":3,"method":"ping"}"""));
        Assert.Equal("""{"error":{"code":-32600,"message":"Invalid Request"},"id":3,"jsonrpc":"2.0"}""",
            Reply("""{"jsonrpc":"1.0","id":3,"method":"ping"}"""));
        Assert.Equal("""{"error":{"code":-32600,"message":"Invalid Request"},"id":"x","jsonrpc":"2.0"}""", Reply("""{"jsonrpc":"2.0","id":"x"}"""));
        Assert.Equal("""{"error":{"code":-32600,"message":"Invalid Request"},"id":4,"jsonrpc":"2.0"}""",
            Reply("""{"jsonrpc":"2.0","id":4,"method":5}"""));
        foreach (var badId in new[] { "null", "true", "[1]", "{}" })
        {
            Assert.Equal("""{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}""",
                Reply("""{"jsonrpc":"2.0","method":"ping","id":""" + badId + "}"));
        }
        Assert.Equal("""{"error":{"code":-32601,"message":"Method not found"},"id":5,"jsonrpc":"2.0"}""",
            Reply("""{"jsonrpc":"2.0","id":5,"method":"resources/subscribe"}"""));
        Assert.Equal(-32601, McpClient.ErrorCode(McpClient.Send(handler, McpClient.Request("logging/setLevel", modern: true))));
        Assert.Equal(-32602, McpClient.ErrorCode(McpClient.Send(handler, """{"jsonrpc":"2.0","id":6,"method":"tools/list","params":[1]}""")));
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":9007199254740993,"method":"ping"}""", """{"id":9007199254740993,"jsonrpc":"2.0","result":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":"req-1","method":"ping"}""", """{"id":"req-1","jsonrpc":"2.0","result":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":-3,"method":"ping"}""", """{"id":-3,"jsonrpc":"2.0","result":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1.5,"method":"ping"}""", """{"id":1.5,"jsonrpc":"2.0","result":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":"a\nb","method":"ping"}""", """{"id":"a\nb","jsonrpc":"2.0","result":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":12345678901234567890,"method":"ping"}""", """{"id":12345678901234567890,"jsonrpc":"2.0","result":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1.50,"method":"ping"}""", """{"id":1.50,"jsonrpc":"2.0","result":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":-0,"method":"ping"}""", """{"id":-0,"jsonrpc":"2.0","result":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":99999999999999999999,"method":"nope"}""", """{"error":{"code":-32601,"message":"Method not found"},"id":99999999999999999999,"jsonrpc":"2.0"}""")]
    public void RequestIdsAreEchoedExactly(string request, string reply) => Assert.Equal(reply, Reply(request));

    [Fact]
    public void ToolCallProtocolErrors()
    {
        foreach (var modern in new[] { false, true })
        {
            Assert.Equal(-32602, McpClient.ErrorCode(McpClient.Send(handler,
                McpClient.Request("tools/call", JsonValue.Object(("name", "delete_meeting")), modern))));
            Assert.Equal(-32602, McpClient.ErrorCode(McpClient.Send(handler,
                McpClient.Request("tools/call", JsonValue.Object(("arguments", JsonValue.Object())), modern))));
        }
        var result = McpClient.Result(handler, "tools/call", JsonValue.Object(("name", "list_meetings"), ("arguments", JsonValue.Array(1))));
        Assert.Equal(JsonValue.True, result["isError"]);
    }

    [Fact]
    public void ListMethodsRejectCursors()
    {
        foreach (var method in new[] { "tools/list", "prompts/list", "resources/list", "resources/templates/list" })
        {
            foreach (var modern in new[] { false, true })
            {
                Assert.Equal(-32602, McpClient.ErrorCode(McpClient.Send(handler,
                    McpClient.Request(method, JsonValue.Object(("cursor", "abc")), modern))));
            }
        }
    }

    [Theory]
    [InlineData("list_meetings", """{"limit":0}""", "limit: must be at least 1")]
    [InlineData("list_meetings", """{"limit":"5"}""", "limit: must be an integer")]
    [InlineData("list_meetings", """{"limit":2.5}""", "limit: must be an integer")]
    [InlineData("list_meetings", """{"colour":"red"}""", "colour: unknown argument")]
    [InlineData("list_meetings", """{"since":"tomorrow"}""", "since: has an invalid format")]
    [InlineData("list_meetings", """{"query":""}""", "query: must be at least 1 character long")]
    [InlineData("get_meeting", """{"meeting_id":"xyz"}""", "meeting_id: has an invalid format")]
    [InlineData("get_meeting", """{"meeting_id":"latest\n"}""", "meeting_id: has an invalid format")]
    [InlineData("get_transcript", "{}", "meeting_id: is required")]
    [InlineData("get_transcript", """{"meeting_id":"latest","speaker_ids":["s1","s1"]}""", "speaker_ids: must not repeat items")]
    [InlineData("get_transcript", """{"meeting_id":"latest","speaker_ids":["bob"]}""", "speaker_ids: has an invalid format")]
    [InlineData("get_transcript", """{"meeting_id":"latest","max_chars":100}""", "max_chars: must be at least 2000")]
    [InlineData("get_transcript", """{"meeting_id":"latest","from":true}""", "from: must be a string or a number")]
    [InlineData("search_meetings", """{"query":"a"}""", "query: must be at least 2 characters long")]
    [InlineData("list_highlights", """{"limit":201}""", "limit: must be at most 200")]
    [InlineData("write_meeting_notes", """{"meeting_id":"latest","decisions":[""]}""", "decisions: must be at least 1 character long")]
    [InlineData("mark_highlight", "{}", "time: is required")]
    public void InvalidToolArgumentsAreToolErrors(string tool, string arguments, string detail)
    {
        var reply = McpClient.Tool(handler, tool, JsonValue.Parse(arguments));
        Assert.True(reply.IsError);
        Assert.StartsWith("Invalid argument: " + detail, reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsListMatchesTheSpecification()
    {
        var tools = McpClient.Result(handler, "tools/list")["tools"]!.ArrayValue!;
        Assert.Equal(["list_meetings", "get_meeting", "get_transcript", "search_meetings", "list_highlights", "mark_highlight",
            "unmark_highlight", "write_meeting_notes"], tools.Select(tool => tool["name"]!.StringValue!));
        string[] writing = ["mark_highlight", "unmark_highlight", "write_meeting_notes"];
        foreach (var tool in tools)
        {
            var writes = writing.Contains(tool["name"]!.StringValue);
            Assert.Equal(JsonValue.Object(("title", tool["title"]!), ("readOnlyHint", !writes), ("destructiveHint", writes),
                ("idempotentHint", true), ("openWorldHint", false)), tool["annotations"]);
            Assert.Equal(JsonValue.Object(("anthropic/maxResultSizeChars", 50_000)), tool["_meta"]);
            Assert.Equal(JsonValue.Str("object"), tool["inputSchema"]!["type"]);
            Assert.Equal(JsonValue.False, tool["inputSchema"]!["additionalProperties"]);
            Assert.True(Encoding.UTF8.GetByteCount(tool["description"]!.StringValue!) < 2_048);
        }
        Assert.Equal(1_366, Encoding.UTF8.GetByteCount(McpText.Instructions));
    }

    /// <summary>Slovo.exe --mcp runs on a PC: its instructions name no Mac and stay under Claude Code's limit.</summary>
    [Fact]
    public void TheWindowsInstructionsDoNotMentionAMac()
    {
        Assert.DoesNotContain("Mac", McpText.WindowsInstructions);
        Assert.Contains("recorded on this computer", McpText.WindowsInstructions);
        Assert.Contains("this computer's local time zone", McpText.WindowsInstructions);
        Assert.True(Encoding.UTF8.GetByteCount(McpText.WindowsInstructions) < 1_500);
        // Everything else is the macOS text, line for line.
        Assert.Equal(McpText.Instructions.Split('\n').Length, McpText.WindowsInstructions.Split('\n').Length);
    }

    [Fact]
    public void ListsAreTheSameWhateverTheAccessSetting()
    {
        string[] methods = ["tools/list", "prompts/list", "resources/templates/list"];
        var enabled = methods.Select(method => McpClient.Result(handler, method)).ToList();
        access.Access = new McpAccess(false, false, 7);
        Assert.Equal(enabled, methods.Select(method => McpClient.Result(handler, method)));
        Assert.Equal(enabled[0]["tools"], McpClient.Result(handler, "tools/list", modern: true)["tools"]);
    }

    [Fact]
    public void AccessOffAndOnBetweenRequests()
    {
        access.Access = new McpAccess(false);
        var disabled = McpClient.Message("integration.mcp.disabled");
        Assert.Equal(ServerInfo, McpClient.Result(handler, "initialize")["serverInfo"]);
        Assert.NotNull(McpClient.Result(handler, "server/discover", modern: true)["capabilities"]);
        Assert.Equal(JsonValue.Object(), McpClient.Result(handler, "ping"));
        foreach (var tool in McpText.Tools)
        {
            var reply = McpClient.Tool(handler, tool.Name, tool.Name == "search_meetings" ? JsonValue.Object(("query", "релиз")) : null);
            Assert.True(reply.IsError);
            Assert.Equal(disabled, reply.Text);
        }
        Assert.Equal(JsonValue.Object(("resources", JsonValue.Array())), McpClient.Result(handler, "resources/list"));
        foreach (var (method, parameters) in new[] { ("resources/read", JsonValue.Object(("uri", "slovo://meetings"))), ("prompts/get", JsonValue.Object(("name", "meeting_tasks"))) })
        {
            var reply = McpClient.Send(handler, McpClient.Request(method, parameters));
            Assert.Equal(-32602, McpClient.ErrorCode(reply));
            Assert.Equal(JsonValue.Str(disabled), reply!["error"]!["message"]);
        }
        access.Access = new McpAccess(true);
        Assert.Equal(2, McpClient.Result(handler, "resources/list")["resources"]!.ArrayValue!.Count);
        Assert.Equal("Доступ Claude к Slovo выключен. Включите его: Slovo → Настройки → Интеграции.",
            McpClient.Message("integration.mcp.disabled", AppLanguage.Ru));
    }

    [Fact]
    public void RateLimitOverAMinuteWindow()
    {
        for (var index = 0; index < 60; index++)
        {
            Assert.False(McpClient.Tool(handler, "list_meetings").IsError);
        }
        var limited = McpClient.Tool(handler, "list_meetings");
        Assert.True(limited.IsError);
        Assert.Equal("Too many requests. Try again in 60 s.", limited.Text);

        clock.Advance(30);
        var read = McpClient.Send(handler, McpClient.Request("resources/read", JsonValue.Object(("uri", "slovo://meetings"))));
        Assert.Equal(-32603, McpClient.ErrorCode(read));
        Assert.Equal(JsonValue.Str("Too many requests. Try again in 30 s."), read!["error"]!["message"]);
        var prompt = McpClient.Send(handler, McpClient.Request("prompts/get", JsonValue.Object(("name", "meeting_tasks"))));
        Assert.Equal(-32603, McpClient.ErrorCode(prompt));
        // Lists are not limited.
        Assert.NotNull(McpClient.Result(handler, "resources/list")["resources"]);

        clock.Advance(30);
        Assert.False(McpClient.Tool(handler, "list_meetings").IsError);
        Assert.NotNull(McpClient.Result(handler, "resources/read", JsonValue.Object(("uri", "slovo://meetings")))["contents"]);
    }

    [Fact]
    public void VerboseSummaryHoldsNoContent()
    {
        var summaries = new List<McpRequestSummary>();
        handler.RequestObserver = summaries.Add;
        McpClient.Tool(handler, "search_meetings", JsonValue.Object(("query", "прототип")));
        McpClient.Send(handler, "garbage");
        McpClient.Send(handler, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        McpClient.Send(handler, McpClient.Request("секретный/метод"));
        Assert.Equal(3, summaries.Count);
        Assert.Equal("tools/call", summaries[0].Method);
        Assert.Equal("search_meetings", summaries[0].Tool);
        Assert.Equal(-32700, summaries[1].ErrorCode);
        Assert.Null(summaries[1].Method);
        Assert.Equal("unknown", summaries[2].Method);
        Assert.Equal(-32601, summaries[2].ErrorCode);
        Assert.Equal("slovo mcp: unknown 0.0 ms error -32601", McpServerMode.VerboseLine(summaries[2]));
        Assert.Equal("slovo mcp: tools/call list_meetings 12.3 ms tool error",
            McpServerMode.VerboseLine(new McpRequestSummary { Method = "tools/call", Tool = "list_meetings", Duration = 0.0123, IsToolError = true }));
    }

    [Fact]
    public void TemplateFillIsSinglePass()
    {
        Assert.Equal("{speakers} · Аня", McpText.Fill("{title} · {speakers}", new Dictionary<string, string> { ["title"] = "{speakers}", ["speakers"] = "Аня" }));
        Assert.Equal("{unknown} {title", McpText.Fill("{unknown} {title", new Dictionary<string, string> { ["title"] = "x" }));
        Assert.Equal("", McpText.Fill("", new Dictionary<string, string> { ["title"] = "x" }));
    }
}
