using Slovo.Core.Json;
using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// <see cref="TranscriptMerger"/>: the first format's merging, timestamps and document, display
/// lines with speakers, the speaker filter, highlight anchoring and labels (port of the macOS
/// <c>MeetingsMergerTests</c>).
/// </summary>
public class TranscriptMergerTests
{
    /// <summary>A fixed zone for the date line, so documents do not depend on the computer's.</summary>
    static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+03", TimeSpan.FromHours(3), "UTC+03", "UTC+03");

    [Fact]
    public void MergeOrdersByStartAndCollapsesConsecutiveSegmentsOfOneSpeaker()
    {
        var microphone = new[]
        {
            Segment(0.8, 3, Speaker.Me, "Hi everyone"),
            Segment(3.5, 6, Speaker.Me, "let's start"),
            Segment(20, 22, Speaker.Me, "Sounds good"),
        };
        var system = new[]
        {
            Segment(12, 15, Speaker.Others, "Hello"),
            Segment(7, 11, Speaker.Others, "Morning"),
            Segment(30, 31, Speaker.Others, "Bye"),
        };

        var merged = TranscriptMerger.Merge(microphone, system);

        Assert.Equal(
        [
            Segment(0.8, 6, Speaker.Me, "Hi everyone let's start"),
            Segment(7, 15, Speaker.Others, "Morning Hello"),
            Segment(20, 22, Speaker.Me, "Sounds good"),
            Segment(30, 31, Speaker.Others, "Bye"),
        ], merged);
    }

    [Fact]
    public void MergeAssignsSpeakersByTrackDropsEmptyTextAndPutsMicrophoneFirstOnTies()
    {
        var microphone = new[]
        {
            Segment(5, 6, Speaker.Others, "  mine\nreally "),
            Segment(8, 9, Speaker.Me, "   "),
        };
        var system = new[] { Segment(5, 7, Speaker.Me, "theirs") };

        var merged = TranscriptMerger.Merge(microphone, system);

        Assert.Equal([Speaker.Me, Speaker.Others], merged.Select(s => s.Speaker));
        Assert.Equal(["mine really", "theirs"], merged.Select(s => s.Text));
        Assert.Empty(TranscriptMerger.Merge([], []));
    }

    [Fact]
    public void MergeKeepsTheLatestEndWhenCollapsingOverlaps()
    {
        var microphone = new[]
        {
            Segment(0, 10, Speaker.Me, "long"),
            Segment(2, 4, Speaker.Me, "short"),
        };
        var merged = TranscriptMerger.Merge(microphone, []);
        Assert.Equal([Segment(0, 10, Speaker.Me, "long short")], merged);
    }

    [Fact]
    public void Timestamp()
    {
        Assert.Equal("00:00", TranscriptMerger.Timestamp(0));
        Assert.Equal("00:05", TranscriptMerger.Timestamp(5.99));
        Assert.Equal("01:05", TranscriptMerger.Timestamp(65.9));
        Assert.Equal("59:59", TranscriptMerger.Timestamp(3599.9));
        Assert.Equal("1:00:00", TranscriptMerger.Timestamp(3600));
        Assert.Equal("1:02:03", TranscriptMerger.Timestamp(3723));
        Assert.Equal("10:00:00", TranscriptMerger.Timestamp(36_000));
        Assert.Equal("00:00", TranscriptMerger.Timestamp(-5));
        Assert.Equal("00:00", TranscriptMerger.Timestamp(double.NaN));
        Assert.Equal("00:00", TranscriptMerger.Timestamp(double.PositiveInfinity));
        // Beyond Int, as a hand-edited meeting.json can hold: clamped instead of trapping.
        Assert.Equal("99999:59:59", TranscriptMerger.Timestamp(1e20));
    }

