namespace Slovo.Core.Video;

/// <summary>H.264 NAL unit types the screen video cares about (ITU-T H.264 table 7-1).</summary>
public static class AvcNalType
{
    public const int NonIdrSlice = 1;
    public const int IdrSlice = 5;
    public const int Sei = 6;
    public const int SequenceParameterSet = 7;
    public const int PictureParameterSet = 8;
    public const int AccessUnitDelimiter = 9;
    public const int EndOfSequence = 10;
    public const int EndOfStream = 11;
    public const int FillerData = 12;

    public static int Of(ReadOnlySpan<byte> nal) => nal.IsEmpty ? -1 : nal[0] & 0x1F;
}

/// <summary>
/// One encoded picture as an MP4 sample wants it: the NAL units that belong in the sample,
/// each prefixed with its 4-byte big-endian length (ISO/IEC 14496-15 with
/// <c>lengthSizeMinusOne</c> 3), and whether it is a sync sample.
/// </summary>
public sealed record AvcSample(byte[] Data, bool IsSync);

/// <summary>
/// Reads H.264 Annex B byte streams, as Media Foundation's encoder produces them, into the
/// shapes an MP4 file stores: parameter sets for the <c>avcC</c> box, and length-prefixed
/// samples without the units that do not belong in them.
/// </summary>
public static class AvcNalUnits
{
    /// <summary>
    /// The NAL units of an Annex B stream, without their start codes. The trailing zero bytes
    /// before a start code belong to the next start code, not to the unit before it.
    /// </summary>
    public static List<ReadOnlyMemory<byte>> Split(ReadOnlyMemory<byte> annexB)
    {
        var units = new List<ReadOnlyMemory<byte>>();
        var span = annexB.Span;
        var start = -1;
        var i = 0;
        while (i + 2 < span.Length)
        {
            if (span[i] == 0 && span[i + 1] == 0 && span[i + 2] == 1)
            {
                if (start >= 0)
                {
                    units.Add(annexB[start..TrimTrailingZeros(span, start, i)]);
                }
                i += 3;
                start = i;
                continue;
            }
            i++;
        }
        if (start >= 0 && start < span.Length)
        {
            units.Add(annexB[start..TrimTrailingZeros(span, start, span.Length)]);
        }
        units.RemoveAll(unit => unit.IsEmpty);
        return units;
    }

    static int TrimTrailingZeros(ReadOnlySpan<byte> span, int start, int end)
    {
        while (end > start && span[end - 1] == 0)
        {
            end--;
        }
        return end;
    }

    /// <summary>
    /// The sample for one access unit, or null when it holds no picture. Parameter sets go to
    /// the <c>avcC</c> box instead (an <c>avc1</c> track keeps them out of the samples), and
    /// delimiters and filler data are left out.
    /// </summary>
    public static AvcSample? Sample(IEnumerable<ReadOnlyMemory<byte>> units)
    {
        using var buffer = new MemoryStream();
        var sync = false;
        var picture = false;
        Span<byte> length = stackalloc byte[4];
        foreach (var unit in units)
        {
            var type = AvcNalType.Of(unit.Span);
            switch (type)
            {
                case AvcNalType.SequenceParameterSet or AvcNalType.PictureParameterSet or AvcNalType.AccessUnitDelimiter
                    or AvcNalType.FillerData or AvcNalType.EndOfSequence or AvcNalType.EndOfStream or < 0:
                    continue;
                case AvcNalType.IdrSlice:
                    sync = true;
                    picture = true;
                    break;
                case >= AvcNalType.NonIdrSlice and < AvcNalType.IdrSlice:
                    picture = true;
                    break;
            }
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, unit.Length);
            buffer.Write(length);
            buffer.Write(unit.Span);
        }
        return picture ? new AvcSample(buffer.ToArray(), sync) : null;
    }
}

/// <summary>
/// The decoder configuration an <c>avc1</c> sample entry carries (<c>AVCDecoderConfigurationRecord</c>,
/// ISO/IEC 14496-15 5.3.3.1): one sequence and one picture parameter set.
/// </summary>
public sealed record AvcDecoderConfiguration(byte[] SequenceParameterSet, byte[] PictureParameterSet)
{
    /// <summary>
    /// The configuration from the parameter sets among <paramref name="units"/>, or null until
    /// both have been seen. A sequence parameter set shorter than 4 bytes cannot name a profile.
    /// </summary>
    public static AvcDecoderConfiguration? From(IEnumerable<ReadOnlyMemory<byte>> units)
    {
        byte[]? sps = null;
        byte[]? pps = null;
        foreach (var unit in units)
        {
            switch (AvcNalType.Of(unit.Span))
            {
                case AvcNalType.SequenceParameterSet when sps is null && unit.Length >= 4:
                    sps = unit.ToArray();
                    break;
                case AvcNalType.PictureParameterSet when pps is null && unit.Length >= 1:
                    pps = unit.ToArray();
                    break;
            }
        }
        return sps is not null && pps is not null ? new AvcDecoderConfiguration(sps, pps) : null;
    }

    public int Profile => SequenceParameterSet[1];
    public int Compatibility => SequenceParameterSet[2];
    public int Level => SequenceParameterSet[3];

    /// <summary>The body of the <c>avcC</c> box.</summary>
    public byte[] Record()
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte(1);
        buffer.WriteByte((byte)Profile);
        buffer.WriteByte((byte)Compatibility);
        buffer.WriteByte((byte)Level);
        // Six reserved bits set, then lengthSizeMinusOne = 3: 4-byte lengths.
        buffer.WriteByte(0xFF);
        // Three reserved bits set, then one sequence parameter set.
        buffer.WriteByte(0xE1);
        WriteSized(buffer, SequenceParameterSet);
        buffer.WriteByte(1);
        WriteSized(buffer, PictureParameterSet);
        if (Profile is 100 or 110 or 122 or 144)
        {
            // High profiles add chroma format and bit depths (4:2:0, 8 bits) and no extension sets.
            buffer.WriteByte(0xFC | 1);
            buffer.WriteByte(0xF8);
            buffer.WriteByte(0xF8);
            buffer.WriteByte(0);
        }
        return buffer.ToArray();
    }

    static void WriteSized(Stream stream, byte[] bytes)
    {
        stream.WriteByte((byte)(bytes.Length >> 8));
        stream.WriteByte((byte)bytes.Length);
        stream.Write(bytes);
    }

    public bool Equals(AvcDecoderConfiguration? other) =>
        other is not null && SequenceParameterSet.AsSpan().SequenceEqual(other.SequenceParameterSet)
        && PictureParameterSet.AsSpan().SequenceEqual(other.PictureParameterSet);

    public override int GetHashCode() => HashCode.Combine(SequenceParameterSet.Length, PictureParameterSet.Length);
}
