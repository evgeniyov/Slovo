using System.Globalization;
using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Core.Mcp;

/// <summary>A tool answer with <c>isError: true</c> and a localized, actionable message.</summary>
internal sealed class McpToolFailure(string message) : Exception(message);

/// <summary>What a tool returns: structured JSON (also sent as text), plain text, or a failure.</summary>
internal sealed class McpToolOutcome
{
    McpToolOutcome(JsonValue? structured, string? text, string? failure)
    {
        Structured = structured;
        Text = text;
        FailureMessage = failure;
    }

    public JsonValue? Structured { get; }
    public string? Text { get; }
    public string? FailureMessage { get; }
    public bool IsFailure => FailureMessage is not null;

    public static McpToolOutcome Of(JsonValue structured) => new(structured, null, null);
    public static McpToolOutcome OfText(string text) => new(null, text, null);
    public static McpToolOutcome Failure(string message) => new(null, null, message);

    public JsonValue Result
    {
        get
        {
            if (Structured is { } value)
            {
                // The same JSON in both fields: clients differ in which one they read, and one
                // reported client drops the text when structured content is present.
                return JsonValue.Object(("content", TextContent(value.SerializedString)), ("structuredContent", value));
            }
            if (Text is { } text)
            {
                return JsonValue.Object(("content", TextContent(text)));
            }
            return JsonValue.Object(("content", TextContent(FailureMessage!)), ("isError", true));
        }
    }

    static JsonValue TextContent(string text) => JsonValue.Array(JsonValue.Object(("type", "text"), ("text", text)));
}

public sealed partial class McpHandler
{
    internal const int ListedSpeakersPerMeeting = 30;
    internal const int DetailedSpeakersPerMeeting = 200;
    internal const int AmbiguousMatchesShown = 10;

    /// <summary>Access, then the rate limit, then argument validation, all before any meeting is read.</summary>
    McpToolOutcome RunTool(string name, JsonValue? rawArguments, McpRequestContext context)
    {
        if (!context.Access.Enabled)
        {
            return McpToolOutcome.Failure(McpMessages.Disabled(context.Language));
        }
        if (ConsumeRateLimit() is { } wait)
        {
            return McpToolOutcome.Failure(McpMessages.TooManyRequests(wait, context.Language));
        }
        IReadOnlyDictionary<string, JsonValue> arguments;
        if (rawArguments is null || rawArguments.IsNull)
        {
            arguments = new Dictionary<string, JsonValue>();
        }
        else if (rawArguments.ObjectValue is { } objectArguments)
        {
            arguments = objectArguments;
        }
        else
        {
            return McpToolOutcome.Failure(McpMessages.BadArgument("arguments: must be an object", context.Language));
        }
        if (validator.Problem(JsonValue.Object(arguments), McpSchemas.InputSchema(name)) is { } problem)
        {
            return McpToolOutcome.Failure(McpMessages.BadArgument(problem, context.Language));
        }
        try
        {
            return name switch
            {
                "list_meetings" => ListMeetings(arguments, context),
                "get_meeting" => GetMeeting(arguments, context),
                "get_transcript" => GetTranscript(arguments, context),
                "search_meetings" => SearchMeetings(arguments, context),
                "list_highlights" => ListHighlights(arguments, context),
                "mark_highlight" => MarkHighlight(arguments, context),
                "unmark_highlight" => UnmarkHighlight(arguments, context),
                "write_meeting_notes" => WriteMeetingNotes(arguments, context),
                _ => throw McpRpcError.InvalidParams("Unknown tool"),
            };
        }
        catch (McpToolFailure failure)
        {
            return McpToolOutcome.Failure(failure.Message);
        }
    }

    static string? StringArgument(IReadOnlyDictionary<string, JsonValue> arguments, string name) =>
        arguments.TryGetValue(name, out var value) ? value.StringValue : null;

    static JsonValue? Argument(IReadOnlyDictionary<string, JsonValue> arguments, string name) =>
        arguments.TryGetValue(name, out var value) ? value : null;

    // MARK: - list_meetings

