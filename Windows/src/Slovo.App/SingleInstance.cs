namespace Slovo.App;

/// <summary>
/// One Slovo per user session: a named mutex, plus a named event the second launch sets so
/// the running instance shows its main window (the macOS reopen event).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    const string MutexName = @"Local\Slovo.App.Instance";
    const string ShowEventName = @"Local\Slovo.App.Show";

    readonly Mutex mutex;
    readonly EventWaitHandle? showEvent;
    Thread? listener;
    volatile bool disposed;

    SingleInstance(Mutex mutex, EventWaitHandle? showEvent)
    {
        this.mutex = mutex;
        this.showEvent = showEvent;
    }

    /// <summary>Null when another instance holds the mutex.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        if (!created)
        {
            mutex.Dispose();
            return null;
        }
        EventWaitHandle? showEvent = null;
        if (OperatingSystem.IsWindows())
        {
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        }
        return new SingleInstance(mutex, showEvent);
    }

    public static void SignalRunningInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            using var showEvent = EventWaitHandle.OpenExisting(ShowEventName);
            showEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    /// <summary>Calls <paramref name="onShowRequested"/> (on a background thread) whenever a second launch asks.</summary>
    public void Listen(Action onShowRequested)
    {
        if (showEvent is null || listener is not null)
        {
            return;
        }
        listener = new Thread(() =>
        {
            while (!disposed)
            {
                if (showEvent.WaitOne(TimeSpan.FromSeconds(1)) && !disposed)
                {
                    onShowRequested();
                }
            }
        })
        { IsBackground = true, Name = "Slovo single instance" };
        listener.Start();
    }

    public void Dispose()
    {
        disposed = true;
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        mutex.Dispose();
        showEvent?.Dispose();
    }
}
