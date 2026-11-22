using System.Diagnostics;
using System.Text;
using Slovo.Core.Video;

namespace Slovo.Core.Tests.Video;

/// <summary>
/// The screen video file: a fragmented MP4 that plays after a crash up to its last fragment,
/// with its samples on the meeting's timeline. Read back box by box.
/// </summary>
public sealed class FragmentedMp4WriterTests : IDisposable
{
    const int Hz = FragmentedMp4Writer.Timescale;
    static readonly DateTimeOffset Created = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

    readonly string folder = Path.Combine(Path.GetTempPath(), "slovo-mp4-" + Guid.NewGuid().ToString("N"));

    public FragmentedMp4WriterTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    static AvcDecoderConfiguration Configuration() => new([0x67, 0x42, 0xC0, 0x1E, 0xDA, 0x02, 0x80], [0x68, 0xCE, 0x3C, 0x80]);

    static byte[] Picture(int number, int length = 20)
    {
        var bytes = new byte[length];
        bytes[0] = (byte)number;
        return bytes;
    }

    string File(string name) => Path.Combine(folder, name);

    [Fact]
    public void TheHeaderIsAFragmentedMovieWithOneVideoTrack()
    {
        var path = File("screen.mov");
        using (var writer = FragmentedMp4Writer.Create(path, 1920, 1080, Configuration(), Created))
        {
            writer.Finish(Hz / 4);
        }
        var bytes = System.IO.File.ReadAllBytes(path);
        var top = Mp4Reader.Boxes(bytes).Select(box => box.Type).ToList();
        Assert.Equal(["ftyp", "moov"], top);
        var ftyp = Mp4Reader.Path(bytes, "ftyp");
        Assert.Equal("iso5", Encoding.ASCII.GetString(ftyp.Body[..4]));
        Assert.Contains("isom", Encoding.ASCII.GetString(ftyp.Body[8..]));

        var moov = Mp4Reader.Path(bytes, "moov");
        Assert.Equal(["mvhd", "trak", "mvex"], moov.Children().Select(box => box.Type));
        var tkhd = Mp4Reader.Path(bytes, "moov", "trak", "tkhd");
        // Version 1: width and height, 16.16 fixed point, are the last eight bytes.
        Assert.Equal(1920u << 16, tkhd.UInt32(tkhd.Body.Length - 8));
        Assert.Equal(1080u << 16, tkhd.UInt32(tkhd.Body.Length - 4));
        var mdhd = Mp4Reader.Path(bytes, "moov", "trak", "mdia", "mdhd");
        Assert.Equal((uint)Hz, mdhd.UInt32(20));
        var hdlr = Mp4Reader.Path(bytes, "moov", "trak", "mdia", "hdlr");
        Assert.Equal("vide", Encoding.ASCII.GetString(hdlr.Body[8..12]));

        var stsd = Mp4Reader.Path(bytes, "moov", "trak", "mdia", "minf", "stbl", "stsd");
        var avc1 = stsd.Child("avc1", skip: 8);
        // The visual sample entry's fields take 78 bytes before its own boxes.
        Assert.Equal(["avcC", "colr", "pasp"], avc1.Children(skip: 78).Select(box => box.Type));
        Assert.Equal(Configuration().Record(), avc1.Child("avcC", skip: 78).Body.ToArray());
        Assert.Equal("nclx", Encoding.ASCII.GetString(avc1.Child("colr", skip: 78).Body[..4]));

        var trex = Mp4Reader.Path(bytes, "moov", "mvex", "trex");
        Assert.Equal(1u, trex.UInt32(4));
    }

    [Fact]
    public void SamplesFollowOneAnotherFromTheStartAndEachLastsUntilTheNext()
    {
        var path = File("screen.mov");
        using (var writer = FragmentedMp4Writer.Create(path, 64, 48, Configuration(), Created))
        {
            // The first picture came 0.3 s after the recording started; it stands from 0.
            Assert.True(writer.AddSample(Picture(1), FragmentedMp4Writer.Ticks(0.3), isSync: true));
            Assert.True(writer.AddSample(Picture(2), FragmentedMp4Writer.Ticks(0.55), isSync: false));
            // A pause: the last picture before it stays up until the next one.
            Assert.True(writer.AddSample(Picture(3), FragmentedMp4Writer.Ticks(5.55), isSync: false));
            writer.Finish(Hz / 4);
        }
        var samples = Mp4Reader.Samples(System.IO.File.ReadAllBytes(path));
        Assert.Equal([0L, FragmentedMp4Writer.Ticks(0.55), FragmentedMp4Writer.Ticks(5.55)], samples.Select(s => s.DecodeTime));
        Assert.Equal([(uint)FragmentedMp4Writer.Ticks(0.55), (uint)(FragmentedMp4Writer.Ticks(5.55) - FragmentedMp4Writer.Ticks(0.55)), (uint)(Hz / 4)], samples.Select(s => s.Duration));
        Assert.Equal([true, false, false], samples.Select(s => s.IsSync));
        Assert.Equal([1, 2, 3], samples.Select(s => (int)s.Data[0]));
    }

