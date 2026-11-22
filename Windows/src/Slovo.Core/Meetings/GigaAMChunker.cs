namespace Slovo.Core.Meetings;

/// <summary>
/// Splits long recordings into pieces short enough for GigaAM (port of the macOS
/// <c>GigaAMChunker</c>; <see cref="SpeechSegmenter"/> runs the same cut on its energy
/// envelope).
///
/// GigaAM is trained on segments up to about 25 seconds and degrades on longer input.
/// Instead of cutting at a fixed offset, which can split a word, each cut goes to the
/// quietest moment near the end of the window.
/// </summary>
public static class GigaAMChunker
{
    /// <summary>Sample ranges <c>(Start, End)</c> that cover <paramref name="samples"/> in order without gaps or overlaps.</summary>
    /// <param name="maxSeconds">Upper bound for every chunk.</param>
    /// <param name="searchSeconds">How far back from the end of a window to look for a pause.</param>
    /// <param name="frameSeconds">Length of the frames whose RMS is compared.</param>
    /// <param name="smoothingFrames">
    /// Neighbours on each side averaged into a frame's score, so the short closure of a stop
    /// consonant does not look like a pause.
    /// </param>
    public static IReadOnlyList<(int Start, int End)> Ranges(ReadOnlySpan<float> samples, int sampleRate = 16_000, double maxSeconds = 20,
                                                             double searchSeconds = 5, double frameSeconds = 0.03, int smoothingFrames = 5)
    {
        if (samples.IsEmpty)
        {
            return [];
        }
        var maxLength = (int)(maxSeconds * sampleRate);
        if (maxLength <= 0 || samples.Length <= maxLength)
        {
            return [(0, samples.Length)];
        }

        var frameLength = Math.Max(1, (int)(frameSeconds * sampleRate));
        var searchLength = Math.Min(maxLength, Math.Max(frameLength, (int)(searchSeconds * sampleRate)));

        var ranges = new List<(int Start, int End)>();
        var start = 0;
        while (samples.Length - start > maxLength)
        {
            var windowEnd = start + maxLength;
            var quietest = QuietestPoint(samples, windowEnd - searchLength, windowEnd, frameLength, Math.Max(0, smoothingFrames));
            var cut = Math.Min(Math.Max(quietest, start + 1), windowEnd);
            ranges.Add((start, cut));
            start = cut;
        }
        ranges.Add((start, samples.Length));
        return ranges;
    }

    /// <summary>The middle of the frame with the lowest smoothed RMS in <c>regionStart..&lt;regionEnd</c>; ties go to the later frame.</summary>
    public static int QuietestPoint(ReadOnlySpan<float> samples, int regionStart, int regionEnd, int frameLength, int smoothingFrames)
    {
        var frameCount = (regionEnd - regionStart) / frameLength;
        if (frameCount <= 0)
        {
            return regionEnd;
        }

        var rms = new double[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var first = regionStart + frame * frameLength;
            double sum = 0;
            for (var index = first; index < first + frameLength; index++)
            {
                var value = (double)samples[index];
                sum += value * value;
            }
            rms[frame] = Math.Sqrt(sum / frameLength);
        }

        var prefix = new double[frameCount + 1];
        for (var frame = 0; frame < frameCount; frame++)
        {
            prefix[frame + 1] = prefix[frame] + rms[frame];
        }

        var best = 0;
        var bestScore = double.PositiveInfinity;
        for (var frame = 0; frame < frameCount; frame++)
        {
            var low = Math.Max(0, frame - smoothingFrames);
            var high = Math.Min(frameCount - 1, frame + smoothingFrames);
            var score = (prefix[high + 1] - prefix[low]) / (high - low + 1);
            if (score <= bestScore)
            {
                best = frame;
                bestScore = score;
            }
        }
        return regionStart + best * frameLength + frameLength / 2;
    }
}
