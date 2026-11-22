using System.Text;
using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Core.Mcp;

/// <summary>
/// A resource URI, parsed strictly: scheme <c>slovo</c>, host <c>meetings</c>, then either
/// nothing or one <c>latest</c> or UUID segment followed by <c>transcript.md</c>. No file path
/// is ever built from URI text.
/// </summary>
internal abstract record McpResourceUri
{
    public sealed record Index : McpResourceUri;
    public sealed record Latest : McpResourceUri;
    public sealed record Meeting(Guid Id) : McpResourceUri;

    public static McpResourceUri? Parse(string text)
    {
        if (text == McpText.MeetingsIndexUri)
        {
            return new Index();
        }
        const string prefix = McpText.MeetingsIndexUri + "/";
        const string suffix = "/transcript.md";
        // Swift compares characters; a URI is plain enough that ordinal comparison agrees.
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith(suffix, StringComparison.Ordinal)
            || Text.TextElements.Count(text) <= prefix.Length + suffix.Length)
        {
            return null;
        }
        var segment = text[prefix.Length..^suffix.Length];
        if (segment == "latest")
        {
            return new Latest();
        }
        if (Text.TextElements.Count(segment) == 36 && Guid.TryParseExact(segment, "D", out var id))
        {
            return new Meeting(id);
        }
        return null;
    }
}

internal enum McpPromptKind
{
    Tasks,
    Summary,
}

public sealed partial class McpHandler
{
    internal const int ResourceCharacterLimit = 100_000;
    internal const int EmbeddedDocumentCharacterLimit = 40_000;
    internal const int IndexedMeetingCount = 50;
    internal const int ListedTranscriptCount = 20;
    internal const int IndexedSpeakersPerMeeting = 10;

    // MARK: - Resources

    /// <summary>Empty while access is off.</summary>
    JsonValue ListResources(McpRequestContext context)
    {
        if (!context.Access.Enabled)
        {
            return JsonValue.Object(("resources", JsonValue.Array()));
        }
        var resources = new List<JsonValue>
        {
            JsonValue.Object(
                ("uri", McpText.MeetingsIndexUri), ("name", McpText.MeetingsIndexName), ("title", McpText.MeetingsIndexTitle),
                ("description", McpText.MeetingsIndexDescription), ("mimeType", McpText.MarkdownMimeType)),
            JsonValue.Object(
                ("uri", McpText.LatestTranscriptUri), ("name", McpText.LatestTranscriptName),
                ("title", McpText.LatestTranscriptTitle), ("description", McpText.LatestTranscriptDescription),
                ("mimeType", McpText.MarkdownMimeType)),
        };
        var transcribed = context.VisibleMeetings.Where(meeting => meeting.Record.Status == MeetingStatus.Transcribed);
        foreach (var archived in transcribed.Take(ListedTranscriptCount))
        {
            var view = new McpMeetingView(archived, context.Language);
            var annotations = JsonValue.Object(("lastModified", context.Dates.Iso(archived.RecordModified)));
            resources.Add(JsonValue.Object(
                ("uri", McpText.TranscriptUri(view.Id)), ("name", $"transcript-{view.ShortId}"),
                ("title", view.Title), ("mimeType", McpText.MarkdownMimeType), ("annotations", annotations)));
        }
        return JsonValue.Object(("resources", JsonValue.Array(resources)));
    }

    JsonValue ReadResource(IReadOnlyDictionary<string, JsonValue> parameters, McpRequestContext context)
    {
        if (!context.Access.Enabled)
        {
            throw McpRpcError.InvalidParams(McpMessages.Disabled(context.Language));
        }
        if (ConsumeRateLimit() is { } wait)
        {
            throw new McpRpcError(-32603, McpMessages.TooManyRequests(wait, context.Language));
        }
        if (!parameters.TryGetValue("uri", out var uriValue) || uriValue.StringValue is not { } uri)
        {
            throw McpRpcError.InvalidParams("resources/read requires a uri");
        }
        var target = McpResourceUri.Parse(uri) ?? throw McpRpcError.ResourceNotFound(uri, context.Era);
        string text;
        switch (target)
        {
            case McpResourceUri.Index:
                text = MeetingsIndex(context);
                break;
            case McpResourceUri.Latest:
                var latest = context.VisibleMeetings.FirstOrDefault(meeting => meeting.Record.Status == MeetingStatus.Transcribed)
                    ?? throw McpRpcError.ResourceNotFound(uri, context.Era);
                text = ResourceDocument(new McpMeetingView(latest, context.Language), context);
                break;
            case McpResourceUri.Meeting meeting:
                var archived = context.VisibleMeetings.FirstOrDefault(candidate => candidate.Record.Id == meeting.Id);
                if (archived is null || archived.Record.Status != MeetingStatus.Transcribed)
                {
                    throw McpRpcError.ResourceNotFound(uri, context.Era);
                }
                text = ResourceDocument(new McpMeetingView(archived, context.Language), context);
                break;
            default:
                throw McpRpcError.ResourceNotFound(uri, context.Era);
        }
        var content = JsonValue.Object(("uri", uri), ("mimeType", McpText.MarkdownMimeType), ("text", text));
        return JsonValue.Object(("contents", JsonValue.Array(content)));
    }

