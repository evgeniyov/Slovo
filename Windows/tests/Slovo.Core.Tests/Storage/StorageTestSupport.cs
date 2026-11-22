using System.ComponentModel;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Core.Tests.Storage;

/// <summary>A clock tests move by hand; timers fire as it passes their due time, on the thread that advances it.</summary>
sealed class ManualTimeProvider(DateTimeOffset start, TimeZoneInfo? zone = null) : TimeProvider
{
    readonly Lock gate = new();
    readonly List<ManualTimer> timers = [];
    DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public override TimeZoneInfo LocalTimeZone => zone ?? TimeZoneInfo.Utc;

    public int ActiveTimers
    {
        get
        {
            lock (gate)
            {
                return timers.Count(timer => timer.Due is not null);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
        }
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward, firing every timer that comes due on the way, in order.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (gate)
        {
            target = now + by;
        }
        while (true)
        {
            ManualTimer? next;
            lock (gate)
            {
                next = timers.Where(timer => timer.Due is { } due && due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
                if (next is null)
                {
                    now = target;
                    return;
                }
                now = next.Due!.Value;
                next.Due = next.Period is { } period && period > TimeSpan.Zero ? now + period : null;
            }
            next.Fire();
        }
    }

    sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }
        public TimeSpan? Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? null : period;
            }
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner.gate)
            {
                Due = null;
                owner.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A UI thread that runs posted work only when the test drains it.</summary>
sealed class QueuedMainThread : IMainThread
{
    readonly Queue<Action> actions = new();
    public bool IsMainThread => true;
    public int Pending => actions.Count;
    public void Post(Action action) => actions.Enqueue(action);

    public void Drain()
    {
        while (actions.TryDequeue(out var action))
        {
            action();
        }
    }
}

/// <summary>A temporary data folder, removed afterwards.</summary>
sealed class TemporaryFolder : IDisposable
{
    public TemporaryFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "SlovoStorageTests-" + Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Root);
        Paths.CreateFolders();
    }

    public string Root { get; }
    public AppPaths Paths { get; }

    public IReadOnlyList<string> Files(string prefix) =>
        Directory.GetFiles(Root).Select(Path.GetFileName).OfType<string>().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Records what retention asked the meetings store to do.</summary>
sealed class FakeMeetingStore(IEnumerable<MeetingRecord> meetings) : IMeetingStore
{
    readonly List<MeetingRecord> list = [.. meetings];

    public List<Guid> DeletedAudio { get; } = [];
    public int Flushes { get; private set; }
    public IReadOnlyList<MeetingRecord> Meetings => list;
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public MeetingRecord? Meeting(Guid id) => list.FirstOrDefault(meeting => meeting.Id == id);

    public MeetingRecord Create(string title)
    {
        var record = new MeetingRecord { Title = title };
        list.Insert(0, record);
        return record;
    }

    public void Update(MeetingRecord meeting)
    {
        var index = list.FindIndex(existing => existing.Id == meeting.Id);
        if (index >= 0)
        {
            list[index] = meeting;
        }
    }

    public void Delete(Guid id) => list.RemoveAll(meeting => meeting.Id == id);

    public void DeleteAudio(Guid id)
    {
        DeletedAudio.Add(id);
        var index = list.FindIndex(existing => existing.Id == id);
        if (index >= 0)
        {
            list[index] = list[index] with { HasAudio = false, HasScreenRecording = false };
        }
    }

    public void SetClaudeNotes(MeetingNotes? notes, Guid id) { }
    public void RefreshFromDisk() { }
    public string MeetingsFolder => Path.GetTempPath();
    public string Folder(Guid id) => Path.Combine(MeetingsFolder, id.ToString("D").ToUpperInvariant());
    public string MicrophoneAudioPath(Guid id) => Path.Combine(Folder(id), "mic.caf");
    public string SystemAudioPath(Guid id) => Path.Combine(Folder(id), "system.caf");
    public string TranscriptPath(Guid id) => Path.Combine(Folder(id), "transcript.md");
    public void Flush() => Flushes++;
}
