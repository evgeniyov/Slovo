using Slovo.Core.Json;
using Slovo.Core.Localization;
using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// <see cref="MeetingEdits"/>: names, «Это я», merges, line reassignment, stars, notes and
/// deletion (port of the macOS <c>MeetingsEditsTests</c>).
/// </summary>
public class MeetingEditsTests
{
    // MARK: - Names

    [Fact]
    public void RenameTrimsResetsAndIgnoresUnknownSpeakers()
    {
        var meeting = Fixture();
        var renamed = MeetingEdits.RenameSpeaker("s2", "  Аня  ", meeting);
        Assert.Equal("Аня", renamed.SpeakerWithId("s2")?.DisplayName);
        Assert.Equal(3, renamed.Speakers.Count);
        Assert.Equal(2, renamed.SpeakerWithId("s2")?.Number);
        Assert.Equal(1, renamed.SpeakerWithId("s2")?.ColorIndex);

        Assert.Null(MeetingEdits.RenameSpeaker("s2", "", renamed).SpeakerWithId("s2")?.DisplayName);
        Assert.Null(MeetingEdits.RenameSpeaker("s2", null, renamed).SpeakerWithId("s2")?.DisplayName);
        Assert.Null(MeetingEdits.RenameSpeaker("s2", " :[]★ ", renamed).SpeakerWithId("s2")?.DisplayName);
        Assert.Equal(meeting, MeetingEdits.RenameSpeaker("s9", "Никто", meeting));
    }

    [Fact]
    public void SanitizedNameRemovesWhatWouldBreakTranscriptLines()
    {
        Assert.Equal("Аня PM Иванова", MeetingEdits.SanitizedName("Аня: [PM]\n★ Иванова"));
        Assert.Equal("Анна Мария", MeetingEdits.SanitizedName("  Анна   Мария  "));
        Assert.Equal(new string('я', 40), MeetingEdits.SanitizedName(new string('я', 50)));
        Assert.Equal(new string('a', 39), MeetingEdits.SanitizedName(new string('a', 39) + " bcd"));
        Assert.Null(MeetingEdits.SanitizedName("   "));
        Assert.Null(MeetingEdits.SanitizedName(":[]★\r\n"));
    }

    [Fact]
    public void RenamingAFallbackSpeakerStoresItFirst()
    {
        var call = new MeetingRecord
        {
            Title = "Звонок",
            Status = MeetingStatus.Transcribed,
            HasSystemAudio = true,
            Segments =
            [
                Segment(0, 3, Speaker.Me, "Алло"),
                Segment(4, 6, Speaker.Others, "Слышу"),
            ],
        };

        var renamed = MeetingEdits.RenameSpeaker("others", "Команда дизайна", call);

        Assert.Equal([new MeetingSpeaker { Id = "others", Track = MeetingTrack.System, DisplayName = "Команда дизайна", ColorIndex = 1 }],
                     renamed.Speakers);
        Assert.Equal(["me", "others"], renamed.EffectiveSpeakers.Select(s => s.Id));
        Assert.Equal(call, MeetingEdits.RenameSpeaker("others", null, call));

        var microphoneOnly = new MeetingRecord
        {
            Title = "Заметка",
            Status = MeetingStatus.Transcribed,
            Segments = [Segment(0, 3, Speaker.Me, "Один")],
        };
        Assert.Equal(microphoneOnly, MeetingEdits.RenameSpeaker("others", "Кто-то", microphoneOnly));
    }

    [Fact]
    public void MarkAsMeKeepsOneMe()
    {
        var first = MeetingEdits.MarkAsMe("s1", Fixture());
        Assert.Equal([true, false, false], first.Speakers.Select(s => s.IsMe));
        var second = MeetingEdits.MarkAsMe("s3", first);
        Assert.Equal([false, false, true], second.Speakers.Select(s => s.IsMe));
        Assert.Equal(second, MeetingEdits.MarkAsMe("s9", second));

        var withFallback = second with { Segments = [.. second.Segments, Segment(30, 32, Speaker.Me, "Без спикера")] };
        var fallbackMe = MeetingEdits.MarkAsMe("me", withFallback);
        Assert.Equal(["s1", "s2", "s3", "me"], fallbackMe.Speakers.Select(s => s.Id));
        Assert.Equal([false, false, false, true], fallbackMe.Speakers.Select(s => s.IsMe));
    }

    // MARK: - Merge and reassign

