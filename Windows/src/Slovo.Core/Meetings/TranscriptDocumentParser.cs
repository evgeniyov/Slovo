using System.Globalization;
using System.Text;
using Slovo.Core.Json;
using Slovo.Core.Model;
using Slovo.Core.Text;

namespace Slovo.Core.Meetings;

/// <summary>What <see cref="MeetingStore.ParseTranscript"/> read back from a <c>transcript.md</c>.</summary>
public sealed record ParsedTranscript(string? Title, IReadOnlyList<TranscriptSegment> Segments, IReadOnlyList<MeetingSpeaker> Speakers,
                                      IReadOnlyList<MeetingHighlight> Highlights);

/// <summary>
/// The recovery parser for <c>transcript.md</c> of either format and interface language (port
/// of the macOS <c>TranscriptDocumentParser</c>).
///
/// It skips headings, list items and the participants line, and turns every
/// <c>[mm:ss] ★ Name: text</c> line into a segment. Speaker names map back as far as a
/// document allows: Me/Я and Others/Собеседники become the track fallbacks, «Спикер N» a
/// numbered microphone speaker, and any other name a numbered microphone speaker with that
/// name. The system track of separated calls cannot be told apart in a document.
/// </summary>
internal static class TranscriptDocumentParser
{
    readonly record struct Line(long Seconds, bool Starred, string Name, string Text);

    sealed class Vocabulary
    {
        public readonly Dictionary<string, Speaker> Fallbacks = new(StringComparer.Ordinal);
        public readonly List<(string Prefix, string Suffix)> Numbered = [];
        public readonly List<string> ParticipantPrefixes = [];

        public Vocabulary()
        {
            foreach (var code in new[] { "en", "ru" })
            {
                Fallbacks[TranscriptMerger.SpeakerName(Speaker.Me, code)] = Speaker.Me;
                Fallbacks[TranscriptMerger.SpeakerName(Speaker.Others, code)] = Speaker.Others;
                var table = MeetingLanguage.Table(code);
                var pattern = table.Get("meetings.speaker.numbered");
                var placeholder = pattern.IndexOf("%ld", StringComparison.Ordinal);
                if (placeholder >= 0)
                {
                    Numbered.Add((pattern[..placeholder], pattern[(placeholder + 3)..]));
                }
                var participants = table.Get("meetings.transcript.participants");
                var slot = participants.IndexOf("%@", StringComparison.Ordinal);
                if (slot > 0)
                {
                    ParticipantPrefixes.Add(participants[..slot]);
                }
            }
        }

