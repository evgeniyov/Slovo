using System.Runtime.InteropServices;
using Slovo.Core.Audio;

namespace Slovo.Core.Meetings;

/// <summary>A half-open range of sample indexes, <c>Lower..&lt;Upper</c>.</summary>
public readonly record struct SampleRange(long Lower, long Upper)
{
    public long Count => Upper - Lower;

    public override string ToString() => $"{Lower}..<{Upper}";
}

/// <summary>A stretch of one track worth transcribing.</summary>
public readonly record struct SpeechSegment(SampleRange Samples, double Start, double End);

/// <summary>
/// Cuts a track into segments of at most <see cref="MaxSeconds"/> at low-energy points (the
/// <see cref="GigaAMChunker"/> logic) and skips silent ones. Pure (port of the macOS
/// <c>SpeechSegmenter</c>).
///
/// The work happens on an energy envelope of 25 ms frames rather than on samples, so a file
/// of any length is scanned block by block and only the envelope (40 values per second) is
/// held in memory:
/// <list type="number">
/// <item>Frames louder than <see cref="SilenceRms"/> are speech. Speech separated by less than
/// <see cref="PauseSeconds"/> of quiet stays together, so a segment follows one turn of talk
/// and its start time places it correctly among the other speaker's lines.</item>
/// <item>Each stretch is padded by <see cref="PaddingSeconds"/> so soft word onsets survive.</item>
/// <item>Stretches longer than <see cref="MaxSeconds"/> go through <see cref="GigaAMChunker"/>
/// applied to the envelope, which cuts at the quietest smoothed moment near the end of each
/// window, as it does for GigaAM itself.</item>
/// </list>
/// </summary>
public sealed record SpeechSegmenter
{
    /// <summary>Envelope resolution; an integer rate lets <see cref="GigaAMChunker"/> measure it in seconds.</summary>
    public const int FramesPerSecond = 40;
    /// <summary><see cref="GigaAMChunker"/> looks this far back from the end of a window for a pause.</summary>
    public const double SearchSeconds = 5;
    /// <summary>Neighbours averaged on each side of a frame, about 150 ms, as in the chunker.</summary>
    public const int SmoothingFrames = 6;

    public SpeechSegmenter(double maxSeconds = 20, float silenceRms = 0.005f)
    {
        MaxSeconds = maxSeconds;
        SilenceRms = silenceRms;
    }

    public double MaxSeconds { get; init; }
    /// <summary>Segments whose RMS never exceeds this are silent.</summary>
    public float SilenceRms { get; init; }
    /// <summary>Quiet at least this long separates two segments.</summary>
    public double PauseSeconds { get; init; } = 1.0;
    /// <summary>Audio kept before and after the detected speech.</summary>
    public double PaddingSeconds { get; init; } = 0.2;
    /// <summary>Stretches with less loud audio than this are clicks or bumps, not speech.</summary>
    public double MinimumSpeechSeconds { get; init; } = 0.1;

    public IReadOnlyList<SpeechSegment> Segments(ReadOnlySpan<float> samples, int sampleRate = MeetingAudioFormat.SampleRate)
    {
        if (sampleRate <= 0 || samples.IsEmpty)
        {
            return [];
        }
        var envelope = new EnergyEnvelope(sampleRate);
        envelope.Append(samples);
        envelope.Finish();
        return Segments(envelope);
    }

    /// <summary>Segments of an audio file (a meeting track), reading it block by block. Throws SlovoException.</summary>
    public IReadOnlyList<SpeechSegment> SegmentsInFile(string path) => Segments(Envelope(path));

    /// <summary>Start and end of each speech segment of a file, for the clip planner. Throws SlovoException.</summary>
    public IReadOnlyList<TimeRange> SpeechRanges(string path) =>
        SegmentsInFile(path).Select(s => new TimeRange(s.Start, s.End)).ToList();

    /// <summary>
    /// The energy envelope of a file on its 16 kHz timeline, read a second at a time with
    /// <see cref="MeetingTrackReader"/>. The meeting pipeline reads a track once and uses the
    /// envelope both for speech detection and for splitting long speaker turns. Throws SlovoException.
    /// </summary>
    public static EnergyEnvelope Envelope(string path)
    {
        using var reader = MeetingTrackReader.Open(path);
        var envelope = new EnergyEnvelope(MeetingAudioFormat.SampleRate);
        while (true)
        {
            var block = reader.Read(MeetingAudioFormat.SampleRate);
            if (block.Length == 0)
            {
                break;
            }
            envelope.Append(block);
        }
        envelope.Finish();
        return envelope;
    }

    /// <summary>
    /// Segments of one slice of an envelope (frames <c>lowerFrame..&lt;upperFrame</c>), with
    /// times on the whole track's timeline, for splitting a long speaker turn at quiet points.
    ///
    /// Unlike <see cref="Segments(EnergyEnvelope)"/>, the slice is not searched for pauses:
    /// the diarizer already decided it is one person's turn, so it is only cut into pieces of
    /// at most <see cref="MaxSeconds"/> at the quietest moments, and pieces without a frame
    /// above <see cref="SilenceRms"/> are dropped.
    /// </summary>
    public IReadOnlyList<SpeechSegment> Segments(EnergyEnvelope envelope, int lowerFrame, int upperFrame)
    {
        var lower = Math.Max(0, Math.Min(lowerFrame, envelope.Frames.Count));
        var upper = Math.Max(lower, Math.Min(upperFrame, envelope.Frames.Count));
        if (upper <= lower || !(MaxSeconds > 0))
        {
            return [];
        }
        return Pieces(envelope, lower, upper);
    }

