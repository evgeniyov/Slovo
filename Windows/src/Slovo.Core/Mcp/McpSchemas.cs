using System.Globalization;
using System.Text.RegularExpressions;
using Slovo.Core.Model;

namespace Slovo.Core.Mcp;

/// <summary>
/// What Claude sees in <c>tools/list</c>, <c>prompts/list</c> and
/// <c>resources/templates/list</c> (port of the macOS <c>MCPSchemas</c>).
///
/// Tool arguments are validated against these same schemas, so what is declared and what
/// is enforced cannot drift apart.
/// </summary>
internal static class McpSchemas
{
    internal const string DatePattern =
        "^(today|yesterday|[1-9][0-9]{0,2}d|[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}(:[0-9]{2})?(Z|[+-][0-9]{2}:[0-9]{2})?)?)$";
    internal const string MeetingIdPattern = "^(latest|[0-9A-Fa-f][0-9A-Fa-f-]{7,35})$";
    internal const string SpeakerIdPattern = "^(me|others|s[0-9]{1,3})$";
    internal const string HighlightIdPattern = "^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$";
    /// <summary>Claude Code keeps tool results up to this many characters inline.</summary>
    internal const int MaximumResultSizeCharacters = 50_000;

    /// <summary>MeetingEdits.maximumNoteCharacters on macOS.</summary>
    internal const int MaximumHighlightNoteCharacters = 500;

    internal static readonly JsonValue MeetingIdSchema = JsonValue.Object(("type", "string"), ("pattern", MeetingIdPattern));

    internal static readonly JsonValue ListMeetingsSchema = JsonValue.Object(
        ("type", "object"),
        ("properties", JsonValue.Object(
            ("query", JsonValue.Object(
                ("type", "string"), ("minLength", 1), ("maxLength", 200),
                ("description", "Words that must all appear in the title; case-insensitive, ё matches е."))),
            ("since", JsonValue.Object(
                ("type", "string"), ("pattern", DatePattern),
                ("description", "Start (inclusive): today, yesterday, 7d, 2026-09-01 or an ISO 8601 date-time in local time."))),
            ("until", JsonValue.Object(
                ("type", "string"), ("pattern", DatePattern), ("description", "End (exclusive), same formats as since."))),
            ("limit", JsonValue.Object(("type", "integer"), ("minimum", 1), ("maximum", 100), ("default", 20))),
            ("cursor", JsonValue.Object(("type", "string"), ("maxLength", 512), ("description", "next_cursor from a previous call."))))),
        ("additionalProperties", false));

    internal static readonly JsonValue GetMeetingSchema = JsonValue.Object(
        ("type", "object"),
        ("properties", JsonValue.Object(
            ("meeting_id", JsonValue.Object(("type", "string"), ("pattern", MeetingIdPattern), ("default", "latest"))),
            ("include_highlights", JsonValue.Object(("type", "boolean"), ("default", true))))),
        ("additionalProperties", false));

    internal static readonly JsonValue GetTranscriptSchema = JsonValue.Object(
        ("type", "object"),
        ("properties", JsonValue.Object(
            ("meeting_id", JsonValue.Object(("type", "string"), ("pattern", MeetingIdPattern))),
            ("speaker_ids", JsonValue.Object(
                ("type", "array"), ("items", JsonValue.Object(("type", "string"), ("pattern", SpeakerIdPattern))),
                ("minItems", 1), ("maxItems", 20), ("uniqueItems", true), ("description", "Speaker ids from get_meeting."))),
            ("from", JsonValue.Object(
                ("type", JsonValue.Array("string", "number")), ("description", "Start of the range: seconds, mm:ss or h:mm:ss."))),
            ("to", JsonValue.Object(
                ("type", JsonValue.Array("string", "number")),
                ("description", "End of the range (exclusive): seconds, mm:ss or h:mm:ss."))),
            ("max_chars", JsonValue.Object(("type", "integer"), ("minimum", 2000), ("maximum", 48000), ("default", 16000))),
            ("cursor", JsonValue.Object(("type", "string"), ("maxLength", 512))))),
        ("required", JsonValue.Array("meeting_id")),
        ("additionalProperties", false));