    [Fact]
    public void MergeMovesSegmentsWithinATrackAndRefusesAcrossTracks()
    {
        var meeting = Fixture();
        var named = MeetingEdits.RenameSpeaker("s1", "Аня", meeting);

        var merged = MeetingEdits.MergeSpeaker("s2", "s1", named);

        Assert.Equal(["s1", "s1", "s3", "s1"], merged.Segments.Select(s => s.SpeakerId));
        Assert.Equal(["s1", "s3"], merged.Speakers.Select(s => s.Id));
        Assert.Equal("Аня", merged.SpeakerWithId("s1")?.DisplayName);
        Assert.Equal(1, merged.SpeakerWithId("s1")?.Number);
        Assert.Equal(0, merged.SpeakerWithId("s1")?.ColorIndex);
        Assert.Equal(meeting.Segments.Select(s => s.Text), merged.Segments.Select(s => s.Text));
        Assert.Equal(["Привет Начнём", "Слышно?", "Да"], TranscriptMerger.Lines(merged).Select(l => l.Text));

        var error = Assert.Throws<SlovoException>(() => MeetingEdits.MergeSpeaker("s3", "s1", meeting));
        Assert.Equal(CoreStrings.Get("meetings.error.mergeAcrossTracks"), error.Message);
        Assert.Equal(meeting, MeetingEdits.MergeSpeaker("s1", "s1", meeting));
        Assert.Equal(meeting, MeetingEdits.MergeSpeaker("s9", "s1", meeting));
    }

    [Fact]
    public void MergeCarriesMeAndWorksWithFallbackSpeakers()
    {
        var meWasSecond = MeetingEdits.MarkAsMe("s2", Fixture());
        var merged = MeetingEdits.MergeSpeaker("s2", "s1", meWasSecond);
        Assert.Equal(["s1", "s3"], merged.Speakers.Select(s => s.Id));
        Assert.Equal([true, false], merged.Speakers.Select(s => s.IsMe));

        var mixed = new MeetingRecord
        {
            Title = "Коворк",
            Status = MeetingStatus.Transcribed,
            Segments =
            [
                Segment(0, 5, Speaker.Me, "Без спикера"),
                Segment(6, 9, Speaker.Me, "Первый", "s1"),
            ],
            Speakers = [new MeetingSpeaker { Id = "s1", Track = MeetingTrack.Microphone, Number = 1, ColorIndex = 0 }],
        };

        var intoNumbered = MeetingEdits.MergeSpeaker("me", "s1", mixed);
        Assert.Equal(["s1", "s1"], intoNumbered.Segments.Select(s => s.SpeakerId));
        Assert.Equal(["s1"], intoNumbered.EffectiveSpeakers.Select(s => s.Id));

        var intoFallback = MeetingEdits.MergeSpeaker("s1", "me", mixed);
        Assert.Equal([null, null], intoFallback.Segments.Select(s => s.SpeakerId));
        Assert.Empty(intoFallback.Speakers);
        Assert.Equal(["me"], intoFallback.EffectiveSpeakers.Select(s => s.Id));
    }

    [Fact]
    public void ReassignLineOnlyWithinItsTrack()
    {
        var meeting = Fixture();
        var lines = TranscriptMerger.Lines(meeting);
        Assert.Equal(4, lines.Count);

        var reassigned = MeetingEdits.ReassignLine(lines[1], "s1", meeting);
        Assert.Equal(["s1", "s1", "s3", "s1"], reassigned.Segments.Select(s => s.SpeakerId));
        Assert.Equal(meeting.Speakers, reassigned.Speakers);

        var error = Assert.Throws<SlovoException>(() => MeetingEdits.ReassignLine(lines[1], "s3", meeting));
        Assert.Equal(CoreStrings.Get("meetings.error.mergeAcrossTracks"), error.Message);

        var toFallback = MeetingEdits.ReassignLine(lines[2], "others", meeting);
        Assert.Equal(["s1", "s2", null, "s1"], toFallback.Segments.Select(s => s.SpeakerId));
        Assert.Equal(["s1", "s2", "s3", "others"], toFallback.EffectiveSpeakers.Select(s => s.Id));
        Assert.Equal(meeting, MeetingEdits.ReassignLine(lines[1], "s9", meeting));
    }

    // MARK: - Moments

