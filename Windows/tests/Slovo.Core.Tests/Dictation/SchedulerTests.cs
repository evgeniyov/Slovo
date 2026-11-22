using Slovo.Core.Dictation;

namespace Slovo.Core.Tests.Dictation;

/// <summary>The real timer and the way the controller returns to the main thread after an await.</summary>
public sealed class SchedulerTests
{
    readonly TestMainThread main = new();

    [Fact]
    public Task TimerRunsOnTheMainThreadAfterItsDelay() => main.RunAsync(async () =>
    {
        var scheduler = new MainThreadScheduler(main);
        var firedOnMain = new List<bool>();
        using var timer = scheduler.Schedule(0.02, () => firedOnMain.Add(main.IsMainThread));
        Assert.Empty(firedOnMain);
        Assert.True(await TestAudio.WaitUntil(() => firedOnMain.Count == 1));
        await Task.Delay(50);
        Assert.Equal([true], firedOnMain);
    });

    [Fact]
    public Task DisposedTimerNeverFires() => main.RunAsync(async () =>
    {
        var scheduler = new MainThreadScheduler(main);
        var fired = false;
        var timer = scheduler.Schedule(0.01, () => fired = true);
        timer.Dispose();
        await Task.Delay(100);
        Assert.False(fired);
    });

    [Fact]
    public Task TimerDisposedAfterItElapsedButBeforeItRanNeverFires() => main.RunAsync(async () =>
    {
        var scheduler = new MainThreadScheduler(main);
        var fired = false;
        var timer = scheduler.Schedule(0, () => fired = true);
        // Blocks the main thread past the due time, so the firing is already posted.
        Thread.Sleep(50);
        timer.Dispose();
        await Task.Delay(50);
        Assert.False(fired);
    });

    [Fact]
    public Task AwaitComesBackToTheMainThread() => main.RunAsync(async () =>
    {
        var value = await Task.Run(async () =>
        {
            await Task.Delay(10);
            return 42;
        }).ResumeOn(main);
        Assert.Equal(42, value);
        Assert.True(main.IsMainThread);

        await Task.Run(() => Thread.Sleep(10)).ResumeOn(main);
        Assert.True(main.IsMainThread);
    });

    [Fact]
    public Task FailuresAreRethrownOnTheMainThread() => main.RunAsync(async () =>
    {
        try
        {
            await Task.Run(() => throw new InvalidOperationException("engine")).ResumeOn(main);
            Assert.Fail("the failure was lost");
        }
        catch (InvalidOperationException error)
        {
            Assert.Equal("engine", error.Message);
            Assert.True(main.IsMainThread);
        }
    });

    [Fact]
    public void ManualClockFiresDueTimersInOrder()
    {
        var clock = new ManualClock();
        var fired = new List<string>();
        clock.Schedule(2, () => fired.Add("b"));
        clock.Schedule(1, () => fired.Add("a"));
        var cancelled = clock.Schedule(1.5, () => fired.Add("x"));
        cancelled.Dispose();
        clock.Schedule(1, () => clock.Schedule(0.5, () => fired.Add("a+0.5")));

        clock.Advance(1.9);
        Assert.Equal(["a", "a+0.5"], fired);
        Assert.Equal(1_001.9, clock.Now, 9);
        clock.Advance(0.2);
        Assert.Equal(["a", "a+0.5", "b"], fired);
        Assert.Equal(0, clock.PendingTimers);
    }
}
