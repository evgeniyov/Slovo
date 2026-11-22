using Slovo.Core.Model;

namespace Slovo.Core.Meetings;

/// <summary>The outcome of <see cref="SpeakerRegistry.Build"/>.</summary>
/// <param name="Speakers">The speakers to store.</param>
/// <param name="IdForCluster">The speaker id of every cluster, per separated track.</param>
public sealed record SpeakerRegistryResult(IReadOnlyList<MeetingSpeaker> Speakers,
                                           IReadOnlyDictionary<MeetingTrack, IReadOnlyDictionary<int, string>> IdForCluster);

/// <summary>The outcome of <see cref="SpeakerRegistry.CarryOver"/>.</summary>
/// <param name="SpeakerIds">The speaker id for each segment in order (null for the track's fallback).</param>
/// <param name="Speakers">The earlier speakers that received segments.</param>
public sealed record SpeakerCarryOver(IReadOnlyList<string?> SpeakerIds, IReadOnlyList<MeetingSpeaker> Speakers);

/// <summary>
/// Numbers separated voices and carries the user's names, colors and "This is me" over
/// re-runs. Pure (port of the macOS <c>SpeakerRegistry</c>).
///
/// Diarization clusters are local to one run, so a re-run cannot know that its cluster 2 is
/// the person the user named «Аня». Speakers are therefore matched by when they talk: the
/// voice whose time overlaps a previous speaker's the most takes over that speaker's id,
/// number, name, color and «Я». A track's fallback speaker that the user renamed or marked as
/// «Я» passes its name and «Я» the same way to a voice, which gets a number of its own.
/// </summary>
public static class SpeakerRegistry
{
    /// <summary>«Я» is set automatically in calls when one microphone voice holds this share.</summary>
    public const double AutomaticMeShare = 0.8;
    /// <summary>A re-run speaker takes over a previous one only above this share of its own time.</summary>
    public const double MinimumMatchShare = 0.3;

    /// <param name="microphoneClusters">Time ranges of each microphone cluster; null when the track was not separated.</param>
    /// <param name="systemClusters">The same for system audio.</param>
    /// <param name="previous">The meeting before this run, whose speakers are matched by overlap.</param>
    public static SpeakerRegistryResult Build(IReadOnlyDictionary<int, IReadOnlyList<TimeRange>>? microphoneClusters,
                                              IReadOnlyDictionary<int, IReadOnlyList<TimeRange>>? systemClusters,
                                              bool hasSystemTrack, MeetingRecord? previous)
    {
        var highest = HighestNumber(previous);
        var speakers = new List<MeetingSpeaker>();
        var idForCluster = new Dictionary<MeetingTrack, IReadOnlyDictionary<int, string>>();

        foreach (var track in MeetingTracks.All)
        {
            var clusters = track == MeetingTrack.Microphone ? microphoneClusters : systemClusters;
            if (clusters is null)
            {
                // An unseparated track shows its fallback speaker through EffectiveSpeakers;
                // only a stored one, which holds the user's name or «Я», needs carrying over.
                var fallbackId = MeetingSpeaker.Fallback(track).Id;
                if (previous?.Speakers.FirstOrDefault(s => s.Id == fallbackId && s.Track == track) is { } kept)
                {
                    speakers.Add(kept);
                }
                continue;
            }

            var ordered = FirstAppearanceOrder(clusters);
            var matches = MatchPrevious(ordered, clusters, track, previous);
            var ids = new Dictionary<int, string>();
            foreach (var cluster in ordered)
            {
                var match = matches.GetValueOrDefault(cluster);
                if (match is { Number: { } number })
                {
                    speakers.Add(new MeetingSpeaker
                    {
                        Id = match.Id,
                        Track = track,
                        Number = number,
                        DisplayName = match.DisplayName,
                        ColorIndex = match.ColorIndex,
                        IsMe = match.IsMe,
                    });
                    ids[cluster] = match.Id;
                }
                else
                {
                    // A voice matched to a fallback speaker cannot keep its id, which stands for
                    // a whole track, so it is numbered like a new voice.
                    highest++;
                    var speaker = new MeetingSpeaker
                    {
                        Id = "s" + highest.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Track = track,
                        Number = highest,
                        DisplayName = match?.DisplayName,
                        ColorIndex = (highest - 1) % MeetingSpeaker.PaletteSize,
                        IsMe = match?.IsMe ?? false,
                    };
                    speakers.Add(speaker);
                    ids[cluster] = speaker.Id;
                }
            }
            idForCluster[track] = ids;
        }

        // In person the laptop hears everyone equally, so only calls, where the user is
        // usually the one voice on the microphone, get «Я» automatically.
        if (hasSystemTrack && !speakers.Any(s => s.IsMe) && microphoneClusters is not null
            && DominantCluster(microphoneClusters) is { } dominant
            && idForCluster.TryGetValue(MeetingTrack.Microphone, out var microphoneIds)
            && microphoneIds.TryGetValue(dominant, out var id))
        {
            var index = speakers.FindIndex(s => s.Id == id);
            if (index >= 0)
            {
                speakers[index] = speakers[index] with { IsMe = true };
            }
        }

        return new SpeakerRegistryResult(speakers, idForCluster);
    }

