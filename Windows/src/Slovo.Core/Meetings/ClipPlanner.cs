using Slovo.Core.Model;

namespace Slovo.Core.Meetings;

/// <summary>A half-open stretch of time in seconds, <c>Lower..&lt;Upper</c> (Swift's <c>Range&lt;TimeInterval&gt;</c>).</summary>
public readonly record struct TimeRange(double Lower, double Upper)
{
    public bool IsEmpty => !(Upper > Lower);
    public double Length => Upper - Lower;

    public override string ToString() => $"{Lower}..<{Upper}";
}

/// <summary>A stretch of one track transcribed as one clip.</summary>
/// <param name="Cluster">The diarization cluster the clip belongs to; null on a track without turns.</param>
public readonly record struct PlannedClip(TimeRange Range, int? Cluster);

/// <summary>
/// Turns speaker turns and energy-detected speech into transcription clips of at most
/// <c>maxSeconds</c>, so every clip has one speaker and no detected speech is left out.
/// Pure (port of the macOS <c>ClipPlanner</c>).
///
/// Speech engines return only text, so the speaker of every word is decided before
/// recognition: each clip is one turn of one person, and a line's timestamp is its clip's start.
/// </summary>
public static class ClipPlanner
{
    /// <summary>Audio kept on each side of a turn, so soft word onsets and endings survive.</summary>
    public const double TurnPaddingSeconds = 0.15;
    /// <summary>Speech the diarizer did not attribute becomes a clip of its own from this length.</summary>
    public const double MinimumUncoveredSeconds = 0.3;

    /// <summary>
    /// Clips sorted by start, never overlapping. A track without turns gets its speech ranges
    /// unassigned. Without the track's energy envelope, turns longer than
    /// <paramref name="maxSeconds"/> are cut into equal pieces.
    /// </summary>
    public static IReadOnlyList<PlannedClip> Plan(IReadOnlyList<SpeakerTurn> turns, IReadOnlyList<TimeRange> speech, double trackDuration,
                                                  double maxSeconds = 20) =>
        Plan(turns, speech, trackDuration, maxSeconds, range => EvenPieces(range, maxSeconds));

    /// <summary>
    /// The pipeline's form: long turns are cut at the quietest moments of the track's
    /// envelope, pieces without sound are dropped, and uncovered stretches that hold only
    /// padding silence do not become clips.
    /// </summary>
    public static IReadOnlyList<PlannedClip> Plan(IReadOnlyList<SpeakerTurn> turns, IReadOnlyList<TimeRange> speech, double trackDuration,
                                                  EnergyEnvelope envelope, SpeechSegmenter segmenter, double maxSeconds = 20)
    {
        var splitter = segmenter with { MaxSeconds = maxSeconds };
        return Plan(turns, speech, trackDuration, maxSeconds, range => QuietPieces(range, envelope, splitter));
    }

    /// <summary>
    /// The cluster of each existing segment, in order: the one with the largest time
    /// overlap, else the nearest turn's; null when there are no turns.
    /// </summary>
    public static IReadOnlyList<int?> Assign(IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<SpeakerTurn> turns)
    {
        var valid = turns.Where(t => double.IsFinite(t.Start) && double.IsFinite(t.End) && t.End >= t.Start).ToList();
        if (valid.Count == 0)
        {
            return segments.Select(_ => (int?)null).ToList();
        }
        return segments.Select(segment =>
        {
            var start = double.IsFinite(segment.Start) ? segment.Start : 0;
            var end = double.IsFinite(segment.End) ? Math.Max(start, segment.End) : start;
            var overlap = new Dictionary<int, double>();
            foreach (var turn in valid)
            {
                var shared = Math.Min(end, turn.End) - Math.Max(start, turn.Start);
                if (shared > 0)
                {
                    overlap[turn.Cluster] = overlap.GetValueOrDefault(turn.Cluster) + shared;
                }
            }
            // Equal overlaps go to the lower cluster, so the result does not depend on
            // dictionary order.
            if (overlap.Count > 0)
            {
                var best = overlap.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First();
                return best.Key;
            }
            return NearestCluster(new TimeRange(start, end), valid);
        }).ToList();
    }

    // MARK: - Planning

