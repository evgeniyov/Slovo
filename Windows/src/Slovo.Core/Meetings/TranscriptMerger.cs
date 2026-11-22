using System.Globalization;
using System.Text;
using Slovo.Core.Json;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Text;

namespace Slovo.Core.Meetings;

/// <summary>One display line of a transcript: consecutive segments of one speaker.</summary>
public sealed record TranscriptLine
{
    /// <summary>The index in the lines array.</summary>
    public int Id { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    public string SpeakerId { get; init; } = "";
    public MeetingTrack Track { get; init; }
    public string Text { get; init; } = "";
    /// <summary>Indexes into <see cref="MeetingRecord.Segments"/>.</summary>
    public IReadOnlyList<int> SegmentIndices { get; init; } = [];
    /// <summary>Highlights anchored to this line.</summary>
    public IReadOnlyList<Guid> HighlightIds { get; init; } = [];

    public bool Equals(TranscriptLine? other) =>
        other is not null && Id == other.Id && Start.Equals(other.Start) && End.Equals(other.End)
        && SpeakerId == other.SpeakerId && Track == other.Track && Text == other.Text
        && SegmentIndices.SequenceEqual(other.SegmentIndices) && HighlightIds.SequenceEqual(other.HighlightIds);

    public override int GetHashCode() => HashCode.Combine(Id, Start, End, SpeakerId, Track, Text);
}

/// <summary>
/// Merges the "Me" and "Others" tracks into one transcript, builds display lines with
/// speakers and important moments, and writes <c>transcript.md</c> (port of the macOS
/// <c>TranscriptMerger</c>; both apps must write the same document byte for byte). Pure.
/// </summary>
public static class TranscriptMerger
{
    /// <summary>Consecutive segments of one speaker stay on one line across pauses up to this long.</summary>
    public const double LineJoinGapSeconds = 3;
    public const double MaximumLineSeconds = 60;
    /// <summary>A moment pressed this soon after a line ended belongs to that line.</summary>
    public const double HighlightLookBackSeconds = 10;
    /// <summary>A moment pressed this soon before a line started belongs to that line.</summary>
    public const double HighlightLookAheadSeconds = 5;
    public const int HighlightExcerptCharacters = 120;
    public const string HighlightMark = "★";

    /// <summary>
    /// Orders segments of both tracks by start time and collapses consecutive segments of
    /// the same speaker. Microphone segments are always "Me" and system audio segments
    /// "Others"; segments without text are dropped. On equal start times the microphone
    /// comes first.
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> Merge(IEnumerable<TranscriptSegment> microphone, IEnumerable<TranscriptSegment> system)
    {
        var tagged = microphone.Select(s => s with { Speaker = Speaker.Me })
            .Concat(system.Select(s => s with { Speaker = Speaker.Others }));
        var ordered = tagged
            .Select((segment, offset) => (offset, segment: segment with { Text = NormalizedText(segment.Text) }))
            .Where(p => p.segment.Text.Length > 0)
            .OrderBy(p => p.segment.Start)
            .ThenBy(p => p.offset)
            .Select(p => p.segment);

        var merged = new List<TranscriptSegment>();
        foreach (var segment in ordered)
        {
            if (merged.Count > 0 && merged[^1].Speaker == segment.Speaker)
            {
                var last = merged[^1];
                merged[^1] = last with { End = Math.Max(last.End, segment.End), Text = last.Text + " " + segment.Text };
            }
            else
            {
                merged.Add(segment);
            }
        }
        return merged;
    }

    /// <summary>
    /// <c>mm:ss</c>, or <c>h:mm:ss</c> from one hour on. Negative, non-finite and absurdly
    /// large times, which only a hand-edited file holds, are clamped to <see cref="MeetingTime"/>.
    /// </summary>
    public static string Timestamp(double seconds)
    {
        var total = (long)Math.Floor(MeetingTime.Clamped(seconds));
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var rest = total % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{rest:00}");
    }

    /// <summary>The localized name of a track's speaker: «Я»/Me or «Собеседники»/Others.</summary>
    public static string SpeakerName(Speaker speaker, AppLanguage? language = null) =>
        SpeakerName(speaker, MeetingLanguage.Code(language));

