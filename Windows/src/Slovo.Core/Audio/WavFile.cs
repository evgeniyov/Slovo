using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Storage;

namespace Slovo.Core.Audio;

/// <summary>
/// Dictation recordings on disk: 16 kHz mono WAV files kept for playback (port of the macOS
/// <c>WAVFile</c>; files are interchangeable between the apps).
/// </summary>
public static class WavFile
{
    /// <summary>Writes the samples as a 16-bit PCM mono WAV file, creating the parent folder. Throws SlovoException.</summary>
    public static void Write(RecordedAudio audio, string path)
    {
        try
        {
            AtomicFile.Write(path, Encode(audio));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new SlovoException(CoreStrings.Format("audio.error.writeFailed", error.Message), error);
        }
    }

    /// <summary>Reads a WAV file back as mono samples at its own sample rate. Throws SlovoException.</summary>
    public static RecordedAudio Read(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new SlovoException(CoreStrings.Format("audio.error.readFailed", Path.GetFileName(path), error.Message), error);
        }
        return Decode(data) ?? throw new SlovoException(CoreStrings.Format("audio.error.unreadableWAV", Path.GetFileName(path)));
    }

    /// <summary><c>yyyy-MM-dd/&lt;UUID&gt;.wav</c>, the path under the recordings folder a history entry stores (local date).</summary>
    public static string RelativePath(Guid id, DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return string.Create(CultureInfo.InvariantCulture, $"{local.Year:0000}-{local.Month:00}-{local.Day:00}/{id.ToString("D").ToUpperInvariant()}.wav");
    }

    /// <summary>16-bit PCM keeps files half the size of float with more precision than recognition uses.</summary>
    public static byte[] Encode(RecordedAudio audio)
    {
        var sampleRate = (uint)Math.Min(1_000_000, Math.Max(1, Math.Round(audio.SampleRate)));
        const int bytesPerSample = 2;
        var frameCount = Math.Min(audio.Samples.Length, (int)Math.Min(int.MaxValue - 64, (uint.MaxValue - 36) / bytesPerSample));
        var dataBytes = frameCount * bytesPerSample;
        var buffer = new byte[44 + dataBytes];
        var span = buffer.AsSpan();
        Encoding.ASCII.GetBytes("RIFF", span[0..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(36 + dataBytes));
        Encoding.ASCII.GetBytes("WAVE", span[8..12]);
        Encoding.ASCII.GetBytes("fmt ", span[12..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], sampleRate * bytesPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], bytesPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        Encoding.ASCII.GetBytes("data", span[36..40]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)dataBytes);
        for (var index = 0; index < frameCount; index++)
        {
            var sample = audio.Samples[index];
            var clamped = float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0f;
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + index * 2)..], (short)Math.Round(clamped * 32767f, MidpointRounding.AwayFromZero));
        }
        return buffer;
    }

    /// <summary>
    /// Parses PCM (8/16/24/32-bit) and IEEE float (32/64-bit) WAV data, including
    /// WAVE_FORMAT_EXTENSIBLE, averaging channels to mono. Null when unreadable.
    /// </summary>
    public static RecordedAudio? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !FourCc(bytes, 0, "RIFF") || !FourCc(bytes, 8, "WAVE"))
        {
            return null;
        }
        (ushort Tag, int Channels, double SampleRate, int Bits)? format = null;
        (int Start, int End)? payload = null;
        var offset = 12;
        while (offset + 8 <= bytes.Length && (format is null || payload is null))
        {
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            var body = offset + 8;
            var end = (int)Math.Min(bytes.Length, body + size);
            if (FourCc(bytes, offset, "fmt ") && end - body >= 16)
            {
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes[body..]);
                if (tag == 0xFFFE && end - body >= 26)
                {
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(body + 24)..]);
                }
                format = (tag, BinaryPrimitives.ReadUInt16LittleEndian(bytes[(body + 2)..]),
                          BinaryPrimitives.ReadUInt32LittleEndian(bytes[(body + 4)..]),
                          BinaryPrimitives.ReadUInt16LittleEndian(bytes[(body + 14)..]));
            }
            else if (FourCc(bytes, offset, "data"))
            {
                payload = (body, end);
            }
            var next = body + size + (size & 1);
            if (next > int.MaxValue)
            {
                break;
            }
            offset = (int)next;
        }
        if (format is not { } f || payload is not { } p || f.Channels <= 0 || f.SampleRate <= 0)
        {
            return null;
        }
        var byteCount = (f.Tag, f.Bits) switch
        {
            (1, 8) => 1,
            (1, 16) => 2,
            (1, 24) => 3,
            (1, 32) or (3, 32) => 4,
            (3, 64) => 8,
            _ => 0,
        };
        if (byteCount == 0)
        {
            return null;
        }
        var frameBytes = byteCount * f.Channels;
        var frames = (p.End - p.Start) / frameBytes;
        var samples = new float[frames];
        var scale = 1f / f.Channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var baseOffset = p.Start + frame * frameBytes;
            var sum = 0f;
            for (var channel = 0; channel < f.Channels; channel++)
            {
                sum += Value(bytes, baseOffset + channel * byteCount, f.Tag, f.Bits);
            }
            samples[frame] = sum * scale;
        }
        return new RecordedAudio(samples, f.SampleRate);
    }

    static float Value(ReadOnlySpan<byte> bytes, int offset, ushort tag, int bits) => (tag, bits) switch
    {
        (1, 8) => (bytes[offset] - 128) / 128f,
        (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]) / 32_768f,
        (1, 24) => (bytes[offset] | bytes[offset + 1] << 8 | (sbyte)bytes[offset + 2] << 16) / 8_388_608f,
        (1, 32) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]) / 2_147_483_648f,
        (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]),
        _ => (float)BinaryPrimitives.ReadDoubleLittleEndian(bytes[offset..]),
    };

    static bool FourCc(ReadOnlySpan<byte> bytes, int offset, string code) =>
        offset + 4 <= bytes.Length && bytes[offset] == code[0] && bytes[offset + 1] == code[1] && bytes[offset + 2] == code[2] && bytes[offset + 3] == code[3];
}
