using Slovo.Core.Model;
using Slovo.Engines.Diarization;
using Voice = Slovo.Engines.Diarization.SpeakerClusterMerger.Voice;

namespace Slovo.Engines.Tests.Diarization;

public class SpeakerClusterMergerTests
{
    /// <summary>A unit vector at an angle in the plane, so distances are easy to reason about.</summary>
    static float[] At(double degrees) => [(float)Math.Cos(degrees * Math.PI / 180), (float)Math.Sin(degrees * Math.PI / 180), 0];

    /// <summary>Euclidean distance between unit vectors at this angle.</summary>
    static double Distance(double degrees) => Math.Sqrt(2 - 2 * Math.Cos(degrees * Math.PI / 180));

    [Fact]
    public void CloseVoicesMergeAndDistantOnesStay()
    {
        // Two voices 20° apart (distance 0.35) and one at 90° (1.41) from them.
        var voices = new Dictionary<int, Voice> { [0] = new(At(0), 30), [1] = new(At(20), 10), [2] = new(At(100), 25) };
        var mapping = SpeakerClusterMerger.Merge(voices, SpeakerCountHint.Automatic, threshold: 0.7);

        Assert.Equal(0, mapping[0]);
        Assert.Equal(0, mapping[1]);
        Assert.Equal(2, mapping[2]);
    }

    [Fact]
    public void MergedCentroidsAreSpeechWeighted()
    {
        var voices = new Dictionary<int, Voice> { [0] = new(At(0), 100), [1] = new(At(40), 1_000), [2] = new(At(70), 100) };
        // The closer pair (1, 2), 30° apart, merges first. Voice 1 has ten times the speech,
        // so their centroid lies at about 42.6°: a 43° threshold then joins voice 0 too,
        // and a 42° one does not.
        var all = SpeakerClusterMerger.Merge(voices, SpeakerCountHint.Automatic, threshold: Distance(43));
        Assert.Single(all.Values.Distinct());
        Assert.Equal(1, all[0]);

        var two = SpeakerClusterMerger.Merge(voices, SpeakerCountHint.Automatic, threshold: Distance(42));
        Assert.Equal([0, 1, 1], two.OrderBy(pair => pair.Key).Select(pair => pair.Value));

        var strict = SpeakerClusterMerger.Merge(voices, SpeakerCountHint.Automatic, threshold: Distance(29));
        Assert.Equal(3, strict.Values.Distinct().Count());
    }

    [Fact]
    public void AnExactCountMergesTheClosestUntilItHolds()
    {
        var voices = new Dictionary<int, Voice>
        {
            [0] = new(At(0), 30), [1] = new(At(60), 30), [2] = new(At(120), 30), [3] = new(At(125), 30),
        };
        var two = SpeakerClusterMerger.Merge(voices, new SpeakerCountHint(Exactly: 2), threshold: 0.01);
        Assert.Equal(2, two.Values.Distinct().Count());
        Assert.Equal(two[2], two[3]);

        var four = SpeakerClusterMerger.Merge(voices, new SpeakerCountHint(Exactly: 4), threshold: 2);
        Assert.Equal(4, four.Values.Distinct().Count());

        // More people than voices found: nothing can be split, nothing merges.
        var many = SpeakerClusterMerger.Merge(voices, new SpeakerCountHint(Exactly: 8), threshold: 2);
        Assert.Equal(4, many.Values.Distinct().Count());
    }

    [Fact]
    public void AMaximumMergesOnlyWhatExceedsIt()
    {
        var voices = new Dictionary<int, Voice> { [0] = new(At(0), 30), [1] = new(At(90), 30), [2] = new(At(180), 30), [3] = new(At(185), 30) };
        var automatic = SpeakerClusterMerger.Merge(voices, SpeakerCountHint.Automatic, threshold: 0.05);
        Assert.Equal(4, automatic.Values.Distinct().Count());

        var capped = SpeakerClusterMerger.Merge(voices, new SpeakerCountHint(Maximum: 3), threshold: 0.05);
        Assert.Equal(3, capped.Values.Distinct().Count());
        Assert.Equal(capped[2], capped[3]);

        // Under the maximum, the threshold still merges.
        var loose = SpeakerClusterMerger.Merge(voices, new SpeakerCountHint(Maximum: 3), threshold: 2);
        Assert.Single(loose.Values.Distinct());
    }

    [Fact]
    public void SmallVoicesJoinTheMostSimilarLargeOneFirst()
    {
        // Voice 2 is a one-second stray window, far from everything but a little closer to 1.
        var voices = new Dictionary<int, Voice> { [0] = new(At(0), 60), [1] = new(At(90), 60), [2] = new(At(175), 1) };
        var automatic = SpeakerClusterMerger.Merge(voices, SpeakerCountHint.Automatic, threshold: 0.5);
        Assert.Equal([0, 1, 1], automatic.OrderBy(pair => pair.Key).Select(pair => pair.Value));

        // With an exact count of three, the quiet voice keeps its own number.
        var exact = SpeakerClusterMerger.Merge(voices, new SpeakerCountHint(Exactly: 3), threshold: 0.5);
        Assert.Equal(3, exact.Values.Distinct().Count());
        // With two, the stray window goes rather than a real person.
        var two = SpeakerClusterMerger.Merge(voices, new SpeakerCountHint(Exactly: 2), threshold: 0.5);
        Assert.Equal([0, 1, 1], two.OrderBy(pair => pair.Key).Select(pair => pair.Value));
    }

    [Fact]
    public void OneOrNoVoicesStayAsTheyAre()
    {
        Assert.Empty(SpeakerClusterMerger.Merge(new Dictionary<int, Voice>(), SpeakerCountHint.Automatic, 0.7));
        var single = SpeakerClusterMerger.Merge(new Dictionary<int, Voice> { [4] = new(At(0), 1) }, new SpeakerCountHint(Exactly: 3), 0.7);
        Assert.Equal(4, single[4]);
    }
}