        public int? NumberInLabel(string name)
        {
            foreach (var (prefix, suffix) in Numbered)
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)
                    || name.Length < prefix.Length + suffix.Length)
                {
                    continue;
                }
                var middle = name[prefix.Length..^suffix.Length];
                // Above the bound the speaker id would not be read back from meeting.json,
                // and the next named speaker's number could overflow.
                if (middle.Length > 0 && middle.All(char.IsAsciiDigit)
                    && long.TryParse(middle, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                    && value <= int.MaxValue && MeetingSpeaker.ValidNumber((int)value) is { } number)
                {
                    return number;
                }
            }
            return null;
        }
    }

    static readonly char[] Newlines = ['\n', '\r', '\v', '\f', '\u0085', (char)0x2028, (char)0x2029];

    public static ParsedTranscript Parse(string text)
    {
        var vocabulary = new Vocabulary();
        string? title = null;
        var lines = new List<Line>();
        foreach (var rawLine in text.Split(Newlines, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = TextElements.TrimSpaces(rawLine);
            if (title is null && line.StartsWith("# ", StringComparison.Ordinal))
            {
                title = TextElements.TrimSpaces(line[2..]);
                continue;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal)
                || vocabulary.ParticipantPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }
            if (ParseLine(line) is { } parsed)
            {
                lines.Add(parsed);
            }
        }

        // Named speakers get numbers above every «Спикер N» in the document, so the two kinds
        // never collide.
        var nextNumber = lines.Select(l => vocabulary.NumberInLabel(l.Name)).OfType<int>().DefaultIfEmpty(0).Max();
        var speakers = new List<MeetingSpeaker>();
        var speakerForName = new Dictionary<string, MeetingSpeaker>(StringComparer.Ordinal);
        var segments = new List<TranscriptSegment>();
        var highlights = new List<MeetingHighlight>();

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var start = (double)line.Seconds;
            if (segments.Count > 0)
            {
                segments[^1] = segments[^1] with { End = Math.Max(segments[^1].Start, start) };
            }

            var track = Speaker.Me;
            string? speakerId = null;
            if (vocabulary.Fallbacks.TryGetValue(line.Name, out var fallback))
            {
                track = fallback;
            }
            else
            {
                if (!speakerForName.TryGetValue(line.Name, out var speaker))
                {
                    if (vocabulary.NumberInLabel(line.Name) is { } number)
                    {
                        speaker = new MeetingSpeaker
                        {
                            Id = "s" + number.ToString(CultureInfo.InvariantCulture),
                            Track = MeetingTrack.Microphone,
                            Number = number,
                            ColorIndex = (number - 1) % MeetingSpeaker.PaletteSize,
                        };
                    }
                    else
                    {
                        nextNumber++;
                        speaker = new MeetingSpeaker
                        {
                            Id = "s" + nextNumber.ToString(CultureInfo.InvariantCulture),
                            Track = MeetingTrack.Microphone,
                            Number = nextNumber,
                            DisplayName = MeetingEdits.SanitizedName(line.Name),
                            ColorIndex = (nextNumber - 1) % MeetingSpeaker.PaletteSize,
                        };
                    }
                    speakerForName[line.Name] = speaker;
                    if (!speakers.Any(s => s.Id == speaker.Id))
                    {
                        speakers.Add(speaker);
                    }
                }
                speakerId = speaker.Id;
            }

            segments.Add(new TranscriptSegment { Start = start, End = start, Speaker = track, Text = line.Text, SpeakerId = speakerId });
            if (line.Starred)
            {
                highlights.Add(new MeetingHighlight
                {
                    Id = StableId(index, line),
                    Time = start,
                    Source = HighlightSource.Transcript,
                    CreatedAt = AppleDate.ReferenceDate,
                });
            }
        }
        return new ParsedTranscript(title, segments, speakers, highlights);
    }

    /// <summary><c>[mm:ss]</c> or <c>[h:mm:ss]</c>, an optional <c>★ </c>, a name of at most 40 characters and <c>: </c> before the text.</summary>
    static Line? ParseLine(string line)
    {
        if (!line.StartsWith('['))
        {
            return null;
        }
        var close = line.IndexOf(']');
        if (close < 0)
        {
            return null;
        }
        var parts = line[1..close].Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return null;
        }
        long seconds = 0;
        foreach (var part in parts)
        {
            // Swift's Int(_:) takes an optional sign and ASCII digits, nothing else.
            if (part.Length == 0 || !long.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                || value < 0 || !part.TrimStart('+', '-').All(char.IsAsciiDigit))
            {
                return null;
            }
            try
            {
                seconds = checked(seconds * 60 + value);
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        var rest = TextElements.TrimSpaces(line[(close + 1)..]);
        var starred = false;
        if (rest.StartsWith(TranscriptMerger.HighlightMark + " ", StringComparison.Ordinal))
        {
            starred = true;
            rest = TextElements.TrimSpaces(rest[TranscriptMerger.HighlightMark.Length..]);
        }
        var colon = rest.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0)
        {
            return null;
        }
        var name = TextElements.TrimSpaces(rest[..colon]);
        if (name.Length == 0 || TextElements.Count(name) > MeetingEdits.MaximumNameCharacters)
        {
            return null;
        }
        return new Line(seconds, starred, name, rest[(colon + 2)..]);
    }

    /// <summary>
    /// The same document always gives the same highlight ids, so a reader that parses it on
    /// every request, like the MCP server, can refer to a moment between requests. FNV-1a over
    /// the UTF-8 of <c>index|seconds|name|text</c>, two 64-bit lanes, exactly as the macOS app
    /// computes it, so both apps give a recovered moment the same id.
    /// </summary>
    internal static Guid StableId(int index, long seconds, string name, string text)
    {
        ulong first = 0xcbf2_9ce4_8422_2325;
        ulong second = 0x6c62_272e_07bb_0142;
        const ulong prime = 0x0000_0100_0000_01b3;
        foreach (var value in Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{index}|{seconds}|{name}|{text}")))
        {
            first = unchecked((first ^ value) * prime);
            second = unchecked((second ^ value) * prime);
        }
        Span<byte> bytes = stackalloc byte[16];
        for (var offset = 0; offset < 8; offset++)
        {
            bytes[offset] = (byte)(first >> (offset * 8));
            bytes[offset + 8] = (byte)(second >> (offset * 8));
        }
        // Version 8 (custom) and the RFC 4122 variant, so the value is a well-formed UUID.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    static Guid StableId(int index, Line line) => StableId(index, line.Seconds, line.Name, line.Text);
}
