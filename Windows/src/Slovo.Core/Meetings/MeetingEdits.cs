using Slovo.Core.Model;
using Slovo.Core.Text;

namespace Slovo.Core.Meetings;

/// <summary>The outcome of <see cref="MeetingEdits.MarkHighlight"/>.</summary>
/// <param name="Created">False when a highlight already sat at that time with that note, so nothing changed.</param>
public readonly record struct HighlightMark(MeetingRecord Record, MeetingHighlight Highlight, bool Created);

/// <summary>The outcome of <see cref="MeetingEdits.UnmarkHighlight"/>.</summary>
public readonly record struct HighlightUnmark(MeetingRecord Record, bool Removed);

/// <summary>
/// The user's corrections to a meeting: names, "This is me", merges, line reassignment and
/// important moments. Each function returns the edited record, which the app saves with
/// <see cref="Platform.IMeetingStore.Update"/>. Pure (port of the macOS <c>MeetingEdits</c>).
///
/// Unknown ids leave the meeting unchanged: the screen can act on a record that changed
/// underneath it, and a no-op is safer than an alert about something the user cannot see.
/// </summary>
public static class MeetingEdits
{
    public const int MaximumNameCharacters = 40;
    public const int MaximumNoteCharacters = 500;

    /// <summary>An empty or null name resets the default label.</summary>
    public static MeetingRecord RenameSpeaker(string id, string? name, MeetingRecord meeting)
    {
        var newName = name is null ? null : SanitizedName(name);
        var speakers = meeting.Speakers.ToList();
        if (StoredIndex(id, meeting, speakers, addingFallback: newName is not null) is not { } index)
        {
            return meeting;
        }
        speakers[index] = speakers[index] with { DisplayName = newName };
        return meeting with { Speakers = speakers };
    }

    /// <summary>Clears <c>IsMe</c> on every other speaker.</summary>
    public static MeetingRecord MarkAsMe(string id, MeetingRecord meeting)
    {
        var speakers = meeting.Speakers.ToList();
        if (StoredIndex(id, meeting, speakers, addingFallback: true) is not { } index)
        {
            return meeting;
        }
        for (var other = 0; other < speakers.Count; other++)
        {
            speakers[other] = speakers[other] with { IsMe = other == index };
        }
        return meeting with { Speakers = speakers };
    }

    /// <summary>
    /// Moves the source's segments to the target and removes the source; only within one
    /// track, otherwise throws <c>meetings.error.mergeAcrossTracks</c> (SlovoException). The
    /// target keeps its name, color and number, and becomes «Я» when the source was.
    /// </summary>
    public static MeetingRecord MergeSpeaker(string source, string target, MeetingRecord meeting)
    {
        if (source == target
            || meeting.EffectiveSpeakers.FirstOrDefault(s => s.Id == source) is not { } sourceSpeaker
            || meeting.SpeakerWithId(target) is not { } targetSpeaker)
        {
            return meeting;
        }
        if (sourceSpeaker.Track != targetSpeaker.Track)
        {
            throw MeetingErrors.MergeAcrossTracks();
        }

        var storedTarget = SegmentSpeakerId(targetSpeaker);
        var segments = meeting.Segments
            .Select(segment => meeting.SpeakerIdFor(segment) == source ? segment with { SpeakerId = storedTarget } : segment)
            .ToList();
        var edited = meeting with { Segments = segments, Speakers = meeting.Speakers.Where(s => s.Id != source).ToList() };
        // The source's number stays used, so a later run never gives it to another voice and
        // references to it in an exported transcript keep meaning this person.
        if (sourceSpeaker.Number is not null)
        {
            edited = edited with { HighestSpeakerNumber = SpeakerRegistry.HighestNumber(meeting, []) };
        }
        if (sourceSpeaker.IsMe)
        {
            var speakers = edited.Speakers.ToList();
            if (StoredIndex(target, edited, speakers, addingFallback: true) is { } index)
            {
                speakers[index] = speakers[index] with { IsMe = true };
                edited = edited with { Speakers = speakers };
            }
        }
        return edited;
    }

    /// <summary>Only within the line's track, otherwise throws <c>meetings.error.mergeAcrossTracks</c> (SlovoException).</summary>
    public static MeetingRecord ReassignLine(TranscriptLine line, string speakerId, MeetingRecord meeting)
    {
        if (meeting.SpeakerWithId(speakerId) is not { } target)
        {
            return meeting;
        }
        if (target.Track != line.Track)
        {
            throw MeetingErrors.MergeAcrossTracks();
        }
        var stored = SegmentSpeakerId(target);
        var segments = meeting.Segments.ToList();
        foreach (var index in line.SegmentIndices)
        {
            if (index < 0 || index >= meeting.Segments.Count
                || MeetingTracks.FromSpeaker(meeting.Segments[index].Speaker) != line.Track)
            {
                continue;
            }
            segments[index] = segments[index] with { SpeakerId = stored };
        }
        return meeting with { Segments = segments };
    }

    /// <summary>Removes every highlight anchored to the line, or adds a Transcript one at its start.</summary>
    public static MeetingRecord ToggleHighlight(TranscriptLine line, MeetingRecord meeting, DateTimeOffset now)
    {
        // The line may come from a filtered list or an older render; anchors are looked up
        // again on the meeting as it is now.
        var current = TranscriptMerger.Lines(meeting).FirstOrDefault(l => l.SegmentIndices.SequenceEqual(line.SegmentIndices));
        var existing = meeting.Highlights.Select(h => h.Id).ToHashSet();
        var anchored = (current?.HighlightIds ?? line.HighlightIds).Where(existing.Contains).ToHashSet();

        if (anchored.Count == 0)
        {
            return meeting with
            {
                Highlights = [.. meeting.Highlights, new MeetingHighlight { Time = line.Start, Source = HighlightSource.Transcript, CreatedAt = now }],
            };
        }
        return meeting with { Highlights = meeting.Highlights.Where(h => !anchored.Contains(h.Id)).ToList() };
    }

