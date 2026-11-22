using System.Buffers.Binary;
using Slovo.Core.Video;

namespace Slovo.Core.Tests.Video;

/// <summary>Reading Media Foundation's Annex B output into what an MP4 stores.</summary>
public sealed class AvcNalUnitsTests
{
    static readonly byte[] Sps = [0x67, 0x42, 0xC0, 0x1E, 0xDA, 0x02, 0x80];
    static readonly byte[] Pps = [0x68, 0xCE, 0x3C, 0x80];

    [Fact]
    public void SplitsOnThreeAndFourByteStartCodesAndLeavesTheZerosOfTheNextStartCodeOut()
    {
        byte[] stream = [0, 0, 0, 1, 0x09, 0xF0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 0, 1, 0x65, 0x88, 0x84];
        var units = AvcNalUnits.Split(stream).Select(unit => unit.ToArray()).ToList();
        Assert.Equal(3, units.Count);
        Assert.Equal(new byte[] { 0x09, 0xF0 }, units[0]);
        Assert.Equal(new byte[] { 0x67, 0x42 }, units[1]);
        Assert.Equal(new byte[] { 0x65, 0x88, 0x84 }, units[2]);
    }

    [Fact]
    public void NothingWithoutAStartCode()
    {
        Assert.Empty(AvcNalUnits.Split(new byte[] { 0x65, 0x88 }));
        Assert.Empty(AvcNalUnits.Split(Array.Empty<byte>()));
    }

    [Fact]
    public void AKeyFrameSampleKeepsItsSlicesLengthPrefixedAndLeavesParameterSetsToTheHeader()
    {
        var units = AvcNalUnits.Split(Stream([0x09, 0xF0], Sps, Pps, [0x06, 0x05, 0x01], [0x65, 0x88, 0x84, 0x21]));
        var sample = AvcNalUnits.Sample(units)!;
        Assert.True(sample.IsSync);
        // SEI and the IDR slice, each behind a 4-byte big-endian length.
        Assert.Equal(new byte[] { 0, 0, 0, 3, 0x06, 0x05, 0x01, 0, 0, 0, 4, 0x65, 0x88, 0x84, 0x21 }, sample.Data);
    }

    [Fact]
    public void AnOtherFrameIsNotASyncSample()
    {
        var sample = AvcNalUnits.Sample(AvcNalUnits.Split(Stream([0x41, 0x9A, 0x02])))!;
        Assert.False(sample.IsSync);
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(sample.Data));
    }

    [Fact]
    public void AnAccessUnitWithoutAPictureIsNoSample() =>
        Assert.Null(AvcNalUnits.Sample(AvcNalUnits.Split(Stream([0x09, 0xF0], Sps, Pps))));

    [Fact]
    public void TheDecoderConfigurationComesFromTheParameterSets()
    {
        var configuration = AvcDecoderConfiguration.From(AvcNalUnits.Split(Stream(Sps, Pps, [0x65, 0x88])))!;
        Assert.Equal(0x42, configuration.Profile);
        Assert.Equal(0xC0, configuration.Compatibility);
        Assert.Equal(0x1E, configuration.Level);
        byte[] expected = [1, 0x42, 0xC0, 0x1E, 0xFF, 0xE1, 0, (byte)Sps.Length, .. Sps, 1, 0, (byte)Pps.Length, .. Pps];
        Assert.Equal(expected, configuration.Record());
    }

    [Fact]
    public void HighProfileConfigurationsSayTheirChromaFormatAndBitDepth()
    {
        byte[] highSps = [0x67, 0x64, 0x00, 0x28, 0xAC];
        var record = AvcDecoderConfiguration.From(AvcNalUnits.Split(Stream(highSps, Pps)))!.Record();
        Assert.Equal(new byte[] { 0xFD, 0xF8, 0xF8, 0 }, record[^4..]);
    }

    [Fact]
    public void NoConfigurationUntilBothParameterSetsAreThere()
    {
        Assert.Null(AvcDecoderConfiguration.From(AvcNalUnits.Split(Stream(Sps, [0x65, 0x88]))));
        Assert.Null(AvcDecoderConfiguration.From(AvcNalUnits.Split(Stream(Pps))));
        // Too short to name a profile.
        Assert.Null(AvcDecoderConfiguration.From(AvcNalUnits.Split(Stream([0x67, 0x42], Pps))));
    }

    internal static byte[] Stream(params byte[][] units)
    {
        var bytes = new List<byte>();
        foreach (var unit in units)
        {
            bytes.AddRange([0, 0, 0, 1]);
            bytes.AddRange(unit);
        }
        return [.. bytes];
    }
}
