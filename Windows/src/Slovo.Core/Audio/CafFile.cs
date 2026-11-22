using System.Buffers.Binary;
using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Core.Audio;

/// <summary>
/// The <c>desc</c> chunk of a CAF file (Apple's <c>CAFAudioDescription</c>): what one frame of
/// the <c>data</c> chunk holds.
/// </summary>
public sealed record CafFormat(double SampleRate, string FormatId, uint FormatFlags, uint BytesPerPacket, uint FramesPerPacket,
                               uint ChannelsPerFrame, uint BitsPerChannel)
{
    /// <summary><c>kCAFLinearPCMFormatFlagIsFloat</c>.</summary>
    public const uint FloatFlag = 1;
    /// <summary><c>kCAFLinearPCMFormatFlagIsLittleEndian</c>.</summary>
    public const uint LittleEndianFlag = 2;

    /// <summary>What meeting tracks are written in: 16 kHz mono 16-bit little-endian signed integers.</summary>
    public static readonly CafFormat MeetingTrack =
        new(MeetingAudioFormat.SampleRate, "lpcm", LittleEndianFlag, MeetingAudioFormat.BytesPerFrame, 1, MeetingAudioFormat.Channels,
            MeetingAudioFormat.BitsPerSample);

    public bool IsLinearPcm => FormatId == "lpcm";
    public bool IsFloat => (FormatFlags & FloatFlag) != 0;
    public bool IsLittleEndian => (FormatFlags & LittleEndianFlag) != 0;
    public int BytesPerFrame => (int)BytesPerPacket;
}

/// <summary>
/// Meeting tracks on disk: Core Audio Format files exactly like the ones the macOS app writes
/// with <c>AVAudioFile</c>, so a meeting folder copied from one computer plays and transcribes
/// on the other.
///
/// The layout <see cref="CafWriter"/> produces is AVAudioFile's: the <c>caff</c> file header, a
/// <c>desc</c> chunk, a <c>free</c> chunk that pads the header to 4 KB, then a <c>data</c> chunk
/// whose samples start at byte 4096. While recording the <c>data</c> chunk's size is -1 ("to
/// the end of the file") and its edit count 0, so a crash leaves a file every reader can
/// still open; closing writes the real size and edit count 1. All header numbers are
/// big-endian; the samples are 16-bit little-endian signed integers.
/// </summary>
public static class CafFile
{
    /// <summary>Where AVAudioFile, and <see cref="CafWriter"/>, put the first sample.</summary>
    public const int DataOffset = 4096;
    /// <summary>The size a data chunk has while it is still being written.</summary>
    public const long UnknownSize = -1;

    internal const int FileHeaderBytes = 8;
    internal const int ChunkHeaderBytes = 12;
    internal const int DescriptionBytes = 32;
    internal const int EditCountBytes = 4;

    /// <summary>True when the file starts like a CAF file (<c>caff</c>, version 1).</summary>
    public static bool IsCaf(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[FileHeaderBytes];
            return stream.ReadAtLeast(header, FileHeaderBytes, throwOnEndOfStream: false) == FileHeaderBytes
                   && header[..4].SequenceEqual("caff"u8) && BinaryPrimitives.ReadUInt16BigEndian(header[4..]) == 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Starts a meeting track at <paramref name="path"/>, replacing any file there. Throws SlovoException.</summary>
    public static CafWriter Create(string path) => CafWriter.Create(path);

    /// <summary>Opens a CAF file for reading. Throws SlovoException.</summary>
    public static CafReader Open(string path) => CafReader.Open(path);

    /// <summary>Length of a CAF file in seconds, or zero when it is missing or cannot be read (<c>MeetingClip.duration</c>).</summary>
    public static double Duration(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }
        try
        {
            using var reader = CafReader.Open(path);
            return reader.Duration;
        }
        catch (SlovoException)
        {
            return 0;
        }
    }

    /// <summary>The whole file as mono samples at its own rate. Throws SlovoException.</summary>
    public static RecordedAudio ReadAll(string path)
    {
        using var reader = CafReader.Open(path);
        return new RecordedAudio(reader.ReadAll(), reader.Format.SampleRate);
    }

