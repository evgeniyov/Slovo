namespace Slovo.Core.Audio;

/// <summary>
/// Turns a stream of 16 kHz samples into about 30 meter values per second (the macOS
/// app's <c>LevelMeter</c> in MeetingAudio.swift), plus the level arithmetic both apps
/// share: RMS, the two decibel scales and the smoothing of the dictation meter.
/// </summary>
/// <remarks>Not thread-safe; the static members are.</remarks>
public sealed class LevelMeter
{
    /// <summary>About 50 ms at 16 kHz, the window the dictation level reads (CaptureBuffer.meterWindow).</summary>
    public const int DictationWindow = 800;

    float sum;
    int count;

    public LevelMeter(int sampleRate = 16_000, int updatesPerSecond = 30)
    {
        WindowLength = Math.Max(1, sampleRate / Math.Max(1, updatesPerSecond));
    }

    public int WindowLength { get; }

    /// <summary>Feeds samples and returns the level (<see cref="MeetingLevel"/>) of the last window they completed, if any.</summary>
    public float? Consume(ReadOnlySpan<float> samples)
    {
        float? level = null;
        foreach (var sample in samples)
        {
            sum += sample * sample;
            count++;
            if (count == WindowLength)
            {
                level = MeetingLevel(MathF.Sqrt(sum / count));
                sum = 0;
                count = 0;
            }
        }
        return level;
    }

    /// <summary>Root mean square of a window of samples; 0 for an empty one.</summary>
    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }
        var sum = 0.0;
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
        }
        return (float)Math.Sqrt(sum / samples.Length);
    }

    /// <summary>
    /// The meeting meter (MeetingAudioMath.level): −50 dBFS and below is empty, full scale
    /// is 1. Speech usually lands around the middle.
    /// </summary>
    public static float MeetingLevel(float rms)
    {
        if (rms <= 0)
        {
            return 0;
        }
        var decibels = 20 * MathF.Log10(rms);
        return Math.Clamp((decibels + 50) / 50, 0, 1);
    }

    /// <summary>
    /// The dictation meter (SpeechEnergy.meterLevel): −55 dBFS and below is empty, −15 dBFS
    /// and above is full, which spreads normal speech over most of the bar.
    /// </summary>
    public static float DictationLevel(float rms)
    {
        if (rms <= 0)
        {
            return 0;
        }
        var decibels = 20 * MathF.Log10(rms);
        return Math.Clamp((decibels + 55) / 40, 0, 1);
    }

    /// <summary>Rises quickly so syllables show at once, falls gently so bars do not flicker (MicrophoneRecorder.smoothedLevel).</summary>
    public static float Smoothed(float previous, float target)
    {
        var factor = target > previous ? 0.6f : 0.2f;
        return previous + (target - previous) * factor;
    }
}
