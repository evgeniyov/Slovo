namespace Slovo.Engines.Tests;

/// <summary>The FIFO queue engines and diarizers serialize their work with (DiarizationJobQueueTests.swift).</summary>
public class JobQueueTests
{
    [Fact]
    public async Task JobsRunOneAtATimeInArrivalOrder()
    {
        var queue = new JobQueue();
        var gate = new Gate();
        var order = new List<int>();
        var running = 0;
        var overlap = false;

        var first = queue.RunAsync(async () =>
        {
            await gate.WaitAsync();
            lock (order)
            {
                order.Add(0);
            }
        }, TestContext.Current.CancellationToken);
        await TestSupport.WaitUntil(() => gate.Arrived == 1);

        var later = new List<Task>();
        for (var index = 1; index <= 4; index++)
        {
            var number = index;
            later.Add(queue.RunAsync(async () =>
            {
                if (Interlocked.Increment(ref running) > 1)
                {
                    overlap = true;
                }
                await Task.Delay(2);
                lock (order)
                {
                    order.Add(number);
                }
                Interlocked.Decrement(ref running);
            }, TestContext.Current.CancellationToken));
            await TestSupport.WaitUntil(() => queue.WaitingCount == number);
        }
        gate.Open();
        await first;
        await Task.WhenAll(later);

        Assert.Equal([0, 1, 2, 3, 4], order);
        Assert.False(overlap);
        Assert.False(queue.IsBusy);
    }

    [Fact]
    public async Task ACallerCancelledWhileWaitingLeavesAtOnce()
    {
        var queue = new JobQueue();
        var gate = new Gate();
        var ran = new List<string>();

        var holder = queue.RunAsync(async () =>
        {
            await gate.WaitAsync();
            ran.Add("holder");
        }, TestContext.Current.CancellationToken);
        await TestSupport.WaitUntil(() => gate.Arrived == 1);
        using var cancellation = new CancellationTokenSource();
        var cancelled = queue.RunAsync(() =>
        {
            ran.Add("cancelled");
            return Task.CompletedTask;
        }, cancellation.Token);
        await TestSupport.WaitUntil(() => queue.WaitingCount == 1);
        var next = queue.RunAsync(() =>
        {
            ran.Add("next");
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        await TestSupport.WaitUntil(() => queue.WaitingCount == 2);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, queue.WaitingCount);
        Assert.Empty(ran);

        gate.Open();
        await holder;
        await next;
        Assert.Equal(["holder", "next"], ran);
    }

    [Fact]
    public async Task AFailingJobReleasesTheQueue()
    {
        var queue = new JobQueue();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queue.RunAsync<int>(() => throw new InvalidOperationException("boom"), TestContext.Current.CancellationToken));
        Assert.Equal(7, await queue.RunAsync(() => Task.FromResult(7), TestContext.Current.CancellationToken));
        Assert.False(queue.IsBusy);
    }

    [Fact]
    public async Task AnAlreadyCancelledCallerNeverRuns()
    {
        var queue = new JobQueue();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ran = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.RunAsync(() =>
        {
            ran = true;
            return Task.CompletedTask;
        }, cancellation.Token));
        Assert.False(ran);
        Assert.False(queue.IsBusy);
    }
}