    [Fact]
    public void ATimeThatDoesNotMoveForwardIsRefused()
    {
        using var writer = new FragmentedMp4Writer(new MemoryStream(), 64, 48, Configuration(), Created);
        Assert.True(writer.AddSample(Picture(1), 1_000, true));
        Assert.True(writer.AddSample(Picture(2), 90_000, false));
        Assert.False(writer.AddSample(Picture(3), 90_000, false));
        Assert.False(writer.AddSample(Picture(4), 45_000, false));
        Assert.False(writer.AddSample([], 180_000, false));
        Assert.Equal(2, writer.AcceptedSamples);
    }

    [Fact]
    public void AFragmentEndsAtAKeyFrameOnceItHoldsASecondAndAtTwelveSecondsRegardless()
    {
        var stream = new MemoryStream();
        var writer = new FragmentedMp4Writer(stream, 64, 48, Configuration(), Created);
        var number = 0;
        void Add(double seconds, bool sync) => Assert.True(writer.AddSample(Picture(++number), FragmentedMp4Writer.Ticks(seconds), sync));
        // Four a second with a key frame every 10 s: one fragment per key frame.
        for (var t = 0.0; t < 20; t += 0.25)
        {
            Add(t, t % 10 == 0);
        }
        Assert.Equal(40, writer.WrittenSamples);
        // A key frame half a second into a fragment does not start another one.
        Add(20, true);
        Add(20.5, true);
        Assert.Equal(80, writer.WrittenSamples);
        // No key frame for longer than twelve seconds: the fragment is cut anyway.
        for (var t = 20.75; t <= 33; t += 0.25)
        {
            Add(t, false);
        }
        writer.Finish(Hz / 4);
        var bytes = stream.ToArray();
        var sizes = Mp4Reader.FragmentSizes(bytes);
        Assert.Equal(40, sizes[0]);
        Assert.Equal(40, sizes[1]);
        Assert.Equal(47, sizes[2]);
        var samples = Mp4Reader.Samples(bytes);
        Assert.Equal(number, samples.Count);
        Assert.False(samples[80 + 47].IsSync);
        // The fragments join up: each starts where the one before ended.
        var moofs = Mp4Reader.Boxes(bytes).Where(box => box.Type == "moof").ToList();
        Assert.Equal((ulong)FragmentedMp4Writer.Ticks(10), moofs[1].Child("traf").Child("tfdt").UInt64(4));
        Assert.Equal((ulong)FragmentedMp4Writer.Ticks(20), moofs[2].Child("traf").Child("tfdt").UInt64(4));
        Assert.Equal([1u, 2u, 3u, 4u], moofs.Select(moof => moof.Child("mfhd").UInt32(4)));
        writer.Dispose();
    }

    [Fact]
    public void AFinishedFileSaysItsLengthAndWhereToSeek()
    {
        var path = File("screen.mov");
        using (var writer = FragmentedMp4Writer.Create(path, 64, 48, Configuration(), Created))
        {
            for (var i = 0; i < 90; i++)
            {
                writer.AddSample(Picture(i), FragmentedMp4Writer.Ticks(i * 0.25), i % 40 == 0);
            }
            writer.Finish(Hz / 4);
            Assert.Equal(FragmentedMp4Writer.Ticks(22.5), writer.WrittenDuration);
        }
        var bytes = System.IO.File.ReadAllBytes(path);
        var mvex = Mp4Reader.Path(bytes, "moov", "mvex");
        var mehd = mvex.Child("mehd");
        Assert.Equal(22_500ul, mehd.UInt64(4));
        var mfra = Mp4Reader.Boxes(bytes).Last();
        Assert.Equal("mfra", mfra.Type);
        var tfra = mfra.Child("tfra");
        Assert.Equal(3u, tfra.UInt32(12));
        var moofOffsets = Mp4Reader.Boxes(bytes).Where(box => box.Type == "moof").Select(box => (ulong)box.Offset).ToList();
        Assert.Equal(moofOffsets[1], tfra.UInt64(16 + 19 + 8));
        var mfro = mfra.Child("mfro");
        Assert.Equal((uint)mfra.Size, mfro.UInt32(4));
    }