    public IReadOnlyList<SpeechSegment> Segments(EnergyEnvelope envelope)
    {
        var energy = envelope.Frames;
        if (energy.Count == 0 || !(MaxSeconds > 0))
        {
            return [];
        }
        var frameSeconds = (double)envelope.FrameLength / envelope.SampleRate;
        var pauseFrames = Math.Max(1, (int)Math.Round(PauseSeconds / frameSeconds, MidpointRounding.AwayFromZero));
        var paddingFrames = Math.Max(0, (int)Math.Round(PaddingSeconds / frameSeconds, MidpointRounding.AwayFromZero));
        var minimumSpeechFrames = Math.Max(1, (int)Math.Round(MinimumSpeechSeconds / frameSeconds, MidpointRounding.AwayFromZero));

        var result = new List<SpeechSegment>();
        foreach (var (start, end) in SpeechStretches(envelope.FrameSpan, pauseFrames, paddingFrames, minimumSpeechFrames))
        {
            result.AddRange(Pieces(envelope, start, end));
        }
        return result;
    }

    /// <summary>Cuts a frame range into pieces of at most <see cref="MaxSeconds"/> at low-energy points and keeps the pieces that hold some sound.</summary>
    List<SpeechSegment> Pieces(EnergyEnvelope envelope, int stretchStart, int stretchEnd)
    {
        var energy = envelope.FrameSpan;
        var chunks = GigaAMChunker.Ranges(energy[stretchStart..stretchEnd], FramesPerSecond, MaxSeconds, SearchSeconds,
                                          1.0 / FramesPerSecond, SmoothingFrames);
        var result = new List<SpeechSegment>();
        foreach (var chunk in chunks)
        {
            var first = stretchStart + chunk.Start;
            var last = stretchStart + chunk.End;
            // A tail cut off a long stretch can hold only padding.
            var loud = false;
            for (var frame = first; frame < last && !loud; frame++)
            {
                loud = energy[frame] > SilenceRms;
            }
            if (!loud)
            {
                continue;
            }
            var lower = Math.Min(envelope.SampleCount, (long)first * envelope.FrameLength);
            var upper = Math.Min(envelope.SampleCount, (long)last * envelope.FrameLength);
            if (upper <= lower)
            {
                continue;
            }
            result.Add(new SpeechSegment(new SampleRange(lower, upper), (double)lower / envelope.SampleRate, (double)upper / envelope.SampleRate));
        }
        return result;
    }

    /// <summary>Padded frame ranges of speech, in order and without overlaps.</summary>
    List<(int Start, int End)> SpeechStretches(ReadOnlySpan<float> energy, int pauseFrames, int paddingFrames, int minimumSpeechFrames)
    {
        var stretches = new List<(int Start, int End, int LoudFrames)>();
        for (var index = 0; index < energy.Length; index++)
        {
            if (!(energy[index] > SilenceRms))
            {
                continue;
            }
            if (stretches.Count > 0 && index - stretches[^1].End < pauseFrames)
            {
                var last = stretches[^1];
                stretches[^1] = (last.Start, index + 1, last.LoudFrames + 1);
            }
            else
            {
                stretches.Add((index, index + 1, 1));
            }
        }

        var padded = new List<(int Start, int End)>();
        foreach (var stretch in stretches)
        {
            if (stretch.LoudFrames < minimumSpeechFrames)
            {
                continue;
            }
            var start = Math.Max(0, stretch.Start - paddingFrames);
            var end = Math.Min(energy.Length, stretch.End + paddingFrames);
            if (padded.Count > 0 && start <= padded[^1].End)
            {
                padded[^1] = (padded[^1].Start, Math.Max(padded[^1].End, end));
            }
            else
            {
                padded.Add((start, end));
            }
        }
        return padded;
    }
}

/// <summary>RMS of consecutive frames of a signal, built incrementally.</summary>
public sealed class EnergyEnvelope
{
    readonly List<float> frames = [];
    double sum;
    int count;

    public EnergyEnvelope(int sampleRate)
    {
        SampleRate = sampleRate;
        FrameLength = Math.Max(1, sampleRate / SpeechSegmenter.FramesPerSecond);
    }

    public int SampleRate { get; }
    public int FrameLength { get; }
    public IReadOnlyList<float> Frames => frames;
    public long SampleCount { get; private set; }

    internal ReadOnlySpan<float> FrameSpan => CollectionsMarshal.AsSpan(frames);

    public void Append(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
            count++;
            if (count == FrameLength)
            {
                CloseFrame();
            }
        }
        SampleCount += samples.Length;
    }

    /// <summary>Closes a final partial frame.</summary>
    public void Finish()
    {
        if (count > 0)
        {
            CloseFrame();
        }
    }

    void CloseFrame()
    {
        frames.Add((float)Math.Sqrt(sum / count));
        sum = 0;
        count = 0;
    }
}
