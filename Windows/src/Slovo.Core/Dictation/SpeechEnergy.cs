using Slovo.Core.Model;

namespace Slovo.Core.Dictation;

/// <summary>
/// Decides whether a recording contains anything worth sending to an engine (the pure
/// <c>SpeechEnergy</c> of the macOS <c>MicrophoneRecorder.swift</c>). Sums are kept in
/// single precision, as the Swift code does, so both apps decide alike. The level meter
/// arithmetic of the same Swift type lives in <see cref="Audio.LevelMeter"/>.
/// </summary>
public static class SpeechEnergy
{
    /// <summary>Recordings shorter than this are dropped as "Nothing heard".</summary>
    public const double MinimumDuration = 0.3;

    /// <summary>
    /// Frame RMS at or above this counts as sound, about −46 dBFS. Kept low on purpose:
    /// dropping quiet speech is worse than sending a click to an engine that returns nothing.
    /// </summary>
    public const float SpeechRms = 0.005f;

    internal const double FrameSeconds = 0.03;

    /// <summary>About 90 ms of sound in total, so a single click or pop is not speech.</summary>
    internal const int MinimumLoudFrames = 3;

    /// <summary>True when the audio is too short or has no frame loud enough to be speech.</summary>
    public static bool IsEffectivelySilent(RecordedAudio audio)
    {
        if (!(audio.SampleRate > 0) || audio.Duration < MinimumDuration)
        {
            return true;
        }
        ReadOnlySpan<float> samples = audio.Samples;
        var frameLength = Math.Max(1, (int)(FrameSeconds * audio.SampleRate));
        var loudFrames = 0;
        var start = 0;
        while (start < samples.Length)
        {
            var end = Math.Min(start + frameLength, samples.Length);
            if (VarianceRms(samples[start..end]) >= SpeechRms)
            {
                loudFrames += 1;
                if (loudFrames >= MinimumLoudFrames)
                {
                    return false;
                }
            }
            start = end;
        }
        return true;
    }

    /// <summary>
    /// RMS around the window's mean, so a constant DC offset from a cheap interface does
    /// not look like sound.
    /// </summary>
    internal static float VarianceRms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }
        float sum = 0;
        float squares = 0;
        foreach (var sample in samples)
        {
            sum += sample;
            squares += sample * sample;
        }
        float count = samples.Length;
        var mean = sum / count;
        return MathF.Sqrt(Math.Max(0, squares / count - mean * mean));
    }
}