    internal static string SpeakerName(Speaker speaker, string code) =>
        MeetingLanguage.Table(code).Get(speaker == Speaker.Me ? "meetings.speaker.me" : "meetings.speaker.others");

    /// <summary>One transcript line: <c>[mm:ss] Me: text</c>.</summary>
    public static string Line(TranscriptSegment segment, AppLanguage? language = null) =>
        Line(segment, MeetingLanguage.Code(language));

    static string Line(TranscriptSegment segment, string code) =>
        $"[{Timestamp(segment.Start)}] {SpeakerName(segment.Speaker, code)}: {NormalizedText(segment.Text)}";

    /// <summary>The transcript as plain text, one line per segment, for copying.</summary>
    public static string PlainText(IEnumerable<TranscriptSegment> segments, AppLanguage? language = null)
    {
        var code = MeetingLanguage.Code(language);
        return string.Join("\n", segments.Select(s => Line(s, code)));
    }

    /// <summary>
    /// The first format's <c>transcript.md</c>: a title line, the date, then
    /// <c>[mm:ss] Me: …</c> lines with speaker names in the given language. The date is
    /// shown in <paramref name="timeZone"/>, the computer's own by default.
    /// </summary>
    public static string Markdown(string title, DateTimeOffset date, IReadOnlyList<TranscriptSegment> segments,
                                  AppLanguage? language = null, TimeZoneInfo? timeZone = null) =>
        Markdown(title, date, segments, MeetingLanguage.Code(language), timeZone);

    static string Markdown(string title, DateTimeOffset date, IReadOnlyList<TranscriptSegment> segments, string code, TimeZoneInfo? timeZone)
    {
        var document = new StringBuilder();
        document.Append("# ").Append(NormalizedText(title)).Append("\n\n")
            .Append(MeetingDates.LongDateShortTime(date, code, timeZone)).Append("\n\n");
        if (segments.Count == 0)
        {
            document.Append(MeetingLanguage.Table(code).Get("meetings.transcript.empty")).Append('\n');
        }
        else
        {
            // Blank lines keep every speaker turn its own Markdown paragraph.
            document.Append(string.Join("\n\n", segments.Select(s => Line(s, code)))).Append('\n');
        }
        return document.ToString();
    }

    /// <summary>Collapses runs of whitespace and line breaks, so every segment stays on one line.</summary>
    public static string NormalizedText(string text) => TextElements.NormalizeWhitespace(text);

    // MARK: - Speakers and important moments. New builds store one segment per transcribed
    // clip; lines exist only for display and for transcript.md.

    /// <summary>
    /// Display lines: segments sorted by start (microphone first on ties), consecutive
    /// segments of one speaker joined while the gap is at most 3 s and the line at most
    /// 60 s long. The speaker filter applies after merging, and highlights are anchored
    /// before filtering, so a moment never jumps to another speaker's line.
    /// </summary>
    public static IReadOnlyList<TranscriptLine> Lines(MeetingRecord meeting, IReadOnlySet<string>? speakerFilter = null) =>
        Filtered(AnchoredLines(meeting), speakerFilter);

    /// <summary>Every line of the meeting with its highlights.</summary>
    internal static IReadOnlyList<TranscriptLine> AnchoredLines(MeetingRecord meeting)
    {
        var lines = MergedLines(meeting);
        var anchors = lines.Select(_ => new List<Guid>()).ToList();
        foreach (var highlight in OrderedHighlights(meeting.Highlights))
        {
            if (HighlightLineIndex(highlight, lines) is { } index)
            {
                anchors[index].Add(highlight.Id);
            }
        }
        return lines.Select((line, index) => anchors[index].Count == 0 ? line : line with { HighlightIds = anchors[index] }).ToList();
    }