    [Fact]
    public void AFileThatWasNeverFinishedKeepsEveryFragmentWrittenAndNoLength()
    {
        var path = File("screen.mov");
        var writer = FragmentedMp4Writer.Create(path, 64, 48, Configuration(), Created);
        for (var i = 0; i < 50; i++)
        {
            writer.AddSample(Picture(i), FragmentedMp4Writer.Ticks(i * 0.25), i % 40 == 0);
        }
        // As a crash leaves it: the file handle goes, the pending ten samples with it.
        writer.Dispose();
        var bytes = System.IO.File.ReadAllBytes(path);
        Assert.Equal(40, Mp4Reader.Samples(bytes).Count);
        Assert.Equal("free", Mp4Reader.Path(bytes, "moov", "mvex").Children()[0].Type);
        Assert.DoesNotContain(Mp4Reader.Boxes(bytes), box => box.Type == "mfra");
        // A fragment cut off mid-write reads as the end of the file.
        var torn = bytes.Concat(bytes[^100..]).ToArray();
        Assert.Equal(40, Mp4Reader.Samples(torn).Count);
    }

    [Fact]
    public void WritesARealH264StreamSampleForSample()
    {
        var (bytes, count) = MuxFixture(File("screen.mov"));
        var samples = Mp4Reader.Samples(bytes);
        Assert.Equal(12, count);
        Assert.Equal(12, samples.Count);
        Assert.Equal([0, 8], samples.Select((sample, index) => (sample, index)).Where(p => p.sample.IsSync).Select(p => p.index));
        var avcC = Mp4Reader.Path(bytes, "moov", "trak", "mdia", "minf", "stbl", "stsd").Child("avc1", skip: 8).Child("avcC", skip: 78);
        // x264's baseline profile.
        Assert.Equal(66, avcC.Body[1]);
    }

    /// <summary>
    /// On a developer's Mac with FFmpeg installed, FFmpeg decodes every picture of the file; the
    /// macOS app's AVFoundation was checked the same way by hand (docs/WINDOWS.md).
    /// </summary>
    [Fact]
    public void FFmpegDecodesTheFileWhenItIsInstalled()
    {
        var ffprobe = new[] { "/opt/homebrew/bin/ffprobe", "/usr/local/bin/ffprobe", "/usr/bin/ffprobe" }.FirstOrDefault(System.IO.File.Exists);
        if (ffprobe is null)
        {
            Assert.Skip("ffprobe is not installed");
        }
        var path = File("screen.mov");
        MuxFixture(path);
        var info = new ProcessStartInfo(ffprobe)
        {
            ArgumentList = { "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height,nb_read_frames", "-of", "csv=p=0", path },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        var errors = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.Equal("", errors);
        Assert.Equal("h264,64,48,12", output);
    }

    static (byte[] Bytes, int Count) MuxFixture(string path)
    {
        var stream = System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Video", "Fixtures", "testsrc-64x48.h264"));
        var units = AvcNalUnits.Split(stream);
        var accessUnits = new List<List<ReadOnlyMemory<byte>>>();
        foreach (var unit in units)
        {
            if (AvcNalType.Of(unit.Span) == AvcNalType.AccessUnitDelimiter || accessUnits.Count == 0)
            {
                accessUnits.Add([]);
            }
            accessUnits[^1].Add(unit);
        }
        var configuration = AvcDecoderConfiguration.From(units)!;
        var count = 0;
        using (var writer = FragmentedMp4Writer.Create(path, 64, 48, configuration, Created))
        {
            foreach (var accessUnit in accessUnits)
            {
                if (AvcNalUnits.Sample(accessUnit) is { } sample)
                {
                    writer.AddSample(sample.Data, FragmentedMp4Writer.Ticks(count++ * 0.25), sample.IsSync);
                }
            }
            writer.Finish(Hz / 4);
        }
        return (System.IO.File.ReadAllBytes(path), count);
    }
}
