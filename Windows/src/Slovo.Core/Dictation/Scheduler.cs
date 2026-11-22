using Slovo.Core.Platform;

namespace Slovo.Core.Dictation;

/// <summary>
/// Runs an action on the main thread after a delay. The dictation controller's timers (the
/// hold checks, the hands-free time limit, message lifetimes and the processing time limits)
/// go through it, so tests drive them with a manual clock instead of waiting; the Swift
/// controller used <c>Task.sleep</c> for the same timers.
/// </summary>
public interface IScheduler
{
    /// <summary>
    /// Calls <paramref name="action"/> on the main thread once <paramref name="seconds"/> have
    /// passed, unless the returned handle is disposed first. Call and dispose on the main thread.
    /// </summary>
    IDisposable Schedule(double seconds, Action action);
}

/// <summary>The real scheduler: a one-shot timer that posts its action to the main thread.</summary>
public sealed class MainThreadScheduler(IMainThread mainThread) : IScheduler
{
    /// <summary>System.Threading.Timer takes due times up to about 49.7 days.</summary>
    const double MaximumDelay = 49 * 24 * 3600;

    public IDisposable Schedule(double seconds, Action action)
    {
        var delay = double.IsFinite(seconds) ? Math.Clamp(seconds, 0, MaximumDelay) : MaximumDelay;
        var handle = new Handle(mainThread, action);
        handle.Start(TimeSpan.FromSeconds(delay));
        return handle;
    }

    /// <summary>
    /// Holds the timer (the timer's state holds the handle, so neither is collected while it
    /// waits). Disposing on the main thread wins over a firing already posted there.
    /// </summary>
    sealed class Handle(IMainThread mainThread, Action action) : IDisposable
    {
        Timer? timer;
        volatile bool isDone;

        public void Start(TimeSpan delay) =>
            timer = new Timer(static state => ((Handle)state!).Elapsed(), this, delay, Timeout.InfiniteTimeSpan);

        void Elapsed()
        {
            if (!isDone)
            {
                mainThread.Post(Run);
            }
        }

        void Run()
        {
            if (isDone)
            {
                return;
            }
            isDone = true;
            timer?.Dispose();
            action();
        }

        public void Dispose()
        {
            isDone = true;
            timer?.Dispose();
        }
    }
}
