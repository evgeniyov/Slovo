using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Slovo.Core.Logging;

namespace Slovo.Platform.Windows.Threading;

/// <summary>
/// A serial queue on a thread of its own (the counterpart of a serial DispatchQueue):
/// actions run one at a time, in the order they were posted. Audio device work runs here,
/// never on the UI thread, because opening a device can block for seconds while a
/// Bluetooth headset switches profiles. The thread is MTA, which is what Core Audio and
/// UI Automation clients want.
/// </summary>
public sealed class WorkerQueue : IDisposable
{
    readonly BlockingCollection<Action> actions = new(new ConcurrentQueue<Action>());
    readonly Thread thread;
    readonly Logger log;
    readonly Lock timerGate = new();
    readonly HashSet<Timer> timers = [];
    volatile bool disposed;

    public WorkerQueue(string name)
    {
        log = Log.For(name);
        thread = new Thread(Run) { IsBackground = true, Name = name };
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.MTA);
        }
        thread.Start();
    }

    public bool IsCurrentThread => Thread.CurrentThread == thread;

    /// <summary>Runs an action on the queue after everything posted before it.</summary>
    public void Post(Action action)
    {
        if (disposed)
        {
            return;
        }
        try
        {
            actions.Add(action);
        }
        catch (InvalidOperationException)
        {
            // Completed while being disposed.
        }
    }

    /// <summary>Posts an action after a delay.</summary>
    public void PostAfter(TimeSpan delay, Action action)
    {
        if (disposed)
        {
            return;
        }
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            lock (timerGate)
            {
                timers.Remove(timer!);
            }
            timer!.Dispose();
            Post(action);
        });
        lock (timerGate)
        {
            timers.Add(timer);
        }
        timer.Change(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Runs a function on the queue and waits for it. Returns <paramref name="fallback"/>
    /// when it did not finish within <paramref name="timeout"/>; it still runs later.
    /// Exceptions are rethrown on the caller.
    /// </summary>
    public T Invoke<T>(Func<T> function, TimeSpan timeout, T fallback)
    {
        if (IsCurrentThread)
        {
            return function();
        }
        var result = fallback;
        ExceptionDispatchInfo? failure = null;
        var done = new ManualResetEventSlim();
        Post(() =>
        {
            try
            {
                result = function();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                done.Set();
            }
        });
        if (!done.Wait(timeout))
        {
            return fallback;
        }
        done.Dispose();
        failure?.Throw();
        return result;
    }

    /// <summary>Runs an action on the queue and waits for it; false when it did not finish in time.</summary>
    public bool Invoke(Action action, TimeSpan timeout) => Invoke(() =>
    {
        action();
        return true;
    }, timeout, false);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        lock (timerGate)
        {
            foreach (var timer in timers)
            {
                timer.Dispose();
            }
            timers.Clear();
        }
        actions.CompleteAdding();
        if (!IsCurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    void Run()
    {
        foreach (var action in actions.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                log.Error("Queued work failed", exception);
            }
        }
    }
}
