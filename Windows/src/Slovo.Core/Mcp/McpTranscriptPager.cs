using System.Globalization;
using System.Text;
using Slovo.Core.Meetings;

namespace Slovo.Core.Mcp;

/// <summary>Which lines of a transcript <c>get_transcript</c> shows, and the page size.</summary>
internal sealed record McpTranscriptSelection
{
    public const int DefaultMaxChars = 16_000;

    /// <summary>Sorted speaker ids; null shows every speaker.</summary>
    public IReadOnlyList<string>? SpeakerIds { get; init; }
    /// <summary>A line is shown when it starts at or after <c>From</c> and before <c>To</c>.</summary>
    public double? From { get; init; }
    public double? To { get; init; }
    public int MaxChars { get; init; } = DefaultMaxChars;

    /// <summary>Ties a cursor to the arguments it was made for; hashes the same text Swift does.</summary>
    public string FilterHash
    {
        get
        {
            var speakers = SpeakerIds is null ? "*" : string.Join(",", SpeakerIds.Order(SwiftStringComparer.Instance));
            var start = From is { } from ? SwiftFormat.Double(from) : "-";
            var end = To is { } to ? SwiftFormat.Double(to) : "-";
            return McpHash.Hex(string.Create(CultureInfo.InvariantCulture, $"get_transcript|{speakers}|{start}|{end}|{MaxChars}"));
        }
    }
}

/// <summary>
/// Where the next transcript page starts. Not signed: a changed cursor can only move within
/// text the same arguments already allow reading.
/// </summary>
internal sealed record McpTranscriptCursor(string Meeting, long Index, long Offset, string Fingerprint, string Filter)
{
    /// <summary>Null unless the text is a cursor this server could have made.</summary>
    public static McpTranscriptCursor? Decode(string text)
    {
        if (McpBase64Url.Object(text, "m", "i", "c", "f", "q") is not { } fields
            || fields["m"].StringValue is not { } rawMeeting || !Guid.TryParseExact(rawMeeting, "D", out var meeting)
            || fields["i"].Kind != JsonKind.Integer || fields["i"].IntValue is not { } index || index < 0
            || fields["c"].Kind != JsonKind.Integer || fields["c"].IntValue is not { } offset || offset < 0
            || fields["f"].StringValue is not { } fingerprint || fields["q"].StringValue is not { } filter)
        {
            return null;
        }
        return new McpTranscriptCursor(meeting.ToString("D"), index, offset, fingerprint, filter);
    }

    public string Encoded => McpBase64Url.Encode(JsonValue.Object(
        ("m", Meeting), ("i", Index), ("c", Offset), ("f", Fingerprint), ("q", Filter)).Serialized());
}

/// <summary>Renders transcript lines and cuts them into pages that stay within <c>max_chars</c>.</summary>
internal static class McpTranscriptPager
{
    /// <param name="Text"><c>[mm:ss] ★ Label: text</c>, followed by <c>  ↳ note: …</c> lines for anchored notes.</param>
    public sealed record RenderedLine(TranscriptLine Line, string Text);

    public const int SpeakersLineCharacters = 1_000;
    /// <summary>The speakers line gives way down to this much when a small <c>max_chars</c> needs the room.</summary>
    public const int MinimumSpeakersLineCharacters = 40;
    /// <summary>Room kept for the "Lines a–b of n" line, which is written after the body is chosen.</summary>
    public const int LinesLineReserve = 120;
    /// <summary>Room kept for the cursor and the sentence after it.</summary>
    public const int FooterReserve = 360;
    /// <summary>Every page shows at least this much text, so a huge header cannot stall paging.</summary>
    public const int MinimumBodyCharacters = 400;

    public static List<RenderedLine> Render(McpMeetingView view, McpTranscriptSelection selection)
    {
        var speakers = selection.SpeakerIds is null ? null : new HashSet<string>(selection.SpeakerIds, StringComparer.Ordinal);
        var rendered = new List<RenderedLine>();
        for (var index = 0; index < view.Lines.Count; index++)
        {
            var line = view.Lines[index];
            if (speakers is not null && !speakers.Contains(line.SpeakerId))
            {
                continue;
            }
            if (selection.From is { } from && line.Start < from)
            {
                continue;
            }
            if (selection.To is { } to && line.Start >= to)
            {
                continue;
            }
            var anchored = view.HighlightsByLine.TryGetValue(index, out var found) ? found : [];
            var text = new StringBuilder();
            text.Append('[').Append(TranscriptMerger.Timestamp(line.Start)).Append("] ")
                .Append(anchored.Count == 0 ? "" : "★ ")
                .Append(view.Label(line.SpeakerId)).Append(": ").Append(line.Text);
            foreach (var highlight in anchored)
            {
                if (highlight.Note is { Length: > 0 } note)
                {
                    text.Append('\n').Append(McpText.NoteMarker).Append(note);
                }
            }
            rendered.Add(new RenderedLine(line, text.ToString()));
        }
        return rendered;
    }

