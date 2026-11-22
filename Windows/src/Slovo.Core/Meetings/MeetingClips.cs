using Slovo.Core.Audio;

namespace Slovo.Core.Meetings;

/// <summary>
/// Reading meeting audio for the processing pipeline (the file half of the macOS
/// <c>MeetingClip</c>): whole tracks for the diarizer and the inbox, and the sample range
/// of a planned clip. The engines take samples, so no temporary WAV is written.
/// </summary>
internal static class MeetingClips
{
    const int BlockFrames = MeetingAudioFormat.SampleRate;

    /// <summary>
    /// Every sample of a CAF or WAV file on the 16 kHz timeline, whatever the file's own rate
    /// and channel count. <paramref name="expectedCount"/> sizes the buffer when known (the
    /// envelope's sample count). Throws SlovoException, OperationCanceledException.
    /// </summary>
    public static float[] ReadAll(string path, long expectedCount = 0, CancellationToken cancellation = default)
    {
        using var reader = MeetingTrackReader.Open(path);
        var samples = new float[(int)Math.Clamp(expectedCount, 0, Array.MaxLength)];
        var count = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var block = reader.Read(BlockFrames);
            if (block.Length == 0)
            {
                break;
            }
            if (count + block.Length > samples.Length)
            {
                var grown = (int)Math.Min(Array.MaxLength, Math.Max((long)samples.Length * 2, (long)count + block.Length));
                Array.Resize(ref samples, grown);
            }
            block.CopyTo(samples, count);
            count += block.Length;
        }
        return count == samples.Length ? samples : samples[..count];
    }

    /// <summary>The 16 kHz sample indexes of a stretch of time, rounded half away from zero as Swift's <c>rounded()</c>.</summary>
    public static SampleRange SampleRange(TimeRange range)
    {
        const double rate = MeetingAudioFormat.SampleRate;
        var lower = Math.Max(0L, (long)Math.Round(range.Lower * rate, MidpointRounding.AwayFromZero));
        var upper = Math.Max(lower, (long)Math.Round(range.Upper * rate, MidpointRounding.AwayFromZero));
        return new SampleRange(lower, upper);
    }
}
