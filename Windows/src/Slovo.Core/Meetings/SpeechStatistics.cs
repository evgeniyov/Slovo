using System.Globalization;
using System.Text;
using Slovo.Core.Model;

namespace Slovo.Core.Meetings;

/// <summary>
/// How much of a meeting one voice said. Derived from the transcript every time it is shown
/// and never stored, so a meeting recorded long before these numbers existed has them the
/// moment it is opened.
/// </summary>
public sealed record SpeakerSpeechStats
{
    public SpeakerSpeechStats(string speakerId, double speakingSeconds = 0, double share = 0, int turns = 0,
                              double averageTurnSeconds = 0, int words = 0, double? wordsPerMinute = null)
    {
        SpeakerId = speakerId;
        SpeakingSeconds = speakingSeconds;
        Share = share;
        Turns = turns;
        AverageTurnSeconds = averageTurnSeconds;
        Words = words;
        WordsPerMinute = wordsPerMinute;
    }

    public string SpeakerId { get; init; }
    /// <summary>
    /// Seconds this speaker was speaking. Segments of one speaker that overlap count once,
    /// so a run that cut a phrase in two cannot inflate the total.
    /// </summary>
    public double SpeakingSeconds { get; init; }
    /// <summary>
    /// This speaker's part of all the speaking in the meeting, 0...1. The shares add up to 1,
    /// so they answer "who talked how much" even when two voices ran over each other; how
    /// much of the recording was speech at all is <see cref="SpeechStats.SpeechShare"/>.
    /// </summary>
    public double Share { get; init; }
    /// <summary>
    /// Times the speaker took the floor: a run of their own segments, ended by another voice
    /// or by a silence of their own longer than the pause threshold.
    /// </summary>
    public int Turns { get; init; }
    /// <summary><see cref="SpeakingSeconds"/> over <see cref="Turns"/>.</summary>
    public double AverageTurnSeconds { get; init; }
    public int Words { get; init; }
    /// <summary>
    /// Words over speaking minutes, or null when so little was said that a rate would describe
    /// where the segments were cut rather than the speaker.
    /// </summary>
    public double? WordsPerMinute { get; init; }

    public string Id => SpeakerId;
}

/// <summary>A meeting's speech in numbers: who spoke how much, and how much of the recording was speech at all.</summary>
public sealed record SpeechStats
{
    public SpeechStats(IReadOnlyList<SpeakerSpeechStats>? speakers = null, double totalSeconds = 0, double speechSeconds = 0,
                       double speechShare = 0, int pauseCount = 0, double longestPauseSeconds = 0, double pauseSeconds = 0)
    {
        Speakers = speakers ?? [];
        TotalSeconds = totalSeconds;
        SpeechSeconds = speechSeconds;
        SpeechShare = speechShare;
        PauseCount = pauseCount;
        LongestPauseSeconds = longestPauseSeconds;
        PauseSeconds = pauseSeconds;
    }

    /// <summary>Only speakers who said something, the longest speaking time first; ties keep the meeting's own speaker order.</summary>
    public IReadOnlyList<SpeakerSpeechStats> Speakers { get; init; }
    /// <summary>
    /// The stretch speech and silence divide: the recording's duration, or the end of the
    /// last word when a hand-edited duration stops before it.
    /// </summary>
    public double TotalSeconds { get; init; }
    /// <summary>Seconds with at least one voice speaking; two voices at once count once.</summary>
    public double SpeechSeconds { get; init; }
    /// <summary><see cref="SpeechSeconds"/> of <see cref="TotalSeconds"/>, 0...1.</summary>
    public double SpeechShare { get; init; }
    /// <summary>
    /// Silences between the first and the last word longer than <see cref="PauseSeconds"/>.
    /// The quiet before a meeting gets going and after it ends is not a pause in the conversation.
    /// </summary>
    public int PauseCount { get; init; }
    public double LongestPauseSeconds { get; init; }
    /// <summary>The silence a gap has to beat to count as a pause.</summary>
    public double PauseSeconds { get; init; }

    public double SilenceSeconds => Math.Max(0, TotalSeconds - SpeechSeconds);

    /// <summary>True when nothing was said at all: no transcript yet, or only empty segments.</summary>
    public bool IsEmpty => Speakers.Count == 0;

