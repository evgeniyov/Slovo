using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Meetings;

/// <summary><see cref="SpeakerTurns.Normalize"/> (port of DiarizationTurnsTests.swift).</summary>
public class SpeakerTurnsTests
{
    static SpeakerTurn Turn(double start, double end, int cluster) => new(start, end, cluster);

    [Fact]
    public void EmptyAndInvalidTurnsGiveNothing()
    {
        Assert.Empty(SpeakerTurns.Normalize([], 60, SpeakerCountHint.Automatic));
        SpeakerTurn[] invalid = [Turn(double.NaN, 3, 0), Turn(1, double.PositiveInfinity, 0), Turn(5, 4, 0), Turn(1, 2, -1)];
        Assert.Empty(SpeakerTurns.Normalize(invalid, 60, SpeakerCountHint.Automatic));
    }

    [Fact]
    public void TurnsAreClampedToTheTrack()
    {
        SpeakerTurn[] turns = [Turn(-1, 2, 0), Turn(9, 12, 1), Turn(11, 14, 0)];
        Assert.Equal(new[] { Turn(0, 2, 0), Turn(9, 10, 1) },
                     SpeakerTurns.Normalize(turns, 10, SpeakerCountHint.Automatic));
    }

    [Fact]
    public void TurnsAreSortedAndOverlapGoesToTheEarlierTurn()
    {
        SpeakerTurn[] turns = [Turn(4, 8, 1), Turn(0, 5, 0), Turn(1, 3, 1), Turn(8.5, 12, 0)];
        Assert.Equal(new[] { Turn(0, 5, 0), Turn(5, 8, 1), Turn(8.5, 12, 0) },
                     SpeakerTurns.Normalize(turns, 60, SpeakerCountHint.Automatic));
    }

    [Fact]
    public void TurnsShorterThanAQuarterSecondAreDropped()
    {
        SpeakerTurn[] turns = [Turn(0, 4, 0), Turn(4.5, 4.7, 1), Turn(6, 6.25, 1), Turn(8, 12, 0)];
        Assert.Equal(new[] { Turn(0, 4, 0), Turn(6, 6.25, 1), Turn(8, 12, 0) },
                     SpeakerTurns.Normalize(turns, 60, new SpeakerCountHint(Exactly: 2)));
    }

    [Fact]
    public void ClippingCanLeaveATurnTooShortToKeep()
    {
        SpeakerTurn[] turns = [Turn(0, 5, 0), Turn(4.9, 5.1, 1), Turn(7, 10, 1)];
        Assert.Equal(new[] { Turn(0, 5, 0), Turn(7, 10, 1) },
                     SpeakerTurns.Normalize(turns, 60, new SpeakerCountHint(Exactly: 2)));
    }

    [Fact]
    public void SameSpeakerTurnsCloserThanPointEightSecondsMerge()
    {
        SpeakerTurn[] turns = [Turn(0, 2, 0), Turn(2.75, 4, 0), Turn(4.5, 9, 1), Turn(9.9, 12, 1), Turn(13, 16, 0)];
        Assert.Equal(new[] { Turn(0, 4, 0), Turn(4.5, 9, 1), Turn(9.9, 12, 1), Turn(13, 16, 0) },
                     SpeakerTurns.Normalize(turns, 60, SpeakerCountHint.Automatic));
    }

    [Fact]
    public void TinyClusterJoinsTheNearestTurnAndMergesWithIt()
    {
        // Cluster 1 holds 1 s of 61 s: under 3 s and under 2%.
        SpeakerTurn[] turns = [Turn(0, 30, 0), Turn(30.5, 31.5, 1), Turn(40, 70, 2)];
        Assert.Equal(new[] { Turn(0, 31.5, 0), Turn(40, 70, 1) },
                     SpeakerTurns.Normalize(turns, 80, SpeakerCountHint.Automatic));
        Assert.Equal(new[] { Turn(0, 31.5, 0), Turn(40, 70, 1) },
                     SpeakerTurns.Normalize(turns, 80, new SpeakerCountHint(Maximum: 3)));
    }

    [Fact]
    public void TinyClusterTurnsAreReassignedOneByOne()
    {
        SpeakerTurn[] turns = [Turn(0, 20, 0), Turn(21, 21.5, 2), Turn(30, 50, 1), Turn(58, 58.5, 2), Turn(60, 80, 0)];
        Assert.Equal(new[] { Turn(0, 20, 0), Turn(21, 21.5, 0), Turn(30, 50, 1), Turn(58, 58.5, 0), Turn(60, 80, 0) },
                     SpeakerTurns.Normalize(turns, 90, SpeakerCountHint.Automatic));
    }

    [Fact]
    public void AnExactHintKeepsTinyClusters()
    {
        SpeakerTurn[] turns = [Turn(0, 30, 0), Turn(30.5, 31.5, 1), Turn(40, 70, 2)];
        Assert.Equal(new[] { Turn(0, 30, 0), Turn(30.5, 31.5, 1), Turn(40, 70, 2) },
                     SpeakerTurns.Normalize(turns, 80, new SpeakerCountHint(Exactly: 3)));
    }

    [Fact]
    public void AClusterWithThreeSecondsOrAShareOfTwoPercentIsNotTiny()
    {
        // 3 s of 63 s: not under 3 s.
        SpeakerTurn[] longEnough = [Turn(0, 30, 0), Turn(31, 34, 1), Turn(40, 70, 2)];
        Assert.Equal(new[] { 0, 1, 2 }, SpeakerTurns.Normalize(longEnough, 80, SpeakerCountHint.Automatic).Select(t => t.Cluster));
        // 1 s of 21 s is almost 5%.
        SpeakerTurn[] largeShare = [Turn(0, 10, 0), Turn(11, 12, 1), Turn(20, 30, 0)];
        Assert.Equal(new[] { 0, 1, 0 }, SpeakerTurns.Normalize(largeShare, 40, SpeakerCountHint.Automatic).Select(t => t.Cluster));
    }

    [Fact]
    public void AOneSpeakerTrackKeepsItsOnlyCluster()
    {
        SpeakerTurn[] turns = [Turn(0, 1, 4), Turn(3, 4, 4)];
        Assert.Equal(new[] { Turn(0, 1, 0), Turn(3, 4, 0) },
                     SpeakerTurns.Normalize(turns, 10, SpeakerCountHint.Automatic));
    }

    [Fact]
    public void ClustersAreRenumberedByFirstAppearance()
    {
        SpeakerTurn[] turns = [Turn(0, 5, 5), Turn(6, 11, 2), Turn(12, 17, 5), Turn(18, 23, 7)];
        Assert.Equal(new[] { 0, 1, 0, 2 }, SpeakerTurns.Normalize(turns, 30, SpeakerCountHint.Automatic).Select(t => t.Cluster));
    }
}
