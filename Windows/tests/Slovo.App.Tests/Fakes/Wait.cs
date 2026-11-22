using Avalonia.Threading;

namespace Slovo.App.Tests.Fakes;

static class Wait
{
    /// <summary>Runs the UI thread's jobs and timers until the condition holds or the time runs out.</summary>
    public static bool Until(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        return true;
    }
}
