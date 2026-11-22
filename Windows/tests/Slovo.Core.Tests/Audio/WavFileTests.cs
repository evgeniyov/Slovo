using Slovo.Core.Audio;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Audio;

public class WavFileTests
{
    [Fact]
    public void RoundTripsSixteenBitMono()
    {
        var samples = Enumerable.Range(0, 1600).Select(i => (float)Math.Sin(i * 0.1) * 0.5f).ToArray();
        var decoded = WavFile.Decode(WavFile.Encode(new RecordedAudio(samples)))!;
        Assert.Equal(16_000, decoded.SampleRate);
        Assert.Equal(samples.Length, decoded.Samples.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.InRange(decoded.Samples[i] - samples[i], -0.0001f, 0.0001f);
        }
    }

    [Fact]
    public void ClampsAndSilencesBadSamples()
    {
        var decoded = WavFile.Decode(WavFile.Encode(new RecordedAudio([2f, -3f, float.NaN])))!;
        Assert.Equal(32767 / 32768f, decoded.Samples[0], 4);
        Assert.Equal(-32767 / 32768f, decoded.Samples[1], 4);
        Assert.Equal(0, decoded.Samples[2]);
    }

    [Fact]
    public void RejectsWhatIsNotWav()
    {
        Assert.Null(WavFile.Decode("not a wav file at all"u8));
        Assert.Throws<SlovoException>(() => WavFile.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav")));
    }

    [Fact]
    public void RelativePathUsesTheLocalDayAndUpperCaseId()
    {
        var id = Guid.Parse("e621e1f8-c36c-495a-93fc-0c247a3e6e5f");
        var date = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 15)));
        Assert.Equal("2026-09-15/E621E1F8-C36C-495A-93FC-0C247A3E6E5F.wav", WavFile.RelativePath(id, date));
    }
}
