namespace Slovo.Core.Audio;

/// <summary>
/// Plays speech faster or slower without changing its pitch, for listening to a meeting at
/// 0.75×–2×: the Windows counterpart of the time-pitch processing AVAudioPlayer applies on
/// the Mac when its rate changes.
/// </summary>
/// <remarks>
/// <para>
/// WSOLA, waveform-similarity overlap-add (Verhelst and Roelands, 1993). The output is a sum
/// of Hann-windowed frames of <see cref="FrameLength"/> input samples (32 ms) laid half a
/// frame apart, so every output sample is covered by exactly two frames whose windows add
/// up to one. Frame <c>k</c> nominally starts where output time <c>k × hop</c> falls in the
/// input at this rate, and is moved by up to <see cref="Tolerance"/> samples (10 ms, about
/// one period of a low voice) to where it best continues the frame before it: the offset
/// with the highest normalised cross-correlation between its first half and the input that
/// followed the previous frame. Frames therefore overlap in phase, so the voice keeps its
/// pitch and only whole periods are dropped (faster) or repeated (slower), and what plays
/// never strays from the nominal position by more than the tolerance.
/// </para>
/// <para>
/// When the natural continuation of the previous frame lies within the tolerance it is taken
/// as it is, the best match there can be. At 1× that happens for every frame and the output
/// is the input, sample for sample; near 1× most frames cost no search at all.
/// </para>
/// <para>
/// The input is read at arbitrary positions through a <see cref="SampleReader"/> (a meeting's
/// mixed tracks), with silence before its start and wherever the reader writes nothing, and
/// <see cref="Reset"/> starts over at any input position and rate. Not thread-safe: one
/// audio callback uses it at a time.
/// </para>
/// </remarks>
public sealed class TimeStretcher
{
    /// <summary>
    /// Reads input samples from <paramref name="position"/> (never negative) and returns how
    /// many it wrote; whatever it leaves unwritten is silence.
    /// </summary>
    public delegate int SampleReader(long position, Span<float> destination);

    const double FrameSeconds = 0.032;
    const double ToleranceSeconds = 0.010;
    /// <summary>
    /// Mean square below which the input after the previous frame counts as silence (about
    /// −80 dBFS): there is no waveform to line up with, so the frame keeps its nominal place.
    /// </summary>
    const double SilenceMeanSquare = 1e-8;

    readonly SampleReader input;
    readonly int hop;
    readonly float[] window;
    /// <summary>The input around a frame's nominal start, <see cref="Tolerance"/> either side.</summary>
    readonly float[] candidates;
    /// <summary>The input that followed the previous frame: what the next one should sound like.</summary>
    readonly float[] template;
    /// <summary>The previous frame's second half, windowed, waiting for the next frame's first half.</summary>
    readonly float[] tail;
    readonly float[] block;
    int blockRead;
    double start;
    long produced;
    long frameIndex;
    long previousStart;

