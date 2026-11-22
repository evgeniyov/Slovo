using Slovo.Core.Audio;

namespace Slovo.Core.Tests.Audio;

public class LevelMeterTests
{
    [Fact]
    public void MeterReportsAboutThirtyTimesASecond()
    {
        var meter = new LevelMeter();
        Assert.Equal(533, meter.WindowLength);
        Assert.Null(meter.Consume(Enumerable.Repeat(0.5f, 100).ToArray()));
        Assert.Equal(0f, meter.Consume(new float[2_000]));
        var loud = Enumerable.Range(0, 1_600).Select(i => (float)Math.Sin(2 * Math.PI * 400 * i / 16_000)).ToArray();
        Assert.Equal(0.94, meter.Consume(loud)!.Value, 0.02);
        Assert.Equal(0f, LevelMeter.MeetingLevel(0.001f));
        Assert.Equal(1f, LevelMeter.MeetingLevel(1));
    }

    [Fact]
    public void RmsAndDictationLevel()
    {
        Assert.Equal(MathF.Sqrt(12.5f), LevelMeter.Rms([3f, 4f]), 4);
        Assert.Equal(0f, LevelMeter.Rms([]));
        Assert.Equal(0f, LevelMeter.DictationLevel(0));
        Assert.Equal(1f, LevelMeter.DictationLevel(1));
        Assert.Equal(0f, LevelMeter.DictationLevel(0.00001f));
        Assert.Equal(0.5, LevelMeter.DictationLevel((float)Math.Pow(10, -35.0 / 20)), 3);
    }

    [Fact]
    public void LevelRisesFasterThanItFalls()
    {
        var rising = LevelMeter.Smoothed(0, 1);
        var falling = 1 - LevelMeter.Smoothed(1, 0);
        Assert.True(rising > falling);
        Assert.Equal(0.5f, LevelMeter.Smoothed(0.5f, 0.5f));
    }
}

public class AudioMixdownTests
{
    static byte[] Floats(params float[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    [Fact]
    public void StereoFloatIsAveraged()
    {
        var mono = AudioMixdown.ToMono(Floats(0.2f, 0.4f, 0.6f, 0.8f), new PcmLayout(PcmSampleType.Float, 32, 2));
        Assert.Equal(2, mono.Length);
        Assert.Equal(0.3f, mono[0], 6);
        Assert.Equal(0.7f, mono[1], 6);
        Assert.Equal([0.3f, 0.7f], AudioMixdown.ToMono([0.2f, 0.4f, 0.6f, 0.8f], 2), (a, b) => Math.Abs(a - b) < 1e-6);
    }

    [Fact]
    public void IntegerSamplesAreScaledToPlusMinusOne()
    {
        var sixteen = new byte[] { 0x00, 0x40, 0x00, 0xC0 };
        Assert.Equal([0.5f, -0.5f], AudioMixdown.ToMono(sixteen, new PcmLayout(PcmSampleType.Integer, 16, 1)));

        var twentyFour = new byte[] { 0x00, 0x00, 0x40, 0x00, 0x00, 0xC0 };
        Assert.Equal([0.5f, -0.5f], AudioMixdown.ToMono(twentyFour, new PcmLayout(PcmSampleType.Integer, 24, 1)));

        var thirtyTwo = BitConverter.GetBytes(int.MinValue).Concat(BitConverter.GetBytes(1 << 30)).ToArray();
        Assert.Equal([-1f, 0.5f], AudioMixdown.ToMono(thirtyTwo, new PcmLayout(PcmSampleType.Integer, 32, 1)));

        var eight = new byte[] { 128, 192, 0 };
        Assert.Equal([0f, 0.5f, -1f], AudioMixdown.ToMono(eight, new PcmLayout(PcmSampleType.Integer, 8, 1)));

        var doubles = BitConverter.GetBytes(0.25).Concat(BitConverter.GetBytes(-0.75)).ToArray();
        Assert.Equal([0.25f, -0.75f], AudioMixdown.ToMono(doubles, new PcmLayout(PcmSampleType.Float, 64, 1)));
    }

    [Fact]
    public void ATrailingPartialFrameIsIgnored()
    {
        var data = Floats(0.1f, 0.3f, 0.5f);
        Assert.Single(AudioMixdown.ToMono(data, new PcmLayout(PcmSampleType.Float, 32, 2)));
    }

    [Fact]
    public void UnsupportedLayoutsAreRefused()
    {
        Assert.False(new PcmLayout(PcmSampleType.Float, 16, 2).IsSupported);
        Assert.False(new PcmLayout(PcmSampleType.Integer, 12, 1).IsSupported);
        Assert.False(new PcmLayout(PcmSampleType.Integer, 16, 0).IsSupported);
        Assert.Throws<ArgumentException>(() => AudioMixdown.ToMono(new byte[4], new PcmLayout(PcmSampleType.Float, 16, 1)));
    }
}