    /// <summary>
    /// The header, notice and untrusted wrapper of <c>get_transcript</c> around the meeting's
    /// <c>transcript.md</c> document, cut on a line boundary at 100,000 characters.
    ///
    /// The document holds what was said and nothing else: <see cref="McpMeetingView"/> keeps
    /// Claude's own notes out of the record it renders, and their existence is reported here,
    /// above the notice and outside the wrapper, so a second run of a prompt cannot read an
    /// earlier summary as if it were the recording.
    /// </summary>
    string ResourceDocument(McpMeetingView view, McpRequestContext context)
    {
        var head = new StringBuilder();
        head.Append(string.Join("\n", McpTranscriptPager.HeaderLines(view, context.Dates))).Append('\n');
        if (view.Archived.Record.ClaudeNotes is { } notes)
        {
            head.Append(McpText.ClaudeNotesLine(notes.Author, context.Dates.Iso(notes.WrittenAt))).Append('\n');
        }
        head.Append(McpText.TranscriptNotice).Append("\n\n").Append(McpText.TranscriptOpenTag).Append('\n');
        var headText = head.ToString();
        var markdown = TranscriptMerger.Markdown(view.Record, context.Language, timeZone: context.Dates.TimeZone);
        if (!markdown.EndsWith('\n'))
        {
            markdown += "\n";
        }
        var closing = McpText.TranscriptCloseTag + "\n";
        var whole = headText + markdown + closing;
        if (whole.Length <= ResourceCharacterLimit)
        {
            return whole;
        }

        var tail = "\n" + McpText.ResourceTruncated(view.Id) + "\n";
        var budget = ResourceCharacterLimit - headText.Length - closing.Length - tail.Length;
        var kept = new StringBuilder();
        foreach (var line in markdown.Split('\n'))
        {
            var length = line.Length + 1;
            if (length > budget)
            {
                break;
            }
            kept.Append(line).Append('\n');
            budget -= length;
        }
        return headText + kept + closing + tail;
    }

    /// <summary>One line per meeting, newest first.</summary>
    string MeetingsIndex(McpRequestContext context)
    {
        var lines = new List<string> { McpText.MeetingsIndexHeading, "", McpText.MeetingsIndexNotice, "" };
        var meetings = context.VisibleMeetings.Take(IndexedMeetingCount).ToList();
        if (meetings.Count == 0)
        {
            lines.Add(McpText.MeetingsIndexEmpty);
        }
        foreach (var archived in meetings)
        {
            var view = new McpMeetingView(archived, context.Language);
            var speakers = view.Speakers.Take(IndexedSpeakersPerMeeting).Select(view.Label).ToList();
            if (view.Speakers.Count > IndexedSpeakersPerMeeting)
            {
                speakers.Add("…");
            }
            var source = archived.Record;
            lines.Add(McpText.IndexLine(
                context.Dates.Iso(source.CreatedAt), TranscriptMerger.Timestamp(source.Duration), view.Title,
                EnumText.Raw(source.Status), speakers.Count == 0 ? McpText.NoSpeakers : string.Join(", ", speakers),
                source.Highlights.Count, view.ShortId));
        }
        return string.Join("\n", lines) + "\n";
    }

    // MARK: - Prompts

