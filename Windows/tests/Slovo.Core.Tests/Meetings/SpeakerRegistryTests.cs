using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Clusters = System.Collections.Generic.Dictionary<int, System.Collections.Generic.IReadOnlyList<Slovo.Core.Meetings.TimeRange>>;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// <see cref="SpeakerRegistry"/>: numbering, colors, fallbacks, automatic «Я» and carrying the
/// user's work over re-runs (port of MeetingsSpeakerRegistryTests.swift).
/// </summary>
public class SpeakerRegistryTests
{
    [Fact]
    public void MicrophoneClustersAreNumberedByFirstAppearanceThenSystemClustersContinue()
    {
        var built = SpeakerRegistry.Build(new Clusters { [3] = [R(10, 20)], [1] = [R(0, 5), R(30, 40)] },
                                          new Clusters { [0] = [R(5, 9)] }, hasSystemTrack: true, previous: null);

        Assert.Equal(
            new[]
            {
                MakeSpeaker("s1", MeetingTrack.Microphone, 1, 0),
                MakeSpeaker("s2", MeetingTrack.Microphone, 2, 1),
                MakeSpeaker("s3", MeetingTrack.System, 3, 2),
            },
            built.Speakers);
        Assert.Equal(new (int, string)[] { (1, "s1"), (3, "s2") }, Ids(built, MeetingTrack.Microphone));
        Assert.Equal(new (int, string)[] { (0, "s3") }, Ids(built, MeetingTrack.System));
    }

