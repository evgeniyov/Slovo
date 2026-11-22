using Slovo.Core.Meetings;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// <see cref="DiarizationTuning"/>: the pure part of DiarizationTuningTests.swift (standard
/// values, parsing and the recorded description).
/// </summary>
public class DiarizationTuningTests
{
    [Fact]
    public void StandardValues()
    {
        var tuning = DiarizationTuning.Standard;
        Assert.Equal(0.1, tuning.SegmentationStepRatio);
        Assert.Equal(0.0, tuning.MinimumSegmentSeconds);
        Assert.True(tuning.ZeroVoteReembed);
        Assert.Equal(0.4, tuning.ZeroVoteMinimumSeconds);
        Assert.Equal(0.775, tuning.ClusteringThreshold);
        Assert.True(tuning.ExcludeOverlap);
        Assert.True(tuning.ExclusiveSegments);
        Assert.Equal(5.0, DiarizationTuning.MinimumTrackSpeechSeconds);
        Assert.Equal("step=0.1 minSeg=0 threshold=0.775 zeroVote=on", tuning.ToString());
    }

    [Fact]
    public void ConfigurationStringsParse()
    {
        var bench = DiarizationTuning.Parse("step=0.2,minSeg=1.0,zeroVote=off,threshold=0.6");
        Assert.NotNull(bench);
        Assert.Equal(0.2, bench.SegmentationStepRatio);
        Assert.Equal(1.0, bench.MinimumSegmentSeconds);
        Assert.False(bench.ZeroVoteReembed);
        Assert.Equal(0.6, bench.ClusteringThreshold);
        Assert.Equal("step=0.2 minSeg=1 threshold=0.6 zeroVote=off", bench.ToString());

        Assert.Equal(DiarizationTuning.Standard, DiarizationTuning.Parse(DiarizationTuning.Standard.ToString()));
        Assert.Equal(DiarizationTuning.Standard, DiarizationTuning.Parse(""));
        Assert.Equal(0.1, DiarizationTuning.Parse("threshold=0.85")?.SegmentationStepRatio);

        var extra = DiarizationTuning.Parse("zeroVoteMin=0.8 excludeOverlap=false exclusive=off");
        Assert.NotNull(extra);
        Assert.Equal(0.8, extra.ZeroVoteMinimumSeconds);
        Assert.False(extra.ExcludeOverlap);
        Assert.False(extra.ExclusiveSegments);
    }

    [Theory]
    [InlineData("speed=2")]
    [InlineData("step")]
    [InlineData("step=")]
    [InlineData("step=fast")]
    [InlineData("step=0")]
    [InlineData("step=1.5")]
    [InlineData("threshold=2.5")]
    [InlineData("threshold=0")]
    [InlineData("minSeg=-1")]
    [InlineData("zeroVote=maybe")]
    [InlineData("step=0,1")]
    [InlineData("threshold=nan")]
    public void BadConfigurationStringsAreRejected(string text)
    {
        Assert.Null(DiarizationTuning.Parse(text));
    }

    // The five offlineConfig tests (FluidAudio's OfflineDiarizerConfig) are not ported: Windows has no FluidAudio.
}