    /// <summary>The header AVAudioFile writes, up to and including the data chunk's edit count.</summary>
    internal static byte[] Header(CafFormat format, long dataChunkSize, uint editCount)
    {
        var header = new byte[DataOffset];
        var span = header.AsSpan();
        "caff"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], 0);

        var offset = FileHeaderBytes;
        WriteChunkHeader(span[offset..], "desc"u8, DescriptionBytes);
        offset += ChunkHeaderBytes;
        BinaryPrimitives.WriteDoubleBigEndian(span[offset..], format.SampleRate);
        System.Text.Encoding.ASCII.GetBytes(format.FormatId.PadRight(4).AsSpan(0, 4), span[(offset + 8)..(offset + 12)]);
        BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 12)..], format.FormatFlags);
        BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 16)..], format.BytesPerPacket);
        BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 20)..], format.FramesPerPacket);
        BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 24)..], format.ChannelsPerFrame);
        BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 28)..], format.BitsPerChannel);
        offset += DescriptionBytes;

        // The free chunk fills the rest of the first 4 KB, so the samples start on a page.
        var dataHeaderOffset = DataOffset - ChunkHeaderBytes - EditCountBytes;
        WriteChunkHeader(span[offset..], "free"u8, dataHeaderOffset - offset - ChunkHeaderBytes);

        WriteChunkHeader(span[dataHeaderOffset..], "data"u8, dataChunkSize);
        BinaryPrimitives.WriteUInt32BigEndian(span[(dataHeaderOffset + ChunkHeaderBytes)..], editCount);
        return header;
    }

    /// <summary>The offset of the data chunk's 64-bit size in a file <see cref="CafWriter"/> wrote.</summary>
    internal const int DataSizeOffset = DataOffset - EditCountBytes - 8;

    static void WriteChunkHeader(Span<byte> span, ReadOnlySpan<byte> type, long size)
    {
        type.CopyTo(span);
        BinaryPrimitives.WriteInt64BigEndian(span[4..], size);
    }
}

/// <summary>
/// Appends 16 kHz mono samples to a meeting track (the file half of the macOS
/// <c>MeetingTrackWriter</c>; timing, gap filling and threading stay with the recorder). Not
/// thread-safe: one writer thread at a time. Readers may open the file while it grows.
/// </summary>
public sealed class CafWriter : IDisposable
{
    const int SilenceChunkFrames = MeetingAudioFormat.SampleRate;

    readonly FileStream stream;
    byte[] buffer = new byte[MeetingAudioFormat.SampleRate * MeetingAudioFormat.BytesPerFrame];
    bool closed;

    CafWriter(string path, FileStream stream)
    {
        Path = path;
        this.stream = stream;
    }

    public string Path { get; }

    /// <summary>Frames written so far, silence included.</summary>
    public long FrameCount { get; private set; }

    /// <summary>
    /// Whether appended samples are written as silence. Each append keeps its length, so a
    /// muted stretch holds its place on the timeline and the other track stays aligned
    /// exactly as if the sound were there.
    /// </summary>
    public bool IsMuted { get; set; }

    /// <summary>Whether any non-zero sample reached the file, so a track that only ever held silence can be told apart.</summary>
    public bool HasSound { get; private set; }

    public double Duration => (double)FrameCount / MeetingAudioFormat.SampleRate;

