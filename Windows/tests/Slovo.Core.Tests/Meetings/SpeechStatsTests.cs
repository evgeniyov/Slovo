using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// <see cref="SpeechStatsCalculator"/>: talk time and shares, turns and pace, speech against
/// silence, and the pauses in between (port of MeetingsSpeechStatsTests.swift).
/// </summary>
public class SpeechStatsTests
{
    static MeetingRecord Meeting(double duration, IReadOnlyList<TranscriptSegment> segments,
                                 IReadOnlyList<MeetingSpeaker>? speakers = null) =>
        new()
        {
            Title = "Планёрка",
            Duration = duration,
            Status = MeetingStatus.Transcribed,
            HasSystemAudio = true,
            Segments = segments,
            Speakers = speakers ?? [],
        };

    static IReadOnlyList<MeetingSpeaker> ThreeSpeakers =>
    [
        new MeetingSpeaker { Id = "s1", Track = MeetingTrack.Microphone, Number = 1, ColorIndex = 0 },
        new MeetingSpeaker { Id = "s2", Track = MeetingTrack.Microphone, Number = 2, ColorIndex = 1 },
        new MeetingSpeaker { Id = "s3", Track = MeetingTrack.Microphone, Number = 3, ColorIndex = 2 },
    ];

    static TranscriptSegment Segment(double start, double end, Speaker speaker, string text, string? speakerId = null) =>
        new() { Start = start, End = end, Speaker = speaker, Text = text, SpeakerId = speakerId };

    [Fact]
    public void SharesOfEverySpeakerAddUpToTheWholeConversation()
    {
        TranscriptSegment[] segments =
        [
            Segment(0, 30, Speaker.Me, "первый", "s1"),
            Segment(30, 50, Speaker.Me, "второй", "s2"),
            Segment(50, 60, Speaker.Me, "третий", "s3"),
        ];

        var stats = SpeechStatsCalculator.Compute(Meeting(60, segments, ThreeSpeakers));

        Assert.Equal(new[] { "s1", "s2", "s3" }, stats.Speakers.Select(s => s.SpeakerId));
        Assert.Equal(new double[] { 30, 20, 10 }, stats.Speakers.Select(s => s.SpeakingSeconds));
        Assert.Equal(1, stats.Speakers.Aggregate(0.0, (sum, s) => sum + s.Share), 0.000_001);
        Assert.Equal(0.5, stats.Speakers[0].Share, 0.000_001);
        Assert.Equal(60, stats.SpeechSeconds, 0.001);
        Assert.Equal(1, stats.SpeechShare, 0.000_001);
        Assert.Equal(0, stats.SilenceSeconds, 0.001);
        Assert.Equal(SpeechStatsCalculator.DefaultPauseSeconds, stats.PauseSeconds);
    }

    /// <summary>The loudest speaker leads even when the file lists them in another order.</summary>
    [Fact]
    public void SpeakersAreOrderedByTalkTime()
    {
        TranscriptSegment[] segments =
        [
            Segment(0, 5, Speaker.Me, "коротко", "s1"),
            Segment(10, 70, Speaker.Me, "длинно", "s3"),
            Segment(80, 100, Speaker.Me, "средне", "s2"),
        ];

        var stats = SpeechStatsCalculator.Compute(Meeting(100, segments, ThreeSpeakers));

        Assert.Equal(new[] { "s3", "s2", "s1" }, stats.Speakers.Select(s => s.SpeakerId));
    }

    [Fact]
    public void SingleSpeakerTakesTheWholeShareAndAShortNoteReportsNoPace()
    {
        TranscriptSegment[] segments = [Segment(0.5, 4.5, Speaker.Me, "Купить билеты на среду")];

        var stats = SpeechStatsCalculator.Compute(Meeting(5, segments));

        var speaker = Assert.Single(stats.Speakers);
        Assert.Equal("me", speaker.SpeakerId);
        Assert.Equal(1, speaker.Share, 0.000_001);
        Assert.Equal(1, speaker.Turns);
        Assert.Equal(4, speaker.Words);
        // Four seconds is too little speech for a pace.
        Assert.Null(speaker.WordsPerMinute);
        Assert.Equal(0, stats.PauseCount);
        Assert.Equal(0.0, stats.LongestPauseSeconds);
    }

    /// <summary>
    /// Two people talking at once fill one stretch of the recording, but each keeps their
    /// own speaking time: the two questions have two different answers.
    /// </summary>
    [Fact]
    public void OverlappingVoicesCountOnceInSpeechAndKeepTheirOwnTime()
    {
        TranscriptSegment[] segments =
        [
            Segment(0, 60, Speaker.Me, "я говорю"),
            Segment(30, 90, Speaker.Others, "и я говорю"),
        ];

        var stats = SpeechStatsCalculator.Compute(Meeting(120, segments));

        Assert.Equal(new double[] { 60, 60 }, stats.Speakers.Select(s => s.SpeakingSeconds));
        Assert.Equal(1, stats.Speakers.Aggregate(0.0, (sum, s) => sum + s.Share), 0.000_001);
        Assert.Equal(90, stats.SpeechSeconds, 0.001);
        Assert.Equal(30, stats.SilenceSeconds, 0.001);
        Assert.Equal(0.75, stats.SpeechShare, 0.000_001);
    }

