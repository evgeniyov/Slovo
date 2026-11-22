using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// <see cref="ClipPlanner"/>: padding, splitting long turns at quiet points, dropped silence,
/// unattributed speech, ordering, and assigning existing segments to new turns
/// (port of MeetingsClipPlannerTests.swift).
/// </summary>
public class ClipPlannerTests
{
    const int Rate = 16_000;

    [Fact]
    public void TurnsArePaddedUpToTheMiddleOfEachGapAndUncoveredSpeechGoesToTheNearestTurn()
    {
        SpeakerTurn[] turns =
        [
            new(1.0, 5.0, 0),
            new(5.2, 9.0, 1),
            new(12.0, 14.0, 0),
        ];
        var clips = ClipPlanner.Plan(turns, [new TimeRange(0.8, 9.6), new TimeRange(11.9, 14.05)], 14.05);

        // 0.8–0.85 is uncovered for less than 0.3 s and stays out; 9.15–9.6 becomes a clip
        // of the turn that ended at 9.0.
        AssertClips(clips, [(0.85, 5.1, 0), (5.1, 9.15, 1), (9.15, 9.6, 1), (11.85, 14.05, 0)]);
    }

    [Fact]
    public void PaddingNeverLeavesTheTrack()
    {
        var clips = ClipPlanner.Plan([new SpeakerTurn(0.05, 3.0, 2)], [], 3.1);
        AssertClips(clips, [(0, 3.1, 2)]);
    }

    [Fact]
    public void TrackWithoutTurnsKeepsItsSpeechRangesUnassigned()
    {
        var clips = ClipPlanner.Plan([], [new TimeRange(5, 7), new TimeRange(1, 3), new TimeRange(7, 9)], 10);
        AssertClips(clips, [(1, 3, null), (5, 7, null), (7, 9, null)]);
    }

    [Fact]
    public void LongTurnWithoutAnEnvelopeIsCutIntoEqualPieces()
    {
        var clips = ClipPlanner.Plan([new SpeakerTurn(0, 45, 1)], [new TimeRange(0, 45)], 45);
        AssertClips(clips, [(0, 15, 1), (15, 30, 1), (30, 45, 1)]);
    }

    [Fact]
    public void LongTurnIsSplitAtAQuietMomentIntoPiecesOfAtMostTwentySeconds()
    {
        float[] samples = [.. Tone(17), .. Silence(0.3), .. Tone(8)];
        var segmenter = new SpeechSegmenter();
        var speech = segmenter.Segments(samples).Select(s => new TimeRange(s.Start, s.End)).ToList();

        var clips = ClipPlanner.Plan([new SpeakerTurn(0, 25.3, 0)], speech, 25.3, Envelope(samples), segmenter);

        Assert.Equal(2, clips.Count);
        var first = clips[0];
        var second = clips[^1];
        Assert.Equal(0, first.Range.Lower, 1e-9);
        Assert.Equal(17.15, first.Range.Upper, 0.1);
        Assert.Equal(first.Range.Upper, second.Range.Lower, 1e-9);
        Assert.Equal(25.3, second.Range.Upper, 1e-6);
        Assert.All(clips, clip => Assert.True(clip.Range.Upper - clip.Range.Lower <= 20 + 1e-9, Describe(clips)));
        Assert.Equal(new int?[] { 0, 0 }, clips.Select(c => c.Cluster));
    }

    [Fact]
    public void SilentPiecesOfALongTurnAreDropped()
    {
        float[] samples = [.. Tone(17), .. Silence(8.3)];
        var segmenter = new SpeechSegmenter();
        var speech = segmenter.Segments(samples).Select(s => new TimeRange(s.Start, s.End)).ToList();

        var clips = ClipPlanner.Plan([new SpeakerTurn(0, 25.3, 3)], speech, 25.3, Envelope(samples), segmenter);

        var clip = Assert.Single(clips);
        Assert.Equal(0, clip.Range.Lower, 1e-9);
        Assert.True(clip.Range.Upper > 17, Describe(clips));
        Assert.True(clip.Range.Upper <= 20 + 1e-9, Describe(clips));
        Assert.Equal(3, clip.Cluster);
    }

