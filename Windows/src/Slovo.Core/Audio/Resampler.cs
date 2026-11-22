using System.Numerics;
using System.Runtime.InteropServices;

namespace Slovo.Core.Audio;

/// <summary>
/// Streaming sample-rate conversion of mono float samples, by default to the 16 kHz every
/// engine and every meeting track uses. The Windows counterpart of the macOS app's
/// <c>MeetingResampler</c> and <c>CaptureConverter</c> resampling (AVAudioConverter).
/// </summary>
/// <remarks>
/// <para>
/// A polyphase windowed-sinc filter: the rate ratio is reduced to <c>up/down</c> (for
/// 44.1 kHz → 16 kHz that is 160/441) and every output sample is a dot product of the
/// input around its position with one phase of a Kaiser-windowed sinc. The cut-off sits
/// at 95 % of the lower Nyquist frequency with a transition band of 10 % of it and 80 dB
/// of stop-band attenuation, so for 48 kHz → 16 kHz everything below 7.2 kHz passes
/// untouched and everything above 8 kHz, which would fold back into the speech band,
/// is removed. Each phase is normalised to unity gain, so DC and low frequencies come out
/// at exactly their input level.
/// </para>
/// <para>
/// Ratios whose reduced numerator exceeds <see cref="MaximumPhases"/> (odd rates such as
/// 44 056 Hz) interpolate linearly between neighbouring phases instead of storing one
/// per position.
/// </para>
/// <para>
/// State carries across <see cref="Process"/> calls, so chunk borders do not click. The
/// filter looks <see cref="Latency"/> input samples ahead, which <see cref="Flush"/>
/// drains at the end: the total output of a stream is <c>ceil(inputs × target / source)</c>
/// samples, and output sample <c>n</c> lies at input position <see cref="SourcePosition"/>.
/// Not thread-safe: one capture callback or one reader uses it at a time.
/// </para>
/// </remarks>
public sealed class Resampler
{
    public const int DefaultTargetRate = 16_000;
    /// <summary>Phases stored before switching to interpolation between phases.</summary>
    public const int MaximumPhases = 1024;
    const double StopbandAttenuation = 80;
    /// <summary>Fraction of the lower Nyquist frequency where the stop band begins.</summary>
    const double StopbandEdge = 1.0;
    /// <summary>Width of the transition band as a fraction of the lower Nyquist frequency.</summary>
    const double TransitionWidth = 0.1;

    readonly int up;
    readonly int down;
    readonly int halfTaps;
    readonly int taps;
    readonly int phaseCount;
    readonly bool exactPhases;
    readonly float[] table;
    readonly bool passthrough;

    float[] buffer;
    int bufferLength;
    /// <summary>The absolute input index of <c>buffer[0]</c>; negative while the leading zeros are in it.</summary>
    long bufferStart;
    long inputCount;
    long outputCount;

