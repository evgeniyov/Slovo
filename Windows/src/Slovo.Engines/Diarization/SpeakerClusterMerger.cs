using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Engines.Diarization;

/// <summary>
/// The second clustering stage: merges the fine voices sherpa-onnx found into people. Pure.
///
/// sherpa-onnx clusters its per-window embeddings with complete linkage, which either
/// merges two similar voices or, with a tight cut, splits one person into several voices,
/// and its exact-count mode spends clusters on outliers. So the first pass splits finely,
/// every voice gets a centroid embedding from its longest turns, and this stage merges
/// them the way pyannote's own pipeline clusters: centroid linkage on unit vectors, with
/// small voices joining the most similar large one first.
/// </summary>
public static class SpeakerClusterMerger
{
    /// <summary>One voice of the first pass: its centroid embedding and seconds of speech.</summary>
    public readonly record struct Voice(float[] Centroid, double Seconds);

    /// <summary>
    /// Maps every voice to the voice it merges into (itself when it stays).
    ///
    /// First, while more voices remain than an exact count asks for (or always, without
    /// one), voices with less than <see cref="SpeakerTurns.TinyClusterSeconds"/> of speech
    /// and under <see cref="SpeakerTurns.TinyClusterShare"/> of all speech join the most
    /// similar larger voice: they are coughs, laughs and stray windows rather than people.
    /// Then the two voices with the closest centroids merge while their Euclidean distance
    /// (between unit vectors, 0…2) is at most <paramref name="threshold"/>, and further
    /// while more voices remain than <c>hint.Maximum</c>; with <c>hint.Exactly</c> they
    /// merge until exactly that many remain, whatever the distance. A merged voice keeps
    /// the number of the one with more speech.
    /// </summary>
    public static IReadOnlyDictionary<int, int> Merge(IReadOnlyDictionary<int, Voice> voices, SpeakerCountHint hint, double threshold)
    {
        var groups = voices.OrderBy(pair => pair.Key)
            .Select(pair => new Group(pair.Key, Normalized(pair.Value.Centroid), Math.Max(pair.Value.Seconds, 0), [pair.Key]))
            .ToList();
        var count = groups.Count;
        var active = Enumerable.Repeat(true, count).ToArray();
        var alive = count;
        var similarity = new double[count, count];
        for (var i = 0; i < count; i++)
        {
            for (var j = i + 1; j < count; j++)
            {
                similarity[i, j] = similarity[j, i] = Dot(groups[i].Centroid, groups[j].Centroid);
            }
        }

        void MergeInto(int keep, int drop)
        {
            var a = groups[keep];
            var b = groups[drop];
            var weightA = Math.Max(a.Seconds, 1e-6);
            var weightB = Math.Max(b.Seconds, 1e-6);
            var centroid = new double[Math.Max(a.Centroid.Length, b.Centroid.Length)];
            for (var k = 0; k < centroid.Length; k++)
            {
                centroid[k] = (k < a.Centroid.Length ? a.Centroid[k] * weightA : 0) + (k < b.Centroid.Length ? b.Centroid[k] * weightB : 0);
            }
            groups[keep] = new Group(a.Seconds >= b.Seconds ? a.Id : b.Id, Normalized(centroid), a.Seconds + b.Seconds, [.. a.Members, .. b.Members]);
            active[drop] = false;
            alive--;
            for (var other = 0; other < count; other++)
            {
                if (active[other] && other != keep)
                {
                    similarity[keep, other] = similarity[other, keep] = Dot(groups[keep].Centroid, groups[other].Centroid);
                }
            }
        }

        var exactly = hint.Exactly is > 0 and var wanted ? wanted : (int?)null;
        var maximum = hint.Maximum is > 0 and var bound ? bound : int.MaxValue;
        var total = groups.Sum(group => group.Seconds);
        bool IsSmall(int index) => groups[index].Seconds < SpeakerTurns.TinyClusterSeconds
            && groups[index].Seconds < SpeakerTurns.TinyClusterShare * total;

        while (alive > (exactly ?? 1))
        {
            var indexes = Enumerable.Range(0, count).Where(index => active[index]).ToList();
            var small = indexes.Where(IsSmall).OrderBy(index => groups[index].Seconds).ThenBy(index => index).Cast<int?>().FirstOrDefault();
            var large = indexes.Where(index => !IsSmall(index)).ToList();
            if (small is not { } tiny || large.Count == 0)
            {
                break;
            }
            MergeInto(large.MaxBy(index => similarity[index, tiny]), tiny);
        }

        while (alive > Math.Max(1, exactly ?? 1))
        {
            var (first, second, best) = (-1, -1, double.NegativeInfinity);
            for (var i = 0; i < count; i++)
            {
                if (!active[i])
                {
                    continue;
                }
                for (var j = i + 1; j < count; j++)
                {
                    if (active[j] && similarity[i, j] > best)
                    {
                        (first, second, best) = (i, j, similarity[i, j]);
                    }
                }
            }
            var distance = Math.Sqrt(Math.Max(0, 2 - 2 * best));
            var mustMerge = exactly is { } target ? alive > target : alive > maximum;
            if (!mustMerge && (exactly is not null || distance > threshold))
            {
                break;
            }
            MergeInto(first, second);
        }

        var mapping = new Dictionary<int, int>();
        for (var index = 0; index < count; index++)
        {
            if (!active[index])
            {
                continue;
            }
            foreach (var member in groups[index].Members)
            {
                mapping[member] = groups[index].Id;
            }
        }
        return mapping;
    }

    sealed record Group(int Id, double[] Centroid, double Seconds, List<int> Members);

    internal static double[] Normalized(IReadOnlyList<float> vector) => Normalized(vector.Select(value => (double)value).ToArray());

    internal static double[] Normalized(double[] vector)
    {
        var length = Math.Sqrt(vector.Sum(value => value * value));
        return length > 0 && double.IsFinite(length) ? vector.Select(value => value / length).ToArray() : new double[vector.Length];
    }

    internal static double Dot(double[] a, double[] b)
    {
        var sum = 0.0;
        for (var k = 0; k < Math.Min(a.Length, b.Length); k++)
        {
            sum += a[k] * b[k];
        }
        return sum;
    }
}
