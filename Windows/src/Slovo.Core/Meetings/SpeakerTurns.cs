using Slovo.Core.Model;

namespace Slovo.Core.Meetings;

/// <summary>Cleans up raw diarization turns before clips are planned. Pure (port of the macOS <c>SpeakerTurns</c>).</summary>
public static class SpeakerTurns
{
    /// <summary>Shorter pieces left after clipping hold no word worth a transcript line.</summary>
    public const double MinimumTurnSeconds = 0.25;
    /// <summary>Same-speaker turns closer than this are one utterance with a breath in between.</summary>
    public const double MergeGapSeconds = 0.8;
    /// <summary>
    /// A cluster under both limits is a stray label rather than a person: a cough, a laugh or
    /// a few frames the clustering could not place.
    /// </summary>
    public const double TinyClusterSeconds = 3;
    public const double TinyClusterShare = 0.02;

    /// <summary>Absorbs floating-point noise in durations that are exactly on a limit.</summary>
    const double Tolerance = 1e-9;

    /// <summary>
    /// Clamps turns to the track, clips overlaps, drops turns under 0.25 s, merges
    /// same-cluster turns separated by less than 0.8 s, folds tiny clusters into their
    /// neighbours unless <c>hint.Exactly</c> is set, and renumbers clusters by first appearance.
    /// </summary>
    public static IReadOnlyList<SpeakerTurn> Normalize(IReadOnlyList<SpeakerTurn> turns, double trackDuration, SpeakerCountHint hint)
    {
        var limit = double.IsFinite(trackDuration) ? Math.Max(0, trackDuration) : double.PositiveInfinity;
        var kept = ClippedToOneSpeaker(Clamped(turns, limit))
            .Where(t => t.End - t.Start >= MinimumTurnSeconds - Tolerance)
            .ToList();
        kept = MergingClose(kept);
        // With an exact count the user told us how many people there are, so a quiet person
        // must keep their own cluster.
        if (hint.Exactly is null)
        {
            kept = FoldingTinyClusters(kept);
        }
        return Renumbered(kept);
    }

    /// <summary>Finite turns inside <c>0...limit</c>, sorted by start.</summary>
    static List<SpeakerTurn> Clamped(IReadOnlyList<SpeakerTurn> turns, double limit)
    {
        var result = new List<SpeakerTurn>();
        foreach (var turn in turns)
        {
            if (!double.IsFinite(turn.Start) || !double.IsFinite(turn.End) || turn.Cluster < 0)
            {
                continue;
            }
            var start = Math.Min(Math.Max(turn.Start, 0), limit);
            var end = Math.Min(Math.Max(turn.End, 0), limit);
            if (end > start)
            {
                result.Add(new SpeakerTurn(start, end, turn.Cluster));
            }
        }
        return result.OrderBy(t => t.Start).ThenBy(t => t.End).ThenBy(t => t.Cluster).ToList();
    }

    /// <summary>Gives overlapping time to the turn that started first, because a transcript line has one speaker.</summary>
    static List<SpeakerTurn> ClippedToOneSpeaker(List<SpeakerTurn> sorted)
    {
        var result = new List<SpeakerTurn>();
        var coveredUntil = double.NegativeInfinity;
        foreach (var original in sorted)
        {
            var turn = original;
            if (turn.Start < coveredUntil)
            {
                turn = turn with { Start = coveredUntil };
            }
            if (!(turn.End > turn.Start))
            {
                continue;
            }
            coveredUntil = Math.Max(coveredUntil, turn.End);
            result.Add(turn);
        }
        return result;
    }

    static List<SpeakerTurn> MergingClose(List<SpeakerTurn> sorted)
    {
        var result = new List<SpeakerTurn>();
        foreach (var turn in sorted)
        {
            if (result.Count > 0 && result[^1].Cluster == turn.Cluster && turn.Start - result[^1].End < MergeGapSeconds)
            {
                result[^1] = result[^1] with { End = Math.Max(result[^1].End, turn.End) };
            }
            else
            {
                result.Add(turn);
            }
        }
        return result;
    }

    static List<SpeakerTurn> FoldingTinyClusters(List<SpeakerTurn> sorted)
    {
        var totalSpeech = sorted.Aggregate(0.0, (sum, t) => sum + (t.End - t.Start));
        var speechByCluster = new Dictionary<int, double>();
        foreach (var turn in sorted)
        {
            speechByCluster[turn.Cluster] = speechByCluster.GetValueOrDefault(turn.Cluster) + (turn.End - turn.Start);
        }
        var tiny = speechByCluster
            .Where(p => p.Value < TinyClusterSeconds && p.Value < TinyClusterShare * totalSpeech)
            .Select(p => p.Key)
            .ToHashSet();
        if (tiny.Count == 0 || tiny.Count >= speechByCluster.Count)
        {
            return sorted;
        }

        var anchors = sorted.Where(t => !tiny.Contains(t.Cluster)).ToList();
        var reassigned = sorted.Select(turn =>
        {
            if (!tiny.Contains(turn.Cluster) || Nearest(turn, anchors) is not { } nearest)
            {
                return turn;
            }
            return new SpeakerTurn(turn.Start, turn.End, nearest.Cluster);
        }).ToList();
        return MergingClose(reassigned);
    }

    /// <summary>
    /// The anchor with the smallest gap to <paramref name="turn"/>; the earlier one wins a
    /// tie, since a short interjection more often answers the previous speaker.
    /// </summary>
    static SpeakerTurn? Nearest(SpeakerTurn turn, List<SpeakerTurn> anchors)
    {
        SpeakerTurn? best = null;
        var bestGap = double.PositiveInfinity;
        foreach (var anchor in anchors)
        {
            var gap = Math.Max(0, Math.Max(anchor.Start - turn.End, turn.Start - anchor.End));
            if (gap < bestGap)
            {
                bestGap = gap;
                best = anchor;
            }
        }
        return best;
    }

    static List<SpeakerTurn> Renumbered(List<SpeakerTurn> sorted)
    {
        var numbers = new Dictionary<int, int>();
        var result = new List<SpeakerTurn>(sorted.Count);
        foreach (var turn in sorted)
        {
            if (!numbers.TryGetValue(turn.Cluster, out var number))
            {
                number = numbers.Count;
                numbers[turn.Cluster] = number;
            }
            result.Add(new SpeakerTurn(turn.Start, turn.End, number));
        }
        return result;
    }
}