    /// <summary>The meeting line and the speakers line that start every transcript Claude reads.</summary>
    public static List<string> HeaderLines(McpMeetingView view, McpDates dates, int speakersLimit = SpeakersLineCharacters)
    {
        var source = view.Archived.Record;
        var title = McpText.TitleLine(view.Title, dates.Iso(source.CreatedAt), TranscriptMerger.Timestamp(source.Duration), view.Id);
        var speakers = string.Join(" · ", view.Speakers.Select(speaker =>
            $"{speaker.Id} = {view.Label(speaker)}" + (speaker.IsMe ? McpText.SlovoUserMark : "")));
        var speakersText = speakers.Length == 0 ? McpText.NoSpeakers : McpMeetingView.Capped(speakers, speakersLimit);
        return [title, McpText.SpeakersPrefix + speakersText];
    }

    public static (string Text, McpTranscriptCursor? Next) Page(McpMeetingView view, McpTranscriptSelection selection,
        IReadOnlyList<RenderedLine> rendered, int start, int offset, bool changed, McpDates dates)
    {
        var notices = new List<string>();
        if (changed)
        {
            notices.Add(McpText.MeetingChanged);
        }
        if (offset > 0 && start < rendered.Count)
        {
            notices.Add(McpText.LineContinues(start + 1));
        }
        int FixedCharacters(List<string> header) =>
            header.Concat(notices).Sum(line => line.Length + 1)
            + McpText.TranscriptNotice.Length + 2
            + McpText.TranscriptOpenTag.Length + 1 + 1
            + McpText.TranscriptCloseTag.Length + 2;

        // The whole page stays within max_chars: when a long speakers line would leave the
        // body less than its minimum, the speakers line is cut instead of the page overflowing.
        var room = selection.MaxChars - LinesLineReserve - FooterReserve - MinimumBodyCharacters;
        var speakersLimit = SpeakersLineCharacters;
        var header = HeaderLines(view, dates, speakersLimit);
        var fixedCharacters = FixedCharacters(header);
        while (fixedCharacters > room && speakersLimit > MinimumSpeakersLineCharacters)
        {
            speakersLimit = Math.Max(MinimumSpeakersLineCharacters, speakersLimit - (fixedCharacters - room));
            header = HeaderLines(view, dates, speakersLimit);
            fixedCharacters = FixedCharacters(header);
        }
        var budget = Math.Max(MinimumBodyCharacters, selection.MaxChars - fixedCharacters - LinesLineReserve - FooterReserve);

        var pieces = new List<string>();
        var used = 0;
        var index = start;
        var pieceOffset = offset;
        int? cutOffset = null;
        while (index < rendered.Count)
        {
            var full = rendered[index].Text;
            var remainderStart = CharacterIndex(full, pieceOffset);
            var remainder = full[remainderStart..];
            var separator = pieces.Count == 0 ? 0 : 1;
            var length = remainder.Length;
            if (used + separator + length <= budget)
            {
                pieces.Add(remainder);
                used += separator + length;
                index++;
                pieceOffset = 0;
                continue;
            }
            // Whole lines only, unless one line alone is longer than a page.
            if (pieces.Count == 0)
            {
                var cut = CutIndex(remainder, budget);
                pieces.Add(remainder[..cut]);
                cutOffset = remainderStart + cut;
            }
            break;
        }

        string linesLine;
        if (rendered.Count == 0)
        {
            linesLine = McpText.NoLinesMatch;
        }
        else if (start >= rendered.Count)
        {
            linesLine = McpText.NoLinesAfter(rendered.Count);
        }
        else
        {
            var last = cutOffset is null ? index - 1 : index;
            linesLine = McpText.LinesRange(start + 1, last + 1, rendered.Count,
                TranscriptMerger.Timestamp(rendered[start].Line.Start), TranscriptMerger.Timestamp(rendered[last].Line.End));
        }

        McpTranscriptCursor? next = null;
        if (cutOffset is not null || index < rendered.Count)
        {
            next = new McpTranscriptCursor(view.Id, index, cutOffset ?? 0, view.Fingerprint, selection.FilterHash);
        }

        var text = new StringBuilder();
        text.Append(string.Join("\n", header)).Append('\n').Append(linesLine).Append('\n');
        foreach (var notice in notices)
        {
            text.Append(notice).Append('\n');
        }
        text.Append(McpText.TranscriptNotice).Append("\n\n").Append(McpText.TranscriptOpenTag).Append('\n');
        if (pieces.Count > 0)
        {
            text.Append(string.Join("\n", pieces)).Append('\n');
        }
        text.Append(McpText.TranscriptCloseTag).Append("\n\n");
        if (next is not null)
        {
            text.Append(McpText.NextCursorLine(next.Encoded)).Append('\n');
            text.Append(cutOffset is null ? McpText.ContinueWithLines(index + 1) : McpText.ContinueWithRestOfLine(index + 1));
        }
        else
        {
            text.Append(McpText.EndOfTranscript);
        }
        return (text.ToString(), next);
    }