    [Fact]
    public void UncoveredSilenceIsNotAClipWhenTheEnvelopeIsKnown()
    {
        // Loud 1–5 s. The detected speech range runs to 5.6 s, so 5.15–5.6 is left
        // uncovered by the padded turn, but it holds no sound.
        float[] samples = [.. Silence(1), .. Tone(4), .. Silence(3)];
        SpeakerTurn[] turns = [new(1, 5, 0)];

        var withEnvelope = ClipPlanner.Plan(turns, [new TimeRange(0.8, 5.6)], 8, Envelope(samples), new SpeechSegmenter());
        AssertClips(withEnvelope, [(0.85, 5.15, 0)]);

        var withoutEnvelope = ClipPlanner.Plan(turns, [new TimeRange(0.8, 5.6)], 8);
        AssertClips(withoutEnvelope, [(0.85, 5.15, 0), (5.15, 5.6, 0)]);
    }

    [Fact]
    public void ClipsNeverOverlapEvenForOverlappingTurns()
    {
        SpeakerTurn[] turns =
        [
            new(4, 8, 1),
            new(0, 5, 0),
            new(6, 7, 2),
            new(double.NaN, 3, 4),
        ];
        var clips = ClipPlanner.Plan(turns, [new TimeRange(0, 9)], 9);
        Assert.NotEmpty(clips);
        foreach (var (left, right) in clips.Zip(clips.Skip(1)))
        {
            Assert.True(left.Range.Upper <= right.Range.Lower + 1e-12, Describe(clips));
        }
        Assert.DoesNotContain(clips, c => c.Cluster == 2 || c.Cluster == 4);
    }

    [Fact]
    public void AssignUsesTheLargestOverlapThenTheNearestTurn()
    {
        SpeakerTurn[] turns =
        [
            new(0, 5, 0),
            new(5, 11, 1),
            new(15, 16, 2),
        ];
        TranscriptSegment[] segments =
        [
            Segment(0, 4),
            Segment(4, 10),
            Segment(12, 13),
            Segment(17, 18),
            Segment(4.5, 5.5),
            Segment(20, 20),
        ];

        Assert.Equal(new int?[] { 0, 1, 1, 2, 0, 2 }, ClipPlanner.Assign(segments, turns));
        Assert.Equal(new int?[] { null, null, null, null, null, null }, ClipPlanner.Assign(segments, []));
        Assert.Empty(ClipPlanner.Assign([], turns));
    }

    // MARK: - Helpers

    static TranscriptSegment Segment(double start, double end) =>
        new() { Start = start, End = end, Speaker = Speaker.Me, Text = "…" };

    static void AssertClips(IReadOnlyList<PlannedClip> clips, (double Lower, double Upper, int? Cluster)[] expected)
    {
        var description = Describe(clips);
        Assert.True(clips.Count == expected.Length, $"{clips.Count} clips instead of {expected.Length}: {description}");
        foreach (var (clip, value) in clips.Zip(expected))
        {
            Assert.True(Math.Abs(clip.Range.Lower - value.Lower) <= 1e-6, $"lower {clip.Range.Lower} != {value.Lower}: {description}");
            Assert.True(Math.Abs(clip.Range.Upper - value.Upper) <= 1e-6, $"upper {clip.Range.Upper} != {value.Upper}: {description}");
            Assert.True(clip.Cluster == value.Cluster, $"cluster {clip.Cluster} != {value.Cluster}: {description}");
        }
    }

    static string Describe(IEnumerable<PlannedClip> clips) => "[" + string.Join(", ", clips) + "]";

    static EnergyEnvelope Envelope(float[] samples)
    {
        var envelope = new EnergyEnvelope(Rate);
        envelope.Append(samples);
        envelope.Finish();
        return envelope;
    }

    static float[] Tone(double seconds) =>
        Enumerable.Range(0, (int)(seconds * Rate))
            .Select(i => (float)(0.2 * Math.Sin(2 * Math.PI * 220 * i / Rate)))
            .ToArray();

    static float[] Silence(double seconds) => new float[(int)(seconds * Rate)];
}