    internal static readonly JsonValue SearchMeetingsSchema = JsonValue.Object(
        ("type", "object"),
        ("properties", JsonValue.Object(
            ("query", JsonValue.Object(
                ("type", "string"), ("minLength", 2), ("maxLength", 200),
                ("description", "Case-insensitive; ё matches е; all words must appear in one line."))),
            ("meeting_id", JsonValue.Object(("type", "string"), ("pattern", MeetingIdPattern))),
            ("since", JsonValue.Object(("type", "string"), ("pattern", DatePattern))),
            ("until", JsonValue.Object(("type", "string"), ("pattern", DatePattern))),
            ("limit", JsonValue.Object(("type", "integer"), ("minimum", 1), ("maximum", 50), ("default", 20))))),
        ("required", JsonValue.Array("query")),
        ("additionalProperties", false));

    internal static readonly JsonValue ListHighlightsSchema = JsonValue.Object(
        ("type", "object"),
        ("properties", JsonValue.Object(
            ("meeting_id", JsonValue.Object(("type", "string"), ("pattern", MeetingIdPattern))),
            ("since", JsonValue.Object(("type", "string"), ("pattern", DatePattern))),
            ("until", JsonValue.Object(("type", "string"), ("pattern", DatePattern))),
            ("limit", JsonValue.Object(("type", "integer"), ("minimum", 1), ("maximum", 200), ("default", 50))))),
        ("additionalProperties", false));

    internal static readonly JsonValue MarkHighlightSchema = JsonValue.Object(
        ("type", "object"),
        ("properties", JsonValue.Object(
            ("meeting_id", JsonValue.Object(("type", "string"), ("pattern", MeetingIdPattern), ("default", "latest"))),
            ("time", JsonValue.Object(
                ("type", JsonValue.Array("string", "number")),
                ("description", "Position to mark: seconds, mm:ss or h:mm:ss, from get_transcript or search_meetings."))),
            ("note", JsonValue.Object(
                ("type", "string"), ("minLength", 1), ("maxLength", MaximumHighlightNoteCharacters),
                ("description", "Why this moment matters, in the user's language. One line, no line breaks."))))),
        ("required", JsonValue.Array("time")),
        ("additionalProperties", false));

    internal static readonly JsonValue UnmarkHighlightSchema = JsonValue.Object(
        ("type", "object"),
        ("properties", JsonValue.Object(
            ("meeting_id", JsonValue.Object(("type", "string"), ("pattern", MeetingIdPattern), ("default", "latest"))),
            ("highlight_id", JsonValue.Object(
                ("type", "string"), ("pattern", HighlightIdPattern),
                ("description", "A highlight id from get_meeting, list_highlights or mark_highlight."))))),
        ("required", JsonValue.Array("highlight_id")),
        ("additionalProperties", false));

    internal static readonly JsonValue WriteMeetingNotesSchema = BuildWriteMeetingNotesSchema();

    static JsonValue BuildWriteMeetingNotesSchema()
    {
        var item = JsonValue.Object(("type", "string"), ("minLength", 1), ("maxLength", MeetingNotes.MaximumItemCharacters));
        return JsonValue.Object(
            ("type", "object"),
            ("properties", JsonValue.Object(
                ("meeting_id", JsonValue.Object(("type", "string"), ("pattern", MeetingIdPattern), ("default", "latest"))),
                ("summary", JsonValue.Object(
                    ("type", "string"), ("minLength", 1), ("maxLength", MeetingNotes.MaximumSummaryCharacters),
                    ("description", "What the meeting was about, in the user's language. One line, no line breaks."))),
                ("decisions", JsonValue.Object(
                    ("type", "array"), ("items", item), ("maxItems", MeetingNotes.MaximumItems),
                    ("description", "What was decided, one decision per item."))),
                ("tasks", JsonValue.Object(
                    ("type", "array"), ("items", item), ("maxItems", MeetingNotes.MaximumItems),
                    ("description", "What someone is to do, one task per item; name the owner when the meeting did."))))),
            ("required", JsonValue.Array("meeting_id")),
            ("additionalProperties", false));
    }

    internal static JsonValue InputSchema(string toolName) => toolName switch
    {
        "list_meetings" => ListMeetingsSchema,
        "get_meeting" => GetMeetingSchema,
        "get_transcript" => GetTranscriptSchema,
        "search_meetings" => SearchMeetingsSchema,
        "list_highlights" => ListHighlightsSchema,
        "mark_highlight" => MarkHighlightSchema,
        "unmark_highlight" => UnmarkHighlightSchema,
        "write_meeting_notes" => WriteMeetingNotesSchema,
        _ => JsonValue.Object(("type", "object"), ("additionalProperties", false)),
    };

