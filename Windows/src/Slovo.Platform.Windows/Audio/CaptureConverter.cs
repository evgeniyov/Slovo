using NAudio.Wave;
using Slovo.Core.Audio;

namespace Slovo.Platform.Windows.Audio;

/// <summary>Reads what WASAPI and Media Foundation report as a stream format.</summary>
public static class CaptureFormat
{
    static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
    static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// The layout of a PCM or IEEE float format, including WAVE_FORMAT_EXTENSIBLE (the usual
    /// shared-mode mix format); null for anything compressed or unknown. Never assumes a rate.
    /// </summary>
    public static PcmLayout? LayoutOf(WaveFormat format)
    {
        PcmSampleType? type = format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => PcmSampleType.Float,
            WaveFormatEncoding.Pcm => PcmSampleType.Integer,
            WaveFormatEncoding.Extensible => format is WaveFormatExtensible extensible
                ? extensible.SubFormat == FloatSubtype ? PcmSampleType.Float
                  : extensible.SubFormat == PcmSubtype ? PcmSampleType.Integer
                  : null
                // An extensible format NAudio could not read further: shared mode mixes in float.
                : format.BitsPerSample == 32 ? PcmSampleType.Float : PcmSampleType.Integer,
            _ => null,
        };
        if (type is not { } sampleType || format.SampleRate <= 0)
        {
            return null;
        }
        var layout = new PcmLayout(sampleType, format.BitsPerSample, format.Channels);
        return layout.IsSupported && layout.BlockAlign == format.BlockAlign ? layout : null;
    }
}

/// <summary>
/// When a capture packet's first frame was recorded, in <c>IUptime</c> seconds
/// (MeetingCaptureBuffers.hostTime on macOS).
/// </summary>
public static class CaptureClock
{
    /// <summary>
    /// A packet position further than this from the arrival time is not trusted: it would put
    /// seconds or hours of silence on the track.
    /// </summary>
    public const double MaximumTimestampSkew = 10;

    /// <summary>
    /// The QPC position WASAPI reports (100 ns units, the clock Stopwatch reads on Windows)
    /// translated to the uptime clock through the time the packet arrived. Without a usable
    /// position, the arrival time minus the packet's duration is close enough, since packets
    /// are delivered as soon as they fill.
    /// </summary>
    /// <param name="qpcPosition">From IAudioCaptureClient::GetBuffer, in 100 ns units; 0 when absent.</param>
    /// <param name="timestampError">AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR was set.</param>
    /// <param name="arrivalUptime">IUptime seconds when the packet arrived.</param>
    /// <param name="arrivalQpcSeconds">The performance counter in seconds at the same moment.</param>
    public static double PacketTime(long qpcPosition, bool timestampError, double arrivalUptime, double arrivalQpcSeconds,
        int frames, int sampleRate)
    {
        var duration = sampleRate > 0 ? Math.Max(0, frames) / (double)sampleRate : 0;
        var estimate = arrivalUptime - duration;
        if (timestampError || qpcPosition <= 0)
        {
            return estimate;
        }
        var age = arrivalQpcSeconds - qpcPosition / 10_000_000.0;
        // A packet cannot come from the future (allowing for clock read jitter).
        if (age < -0.05 || age > MaximumTimestampSkew)
        {
            return estimate;
        }
        return arrivalUptime - age;
    }
}

/// <summary>A converted chunk: 16 kHz mono samples and the uptime of the first one, when known.</summary>
public readonly record struct ConvertedChunk(float[] Samples, double? Time)
{
    public static readonly ConvertedChunk Empty = new([], null);
}

/// <summary>
/// Turns capture packets of one format into 16 kHz mono with the time of their first
/// sample. The resampler keeps its filter state between packets, so borders do not click;
/// a device switch starts a new converter and <see cref="Flush"/>es the old one.
/// Used from one thread at a time (the capture thread, then the closing one).
/// </summary>
public sealed class CaptureConverter
{
    readonly PcmLayout layout;
    readonly Resampler resampler;
    /// <summary>The time of input sample <see cref="anchorIndex"/>, from the last packet that had one.</summary>
    double? anchorTime;
    long anchorIndex;

    public CaptureConverter(PcmLayout layout, int sampleRate, int targetRate = Resampler.DefaultTargetRate)
    {
        if (!layout.IsSupported)
        {
            throw new ArgumentException($"Unsupported capture layout {layout}.", nameof(layout));
        }
        this.layout = layout;
        SampleRate = sampleRate;
        resampler = new Resampler(sampleRate, targetRate);
    }

    public int SampleRate { get; }
    public PcmLayout Layout => layout;

    /// <summary>Converts one packet; <paramref name="silent"/> packets (AUDCLNT_BUFFERFLAGS_SILENT) count as zeros.</summary>
    public ConvertedChunk Convert(ReadOnlySpan<byte> data, bool silent, double? packetTime)
    {
        var frames = data.Length / layout.BlockAlign;
        if (frames == 0)
        {
            return ConvertedChunk.Empty;
        }
        var mono = silent ? new float[frames] : AudioMixdown.ToMono(data, layout);
        return ConvertMono(mono, packetTime);
    }

    /// <summary>Converts mono samples at the capture rate.</summary>
    public ConvertedChunk ConvertMono(ReadOnlySpan<float> mono, double? packetTime)
    {
        if (packetTime is { } time)
        {
            anchorTime = time;
            anchorIndex = resampler.InputCount;
        }
        var firstOutput = resampler.OutputCount;
        var samples = resampler.Process(mono);
        return new ConvertedChunk(samples, TimeOf(firstOutput));
    }

    /// <summary>What the resampler still holds back; call once when the capture ends.</summary>
    public ConvertedChunk Flush()
    {
        var firstOutput = resampler.OutputCount;
        var time = TimeOf(firstOutput);
        var samples = resampler.Flush();
        anchorTime = null;
        return new ConvertedChunk(samples, samples.Length == 0 ? null : time);
    }

    double? TimeOf(long outputIndex) =>
        anchorTime is { } time ? time + (resampler.SourcePosition(outputIndex) - anchorIndex) / SampleRate : null;
}
