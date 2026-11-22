using System.Text;
using Slovo.Core.Logging;
using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Core.Mcp;

/// <summary>What Claude may read and write right now, as the Settings → Integrations switches say.</summary>
/// <param name="Enabled">The read switch.</param>
/// <param name="CanWrite">Whether Claude may write its conclusions into a meeting. Its own switch, and it means nothing while Enabled is off.</param>
/// <param name="MaxAgeDays">0 = all meetings, otherwise meetings created within this many days.</param>
public readonly record struct McpAccess(bool Enabled, bool CanWrite = false, int MaxAgeDays = 0);

/// <summary>
/// What <c>Slovo.exe --mcp --verbose</c> reports about one answered request. It never holds
/// arguments, ids or meeting content, because Claude keeps the server's stderr in a log file.
/// </summary>
public sealed record McpRequestSummary
{
    /// <summary>The JSON-RPC method, "unknown" for a method Slovo does not serve, null when the line was not a valid request.</summary>
    public string? Method { get; set; }
    /// <summary>The tool of a <c>tools/call</c> naming a known tool.</summary>
    public string? Tool { get; set; }
    /// <summary>Seconds.</summary>
    public double Duration { get; set; }
    /// <summary>The JSON-RPC error code of the reply, if it was an error.</summary>
    public int? ErrorCode { get; set; }
    /// <summary>True when a tool answered with <c>isError</c>.</summary>
    public bool IsToolError { get; set; }
}

/// <summary>The protocol revision family a request belongs to.</summary>
internal enum McpEra
{
    /// <summary>2025-11-25 and older: the <c>initialize</c> handshake.</summary>
    Legacy,
    /// <summary>2026-07-28: stateless requests carrying their version in <c>_meta</c>.</summary>
    Modern,
}

/// <summary>A JSON-RPC error answer.</summary>
internal sealed class McpRpcError(int code, string message, JsonValue? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public JsonValue? ErrorData { get; } = data;

    public static McpRpcError ParseError => new(-32700, "Parse error");
    public static McpRpcError InvalidRequest => new(-32600, "Invalid Request");
    public static McpRpcError MethodNotFound => new(-32601, "Method not found");
    public static McpRpcError InternalError => new(-32603, "Internal error");

    public static McpRpcError InvalidParams(string message, JsonValue? data = null) => new(-32602, message, data);

    public static McpRpcError UnsupportedProtocolVersion(JsonValue requested) => new(-32022, "Unsupported protocol version",
        JsonValue.Object(("supported", JsonValue.Array(McpText.SupportedProtocolVersions.Select(JsonValue.Str))), ("requested", requested)));

    /// <summary>
    /// Revision 2026-07-28 reports a missing resource as invalid params and forbids the older
    /// −32002, which legacy clients still expect.
    /// </summary>
    public static McpRpcError ResourceNotFound(string uri, McpEra era) => new(era == McpEra.Modern ? -32602 : -32002,
        "Resource not found", JsonValue.Object(("uri", Text.TextElements.Prefix(uri, 2048))));
}

/// <summary>What one request reads at most once: the preferences, the clock and the meeting list.</summary>
internal sealed class McpRequestContext
{
    readonly Func<AppLanguage> languageSource;
    readonly Func<McpAccess> accessSource;
    readonly IMeetingArchiveReading archive;
    AppLanguage? language;
    McpAccess? access;
    IReadOnlyList<ArchivedMeeting>? visibleMeetings;

    public McpRequestContext(McpEra era, DateTimeOffset now, TimeZoneInfo timeZone, Func<AppLanguage> language,
                             Func<McpAccess> access, IMeetingArchiveReading archive)
    {
        Era = era;
        Now = now;
        Dates = new McpDates(now, timeZone);
        languageSource = language;
        accessSource = access;
        this.archive = archive;
    }

    public McpEra Era { get; }
    public DateTimeOffset Now { get; }
    public McpDates Dates { get; }