    public static int PageCount(McpMeetingView view, McpTranscriptSelection selection, IReadOnlyList<RenderedLine> rendered,
        McpDates dates)
    {
        var pages = 0;
        var start = 0;
        var offset = 0;
        while (true)
        {
            pages++;
            var next = Page(view, selection, rendered, start, offset, changed: false, dates).Next;
            if (next is null || pages >= 100_000)
            {
                return pages;
            }
            start = (int)next.Index;
            offset = (int)next.Offset;
        }
    }

    /// <summary>Seconds, <c>mm:ss</c> or <c>h:mm:ss</c>; null for anything else or a negative value.</summary>
    public static double? TimelinePosition(JsonValue value)
    {
        switch (value.Kind)
        {
            case JsonKind.Integer:
                return value.IntValue >= 0 ? value.IntValue : null;
            case JsonKind.Number:
                var number = value.DoubleValue!.Value;
                return double.IsFinite(number) && number >= 0 ? number : null;
            case JsonKind.String:
                var text = McpUnicode.TrimHorizontalWhitespace(value.StringValue!);
                if (text.Length == 0 || Text.TextElements.Count(text) > 16)
                {
                    return null;
                }
                if (text.All(c => c is >= '0' and <= '9' or '.'))
                {
                    return double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
                           && double.IsFinite(seconds) ? seconds : null;
                }
                var parts = text.Split(':');
                if (parts.Length is < 2 or > 3
                    || !parts.All(part => part.Length > 0 && part.All(c => c is >= '0' and <= '9'))
                    || !parts.Skip(1).All(part => part.Length == 2))
                {
                    return null;
                }
                var numbers = new List<long>();
                foreach (var part in parts)
                {
                    if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    {
                        return null;
                    }
                    numbers.Add(parsed);
                }
                if (!numbers.Skip(1).All(number => number < 60))
                {
                    return null;
                }
                return numbers.Aggregate(0L, (total, next) => total * 60 + next);
            default:
                return null;
        }
    }

    /// <summary>The last character (grapheme cluster) boundary at or before a UTF-16 offset.</summary>
    public static int CharacterIndex(string text, int utf16Offset)
    {
        if (utf16Offset <= 0)
        {
            return 0;
        }
        var used = 0;
        while (used < text.Length)
        {
            var width = StringInfo.GetNextTextElementLength(text.AsSpan(used));
            if (used + width > utf16Offset)
            {
                break;
            }
            used += width;
        }
        return used;
    }

    /// <summary>
    /// Where to cut a line longer than a page: after the last whitespace that fits, or at the
    /// last fitting character when one word is longer than the page. Always cuts after at
    /// least one character, so paging moves forward.
    /// </summary>
    public static int CutIndex(string text, int limit)
    {
        var index = 0;
        int? lastBreak = null;
        while (index < text.Length)
        {
            var width = StringInfo.GetNextTextElementLength(text.AsSpan(index));
            if (index + width > limit)
            {
                break;
            }
            var isWhitespace = McpUnicode.IsWhitespaceCharacter(text.Substring(index, width));
            index += width;
            if (isWhitespace)
            {
                lastBreak = index;
            }
        }
        if (index == 0 && text.Length > 0)
        {
            return StringInfo.GetNextTextElementLength(text.AsSpan());
        }
        if (index < text.Length && lastBreak is { } breakIndex)
        {
            return breakIndex;
        }
        return index;
    }
}