    static List<TranscriptLine> MergedLines(MeetingRecord meeting)
    {
        var ordered = meeting.Segments
            .Select((segment, offset) => (offset, segment, text: NormalizedText(segment.Text)))
            .Where(p => p.text.Length > 0)
            .OrderBy(p => p.segment.Start)
            .ThenBy(p => TrackOrder(MeetingTracks.FromSpeaker(p.segment.Speaker)))
            .ThenBy(p => p.offset);

        var lines = new List<TranscriptLine>();
        var indices = new List<List<int>>();
        foreach (var (index, segment, text) in ordered)
        {
            var speakerId = meeting.SpeakerIdFor(segment);
            var track = MeetingTracks.FromSpeaker(segment.Speaker);
            var end = Math.Max(segment.Start, segment.End);
            if (lines.Count > 0)
            {
                var last = lines[^1];
                if (last.SpeakerId == speakerId && last.Track == track
                    && segment.Start - last.End <= LineJoinGapSeconds
                    && Math.Max(last.End, end) - last.Start <= MaximumLineSeconds)
                {
                    indices[^1].Add(index);
                    lines[^1] = last with { End = Math.Max(last.End, end), Text = last.Text + " " + text, SegmentIndices = indices[^1] };
                    continue;
                }
            }
            indices.Add([index]);
            lines.Add(new TranscriptLine
            {
                Id = lines.Count,
                Start = segment.Start,
                End = end,
                SpeakerId = speakerId,
                Track = track,
                Text = text,
                SegmentIndices = indices[^1],
            });
        }
        return lines;
    }

    internal static IReadOnlyList<TranscriptLine> Filtered(IReadOnlyList<TranscriptLine> lines, IReadOnlySet<string>? speakerFilter)
    {
        if (speakerFilter is null)
        {
            return lines;
        }
        return lines.Where(line => speakerFilter.Contains(line.SpeakerId)).Select((line, index) => line with { Id = index }).ToList();
    }

    static IEnumerable<MeetingHighlight> OrderedHighlights(IEnumerable<MeetingHighlight> highlights) =>
        highlights.OrderBy(h => h.Time).ThenBy(h => h.CreatedAt).ThenBy(h => h.Id.ToUpperString(), StringComparer.Ordinal);

    static int TrackOrder(MeetingTrack track) => track == MeetingTrack.Microphone ? 0 : 1;

    /// <summary>
    /// <c>displayName</c>; else «Я»/Me when <c>isMe</c>; else «Спикер N»/Speaker N when
    /// numbered; else the track's «Я»/Me or «Собеседники»/Others.
    /// </summary>
    public static string SpeakerLabel(MeetingSpeaker speaker, AppLanguage? language = null) =>
        SpeakerLabel(speaker, MeetingLanguage.Code(language));

    static string SpeakerLabel(MeetingSpeaker speaker, string code)
    {
        if (speaker.DisplayName is { } displayName && NormalizedText(displayName) is { Length: > 0 } name)
        {
            return name;
        }
        if (speaker.IsMe)
        {
            return SpeakerName(Speaker.Me, code);
        }
        if (speaker.Number is { } number)
        {
            return MeetingLanguage.Table(code).Format("meetings.speaker.numbered", number);
        }
        return SpeakerName(speaker.Track.ToSpeaker(), code);
    }

    /// <summary>
    /// Whether a speaker is the person who recorded: marked «Это я», or the microphone's
    /// fallback while no speaker is marked. Microphone speech was «Я» before speakers were
    /// separated, and stays so until the user picks someone else.
    /// </summary>
    public static bool IsMe(MeetingSpeaker speaker, MeetingRecord meeting)
    {
        if (speaker.IsMe)
        {
            return true;
        }
        return speaker.Id == MeetingSpeaker.Fallback(MeetingTrack.Microphone).Id && speaker.Track == MeetingTrack.Microphone
            && !meeting.Speakers.Any(s => s.IsMe);
    }

    /// <summary>
    /// The label a speaker has in this meeting. Like <see cref="SpeakerLabel(MeetingSpeaker, AppLanguage?)"/>,
    /// except that an unnamed, unnumbered microphone speaker who is not «Я» reads «Микрофон»:
    /// once another speaker is marked «Это я», the microphone's own name «Я» would otherwise
    /// appear twice.
    /// </summary>
    public static string SpeakerLabel(MeetingSpeaker speaker, MeetingRecord meeting, AppLanguage? language = null) =>
        SpeakerLabel(speaker, meeting, MeetingLanguage.Code(language));

