using System.Globalization;
using System.Text;
using Slovo.Core.Json;
using Slovo.Core.Localization;
using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Core.Mcp;

/// <summary>
/// One meeting prepared for output: every string taken from the meeting sanitized and
/// capped, speaker labels in the interface language, display lines and highlight anchors
/// computed once (port of the macOS <c>MCPMeetingView</c>).
/// </summary>
internal sealed class McpMeetingView
{
    public const int MaximumTitleCharacters = 200;
    public const int MaximumNameCharacters = 80;
    public const int MaximumNoteCharacters = 500;
    public const int MaximumLineExcerptCharacters = 240;

    readonly Dictionary<string, string> labels = new(StringComparer.Ordinal);
    MeetingRecord? record;
    IReadOnlyList<MeetingSpeaker>? speakers;
    IReadOnlyList<TranscriptLine>? lines;
    IReadOnlyList<MeetingHighlight>? highlights;
    Dictionary<Guid, int>? anchors;
    Dictionary<int, List<MeetingHighlight>>? highlightsByLine;
    string? fingerprint;

    public McpMeetingView(ArchivedMeeting archived, AppLanguage language)
    {
        Archived = archived;
        Language = language;
        Title = SingleLine(archived.Record.Title, MaximumTitleCharacters);
    }

    public ArchivedMeeting Archived { get; }
    public AppLanguage Language { get; }

    /// <summary>The sanitized title; computed eagerly because every list shows it.</summary>
    public string Title { get; }

    /// <summary>The full lowercase id, the form results use.</summary>
    public string Id => Archived.Record.Id.ToString("D");

    /// <summary>The first 8 characters of the id, accepted wherever an id is.</summary>
    public string ShortId => Id[..8];

    /// <summary>The record with sanitized text, names and notes; built only when lines are needed.</summary>
    public MeetingRecord Record => record ??= Sanitized(Archived.Record, Title);

    public IReadOnlyList<MeetingSpeaker> Speakers => speakers ??= Archived.Record.EffectiveSpeakers.Select(SanitizedSpeaker).ToList();

    public IReadOnlyList<TranscriptLine> Lines => lines ??= TranscriptMerger.Lines(Record);

    /// <summary>Highlights in timeline order.</summary>
    public IReadOnlyList<MeetingHighlight> Highlights => highlights ??= Record.Highlights
        .OrderBy(highlight => highlight.Time)
        .ThenBy(highlight => AppleDate.ToReferenceSeconds(highlight.CreatedAt))
        .ThenBy(highlight => highlight.Id.ToUpperString(), StringComparer.Ordinal)
        .ToList();

    /// <summary>The display line each anchored highlight belongs to.</summary>
    public IReadOnlyDictionary<Guid, int> Anchors
    {
        get
        {
            if (anchors is null)
            {
                anchors = [];
                foreach (var highlight in Highlights)
                {
                    if (TranscriptMerger.HighlightLineIndex(highlight, Lines) is { } index && index >= 0 && index < Lines.Count)
                    {
                        anchors[highlight.Id] = index;
                    }
                }
            }
            return anchors;
        }
    }

    public IReadOnlyDictionary<int, List<MeetingHighlight>> HighlightsByLine
    {
        get
        {
            if (highlightsByLine is null)
            {
                highlightsByLine = [];
                foreach (var highlight in Highlights)
                {
                    if (Anchors.TryGetValue(highlight.Id, out var index))
                    {
                        if (!highlightsByLine.TryGetValue(index, out var list))
                        {
                            list = [];
                            highlightsByLine[index] = list;
                        }
                        list.Add(highlight);
                    }
                }
            }
            return highlightsByLine;
        }
    }

    /// <summary>
    /// Identifies the version of the file the meeting came from, for transcript cursors: the
    /// same text Swift hashes, its modification date in seconds since 2001 and its size.
    /// </summary>
    public string Fingerprint => fingerprint ??= McpHash.Hex(
        SwiftFormat.Double(AppleDate.ToReferenceSeconds(Archived.RecordModified)) + "|"
        + Archived.RecordSize.ToString(CultureInfo.InvariantCulture));