    public AppLanguage Language => language ??= languageSource();

    public McpAccess Access => access ??= accessSource();

    /// <summary>Meetings within the age limit, newest first. Older meetings do not exist for any tool, resource or prompt.</summary>
    public IReadOnlyList<ArchivedMeeting> VisibleMeetings =>
        visibleMeetings ??= archive.Meetings().Where(meeting => IsVisible(meeting.Record.CreatedAt)).ToList();

    public bool IsVisible(DateTimeOffset createdAt)
    {
        var days = Access.MaxAgeDays;
        if (days <= 0)
        {
            return true;
        }
        return createdAt >= Now.AddSeconds(-(double)days * 86_400);
    }
}

/// <summary>
/// Answers MCP requests from both protocol eras: the legacy <c>initialize</c> handshake and
/// stateless 2026-07-28 requests (port of the macOS <c>MCPHandler</c>). Every read goes
/// through <see cref="IMeetingArchiveReading"/>, which changes nothing; the only writes are
/// <c>write_meeting_notes</c>, through <see cref="IMeetingNotesWriter"/>, and
/// <c>mark_highlight</c>/<c>unmark_highlight</c>, through <see cref="IMeetingHighlightsWriter"/>,
/// both only while the same switch is on.
/// </summary>
public sealed partial class McpHandler
{
    static readonly Logger Logger = Log.For("MCP");

    /// <summary>At most this many tool calls, resource reads and prompts in any window.</summary>
    internal const int RateLimitRequests = 60;
    internal const double RateLimitWindow = 60;
    /// <summary>No tool result text is longer; each tool declares 50,000 to Claude Code.</summary>
    internal const int MaximumResultCharacters = 48_000;

    static readonly HashSet<string> KnownMethods = new(StringComparer.Ordinal)
    {
        "initialize", "ping", "server/discover", "tools/list", "tools/call", "resources/list",
        "resources/templates/list", "resources/read", "prompts/list", "prompts/get",
        "notifications/initialized", "notifications/cancelled",
    };

    static readonly JsonValue Capabilities = JsonValue.Object(
        ("tools", JsonValue.Object()), ("resources", JsonValue.Object()), ("prompts", JsonValue.Object()));

    readonly IMeetingArchiveReading archive;
    /// <summary>Null when this handler cannot write at all, whatever the switch says.</summary>
    readonly IMeetingNotesWriter? notes;
    /// <summary>Null when this handler cannot write at all, whatever the switch says.</summary>
    readonly IMeetingHighlightsWriter? highlights;
    readonly Func<McpAccess> access;
    readonly Func<AppLanguage> language;
    readonly Func<DateTimeOffset> now;
    readonly TimeZoneInfo timeZone;
    readonly string serverVersion;
    readonly IUptime clock;
    readonly McpSchemaValidator validator = new();
    readonly List<double> rateLimitedRequestTimes = [];

    enum CacheHint
    {
        /// <summary>Fixed per build: tool, prompt and template lists.</summary>
        Shared,
        /// <summary>Depends on the user's meetings and settings.</summary>
        PerUser,
    }

    /// <param name="access">Read before every tool call, resource read and prompt, so switching access off in the running app applies to the next request.</param>
    /// <param name="language">The interface language for error messages, labels and prompts.</param>
    /// <param name="clock">Monotonic time for the rate limit.</param>
    /// <summary>What <c>initialize</c> tells Claude about the server (<see cref="McpText.Instructions"/> unless the app passes its own).</summary>
    readonly string instructions;

