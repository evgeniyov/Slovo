using System.Buffers.Binary;

namespace Slovo.Core.Audio;

public enum PcmSampleType
{
    /// <summary>Signed integers (unsigned for 8-bit samples, as in WAV).</summary>
    Integer,
    /// <summary>IEEE floating point.</summary>
    Float,
}

/// <summary>
/// The layout of interleaved little-endian PCM: what WASAPI and Media Foundation deliver.
/// A 24-bit stream in 32-bit containers is described by its container, 32 bits.
/// </summary>
public readonly record struct PcmLayout(PcmSampleType Type, int BitsPerSample, int Channels)
{
    public int BytesPerSample => BitsPerSample / 8;
    public int BlockAlign => BytesPerSample * Channels;

    public bool IsSupported => Channels > 0 && (Type == PcmSampleType.Float
        ? BitsPerSample is 32 or 64
        : BitsPerSample is 8 or 16 or 24 or 32);
}

/// <summary>
/// Averages the channels of interleaved PCM into one float channel (MeetingAudioMath.mixdown
/// and CaptureConverter.downmix on macOS). A trailing partial frame is ignored.
/// </summary>
public static class AudioMixdown
{
    public static float[] ToMono(ReadOnlySpan<byte> data, PcmLayout layout)
    {
        if (!layout.IsSupported)
        {
            throw new ArgumentException($"Unsupported PCM layout {layout}.", nameof(layout));
        }
        var frames = data.Length / layout.BlockAlign;
        var mono = new float[frames];
        var channels = layout.Channels;
        var width = layout.BytesPerSample;
        var scale = 1f / channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            var offset = frame * layout.BlockAlign;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += Sample(data.Slice(offset + channel * width, width), layout);
            }
            mono[frame] = sum * scale;
        }
        return mono;
    }

    /// <summary>Averages interleaved float channels.</summary>
    public static float[] ToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        if (channels == 1)
        {
            return interleaved.ToArray();
        }
        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        var scale = 1f / channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += interleaved[frame * channels + channel];
            }
            mono[frame] = sum * scale;
        }
        return mono;
    }

    static float Sample(ReadOnlySpan<byte> bytes, PcmLayout layout)
    {
        if (layout.Type == PcmSampleType.Float)
        {
            return layout.BitsPerSample == 32
                ? BinaryPrimitives.ReadSingleLittleEndian(bytes)
                : (float)BinaryPrimitives.ReadDoubleLittleEndian(bytes);
        }
        return layout.BitsPerSample switch
        {
            8 => (bytes[0] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32_768f,
            24 => ((bytes[0] | (bytes[1] << 8) | (bytes[2] << 16)) << 8 >> 8) / 8_388_608f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2_147_483_648f,
        };
    }
}