    public bool Equals(SpeechStats? other) =>
        other is not null && Speakers.SequenceEqual(other.Speakers) && TotalSeconds.Equals(other.TotalSeconds)
        && SpeechSeconds.Equals(other.SpeechSeconds) && SpeechShare.Equals(other.SpeechShare) && PauseCount == other.PauseCount
        && LongestPauseSeconds.Equals(other.LongestPauseSeconds) && PauseSeconds.Equals(other.PauseSeconds);

    public override int GetHashCode() => HashCode.Combine(Speakers.Count, TotalSeconds, SpeechSeconds, PauseCount);
}

/// <summary>
/// Derives who spoke how much, at what pace, and where a meeting went quiet, from the
/// segments and speakers it already has. Pure (port of the macOS <c>SpeechStatsCalculator</c>).
/// </summary>
public static class SpeechStatsCalculator
{
    /// <summary>
    /// A silence has to beat this to be worth calling a pause, and it ends the turn of the
    /// speaker before it. Shorter gaps are the breathing inside one thought: the transcript
    /// itself keeps a speaker's line together across three seconds.
    /// </summary>
    public const double DefaultPauseSeconds = 5;
    /// <summary>
    /// Under this much speech, words per minute says more about where the segments were cut
    /// than about the speaker, so no pace is reported.
    /// </summary>
    public const double MinimumPaceSeconds = 10;

    public static SpeechStats Compute(MeetingRecord meeting, double pauseSeconds = DefaultPauseSeconds)
    {
        var spoken = Ordered(meeting);
        var totalSeconds = Math.Max(MeetingTime.Clamped(meeting.Duration), spoken.Count > 0 ? spoken[^1].End : 0);
        if (spoken.Count == 0)
        {
            return new SpeechStats(totalSeconds: totalSeconds, pauseSeconds: pauseSeconds);
        }

        var speech = Merged(spoken.Select(s => (s.Start, s.End)));
        var speechSeconds = Math.Min(totalSeconds, speech.Aggregate(0.0, (sum, interval) => sum + (interval.End - interval.Start)));
        var pauseCount = 0;
        double longestPause = 0;
        for (var index = 1; index < speech.Count; index++)
        {
            var gap = speech[index].Start - speech[index - 1].End;
            if (!(gap > pauseSeconds))
            {
                continue;
            }
            pauseCount++;
            longestPause = Math.Max(longestPause, gap);
        }

        return new SpeechStats(
            speakers: Speakers(spoken, meeting, pauseSeconds),
            totalSeconds: totalSeconds,
            speechSeconds: speechSeconds,
            speechShare: totalSeconds > 0 ? speechSeconds / totalSeconds : 0,
            pauseCount: pauseCount,
            longestPauseSeconds: longestPause,
            pauseSeconds: pauseSeconds);
    }

    // MARK: - Speakers

    static List<SpeakerSpeechStats> Speakers(List<Spoken> spoken, MeetingRecord meeting, double pauseSeconds)
    {
        var intervals = new Dictionary<string, List<(double Start, double End)>>(StringComparer.Ordinal);
        var words = new Dictionary<string, int>(StringComparer.Ordinal);
        var turns = new Dictionary<string, int>(StringComparer.Ordinal);
        // A turn runs until another voice speaks or its own speaker falls silent for longer
        // than the threshold; runEnd is how far the turn has reached so far.
        string? currentSpeaker = null;
        double runEnd = 0;
        foreach (var item in spoken)
        {
            if (currentSpeaker != item.SpeakerId || item.Start - runEnd > pauseSeconds)
            {
                turns[item.SpeakerId] = turns.GetValueOrDefault(item.SpeakerId) + 1;
                currentSpeaker = item.SpeakerId;
                runEnd = item.End;
            }
            else
            {
                runEnd = Math.Max(runEnd, item.End);
            }
            if (!intervals.TryGetValue(item.SpeakerId, out var list))
            {
                list = [];
                intervals[item.SpeakerId] = list;
            }
            list.Add((item.Start, item.End));
            words[item.SpeakerId] = words.GetValueOrDefault(item.SpeakerId) + item.Words;
        }

        var speaking = intervals.ToDictionary(p => p.Key, p => Merged(p.Value).Aggregate(0.0, (sum, i) => sum + (i.End - i.Start)), StringComparer.Ordinal);
        var totalSpeaking = speaking.Values.Aggregate(0.0, (sum, value) => sum + value);
        var order = meeting.EffectiveSpeakers.Select(s => s.Id).ToList();
        int OrderOf(string id)
        {
            var index = order.IndexOf(id);
            return index < 0 ? order.Count : index;
        }

        return speaking.Keys
            .Select(id =>
            {
                var seconds = speaking[id];
                var count = turns.GetValueOrDefault(id);
                var said = words.GetValueOrDefault(id);
                return new SpeakerSpeechStats(
                    speakerId: id,
                    speakingSeconds: seconds,
                    share: totalSpeaking > 0 ? seconds / totalSpeaking : 0,
                    turns: count,
                    averageTurnSeconds: count > 0 ? seconds / count : 0,
                    words: said,
                    wordsPerMinute: seconds >= MinimumPaceSeconds ? said / (seconds / 60) : null);
            })
            .OrderByDescending(s => s.SpeakingSeconds)
            .ThenBy(s => OrderOf(s.SpeakerId))
            .ThenBy(s => s.SpeakerId, StringComparer.Ordinal)
            .ToList();
    }