    public McpHandler(IMeetingArchiveReading archive, Func<McpAccess> access, Func<AppLanguage> language,
                      Func<DateTimeOffset> now, string serverVersion, IMeetingNotesWriter? notes = null,
                      IMeetingHighlightsWriter? highlights = null, TimeZoneInfo? timeZone = null, IUptime? clock = null,
                      string? instructions = null)
    {
        this.instructions = instructions ?? McpText.Instructions;
        this.archive = archive;
        this.notes = notes;
        this.highlights = highlights;
        this.access = access;
        this.language = language;
        this.now = now;
        this.timeZone = timeZone ?? TimeZoneInfo.Local;
        this.serverVersion = serverVersion;
        this.clock = clock ?? SystemUptime.Shared;
    }

    /// <summary>Called after every answered request with what <c>--verbose</c> may print.</summary>
    public Action<McpRequestSummary>? RequestObserver { get; set; }

    /// <summary>One request line in, one reply line out (null for notifications).</summary>
    public byte[]? Handle(ReadOnlySpan<byte> line)
    {
        var started = clock.Now;
        var summary = new McpRequestSummary();
        string? numericId = null;
        var reply = Reply(line, ref numericId, summary);
        if (reply is null)
        {
            return null;
        }
        summary.Duration = Math.Max(0, clock.Now - started);
        RequestObserver?.Invoke(summary);
        return Serialized(reply, numericId);
    }