    static string SpeakerLabel(MeetingSpeaker speaker, MeetingRecord meeting, string code)
    {
        var shown = speaker with { IsMe = IsMe(speaker, meeting) };
        var hasName = NormalizedText(shown.DisplayName ?? "").Length > 0;
        if (!shown.IsMe && !hasName && shown.Number is null && shown.Track == MeetingTrack.Microphone)
        {
            return MeetingLanguage.Table(code).Get("meetings.speaker.microphone");
        }
        return SpeakerLabel(shown, code);
    }

    /// <summary>
    /// The <c>transcript.md</c> document, also used for Export and by the MCP server. A
    /// meeting with only unrenamed fallback speakers and no highlights gives the first
    /// format's document; otherwise the document adds the date with the duration,
    /// participants, Claude's notes, important moments and headings. Dates are shown in
    /// <paramref name="timeZone"/>, the computer's own by default.
    /// </summary>
    public static string Markdown(MeetingRecord meeting, AppLanguage? language = null, IReadOnlySet<string>? speakerFilter = null,
                                  TimeZoneInfo? timeZone = null)
    {
        var code = MeetingLanguage.Code(language);
        var table = MeetingLanguage.Table(code);
        var all = AnchoredLines(meeting);
        var visible = Filtered(all, speakerFilter);

        if (HasOnlyFirstFormatContent(meeting))
        {
            var segments = visible
                .Select(line => new TranscriptSegment { Start = line.Start, End = line.End, Speaker = line.Track.ToSpeaker(), Text = line.Text })
                .ToList();
            return Markdown(meeting.Title, meeting.CreatedAt, segments, code, timeZone);
        }

        var document = new StringBuilder();
        document.Append("# ").Append(NormalizedText(meeting.Title)).Append("\n\n");
        var dateLine = MeetingDates.LongDateShortTime(meeting.CreatedAt, code, timeZone);
        if (double.IsFinite(meeting.Duration) && meeting.Duration > 0)
        {
            dateLine += " · " + Timestamp(meeting.Duration);
        }
        document.Append(dateLine).Append("\n\n");

        var shownSpeakers = visible.Select(line => line.SpeakerId).ToHashSet(StringComparer.Ordinal);
        var participants = meeting.EffectiveSpeakers
            .Where(s => shownSpeakers.Contains(s.Id))
            .Select(s => SpeakerLabel(s, meeting, code))
            .ToList();
        if (participants.Count > 0)
        {
            document.Append(table.Format("meetings.transcript.participants", string.Join(", ", participants))).Append("\n\n");
        }

        if (meeting.ClaudeNotes is { IsEmpty: false } notes)
        {
            document.Append(ClaudeNotes(notes, code, timeZone));
        }

        var moments = HighlightItems(meeting, all, speakerFilter, code);
        if (moments.Count > 0)
        {
            document.Append("## ").Append(table.Get("meetings.transcript.highlightsHeading")).Append("\n\n");
            document.Append(string.Join("\n", moments)).Append("\n\n");
        }

        document.Append("## ").Append(table.Get("meetings.transcript.heading")).Append("\n\n");
        if (visible.Count == 0)
        {
            document.Append(table.Get("meetings.transcript.empty")).Append('\n');
        }
        else
        {
            // Blank lines keep every line its own Markdown paragraph.
            document.Append(string.Join("\n\n", visible.Select(line => Text(line, meeting, code)))).Append('\n');
        }
        return document.ToString();
    }

    /// <summary>The transcript lines as plain text for copying, with ★ on lines that have highlights.</summary>
    public static string PlainText(MeetingRecord meeting, AppLanguage? language = null, IReadOnlySet<string>? speakerFilter = null)
    {
        var code = MeetingLanguage.Code(language);
        return string.Join("\n", Lines(meeting, speakerFilter).Select(line => Text(line, meeting, code)));
    }