    /// <summary><c>tools/list</c> entries in their fixed order; the same for every connection.</summary>
    internal static readonly IReadOnlyList<JsonValue> ToolEntries = McpText.Tools.Select(tool =>
    {
        // A tool that writes says so, and says that it replaces what was there before.
        var annotations = JsonValue.Object(
            ("title", tool.Title), ("readOnlyHint", tool.ReadOnly), ("destructiveHint", !tool.ReadOnly),
            ("idempotentHint", true), ("openWorldHint", false));
        var meta = JsonValue.Object((McpText.MaximumResultSizeMetaKey, MaximumResultSizeCharacters));
        return JsonValue.Object(
            ("name", tool.Name), ("title", tool.Title), ("description", tool.Description),
            ("inputSchema", InputSchema(tool.Name)), ("annotations", annotations), ("_meta", meta));
    }).ToList();

    internal static readonly IReadOnlyList<JsonValue> Prompts = BuildPrompts();

    static List<JsonValue> BuildPrompts()
    {
        var meetingId = JsonValue.Object(
            ("name", "meeting_id"), ("description", McpText.MeetingIdArgumentDescription), ("required", false));
        var detail = JsonValue.Object(("name", "detail"), ("description", McpText.DetailArgumentDescription), ("required", false));
        var tasks = JsonValue.Object(
            ("name", McpText.MeetingTasksName), ("title", McpText.MeetingTasksTitle),
            ("description", McpText.MeetingTasksDescription), ("arguments", JsonValue.Array(meetingId)));
        var summary = JsonValue.Object(
            ("name", McpText.MeetingSummaryName), ("title", McpText.MeetingSummaryTitle),
            ("description", McpText.MeetingSummaryDescription), ("arguments", JsonValue.Array(meetingId, detail)));
        return [tasks, summary];
    }

    internal static readonly IReadOnlyList<JsonValue> ResourceTemplates =
    [
        JsonValue.Object(
            ("uriTemplate", McpText.TranscriptUriTemplate), ("name", McpText.TranscriptTemplateName),
            ("title", McpText.TranscriptTemplateTitle), ("description", McpText.TranscriptTemplateDescription),
            ("mimeType", McpText.MarkdownMimeType)),
    ];
}

/// <summary>
/// Checks a value against the subset of JSON Schema the tool schemas use: <c>type</c>,
/// <c>properties</c>, <c>required</c>, <c>additionalProperties: false</c>, string lengths and
/// patterns, numeric bounds and array items, counts and uniqueness.
/// </summary>
internal sealed class McpSchemaValidator
{
    readonly Dictionary<string, Regex> expressions = new(StringComparer.Ordinal);

    /// <summary>Actionable hints for pattern failures, by argument name.</summary>
    static readonly Dictionary<string, string> FormatHints = new(StringComparer.Ordinal)
    {
        ["meeting_id"] = McpText.MeetingIdFormatHint,
        ["since"] = McpText.DateFormatHint,
        ["until"] = McpText.DateFormatHint,
        ["speaker_ids"] = McpText.SpeakerIdFormatHint,
        ["highlight_id"] = McpText.HighlightIdFormatHint,
    };

    /// <summary>The first problem as "argument: what is wrong", or null when the value is valid.</summary>
    public string? Problem(JsonValue value, JsonValue schema, string path = "")
    {
        if (schema["type"] is { } expected)
        {
            var names = expected.StringValue is { } single
                ? [single]
                : expected.ArrayValue?.Select(item => item.StringValue).OfType<string>().ToList() ?? [];
            if (names.Count > 0 && !names.Any(name => HasType(value, name)))
            {
                return $"{Label(path)}: must be {string.Join(" or ", names.Select(TypeDescription))}";
            }
        }
        return value.Kind switch
        {
            JsonKind.Object => ObjectProblem(value.ObjectValue!, schema, path),
            JsonKind.Array => ArrayProblem(value.ArrayValue!, schema, path),
            JsonKind.String => StringProblem(value.StringValue!, schema, path),
            JsonKind.Integer or JsonKind.Number => NumberProblem(value, schema, path),
            _ => null,
        };
    }

    /// <summary>An argument name taken from a request, safe to repeat in a message.</summary>
    public static string DisplayName(string name) => McpMeetingView.SingleLine(name, 40);