    [Fact]
    public void ToggleHighlightAddsAtTheLineStartAndRemovesEveryAnchoredMoment()
    {
        var meeting = Fixture();
        var now = AppleDate.FromReferenceSeconds(779_100_000);
        var lines = TranscriptMerger.Lines(meeting);

        var starred = MeetingEdits.ToggleHighlight(lines[1], meeting, now);

        var star = starred.Highlights.FirstOrDefault();
        Assert.NotNull(star);
        Assert.Single(starred.Highlights);
        Assert.Equal(6, star.Time);
        Assert.Equal(HighlightSource.Transcript, star.Source);
        Assert.Equal(now, star.CreatedAt);
        Assert.Null(star.Note);
        var starredLines = TranscriptMerger.Lines(starred);
        Assert.Equal([[], [star.Id], [], []], starredLines.Select(l => l.HighlightIds.ToArray()));
        // A line rendered before the star was added still finds it.
        Assert.Empty(MeetingEdits.ToggleHighlight(lines[1], starred, now).Highlights);
        Assert.Empty(MeetingEdits.ToggleHighlight(starredLines[1], starred, now).Highlights);

        var during = new MeetingHighlight { Time = 7, Source = HighlightSource.Recording, CreatedAt = now };
        var later = new MeetingHighlight { Time = 9.5, Note = "важно", Source = HighlightSource.Recording, CreatedAt = now };
        var elsewhere = new MeetingHighlight { Time = 12, Source = HighlightSource.Recording, CreatedAt = now };
        var marked = meeting with { Highlights = [during, elsewhere, later] };
        var cleared = MeetingEdits.ToggleHighlight(TranscriptMerger.Lines(marked)[1], marked, now);
        Assert.Equal([elsewhere], cleared.Highlights);
    }

    [Fact]
    public void NotesAreOneLineOfAtMostFiveHundredCharacters()
    {
        var moment = new MeetingHighlight { Time = 7, Source = HighlightSource.Recording };
        var meeting = Fixture() with { Highlights = [moment] };

        var noted = MeetingEdits.SetHighlightNote(moment.Id, "  важно\nочень  ", meeting);
        Assert.Equal("важно очень", noted.Highlights.FirstOrDefault()?.Note);
        Assert.Null(MeetingEdits.SetHighlightNote(moment.Id, " \n ", noted).Highlights.FirstOrDefault()?.Note);
        Assert.Null(MeetingEdits.SetHighlightNote(moment.Id, null, noted).Highlights.FirstOrDefault()?.Note);
        var @long = MeetingEdits.SetHighlightNote(moment.Id, new string('ж', 600), meeting);
        Assert.Equal(500, @long.Highlights.FirstOrDefault()?.Note?.Length);
        Assert.Equal(meeting, MeetingEdits.SetHighlightNote(Guid.NewGuid(), "x", meeting));
    }

    [Fact]
    public void DeleteHighlight()
    {
        var first = new MeetingHighlight { Time = 1, Source = HighlightSource.Transcript };
        var second = new MeetingHighlight { Time = 12, Source = HighlightSource.Recording };
        var meeting = Fixture() with { Highlights = [first, second] };

        Assert.Equal([second], MeetingEdits.DeleteHighlight(first.Id, meeting).Highlights);
        Assert.Equal(meeting, MeetingEdits.DeleteHighlight(Guid.NewGuid(), meeting));
    }

    // MARK: - Fixture

    static TranscriptSegment Segment(double start, double end, Speaker speaker, string text, string? speakerId = null) =>
        new() { Start = start, End = end, Speaker = speaker, Text = text, SpeakerId = speakerId };

    /// <summary>Two microphone voices and one remote voice: s1 0–5, s2 6–10, s3 11–15, s1 16–20.</summary>
    static MeetingRecord Fixture() => new()
    {
        Title = "Созвон",
        CreatedAt = AppleDate.FromReferenceSeconds(779_000_000),
        Status = MeetingStatus.Transcribed,
        HasSystemAudio = true,
        HasAudio = true,
        EngineId = EngineId.Parakeet,
        Segments =
        [
            Segment(0, 5, Speaker.Me, "Привет", "s1"),
            Segment(6, 10, Speaker.Me, "Начнём", "s2"),
            Segment(11, 15, Speaker.Others, "Слышно?", "s3"),
            Segment(16, 20, Speaker.Me, "Да", "s1"),
        ],
        Speakers =
        [
            new MeetingSpeaker { Id = "s1", Track = MeetingTrack.Microphone, Number = 1, ColorIndex = 0 },
            new MeetingSpeaker { Id = "s2", Track = MeetingTrack.Microphone, Number = 2, ColorIndex = 1 },
            new MeetingSpeaker { Id = "s3", Track = MeetingTrack.System, Number = 3, ColorIndex = 2 },
        ],
    };
}