    /// <summary>
    /// Important moments in the <c>transcript.md</c> list format, for "Copy moments":
    /// <c>- [12:40] Аня: «first 120 characters…» — note</c>. Moments with no line keep time and
    /// note; with a speaker filter only moments on the chosen speakers' lines are listed
    /// (the macOS <c>AppModel.highlightsText</c>).
    /// </summary>
    public static string HighlightsText(MeetingRecord meeting, AppLanguage? language = null, IReadOnlySet<string>? speakerFilter = null)
    {
        var code = MeetingLanguage.Code(language);
        var lines = AnchoredLines(meeting);
        var items = new List<string>();
        foreach (var highlight in meeting.Highlights.OrderBy(h => h.Time))
        {
            var line = AnchoredLine(highlight, lines);
            if (speakerFilter is { Count: > 0 } && (line is null || !speakerFilter.Contains(line.SpeakerId)))
            {
                continue;
            }
            var text = $"- [{Timestamp(highlight.Time)}]";
            if (line is not null)
            {
                var name = meeting.SpeakerWithId(line.SpeakerId) is { } speaker ? SpeakerLabel(speaker, meeting, code) : "";
                text += $" {name}: «{Excerpt(line.Text)}»";
            }
            if (highlight.Note?.Trim() is { Length: > 0 } note)
            {
                text += " — " + note;
            }
            items.Add(text);
        }
        return string.Join("\n", items);
    }

    /// <summary>
    /// The line an important moment belongs to: the line that lists it, else the anchoring
    /// rule of <see cref="HighlightLineIndex"/>. Pass unfiltered lines, so the rule never picks
    /// a line of another speaker because the right one is filtered out (the macOS
    /// <c>anchoredLine(of:in:)</c>).
    /// </summary>
    public static TranscriptLine? AnchoredLine(MeetingHighlight highlight, IReadOnlyList<TranscriptLine> lines)
    {
        if (lines.FirstOrDefault(line => line.HighlightIds.Contains(highlight.Id)) is { } listed)
        {
            return listed;
        }
        return HighlightLineIndex(highlight, lines) is { } index && index < lines.Count ? lines[index] : null;
    }

    /// <summary>
    /// The line a highlight belongs to: the line containing its time, else the last line
    /// that ended within 10 s before it (people press after hearing the important part),
    /// else the next line starting within 5 s after it; null when none is that close.
    ///
    /// When lines of both tracks contain the time, the one that started last wins: it is the
    /// most recent thing said, and a star put on a line lands at that line's start, so
    /// toggling it finds the same line again.
    /// </summary>
    public static int? HighlightLineIndex(MeetingHighlight highlight, IReadOnlyList<TranscriptLine> lines)
    {
        var time = highlight.Time;
        if (!double.IsFinite(time))
        {
            return null;
        }

        int? containing = null;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (!(line.Start <= time && time <= line.End))
            {
                continue;
            }
            if (containing is { } current && lines[current].Start > line.Start)
            {
                continue;
            }
            containing = index;
        }
        if (containing is not null)
        {
            return containing;
        }

        int? before = null;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (!(line.End < time && time - line.End <= HighlightLookBackSeconds))
            {
                continue;
            }
            if (before is { } current && lines[current].End > line.End)
            {
                continue;
            }
            before = index;
        }
        if (before is not null)
        {
            return before;
        }