    static List<PlannedClip> Plan(IReadOnlyList<SpeakerTurn> turns, IReadOnlyList<TimeRange> speech, double trackDuration,
                                  double maxSeconds, Func<TimeRange, IReadOnlyList<TimeRange>> split)
    {
        var latestEnd = turns.Select(t => t.End).Concat(speech.Select(r => r.Upper)).Where(double.IsFinite).DefaultIfEmpty(0).Max();
        var limit = double.IsFinite(trackDuration) && trackDuration > 0 ? trackDuration : Math.Max(0, latestEnd);
        var speechUnion = Union(speech.Select(r => Clamped(r, limit)).OfType<TimeRange>());
        var ordered = ExclusiveTurns(turns, limit);

        if (ordered.Count == 0)
        {
            return speechUnion
                .SelectMany(range => PiecesWithin(range, maxSeconds, split).Select(piece => new PlannedClip(piece, null)))
                .ToList();
        }

        // Padding stops at the middle of the gap to the neighbouring turn, whoever speaks
        // there, so padded turns never overlap.
        var padded = new List<PlannedClip>();
        for (var index = 0; index < ordered.Count; index++)
        {
            var turn = ordered[index];
            var lower = Math.Max(0, turn.Start - TurnPaddingSeconds);
            if (index > 0)
            {
                lower = Math.Max(lower, (ordered[index - 1].End + turn.Start) / 2);
            }
            var upper = Math.Min(limit, turn.End + TurnPaddingSeconds);
            if (index + 1 < ordered.Count)
            {
                upper = Math.Min(upper, (turn.End + ordered[index + 1].Start) / 2);
            }
            if (!(upper > lower))
            {
                continue;
            }
            padded.Add(new PlannedClip(new TimeRange(lower, upper), turn.Cluster));
        }

        var clips = new List<PlannedClip>();
        foreach (var clip in padded)
        {
            if (clip.Range.Upper - clip.Range.Lower > maxSeconds)
            {
                clips.AddRange(split(clip.Range).Select(piece => new PlannedClip(piece, clip.Cluster)));
            }
            else
            {
                clips.Add(clip);
            }
        }

        // Words the diarizer missed still reach the transcript, under the speaker whose turn
        // is closest.
        foreach (var piece in Subtract(padded.Select(c => c.Range).ToList(), speechUnion))
        {
            if (piece.Upper - piece.Lower < MinimumUncoveredSeconds)
            {
                continue;
            }
            var cluster = NearestCluster(piece, ordered);
            clips.AddRange(split(piece).Select(p => new PlannedClip(p, cluster)));
        }

        return NonOverlapping(clips);
    }

    /// <summary>
    /// Turns clamped to the track, sorted by start, with any overlap clipped to the earlier
    /// turn, as one speaker at a time needs.
    /// </summary>
    static List<SpeakerTurn> ExclusiveTurns(IReadOnlyList<SpeakerTurn> turns, double limit)
    {
        var clampedTurns = new List<SpeakerTurn>();
        foreach (var turn in turns)
        {
            if (!double.IsFinite(turn.Start) || !double.IsFinite(turn.End))
            {
                continue;
            }
            var start = Math.Min(Math.Max(0, turn.Start), limit);
            var end = Math.Min(Math.Max(0, turn.End), limit);
            if (end > start)
            {
                clampedTurns.Add(new SpeakerTurn(start, end, turn.Cluster));
            }
        }
        var result = new List<SpeakerTurn>();
        foreach (var turn in clampedTurns.OrderBy(t => t.Start).ThenBy(t => t.End))
        {
            var current = turn;
            if (result.Count > 0 && current.Start < result[^1].End)
            {
                current = current with { Start = result[^1].End };
            }
            if (current.End > current.Start)
            {
                result.Add(current);
            }
        }
        return result;
    }

    static IReadOnlyList<TimeRange> PiecesWithin(TimeRange range, double maxSeconds, Func<TimeRange, IReadOnlyList<TimeRange>> split) =>
        range.Upper - range.Lower > maxSeconds ? split(range) : [range];

    static List<PlannedClip> NonOverlapping(List<PlannedClip> clips)
    {
        var result = new List<PlannedClip>();
        foreach (var clip in clips.OrderBy(c => c.Range.Lower))
        {
            var current = clip;
            if (result.Count > 0 && current.Range.Lower < result[^1].Range.Upper)
            {
                var last = result[^1];
                if (!(current.Range.Upper > last.Range.Upper))
                {
                    continue;
                }
                current = current with { Range = new TimeRange(last.Range.Upper, current.Range.Upper) };
            }
            result.Add(current);
        }
        return result;
    }