    public Resampler(int sourceRate, int targetRate = DefaultTargetRate)
    {
        if (sourceRate <= 0 || sourceRate > 1_536_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRate), sourceRate, "Unsupported sample rate.");
        }
        if (targetRate <= 0 || targetRate > 1_536_000)
        {
            throw new ArgumentOutOfRangeException(nameof(targetRate), targetRate, "Unsupported sample rate.");
        }
        SourceRate = sourceRate;
        TargetRate = targetRate;
        var divisor = GreatestCommonDivisor(sourceRate, targetRate);
        up = targetRate / divisor;
        down = sourceRate / divisor;
        passthrough = sourceRate == targetRate;
        if (passthrough)
        {
            table = [];
            buffer = [];
            return;
        }

        var lowerNyquist = Math.Min(sourceRate, targetRate) / 2.0;
        var transition = TransitionWidth * lowerNyquist;
        var cutoff = (StopbandEdge - TransitionWidth / 2) * lowerNyquist;
        // Kaiser's estimate of the filter length for this attenuation and transition width,
        // as a duration, then as half a window in input samples.
        var seconds = (StopbandAttenuation - 7.95) / (2.285 * 2 * Math.PI * transition);
        var halfWidth = seconds / 2 * sourceRate;
        halfTaps = Math.Max(2, (int)Math.Ceiling(halfWidth));
        taps = 2 * halfTaps;
        exactPhases = up <= MaximumPhases;
        phaseCount = exactPhases ? up : MaximumPhases;
        table = BuildTable(cutoff / sourceRate, halfWidth, KaiserBeta(StopbandAttenuation));
        buffer = new float[Math.Max(4096, taps * 2)];
        Reset();
    }

    public int SourceRate { get; }
    public int TargetRate { get; }

    /// <summary>Input samples the filter reads beyond an output's position; the output lags by this much until <see cref="Flush"/>.</summary>
    public int Latency => passthrough ? 0 : halfTaps;

    /// <summary>Taps per output sample (0 when the rates match).</summary>
    public int TapCount => taps;

    /// <summary>Input samples consumed since the start or the last <see cref="Flush"/>.</summary>
    public long InputCount => inputCount;

    /// <summary>Output samples produced since the start or the last <see cref="Flush"/>.</summary>
    public long OutputCount => outputCount;

    /// <summary>The position, in input samples since the start of the stream, of output sample <paramref name="outputIndex"/>.</summary>
    public double SourcePosition(long outputIndex) => (double)outputIndex * down / up;

    /// <summary>Converts a whole signal at once.</summary>
    public static float[] Resample(ReadOnlySpan<float> input, int sourceRate, int targetRate = DefaultTargetRate)
    {
        var resampler = new Resampler(sourceRate, targetRate);
        var body = resampler.Process(input);
        var tail = resampler.Flush();
        if (tail.Length == 0)
        {
            return body;
        }
        var result = new float[body.Length + tail.Length];
        body.CopyTo(result, 0);
        tail.CopyTo(result, body.Length);
        return result;
    }

    /// <summary>Feeds samples and returns every output sample they complete.</summary>
    public float[] Process(ReadOnlySpan<float> input)
    {
        if (passthrough)
        {
            inputCount += input.Length;
            outputCount += input.Length;
            return input.ToArray();
        }
        if (input.IsEmpty)
        {
            return [];
        }
        Append(input);
        inputCount += input.Length;
        return Produce(inputCount);
    }

    /// <summary>
    /// Returns the output the look-ahead still holds back, as if the input were followed
    /// by silence, and starts a new stream.
    /// </summary>
    public float[] Flush()
    {
        if (passthrough)
        {
            inputCount = 0;
            outputCount = 0;
            return [];
        }
        var end = inputCount;
        Append(new float[halfTaps + 1]);
        var tail = Produce(end, allowPastInput: true);
        Reset();
        return tail;
    }

    /// <summary>Forgets the stream without producing anything.</summary>
    public void Reset()
    {
        if (passthrough)
        {
            inputCount = 0;
            outputCount = 0;
            return;
        }
        // The samples before the first one are silence.
        Array.Clear(buffer);
        bufferLength = halfTaps - 1;
        bufferStart = -(halfTaps - 1);
        inputCount = 0;
        outputCount = 0;
    }

    /// <summary>
    /// Produces outputs whose position is below <paramref name="end"/>. Unless
    /// <paramref name="allowPastInput"/>, only those whose whole window has arrived.
    /// </summary>
    float[] Produce(long end, bool allowPastInput = false)
    {
        var available = bufferStart + bufferLength;
        var results = new List<float>();
        var weights = new float[taps];
        while (true)
        {
            var numerator = outputCount * down;
            var index = numerator / up;
            var phase = (int)(numerator % up);
            if (index >= end)
            {
                break;
            }
            if (index + halfTaps >= available)
            {
                if (!allowPastInput)
                {
                    break;
                }
                throw new InvalidOperationException("Resampler look-ahead ran past the buffered input.");
            }
            var row = Weights(phase, weights);
            var start = (int)(index - halfTaps + 1 - bufferStart);
            results.Add(Dot(row, buffer.AsSpan(start, taps)));
            outputCount++;
        }
        Compact();
        return [.. results];
    }

    ReadOnlySpan<float> Weights(int phase, float[] scratch)
    {
        if (exactPhases)
        {
            return table.AsSpan(phase * taps, taps);
        }
        var position = (double)phase * phaseCount / up;
        var lower = (int)position;
        var fraction = (float)(position - lower);
        var a = table.AsSpan(lower * taps, taps);
        var b = table.AsSpan((lower + 1) * taps, taps);
        for (var i = 0; i < taps; i++)
        {
            scratch[i] = a[i] + (b[i] - a[i]) * fraction;
        }
        return scratch;
    }

    void Append(ReadOnlySpan<float> input)
    {
        if (bufferLength + input.Length > buffer.Length)
        {
            var grown = new float[Math.Max(buffer.Length * 2, bufferLength + input.Length)];
            buffer.AsSpan(0, bufferLength).CopyTo(grown);
            buffer = grown;
        }
        input.CopyTo(buffer.AsSpan(bufferLength));
        bufferLength += input.Length;
    }

    /// <summary>Drops the input no future output reads.</summary>
    void Compact()
    {
        var nextIndex = outputCount * down / up;
        var keepFrom = nextIndex - halfTaps + 1;
        var drop = (int)Math.Clamp(keepFrom - bufferStart, 0, bufferLength);
        if (drop == 0)
        {
            return;
        }
        buffer.AsSpan(drop, bufferLength - drop).CopyTo(buffer);
        bufferLength -= drop;
        bufferStart += drop;
    }

    /// <summary>
    /// One row of <see cref="taps"/> weights per phase <c>f = r / phaseCount</c>, plus a row
    /// for <c>f = 1</c> to interpolate towards. Weight <c>m</c> of a row applies to input
    /// <c>index − halfTaps + 1 + m</c>, at distance <c>f + halfTaps − 1 − m</c> from the output.
    /// </summary>
    float[] BuildTable(double normalizedCutoff, double halfWidth, double beta)
    {
        var rows = phaseCount + 1;
        var result = new float[rows * taps];
        var twoCutoff = 2 * normalizedCutoff;
        var i0Beta = BesselI0(beta);
        var row = new double[taps];
        for (var r = 0; r < rows; r++)
        {
            var fraction = (double)r / phaseCount;
            var sum = 0.0;
            for (var m = 0; m < taps; m++)
            {
                var distance = fraction + halfTaps - 1 - m;
                var ratio = distance / halfWidth;
                var window = Math.Abs(ratio) >= 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - ratio * ratio)) / i0Beta;
                var value = twoCutoff * Sinc(twoCutoff * distance) * window;
                row[m] = value;
                sum += value;
            }
            for (var m = 0; m < taps; m++)
            {
                result[r * taps + m] = (float)(row[m] / sum);
            }
        }
        return result;
    }

    internal static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = 0f;
        var i = 0;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var accumulator = Vector<float>.Zero;
            var va = MemoryMarshal.Cast<float, Vector<float>>(a);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
            for (var v = 0; v < va.Length; v++)
            {
                accumulator += va[v] * vb[v];
            }
            sum = Vector.Sum(accumulator);
            i = va.Length * Vector<float>.Count;
        }
        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    static double Sinc(double x) => Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

    /// <summary>Kaiser's β for a stop-band attenuation in dB.</summary>
    static double KaiserBeta(double attenuation) => attenuation > 50
        ? 0.1102 * (attenuation - 8.7)
        : attenuation >= 21 ? 0.5842 * Math.Pow(attenuation - 21, 0.4) + 0.07886 * (attenuation - 21) : 0;

    /// <summary>The modified Bessel function of the first kind, order zero, by its power series.</summary>
    static double BesselI0(double x)
    {
        var sum = 1.0;
        var term = 1.0;
        var half = x / 2;
        for (var k = 1; k < 64; k++)
        {
            term *= half / k;
            var squared = term * term;
            sum += squared;
            if (squared < sum * 1e-17)
            {
                break;
            }
        }
        return sum;
    }

    static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }
        return a;
    }
}