        int? after = null;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (!(line.Start > time && line.Start - time <= HighlightLookAheadSeconds))
            {
                continue;
            }
            if (after is { } current && lines[current].Start <= line.Start)
            {
                continue;
            }
            after = index;
        }
        return after;
    }

    // MARK: - Document parts

    /// <summary>
    /// Claude's own account of the meeting, under its own heading and its author's name and
    /// date, before the moments and the transcript. A reader of the document must never take
    /// it for something that was said, and the recovery parser skips it: headings and list
    /// items are ignored, and the text of a note can never start like a <c>[mm:ss]</c> line.
    /// </summary>
    static string ClaudeNotes(MeetingNotes notes, string code, TimeZoneInfo? timeZone)
    {
        var table = MeetingLanguage.Table(code);
        var section = new StringBuilder();
        section.Append("## ").Append(table.Get("meetings.transcript.claudeHeading")).Append("\n\n");
        section.Append(table.Format("meetings.transcript.claudeWritten", notes.Author, MeetingDates.LongDateShortTime(notes.WrittenAt, code, timeZone)))
            .Append("\n\n");
        if (notes.Summary.Length > 0)
        {
            section.Append(notes.Summary).Append("\n\n");
        }
        if (notes.Decisions.Count > 0)
        {
            section.Append(table.Get("meetings.transcript.claudeDecisions")).Append("\n\n");
            section.Append(string.Join("\n", notes.Decisions.Select(d => "- " + d))).Append("\n\n");
        }
        if (notes.Tasks.Count > 0)
        {
            section.Append(table.Get("meetings.transcript.claudeTasks")).Append("\n\n");
            section.Append(string.Join("\n", notes.Tasks.Select(t => "- " + t))).Append("\n\n");
        }
        return section.ToString();
    }

    /// <summary>
    /// True when nothing in the meeting needs the second format: every speaker shown is an
    /// unrenamed fallback, no moment is marked and Claude wrote nothing. Such meetings keep
    /// the first format's document, which older builds and scripts already read.
    /// </summary>
    internal static bool HasOnlyFirstFormatContent(MeetingRecord meeting)
    {
        if (meeting.Highlights.Count > 0 || meeting.ClaudeNotes is not null)
        {
            return false;
        }
        return meeting.EffectiveSpeakers.All(speaker =>
            speaker.Id == MeetingSpeaker.Fallback(speaker.Track).Id && speaker.Number is null && !speaker.IsMe
            && NormalizedText(speaker.DisplayName ?? "").Length == 0);
    }

    /// <summary><c>[mm:ss] ★ Name: text</c>, the star only on lines with highlights.</summary>
    internal static string Text(TranscriptLine line, MeetingRecord meeting, string code)
    {
        var mark = line.HighlightIds.Count == 0 ? "" : HighlightMark + " ";
        return $"[{Timestamp(line.Start)}] {mark}{Label(line, meeting, code)}: {line.Text}";
    }

    internal static string Label(TranscriptLine line, MeetingRecord meeting, string code) =>
        meeting.SpeakerWithId(line.SpeakerId) is { } speaker
            ? SpeakerLabel(speaker, meeting, code)
            : SpeakerName(line.Track.ToSpeaker(), code);

    /// <summary>The start of a line for the list of moments, with an ellipsis when it was cut.</summary>
    public static string Excerpt(string text)
    {
        if (TextElements.Count(text) <= HighlightExcerptCharacters)
        {
            return text;
        }
        return TextElements.TrimSpaces(TextElements.Prefix(text, HighlightExcerptCharacters)) + "…";
    }

    /// <summary>
    /// <c>- [mm:ss] Name: «excerpt» — note</c> for each moment in time order. With a speaker
    /// filter only moments on the chosen speakers' lines are listed.
    /// </summary>
    static List<string> HighlightItems(MeetingRecord meeting, IReadOnlyList<TranscriptLine> lines, IReadOnlySet<string>? speakerFilter, string code)
    {
        var lineOfHighlight = new Dictionary<Guid, int>();
        for (var index = 0; index < lines.Count; index++)
        {
            foreach (var id in lines[index].HighlightIds)
            {
                lineOfHighlight[id] = index;
            }
        }
        var items = new List<string>();
        foreach (var highlight in OrderedHighlights(meeting.Highlights))
        {
            int? index = lineOfHighlight.TryGetValue(highlight.Id, out var found) ? found : null;
            if (speakerFilter is not null && (index is not { } i || !speakerFilter.Contains(lines[i].SpeakerId)))
            {
                continue;
            }
            var item = $"- [{Timestamp(highlight.Time)}]";
            if (index is { } lineIndex)
            {
                var line = lines[lineIndex];
                item += $" {Label(line, meeting, code)}: «{Excerpt(line.Text)}»";
            }
            if (highlight.Note is { } rawNote && NormalizedText(rawNote) is { Length: > 0 } note)
            {
                item += " — " + note;
            }
            items.Add(item);
        }
        return items;
    }
}