    /// <summary>
    /// The speakers an earlier run found on a track that this run did not separate: the
    /// separation was turned off, the model is missing, it failed, or the track had too little
    /// speech this time. Without this a full re-run folds every line of that track back into
    /// «Я» or «Собеседники» and the names the user gave are gone for good.
    ///
    /// Each new segment goes to the earlier speaker whose lines overlap it the most in time,
    /// else to the one whose line is nearest (<see cref="ClipPlanner.Assign"/> over the earlier
    /// lines as turns).
    /// </summary>
    /// <returns>
    /// Null when <paramref name="previous"/> has no numbered speaker on the track, since a
    /// track's fallback speaker needs no carrying over.
    /// </returns>
    public static SpeakerCarryOver? CarryOver(MeetingTrack track, IReadOnlyList<TranscriptSegment> segments, MeetingRecord? previous)
    {
        if (previous is null)
        {
            return null;
        }
        var earlier = previous.Segments
            .Where(s => MeetingTracks.FromSpeaker(s.Speaker) == track && double.IsFinite(s.Start) && double.IsFinite(s.End))
            .ToList();
        var earlierIds = earlier.Select(previous.SpeakerIdFor).ToList();
        var numbered = previous.Speakers.Where(s => s.Track == track && s.Number is not null).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (!earlierIds.Any(numbered.Contains))
        {
            return null;
        }

        var clusterOfId = new Dictionary<string, int>(StringComparer.Ordinal);
        var idOfCluster = new List<string>();
        var turns = new List<SpeakerTurn>();
        for (var index = 0; index < earlier.Count; index++)
        {
            var segment = earlier[index];
            var id = earlierIds[index];
            if (!clusterOfId.TryGetValue(id, out var cluster))
            {
                cluster = idOfCluster.Count;
                clusterOfId[id] = cluster;
                idOfCluster.Add(id);
            }
            turns.Add(new SpeakerTurn(segment.Start, Math.Max(segment.Start, segment.End), cluster));
        }

        var fallbackId = MeetingSpeaker.Fallback(track).Id;
        var speakerIds = ClipPlanner.Assign(segments, turns).Select(cluster =>
        {
            if (cluster is not { } value || value < 0 || value >= idOfCluster.Count)
            {
                return null;
            }
            var id = idOfCluster[value];
            return id == fallbackId ? null : id;
        }).ToList();
        var used = speakerIds.OfType<string>().ToHashSet(StringComparer.Ordinal);
        var speakers = previous.Speakers.Where(s => s.Track == track && used.Contains(s.Id)).ToList();
        return new SpeakerCarryOver(speakerIds, speakers);
    }

    // MARK: - Matching

    /// <summary>One-to-one, greedy by the seconds of overlap, largest first.</summary>
    static Dictionary<int, MeetingSpeaker> MatchPrevious(List<int> ordered, IReadOnlyDictionary<int, IReadOnlyList<TimeRange>> clusters,
                                                         MeetingTrack track, MeetingRecord? previous)
    {
        var result = new Dictionary<int, MeetingSpeaker>();
        if (previous is null)
        {
            return result;
        }
        // A fallback speaker stands for everyone its segments show, not one voice, so it
        // takes part only when it holds the user's work: a name or «Я». Its time is the time
        // of those segments, and the voice that overlaps it the most receives that work.
        var fallbackId = MeetingSpeaker.Fallback(track).Id;
        var candidates = previous.Speakers.Where(speaker =>
        {
            if (speaker.Track != track)
            {
                return false;
            }
            if (speaker.Number is not null)
            {
                return true;
            }
            var named = TranscriptMerger.NormalizedText(speaker.DisplayName ?? "").Length > 0;
            return speaker.Id == fallbackId && (named || speaker.IsMe);
        }).ToList();
        if (candidates.Count == 0)
        {
            return result;
        }

        var candidateTimes = candidates.Select(candidate => ClipPlanner.Union(previous.Segments
                .Where(s => previous.SpeakerIdFor(s) == candidate.Id)
                .Where(s => s.End > s.Start)
                .Select(s => new TimeRange(s.Start, s.End))))
            .ToList();

        var pairs = new List<(double Overlap, int NewOrder, int CandidateOrder)>();
        for (var newOrder = 0; newOrder < ordered.Count; newOrder++)
        {
            var times = ClipPlanner.Union(clusters.GetValueOrDefault(ordered[newOrder]) ?? []);
            var total = Length(times);
            if (!(total > 0))
            {
                continue;
            }
            for (var candidateOrder = 0; candidateOrder < candidateTimes.Count; candidateOrder++)
            {
                var shared = Overlap(times, candidateTimes[candidateOrder]);
                if (shared > 0 && shared >= MinimumMatchShare * total)
                {
                    pairs.Add((shared, newOrder, candidateOrder));
                }
            }
        }

        var taken = new HashSet<int>();
        foreach (var pair in pairs.OrderByDescending(p => p.Overlap).ThenBy(p => p.NewOrder).ThenBy(p => p.CandidateOrder))
        {
            var cluster = ordered[pair.NewOrder];
            if (result.ContainsKey(cluster) || taken.Contains(pair.CandidateOrder))
            {
                continue;
            }
            result[cluster] = candidates[pair.CandidateOrder];
            taken.Add(pair.CandidateOrder);
        }
        return result;
    }

