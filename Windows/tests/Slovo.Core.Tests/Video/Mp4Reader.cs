using System.Buffers.Binary;
using System.Text;

namespace Slovo.Core.Tests.Video;

/// <summary>One ISO BMFF box as the tests read it back.</summary>
sealed record Mp4Box(string Type, long Offset, long Size, byte[] File)
{
    /// <summary>Bytes after the 8-byte header.</summary>
    public ReadOnlySpan<byte> Body => File.AsSpan((int)Offset + 8, (int)Size - 8);

    public uint UInt32(int bodyOffset) => BinaryPrimitives.ReadUInt32BigEndian(Body[bodyOffset..]);
    public ulong UInt64(int bodyOffset) => BinaryPrimitives.ReadUInt64BigEndian(Body[bodyOffset..]);

    /// <summary>The boxes inside this one, starting <paramref name="skip"/> bytes into the body (a sample entry's fields, stsd's count).</summary>
    public List<Mp4Box> Children(int skip = 0) => Mp4Reader.Boxes(File, Offset + 8 + skip, Offset + Size);

    public Mp4Box Child(string type, int skip = 0) => Children(skip).Single(box => box.Type == type);
}

/// <summary>A minimal reader of the boxes FragmentedMp4Writer writes, to check the file from the outside.</summary>
static class Mp4Reader
{
    public static List<Mp4Box> Boxes(byte[] file, long start = 0, long? end = null)
    {
        var result = new List<Mp4Box>();
        var position = start;
        var limit = end ?? file.Length;
        while (position + 8 <= limit)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)position));
            var type = Encoding.ASCII.GetString(file, (int)position + 4, 4);
            if (size < 8 || position + size > limit)
            {
                // A box cut off by a crash: what came before it is all there is.
                break;
            }
            result.Add(new Mp4Box(type, position, size, file));
            position += size;
        }
        return result;
    }

    public static Mp4Box Path(byte[] file, params string[] path)
    {
        var box = Boxes(file).Single(b => b.Type == path[0]);
        foreach (var type in path.Skip(1))
        {
            box = box.Child(type);
        }
        return box;
    }

    /// <summary>One sample of a fragment, as its trun describes it.</summary>
    public sealed record Sample(long DecodeTime, uint Duration, uint Size, bool IsSync, byte[] Data);

    /// <summary>Every sample of every fragment, with its bytes found through the trun's data offset.</summary>
    public static List<Sample> Samples(byte[] file)
    {
        var samples = new List<Sample>();
        foreach (var moof in Boxes(file).Where(b => b.Type == "moof"))
        {
            var traf = moof.Child("traf");
            var tfdt = traf.Child("tfdt");
            var time = (long)tfdt.UInt64(4);
            var trun = traf.Child("trun");
            var count = (int)trun.UInt32(4);
            var dataOffset = (int)trun.UInt32(8);
            var position = (int)moof.Offset + dataOffset;
            for (var i = 0; i < count; i++)
            {
                var entry = 12 + i * 12;
                var duration = trun.UInt32(entry);
                var size = trun.UInt32(entry + 4);
                var flags = trun.UInt32(entry + 8);
                samples.Add(new Sample(time, duration, size, (flags & 0x0001_0000) == 0, file.AsSpan(position, (int)size).ToArray()));
                position += (int)size;
                time += duration;
            }
        }
        return samples;
    }

    /// <summary>Samples per fragment.</summary>
    public static List<int> FragmentSizes(byte[] file) =>
        Boxes(file).Where(b => b.Type == "moof").Select(moof => (int)moof.Child("traf").Child("trun").UInt32(4)).ToList();
}