    McpToolOutcome ListMeetings(IReadOnlyDictionary<string, JsonValue> arguments, McpRequestContext context)
    {
        var language = context.Language;
        var range = DateRange(arguments, context);
        var query = StringArgument(arguments, "query");
        var words = query is null ? null : SearchWords(query, "query", context);
        var limit = (int)(Argument(arguments, "limit")?.IntValue ?? 20);
        // A cursor only continues the listing it was made for.
        var filter = McpHash.Hex(string.Join("\u001F",
            "list_meetings", query ?? "", StringArgument(arguments, "since") ?? "", StringArgument(arguments, "until") ?? ""));

        var candidates = context.VisibleMeetings.Where(meeting => range.Contains(meeting.Record.CreatedAt)).ToList();
        if (words is not null)
        {
            candidates = candidates
                .Where(archived => McpSearch.Matches(McpSearch.Normalized(McpSanitizer.Sanitize(archived.Record.Title)), words))
                .ToList();
        }
        if (StringArgument(arguments, "cursor") is { } cursorText)
        {
            if (McpListCursor.Decode(cursorText) is not { } cursor || cursor.Filter != filter)
            {
                throw new McpToolFailure(McpMessages.BadCursor(language));
            }
            candidates = candidates.Where(cursor.Precedes).ToList();
        }

        var page = candidates.Take(limit).ToList();
        var items = page.Select(archived => MeetingSummary(new McpMeetingView(archived, language), context)).ToList();
        var result = Fitted(items.Count, count =>
        {
            var fields = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                ["now"] = context.Dates.Iso(context.Now),
                ["time_zone"] = context.Dates.TimeZoneIdentifier,
                ["meetings"] = JsonValue.Array(items.Take(count)),
            };
            if (count > 0 && count < candidates.Count)
            {
                fields["next_cursor"] = McpListCursor.After(page[count - 1], filter).Encoded;
            }
            if (candidates.Count == 0)
            {
                fields["message"] = McpMessages.NoMeetings(language);
            }
            return JsonValue.Object(fields);
        });
        return McpToolOutcome.Of(result);
    }

    JsonValue MeetingSummary(McpMeetingView view, McpRequestContext context)
    {
        var source = view.Archived.Record;
        var speakers = view.Speakers.Take(ListedSpeakersPerMeeting).Select(speaker =>
            JsonValue.Object(("id", speaker.Id), ("name", view.Label(speaker)), ("is_me", speaker.IsMe)));
        var fields = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
        {
            ["id"] = view.Id,
            ["short_id"] = view.ShortId,
            ["title"] = view.Title,
            ["created_at"] = context.Dates.Iso(source.CreatedAt),
            ["duration_seconds"] = JsonValue.Seconds(source.Duration),
            ["duration_text"] = TranscriptMerger.Timestamp(source.Duration),
            ["status"] = EnumText.Raw(source.Status),
            ["has_transcript"] = source.Status == MeetingStatus.Transcribed,
            ["speakers"] = JsonValue.Array(speakers),
            ["highlight_count"] = source.Highlights.Count,
            ["engine"] = JsonValue.Optional(source.EngineId is { } engine ? EnumText.Raw(engine) : null),
            ["speaker_separation"] = source.Diarization is { } diarization ? EnumText.Raw(diarization.State) : "none",
        };
        if (view.Speakers.Count > ListedSpeakersPerMeeting)
        {
            fields["speakers_truncated"] = true;
        }
        return JsonValue.Object(fields);
    }

    // MARK: - get_meeting

    McpToolOutcome GetMeeting(IReadOnlyDictionary<string, JsonValue> arguments, McpRequestContext context)
    {
        var reference = StringArgument(arguments, "meeting_id") ?? "latest";
        var includeHighlights = Argument(arguments, "include_highlights")?.BoolValue ?? true;
        var (archived, latestNote) = Resolved(reference, context);
        var view = new McpMeetingView(archived, context.Language);
        var source = archived.Record;

        var meeting = JsonValue.Object(
            ("id", view.Id),
            ("short_id", view.ShortId),
            ("title", view.Title),
            ("created_at", context.Dates.Iso(source.CreatedAt)),
            ("duration_seconds", JsonValue.Seconds(source.Duration)),
            ("duration_text", TranscriptMerger.Timestamp(source.Duration)),
            ("status", EnumText.Raw(source.Status)),
            ("engine", JsonValue.Optional(source.EngineId is { } engine ? EnumText.Raw(engine) : null)),
            ("speaker_separation", source.Diarization is { } diarization ? EnumText.Raw(diarization.State) : "none"),
            ("has_system_audio", source.HasSystemAudio));
        var speakers = view.Speakers.Take(DetailedSpeakersPerMeeting).Select(speaker => JsonValue.Object(
            ("id", speaker.Id), ("name", view.Label(speaker)), ("is_me", speaker.IsMe),
            ("source", EnumText.Raw(speaker.Track)), ("talk_seconds", JsonValue.Seconds(source.TalkTime(speaker.Id))))).ToList();
        var highlightItems = includeHighlights
            ? view.Highlights.Select(highlight => JsonValue.Object(HighlightFields(highlight, view))).ToList()
            : [];
        var transcript = source.Status == MeetingStatus.Transcribed ? TranscriptSummary(view, context) : JsonValue.Null;
        var statusNote = McpText.StatusNote(source.Status);

        var result = Fitted(highlightItems.Count, count =>
        {
            var fields = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                ["meeting"] = meeting,
                ["speakers"] = JsonValue.Array(speakers),
                ["transcript"] = transcript,
            };
            var notes = new[] { latestNote, statusNote }.OfType<string>().ToList();
            if (includeHighlights)
            {
                fields["highlights"] = JsonValue.Array(highlightItems.Take(count));
                if (count < highlightItems.Count)
                {
                    fields["highlights_truncated"] = true;
                    notes.Add(McpText.HighlightsTruncated);
                }
            }
            if (view.Speakers.Count > DetailedSpeakersPerMeeting)
            {
                fields["speakers_truncated"] = true;
            }
            if (notes.Count > 0)
            {
                fields["note"] = string.Join(" ", notes);
            }
            return JsonValue.Object(fields);
        });
        return McpToolOutcome.Of(result);
    }

    static Dictionary<string, JsonValue> HighlightFields(MeetingHighlight highlight, McpMeetingView view)
    {
        var line = view.Line(highlight);
        return new Dictionary<string, JsonValue>(StringComparer.Ordinal)
        {
            ["id"] = highlight.Id.ToString("D"),
            ["time_seconds"] = JsonValue.Seconds(highlight.Time),
            ["time_text"] = TranscriptMerger.Timestamp(highlight.Time),
            ["note"] = JsonValue.Optional(highlight.Note),
            ["speaker_id"] = JsonValue.Optional(line?.SpeakerId),
            ["speaker"] = JsonValue.Optional(line is null ? null : view.Label(line.SpeakerId)),
            ["line"] = JsonValue.Optional(line is null ? null : McpMeetingView.Capped(line.Text, McpMeetingView.MaximumLineExcerptCharacters)),
        };
    }

    static JsonValue TranscriptSummary(McpMeetingView view, McpRequestContext context)
    {
        var selection = new McpTranscriptSelection { MaxChars = McpTranscriptSelection.DefaultMaxChars };
        var rendered = McpTranscriptPager.Render(view, selection);
        var characters = rendered.Sum(line => line.Text.Length) + Math.Max(0, rendered.Count - 1);
        var pages = McpTranscriptPager.PageCount(view, selection, rendered, context.Dates);
        return JsonValue.Object(("lines", rendered.Count), ("characters", characters), ("pages_at_default_size", pages));
    }

    // MARK: - get_transcript

    McpToolOutcome GetTranscript(IReadOnlyDictionary<string, JsonValue> arguments, McpRequestContext context)
    {
        var language = context.Language;
        var reference = StringArgument(arguments, "meeting_id") ?? "latest";
        var from = Argument(arguments, "from") is { } fromValue ? TimelinePosition(fromValue, "from", context) : (double?)null;
        var to = Argument(arguments, "to") is { } toValue ? TimelinePosition(toValue, "to", context) : (double?)null;
        if (from is { } start && to is { } end && start >= end)
        {
            throw BadArgument("from: must be earlier than to", context);
        }
        var speakerIds = Argument(arguments, "speaker_ids")?.ArrayValue?
            .Select(item => item.StringValue).OfType<string>().Order(SwiftStringComparer.Instance).ToList();
        var maxChars = (int)(Argument(arguments, "max_chars")?.IntValue ?? McpTranscriptSelection.DefaultMaxChars);

        var (archived, _) = Resolved(reference, context);
        RequireTranscript(archived.Record.Status, language);
        var view = new McpMeetingView(archived, language);
        if (speakerIds is not null)
        {
            var known = view.Speakers.Select(speaker => speaker.Id).ToList();
            if (speakerIds.FirstOrDefault(id => !known.Contains(id)) is { } unknown)
            {
                throw BadArgument($"speaker_ids: this meeting has no speaker {unknown}; its speakers are " + string.Join(", ", known),
                    context);
            }
        }

        var selection = new McpTranscriptSelection { SpeakerIds = speakerIds, From = from, To = to, MaxChars = maxChars };
        var rendered = McpTranscriptPager.Render(view, selection);
        var startLine = 0;
        var offset = 0;
        var changed = false;
        if (StringArgument(arguments, "cursor") is { } cursorText)
        {
            if (McpTranscriptCursor.Decode(cursorText) is not { } cursor || cursor.Meeting != view.Id
                || cursor.Filter != selection.FilterHash)
            {
                throw new McpToolFailure(McpMessages.BadCursor(language));
            }
            if (cursor.Fingerprint == view.Fingerprint)
            {
                var valid = cursor.Index < rendered.Count
                    ? cursor.Offset < rendered[(int)cursor.Index].Text.Length
                    : cursor.Index == rendered.Count && cursor.Offset == 0;
                if (!valid)
                {
                    throw new McpToolFailure(McpMessages.BadCursor(language));
                }
                startLine = (int)cursor.Index;
                offset = (int)cursor.Offset;
            }
            else
            {
                // The meeting was saved again, for example after a rename: continue from the
                // same position and say that lines may have shifted.
                changed = true;
                startLine = (int)Math.Min(cursor.Index, rendered.Count);
                offset = startLine < rendered.Count ? (int)Math.Min(cursor.Offset, int.MaxValue) : 0;
                if (startLine < rendered.Count && offset >= rendered[startLine].Text.Length)
                {
                    startLine++;
                    offset = 0;
                }
            }
        }
        var page = McpTranscriptPager.Page(view, selection, rendered, startLine, offset, changed, context.Dates);
        return McpToolOutcome.OfText(page.Text);
    }

    double TimelinePosition(JsonValue value, string argument, McpRequestContext context) =>
        McpTranscriptPager.TimelinePosition(value) ?? throw BadArgument($"{argument}: {McpText.TimelineFormatHint}", context);

    static void RequireTranscript(MeetingStatus status, AppLanguage language)
    {
        switch (status)
        {
            case MeetingStatus.Recording or MeetingStatus.Transcribing:
                throw new McpToolFailure(McpMessages.StillProcessing(language));
            case MeetingStatus.Recorded or MeetingStatus.Failed:
                throw new McpToolFailure(McpMessages.NoTranscript(language));
        }
    }

    // MARK: - search_meetings

    McpToolOutcome SearchMeetings(IReadOnlyDictionary<string, JsonValue> arguments, McpRequestContext context)
    {
        var language = context.Language;
        var words = SearchWords(StringArgument(arguments, "query") ?? "", "query", context);
        var range = DateRange(arguments, context);
        var limit = (int)(Argument(arguments, "limit")?.IntValue ?? 20);
        var scope = MeetingScope(arguments, range, context);

        var hits = new List<JsonValue>();
        string? nextStep = null;
        var truncated = false;
        foreach (var archived in scope)
        {
            var view = new McpMeetingView(archived, language);
            var common = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                ["meeting_id"] = view.Id,
                ["short_id"] = view.ShortId,
                ["title"] = view.Title,
                ["created_at"] = context.Dates.Iso(archived.Record.CreatedAt),
            };
            // Ordered within the meeting: a title hit first, then by line.
            var found = new List<(int Order, double? Time, Dictionary<string, JsonValue> Fields)>();
            if (McpSearch.Matches(McpSearch.Normalized(McpSanitizer.Sanitize(archived.Record.Title)), words))
            {
                var fields = new Dictionary<string, JsonValue>(common, StringComparer.Ordinal)
                {
                    ["time_seconds"] = JsonValue.Null,
                    ["time_text"] = JsonValue.Null,
                    ["speaker_id"] = JsonValue.Null,
                    ["speaker"] = JsonValue.Null,
                    ["snippet"] = view.Title,
                };
                found.Add((-1, null, fields));
            }
            var hitLines = new HashSet<int>();
            for (var index = 0; index < view.Lines.Count; index++)
            {
                var line = view.Lines[index];
                if (!McpSearch.Matches(McpSearch.Normalized(line.Text), words))
                {
                    continue;
                }
                hitLines.Add(index);
                var snippet = McpSearch.Snippet(line.Text, words);
                found.Add((index, line.Start, LineHit(common, line, view, snippet)));
            }
            foreach (var speaker in view.Speakers)
            {
                if (!McpSearch.Matches(McpSearch.Normalized(view.Label(speaker)), words))
                {
                    continue;
                }
                var index = -1;
                for (var candidate = 0; candidate < view.Lines.Count; candidate++)
                {
                    if (view.Lines[candidate].SpeakerId == speaker.Id)
                    {
                        index = candidate;
                        break;
                    }
                }
                if (index < 0 || hitLines.Contains(index))
                {
                    continue;
                }
                hitLines.Add(index);
                var line = view.Lines[index];
                var snippet = McpMeetingView.Capped(line.Text, McpMeetingView.MaximumLineExcerptCharacters);
                found.Add((index, line.Start, LineHit(common, line, view, snippet)));
            }
            // Orders are unique within a meeting, so the order of equal keys never matters.
            found.Sort((left, right) => left.Order.CompareTo(right.Order));
            var full = false;
            foreach (var hit in found)
            {
                if (hits.Count >= limit)
                {
                    truncated = true;
                    full = true;
                    break;
                }
                nextStep ??= Step(view, hit.Time, context);
                hits.Add(JsonValue.Object(hit.Fields));
            }
            if (full)
            {
                break;
            }
        }

        var result = Fitted(hits.Count, count =>
        {
            var fields = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                ["hits"] = JsonValue.Array(hits.Take(count)),
                ["truncated"] = truncated || count < hits.Count,
            };
            if (count > 0 && nextStep is not null)
            {
                fields["next_step"] = nextStep;
            }
            if (hits.Count == 0)
            {
                fields["message"] = McpMessages.NoMeetings(language);
            }
            return JsonValue.Object(fields);
        });
        return McpToolOutcome.Of(result);
    }

    static Dictionary<string, JsonValue> LineHit(Dictionary<string, JsonValue> common, TranscriptLine line, McpMeetingView view,
                                                 string snippet) =>
        new(common, StringComparer.Ordinal)
        {
            ["time_seconds"] = JsonValue.Seconds(line.Start),
            ["time_text"] = TranscriptMerger.Timestamp(line.Start),
            ["speaker_id"] = line.SpeakerId,
            ["speaker"] = view.Label(line.SpeakerId),
            ["snippet"] = snippet,
        };

    /// <summary>What to call next for the first hit: its transcript around the hit, with the short id when it is unambiguous.</summary>
    static string Step(McpMeetingView view, double? time, McpRequestContext context)
    {
        var sharing = context.VisibleMeetings.Count(meeting => meeting.Record.Id.ToString("D").StartsWith(view.ShortId, StringComparison.Ordinal));
        var reference = sharing == 1 ? view.ShortId : view.Id;
        if (time is not { } seconds)
        {
            return $"get_meeting meeting_id={reference}";
        }
        return $"get_transcript meeting_id={reference} from={TranscriptMerger.Timestamp(Math.Max(0, seconds - 30))} "
            + $"to={TranscriptMerger.Timestamp(seconds + 60)}";
    }

    // MARK: - list_highlights

    McpToolOutcome ListHighlights(IReadOnlyDictionary<string, JsonValue> arguments, McpRequestContext context)
    {
        var range = DateRange(arguments, context);
        var limit = (int)(Argument(arguments, "limit")?.IntValue ?? 50);
        var scope = MeetingScope(arguments, range, context);

        var items = new List<JsonValue>();
        var truncated = false;
        foreach (var archived in scope.Where(meeting => meeting.Record.Highlights.Count > 0))
        {
            var view = new McpMeetingView(archived, context.Language);
            foreach (var highlight in view.Highlights)
            {
                if (items.Count >= limit)
                {
                    truncated = true;
                    break;
                }
                var fields = HighlightFields(highlight, view);
                fields["meeting_id"] = view.Id;
                fields["short_id"] = view.ShortId;
                fields["meeting_title"] = view.Title;
                fields["source"] = EnumText.Raw(highlight.Source);
                items.Add(JsonValue.Object(fields));
            }
            if (truncated)
            {
                break;
            }
        }
        var result = Fitted(items.Count, count => JsonValue.Object(
            ("highlights", JsonValue.Array(items.Take(count))), ("truncated", truncated || count < items.Count)));
        return McpToolOutcome.Of(result);
    }

    // MARK: - mark_highlight

    /// <summary>
    /// Marks a moment in a transcribed meeting as important, the way a star on a transcript
    /// line does. Idempotent: marking the same time and note again finds the highlight that
    /// call already made and changes nothing.
    /// </summary>
    McpToolOutcome MarkHighlight(IReadOnlyDictionary<string, JsonValue> arguments, McpRequestContext context)
    {
        var language = context.Language;
        if (!context.Access.CanWrite)
        {
            return McpToolOutcome.Failure(McpMessages.WriteDisabled(language));
        }
        if (highlights is not { } writer)
        {
            return McpToolOutcome.Failure(McpMessages.WriteFailed(language));
        }

        var reference = StringArgument(arguments, "meeting_id") ?? "latest";
        // The schema requires this argument, so it is always present and of the right type.
        var time = TimelinePosition(Argument(arguments, "time") ?? JsonValue.Null, "time", context);
        var note = Argument(arguments, "note") is { } noteValue
            ? CheckedNotesText(noteValue, "note", McpSchemas.MaximumHighlightNoteCharacters, context)
            : null;

        var (archived, _) = Resolved(reference, context);
        RequireTranscript(archived.Record.Status, language);

        HighlightMarkResult result;
        try
        {
            result = writer.Mark(time, note, archived.Record.Id, context.Now);
        }
        catch (Exception)
        {
            throw new McpToolFailure(McpMessages.WriteFailed(language));
        }

        var view = new McpMeetingView(new ArchivedMeeting(result.Record, context.Now, 0), language);
        return McpToolOutcome.Of(JsonValue.Object(
            ("meeting_id", view.Id), ("short_id", view.ShortId), ("title", view.Title),
            ("highlight", JsonValue.Object(HighlightFields(result.Highlight, view))), ("created", result.Created),
            ("note", McpText.HighlightMarked(result.Created))));
    }

    // MARK: - unmark_highlight

    /// <summary>Removes one important moment by its id. Idempotent: an id that is already gone changes nothing.</summary>
    McpToolOutcome UnmarkHighlight(IReadOnlyDictionary<string, JsonValue> arguments, McpRequestContext context)
    {
        var language = context.Language;
        if (!context.Access.CanWrite)
        {
            return McpToolOutcome.Failure(McpMessages.WriteDisabled(language));
        }
        if (highlights is not { } writer)
        {
            return McpToolOutcome.Failure(McpMessages.WriteFailed(language));
        }

        var reference = StringArgument(arguments, "meeting_id") ?? "latest";
        if (StringArgument(arguments, "highlight_id") is not { } idText || !Guid.TryParseExact(idText, "D", out var highlightId))
        {
            throw BadArgument($"highlight_id: {McpText.HighlightIdFormatHint}", context);
        }

        var (archived, _) = Resolved(reference, context);
        RequireTranscript(archived.Record.Status, language);

        HighlightUnmarkResult result;
        try
        {
            result = writer.Unmark(highlightId, archived.Record.Id);
        }
        catch (Exception)
        {
            throw new McpToolFailure(McpMessages.WriteFailed(language));
        }

        var view = new McpMeetingView(new ArchivedMeeting(result.Record, context.Now, 0), language);
        return McpToolOutcome.Of(JsonValue.Object(
            ("meeting_id", view.Id), ("short_id", view.ShortId), ("title", view.Title),
            ("highlight_id", highlightId.ToString("D")), ("removed", result.Removed),
            ("note", McpText.HighlightUnmarked(result.Removed))));
    }

    // MARK: - write_meeting_notes

    /// <summary>
    /// The one tool that changes a meeting's notes. It needs its own switch, and writes
    /// through <see cref="IMeetingNotesWriter"/>, which reads the record again immediately
    /// before replacing it, so nothing the app saved meanwhile is lost. It always replaces:
    /// running it twice leaves one set of notes, not two. Nothing it writes reaches the
    /// segments, so the transcript stays what was said.
    /// </summary>
    McpToolOutcome WriteMeetingNotes(IReadOnlyDictionary<string, JsonValue> arguments, McpRequestContext context)
    {
        var language = context.Language;
        if (!context.Access.CanWrite)
        {
            return McpToolOutcome.Failure(McpMessages.WriteDisabled(language));
        }
        if (notes is not { } writer)
        {
            return McpToolOutcome.Failure(McpMessages.WriteFailed(language));
        }

        var reference = StringArgument(arguments, "meeting_id") ?? "latest";
        var summary = Argument(arguments, "summary") is { } summaryValue
            ? CheckedNotesText(summaryValue, "summary", MeetingNotes.MaximumSummaryCharacters, context)
            : null;
        var decisions = CheckedNotesItems(Argument(arguments, "decisions"), "decisions", context);
        var tasks = CheckedNotesItems(Argument(arguments, "tasks"), "tasks", context);
        if (summary is null && decisions.Count == 0 && tasks.Count == 0)
        {
            throw BadArgument("summary: write a summary, decisions or tasks", context);
        }

        var (archived, _) = Resolved(reference, context);
        // Notes about a meeting with no transcript would be about nothing.
        RequireTranscript(archived.Record.Status, language);

        var replaced = archived.Record.ClaudeNotes is not null;
        var written = new MeetingNotes(summary ?? "", decisions, tasks, MeetingNotes.DefaultAuthor, context.Now);
        // What the file holds afterwards is reported, so the date is the one the user will see:
        // notes that repeat what the meeting already holds keep their first date.
        MeetingNotes stored;
        try
        {
            stored = writer.Write(written, archived.Record.Id) ?? written;
        }
        catch (Exception)
        {
            throw new McpToolFailure(McpMessages.WriteFailed(language));
        }

        var view = new McpMeetingView(archived, language);
        return McpToolOutcome.Of(JsonValue.Object(
            ("meeting_id", view.Id), ("short_id", view.ShortId), ("title", view.Title),
            ("author", stored.Author), ("written_at", context.Dates.Iso(stored.WrittenAt)),
            ("summary_written", stored.Summary.Length > 0), ("decisions", stored.Decisions.Count),
            ("tasks", stored.Tasks.Count), ("replaced", replaced), ("note", McpText.NotesWritten(replaced))));
    }

    /// <summary>
    /// Text Claude writes into a meeting is held to the rules Slovo's own output follows: one
    /// line, no control characters, no transcript tag, and nothing the recovery parser of
    /// <c>transcript.md</c> would read back as a spoken line or a marked moment.
    ///
    /// Bad text is refused, never repaired: quietly rewriting what Claude wrote would put words
    /// in its mouth, and a refusal says what to fix.
    /// </summary>
    string CheckedNotesText(JsonValue value, string argument, int limit, McpRequestContext context)
    {
        if (value.StringValue is not { } text)
        {
            throw BadArgument($"{argument}: must be a string", context);
        }
        if (text.EnumerateRunes().Any(McpSanitizer.IsControl))
        {
            throw BadArgument($"{argument}: must be one line, without control characters", context);
        }
        if (McpSanitizer.Sanitize(text) != text)
        {
            var invisible = text.EnumerateRunes().Any(McpSanitizer.IsInvisible);
            throw BadArgument($"{argument}: " + (invisible ? "must not contain invisible characters" : "must not contain a transcript tag"),
                context);
        }
        var trimmed = McpUnicode.TrimHorizontalWhitespace(text);
        if (trimmed.Length == 0)
        {
            throw BadArgument($"{argument}: must not be empty", context);
        }
        if (Text.TextElements.Count(trimmed) > limit)
        {
            throw BadArgument(string.Create(CultureInfo.InvariantCulture, $"{argument}: must be at most {limit} characters long"),
                context);
        }
        if (McpUnicode.FirstCharacter(trimmed) == "[" || trimmed.Contains(TranscriptMerger.HighlightMark, StringComparison.Ordinal))
        {
            throw BadArgument($"{argument}: must not start with \"[\" or contain "
                + $"{TranscriptMerger.HighlightMark}, which mark lines of a transcript", context);
        }
        return trimmed;
    }

    List<string> CheckedNotesItems(JsonValue? value, string argument, McpRequestContext context)
    {
        if (value?.ArrayValue is not { } elements)
        {
            return [];
        }
        if (elements.Count > MeetingNotes.MaximumItems)
        {
            throw BadArgument(string.Create(CultureInfo.InvariantCulture,
                $"{argument}: must have at most {MeetingNotes.MaximumItems} items"), context);
        }
        return elements.Select((element, index) => CheckedNotesText(element, string.Create(CultureInfo.InvariantCulture,
            $"{argument}[{index}]"), MeetingNotes.MaximumItemCharacters, context)).ToList();
    }

    // MARK: - Shared argument handling

    /// <summary>A meeting found for a reference (with the note about a newer one in progress), or the message saying why not.</summary>
    internal sealed record MeetingResolution(ArchivedMeeting? Meeting, string? Note, string? FailureMessage);

    internal MeetingResolution ResolveMeeting(string reference, McpRequestContext context)
    {
        var language = context.Language;
        var visible = context.VisibleMeetings;
        if (reference == "latest")
        {
            var index = -1;
            for (var candidate = 0; candidate < visible.Count; candidate++)
            {
                if (visible[candidate].Record.Status == MeetingStatus.Transcribed)
                {
                    index = candidate;
                    break;
                }
            }
            if (index < 0)
            {
                if (visible.Any(meeting => IsInProgress(meeting.Record.Status)))
                {
                    return new(null, null, McpMessages.StillProcessing(language));
                }
                return new(null, null, visible.Count == 0 ? McpMessages.NoMeetings(language) : McpMessages.NotFound(reference, language));
            }
            var newer = visible.Take(index).FirstOrDefault(meeting => IsInProgress(meeting.Record.Status));
            var note = newer is null ? null : McpText.NewerMeetingInProgress(
                new McpMeetingView(newer, language).Title, context.Dates.Iso(newer.Record.CreatedAt));
            return new(visible[index], note, null);
        }

        var lowered = reference.ToLowerInvariant();
        if (Text.TextElements.Count(lowered) == 36 && Guid.TryParseExact(lowered, "D", out var id))
        {
            if (visible.FirstOrDefault(meeting => meeting.Record.Id == id) is { } match)
            {
                return new(match, null, null);
            }
            if (archive.UnreadableMeetingDate(id) is { } created && context.IsVisible(created))
            {
                return new(null, null, McpMessages.Damaged(language));
            }
            return new(null, null, McpMessages.NotFound(reference, language));
        }
        var matches = visible.Where(archived =>
        {
            var full = archived.Record.Id.ToString("D");
            return lowered.Contains('-')
                ? full.StartsWith(lowered, StringComparison.Ordinal)
                : full.Replace("-", "", StringComparison.Ordinal).StartsWith(lowered, StringComparison.Ordinal);
        }).ToList();
        if (matches.Count == 1)
        {
            return new(matches[0], null, null);
        }
        if (matches.Count == 0)
        {
            return new(null, null, McpMessages.NotFound(reference, language));
        }
        var listed = matches.Take(AmbiguousMatchesShown).Select(archived =>
        {
            var view = new McpMeetingView(archived, language);
            return $"- {view.Id} «{view.Title}» {context.Dates.Iso(archived.Record.CreatedAt)}";
        });
        return new(null, null, string.Join("\n", new[] { McpMessages.Ambiguous(language) }.Concat(listed)));
    }

    (ArchivedMeeting Meeting, string? Note) Resolved(string reference, McpRequestContext context)
    {
        var resolution = ResolveMeeting(reference, context);
        if (resolution.Meeting is { } meeting)
        {
            return (meeting, resolution.Note);
        }
        throw new McpToolFailure(resolution.FailureMessage ?? McpMessages.NotFound(reference, context.Language));
    }

    static bool IsInProgress(MeetingStatus status) => status is MeetingStatus.Recording or MeetingStatus.Transcribing;

    /// <summary>One meeting when <c>meeting_id</c> is given, otherwise every visible meeting; both limited to the date range.</summary>
    List<ArchivedMeeting> MeetingScope(IReadOnlyDictionary<string, JsonValue> arguments, McpDateRange range, McpRequestContext context)
    {
        if (StringArgument(arguments, "meeting_id") is { } reference)
        {
            var (archived, _) = Resolved(reference, context);
            return range.Contains(archived.Record.CreatedAt) ? [archived] : [];
        }
        return context.VisibleMeetings.Where(meeting => range.Contains(meeting.Record.CreatedAt)).ToList();
    }

    McpDateRange DateRange(IReadOnlyDictionary<string, JsonValue> arguments, McpRequestContext context)
    {
        var range = new McpDateRange();
        foreach (var name in new[] { "since", "until" })
        {
            if (StringArgument(arguments, name) is not { } text)
            {
                continue;
            }
            if (context.Dates.Parse(text) is not { } date)
            {
                throw BadArgument($"{name}: {McpText.DateFormatHint}", context);
            }
            if (name == "since")
            {
                range.Since = date;
            }
            else
            {
                range.Until = date;
            }
        }
        if (range.Since is { } since && range.Until is { } until && since >= until)
        {
            throw BadArgument("since: must be earlier than until", context);
        }
        return range;
    }

    List<string> SearchWords(string query, string argument, McpRequestContext context)
    {
        var words = McpSearch.Words(query);
        if (words.Count == 0)
        {
            throw BadArgument($"{argument}: must contain at least one word", context);
        }
        return words;
    }

    static McpToolFailure BadArgument(string detail, McpRequestContext context) =>
        new(McpMessages.BadArgument(detail, context.Language));

    /// <summary>
    /// The result built with as many of <paramref name="count"/> items as keep its text within
    /// 48,000 characters, measured in UTF-16 units as Claude's clients count string length.
    /// </summary>
    static JsonValue Fitted(int count, Func<int, JsonValue> build)
    {
        var whole = build(count);
        if (whole.SerializedString.Length <= MaximumResultCharacters)
        {
            return whole;
        }
        var best = build(0);
        var low = 1;
        var high = count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var candidate = build(middle);
            if (candidate.SerializedString.Length <= MaximumResultCharacters)
            {
                best = candidate;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return best;
    }
}
