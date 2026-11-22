namespace Slovo.Core.Model;

/// <summary>An audio input device. On Windows the id is the MMDevice endpoint id.</summary>
public sealed record AudioInputDevice(string Uid, string Name);

/// <summary>A finished microphone recording: 16 kHz mono float samples.</summary>
public sealed record RecordedAudio(float[] Samples, double SampleRate = 16_000)
{
    public double Duration => SampleRate > 0 ? Samples.Length / SampleRate : 0;
    public static readonly RecordedAudio Empty = new([]);
}

/// <summary>A monotonic time source in seconds; tests inject a fake to drive timeouts.</summary>
public interface IUptime
{
    double Now { get; }
}

public sealed class SystemUptime : IUptime
{
    public static readonly SystemUptime Shared = new();
    public double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}

/// <summary>An error whose message is already localized.</summary>
public sealed class SlovoException(string message, Exception? inner = null) : Exception(message, inner);
