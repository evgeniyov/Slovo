using System.Buffers.Binary;
using System.Text;

namespace Slovo.Core.Video;

/// <summary>
/// Writes one H.264 video track as a fragmented MP4 (ISO/IEC 14496-12 with movie fragments):
/// <c>ftyp</c> and a <c>moov</c> without samples first, then a <c>moof</c> + <c>mdat</c> pair
/// every few seconds, flushed to disk as it is written. It is the Windows counterpart of the
/// macOS <c>AVAssetWriter</c> with <c>movieFragmentInterval</c>: a crash, a force-quit or a
/// failed write leaves a file that plays up to the last fragment written, where an ordinary
/// MP4 would be unreadable without the index written at the very end.
/// </summary>
/// <remarks>
/// <para>
/// Samples are placed on the track's own timeline in <see cref="Timescale"/> units. The first
/// fragment starts at 0 and every sample lasts until the next one starts, so the picture shown
/// at any moment is the last one captured before it — also across a pause, whose last picture
/// simply stays up. A fragment is written once the sample after it is known, because its last
/// sample's length is only known then.
/// </para>
/// <para>
/// A finished file also gets the total length (<c>mehd</c>, written into space a <c>free</c> box
/// holds from the start) and a random-access index (<c>mfra</c>) at the end, so players show the
/// length and seek without scanning every fragment. An unfinished one lacks both and still plays.
/// </para>
/// <para>Not thread-safe: the owner serializes calls.</para>
/// </remarks>
public sealed class FragmentedMp4Writer : IDisposable
{
    /// <summary>Ticks per second of the video track: the usual 90 kHz of MPEG video.</summary>
    public const int Timescale = 90_000;
    /// <summary>Ticks per second of the movie header: milliseconds.</summary>
    public const int MovieTimescale = 1_000;
    /// <summary>A fragment is closed at a sync sample once it is at least this long.</summary>
    public const double MinimumFragmentSeconds = 1;
    /// <summary>A fragment is closed at any sample once it is this long, key frame or not, so no more than this is ever at risk.</summary>
    public const double MaximumFragmentSeconds = 12;

    const uint TrackId = 1;
    /// <summary>Sync sample: depends on no other (sample_depends_on = 2).</summary>
    const uint SyncSampleFlags = 0x0200_0000;
    /// <summary>Depends on others (sample_depends_on = 1) and is not a sync sample.</summary>
    const uint NonSyncSampleFlags = 0x0101_0000;
    const int MehdSize = 20;

    static readonly DateTimeOffset Epoch1904 = new(1904, 1, 1, 0, 0, 0, TimeSpan.Zero);

    readonly Stream stream;
    readonly bool ownsStream;
    readonly bool flushToDisk;
    readonly long mehdOffset;
    readonly List<PendingSample> pending = [];
    readonly MemoryStream pendingData = new();
    readonly List<(ulong Time, ulong MoofOffset)> randomAccess = [];
    uint sequenceNumber;
    long? lastTime;
    bool finished;

    readonly record struct PendingSample(long Time, int Offset, int Size, bool IsSync);