    [Fact]
    public void OverlappingAndZeroLengthSegmentsOfOneSpeakerAreNotCountedTwice()
    {
        TranscriptSegment[] segments =
        [
            Segment(0, 20, Speaker.Me, "первая"),
            Segment(10, 15, Speaker.Me, "внутри первой"),
            Segment(20, 20, Speaker.Me, "мгновение"),
        ];

        var stats = SpeechStatsCalculator.Compute(Meeting(30, segments));

        Assert.Equal(20, stats.Speakers[0].SpeakingSeconds, 0.001);
        Assert.Equal(1, stats.Speakers[0].Turns);
        Assert.Equal(4, stats.Speakers[0].Words);
        Assert.Equal(20, stats.SpeechSeconds, 0.001);
    }

    [Fact]
    public void MeetingWithoutSegmentsHasNoSpeakersAndNoNumbers()
    {
        var stats = SpeechStatsCalculator.Compute(Meeting(600, []));

        Assert.True(stats.IsEmpty);
        Assert.Empty(stats.Speakers);
        Assert.Equal(0.0, stats.SpeechSeconds);
        Assert.Equal(0.0, stats.SpeechShare);
        Assert.Equal(600.0, stats.TotalSeconds);
        Assert.Equal(600.0, stats.SilenceSeconds);
        Assert.Equal(0, stats.PauseCount);
        Assert.Equal(0.0, stats.LongestPauseSeconds);

        var blank = Meeting(60, [Segment(0, 10, Speaker.Me, "   ")]);
        Assert.True(SpeechStatsCalculator.Compute(blank).IsEmpty);
    }

    [Fact]
    public void TurnsEndAtAnotherVoiceOrAtALongerSilenceOfTheSameSpeaker()
    {
        TranscriptSegment[] segments =
        [
            Segment(0, 10, Speaker.Me, "начинаю", "s1"),
            Segment(12, 20, Speaker.Me, "и продолжаю", "s1"),
            Segment(40, 50, Speaker.Me, "отвечаю", "s2"),
            Segment(52, 60, Speaker.Me, "снова я", "s1"),
            Segment(80, 90, Speaker.Me, "и ещё раз", "s1"),
        ];

        var stats = SpeechStatsCalculator.Compute(Meeting(90, segments, ThreeSpeakers));

        var first = stats.Speakers.FirstOrDefault(s => s.SpeakerId == "s1");
        // A two second gap keeps a turn, twenty seconds ends it.
        Assert.Equal(3, first?.Turns);
        Assert.Equal(36, first?.SpeakingSeconds);
        Assert.Equal(12, first?.AverageTurnSeconds);
        Assert.Equal(1, stats.Speakers.FirstOrDefault(s => s.SpeakerId == "s2")?.Turns);
        Assert.Equal(2, stats.PauseCount);
        Assert.Equal(20, stats.LongestPauseSeconds, 0.001);
    }

    /// <summary>
    /// The quiet before the meeting gets going and after it ends is not a pause in the
    /// conversation, only silence.
    /// </summary>
    [Fact]
    public void QuietBeforeTheFirstWordAndAfterTheLastIsNotAPause()
    {
        TranscriptSegment[] segments = [Segment(300, 310, Speaker.Me, "единственная фраза")];

        var stats = SpeechStatsCalculator.Compute(Meeting(600, segments));

        Assert.Equal(0, stats.PauseCount);
        Assert.Equal(0.0, stats.LongestPauseSeconds);
        Assert.Equal(590, stats.SilenceSeconds, 0.001);
    }

    [Fact]
    public void PaceCountsWordsAndKeepsHyphenatedRussianWordsWhole()
    {
        // Ten words: the dash between spaces is punctuation, «кое-что» and «что-то» one
        // word each. Splitting on whitespace would find eleven.
        const string text = "Кое-что мы успели, а что-то нет — переносим на следующую неделю";
        TranscriptSegment[] segments = [Segment(0, 30, Speaker.Me, text)];

        var stats = SpeechStatsCalculator.Compute(Meeting(30, segments));

        Assert.Equal(10, SpeechStatsCalculator.WordCount(text));
        Assert.Equal(10, stats.Speakers[0].Words);
        Assert.Equal(20, stats.Speakers[0].WordsPerMinute ?? 0, 0.000_001);
    }

    /// <summary>
    /// A meeting whose speakers were never separated has the fallback voice of each track,
    /// and a stored speaker who never said anything gets no row at all.
    /// </summary>
    [Fact]
    public void UnseparatedTracksUseTheirFallbackSpeakersAndSilentSpeakersAreLeftOut()
    {
        MeetingSpeaker[] stored = [new MeetingSpeaker { Id = "s1", Track = MeetingTrack.Microphone, Number = 1, ColorIndex = 0 }];
        TranscriptSegment[] segments =
        [
            Segment(0, 20, Speaker.Me, "мой голос"),
            Segment(25, 45, Speaker.Others, "их голос"),
        ];

        var stats = SpeechStatsCalculator.Compute(Meeting(45, segments, stored));

        Assert.Equal(new[] { "me", "others" }, stats.Speakers.Select(s => s.SpeakerId).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(stats.Speakers, s => s.SpeakerId == "s1");
    }

    /// <summary>
    /// A duration that stops before the last word, which only a hand-edited file has,
    /// would otherwise make speech more than the whole recording.
    /// </summary>
    [Fact]
    public void TimelineFallsBackToTheLastWordWhenTheStoredDurationEndsBeforeIt()
    {
        TranscriptSegment[] segments = [Segment(0, 40, Speaker.Me, "говорю дольше, чем записано")];

        var stats = SpeechStatsCalculator.Compute(Meeting(0, segments));

        Assert.Equal(40.0, stats.TotalSeconds);
        Assert.Equal(40, stats.SpeechSeconds, 0.001);
        Assert.Equal(1, stats.SpeechShare, 0.000_001);
        Assert.Equal(0, stats.SilenceSeconds, 0.001);
    }
}
