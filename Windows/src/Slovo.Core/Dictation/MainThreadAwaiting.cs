using System.Runtime.CompilerServices;
using Slovo.Core.Platform;

namespace Slovo.Core.Dictation;

/// <summary>
/// <c>await task.ResumeOn(mainThread)</c>: waits for a task wherever it completes and then
/// continues on the main thread, also when the task failed (its exception is rethrown
/// there). The counterpart of returning to the main actor after an <c>await</c> in Swift,
/// independent of whichever SynchronizationContext the caller had.
/// </summary>
internal static class MainThreadAwaiting
{
    public static MainThreadTaskAwaitable ResumeOn(this Task task, IMainThread mainThread) => new(task, mainThread);

    public static MainThreadTaskAwaitable<T> ResumeOn<T>(this Task<T> task, IMainThread mainThread) => new(task, mainThread);

    internal static Action Resume(IMainThread mainThread, Action continuation) => () =>
    {
        if (mainThread.IsMainThread)
        {
            continuation();
        }
        else
        {
            mainThread.Post(continuation);
        }
    };
}

internal readonly struct MainThreadTaskAwaitable(Task task, IMainThread mainThread)
{
    public Awaiter GetAwaiter() => new(task, mainThread);

    public readonly struct Awaiter(Task task, IMainThread mainThread) : ICriticalNotifyCompletion
    {
        public bool IsCompleted => task.IsCompleted && mainThread.IsMainThread;

        public void GetResult() => task.GetAwaiter().GetResult();

        public void OnCompleted(Action continuation) =>
            task.ConfigureAwait(false).GetAwaiter().OnCompleted(MainThreadAwaiting.Resume(mainThread, continuation));

        public void UnsafeOnCompleted(Action continuation) =>
            task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(MainThreadAwaiting.Resume(mainThread, continuation));
    }
}

internal readonly struct MainThreadTaskAwaitable<T>(Task<T> task, IMainThread mainThread)
{
    public Awaiter GetAwaiter() => new(task, mainThread);

    public readonly struct Awaiter(Task<T> task, IMainThread mainThread) : ICriticalNotifyCompletion
    {
        public bool IsCompleted => task.IsCompleted && mainThread.IsMainThread;

        public T GetResult() => task.GetAwaiter().GetResult();

        public void OnCompleted(Action continuation) =>
            task.ConfigureAwait(false).GetAwaiter().OnCompleted(MainThreadAwaiting.Resume(mainThread, continuation));

        public void UnsafeOnCompleted(Action continuation) =>
            task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(MainThreadAwaiting.Resume(mainThread, continuation));
    }
}