    public string Label(MeetingSpeaker speaker)
    {
        if (labels.TryGetValue(speaker.Id, out var cached))
        {
            return cached;
        }
        var label = TranscriptMerger.SpeakerLabel(speaker, Archived.Record, Language);
        labels[speaker.Id] = label;
        return label;
    }

    public string Label(string speakerId)
    {
        if (Speakers.FirstOrDefault(speaker => speaker.Id == speakerId) is { } shown)
        {
            return Label(shown);
        }
        if (Archived.Record.SpeakerWithId(speakerId) is { } stored)
        {
            return Label(SanitizedSpeaker(stored));
        }
        return speakerId;
    }

    /// <summary>The anchored line of a highlight, if any.</summary>
    public TranscriptLine? Line(MeetingHighlight highlight) =>
        Anchors.TryGetValue(highlight.Id, out var index) ? Lines[index] : null;

    public static MeetingRecord Sanitized(MeetingRecord source, string title) => source with
    {
        Title = title,
        ErrorMessage = null,
        // Claude's own notes are dropped here, because everything rendered from this record
        // goes inside the untrusted-transcript wrapper, whose notice says the text is
        // recognized speech. A retelling served under that notice would come back as a
        // quotation of words nobody said. McpResources says outside the wrapper that the
        // notes exist.
        ClaudeNotes = null,
        Segments = source.Segments.Select(segment => segment with { Text = McpSanitizer.Sanitize(segment.Text) }).ToList(),
        Speakers = source.Speakers.Select(SanitizedSpeaker).ToList(),
        Highlights = source.Highlights.Select(highlight => highlight with
        {
            Note = highlight.Note is { } note && SingleLine(note, MaximumNoteCharacters) is { Length: > 0 } clean ? clean : null,
        }).ToList(),
    };

    public static MeetingSpeaker SanitizedSpeaker(MeetingSpeaker speaker) => speaker with
    {
        DisplayName = speaker.DisplayName is { } name && SingleLine(name, MaximumNameCharacters) is { Length: > 0 } clean ? clean : null,
    };

    /// <summary>Sanitized, on one line with single spaces, and at most <paramref name="limit"/> characters.</summary>
    public static string SingleLine(string text, int limit) => Capped(JoinedWords(McpSanitizer.Sanitize(text)), limit);

    /// <summary>
    /// Swift's <c>split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")</c>: the
    /// characters (grapheme clusters) between runs of whitespace characters, joined by single spaces.
    /// </summary>
    public static string JoinedWords(string text)
    {
        var output = new StringBuilder(text.Length);
        var pendingSpace = false;
        var index = 0;
        while (index < text.Length)
        {
            var length = StringInfo.GetNextTextElementLength(text.AsSpan(index));
            var character = text.AsSpan(index, length);
            if (Rune.DecodeFromUtf16(character, out var first, out _) == System.Buffers.OperationStatus.Done && Rune.IsWhiteSpace(first))
            {
                pendingSpace = output.Length > 0;
            }
            else
            {
                if (pendingSpace)
                {
                    output.Append(' ');
                    pendingSpace = false;
                }
                output.Append(character);
            }
            index += length;
        }
        return output.ToString();
    }

    /// <summary>At most <paramref name="limit"/> characters, the last one an ellipsis when text was cut.</summary>
    public static string Capped(string text, int limit)
    {
        if (Text.TextElements.Count(text) <= limit)
        {
            return text;
        }
        return Text.TextElements.Prefix(text, Math.Max(0, limit - 1)) + "…";
    }
}

/// <summary>Dates as tools accept and report them, in the computer's time zone (port of <c>MCPDates</c>).</summary>
internal sealed class McpDates(DateTimeOffset now, TimeZoneInfo timeZone)
{
    public DateTimeOffset Now { get; } = now;
    public TimeZoneInfo TimeZone { get; } = timeZone;

    /// <summary>The IANA name of the time zone, such as <c>Europe/Moscow</c>, the name macOS reports.</summary>
    public string TimeZoneIdentifier => IanaName(TimeZone);

    public static string IanaName(TimeZoneInfo zone)
    {
        if (zone.HasIanaId)
        {
            return zone.Id;
        }
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;
    }