    /// <summary>Creates the parent folder and a new track with an open-ended data chunk. Throws SlovoException.</summary>
    public static CafWriter Create(string path)
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
            }
            var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 1 << 16);
            try
            {
                stream.Write(CafFile.Header(CafFormat.MeetingTrack, CafFile.UnknownSize, 0));
                stream.Flush();
            }
            catch
            {
                stream.Dispose();
                throw;
            }
            return new CafWriter(path, stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw MeetingErrors.FileFailed(error);
        }
    }

    /// <summary>
    /// Appends float samples as 16-bit integers the way Core Audio converts them: scaled by
    /// 32768, rounded half away from zero and clamped, NaN written as zero. While
    /// <see cref="IsMuted"/> is on, zeros of the same length are written instead. Throws SlovoException.
    /// </summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        ThrowIfClosed();
        if (samples.IsEmpty)
        {
            return;
        }
        if (IsMuted)
        {
            AppendSilence(samples.Length);
            return;
        }
        var offset = 0;
        while (offset < samples.Length)
        {
            var count = Math.Min(samples.Length - offset, buffer.Length / 2);
            var bytes = buffer.AsSpan(0, count * 2);
            for (var index = 0; index < count; index++)
            {
                var value = ToInt16(samples[offset + index]);
                if (value != 0)
                {
                    HasSound = true;
                }
                BinaryPrimitives.WriteInt16LittleEndian(bytes[(index * 2)..], value);
            }
            WriteBytes(bytes, count);
            offset += count;
        }
    }

    /// <summary>Appends <paramref name="frames"/> frames of silence. Throws SlovoException.</summary>
    public void AppendSilence(long frames)
    {
        ThrowIfClosed();
        if (frames <= 0)
        {
            return;
        }
        var zeros = new byte[(int)Math.Min(frames, SilenceChunkFrames) * 2];
        var remaining = frames;
        while (remaining > 0)
        {
            var count = (int)Math.Min(remaining, SilenceChunkFrames);
            WriteBytes(zeros.AsSpan(0, count * 2), count);
            remaining -= count;
        }
    }

    /// <summary>
    /// Hands buffered samples to the system, and to the disk itself when
    /// <paramref name="toDisk"/>, so a crash loses as little as possible. Throws SlovoException.
    /// </summary>
    public void Flush(bool toDisk = false)
    {
        ThrowIfClosed();
        try
        {
            stream.Flush(toDisk);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw MeetingErrors.FileFailed(error);
        }
    }

    /// <summary>Writes the data chunk's real size and closes the file; later calls do nothing. Throws SlovoException.</summary>
    public void Close()
    {
        if (closed)
        {
            return;
        }
        closed = true;
        try
        {
            Span<byte> tail = stackalloc byte[12];
            BinaryPrimitives.WriteInt64BigEndian(tail, FrameCount * MeetingAudioFormat.BytesPerFrame + CafFile.EditCountBytes);
            BinaryPrimitives.WriteUInt32BigEndian(tail[8..], 1);
            stream.Flush();
            stream.Position = CafFile.DataSizeOffset;
            stream.Write(tail);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            throw MeetingErrors.FileFailed(error);
        }
        finally
        {
            stream.Dispose();
            buffer = [];
        }
    }

    public void Dispose()
    {
        try
        {
            Close();
        }
        catch (SlovoException)
        {
            // Dispose must not throw; Close reports the failure to callers that care.
        }
    }

    /// <summary>Core Audio's Float32 → Int16: ×32768, rounded half away from zero, clamped; NaN is 0.</summary>
    public static short ToInt16(float sample)
    {
        if (float.IsNaN(sample))
        {
            return 0;
        }
        var scaled = Math.Round((double)sample * 32768, MidpointRounding.AwayFromZero);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    void WriteBytes(ReadOnlySpan<byte> bytes, int frames)
    {
        try
        {
            stream.Write(bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw MeetingErrors.FileFailed(error);
        }
        FrameCount += frames;
    }

    void ThrowIfClosed() => ObjectDisposedException.ThrowIf(closed, this);
}

/// <summary>
/// Reads a CAF file of linear PCM (16/24/32-bit integers or 32/64-bit floats, either byte
/// order, any channel count) forward or at any position, averaging channels to mono, without
/// loading the whole file. A data chunk of size -1, or one that claims more than the file
/// holds (a recording cut off by a crash), reads to the end of the file.
/// </summary>
public sealed class CafReader : IDisposable
{
    readonly FileStream stream;
    readonly long dataStart;
    readonly int bytesPerSample;
    byte[] buffer = [];

    CafReader(FileStream stream, CafFormat format, long dataStart, long frameCount)
    {
        this.stream = stream;
        Format = format;
        this.dataStart = dataStart;
        FrameCount = frameCount;
        bytesPerSample = (int)(format.BitsPerChannel / 8);
    }

    public CafFormat Format { get; }

    /// <summary>Whole frames in the data chunk.</summary>
    public long FrameCount { get; }

    public double Duration => Format.SampleRate > 0 ? FrameCount / Format.SampleRate : 0;

    /// <summary>The next frame <see cref="Read(Span{float})"/> returns.</summary>
    public long Position
    {
        get;
        set => field = Math.Clamp(value, 0, FrameCount);
    }

    /// <summary>Opens and checks the header. Throws SlovoException for a missing, unreadable or unsupported file.</summary>
    public static CafReader Open(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw MeetingErrors.ReadFailed(error);
        }
        try
        {
            return Parse(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    static CafReader Parse(FileStream stream)
    {
        var length = stream.Length;
        Span<byte> header = stackalloc byte[CafFile.FileHeaderBytes];
        if (!ReadExactly(stream, header) || !header[..4].SequenceEqual("caff"u8) || BinaryPrimitives.ReadUInt16BigEndian(header[4..]) != 1)
        {
            throw MeetingErrors.UnsupportedAudioFormat();
        }
        CafFormat? format = null;
        long? dataStart = null;
        long dataBytes = 0;
        long offset = CafFile.FileHeaderBytes;
        Span<byte> chunk = stackalloc byte[CafFile.ChunkHeaderBytes];
        Span<byte> description = stackalloc byte[CafFile.DescriptionBytes];
        while (offset + CafFile.ChunkHeaderBytes <= length && dataStart is null)
        {
            stream.Position = offset;
            if (!ReadExactly(stream, chunk))
            {
                break;
            }
            var size = BinaryPrimitives.ReadInt64BigEndian(chunk[4..]);
            var body = offset + CafFile.ChunkHeaderBytes;
            if (chunk[..4].SequenceEqual("desc"u8))
            {
                if (size < CafFile.DescriptionBytes || !ReadExactly(stream, description))
                {
                    throw MeetingErrors.UnsupportedAudioFormat();
                }
                format = new CafFormat(
                    BinaryPrimitives.ReadDoubleBigEndian(description),
                    System.Text.Encoding.ASCII.GetString(description[8..12]),
                    BinaryPrimitives.ReadUInt32BigEndian(description[12..]),
                    BinaryPrimitives.ReadUInt32BigEndian(description[16..]),
                    BinaryPrimitives.ReadUInt32BigEndian(description[20..]),
                    BinaryPrimitives.ReadUInt32BigEndian(description[24..]),
                    BinaryPrimitives.ReadUInt32BigEndian(description[28..]));
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                // The edit count comes first; -1 (or a size past the end of a file cut off
                // by a crash) means the samples run to the end of the file.
                var start = body + CafFile.EditCountBytes;
                var available = Math.Max(0, length - start);
                dataStart = start;
                dataBytes = size < CafFile.EditCountBytes ? available : Math.Min(available, size - CafFile.EditCountBytes);
                break;
            }
            if (size < 0)
            {
                break;
            }
            offset = body + size;
        }
        if (format is null || dataStart is null)
        {
            throw MeetingErrors.UnsupportedAudioFormat();
        }
        var bits = format.BitsPerChannel;
        var supported = format.IsLinearPcm && format.SampleRate > 0 && double.IsFinite(format.SampleRate)
                        && format.ChannelsPerFrame > 0 && format.FramesPerPacket == 1
                        && (format.IsFloat ? bits is 32 or 64 : bits is 16 or 24 or 32)
                        && format.BytesPerPacket == bits / 8 * format.ChannelsPerFrame;
        if (!supported)
        {
            throw MeetingErrors.UnsupportedAudioFormat();
        }
        return new CafReader(stream, format, dataStart.Value, dataBytes / format.BytesPerFrame);
    }

    /// <summary>Reads up to <c>destination.Length</c> mono frames from <see cref="Position"/>; 0 at the end. Throws SlovoException.</summary>
    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(buffer is null, this);
        var frames = (int)Math.Min(destination.Length, FrameCount - Position);
        if (frames <= 0)
        {
            return 0;
        }
        var frameBytes = Format.BytesPerFrame;
        const int blockFrames = 16_384;
        var done = 0;
        while (done < frames)
        {
            var count = Math.Min(blockFrames, frames - done);
            var byteCount = count * frameBytes;
            if (buffer.Length < byteCount)
            {
                buffer = new byte[byteCount];
            }
            var bytes = buffer.AsSpan(0, byteCount);
            try
            {
                stream.Position = dataStart + Position * frameBytes;
                var read = stream.ReadAtLeast(bytes, byteCount, throwOnEndOfStream: false);
                count = read / frameBytes;
            }
            catch (IOException error)
            {
                throw MeetingErrors.ReadFailed(error);
            }
            if (count == 0)
            {
                break;
            }
            Convert(bytes[..(count * frameBytes)], destination.Slice(done, count));
            done += count;
            Position += count;
        }
        return done;
    }

    /// <summary>Up to <paramref name="count"/> next frames; fewer only at the end of the file.</summary>
    public float[] Read(int count)
    {
        var samples = new float[Math.Max(0, (int)Math.Min(count, FrameCount - Position))];
        var read = Read(samples);
        return read == samples.Length ? samples : samples[..read];
    }

    /// <summary>
    /// The samples between two times in seconds on the file's own timeline, clamped to the
    /// file; only that stretch is read.
    /// </summary>
    public float[] ReadRange(double startSeconds, double endSeconds)
    {
        var rate = Format.SampleRate;
        var first = double.IsFinite(startSeconds) ? (long)Math.Floor(Math.Max(0, startSeconds) * rate) : 0;
        var last = double.IsFinite(endSeconds) ? (long)Math.Ceiling(Math.Max(0, endSeconds) * rate) : FrameCount;
        first = Math.Min(first, FrameCount);
        last = Math.Clamp(last, first, FrameCount);
        Position = first;
        return Read((int)Math.Min(int.MaxValue, last - first));
    }

    /// <summary>Every frame from the start. Throws SlovoException.</summary>
    public float[] ReadAll()
    {
        Position = 0;
        return Read((int)Math.Min(int.MaxValue, FrameCount));
    }

    public void Dispose()
    {
        stream.Dispose();
        buffer = null!;
    }

    void Convert(ReadOnlySpan<byte> bytes, Span<float> destination)
    {
        var channels = (int)Format.ChannelsPerFrame;
        var frameBytes = Format.BytesPerFrame;
        var scale = 1f / channels;
        for (var frame = 0; frame < destination.Length; frame++)
        {
            var baseOffset = frame * frameBytes;
            if (channels == 1)
            {
                destination[frame] = Sample(bytes[baseOffset..]);
                continue;
            }
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += Sample(bytes[(baseOffset + channel * bytesPerSample)..]);
            }
            destination[frame] = sum * scale;
        }
    }

    float Sample(ReadOnlySpan<byte> bytes)
    {
        var little = Format.IsLittleEndian;
        if (Format.IsFloat)
        {
            return bytesPerSample == 4
                ? little ? BinaryPrimitives.ReadSingleLittleEndian(bytes) : BinaryPrimitives.ReadSingleBigEndian(bytes)
                : (float)(little ? BinaryPrimitives.ReadDoubleLittleEndian(bytes) : BinaryPrimitives.ReadDoubleBigEndian(bytes));
        }
        return bytesPerSample switch
        {
            2 => (little ? BinaryPrimitives.ReadInt16LittleEndian(bytes) : BinaryPrimitives.ReadInt16BigEndian(bytes)) / 32_768f,
            3 => (little
                    ? bytes[0] | bytes[1] << 8 | (sbyte)bytes[2] << 16
                    : bytes[2] | bytes[1] << 8 | (sbyte)bytes[0] << 16) / 8_388_608f,
            _ => (little ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : BinaryPrimitives.ReadInt32BigEndian(bytes)) / 2_147_483_648f,
        };
    }

    static bool ReadExactly(FileStream stream, Span<byte> destination) =>
        stream.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false) == destination.Length;
}