    /// <summary>
    /// The reply as one line, with a numeric id written exactly as the request wrote it: a
    /// client matches replies by id, and <c>12345678901234567890</c> must not come back as
    /// <c>1.2345678901234567e+19</c>.
    /// </summary>
    internal static byte[] Serialized(JsonValue reply, string? numericId)
    {
        if (numericId is null || reply.ObjectValue is not { } fields || !fields.TryGetValue("id", out var id) || id.IsNull)
        {
            return reply.Serialized();
        }
        var output = new StringBuilder("{");
        var first = true;
        foreach (var (key, value) in reply.SortedMembers)
        {
            if (!first)
            {
                output.Append(',');
            }
            first = false;
            output.Append(JsonValue.Str(key).SerializedString).Append(':');
            output.Append(key == "id" ? numericId : value.SerializedString);
        }
        output.Append('}');
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    /// <summary>The answer to a line longer than <see cref="McpServer.MaximumLineBytes"/>, which is never parsed.</summary>
    internal static byte[] OversizedLineReply() => ErrorMessage(JsonValue.Null, McpRpcError.ParseError).Serialized();

    internal static JsonValue ErrorMessage(JsonValue id, McpRpcError error)
    {
        var body = new List<(string, JsonValue)> { ("code", error.Code), ("message", error.Message) };
        if (error.ErrorData is { } data)
        {
            body.Add(("data", data));
        }
        return JsonValue.Object(("jsonrpc", "2.0"), ("id", id), ("error", JsonValue.Object([.. body])));
    }

    JsonValue ServerInfo => JsonValue.Object(("name", McpText.ServerName), ("title", McpText.ServerTitle), ("version", serverVersion));

    /// <summary>Seconds to wait when this request is over the rate limit; otherwise the request is counted and null is returned.</summary>
    internal int? ConsumeRateLimit()
    {
        var current = clock.Now;
        rateLimitedRequestTimes.RemoveAll(time => current - time >= RateLimitWindow);
        if (rateLimitedRequestTimes.Count >= RateLimitRequests)
        {
            var oldest = rateLimitedRequestTimes.Count > 0 ? rateLimitedRequestTimes[0] : current;
            return Math.Max(1, (int)Math.Ceiling(oldest + RateLimitWindow - current));
        }
        rateLimitedRequestTimes.Add(current);
        return null;
    }

    // MARK: - Envelope

    JsonValue? Reply(ReadOnlySpan<byte> line, ref string? numericId, McpRequestSummary summary)
    {
        JsonValue message;
        try
        {
            (message, var rawId) = JsonValue.ParseMessage(line);
            numericId = rawId;
        }
        catch (JsonParseException)
        {
            return Failure(McpRpcError.ParseError, JsonValue.Null, summary);
        }
        // Batches are not part of MCP.
        if (message.ObjectValue is not { } fields)
        {
            return Failure(McpRpcError.InvalidRequest, JsonValue.Null, summary);
        }
        JsonValue? id = null;
        if (fields.TryGetValue("id", out var rawValue))
        {
            if (rawValue.Kind is JsonKind.String or JsonKind.Integer or JsonKind.Number)
            {
                id = rawValue;
            }
            else
            {
                return Failure(McpRpcError.InvalidRequest, JsonValue.Null, summary);
            }
        }
        if (!fields.TryGetValue("jsonrpc", out var version) || version != JsonValue.Str("2.0")
            || !fields.TryGetValue("method", out var methodValue) || methodValue.StringValue is not { } method)
        {
            return Failure(McpRpcError.InvalidRequest, id ?? JsonValue.Null, summary);
        }
        summary.Method = KnownMethods.Contains(method) ? method : "unknown";
        // Notifications get no reply; notifications/cancelled names a request that was
        // already answered, because requests are handled one at a time.
        if (id is null)
        {
            return null;
        }

        try
        {
            IReadOnlyDictionary<string, JsonValue> parameters;
            if (!fields.TryGetValue("params", out var rawParameters) || rawParameters.IsNull)
            {
                parameters = new Dictionary<string, JsonValue>();
            }
            else if (rawParameters.ObjectValue is { } parameterObject)
            {
                parameters = parameterObject;
            }
            else
            {
                throw McpRpcError.InvalidParams("params must be an object");
            }
            var context = new McpRequestContext(Era(parameters), now(), timeZone, language, access, archive);
            var result = Dispatch(method, parameters, context, summary);
            return JsonValue.Object(("jsonrpc", "2.0"), ("id", id), ("result", result));
        }
        catch (McpRpcError error)
        {
            return Failure(error, id, summary);
        }
        catch (Exception error)
        {
            Logger.Error("An MCP request failed unexpectedly: " + error.GetType().Name);
            return Failure(McpRpcError.InternalError, id, summary);
        }
    }

    static JsonValue Failure(McpRpcError error, JsonValue id, McpRequestSummary summary)
    {
        summary.ErrorCode = error.Code;
        return ErrorMessage(id, error);
    }

    /// <summary>A request is modern when it carries the protocol version in <c>_meta</c>.</summary>
    static McpEra Era(IReadOnlyDictionary<string, JsonValue> parameters)
    {
        var meta = parameters.TryGetValue("_meta", out var rawMeta) ? rawMeta.ObjectValue : null;
        if (meta is null || !meta.TryGetValue(McpText.ProtocolVersionMetaKey, out var requested))
        {
            return McpEra.Legacy;
        }
        if (requested != JsonValue.Str(McpText.ModernProtocolVersion))
        {
            throw McpRpcError.UnsupportedProtocolVersion(requested);
        }
        if (!meta.TryGetValue(McpText.ClientCapabilitiesMetaKey, out var capabilities) || capabilities.Kind != JsonKind.Object)
        {
            throw McpRpcError.InvalidParams($"Missing _meta {McpText.ClientCapabilitiesMetaKey}");
        }
        return McpEra.Modern;
    }

    JsonValue Dispatch(string method, IReadOnlyDictionary<string, JsonValue> parameters, McpRequestContext context,
                       McpRequestSummary summary)
    {
        var modern = context.Era == McpEra.Modern;
        switch (method)
        {
            case "initialize":
                if (modern)
                {
                    throw McpRpcError.MethodNotFound;
                }
                return InitializeResult(parameters);
            case "ping":
                if (modern)
                {
                    throw McpRpcError.MethodNotFound;
                }
                return JsonValue.Object();
            case "server/discover":
                if (!modern)
                {
                    throw McpRpcError.InvalidParams($"server/discover requires _meta {McpText.ProtocolVersionMetaKey}");
                }
                return Decorate(JsonValue.Object(
                    ("supportedVersions", JsonValue.Array(McpText.SupportedProtocolVersions.Select(JsonValue.Str))),
                    ("capabilities", Capabilities), ("instructions", instructions)), context, CacheHint.Shared);
            case "tools/list":
                RejectCursor(parameters);
                return Decorate(JsonValue.Object(("tools", JsonValue.Array(McpSchemas.ToolEntries))), context, CacheHint.Shared);
            case "tools/call":
                return Decorate(CallTool(parameters, context, summary), context, null);
            case "resources/list":
                RejectCursor(parameters);
                return Decorate(ListResources(context), context, CacheHint.PerUser);
            case "resources/templates/list":
                RejectCursor(parameters);
                return Decorate(JsonValue.Object(("resourceTemplates", JsonValue.Array(McpSchemas.ResourceTemplates))),
                    context, CacheHint.Shared);
            case "resources/read":
                return Decorate(ReadResource(parameters, context), context, CacheHint.PerUser);
            case "prompts/list":
                RejectCursor(parameters);
                return Decorate(JsonValue.Object(("prompts", JsonValue.Array(McpSchemas.Prompts))), context, CacheHint.Shared);
            case "prompts/get":
                return Decorate(GetPrompt(parameters, context), context, null);
            default:
                throw McpRpcError.MethodNotFound;
        }
    }

    JsonValue InitializeResult(IReadOnlyDictionary<string, JsonValue> parameters)
    {
        var requested = parameters.TryGetValue("protocolVersion", out var value) ? value.StringValue : null;
        var version = requested is not null && McpText.LegacyProtocolVersions.Contains(requested)
            ? requested
            : McpText.DefaultLegacyProtocolVersion;
        return JsonValue.Object(
            ("protocolVersion", version), ("capabilities", Capabilities), ("serverInfo", ServerInfo),
            ("instructions", instructions));
    }

    /// <summary>Modern results carry <c>resultType</c>, the server info and cache hints; legacy results carry none of them.</summary>
    JsonValue Decorate(JsonValue result, McpRequestContext context, CacheHint? cache)
    {
        if (context.Era != McpEra.Modern || result.ObjectValue is not { } original)
        {
            return result;
        }
        var fields = original.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        fields["resultType"] = "complete";
        var meta = fields.TryGetValue("_meta", out var existing) && existing.ObjectValue is { } existingMeta
            ? existingMeta.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        meta[McpText.ServerInfoMetaKey] = ServerInfo;
        fields["_meta"] = JsonValue.Object(meta);
        switch (cache)
        {
            case CacheHint.Shared:
                fields["ttlMs"] = 3_600_000;
                fields["cacheScope"] = "public";
                break;
            case CacheHint.PerUser:
                fields["ttlMs"] = 0;
                fields["cacheScope"] = "private";
                break;
        }
        return JsonValue.Object(fields);
    }

    /// <summary>Lists are returned whole, so no cursor Slovo gave out exists.</summary>
    static void RejectCursor(IReadOnlyDictionary<string, JsonValue> parameters)
    {
        if (parameters.TryGetValue("cursor", out var cursor) && !cursor.IsNull)
        {
            throw McpRpcError.InvalidParams("Invalid cursor");
        }
    }

    JsonValue CallTool(IReadOnlyDictionary<string, JsonValue> parameters, McpRequestContext context, McpRequestSummary summary)
    {
        if (!parameters.TryGetValue("name", out var nameValue) || nameValue.StringValue is not { } name)
        {
            throw McpRpcError.InvalidParams("tools/call requires a tool name");
        }
        if (!McpText.Tools.Any(tool => tool.Name == name))
        {
            throw McpRpcError.InvalidParams("Unknown tool");
        }
        summary.Tool = name;
        var outcome = RunTool(name, parameters.TryGetValue("arguments", out var arguments) ? arguments : null, context);
        if (outcome.IsFailure)
        {
            summary.IsToolError = true;
        }
        return outcome.Result;
    }
}