    JsonValue GetPrompt(IReadOnlyDictionary<string, JsonValue> parameters, McpRequestContext context)
    {
        McpPromptKind kind;
        switch (parameters.TryGetValue("name", out var nameValue) ? nameValue.StringValue : null)
        {
            case McpText.MeetingTasksName:
                kind = McpPromptKind.Tasks;
                break;
            case McpText.MeetingSummaryName:
                kind = McpPromptKind.Summary;
                break;
            default:
                throw McpRpcError.InvalidParams("Unknown prompt");
        }
        var language = context.Language;
        if (!context.Access.Enabled)
        {
            throw McpRpcError.InvalidParams(McpMessages.Disabled(language));
        }
        if (ConsumeRateLimit() is { } wait)
        {
            throw new McpRpcError(-32603, McpMessages.TooManyRequests(wait, language));
        }

        var arguments = PromptArguments(parameters.TryGetValue("arguments", out var raw) ? raw : null, kind, language);
        // Clients may send an empty string for an optional argument the user left out.
        var reference = arguments.TryGetValue("meeting_id", out var given) && given.Length > 0 ? given : "latest";
        if (validator.Problem(JsonValue.Str(reference), McpSchemas.MeetingIdSchema, "meeting_id") is { } problem)
        {
            throw McpRpcError.InvalidParams(McpMessages.BadArgument(problem, language));
        }
        var detail = arguments.TryGetValue("detail", out var givenDetail) && givenDetail.Length > 0 ? givenDetail : "short";
        if (detail is not ("short" or "full"))
        {
            throw McpRpcError.InvalidParams(McpMessages.BadArgument("detail: use short or full", language));
        }

        var resolution = ResolveMeeting(reference, context);
        if (resolution.Meeting is not { } archived)
        {
            throw McpRpcError.InvalidParams(resolution.FailureMessage ?? McpMessages.NotFound(reference, language));
        }
        switch (archived.Record.Status)
        {
            case MeetingStatus.Recording or MeetingStatus.Transcribing:
                throw McpRpcError.InvalidParams(McpMessages.StillProcessing(language));
            case MeetingStatus.Recorded or MeetingStatus.Failed:
                throw McpRpcError.InvalidParams(McpMessages.NoTranscript(language));
        }

        var view = new McpMeetingView(archived, language);
        var template = kind == McpPromptKind.Tasks
            ? McpMessages.TasksPrompt(language)
            : detail == "full" ? McpMessages.FullSummaryPrompt(language) : McpMessages.ShortSummaryPrompt(language);
        var speakers = view.Speakers.Take(ListedSpeakersPerMeeting).Select(speaker =>
            speaker.IsMe ? $"{view.Label(speaker)} ({McpMessages.SlovoUser(language)})" : view.Label(speaker));
        var instruction = McpText.Fill(template, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = view.Title,
            ["date"] = context.Dates.Localized(archived.Record.CreatedAt, language),
            ["duration"] = TranscriptMerger.Timestamp(archived.Record.Duration),
            ["speakers"] = string.Join(", ", speakers),
        });

        var document = ResourceDocument(view, context);
        JsonValue attachment;
        if (document.Length <= EmbeddedDocumentCharacterLimit)
        {
            var resource = JsonValue.Object(
                ("uri", McpText.TranscriptUri(view.Id)), ("mimeType", McpText.MarkdownMimeType), ("text", document));
            attachment = JsonValue.Object(("type", "resource"), ("resource", resource));
        }
        else
        {
            var paging = McpText.Fill(McpMessages.PagingPrompt(language),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["meeting_id"] = view.Id });
            attachment = JsonValue.Object(("type", "text"), ("text", paging));
        }
        var first = JsonValue.Object(("role", "user"), ("content", JsonValue.Object(("type", "text"), ("text", instruction))));
        var second = JsonValue.Object(("role", "user"), ("content", attachment));
        var description = kind == McpPromptKind.Tasks ? McpText.MeetingTasksDescription : McpText.MeetingSummaryDescription;
        return JsonValue.Object(("description", description), ("messages", JsonValue.Array(first, second)));
    }

    static Dictionary<string, string> PromptArguments(JsonValue? raw, McpPromptKind kind, AppLanguage language)
    {
        if (raw is null || raw.IsNull)
        {
            return [];
        }
        if (raw.ObjectValue is not { } values)
        {
            throw McpRpcError.InvalidParams(McpMessages.BadArgument("arguments: must be an object", language));
        }
        var allowed = kind == McpPromptKind.Tasks ? new[] { "meeting_id" } : ["meeting_id", "detail"];
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in values.Keys.Order(SwiftStringComparer.Instance))
        {
            if (!allowed.Contains(key))
            {
                var detail = $"{McpSchemaValidator.DisplayName(key)}: unknown argument";
                throw McpRpcError.InvalidParams(McpMessages.BadArgument(detail, language));
            }
            if (values[key].StringValue is not { } text)
            {
                throw McpRpcError.InvalidParams(McpMessages.BadArgument($"{key}: must be a string", language));
            }
            arguments[key] = text;
        }
        return arguments;
    }
}