    /// <summary>
    /// The highest speaker number the meeting has used, so new speakers never take the number
    /// of a merged one and exported timecoded references stay valid.
    ///
    /// A merge removes the source speaker, so stored numbers alone can miss it: runs and
    /// merges record the highest number in <see cref="MeetingRecord.HighestSpeakerNumber"/>,
    /// and for meetings saved before that the ids segments carry and the counts of the last
    /// run cover the usual cases.
    /// </summary>
    public static int HighestNumber(MeetingRecord? meeting)
    {
        if (meeting is null)
        {
            return 0;
        }
        // Counts are bounded before adding, so a hand-edited file cannot overflow the sum.
        var counts = meeting.Diarization?.SpeakersPerTrack.Values.Select(v => (long)Math.Min(Math.Max(0, v), MeetingSpeaker.MaximumNumber)) ?? [];
        var highest = Math.Max((int)Math.Min(counts.Sum(), MeetingSpeaker.MaximumNumber), meeting.HighestSpeakerNumber ?? 0);
        foreach (var speaker in meeting.Speakers)
        {
            highest = Math.Max(highest, Math.Max(speaker.Number ?? 0, NumberFromId(speaker.Id) ?? 0));
        }
        foreach (var segment in meeting.Segments)
        {
            if (segment.SpeakerId is { } id && NumberFromId(id) is { } value)
            {
                highest = Math.Max(highest, value);
            }
        }
        return highest;
    }

    /// <summary>
    /// What <see cref="MeetingRecord.HighestSpeakerNumber"/> becomes when
    /// <paramref name="speakers"/> replace the speakers of <paramref name="meeting"/>: never
    /// lower than before; null while no number was ever used.
    /// </summary>
    public static int? HighestNumber(MeetingRecord? meeting, IReadOnlyList<MeetingSpeaker> speakers)
    {
        var highest = Math.Max(HighestNumber(meeting), speakers.Select(s => s.Number ?? 0).DefaultIfEmpty(0).Max());
        return highest > 0 ? highest : null;
    }

    /// <summary>Swift's <c>Int(id.dropFirst())</c> after an "s": an optional sign, then decimal digits.</summary>
    static int? NumberFromId(string id)
    {
        if (!id.StartsWith('s') || id.Length < 2)
        {
            return null;
        }
        var digits = id.AsSpan(1);
        var start = digits[0] is '+' or '-' ? 1 : 0;
        if (start == digits.Length)
        {
            return null;
        }
        foreach (var c in digits[start..])
        {
            if (c is < '0' or > '9')
            {
                return null;
            }
        }
        if (!long.TryParse(digits, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value is < int.MinValue or > int.MaxValue)
        {
            return null;
        }
        return MeetingSpeaker.ValidNumber((int)value);
    }

    // MARK: - Time

    static List<int> FirstAppearanceOrder(IReadOnlyDictionary<int, IReadOnlyList<TimeRange>> clusters) =>
        clusters
            .Where(p => p.Value.Count > 0)
            .Select(p => (Cluster: p.Key, Start: p.Value.Min(r => r.Lower)))
            .OrderBy(p => p.Start)
            .ThenBy(p => p.Cluster)
            .Select(p => p.Cluster)
            .ToList();

    static int? DominantCluster(IReadOnlyDictionary<int, IReadOnlyList<TimeRange>> clusters)
    {
        var times = clusters.ToDictionary(p => p.Key, p => Length(ClipPlanner.Union(p.Value)));
        var total = times.Values.Sum();
        if (!(total > 0))
        {
            return null;
        }
        var best = times.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First();
        return best.Value >= AutomaticMeShare * total ? best.Key : null;
    }

    static double Length(IReadOnlyList<TimeRange> ranges) => ranges.Sum(r => r.Upper - r.Lower);

    /// <summary>Seconds shared by two sorted, disjoint range lists.</summary>
    static double Overlap(IReadOnlyList<TimeRange> left, IReadOnlyList<TimeRange> right)
    {
        double total = 0;
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Count && rightIndex < right.Count)
        {
            var a = left[leftIndex];
            var b = right[rightIndex];
            var shared = Math.Min(a.Upper, b.Upper) - Math.Max(a.Lower, b.Lower);
            if (shared > 0)
            {
                total += shared;
            }
            if (a.Upper < b.Upper)
            {
                leftIndex++;
            }
            else
            {
                rightIndex++;
            }
        }
        return total;
    }
}
