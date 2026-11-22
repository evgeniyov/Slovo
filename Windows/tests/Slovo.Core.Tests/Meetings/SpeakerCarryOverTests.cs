using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// <see cref="SpeakerRegistry.CarryOver"/>: a full re-run that does not separate a track keeps
/// the speakers an earlier run found there, instead of folding their lines back into the
/// track's fallback (on macOS this is covered through MeetingsTranscriberTests).
/// </summary>
public sealed class SpeakerCarryOverTests
{
    static TranscriptSegment Segment(double start, double end, Speaker speaker, string? id = null) =>
        new() { Start = start, End = end, Speaker = speaker, Text = "…", SpeakerId = id };

    static MeetingRecord Previous() => new()
    {
        Title = "Коворк",
        Status = MeetingStatus.Transcribed,
        Segments =
        [
            Segment(0, 10, Speaker.Me, "s1"),
            Segment(10, 20, Speaker.Me, "s2"),
            Segment(20, 30, Speaker.Me),
            Segment(0, 30, Speaker.Others),
        ],
        Speakers =
        [
            new MeetingSpeaker { Id = "s1", Track = MeetingTrack.Microphone, Number = 1, DisplayName = "Аня", ColorIndex = 0 },
            new MeetingSpeaker { Id = "s2", Track = MeetingTrack.Microphone, Number = 2, ColorIndex = 1, IsMe = true },
        ],
    };

    [Fact]
    public void NewSegmentsGoToTheEarlierSpeakerTheyOverlapMost()
    {
        var segments = new[]
        {
            Segment(1, 6, Speaker.Me),
            Segment(8, 14, Speaker.Me),
            Segment(22, 28, Speaker.Me),
            // No earlier line overlaps it: the nearest one decides.
            Segment(40, 45, Speaker.Me),
        };

        var carried = SpeakerRegistry.CarryOver(MeetingTrack.Microphone, segments, Previous());

        Assert.NotNull(carried);
        Assert.Equal(["s1", "s2", null, null], carried.SpeakerIds);
        Assert.Equal(["s1", "s2"], carried.Speakers.Select(s => s.Id));
        Assert.Equal("Аня", carried.Speakers[0].DisplayName);
        Assert.True(carried.Speakers[1].IsMe);
    }

    [Fact]
    public void OnlyTheSpeakersThatReceivedSegmentsComeBack()
    {
        var carried = SpeakerRegistry.CarryOver(MeetingTrack.Microphone, [Segment(12, 18, Speaker.Me)], Previous());
        Assert.NotNull(carried);
        Assert.Equal(["s2"], carried.SpeakerIds);
        Assert.Equal(["s2"], carried.Speakers.Select(s => s.Id));
    }

    [Fact]
    public void NothingIsCarriedWithoutNumberedSpeakersOnTheTrack()
    {
        Assert.Null(SpeakerRegistry.CarryOver(MeetingTrack.Microphone, [Segment(1, 2, Speaker.Me)], null));
        Assert.Null(SpeakerRegistry.CarryOver(MeetingTrack.System, [Segment(1, 2, Speaker.Others)], Previous()));
        var renamedFallbackOnly = new MeetingRecord
        {
            Title = "Созвон",
            Segments = [Segment(0, 10, Speaker.Others)],
            Speakers = [new MeetingSpeaker { Id = "others", Track = MeetingTrack.System, DisplayName = "Команда", ColorIndex = 1 }],
        };
        Assert.Null(SpeakerRegistry.CarryOver(MeetingTrack.System, [Segment(1, 2, Speaker.Others)], renamedFallbackOnly));
    }

    [Fact]
    public void SegmentsWithoutEarlierLinesStayWithTheFallback()
    {
        var carried = SpeakerRegistry.CarryOver(MeetingTrack.Microphone, [], Previous());
        Assert.NotNull(carried);
        Assert.Empty(carried.SpeakerIds);
        Assert.Empty(carried.Speakers);
    }
}