    public static MeetingRecord SetHighlightNote(Guid id, string? note, MeetingRecord meeting)
    {
        var index = IndexOf(meeting.Highlights, id);
        if (index < 0)
        {
            return meeting;
        }
        var highlights = meeting.Highlights.ToList();
        highlights[index] = highlights[index] with { Note = SanitizedNote(note) };
        return meeting with { Highlights = highlights };
    }

    public static MeetingRecord DeleteHighlight(Guid id, MeetingRecord meeting) =>
        meeting with { Highlights = meeting.Highlights.Where(h => h.Id != id).ToList() };

    /// <summary>
    /// Adds a highlight at <paramref name="time"/>, marked as if a transcript line were
    /// starred rather than marked live while recording. A highlight already sitting at that
    /// exact time with the same note is reused instead of duplicated, so marking the same
    /// moment twice changes nothing.
    /// </summary>
    public static HighlightMark MarkHighlight(double time, string? note, MeetingRecord meeting, DateTimeOffset now)
    {
        var clampedTime = MeetingTime.Clamped(time);
        var cleanNote = SanitizedNote(note);
        if (meeting.Highlights.FirstOrDefault(h => h.Time == clampedTime && h.Note == cleanNote) is { } existing)
        {
            return new HighlightMark(meeting, existing, false);
        }
        var highlight = new MeetingHighlight { Time = clampedTime, Note = cleanNote, Source = HighlightSource.Transcript, CreatedAt = now };
        return new HighlightMark(meeting with { Highlights = [.. meeting.Highlights, highlight] }, highlight, true);
    }

    /// <summary>Removes the highlight with this id; Removed is false, and the meeting unchanged, when none has it.</summary>
    public static HighlightUnmark UnmarkHighlight(Guid id, MeetingRecord meeting)
    {
        if (IndexOf(meeting.Highlights, id) < 0)
        {
            return new HighlightUnmark(meeting, false);
        }
        return new HighlightUnmark(DeleteHighlight(id, meeting), true);
    }

    /// <summary>
    /// Trimmed, at most 40 characters, without line breaks, ":", "[", "]" or "★"; null when
    /// nothing is left. Those characters would break the <c>[mm:ss] ★ Name: text</c> lines of
    /// <c>transcript.md</c> that the recovery parser reads back.
    /// </summary>
    public static string? SanitizedName(string name)
    {
        var kept = new System.Text.StringBuilder(name.Length);
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(name);
        while (enumerator.MoveNext())
        {
            var character = (string)enumerator.Current;
            if (IsNewline(character))
            {
                kept.Append(' ');
            }
            else if (character is not (":" or "[" or "]" or "★"))
            {
                kept.Append(character);
            }
        }
        return Limited(TranscriptMerger.NormalizedText(kept.ToString()), MaximumNameCharacters);
    }

    /// <summary>One line of at most 500 characters; null when empty.</summary>
    public static string? SanitizedNote(string? note) =>
        note is null ? null : Limited(TranscriptMerger.NormalizedText(note), MaximumNoteCharacters);

    // MARK: - Private

    static string? Limited(string text, int characters)
    {
        var result = TextElements.TrimSpaces(TextElements.Prefix(text, characters));
        return result.Length == 0 ? null : result;
    }

    /// <summary>Swift's <c>Character.isNewline</c>: a grapheme whose first scalar is a line break.</summary>
    static bool IsNewline(string character)
    {
        var first = System.Text.Rune.GetRuneAt(character, 0).Value;
        return first is 0x0A or 0x0B or 0x0C or 0x0D or 0x85 or 0x2028 or 0x2029;
    }

    static int IndexOf(IReadOnlyList<MeetingHighlight> highlights, Guid id)
    {
        for (var index = 0; index < highlights.Count; index++)
        {
            if (highlights[index].Id == id)
            {
                return index;
            }
        }
        return -1;
    }

    /// <summary>
    /// The index of a stored speaker in <paramref name="speakers"/> (a copy of the meeting's).
    /// A fallback speaker the transcript shows is appended first when
    /// <paramref name="addingFallback"/>, so its name or «Я» has somewhere to live.
    /// </summary>
    static int? StoredIndex(string id, MeetingRecord meeting, List<MeetingSpeaker> speakers, bool addingFallback)
    {
        var index = speakers.FindIndex(s => s.Id == id);
        if (index >= 0)
        {
            return index;
        }
        if (!addingFallback
            || MeetingTracks.All.Select(MeetingSpeaker.Fallback).FirstOrDefault(s => s.Id == id) is not { } fallback
            || !meeting.EffectiveSpeakers.Any(s => s.Id == id))
        {
            return null;
        }
        speakers.Add(fallback);
        return speakers.Count - 1;
    }

    /// <summary>
    /// Segments name a fallback speaker by leaving <c>speakerID</c> empty, which keeps them
    /// readable by builds that know nothing about speakers.
    /// </summary>
    static string? SegmentSpeakerId(MeetingSpeaker speaker) =>
        speaker.Id == MeetingSpeaker.Fallback(speaker.Track).Id ? null : speaker.Id;
}
