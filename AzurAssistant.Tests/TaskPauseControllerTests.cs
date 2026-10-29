using System.Diagnostics;
using AzurAssistant.Contracts;
using AzurAssistant.Runtime;

namespace AzurAssistant.Tests;

/// <summary>Covers the pause clock, observation invalidation and action gates of one run session.</summary>
public class TaskPauseControllerTests
{
    private static FrameSnapshot Frame(TimeSpan timestamp, long id = 1) =>
        TestFrames.Uniform(8, 8, timestamp: timestamp) with { Id = id };

    private static async Task AwaitWithin(Task task, int milliseconds = 3000)
    {
        var finished = await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(false);
        if (finished != task) throw new TimeoutException("等待被测操作完成超时");
        await task.ConfigureAwait(false);
    }

    private static async Task<T> AwaitWithin<T>(Task<T> task, int milliseconds = 3000)
    {
        var finished = await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(false);
        if (finished != task) throw new TimeoutException("等待被测操作完成超时");
        return await task.ConfigureAwait(false);
    }

    [Fact]
    public async Task FreshController_IsActiveWithNoObservationBoundary()
    {
        using var execution = new TaskPauseController();

        Assert.False(execution.IsPaused);
        Assert.Equal(0, execution.Generation);
        Assert.True(execution.IsObservationCurrent(Frame(TimeSpan.FromTicks(1))));
        await AwaitWithin(execution.WaitAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Pause_ReleasesInputAndBlocksTheCooperativeWait()
    {
        using var execution = new TaskPauseController();
        var releases = 0;

        var result = await AwaitWithin(execution.ToggleAsync(() => releases++, _ => Task.CompletedTask, CancellationToken.None));

        Assert.Equal(PauseToggleResult.Paused, result);
        Assert.True(execution.IsPaused);
        Assert.Equal(1, releases);
        using var barrier = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(barrier.Token));
    }

    [Fact]
    public async Task Resume_ActivatesWindowBeforeUnblockingWaiters()
    {
        using var execution = new TaskPauseController();
        var order = new List<string>();
        await execution.ToggleAsync(() => order.Add("release"), _ => Task.CompletedTask, CancellationToken.None);
        var waiter = execution.WaitAsync(CancellationToken.None);
        Assert.True(TaskWait.IsNotCompletedBefore(waiter, 60), "暂停期间协作者应停在关口");

        await AwaitWithin(execution.ToggleAsync(() => order.Add("ignored"), _ =>
        {
            order.Add("activate");
            Assert.False(waiter.IsCompleted);
            Assert.True(execution.IsPaused);
            return Task.CompletedTask;
        }, CancellationToken.None));

        Assert.Equal(["release", "activate"], order);
        Assert.False(execution.IsPaused);
        await AwaitWithin(waiter);
    }

    [Fact]
    public async Task Resume_AdvancesGenerationAndInvalidatesEarlierObservations()
    {
        using var execution = new TaskPauseController();
        var beforePause = Frame(TestFrames.CaptureClock());
        Assert.True(execution.IsObservationCurrent(beforePause));

        await execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None);
        await Task.Delay(30);
        await execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None);
        await Task.Delay(30);