    // MARK: - Segments

    /// <summary>One transcribed segment reduced to what the numbers need.</summary>
    readonly record struct Spoken(int Offset, string SpeakerId, MeetingTrack Track, double Start, double End, int Words);

    /// <summary>
    /// The segments that carry speech, in the order they were said: by start, the microphone
    /// first on ties, as the transcript itself is ordered. Times are clamped, because
    /// meeting.json can have been edited by hand.
    /// </summary>
    static List<Spoken> Ordered(MeetingRecord meeting) =>
        meeting.Segments
            .Select((segment, offset) => (segment, offset, text: TranscriptMerger.NormalizedText(segment.Text)))
            .Where(p => p.text.Length > 0)
            .Select(p =>
            {
                var start = MeetingTime.Clamped(p.segment.Start);
                return new Spoken(p.offset, meeting.SpeakerIdFor(p.segment), MeetingTracks.FromSpeaker(p.segment.Speaker), start,
                                  Math.Max(start, MeetingTime.Clamped(p.segment.End)), WordCount(p.text));
            })
            .OrderBy(s => s.Start)
            .ThenBy(s => s.Track == MeetingTrack.Microphone ? 0 : 1)
            .ThenBy(s => s.Offset)
            .ToList();

    /// <summary>
    /// A word is a run of letters or digits; a hyphen or apostrophe between two of them stays
    /// inside it, so «кое-что» and "Let's" count once each and a dash standing between spaces
    /// counts for nothing. Counting whitespace-separated pieces instead would score Russian
    /// punctuation as words and make every pace too fast.
    /// </summary>
    public static int WordCount(string text)
    {
        var characters = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            characters.Add((string)enumerator.Current);
        }
        var count = 0;
        var index = 0;
        while (index < characters.Count)
        {
            if (!IsWordCharacter(characters[index]))
            {
                index++;
                continue;
            }
            index++;
            while (index < characters.Count)
            {
                if (IsWordCharacter(characters[index]))
                {
                    index++;
                }
                else if (characters[index] is "-" or "'" or "’" && index + 1 < characters.Count && IsWordCharacter(characters[index + 1]))
                {
                    index += 2;
                }
                else
                {
                    break;
                }
            }
            count++;
        }
        return count;
    }

    /// <summary>Swift's <c>Character.isLetter || Character.isNumber</c>, judged by the first scalar.</summary>
    static bool IsWordCharacter(string character)
    {
        var rune = Rune.GetRuneAt(character, 0);
        return Rune.IsLetter(rune) || Rune.IsNumber(rune);
    }

    /// <summary>
    /// The intervals sorted and merged, overlapping and touching ones becoming one. Segments of
    /// zero length carry words but no time, so they drop out here.
    /// </summary>
    internal static List<(double Start, double End)> Merged(IEnumerable<(double Start, double End)> intervals)
    {
        var result = new List<(double Start, double End)>();
        foreach (var interval in intervals.OrderBy(i => i.Start))
        {
            if (!(interval.End > interval.Start))
            {
                continue;
            }
            if (result.Count > 0 && interval.Start <= result[^1].End)
            {
                result[^1] = (result[^1].Start, Math.Max(result[^1].End, interval.End));
            }
            else
            {
                result.Add(interval);
            }
        }
        return result;
    }
}