    string? ObjectProblem(IReadOnlyDictionary<string, JsonValue> members, JsonValue schema, string path)
    {
        var properties = schema["properties"]?.ObjectValue ?? new Dictionary<string, JsonValue>();
        foreach (var name in schema["required"]?.ArrayValue?.Select(item => item.StringValue).OfType<string>() ?? [])
        {
            if (!members.ContainsKey(name))
            {
                return $"{Join(path, name)}: is required";
            }
        }
        var sortedNames = members.Keys.Order(SwiftStringComparer.Instance).ToList();
        if (schema["additionalProperties"] == JsonValue.False
            && sortedNames.FirstOrDefault(name => !properties.ContainsKey(name)) is { } unknown)
        {
            return $"{Join(path, DisplayName(unknown))}: unknown argument";
        }
        foreach (var name in sortedNames)
        {
            if (!properties.TryGetValue(name, out var memberSchema))
            {
                continue;
            }
            if (Problem(members[name], memberSchema, Join(path, name)) is { } problem)
            {
                return problem;
            }
        }
        return null;
    }

    string? ArrayProblem(IReadOnlyList<JsonValue> elements, JsonValue schema, string path)
    {
        var label = Label(path);
        if (schema["minItems"]?.IntValue is { } minimum && elements.Count < minimum)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{label}: must have at least {minimum} item{(minimum == 1 ? "" : "s")}");
        }
        if (schema["maxItems"]?.IntValue is { } maximum && elements.Count > maximum)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{label}: must have at most {maximum} items");
        }
        if (schema["uniqueItems"] == JsonValue.True)
        {
            for (var index = 0; index < elements.Count; index++)
            {
                for (var earlier = 0; earlier < index; earlier++)
                {
                    if (elements[earlier].Equals(elements[index]))
                    {
                        return $"{label}: must not repeat items";
                    }
                }
            }
        }
        if (schema["items"] is { } itemSchema)
        {
            foreach (var element in elements)
            {
                // Items are named after the array, so the format hint of the array applies.
                if (Problem(element, itemSchema, path) is { } problem)
                {
                    return problem;
                }
            }
        }
        return null;
    }

    string? StringProblem(string text, JsonValue schema, string path)
    {
        var label = Label(path);
        var length = McpUnicode.ScalarCount(text);
        if (schema["minLength"]?.IntValue is { } minimum && length < minimum)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{label}: must be at least {minimum} character{(minimum == 1 ? "" : "s")} long");
        }
        if (schema["maxLength"]?.IntValue is { } maximum && length > maximum)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{label}: must be at most {maximum} characters long");
        }
        if (schema["pattern"]?.StringValue is { } pattern && !Matches(text, pattern))
        {
            var hint = FormatHints.TryGetValue(path, out var found) ? "; " + found : "";
            return $"{label}: has an invalid format{hint}";
        }
        return null;
    }

    static string? NumberProblem(JsonValue value, JsonValue schema, string path)
    {
        if (value.DoubleValue is not { } number)
        {
            return null;
        }
        var label = Label(path);
        if (schema["minimum"]?.DoubleValue is { } minimum && number < minimum)
        {
            return $"{label}: must be at least {Format(minimum)}";
        }
        if (schema["maximum"]?.DoubleValue is { } maximum && number > maximum)
        {
            return $"{label}: must be at most {Format(maximum)}";
        }
        return null;
    }

    bool Matches(string text, string pattern)
    {
        if (!expressions.TryGetValue(pattern, out var expression))
        {
            try
            {
                expression = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                return false;
            }
            expressions[pattern] = expression;
        }
        // Like ICU, .NET's `$` also matches before a final line break; the whole value must match.
        var match = expression.Match(text);
        return match.Success && match.Index == 0 && match.Length == text.Length;
    }

    static bool HasType(JsonValue value, string type) => (type, value.Kind) switch
    {
        ("object", JsonKind.Object) or ("array", JsonKind.Array) or ("string", JsonKind.String)
            or ("boolean", JsonKind.Bool) or ("null", JsonKind.Null) => true,
        ("integer", JsonKind.Integer) or ("number", JsonKind.Integer) or ("number", JsonKind.Number) => true,
        ("integer", JsonKind.Number) => JsonValue.ExactInteger(value.DoubleValue!.Value) is not null,
        _ => false,
    };

    static string TypeDescription(string type) => type switch
    {
        "integer" => "an integer",
        "array" => "an array",
        "object" => "an object",
        "boolean" => "true or false",
        _ => $"a {type}",
    };

    static string Label(string path) => path.Length == 0 ? "arguments" : path;

    static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    static string Format(double number) =>
        JsonValue.ExactInteger(number) is { } whole ? whole.ToString(CultureInfo.InvariantCulture) : SwiftFormat.Double(number);
}