    /// <summary>ISO 8601 with the local offset, for example <c>2026-09-15T11:02:07+03:00</c>.</summary>
    public string Iso(DateTimeOffset date)
    {
        var local = TimeZoneInfo.ConvertTime(date, TimeZone);
        var offset = local.Offset;
        var magnitude = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture,
            $"{local.Year:D4}-{local.Month:D2}-{local.Day:D2}T{local.Hour:D2}:{local.Minute:D2}:{local.Second:D2}"
            + $"{(offset < TimeSpan.Zero ? '-' : '+')}{(int)magnitude.TotalHours:D2}:{magnitude.Minutes:D2}");
    }

    /// <summary>A long date and short time in the interface language, for prompt texts.</summary>
    public string Localized(DateTimeOffset date, AppLanguage language) =>
        MeetingDates.LongDateShortTime(date, language, TimeZone);

    /// <summary>
    /// <c>today</c>, <c>yesterday</c>, <c>Nd</c> (1–365 days back from now), <c>YYYY-MM-DD</c>
    /// (local midnight) or an ISO 8601 date-time, local unless it names an offset. Null for
    /// anything else, including dates the calendar would roll over, such as February 30.
    /// </summary>
    public DateTimeOffset? Parse(string text)
    {
        switch (text)
        {
            case "today":
                return StartOfDay(Now);
            case "yesterday":
                return AddingDays(StartOfDay(Now), -1);
        }
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 0 && bytes[^1] == (byte)'d')
        {
            if (bytes.Length < 2 || bytes.Length > 4 || bytes[0] == (byte)'0'
                || Number(bytes, 0, bytes.Length - 1) is not { } days || days is < 1 or > 365)
            {
                return null;
            }
            return AddingDays(Now, -days);
        }
        if (bytes.Length < 10 || Number(bytes, 0, 4) is not { } year || bytes[4] != (byte)'-'
            || Number(bytes, 5, 7) is not { } month || bytes[7] != (byte)'-' || Number(bytes, 8, 10) is not { } day
            || month is < 1 or > 12 || day is < 1 or > 31)
        {
            return null;
        }
        int hour = 0, minute = 0, second = 0;
        TimeSpan? offset = null;
        if (bytes.Length > 10)
        {
            if (bytes[10] != (byte)'T' || bytes.Length < 16 || Number(bytes, 11, 13) is not { } parsedHour
                || bytes[13] != (byte)':' || Number(bytes, 14, 16) is not { } parsedMinute
                || parsedHour > 23 || parsedMinute > 59)
            {
                return null;
            }
            hour = parsedHour;
            minute = parsedMinute;
            var cursor = 16;
            if (cursor < bytes.Length && bytes[cursor] == (byte)':')
            {
                if (Number(bytes, 17, 19) is not { } value || value > 59)
                {
                    return null;
                }
                second = value;
                cursor = 19;
            }
            if (cursor < bytes.Length)
            {
                if (bytes[cursor] == (byte)'Z')
                {
                    offset = TimeSpan.Zero;
                    cursor++;
                }
                else if (bytes[cursor] is (byte)'+' or (byte)'-')
                {
                    if (Number(bytes, cursor + 1, cursor + 3) is not { } hours || bytes.Length <= cursor + 3
                        || bytes[cursor + 3] != (byte)':' || Number(bytes, cursor + 4, cursor + 6) is not { } minutes
                        || hours > 18 || minutes > 59)
                    {
                        return null;
                    }
                    var sign = bytes[cursor] == (byte)'-' ? -1 : 1;
                    offset = TimeSpan.FromSeconds(sign * (hours * 3600 + minutes * 60));
                    cursor += 6;
                }
                else
                {
                    return null;
                }
            }
            if (cursor != bytes.Length)
            {
                return null;
            }
        }
        // The calendar would roll February 30 over into March; such a date is refused.
        if (year < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }
        try
        {
            var wall = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
            if (offset is { } fixedOffset)
            {
                // Offsets up to ±18 hours are accepted, more than a DateTimeOffset holds, so the
                // instant is built in UTC.
                return new DateTimeOffset(wall.Ticks - fixedOffset.Ticks, TimeSpan.Zero);
            }
            return Resolve(wall);
        }
        catch (ArgumentOutOfRangeException)
        {
            // The first and last days a DateTime holds, pushed past its range by an offset.
            return null;
        }
    }

    /// <summary>The first moment of the local day of <paramref name="date"/>.</summary>
    public DateTimeOffset StartOfDay(DateTimeOffset date)
    {
        var local = TimeZoneInfo.ConvertTime(date, TimeZone);
        return Resolve(local.DateTime.Date);
    }

    /// <summary>The same local wall time <paramref name="days"/> days away, as the calendar adds days.</summary>
    public DateTimeOffset AddingDays(DateTimeOffset date, int days)
    {
        var local = TimeZoneInfo.ConvertTime(date, TimeZone);
        // In an hour that happens twice, the calendar keeps the offset the date started with.
        return Resolve(local.DateTime.AddDays(days), preferredOffset: local.Offset);
    }

    /// <summary>
    /// A local wall time as an instant, the way Foundation's calendar resolves it: a time
    /// skipped by a clock change moves forward by the gap, and a time that happens twice is
    /// the first of the two unless <paramref name="preferredOffset"/> names the other.
    /// </summary>
    DateTimeOffset Resolve(DateTime wall, TimeSpan? preferredOffset = null)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (TimeZone.IsInvalidTime(wall))
        {
            // Read with the offset in force just before the change, which lands after it.
            var before = TimeZone.GetUtcOffset(wall.AddHours(-3));
            return new DateTimeOffset(wall.Ticks - before.Ticks, TimeSpan.Zero);
        }
        if (TimeZone.IsAmbiguousTime(wall))
        {
            var offsets = TimeZone.GetAmbiguousTimeOffsets(wall);
            var chosen = preferredOffset is { } preferred && offsets.Contains(preferred) ? preferred : offsets.Max();
            return new DateTimeOffset(wall.Ticks - chosen.Ticks, TimeSpan.Zero);
        }
        return new DateTimeOffset(wall, TimeZone.GetUtcOffset(wall)).ToUniversalTime();
    }

    static int? Number(byte[] bytes, int start, int end)
    {
        if (start < 0 || end > bytes.Length || start >= end)
        {
            return null;
        }
        var value = 0;
        for (var index = start; index < end; index++)
        {
            var current = bytes[index];
            if (current is < (byte)'0' or > (byte)'9')
            {
                return null;
            }
            value = value * 10 + (current - '0');
        }
        return value;
    }
}