    /// <param name="sampleRate">The input's sample rate; the output has the same rate.</param>
    public TimeStretcher(double sampleRate, SampleReader input)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        this.input = input;
        hop = Math.Max(8, (int)Math.Round(sampleRate * FrameSeconds / 2));
        FrameLength = 2 * hop;
        Tolerance = Math.Max(1, (int)Math.Round(sampleRate * ToleranceSeconds));
        // A periodic Hann window: at half-frame overlap neighbouring windows sum to exactly one.
        window = new float[FrameLength];
        for (var i = 0; i < FrameLength; i++)
        {
            window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FrameLength));
        }
        candidates = new float[FrameLength + 2 * Tolerance];
        template = new float[hop];
        tail = new float[hop];
        block = new float[hop];
        Reset(0, 1);
    }

    /// <summary>Samples in one frame; frames start half of this apart in the output.</summary>
    public int FrameLength { get; }

    /// <summary>How many samples a frame may move from its nominal position to line up with the previous one.</summary>
    public int Tolerance { get; }

    /// <summary>Input samples per output sample: 2 plays twice as fast.</summary>
    public double Rate { get; private set; } = 1;

    /// <summary>The input position the next output sample stands for: the start plus the output so far times the rate.</summary>
    public double Position => start + produced * Rate;

    /// <summary>Starts over: the next output begins exactly at <paramref name="inputPosition"/> and runs at <paramref name="rate"/>.</summary>
    public void Reset(double inputPosition, double rate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        start = inputPosition;
        Rate = rate;
        produced = 0;
        // Frame 0 sits exactly at the start. Its rising half would sound before it, so only
        // the falling half is kept, to overlap the first half of the next frame.
        previousStart = NominalStart(0);
        ReadInput(previousStart + hop, tail);
        for (var i = 0; i < hop; i++)
        {
            tail[i] *= window[hop + i];
        }
        frameIndex = 1;
        blockRead = hop;
    }

    /// <summary>Fills <paramref name="destination"/> with the next output samples.</summary>
    public void Read(Span<float> destination)
    {
        while (destination.Length > 0)
        {
            if (blockRead == hop)
            {
                NextBlock();
            }
            var count = Math.Min(hop - blockRead, destination.Length);
            block.AsSpan(blockRead, count).CopyTo(destination);
            blockRead += count;
            produced += count;
            destination = destination[count..];
        }
    }

    /// <summary>Where frame <paramref name="index"/> starts when nothing moves it: its centre on output time index × hop.</summary>
    long NominalStart(long index) => (long)Math.Floor(start + index * hop * Rate + 0.5) - hop;

    /// <summary>Places the next frame and overlaps its first half with the previous frame's second half.</summary>
    void NextBlock()
    {
        var nominal = NominalStart(frameIndex);
        var natural = previousStart + hop;
        long chosen;
        ReadOnlySpan<float> frame;
        if (Math.Abs(natural - nominal) <= Tolerance)
        {
            chosen = natural;
            frame = candidates.AsSpan(0, FrameLength);
            ReadInput(chosen, candidates.AsSpan(0, FrameLength));
        }
        else
        {
            var from = nominal - Tolerance;
            ReadInput(from, candidates);
            ReadInput(natural, template);
            var offset = BestOffset();
            chosen = from + offset;
            frame = candidates.AsSpan(offset, FrameLength);
        }
        for (var i = 0; i < hop; i++)
        {
            block[i] = tail[i] + window[i] * frame[i];
            tail[i] = window[hop + i] * frame[hop + i];
        }
        previousStart = chosen;
        frameIndex++;
        blockRead = 0;
    }

    /// <summary>
    /// The offset into <see cref="candidates"/> whose first half-frame is most like the
    /// template, by normalised cross-correlation; the nominal position when the template is silence.
    /// </summary>
    int BestOffset()
    {
        var templateEnergy = Energy(template);
        if (templateEnergy < hop * SilenceMeanSquare)
        {
            return Tolerance;
        }
        var best = Tolerance;
        var bestScore = double.NegativeInfinity;
        var energy = Energy(candidates.AsSpan(0, hop));
        for (var offset = 0; offset <= 2 * Tolerance; offset++)
        {
            if (offset > 0)
            {
                double gone = candidates[offset - 1];
                double added = candidates[offset + hop - 1];
                energy += added * added - gone * gone;
            }
            var correlation = Resampler.Dot(template, candidates.AsSpan(offset, hop));
            var score = correlation / Math.Sqrt(Math.Max(energy, 1e-12));
            if (score > bestScore)
            {
                bestScore = score;
                best = offset;
            }
        }
        return best;
    }

    static double Energy(ReadOnlySpan<float> samples)
    {
        var sum = 0.0;
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
        }
        return sum;
    }

    /// <summary>Reads the input with silence before its start and wherever the reader writes nothing.</summary>
    void ReadInput(long position, Span<float> destination)
    {
        destination.Clear();
        if (position < 0)
        {
            var skip = (int)Math.Min(-position, destination.Length);
            destination = destination[skip..];
            position += skip;
        }
        if (destination.Length > 0)
        {
            input(position, destination);
        }
    }
}
