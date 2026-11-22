using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Storage;

namespace Slovo.Core.Tests.Storage;

/// <summary>The debounce behind every store (SaveDebouncer of InputStoreTests) and StoreFile's flush and load warnings.</summary>
public sealed class SaveDebouncerTests : IDisposable
{
    readonly TemporaryFolder folder = new();
    readonly ManualTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_789_000_000));

    public void Dispose() => folder.Dispose();

    [Fact]
    public void CoalescesAndFlushes()
    {
        var runs = 0;
        var debouncer = new SaveDebouncer(() => runs++, ImmediateMainThread.Shared, clock, TimeSpan.FromMilliseconds(30));
        debouncer.Schedule();
        clock.Advance(TimeSpan.FromMilliseconds(20));
        debouncer.Schedule();
        debouncer.Schedule();
        Assert.True(debouncer.IsPending);
        clock.Advance(TimeSpan.FromMilliseconds(29));
        Assert.Equal(0, runs);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, runs);
        Assert.False(debouncer.IsPending);

        debouncer.Flush();
        Assert.Equal(1, runs);

        debouncer.Schedule();
        debouncer.Flush();
        Assert.Equal(2, runs);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, runs);

        debouncer.Schedule();
        debouncer.Cancel();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, runs);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public void TheActionRunsOnTheMainThread()
    {
        var runs = 0;
        var mainThread = new QueuedMainThread();
        var debouncer = new SaveDebouncer(() => runs++, mainThread, clock);
        Assert.Equal(TimeSpan.FromMilliseconds(500), debouncer.Delay);
        debouncer.Schedule();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, runs);
        Assert.Equal(1, mainThread.Pending);
        mainThread.Drain();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void AFireOvertakenByANewScheduleDoesNotRun()
    {
        var runs = 0;
        var mainThread = new QueuedMainThread();
        var debouncer = new SaveDebouncer(() => runs++, mainThread, clock);
        debouncer.Schedule();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        // The timer fired and posted, but a change came in before the UI thread got to it.
        debouncer.Schedule();
        mainThread.Drain();
        Assert.Equal(0, runs);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        mainThread.Drain();
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task WorksWithTheSystemClock()
    {
        var done = new TaskCompletionSource();
        var debouncer = new SaveDebouncer(() => done.TrySetResult(), ImmediateMainThread.Shared, delay: TimeSpan.FromMilliseconds(30));
        debouncer.Schedule();
        Assert.Same(done.Task, await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)));
    }

    // MARK: - StoreFile

    [Fact]
    public void StoreFileSavesAfterTheDebounceAndOnFlush()
    {
        var path = folder.Paths.NotesFile;
        var store = new StoreFile<List<Note>>(path, ImmediateMainThread.Shared, clock);
        Assert.Null(store.InitialValue);
        Assert.Null(store.LoadWarning);
        store.ScheduleSave([new Note { Text = "a" }]);
        Assert.True(store.HasPendingChanges);
        clock.Advance(TimeSpan.FromMilliseconds(499));
        Assert.False(File.Exists(path));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(File.Exists(path));
        Assert.False(store.HasPendingChanges);

        store.ScheduleSave([new Note { Text = "b" }]);
        store.Flush();
        Assert.Contains("\"b\"", File.ReadAllText(path));
        Assert.Equal(0, clock.ActiveTimers);
        store.Flush();
    }

    [Fact]
    public void AFailedWriteKeepsTheChangePending()
    {
        var blocker = Path.Combine(folder.Root, "blocker");
        File.WriteAllText(blocker, "x");
        var store = new StoreFile<List<Note>>(Path.Combine(blocker, "notes.json"), ImmediateMainThread.Shared, clock);
        store.ScheduleSave([]);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(store.HasPendingChanges);
        Assert.Throws<SlovoException>(store.Flush);
    }
}
