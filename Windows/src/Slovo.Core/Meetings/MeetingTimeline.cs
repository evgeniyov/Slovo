using Slovo.Core.Audio;

namespace Slovo.Core.Meetings;

/// <summary>
/// The 16 kHz timeline every track of a meeting shares (the timing half of the macOS
/// <c>MeetingTrackWriter</c>). Its origin is the <see cref="Model.IUptime"/> reading taken
/// when the recording starts; a sample captured at uptime <c>t</c> belongs at frame
/// <see cref="Frame"/>(<c>t</c>, origin) plus whatever silence was added for sleeps, which
/// the uptime clock may not count.
/// </summary>
public static class MeetingTimeline
{
    public const int SampleRate = MeetingAudioFormat.SampleRate;
    /// <summary>Gaps and overlaps shorter than this are ordinary scheduling jitter and are left alone.</summary>
    public const double GapTolerance = 0.1;
    /// <summary>Protects the disk from a bogus timestamp: no single buffer fills more than this.</summary>
    public const double MaximumGap = 3600;
    /// <summary>
    /// The most one sleep pad writes at once. A sleep is measured by the clocks rather than read
    /// from a buffer, so it may exceed <see cref="MaximumGap"/>; a longer one is padded in parts.
    /// </summary>
    public const double MaximumSleepPad = 48 * 3600;
    /// <summary>Disk bytes one second of a track takes: 16 kHz of 16-bit samples.</summary>
    public const long BytesPerSecond = MeetingAudioFormat.BytesPerSecond;

    /// <summary>
    /// Where a time falls on the timeline that starts at <paramref name="origin"/> (both in
    /// uptime seconds); 0 for anything at or before the origin. A micro-frame of slack keeps a
    /// time such as 3.3 s, which doubles hold as 3.29999…, on its own frame.
    /// </summary>
    public static long Frame(double time, double origin)
    {
        var seconds = time - origin;
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return 0;
        }
        return (long)Math.Floor(seconds * SampleRate + 1e-6);
    }

    /// <summary>Whole frames in a number of seconds; 0 for anything negative or not finite.</summary>
    public static long Frames(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 ? (long)Math.Floor(seconds * SampleRate + 1e-6) : 0;

    /// <summary>
    /// What an interval is worth as padding: never negative, and never more than the gap
    /// safeguard allows to be written at once.
    /// </summary>
    public static double PadSeconds(double seconds) =>
        double.IsFinite(seconds) ? Math.Min(Math.Max(0, seconds), MaximumGap) : 0;
}

/// <summary>Sample arithmetic of meeting recording (the parts of the macOS <c>MeetingAudioMath</c> the recorder uses).</summary>
public static class MeetingAudioMath
{
    /// <summary>
    /// How much louder the meeting microphone track is made before it reaches disk: enough to
    /// lift a quiet or distant voice without making normal speech uncomfortably loud.
    /// </summary>
    public const float MicrophoneBoostDecibels = 9;

    static readonly float MicrophoneBoostGain = MathF.Pow(10, MicrophoneBoostDecibels / 20);

    /// <summary>
    /// Boosts every sample by the microphone gain, then softens whatever that boost pushes past
    /// full scale with <c>tanh</c> instead of hard-clipping it, so a raised voice compresses
    /// instead of crackling. Near silence the boost is close to linear; <c>tanh</c> only bends
    /// it as samples approach ±1.
    /// </summary>
    public static float[] MicrophoneBoost(ReadOnlySpan<float> samples)
    {
        var boosted = new float[samples.Length];
        for (var index = 0; index < samples.Length; index++)
        {
            boosted[index] = MathF.Tanh(samples[index] * MicrophoneBoostGain);
        }
        return boosted;
    }

    /// <summary>Rises quickly and falls slowly, so the meeting meter reads like speech rather than flicker.</summary>
    public static float SmoothedLevel(float current, float target)
    {
        var factor = target > current ? 0.6f : 0.25f;
        return current + (target - current) * factor;
    }

    /// <summary>Whether every sample is zero (NaN counts as sound, as in Swift).</summary>
    public static bool IsSilent(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            if (sample != 0)
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>What <see cref="SilenceDetector"/> reports.</summary>
public enum SilenceEvent
{
    /// <summary>Only zeros, or nothing at all, since the start for the whole timeout.</summary>
    Silence,
    /// <summary>Real audio after <see cref="Silence"/> was reported.</summary>
    SignalAfterSilence,
}

/// <summary>
/// Decides when system audio has delivered nothing but silence for long enough to say so
/// (port of the macOS <c>SilenceDetector</c>). On macOS that means System Audio Recording is
/// most likely denied; on Windows loopback delivers nothing while nothing plays, so it usually
/// means nothing is playing yet, or the call plays on another device. Not thread-safe.
/// </summary>
public sealed class SilenceDetector(double timeout)
{
    double silentSeconds;

    public double Timeout { get; } = timeout;
    public bool HeardSignal { get; private set; }
    public bool ReportedSilence { get; private set; }

    public SilenceEvent? Consume(int frames, double sampleRate, bool allZero)
    {
        if (frames <= 0 || sampleRate <= 0)
        {
            return null;
        }
        if (!allZero)
        {
            var recovering = ReportedSilence && !HeardSignal;
            HeardSignal = true;
            return recovering ? SilenceEvent.SignalAfterSilence : null;
        }
        if (HeardSignal || ReportedSilence)
        {
            return null;
        }
        silentSeconds += frames / sampleRate;
        if (silentSeconds >= Timeout)
        {
            ReportedSilence = true;
            return SilenceEvent.Silence;
        }
        return null;
    }

    /// <summary>For when no buffers arrived at all during the first seconds.</summary>
    public SilenceEvent? NoAudio(double seconds)
    {
        if (HeardSignal || ReportedSilence || seconds < Timeout)
        {
            return null;
        }
        ReportedSilence = true;
        return SilenceEvent.Silence;
    }
}