        Assert.Equal(1, execution.Generation);
        Assert.False(execution.IsObservationCurrent(beforePause));
        Assert.True(execution.IsObservationCurrent(Frame(TestFrames.CaptureClock())));
    }

    [Fact]
    public async Task SecondToggle_PausesAndResumesAgainWithMonotonicGeneration()
    {
        using var execution = new TaskPauseController();
        for (var round = 1; round <= 2; round++)
        {
            await execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None);
            Assert.True(execution.IsPaused);
            await execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None);
            Assert.False(execution.IsPaused);
            Assert.Equal(round, execution.Generation);
        }
    }

    [Fact]
    public async Task Elapsed_FrozeWhilePausedAndCountsActiveTimeOnly()
    {
        using var execution = new TaskPauseController();
        await Task.Delay(60);
        await execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None);
        var frozen = execution.Elapsed;
        await Task.Delay(400);
        Assert.Equal(frozen, execution.Elapsed);

        await execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None);
        await Task.Delay(120);
        var resumed = execution.Elapsed;
        Assert.True(resumed > frozen, $"活动时钟应继续前进：frozen={frozen}, resumed={resumed}");
        Assert.True((resumed - frozen) < TimeSpan.FromSeconds(2),
            $"暂停的 400ms 不应计入活动时长：frozen={frozen}, resumed={resumed}");
    }

    [Fact]
    public async Task Delay_IsFrozenWhilePausedAndCompletesAfterResume()
    {
        using var execution = new TaskPauseController();
        var delay = execution.DelayAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);
        await Task.Delay(60);
        await execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(TaskWait.IsNotCompletedBefore(delay, 700), "暂停期间等待时长不应消耗");

        await execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None);
        await AwaitWithin(delay, 2000);
        Assert.True(delay.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Delay_RejectsInvalidDurations()
    {
        using var execution = new TaskPauseController();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => execution.DelayAsync(TimeSpan.FromMilliseconds(-5), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            execution.DelayAsync(TimeSpan.FromMilliseconds(uint.MaxValue), CancellationToken.None));
    }

    [Fact]
    public async Task Delay_TreatsTheInfiniteTimeSpanAsWaitingUntilCancelled()
    {
        using var execution = new TaskPauseController();
        using var cancellation = new CancellationTokenSource();

        var waiting = execution.DelayAsync(Timeout.InfiniteTimeSpan, cancellation.Token);
        await Task.Delay(150);
        Assert.False(waiting.IsCompleted, "无限等待不应自行结束");

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task Actions_AreSerialisedAndReleasedOnDispose()
    {
        using var execution = new TaskPauseController();
        var first = await AwaitWithin(execution.EnterActionAsync(CancellationToken.None));
        var second = execution.EnterActionAsync(CancellationToken.None);

        Assert.True(TaskWait.IsNotCompletedBefore(second, 150), "同一时刻只允许一个动作");

        first.Dispose();
        var acquired = await AwaitWithin(second);
        acquired.Dispose();
        var third = await AwaitWithin(execution.EnterActionAsync(CancellationToken.None));
        third.Dispose();
    }

    [Fact]
    public async Task Actions_AreBlockedWhilePausedAndResumedAfterResume()
    {
        using var execution = new TaskPauseController();
        await execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None);
        var pending = execution.EnterActionAsync(CancellationToken.None);
        Assert.True(TaskWait.IsNotCompletedBefore(pending, 150), "暂停期间不得发送新动作");

        await execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None);
        var lease = await AwaitWithin(pending);
        lease.Dispose();
    }

    [Fact]
    public async Task Finishing_RejectsNewActionsAndFurtherToggles()
    {
        using var execution = new TaskPauseController();
        await AwaitWithin(execution.CompleteAsync(CancellationToken.None));

        using var barrier = new CancellationTokenSource(300);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.EnterActionAsync(barrier.Token));
        Assert.Contains("收尾", error.Message);
        Assert.Equal(PauseToggleResult.None, await AwaitWithin(execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None)));
    }

    [Fact]
    public async Task Complete_WaitsForThePauseToClearBeforeEnteringFinishing()
    {
        using var execution = new TaskPauseController();
        await execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None);
        var completing = execution.CompleteAsync(CancellationToken.None);
        Assert.True(TaskWait.IsNotCompletedBefore(completing, 150), "暂停期间不应进入收尾");

        await execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None);
        await AwaitWithin(completing);
    }

    [Fact]
    public async Task DrainActions_WaitsForAnInFlightAction()
    {
        using var execution = new TaskPauseController();
        var lease = await AwaitWithin(execution.EnterActionAsync(CancellationToken.None));
        var draining = execution.DrainActionsAsync();

        Assert.True(TaskWait.IsNotCompletedBefore(draining, 150), "在途动作未结束时不应排空");
        lease.Dispose();
        await AwaitWithin(draining);
    }

    [Fact]
    public async Task End_ObservationsBecomeStaleAndWaitersExitWithError()
    {
        using var execution = new TaskPauseController();
        await execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None);
        var waiter = execution.WaitAsync(CancellationToken.None);
        execution.End();

        Assert.False(execution.IsPaused);
        Assert.False(execution.IsObservationCurrent(Frame(TimeSpan.FromSeconds(5))));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.Contains("已结束", error.Message);
        Assert.Equal(PauseToggleResult.None, await AwaitWithin(execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None)));
    }

    [Fact]
    public async Task Dispose_EndsTheSessionIdempotently()
    {
        var execution = new TaskPauseController();
        execution.Dispose();
        execution.End();

        using var barrier = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(barrier.Token));
    }

    [Fact]
    public async Task UncontrolledExecution_IsAlwaysCurrentAndNeverPauses()
    {
        var execution = TaskExecution.Uncontrolled;

        await execution.WaitAsync(CancellationToken.None);
        Assert.Equal(0, execution.Generation);
        Assert.True(execution.IsObservationCurrent(Frame(TimeSpan.Zero)));
        var lease = await execution.EnterActionAsync(CancellationToken.None);
        lease.Dispose();
        var second = await execution.EnterActionAsync(CancellationToken.None);
        second.Dispose();
        Assert.True(execution.Elapsed >= TimeSpan.Zero);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(cancelled.Token));
        Assert.True(execution.IsObservationCurrent(Frame(TimeSpan.Zero)));
    }

    [Fact]
    public async Task Deadline_ExpiresOnTheActiveClockAndIgnoresPausedTime()
    {
        using var execution = new TaskPauseController();
        using var deadline = execution.CreateDeadline(CancellationToken.None);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(400));
        await execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None);

        var sw = Stopwatch.StartNew();
        await Task.Delay(900);
        Assert.False(deadline.Token.IsCancellationRequested);

        await execution.ToggleAsync(() => { }, _ => Task.CompletedTask, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Delay(Timeout.Infinite, deadline.Token).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.True(sw.ElapsedMilliseconds >= 400, $"恢复后剩余活动时长才计时：{sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Deadline_RejectsInvalidDurationsAndAcceptsInfinite()
    {
        using var execution = new TaskPauseController();
        using var deadline = execution.CreateDeadline(CancellationToken.None);
        Assert.Throws<ArgumentOutOfRangeException>(() => deadline.CancelAfter(TimeSpan.FromMilliseconds(-5)));
        deadline.CancelAfter(Timeout.InfiniteTimeSpan);
        Assert.False(deadline.Token.IsCancellationRequested);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(20));
        Assert.True(deadline.Token.WaitHandle.WaitOne(3000));
    }
}
