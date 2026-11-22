namespace Slovo.Engines;

/// <summary>
/// Runs jobs one at a time, in arrival order (the port of FluidAudioJobQueue.swift).
///
/// Native recognizers and diarizers are not thread-safe, and a second decode of the same
/// model gains nothing but memory, so each engine and each diarizer queues its work here.
/// A caller cancelled while waiting leaves at once instead of sitting behind a long
/// recording.
/// </summary>
public sealed class JobQueue
{
    readonly Lock gate = new();
    readonly LinkedList<TaskCompletionSource> waiters = new();
    bool isBusy;

    /// <summary>Callers queued behind the running job; for tests.</summary>
    public int WaitingCount
    {
        get
        {
            lock (gate)
            {
                return waiters.Count;
            }
        }
    }

    /// <summary>Whether a job holds the queue.</summary>
    public bool IsBusy
    {
        get
        {
            lock (gate)
            {
                return isBusy;
            }
        }
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> job, CancellationToken cancellation = default)
    {
        await AcquireAsync(cancellation).ConfigureAwait(false);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            return await job().ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    public Task RunAsync(Func<Task> job, CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            await job().ConfigureAwait(false);
            return true;
        }, cancellation);

    /// <summary>
    /// Waits for the queue. Every successful acquire must be paired with one
    /// <see cref="Release"/>; <see cref="RunAsync{T}"/> does both.
    /// </summary>
    public Task AcquireAsync(CancellationToken cancellation = default)
    {
        LinkedListNode<TaskCompletionSource> node;
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!isBusy)
            {
                isBusy = true;
                return Task.CompletedTask;
            }
            node = waiters.AddLast(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        if (!cancellation.CanBeCanceled)
        {
            return node.Value.Task;
        }
        var registration = cancellation.Register(() =>
        {
            lock (gate)
            {
                // Already handed the queue: the caller owns it and releases it as usual.
                if (node.List is null)
                {
                    return;
                }
                waiters.Remove(node);
            }
            node.Value.TrySetCanceled(cancellation);
        });
        return WaitAndUnregister(node.Value.Task, registration);
    }

    static async Task WaitAndUnregister(Task task, CancellationTokenRegistration registration)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Hands the queue straight to the next waiter, so no new caller can slip in between.</summary>
    public void Release()
    {
        TaskCompletionSource? next = null;
        lock (gate)
        {
            if (waiters.First is { } first)
            {
                waiters.RemoveFirst();
                next = first.Value;
            }
            else
            {
                isBusy = false;
            }
        }
        next?.TrySetResult();
    }
}