    [Fact]
    public void ColorsFollowNumbersAndWrapAroundThePalette()
    {
        var clusters = new Clusters();
        for (var index = 0; index < 9; index++)
        {
            clusters[index] = [R(index * 10, index * 10 + 5)];
        }
        var built = SpeakerRegistry.Build(clusters, null, hasSystemTrack: false, previous: null);
        Assert.Equal(Enumerable.Range(1, 9).Select(n => (int?)n), built.Speakers.Select(s => s.Number));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 0 }, built.Speakers.Select(s => s.ColorIndex));
    }

    [Fact]
    public void UnseparatedTracksKeepOnlyAStoredFallback()
    {
        var plain = SpeakerRegistry.Build(null, new Clusters { [0] = [R(0, 5)] }, hasSystemTrack: true, previous: null);
        Assert.Equal(new[] { "s1" }, plain.Speakers.Select(s => s.Id));
        Assert.Equal(MeetingTrack.System, plain.Speakers.FirstOrDefault()?.Track);
        Assert.DoesNotContain(MeetingTrack.Microphone, plain.IdForCluster);

        var renamedOthers = new MeetingSpeaker { Id = "others", Track = MeetingTrack.System, DisplayName = "Команда", ColorIndex = 1 };
        var previous = new MeetingRecord
        {
            Title = "Звонок",
            Status = MeetingStatus.Transcribed,
            HasSystemAudio = true,
            Segments = [Segment(0, 5, Speaker.Others, "Алло")],
            Speakers = [renamedOthers],
        };
        var kept = SpeakerRegistry.Build(new Clusters { [0] = [R(0, 10)] }, null, hasSystemTrack: true, previous: previous);
        Assert.Equal(
            new[]
            {
                MakeSpeaker("s1", MeetingTrack.Microphone, 1, 0, isMe: true),
                renamedOthers,
            },
            kept.Speakers);
    }

    [Fact]
    public void MeIsAutomaticOnlyInCallsAndFromEightyPercent()
    {
        static bool[] Me(Clusters clusters, bool call) =>
            SpeakerRegistry.Build(clusters, null, hasSystemTrack: call, previous: null).Speakers.Select(s => s.IsMe).ToArray();

        Assert.Equal(new[] { true, false }, Me(new Clusters { [0] = [R(0, 85)], [1] = [R(85, 100)] }, call: true));
        Assert.Equal(new[] { true, false }, Me(new Clusters { [0] = [R(0, 80)], [1] = [R(80, 100)] }, call: true));
        Assert.Equal(new[] { false, false }, Me(new Clusters { [0] = [R(0, 79)], [1] = [R(79, 100)] }, call: true));
        Assert.Equal(new[] { false, true }, Me(new Clusters { [0] = [R(0, 20)], [1] = [R(20, 100)] }, call: true));
        Assert.Equal(new[] { false }, Me(new Clusters { [0] = [R(0, 100)] }, call: false));
    }

    /// <summary>
    /// A call recorded with the microphone off: nothing on the microphone track can hold
    /// most of the speech, so nobody becomes «Я» by the call rule.
    /// </summary>
    [Fact]
    public void ASilentMicrophoneTrackGetsNoAutomaticMe()
    {
        var system = new Clusters { [0] = [R(0, 30)], [1] = [R(30, 40)] };
        Clusters?[] microphones = [null, new Clusters(), new Clusters { [0] = [] }, new Clusters { [0] = [R(5, 5)] }];
        foreach (var microphone in microphones)
        {
            var built = SpeakerRegistry.Build(microphone, system, hasSystemTrack: true, previous: null);
            Assert.DoesNotContain(built.Speakers, s => s.IsMe);
        }
        var silent = SpeakerRegistry.Build(new Clusters(), system, hasSystemTrack: true, previous: null);
        Assert.Equal(new[] { MeetingTrack.System, MeetingTrack.System }, silent.Speakers.Select(s => s.Track));
    }

    [Fact]
    public void ReRunKeepsIdNumberNameColorAndMeOfMatchedSpeakers()
    {
        var previous = new MeetingRecord
        {
            Title = "Коворк",
            Status = MeetingStatus.Transcribed,
            Segments =
            [
                Segment(0, 10, Speaker.Me, "a", "s1"),
                Segment(10, 20, Speaker.Me, "b", "s2"),
                Segment(20, 30, Speaker.Me, "c", "s1"),
            ],
            Speakers =
            [
                MakeSpeaker("s1", MeetingTrack.Microphone, 1, 6, displayName: "Аня"),
                MakeSpeaker("s2", MeetingTrack.Microphone, 2, 1, isMe: true),
            ],
        };

        var built = SpeakerRegistry.Build(new Clusters { [0] = [R(10, 20)], [1] = [R(0, 10), R(20, 30)] }, null,
                                          hasSystemTrack: true, previous: previous);

        Assert.Equal(previous.Speakers, built.Speakers);
        Assert.Equal(new (int, string)[] { (0, "s2"), (1, "s1") }, Ids(built, MeetingTrack.Microphone));
    }

    [Fact]
    public void PreviousMeChoiceWinsOverTheAutomaticOne()
    {
        var previous = new MeetingRecord
        {
            Title = "Звонок",
            Status = MeetingStatus.Transcribed,
            HasSystemAudio = true,
            Segments =
            [
                Segment(0, 85, Speaker.Me, "a", "s1"),
                Segment(85, 100, Speaker.Me, "b", "s2"),
            ],
            Speakers =
            [
                MakeSpeaker("s1", MeetingTrack.Microphone, 1, 0),
                MakeSpeaker("s2", MeetingTrack.Microphone, 2, 1, isMe: true),
            ],
        };
        var built = SpeakerRegistry.Build(new Clusters { [0] = [R(0, 85)], [1] = [R(85, 100)] }, null,
                                          hasSystemTrack: true, previous: previous);
        Assert.Equal(new[] { false, true }, built.Speakers.Select(s => s.IsMe));
    }

    [Fact]
    public void NewSpeakersGetNumbersAboveTheHighestEverUsed()
    {
        var previous = new MeetingRecord
        {
            Title = "Коворк",
            Status = MeetingStatus.Transcribed,
            HasSystemAudio = true,
            Segments =
            [
                Segment(0, 10, Speaker.Me, "a", "s1"),
                Segment(20, 30, Speaker.Me, "b", "s4"),
            ],
            Speakers =
            [
                MakeSpeaker("s1", MeetingTrack.Microphone, 1, 0),
                MakeSpeaker("s4", MeetingTrack.Microphone, 4, 3),
            ],
        };

        var built = SpeakerRegistry.Build(new Clusters { [0] = [R(0, 10)], [1] = [R(20, 30)], [2] = [R(40, 50)] },
                                          new Clusters { [0] = [R(60, 70)] }, hasSystemTrack: true, previous: previous);

        Assert.Equal(new[] { "s1", "s4", "s5", "s6" }, built.Speakers.Select(s => s.Id));
        Assert.Equal(new int?[] { 1, 4, 5, 6 }, built.Speakers.Select(s => s.Number));
        Assert.Equal(new[] { 0, 3, 4, 5 }, built.Speakers.Select(s => s.ColorIndex));
        Assert.Equal(new[] { MeetingTrack.Microphone, MeetingTrack.Microphone, MeetingTrack.Microphone, MeetingTrack.System },
                     built.Speakers.Select(s => s.Track));
    }

    [Fact]
    public void MergedAwayNumbersAreNotReusedAfterTheLastRun()
    {
        var previous = new MeetingRecord
        {
            Title = "Коворк",
            Status = MeetingStatus.Transcribed,
            Segments = [Segment(0, 30, Speaker.Me, "a", "s1")],
            Speakers = [MakeSpeaker("s1", MeetingTrack.Microphone, 1, 0)],
            Diarization = new MeetingDiarization { State = DiarizationState.Applied, SpeakersPerTrack = new() { ["microphone"] = 3 } },
        };
        var built = SpeakerRegistry.Build(new Clusters { [0] = [R(0, 30)], [1] = [R(40, 50)] }, null,
                                          hasSystemTrack: false, previous: previous);
        Assert.Equal(new[] { "s1", "s4" }, built.Speakers.Select(s => s.Id));
    }

    [Fact]
    public void NumbersRemovedByMergesAreNeverGivenToNewVoices()
    {
        // Stores a run's result the way MeetingTranscriber does.
        static MeetingRecord Stored(Clusters clusters, MeetingRecord meeting)
        {
            var built = SpeakerRegistry.Build(clusters, null, hasSystemTrack: false, previous: meeting);
            var ids = built.IdForCluster.GetValueOrDefault(MeetingTrack.Microphone);
            var segments = clusters
                .SelectMany(pair => pair.Value.Select(range =>
                    Segment(range.Lower, range.Upper, Speaker.Me, $"c{pair.Key}", ids?.GetValueOrDefault(pair.Key))))
                .OrderBy(s => s.Start)
                .ToList();
            return meeting with
            {
                Segments = segments,
                HighestSpeakerNumber = SpeakerRegistry.HighestNumber(meeting, built.Speakers),
                Speakers = built.Speakers,
                Diarization = new MeetingDiarization
                {
                    State = DiarizationState.Applied,
                    SpeakersPerTrack = new() { ["microphone"] = built.Speakers.Count },
                },
            };
        }

        var meeting = Stored(new Clusters { [0] = [R(0, 10)], [1] = [R(10, 20)], [2] = [R(20, 30)] },
                             new MeetingRecord { Title = "Коворк", Status = MeetingStatus.Transcribed });
        Assert.Equal(new[] { "s1", "s2", "s3" }, meeting.Speakers.Select(s => s.Id));
        meeting = MeetingEdits.MergeSpeaker("s3", "s2", meeting);
        meeting = Stored(new Clusters { [0] = [R(0, 10)], [1] = [R(10, 30)], [2] = [R(40, 50)] }, meeting);
        Assert.Equal(new[] { "s1", "s2", "s4" }, meeting.Speakers.Select(s => s.Id));
        // After this merge no speaker, segment or run count names 4 any more.
        meeting = MeetingEdits.MergeSpeaker("s4", "s1", meeting);
        Assert.Equal(4, meeting.HighestSpeakerNumber);

        var built = SpeakerRegistry.Build(new Clusters { [0] = [R(0, 10), R(40, 50)], [1] = [R(10, 30)], [2] = [R(60, 70)] },
                                          null, hasSystemTrack: false, previous: meeting);
        Assert.Equal(new[] { "s1", "s2", "s5" }, built.Speakers.Select(s => s.Id));
    }

    [Fact]
    public void MatchingIsOneToOneAndNeedsThirtyPercentOfTheNewSpeakersTime()
    {
        var previous = new MeetingRecord
        {
            Title = "Коворк",
            Status = MeetingStatus.Transcribed,
            Segments = [Segment(0, 10, Speaker.Me, "a", "s1")],
            Speakers = [MakeSpeaker("s1", MeetingTrack.Microphone, 1, 0, displayName: "Аня")],
        };

        var split = SpeakerRegistry.Build(new Clusters { [0] = [R(0, 6)], [1] = [R(6, 10)] }, null,
                                          hasSystemTrack: false, previous: previous);
        Assert.Equal(new[] { "s1", "s2" }, split.Speakers.Select(s => s.Id));
        Assert.Equal(new string?[] { "Аня", null }, split.Speakers.Select(s => s.DisplayName));

        var barelyOverlapping = SpeakerRegistry.Build(new Clusters { [0] = [R(8, 18)] }, null,
                                                      hasSystemTrack: false, previous: previous);
        Assert.Equal(new[] { "s2" }, barelyOverlapping.Speakers.Select(s => s.Id));
        Assert.Null(barelyOverlapping.Speakers.FirstOrDefault()?.DisplayName);
    }

    [Fact]
    public void FallbackSpeakersAreNeverMatchedToVoices()
    {
        var previous = new MeetingRecord
        {
            Title = "Коворк",
            Status = MeetingStatus.Transcribed,
            Segments = [Segment(0, 100, Speaker.Me, "a")],
        };
        var built = SpeakerRegistry.Build(new Clusters { [0] = [R(0, 90)], [1] = [R(90, 100)] }, null,
                                          hasSystemTrack: false, previous: previous);
        Assert.Equal(new[] { "s1", "s2" }, built.Speakers.Select(s => s.Id));
        Assert.Equal(new[] { false, false }, built.Speakers.Select(s => s.IsMe));
    }

    [Fact]
    public void RenamedFallbackSpeakersPassTheirNameAndMeToTheVoiceThatOverlapsThemMost()
    {
        // Transcribed before the speaker model was downloaded, then renamed.
        var previous = new MeetingRecord
        {
            Title = "Звонок",
            Status = MeetingStatus.Transcribed,
            HasSystemAudio = true,
            Segments =
            [
                Segment(0, 10, Speaker.Me, "a"),
                Segment(10, 20, Speaker.Others, "b"),
                Segment(20, 24, Speaker.Others, "c"),
            ],
            Speakers =
            [
                new MeetingSpeaker { Id = "me", Track = MeetingTrack.Microphone, DisplayName = "Михаил", ColorIndex = 0, IsMe = true },
                new MeetingSpeaker { Id = "others", Track = MeetingTrack.System, DisplayName = "Аня", ColorIndex = 1 },
            ],
        };

        var built = SpeakerRegistry.Build(new Clusters { [0] = [R(0, 10)] }, new Clusters { [0] = [R(20, 24)], [1] = [R(10, 20)] },
                                          hasSystemTrack: true, previous: previous);

        Assert.Equal(
            new[]
            {
                MakeSpeaker("s1", MeetingTrack.Microphone, 1, 0, displayName: "Михаил", isMe: true),
                MakeSpeaker("s2", MeetingTrack.System, 2, 1, displayName: "Аня"),
                MakeSpeaker("s3", MeetingTrack.System, 3, 2),
            },
            built.Speakers);
        Assert.Equal(new (int, string)[] { (0, "s3"), (1, "s2") }, Ids(built, MeetingTrack.System));

        // A stored fallback without a name or «Я» has nothing to pass on.
        var reset = previous with
        {
            Speakers = [new MeetingSpeaker { Id = "others", Track = MeetingTrack.System, DisplayName = " ", ColorIndex = 1 }],
        };
        var plain = SpeakerRegistry.Build(null, new Clusters { [0] = [R(10, 24)] }, hasSystemTrack: true, previous: reset);
        Assert.Equal(new[] { MakeSpeaker("s1", MeetingTrack.System, 1, 0) }, plain.Speakers);
    }

    [Fact]
    public void SpeakersOfTheOtherTrackAreNotMatched()
    {
        var previous = new MeetingRecord
        {
            Title = "Звонок",
            Status = MeetingStatus.Transcribed,
            HasSystemAudio = true,
            Segments = [Segment(0, 10, Speaker.Others, "a", "s1")],
            Speakers = [MakeSpeaker("s1", MeetingTrack.System, 1, 0, displayName: "Олег")],
        };
        var built = SpeakerRegistry.Build(new Clusters { [0] = [R(0, 10)] }, null, hasSystemTrack: true, previous: previous);
        Assert.Equal(new[] { "s2" }, built.Speakers.Select(s => s.Id));
        Assert.Equal(MeetingTrack.Microphone, built.Speakers.FirstOrDefault()?.Track);
    }

    // MARK: - Helpers

    static TimeRange R(double lower, double upper) => new(lower, upper);

    static MeetingSpeaker MakeSpeaker(string id, MeetingTrack track, int? number, int colorIndex, string? displayName = null,
                                      bool isMe = false) =>
        new() { Id = id, Track = track, Number = number, DisplayName = displayName, ColorIndex = colorIndex, IsMe = isMe };

    static TranscriptSegment Segment(double start, double end, Speaker speaker, string text, string? speakerId = null) =>
        new() { Start = start, End = end, Speaker = speaker, Text = text, SpeakerId = speakerId };

    /// <summary>The cluster-to-id map of a track sorted by cluster, since dictionaries have no order; empty when the track has none.</summary>
    static (int Cluster, string Id)[] Ids(SpeakerRegistryResult built, MeetingTrack track)
    {
        Assert.True(built.IdForCluster.ContainsKey(track), $"no ids for {track}");
        return built.IdForCluster[track].OrderBy(p => p.Key).Select(p => (p.Key, p.Value)).ToArray();
    }
}