    /// <summary>Writes the file header to <paramref name="stream"/>, which must be writable and seekable.</summary>
    public FragmentedMp4Writer(Stream stream, int width, int height, AvcDecoderConfiguration configuration,
                               DateTimeOffset creationTime, bool ownsStream = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);
        this.stream = stream;
        this.ownsStream = ownsStream;
        flushToDisk = stream is FileStream;
        Width = width;
        Height = height;
        var header = new BoxWriter();
        WriteFileType(header);
        var start = stream.Position;
        mehdOffset = start + WriteMovie(header, width, height, configuration, creationTime);
        stream.Write(header.ToArray());
        Flush();
    }

    /// <summary>Creates (or replaces) the file at <paramref name="path"/>; readers may open it while it is written.</summary>
    public static FragmentedMp4Writer Create(string path, int width, int height, AvcDecoderConfiguration configuration, DateTimeOffset creationTime)
    {
        var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete, 64 * 1024);
        try
        {
            return new FragmentedMp4Writer(file, width, height, configuration, creationTime, ownsStream: true);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Samples written to disk so far, in fragments that are complete.</summary>
    public long WrittenSamples { get; private set; }

    /// <summary>Every sample accepted so far, those still waiting for their fragment included.</summary>
    public long AcceptedSamples => WrittenSamples + pending.Count;

    /// <summary>Converts seconds to track ticks.</summary>
    public static long Ticks(double seconds) =>
        double.IsFinite(seconds) ? (long)Math.Round(Math.Max(0, seconds) * Timescale) : 0;

    /// <summary>
    /// Adds a sample shown from <paramref name="time"/> track ticks on. A time not after the
    /// previous sample's is refused (false): the track's timeline only moves forward. The
    /// fragment before is written first when this sample closes it.
    /// </summary>
    public bool AddSample(ReadOnlySpan<byte> data, long time, bool isSync)
    {
        ObjectDisposedException.ThrowIf(finished, this);
        if (data.IsEmpty)
        {
            return false;
        }
        if (lastTime is { } previous)
        {
            if (time <= previous)
            {
                return false;
            }
        }
        else
        {
            // The first picture stands from the start of the recording on.
            time = 0;
        }
        if (pending.Count > 0)
        {
            var length = time - pending[0].Time;
            if ((isSync && length >= Ticks(MinimumFragmentSeconds)) || length >= Ticks(MaximumFragmentSeconds))
            {
                WriteFragment(time);
            }
        }
        pending.Add(new PendingSample(time, (int)pendingData.Length, data.Length, isSync));
        pendingData.Write(data);
        lastTime = time;
        return true;
    }

    /// <summary>
    /// Writes what is pending, the random-access index and the total length, and closes the
    /// file. The last sample lasts <paramref name="lastSampleDuration"/> ticks (at least one).
    /// </summary>
    public void Finish(long lastSampleDuration)
    {
        if (finished)
        {
            return;
        }
        try
        {
            if (pending.Count > 0)
            {
                WriteFragment(pending[^1].Time + Math.Max(1, lastSampleDuration));
            }
            if (WrittenSamples > 0)
            {
                WriteRandomAccessIndex();
                PatchMovieLength();
            }
            Flush();
        }
        finally
        {
            finished = true;
            if (ownsStream)
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>The end of the last written fragment, in ticks.</summary>
    public long WrittenDuration { get; private set; }

    public void Dispose()
    {
        if (!finished)
        {
            finished = true;
            if (ownsStream)
            {
                stream.Dispose();
            }
        }
        pendingData.Dispose();
    }

    // MARK: - Fragments

    void WriteFragment(long end)
    {
        var count = pending.Count;
        var moofSize = 88 + 12 * count;
        var box = new BoxWriter();
        var moofOffset = stream.Position;
        box.Begin("moof");
        box.Begin("mfhd");
        box.FullHeader(0, 0);
        box.UInt32(++sequenceNumber);
        box.End();
        box.Begin("traf");
        box.Begin("tfhd");
        // default-base-is-moof: data offsets count from the start of this moof.
        box.FullHeader(0, 0x020000);
        box.UInt32(TrackId);
        box.End();
        box.Begin("tfdt");
        box.FullHeader(1, 0);
        box.UInt64((ulong)pending[0].Time);
        box.End();
        box.Begin("trun");
        // data-offset, sample-duration, sample-size and sample-flags present.
        box.FullHeader(0, 0x000001 | 0x000100 | 0x000200 | 0x000400);
        box.UInt32((uint)count);
        box.UInt32((uint)(moofSize + 8));
        for (var i = 0; i < count; i++)
        {
            var sample = pending[i];
            var next = i + 1 < count ? pending[i + 1].Time : end;
            box.UInt32((uint)Math.Clamp(next - sample.Time, 1, uint.MaxValue));
            box.UInt32((uint)sample.Size);
            box.UInt32(sample.IsSync ? SyncSampleFlags : NonSyncSampleFlags);
        }
        box.End();
        box.End();
        box.End();
        if (box.Position != moofSize)
        {
            throw new InvalidOperationException("moof size mismatch");
        }
        var payload = pendingData.GetBuffer().AsSpan(0, (int)pendingData.Length);
        box.UInt32((uint)(payload.Length + 8));
        box.FourCC("mdat");
        stream.Write(box.ToArray());
        stream.Write(payload);
        Flush();

        var firstSync = pending.FindIndex(sample => sample.IsSync);
        if (firstSync == 0)
        {
            randomAccess.Add(((ulong)pending[0].Time, (ulong)moofOffset));
        }
        WrittenSamples += count;
        WrittenDuration = end;
        pending.Clear();
        pendingData.SetLength(0);
    }

    void Flush()
    {
        if (flushToDisk && stream is FileStream file)
        {
            file.Flush(flushToDisk: true);
        }
        else
        {
            stream.Flush();
        }
    }

    void WriteRandomAccessIndex()
    {
        var box = new BoxWriter();
        box.Begin("mfra");
        box.Begin("tfra");
        box.FullHeader(1, 0);
        box.UInt32(TrackId);
        // Reserved bits, then 1-byte traf, trun and sample numbers.
        box.UInt32(0);
        box.UInt32((uint)randomAccess.Count);
        foreach (var (time, offset) in randomAccess)
        {
            box.UInt64(time);
            box.UInt64(offset);
            box.Byte(1);
            box.Byte(1);
            box.Byte(1);
        }
        box.End();
        box.Begin("mfro");
        box.FullHeader(0, 0);
        var mfraSize = box.Position + 4;
        box.UInt32((uint)mfraSize);
        box.End();
        box.End();
        stream.Write(box.ToArray());
    }

    /// <summary>Turns the <c>free</c> box that held the place into a <c>mehd</c> with the total length.</summary>
    void PatchMovieLength()
    {
        var box = new BoxWriter();
        box.Begin("mehd");
        box.FullHeader(1, 0);
        box.UInt64((ulong)Math.Round(WrittenDuration * (double)MovieTimescale / Timescale));
        box.End();
        var end = stream.Position;
        stream.Position = mehdOffset;
        stream.Write(box.ToArray());
        stream.Position = end;
    }

    // MARK: - Header

    static void WriteFileType(BoxWriter box)
    {
        box.Begin("ftyp");
        box.FourCC("iso5");
        box.UInt32(512);
        foreach (var brand in new[] { "iso5", "iso6", "isom", "avc1", "mp41" })
        {
            box.FourCC(brand);
        }
        box.End();
    }

    /// <summary>Writes the <c>moov</c> box; returns where in it the length placeholder starts.</summary>
    static long WriteMovie(BoxWriter box, int width, int height, AvcDecoderConfiguration configuration, DateTimeOffset creationTime)
    {
        var seconds = (ulong)Math.Max(0, (creationTime - Epoch1904).TotalSeconds);
        box.Begin("moov");

        box.Begin("mvhd");
        box.FullHeader(1, 0);
        box.UInt64(seconds);
        box.UInt64(seconds);
        box.UInt32(MovieTimescale);
        box.UInt64(0);
        box.UInt32(0x0001_0000); // rate 1.0
        box.UInt16(0x0100); // volume 1.0
        box.Zeros(2 + 8);
        WriteMatrix(box);
        box.Zeros(24);
        box.UInt32(TrackId + 1);
        box.End();

        box.Begin("trak");
        box.Begin("tkhd");
        // Enabled, in the movie and in its preview.
        box.FullHeader(1, 0x000007);
        box.UInt64(seconds);
        box.UInt64(seconds);
        box.UInt32(TrackId);
        box.Zeros(4);
        box.UInt64(0);
        box.Zeros(8);
        box.UInt16(0); // layer
        box.UInt16(0); // alternate group
        box.UInt16(0); // volume: a video track is silent
        box.Zeros(2);
        WriteMatrix(box);
        box.UInt32((uint)width << 16);
        box.UInt32((uint)height << 16);
        box.End();

        box.Begin("mdia");
        box.Begin("mdhd");
        box.FullHeader(1, 0);
        box.UInt64(seconds);
        box.UInt64(seconds);
        box.UInt32(Timescale);
        box.UInt64(0);
        box.UInt16(0x55C4); // "und"
        box.UInt16(0);
        box.End();

        box.Begin("hdlr");
        box.FullHeader(0, 0);
        box.UInt32(0);
        box.FourCC("vide");
        box.Zeros(12);
        box.Bytes(Encoding.ASCII.GetBytes("VideoHandler\0"));
        box.End();

        box.Begin("minf");
        box.Begin("vmhd");
        box.FullHeader(0, 1);
        box.Zeros(8);
        box.End();
        box.Begin("dinf");
        box.Begin("dref");
        box.FullHeader(0, 0);
        box.UInt32(1);
        box.Begin("url ");
        box.FullHeader(0, 1); // the media is in this file
        box.End();
        box.End();
        box.End();

        box.Begin("stbl");
        box.Begin("stsd");
        box.FullHeader(0, 0);
        box.UInt32(1);
        WriteSampleEntry(box, width, height, configuration);
        box.End();
        foreach (var empty in new[] { "stts", "stsc", "stco" })
        {
            box.Begin(empty);
            box.FullHeader(0, 0);
            box.UInt32(0);
            box.End();
        }
        box.Begin("stsz");
        box.FullHeader(0, 0);
        box.UInt32(0);
        box.UInt32(0);
        box.End();
        box.End(); // stbl
        box.End(); // minf
        box.End(); // mdia
        box.End(); // trak

        box.Begin("mvex");
        // Room for the total length, written once the file is finished.
        var mehdOffset = box.Position;
        box.UInt32(MehdSize);
        box.FourCC("free");
        box.Zeros(MehdSize - 8);
        box.Begin("trex");
        box.FullHeader(0, 0);
        box.UInt32(TrackId);
        box.UInt32(1);
        box.UInt32(0);
        box.UInt32(0);
        box.UInt32(0);
        box.End();
        box.End(); // mvex

        box.End(); // moov
        return mehdOffset;
    }

    static void WriteSampleEntry(BoxWriter box, int width, int height, AvcDecoderConfiguration configuration)
    {
        box.Begin("avc1");
        box.Zeros(6);
        box.UInt16(1); // data reference index
        box.Zeros(16);
        box.UInt16((ushort)width);
        box.UInt16((ushort)height);
        box.UInt32(0x0048_0000); // 72 dpi
        box.UInt32(0x0048_0000);
        box.Zeros(4);
        box.UInt16(1); // one frame per sample
        var name = Encoding.ASCII.GetBytes("Slovo screen");
        box.Byte((byte)name.Length);
        box.Bytes(name);
        box.Zeros(31 - name.Length);
        box.UInt16(0x0018); // colour, no alpha
        box.UInt16(0xFFFF);

        box.Begin("avcC");
        box.Bytes(configuration.Record());
        box.End();

        // BT.709 primaries, transfer and matrix, limited range: what the NV12 conversion produces.
        box.Begin("colr");
        box.FourCC("nclx");
        box.UInt16(1);
        box.UInt16(1);
        box.UInt16(1);
        box.Byte(0);
        box.End();

        box.Begin("pasp");
        box.UInt32(1);
        box.UInt32(1);
        box.End();

        box.End();
    }

    static void WriteMatrix(BoxWriter box)
    {
        foreach (var value in new uint[] { 0x0001_0000, 0, 0, 0, 0x0001_0000, 0, 0, 0, 0x4000_0000 })
        {
            box.UInt32(value);
        }
    }

    /// <summary>Builds boxes in memory, filling in each box's size when it ends.</summary>
    sealed class BoxWriter
    {
        readonly MemoryStream buffer = new();
        readonly Stack<long> starts = new();

        public long Position => buffer.Position;

        public void Begin(string type)
        {
            starts.Push(buffer.Position);
            UInt32(0);
            FourCC(type);
        }

        public void End()
        {
            var start = starts.Pop();
            var end = buffer.Position;
            buffer.Position = start;
            UInt32((uint)(end - start));
            buffer.Position = end;
        }

        public void FullHeader(byte version, uint flags) => UInt32(((uint)version << 24) | (flags & 0xFF_FFFF));

        public void FourCC(string code) => Bytes(Encoding.ASCII.GetBytes(code));

        public void Byte(byte value) => buffer.WriteByte(value);

        public void UInt16(ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            buffer.Write(bytes);
        }

        public void UInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            buffer.Write(bytes);
        }

        public void UInt64(ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            buffer.Write(bytes);
        }

        public void Zeros(int count)
        {
            for (var i = 0; i < count; i++)
            {
                buffer.WriteByte(0);
            }
        }

        public void Bytes(ReadOnlySpan<byte> bytes) => buffer.Write(bytes);

        public byte[] ToArray() => buffer.ToArray();
    }
}
