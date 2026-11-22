using System.Collections.Concurrent;
using Slovo.Core.Dictation;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Core.Tests.Dictation;

/// <summary>
/// A UI thread for tests: <see cref="RunAsync"/> runs the test body on a dedicated thread
/// that pumps posted work, with a SynchronizationContext, so the controller's state is only
/// ever touched there, as on the real UI thread. Work posted after the body finished is
/// dropped.
/// </summary>
sealed class TestMainThread : IMainThread
{
    /// <summary>How long the thread may wait for work before the test fails as hung.</summary>
    static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);

    readonly BlockingCollection<Action> queue = new();
    volatile Thread? thread;

    public bool IsMainThread => Thread.CurrentThread == thread;

    public void Post(Action action) => queue.Add(action);

    public Task RunAsync(Func<Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new Thread(() => Pump(body, done)) { IsBackground = true, Name = "Test main thread" };
        thread = pump;
        pump.Start();
        return done.Task;
    }

    public Task RunAsync(Action body) => RunAsync(() =>
    {
        body();
        return Task.CompletedTask;
    });

    void Pump(Func<Task> body, TaskCompletionSource done)
    {
        SynchronizationContext.SetSynchronizationContext(new Context(this));
        Exception? stray = null;
        Task task;
        try
        {
            task = body();
        }
        catch (Exception error)
        {
            task = Task.FromException(error);
        }
        var finished = false;
        task.ContinueWith(_ => Post(() => finished = true), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        while (!finished)
        {
            if (!queue.TryTake(out var action, IdleTimeout))
            {
                stray ??= new TimeoutException("The test's main thread waited 10 s for work: something never completed.");
                break;
            }
            try
            {
                action();
            }
            catch (Exception error)
            {
                stray ??= error;
            }
        }
        SynchronizationContext.SetSynchronizationContext(null);
        if (task.IsFaulted)
        {
            done.SetException(task.Exception!.InnerExceptions);
        }
        else if (stray is not null)
        {
            done.SetException(stray);
        }
        else if (task.IsCanceled)
        {
            done.SetCanceled();
        }
        else
        {
            done.SetResult();
        }
    }

    sealed class Context(TestMainThread owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => owner.Post(() => d(state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public override SynchronizationContext CreateCopy() => this;
    }
}

/// <summary>
/// Uptime and timers for tests: time stands still until <see cref="Advance"/> moves it,
/// firing due timers in order, on the calling thread.
/// </summary>
sealed class ManualClock : IUptime, IScheduler
{
    readonly List<Entry> entries = [];
    long sequence;

    public double Now { get; private set; } = 1_000;

    public int PendingTimers => entries.Count(e => !e.IsDone);

    public IDisposable Schedule(double seconds, Action action)
    {
        var entry = new Entry(Now + Math.Max(0, seconds), sequence++, action);
        entries.Add(entry);
        return entry;
    }

    public void Advance(double seconds)
    {
        var target = Now + seconds;
        while (entries.Where(e => !e.IsDone && e.Due <= target).OrderBy(e => e.Due).ThenBy(e => e.Sequence).FirstOrDefault() is { } next)
        {
            entries.Remove(next);
            Now = Math.Max(Now, next.Due);
            next.Fire();
        }
        Now = target;
        entries.RemoveAll(e => e.IsDone);
    }

    sealed class Entry(double due, long sequence, Action action) : IDisposable
    {
        public double Due => due;
        public long Sequence => sequence;
        public bool IsDone { get; private set; }

        public void Fire()
        {
            if (IsDone)
            {
                return;
            }
            IsDone = true;
            action();
        }

        public void Dispose() => IsDone = true;
    }
}