    /// <summary>
    /// The cluster of the turn whose nearest edge is closest to the range; on a tie the
    /// earlier turn, since unattributed speech most often trails a turn.
    /// </summary>
    static int? NearestCluster(TimeRange range, IReadOnlyList<SpeakerTurn> turns)
    {
        (double Distance, int Cluster)? best = null;
        foreach (var turn in turns)
        {
            double distance;
            if (range.Upper <= turn.Start)
            {
                distance = turn.Start - range.Upper;
            }
            else if (range.Lower >= turn.End)
            {
                distance = range.Lower - turn.End;
            }
            else
            {
                distance = 0;
            }
            if (best is null || distance < best.Value.Distance)
            {
                best = (distance, turn.Cluster);
            }
        }
        return best?.Cluster;
    }

    // MARK: - Splitting

    static IReadOnlyList<TimeRange> EvenPieces(TimeRange range, double maxSeconds)
    {
        var length = range.Upper - range.Lower;
        if (!(maxSeconds > 0) || !(length > maxSeconds))
        {
            return [range];
        }
        var count = (int)Math.Ceiling(length / maxSeconds);
        var step = length / count;
        var pieces = new List<TimeRange>(count);
        for (var index = 0; index < count; index++)
        {
            var lower = range.Lower + step * index;
            var upper = index == count - 1 ? range.Upper : range.Lower + step * (index + 1);
            pieces.Add(new TimeRange(lower, upper));
        }
        return pieces;
    }

    static IReadOnlyList<TimeRange> QuietPieces(TimeRange range, EnergyEnvelope envelope, SpeechSegmenter segmenter)
    {
        var rate = (double)envelope.SampleRate;
        var frameLength = envelope.FrameLength;
        var firstSample = Math.Max(0L, (long)Math.Floor(range.Lower * rate));
        var endSample = Math.Max(firstSample, (long)Math.Ceiling(range.Upper * rate));
        var firstFrame = (int)Math.Min(int.MaxValue, firstSample / frameLength);
        var endFrame = (int)Math.Min(int.MaxValue, (endSample + frameLength - 1) / frameLength);
        // Envelope frames are 25 ms, so piece edges are clamped back to the range.
        var pieces = new List<TimeRange>();
        foreach (var segment in segmenter.Segments(envelope, firstFrame, endFrame))
        {
            var lower = Math.Max(range.Lower, segment.Start);
            var upper = Math.Min(range.Upper, segment.End);
            if (upper > lower)
            {
                pieces.Add(new TimeRange(lower, upper));
            }
        }
        return pieces;
    }

    // MARK: - Range arithmetic

    static TimeRange? Clamped(TimeRange range, double limit)
    {
        if (!double.IsFinite(range.Lower) || !double.IsFinite(range.Upper))
        {
            return null;
        }
        var lower = Math.Min(Math.Max(0, range.Lower), limit);
        var upper = Math.Min(Math.Max(0, range.Upper), limit);
        return upper > lower ? new TimeRange(lower, upper) : null;
    }

    /// <summary>
    /// Sorted, disjoint ranges covering the same time. Ranges that only touch stay apart, so
    /// the segmenter's consecutive pieces are not joined into one over-long range.
    /// </summary>
    public static IReadOnlyList<TimeRange> Union(IEnumerable<TimeRange> ranges)
    {
        var result = new List<TimeRange>();
        foreach (var range in ranges.OrderBy(r => r.Lower))
        {
            if (range.IsEmpty)
            {
                continue;
            }
            if (result.Count > 0 && range.Lower < result[^1].Upper)
            {
                var last = result[^1];
                result[^1] = new TimeRange(last.Lower, Math.Max(last.Upper, range.Upper));
            }
            else
            {
                result.Add(range);
            }
        }
        return result;
    }

    /// <summary>The parts of <paramref name="ranges"/> (sorted and disjoint) that <paramref name="covered"/> does not cover.</summary>
    static List<TimeRange> Subtract(IReadOnlyList<TimeRange> covered, IReadOnlyList<TimeRange> ranges)
    {
        var cover = Union(covered);
        var result = new List<TimeRange>();
        var coverIndex = 0;
        foreach (var range in ranges)
        {
            var cursor = range.Lower;
            while (coverIndex < cover.Count && cover[coverIndex].Upper <= cursor)
            {
                coverIndex++;
            }
            var index = coverIndex;
            while (index < cover.Count && cover[index].Lower < range.Upper)
            {
                if (cover[index].Lower > cursor)
                {
                    result.Add(new TimeRange(cursor, cover[index].Lower));
                }
                cursor = Math.Max(cursor, cover[index].Upper);
                index++;
            }
            if (cursor < range.Upper)
            {
                result.Add(new TimeRange(cursor, range.Upper));
            }
        }
        return result;
    }
}
