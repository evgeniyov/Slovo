using Slovo.Core.Audio;
using Slovo.Core.Json;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Core.Tests.Meetings;

/// <summary>A temporary data folder for one test, removed afterwards.</summary>
public sealed class TemporaryMeetingsFolder : IDisposable
{
    public TemporaryMeetingsFolder()
    {
        Base = Path.Combine(Path.GetTempPath(), "SlovoMeetingsTests-" + Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Base);
        Paths.CreateFolders();
    }

    public string Base { get; }
    public AppPaths Paths { get; }

    public MeetingRecord Write(MeetingRecord record)
    {
        Directory.CreateDirectory(Paths.MeetingFolder(record.Id));
        File.WriteAllBytes(Path.Combine(Paths.MeetingFolder(record.Id), "meeting.json"), SlovoJson.SerializeToUtf8(record));
        return record;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Base, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Timers that fire only when the test advances the clock, on the test's own thread.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    readonly List<ManualTimer> timers = [];
    DateTimeOffset now = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public int ActiveTimers => timers.Count(t => t.Due is not null);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        now += by;
        foreach (var timer in timers.ToList())
        {
            if (timer.Due is { } due && due <= now)
            {
                timer.Due = null;
                timer.Callback(timer.State);
            }
        }
    }

    sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public DateTimeOffset? Due { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
            return true;
        }

        public void Dispose() => Due = null;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Collects posted work until the test runs it, like a UI thread that is busy.</summary>
public sealed class QueuedMainThread : IMainThread
{
    readonly Queue<Action> queue = new();

    public bool IsMainThread => true;
    public int Pending => queue.Count;

    public void Post(Action action) => queue.Enqueue(action);

    public void RunAll()
    {
        while (queue.TryDequeue(out var action))
        {
            action();
        }
    }
}

public static class MeetingAudioSamples
{
    /// <summary>A quiet tone written as a meeting track, like the Swift tests' <c>writeTone</c>.</summary>
    public static void WriteTone(string path, double seconds)
    {
        using var writer = CafWriter.Create(path);
        var samples = new float[(int)(seconds * MeetingAudioFormat.SampleRate)];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = (float)(0.2 * Math.Sin(index / 10.0));
        }
        writer.Append(samples);
        writer.Close();
    }
}
