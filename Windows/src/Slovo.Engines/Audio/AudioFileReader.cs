using System.Buffers.Binary;
using Slovo.Core.Audio;
using Slovo.Core.Localization;
using Slovo.Core.Model;

namespace Slovo.Engines.Audio;

/// <summary>
/// Reads the uncompressed recordings the engines are handed, averaged to mono and resampled
/// to 16 kHz (the part of AudioLoader.swift the engines need): WAV through Core's
/// <see cref="WavFile"/>, and CAF with linear PCM, as meeting audio is stored. Resampling
/// is Core's <see cref="Resampler"/>.
///
/// The CAF part stays inside the engines until Core reads CAF itself.
/// </summary>
internal static class AudioFileReader
{
    public const int SampleRate = Resampler.DefaultTargetRate;

    /// <summary>16 kHz mono samples of a WAV or CAF file. Throws SlovoException.</summary>
    public static float[] ReadMono16k(string path, CancellationToken cancellation = default)
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
        cancellation.ThrowIfCancellationRequested();
        var audio = Decode(data) ?? throw new SlovoException(CoreStrings.Format("audio.error.unreadableWAV", Path.GetFileName(path)));
        cancellation.ThrowIfCancellationRequested();
        var rate = (int)Math.Round(audio.SampleRate);
        return rate == SampleRate || audio.Samples.Length == 0 ? audio.Samples : Resampler.Resample(audio.Samples, rate, SampleRate);
    }

    /// <summary>Mono samples at the file's own rate; null when the data is neither WAV nor linear-PCM CAF.</summary>
    internal static RecordedAudio? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..4].SequenceEqual("caff"u8))
        {
            return DecodeCaf(data);
        }
        return WavFile.Decode(data);
    }

    /// <summary>
    /// Core Audio Format with linear PCM: a <c>desc</c> chunk, then a <c>data</c> chunk that
    /// starts with a 4-byte edit count. Chunk headers are big-endian; samples may be either.
    /// A data size of -1 means "up to the end of the file", as a recording cut off before it
    /// was finished leaves it.
    /// </summary>
    static RecordedAudio? DecodeCaf(ReadOnlySpan<byte> data)
    {
        const uint LinearPcm = 0x6C70636D; // 'lpcm'
        const uint FloatFlag = 1, LittleEndianFlag = 2;
        (double Rate, PcmLayout Layout, bool BigEndian)? description = null;
        var offset = 8;
        while (offset + 12 <= data.Length)
        {
            var id = data.Slice(offset, 4);
            var size = BinaryPrimitives.ReadInt64BigEndian(data.Slice(offset + 4, 8));
            var body = offset + 12;
            if (id.SequenceEqual("desc"u8) && body + 32 <= data.Length)
            {
                var rate = BinaryPrimitives.ReadDoubleBigEndian(data.Slice(body, 8));
                var formatId = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(body + 8, 4));
                var flags = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(body + 12, 4));
                var framesPerPacket = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(body + 20, 4));
                var channels = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(body + 24, 4));
                var bits = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(body + 28, 4));
                var layout = new PcmLayout((flags & FloatFlag) != 0 ? PcmSampleType.Float : PcmSampleType.Integer, bits, channels);
                if (formatId != LinearPcm || framesPerPacket != 1 || !(rate > 0) || !layout.IsSupported)
                {
                    return null;
                }
                description = (rate, layout, (flags & LittleEndianFlag) == 0);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (description is not { } format)
                {
                    return null;
                }
                var start = body + 4;
                var length = (int)Math.Clamp(size < 0 ? data.Length - start : Math.Min(size - 4, data.Length - start), 0, int.MaxValue);
                var samples = LittleEndianSigned(data.Slice(start, length), format.Layout, format.BigEndian);
                return new RecordedAudio(AudioMixdown.ToMono(samples, format.Layout), format.Rate);
            }
            if (size < 0)
            {
                return null;
            }
            offset = (int)Math.Min(int.MaxValue, body + size);
        }
        return null;
    }

    /// <summary>
    /// CAF samples as <see cref="AudioMixdown"/> reads them: little-endian, and 8-bit ones
    /// unsigned as in WAV (CAF stores them signed).
    /// </summary>
    static byte[] LittleEndianSigned(ReadOnlySpan<byte> samples, PcmLayout layout, bool bigEndian)
    {
        var bytes = samples[..(samples.Length - samples.Length % layout.BlockAlign)].ToArray();
        var width = layout.BytesPerSample;
        if (bigEndian && width > 1)
        {
            for (var index = 0; index + width <= bytes.Length; index += width)
            {
                bytes.AsSpan(index, width).Reverse();
            }
        }
        if (width == 1 && layout.Type == PcmSampleType.Integer)
        {
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] ^= 0x80;
            }
        }
        return bytes;
    }
}