    [Fact]
    public void MarkdownInEnglish()
    {
        var segments = new[]
        {
            Segment(5, 8, Speaker.Me, "Hello there"),
            Segment(3725, 3730, Speaker.Others, "Hi"),
        };
        var document = TranscriptMerger.Markdown("Weekly\nsync", DateTimeOffset.FromUnixTimeSeconds(1_789_000_000),
                                                 segments, AppLanguage.En, Zone);
        var lines = document.Split('\n');

        Assert.Equal("# Weekly sync", lines.FirstOrDefault());
        Assert.Contains("[00:05] Me: Hello there\n\n[1:02:05] Others: Hi\n", document, StringComparison.Ordinal);
        Assert.Contains("2026", document, StringComparison.Ordinal);
        Assert.EndsWith("Others: Hi\n", document, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownInRussianUsesRussianSpeakerNames()
    {
        var segments = new[]
        {
            Segment(0, 2, Speaker.Me, "Привет"),
            Segment(61, 63, Speaker.Others, "Здравствуйте"),
        };
        var document = TranscriptMerger.Markdown("Планёрка", DateTimeOffset.UtcNow, segments, AppLanguage.Ru, Zone);
        Assert.StartsWith("# Планёрка\n\n", document, StringComparison.Ordinal);
        Assert.Contains("[00:00] Я: Привет\n\n[01:01] Собеседники: Здравствуйте\n", document, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownWithoutSpeechSaysSo()
    {
        var english = TranscriptMerger.Markdown("Empty", DateTimeOffset.UtcNow, [], AppLanguage.En, Zone);
        Assert.EndsWith("No speech was recognized.\n", english, StringComparison.Ordinal);
        var russian = TranscriptMerger.Markdown("Пусто", DateTimeOffset.UtcNow, [], AppLanguage.Ru, Zone);
        Assert.EndsWith("Речь не распознана.\n", russian, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextHasOneLinePerSegment()
    {
        var segments = new[]
        {
            Segment(1, 2, Speaker.Me, "One"),
            Segment(3, 4, Speaker.Others, "Two"),
        };
        Assert.Equal("[00:01] Me: One\n[00:03] Others: Two", TranscriptMerger.PlainText(segments, AppLanguage.En));
    }

    // MARK: - Display lines

    [Fact]
    public void LinesJoinOneSpeakerAcrossGapsOfAtMostThreeSeconds()
    {
        var record = Meeting(
        [
            Segment(0, 2, Speaker.Me, "a", "s1"),
            Segment(5, 7, Speaker.Me, " b ", "s1"),
            Segment(10.5, 12, Speaker.Me, "c", "s1"),
        ], speakers: [Speaker1]);

        var lines = TranscriptMerger.Lines(record);

        Assert.Equal(
        [
            new TranscriptLine { Id = 0, Start = 0, End = 7, SpeakerId = "s1", Track = MeetingTrack.Microphone, Text = "a b", SegmentIndices = [0, 1] },
            new TranscriptLine { Id = 1, Start = 10.5, End = 12, SpeakerId = "s1", Track = MeetingTrack.Microphone, Text = "c", SegmentIndices = [2] },
        ], lines);
    }

    [Fact]
    public void LinesStayAtMostSixtySecondsLong()
    {
        var record = Meeting(
        [
            Segment(0, 20, Speaker.Me, "a", "s1"),
            Segment(20, 40, Speaker.Me, "b", "s1"),
            Segment(40, 60, Speaker.Me, "c", "s1"),
            Segment(60, 61, Speaker.Me, "d", "s1"),
        ], speakers: [Speaker1]);

        var lines = TranscriptMerger.Lines(record);

        Assert.Equal(["a b c", "d"], lines.Select(l => l.Text));
        Assert.Equal([[0, 1, 2], [3]], lines.Select(l => l.SegmentIndices.ToArray()));
        Assert.Equal(60, lines.FirstOrDefault()?.End);
    }

    [Fact]
    public void LinesAreSortedWithMicrophoneFirstOnTiesAndSkipEmptyText()
    {
        var record = Meeting(
        [
            Segment(5, 6, Speaker.Others, "theirs"),
            Segment(5, 7, Speaker.Me, "mine", "s1"),
            Segment(0, 1, Speaker.Me, "first", "s1"),
            Segment(2, 3, Speaker.Me, "  ", "s1"),
        ], speakers: [Speaker1]);

        var lines = TranscriptMerger.Lines(record);

        Assert.Equal(["first", "mine", "theirs"], lines.Select(l => l.Text));
        Assert.Equal([0, 1, 2], lines.Select(l => l.Id));
        Assert.Equal(["s1", "s1", "others"], lines.Select(l => l.SpeakerId));
        Assert.Equal([MeetingTrack.Microphone, MeetingTrack.Microphone, MeetingTrack.System], lines.Select(l => l.Track));
        Assert.Equal([[2], [1], [0]], lines.Select(l => l.SegmentIndices.ToArray()));
    }

    [Fact]
    public void SpeakerFilterAppliesAfterMerging()
    {
        var record = Meeting(
        [
            Segment(0, 2, Speaker.Me, "a", "s1"),
            Segment(2.5, 3, Speaker.Me, "b", "s2"),
            Segment(3.5, 5, Speaker.Me, "c", "s1"),
        ], speakers: [Speaker1, Speaker2]);

        Assert.Equal(["a", "b", "c"], TranscriptMerger.Lines(record).Select(l => l.Text));
        var filtered = TranscriptMerger.Lines(record, new HashSet<string> { "s1" });
        Assert.Equal(["a", "c"], filtered.Select(l => l.Text));
        Assert.Equal([0, 1], filtered.Select(l => l.Id));
        Assert.Empty(TranscriptMerger.Lines(record, new HashSet<string>()));
    }

    // MARK: - Highlights

    [Fact]
    public void HighlightAnchorsToTheContainingLineThenLooksBackTenThenAheadFiveSeconds()
    {
        TranscriptLine[] lines =
        [
            Line(0, 0, 10),
            Line(1, 20, 30),
            Line(2, 50, 60),
        ];
        Assert.Equal(0, TranscriptMerger.HighlightLineIndex(Moment(5), lines));
        Assert.Equal(1, TranscriptMerger.HighlightLineIndex(Moment(20), lines));
        Assert.Equal(1, TranscriptMerger.HighlightLineIndex(Moment(35), lines));
        Assert.Equal(1, TranscriptMerger.HighlightLineIndex(Moment(40), lines));
        Assert.Equal(2, TranscriptMerger.HighlightLineIndex(Moment(45), lines));
        Assert.Null(TranscriptMerger.HighlightLineIndex(Moment(44), lines));
        Assert.Null(TranscriptMerger.HighlightLineIndex(Moment(71), lines));
        Assert.Null(TranscriptMerger.HighlightLineIndex(Moment(double.NaN), lines));
        Assert.Null(TranscriptMerger.HighlightLineIndex(Moment(5), []));
    }

    [Fact]
    public void HighlightInOverlappingLinesGoesToTheLineThatStartedLast()
    {
        TranscriptLine[] lines =
        [
            Line(0, 0, 10),
            new TranscriptLine { Id = 1, Start = 5, End = 7, SpeakerId = "others", Track = MeetingTrack.System, Text = "…", SegmentIndices = [1] },
        ];
        Assert.Equal(1, TranscriptMerger.HighlightLineIndex(Moment(6), lines));
        Assert.Equal(0, TranscriptMerger.HighlightLineIndex(Moment(8), lines));
    }

    [Fact]
    public void LinesCarryTheirHighlightsAndTheFilterDoesNotMoveThem()
    {
        var early = Moment(1);
        var late = Moment(3.2);
        var lost = Moment(100);
        var record = Meeting(
        [
            Segment(0, 2, Speaker.Me, "a", "s1"),
            Segment(3, 4, Speaker.Me, "b", "s2"),
        ], speakers: [Speaker1, Speaker2], highlights: [late, lost, early]);

        var lines = TranscriptMerger.Lines(record);
        Assert.Equal([[early.Id], [late.Id]], lines.Select(l => l.HighlightIds.ToArray()));
        Assert.Equal([[late.Id]], TranscriptMerger.Lines(record, new HashSet<string> { "s2" }).Select(l => l.HighlightIds.ToArray()));
    }

    [Fact]
    public void PlainTextOfAMeetingShowsNamesAndStars()
    {
        var anya = Speaker1 with { DisplayName = "Аня" };
        var record = Meeting(
        [
            Segment(0, 2, Speaker.Me, "a", "s1"),
            Segment(10, 12, Speaker.Me, "b", "s2"),
        ], speakers: [anya, Speaker2], highlights: [Moment(1)]);

        Assert.Equal("[00:00] ★ Аня: a\n[00:10] Спикер 2: b", TranscriptMerger.PlainText(record, AppLanguage.Ru));
        Assert.Equal("[00:10] Speaker 2: b", TranscriptMerger.PlainText(record, AppLanguage.En, new HashSet<string> { "s2" }));
    }

    [Fact]
    public void PlainTextOfAFirstFormatMeetingMatchesTheSegmentForm()
    {
        var segments = new[]
        {
            Segment(1, 2, Speaker.Me, "One"),
            Segment(3, 4, Speaker.Others, "Two"),
        };
        var record = Meeting(segments);
        Assert.Equal(TranscriptMerger.PlainText(segments, AppLanguage.En), TranscriptMerger.PlainText(record, AppLanguage.En));
    }

    // MARK: - Labels

    [Fact]
    public void SpeakerLabelsInEnglishAndRussian()
    {
        var numbered = new MeetingSpeaker { Id = "s2", Track = MeetingTrack.System, Number = 2, ColorIndex = 1 };
        Assert.Equal("Speaker 2", TranscriptMerger.SpeakerLabel(numbered, AppLanguage.En));
        Assert.Equal("Спикер 2", TranscriptMerger.SpeakerLabel(numbered, AppLanguage.Ru));

        var me = numbered with { IsMe = true };
        Assert.Equal("Me", TranscriptMerger.SpeakerLabel(me, AppLanguage.En));
        Assert.Equal("Я", TranscriptMerger.SpeakerLabel(me, AppLanguage.Ru));

        var named = me with { DisplayName = "Аня" };
        Assert.Equal("Аня", TranscriptMerger.SpeakerLabel(named, AppLanguage.En));
        Assert.Equal("Аня", TranscriptMerger.SpeakerLabel(named, AppLanguage.Ru));

        var blank = MeetingSpeaker.Fallback(MeetingTrack.System) with { DisplayName = "   " };
        Assert.Equal("Others", TranscriptMerger.SpeakerLabel(blank, AppLanguage.En));
        Assert.Equal("Собеседники", TranscriptMerger.SpeakerLabel(blank, AppLanguage.Ru));
        Assert.Equal("Me", TranscriptMerger.SpeakerLabel(MeetingSpeaker.Fallback(MeetingTrack.Microphone), AppLanguage.En));
    }

    [Fact]
    public void MicrophoneFallbackIsMeUntilAnotherSpeakerIsMarked()
    {
        var call = new MeetingRecord
        {
            Title = "Звонок",
            CreatedAt = AppleDate.FromReferenceSeconds(779_000_000),
            Status = MeetingStatus.Transcribed,
            HasSystemAudio = true,
            Segments =
            [
                Segment(0, 3, Speaker.Me, "Алло"),
                Segment(4, 6, Speaker.Others, "Слышу"),
            ],
        };
        var microphone = MeetingSpeaker.Fallback(MeetingTrack.Microphone);
        Assert.True(TranscriptMerger.IsMe(microphone, call));
        Assert.False(TranscriptMerger.IsMe(MeetingSpeaker.Fallback(MeetingTrack.System), call));
        Assert.Equal(["Я", "Собеседники"], call.EffectiveSpeakers.Select(s => TranscriptMerger.SpeakerLabel(s, call, AppLanguage.Ru)));

        // «Это я» on «Собеседники» leaves one «Я».
        var marked = MeetingEdits.MarkAsMe("others", call);
        Assert.Equal(["me", "others"], marked.EffectiveSpeakers.Select(s => s.Id));
        Assert.Equal([false, true], marked.EffectiveSpeakers.Select(s => TranscriptMerger.IsMe(s, marked)));
        Assert.Equal(["Микрофон", "Я"], marked.EffectiveSpeakers.Select(s => TranscriptMerger.SpeakerLabel(s, marked, AppLanguage.Ru)));
        Assert.Equal(["Microphone", "Me"], marked.EffectiveSpeakers.Select(s => TranscriptMerger.SpeakerLabel(s, marked, AppLanguage.En)));
        Assert.Equal("[00:00] Microphone: Алло\n[00:04] Me: Слышу", TranscriptMerger.PlainText(marked, AppLanguage.En));
        Assert.Contains("Participants: Microphone, Me", TranscriptMerger.Markdown(marked, AppLanguage.En, timeZone: Zone), StringComparison.Ordinal);

        // A name still wins, and a stored microphone speaker follows the same rule.
        var named = MeetingEdits.RenameSpeaker("me", "Переговорка", marked);
        var namedMicrophone = named.SpeakerWithId("me");
        Assert.NotNull(namedMicrophone);
        Assert.Equal("Переговорка", TranscriptMerger.SpeakerLabel(namedMicrophone, named, AppLanguage.Ru));
        var reset = MeetingEdits.RenameSpeaker("me", null, named);
        var storedMicrophone = reset.SpeakerWithId("me");
        Assert.NotNull(storedMicrophone);
        Assert.Equal("Микрофон", TranscriptMerger.SpeakerLabel(storedMicrophone, reset, AppLanguage.Ru));
        var back = MeetingEdits.MarkAsMe("me", reset);
        Assert.Equal(["Я", "Собеседники"], back.EffectiveSpeakers.Select(s => TranscriptMerger.SpeakerLabel(s, back, AppLanguage.Ru)));

        // Separated microphone: speech no voice was assigned to stops reading «Я» once a voice is.
        var mixed = Meeting(
        [
            Segment(0, 5, Speaker.Me, "Первый", "s1"),
            Segment(9, 12, Speaker.Me, "Без спикера"),
        ], speakers: [Speaker1, Speaker2]);
        Assert.Equal(["Speaker 1", "Speaker 2", "Me"], mixed.EffectiveSpeakers.Select(s => TranscriptMerger.SpeakerLabel(s, mixed, AppLanguage.En)));
        var mixedMarked = MeetingEdits.MarkAsMe("s1", mixed);
        Assert.Equal(["Me", "Speaker 2", "Microphone"],
                     mixedMarked.EffectiveSpeakers.Select(s => TranscriptMerger.SpeakerLabel(s, mixedMarked, AppLanguage.En)));
    }

    // MARK: - Helpers

    static readonly MeetingSpeaker Speaker1 = new() { Id = "s1", Track = MeetingTrack.Microphone, Number = 1, ColorIndex = 0 };
    static readonly MeetingSpeaker Speaker2 = new() { Id = "s2", Track = MeetingTrack.Microphone, Number = 2, ColorIndex = 1 };

    static TranscriptSegment Segment(double start, double end, Speaker speaker, string text, string? speakerId = null) =>
        new() { Start = start, End = end, Speaker = speaker, Text = text, SpeakerId = speakerId };

    static MeetingRecord Meeting(IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<MeetingSpeaker>? speakers = null,
                                 IReadOnlyList<MeetingHighlight>? highlights = null) =>
        new()
        {
            Title = "Test",
            CreatedAt = AppleDate.FromReferenceSeconds(779_000_000),
            Status = MeetingStatus.Transcribed,
            Segments = segments,
            Speakers = speakers ?? [],
            Highlights = highlights ?? [],
        };

    static TranscriptLine Line(int id, double start, double end) =>
        new() { Id = id, Start = start, End = end, SpeakerId = "s1", Track = MeetingTrack.Microphone, Text = "…", SegmentIndices = [id] };

    static MeetingHighlight Moment(double time) =>
        new() { Time = time, Source = HighlightSource.Recording, CreatedAt = AppleDate.FromReferenceSeconds(779_000_000) };
}
