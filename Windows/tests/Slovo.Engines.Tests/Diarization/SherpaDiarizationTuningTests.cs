using System.Globalization;
using Slovo.Engines.Diarization;

namespace Slovo.Engines.Tests.Diarization;

/// <summary>After DiarizationTuningTests.swift, for the values sherpa-onnx takes.</summary>
public class DiarizationTuningTests
{
    [Fact]
    public void StandardValues()
    {
        var tuning = SherpaDiarizationTuning.Standard;
        Assert.Equal(0.1, tuning.SegmentationStepRatio);
        Assert.Equal(0.2, tuning.SplitThreshold);
        Assert.Equal(0.7045, tuning.ClusteringThreshold);
        Assert.Equal(0, tuning.MinDurationOn);
        Assert.Equal(0, tuning.MinDurationOff);
        Assert.Equal(5, SherpaDiarizationTuning.MinimumTrackSpeechSeconds);
        Assert.Equal("step=0.1 split=0.2 threshold=0.7045 minOn=0 minOff=0", tuning.Description);
        Assert.Equal(tuning.Description, tuning.ToString());
    }

    [Fact]
    public void ConfigurationStringsParse()
    {
        var bench = SherpaDiarizationTuning.Parse("step=0.2,split=0.25,threshold=0.6,minOn=0.3,minOff=0.5");
        Assert.NotNull(bench);
        Assert.Equal(new SherpaDiarizationTuning(0.2, 0.25, 0.6, 0.3, 0.5), bench);
        Assert.Equal("step=0.2 split=0.25 threshold=0.6 minOn=0.3 minOff=0.5", bench.Description);

        Assert.Equal(SherpaDiarizationTuning.Standard, SherpaDiarizationTuning.Parse(SherpaDiarizationTuning.Standard.Description));
        Assert.Equal(SherpaDiarizationTuning.Standard, SherpaDiarizationTuning.Parse(""));
        Assert.Equal(0.1, SherpaDiarizationTuning.Parse("threshold=0.85")?.SegmentationStepRatio);
        Assert.Equal(0.85, SherpaDiarizationTuning.Parse("THRESHOLD=0.85")?.ClusteringThreshold);
    }

    [Fact]
    public void TheDescriptionIgnoresTheCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            Assert.Equal("step=0.1 split=0.2 threshold=0.7045 minOn=0 minOff=0", SherpaDiarizationTuning.Standard.Description);
            Assert.Equal(0.6, SherpaDiarizationTuning.Parse("threshold=0.6")?.ClusteringThreshold);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
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
    [InlineData("split=-1")]
    [InlineData("minOn=-1")]
    [InlineData("threshold=nan")]
    [InlineData("step=0,1")]
    [InlineData("zeroVote=on")]
    [InlineData("minSeg=0")]
    public void BadConfigurationStringsAreRejected(string text)
    {
        Assert.Null(SherpaDiarizationTuning.Parse(text));
    }
}