/// <summary><c>since</c> inclusive, <c>until</c> exclusive.</summary>
internal struct McpDateRange
{
    public DateTimeOffset? Since;
    public DateTimeOffset? Until;

    public readonly bool Contains(DateTimeOffset date)
    {
        if (Since is { } since && date < since)
        {
            return false;
        }
        if (Until is { } until && date >= until)
        {
            return false;
        }
        return true;
    }
}

/// <summary>A stable hash for cursors (FNV-1a over UTF-8), so cursors survive a restart of the server between pages.</summary>
internal static class McpHash
{
    public static string Hex(string text)
    {
        var hash = 0xCBF29CE484222325UL;
        foreach (var value in Encoding.UTF8.GetBytes(text))
        {
            hash ^= value;
            hash = unchecked(hash * 0x00000100000001B3UL);
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}

/// <summary>Cursors travel as unpadded base64url JSON.</summary>
internal static class McpBase64Url
{
    public static string Encode(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').Replace("=", "", StringComparison.Ordinal);

    public static byte[]? Decode(string text)
    {
        if (text.Length == 0 || text.Length % 4 == 1 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return null;
        }
        var base64 = text.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The object inside a cursor when it has exactly these keys.</summary>
    public static IReadOnlyDictionary<string, JsonValue>? Object(string text, params string[] keys)
    {
        if (Decode(text) is not { } data)
        {
            return null;
        }
        JsonValue value;
        try
        {
            value = JsonValue.Parse(data);
        }
        catch (JsonParseException)
        {
            return null;
        }
        if (value.ObjectValue is not { } members || members.Count != keys.Length || !keys.All(members.ContainsKey))
        {
            return null;
        }
        return members;
    }
}

/// <summary>
/// Where the next <c>list_meetings</c> page starts: after the meeting with this date and id,
/// in newest-first order, so meetings added or deleted meanwhile do not shift pages.
/// </summary>
internal sealed record McpListCursor
{
    /// <summary><c>createdAt</c> in seconds since 2001, the precision <c>meeting.json</c> stores.</summary>
    public double Time { get; init; }
    /// <summary>The canonical (upper-case) id string, which breaks ties in list order.</summary>
    public string Id { get; init; } = "";
    public string Filter { get; init; } = "";

    public static McpListCursor After(ArchivedMeeting archived, string filter) => new()
    {
        Time = AppleDate.ToReferenceSeconds(archived.Record.CreatedAt),
        Id = archived.Record.Id.ToUpperString(),
        Filter = filter,
    };

    public static McpListCursor? Decode(string text)
    {
        if (McpBase64Url.Object(text, "k", "t", "i", "q") is not { } fields || fields["k"] != JsonValue.Str("m")
            || fields["t"].DoubleValue is not { } time || !double.IsFinite(time)
            || fields["i"].StringValue is not { } rawId || !Guid.TryParseExact(rawId, "D", out var id)
            || fields["q"].StringValue is not { } filter)
        {
            return null;
        }
        return new McpListCursor { Time = time, Id = id.ToUpperString(), Filter = filter };
    }

    public string Encoded => McpBase64Url.Encode(
        JsonValue.Object(("k", "m"), ("t", JsonValue.Number(Time)), ("i", Id), ("q", Filter)).Serialized());

    /// <summary>True when the meeting comes after the cursor position.</summary>
    public bool Precedes(ArchivedMeeting archived)
    {
        var meetingTime = AppleDate.ToReferenceSeconds(archived.Record.CreatedAt);
        return meetingTime < Time || (meetingTime == Time && string.CompareOrdinal(archived.Record.Id.ToUpperString(), Id) > 0);
    }
}

/// <summary>Localized messages for tool errors and prompts, from <c>Integration.strings</c>.</summary>
internal static class McpMessages
{
    static StringTable Table(AppLanguage language) => CoreStrings.TableFor(CoreStrings.ResolveCode(language));

    public static string Disabled(AppLanguage language) => Table(language).Get("integration.mcp.disabled");
    public static string NotFound(string reference, AppLanguage language) => Table(language).Format("integration.mcp.notFound", reference);
    public static string Ambiguous(AppLanguage language) => Table(language).Get("integration.mcp.ambiguous");
    public static string NoTranscript(AppLanguage language) => Table(language).Get("integration.mcp.noTranscript");
    public static string StillProcessing(AppLanguage language) => Table(language).Get("integration.mcp.stillProcessing");
    public static string BadCursor(AppLanguage language) => Table(language).Get("integration.mcp.badCursor");
    public static string BadArgument(string detail, AppLanguage language) => Table(language).Format("integration.mcp.badArgument", detail);
    public static string NoMeetings(AppLanguage language) => Table(language).Get("integration.mcp.noMeetings");
    public static string TooManyRequests(int seconds, AppLanguage language) => Table(language).Format("integration.mcp.tooManyRequests", seconds);
    public static string Damaged(AppLanguage language) => Table(language).Get("integration.mcp.damaged");
    public static string WriteDisabled(AppLanguage language) => Table(language).Get("integration.mcp.writeDisabled");
    public static string WriteFailed(AppLanguage language) => Table(language).Get("integration.mcp.writeFailed");
    public static string SlovoUser(AppLanguage language) => Table(language).Get("integration.prompt.slovoUser");
    public static string TasksPrompt(AppLanguage language) => Table(language).Get("integration.prompt.tasks");
    public static string ShortSummaryPrompt(AppLanguage language) => Table(language).Get("integration.prompt.summary.short");
    public static string FullSummaryPrompt(AppLanguage language) => Table(language).Get("integration.prompt.summary.full");
    public static string PagingPrompt(AppLanguage language) => Table(language).Get("integration.prompt.paging");
}
