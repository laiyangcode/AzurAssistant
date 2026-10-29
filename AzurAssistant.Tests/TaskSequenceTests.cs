using AzurAssistant.Contracts;
using AzurAssistant.Runtime;

namespace AzurAssistant.Tests;

/// <summary>Covers the ordering, skip-on-failure and exit-intent rules of the daily sequence.</summary>
public class TaskSequenceTests
{
    private readonly List<string> _logs = [];

    private TaskContext Context(IGameInput input, ITaskFailureReporter? failures = null, TaskExecution? execution = null) =>
        new(new EmptyFrameFeed(TestFrames.Uniform(8, 8)), input, message => _logs.Add(message), failures, execution);

    [Fact]
    public async Task AllSuccess_RunsEveryTaskOnceInOrderAndReportsSuccess()
    {
        var first = FakeTask.Succeeds("领取邮件");
        var second = FakeTask.Succeeds("家园中枢");
        var third = FakeTask.Succeeds("日常委托");
        var input = new RecordingGameInput();
        var sequence = new TaskSequence([first, second, third]);

        var result = await sequence.ExecuteAsync(Context(input), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("日常一条龙完成", result.Message);
        Assert.Equal(1, first.StartCount);
        Assert.Equal(1, second.StartCount);
        Assert.Equal(1, third.StartCount);
        Assert.Equal(3, input.Calls.Count(call => call == "activate"));
        Assert.True(input.ReleaseAllCount >= 3);
    }

    [Fact]
    public async Task FailureWithRecovery_SkipsTheFailedItemAndContinuesWithoutRepeatingIt()
    {
        var mail = FakeTask.Succeeds("领取邮件");
        var home = FakeTask.Fails("家园中枢", "饱腹值读取失败");
        var activity = FakeTask.Succeeds("日常周常领取");
        var input = new RecordingGameInput();
        var recoveries = 0;
        var sequence = new TaskSequence([mail, home, activity],
            recoverFailure: (context, token) => { recoveries++; return Task.FromResult(new TaskResult(true, "已返回大世界")); });

        var result = await sequence.ExecuteAsync(Context(input), token: CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, recoveries);
        Assert.Equal(1, home.StartCount);
        Assert.Equal(1, activity.StartCount);
        Assert.Contains("未完成并跳过：家园中枢", result.Message);
        Assert.Contains(_logs, line => line.Contains("跳过此项并继续后续任务"));
    }

    [Fact]
    public async Task FailureWithoutRecovery_StopsTheSequenceImmediately()
    {
        var mail = FakeTask.Fails("领取邮件", "邮件列表未出现");
        var home = FakeTask.Succeeds("家园中枢");
        var sequence = new TaskSequence([mail, home]);

        var result = await sequence.ExecuteAsync(Context(new RecordingGameInput()), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("一条龙停止于领取邮件", result.Message);
        Assert.Equal(0, home.StartCount);
    }

    [Fact]
    public async Task FailedRecovery_StopsAndReportsBothReasons()
    {
        var home = FakeTask.Fails("家园中枢", "产出页未确认");
        var after = FakeTask.Succeeds("日常周常领取");
        var sequence = new TaskSequence([home, after],
            recoverFailure: (_, _) => Task.FromResult(new TaskResult(false, "恢复页面失败")));

        var result = await sequence.ExecuteAsync(Context(new RecordingGameInput()), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("产出页未确认", result.Message);
        Assert.Contains("恢复页面失败", result.Message);
        Assert.Equal(0, after.StartCount);
    }

    [Fact]
    public async Task BeforeTaskRejection_PreventsTheTaskBodyFromRunning()
    {
        var mail = FakeTask.Succeeds("领取邮件");
        var sequence = new TaskSequence([mail],
            beforeTask: (task, _, _) => Task.FromResult(new TaskResult(false, $"{task.Name}起点检查不通过")));

        var result = await sequence.ExecuteAsync(Context(new RecordingGameInput()), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, mail.StartCount);
        Assert.Contains("的起点检查", result.Message);
    }

    [Fact]
    public async Task EveryItemIncomplete_IsSummarisedInOneMessage()
    {
        var a = FakeTask.Fails("领取邮件");
        var b = FakeTask.Fails("通行证领取");
        var sequence = new TaskSequence([a, b], recoverFailure: (_, _) => Task.FromResult(new TaskResult(true, "已恢复")));

        var result = await sequence.ExecuteAsync(Context(new RecordingGameInput()), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("领取邮件、通行证领取", result.Message);
    }

    [Fact]
    public async Task AfterTaskExitIntent_IsPropagatedOnlyAfterTheWholeSequenceSucceeded()
    {
        var mail = FakeTask.Succeeds("领取邮件");
        var sequence = new TaskSequence([mail],
            afterTasks: (_, _) => Task.FromResult(new TaskResult(true, "游戏已关闭", RequestApplicationExit: true)),
            afterTaskName: "关闭游戏");

        var result = await sequence.ExecuteAsync(Context(new RecordingGameInput()), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.RequestApplicationExit);
    }

    [Fact]
    public async Task AfterTaskFailure_ReportsTailFailureAndDropsExitIntent()
    {
        var sequence = new TaskSequence([FakeTask.Succeeds("领取邮件")],
            afterTasks: (_, _) => Task.FromResult(new TaskResult(false, "关闭进程超时")),
            afterTaskName: "关闭游戏");

        var result = await sequence.ExecuteAsync(Context(new RecordingGameInput()), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.RequestApplicationExit);
        Assert.Contains("一条龙收尾失败：关闭进程超时", result.Message);
    }

    [Fact]
    public async Task TaskException_PropagatesAfterRecordingFailureEvidence()
    {
        var reporter = new RecordingFailureReporter();
        var sequence = new TaskSequence([FakeTask.Throws("家园中枢", "画面尺寸异常")]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sequence.ExecuteAsync(Context(new RecordingGameInput(), reporter), CancellationToken.None));

        Assert.Equal("画面尺寸异常", error.Message);
        Assert.Contains(reporter.Reports, report => report.Contains("画面尺寸异常"));
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutFailureEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        var stopping = new FakeTask("领取邮件", "领取邮件", token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<TaskResult>(token);
        });
        var reporter = new RecordingFailureReporter();
        var sequence = new TaskSequence([stopping]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sequence.ExecuteAsync(Context(new RecordingGameInput(), reporter), cancellation.Token));

        Assert.Empty(reporter.Reports);
    }

    [Fact]
    public async Task ChildContexts_AreNamedAndTaggedForTheFailureReporter()
    {
        var mail = FakeTask.Succeeds("领取邮件");
        var home = FakeTask.Succeeds("家园中枢");
        var reporter = new RecordingFailureReporter();
        var sequence = new TaskSequence([mail, home]);

        await sequence.ExecuteAsync(Context(new RecordingGameInput(), reporter), CancellationToken.None);

        Assert.Equal(new[] { ("领取邮件", "领取邮件"), ("家园中枢", "家园中枢") }, reporter.Tasks);
        Assert.Contains(_logs, line => line.Contains("[领取邮件] 开始执行"));
        Assert.Contains(_logs, line => line.Contains("[家园中枢] 开始执行"));
    }

    [Fact]
    public void ForTask_NestsNamesAndKeepsOneLogSink()
    {
        var sequence = new TaskSequence([FakeTask.Succeeds("领取邮件")]);
        var root = Context(new RecordingGameInput());

        var child = root.ForTask("日常委托").ForTask("领取奖励");

        Assert.Equal("日常委托-领取奖励", child.TaskPath);
        child.Log("开始");
        Assert.Contains(_logs, line => line == "[日常委托-领取奖励] 开始");
        Assert.Same(root.Execution, child.Execution);
    }

    [Fact]
    public async Task PreReportedFailure_IsNotReportedTwice()
    {
        var reporter = new RecordingFailureReporter();
        var failing = new FakeTask("mail", "领取邮件",
            _ => Task.FromResult(new TaskResult(false, "已自行留证", FailureReported: true)));
        var sequence = new TaskSequence([failing]);

        var result = await sequence.ExecuteAsync(Context(new RecordingGameInput(), reporter), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(reporter.Reports);
    }

    [Fact]
    public async Task PauseBarrier_BlocksTheNextItemUntilResume()
    {
        using var execution = new TaskPauseController();
        var input = new RecordingGameInput();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mail = new FakeTask("领取邮件", "领取邮件", async token =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return new TaskResult(true, "完成");
        });
        var home = FakeTask.Succeeds("家园中枢");
        var sequence = new TaskSequence([mail, home]);
        var run = Task.Run(() => sequence.ExecuteAsync(Context(input, execution: execution), CancellationToken.None));

        await started.Task;
        Assert.Equal(PauseToggleResult.Paused,
            await execution.ToggleAsync(input.ReleaseAll, _ => Task.CompletedTask, CancellationToken.None));
        release.SetResult();

        await TaskWait.UntilAsync(() => mail.StartCount == 1, "第一项已执行完主体");
        Assert.Equal(0, home.StartCount);
        Assert.True(TaskWait.IsNotCompletedBefore(run, 300), "暂停期间第二项不应开始");

        Assert.Equal(PauseToggleResult.Resumed,
            await execution.ToggleAsync(input.ReleaseAll, _ => Task.CompletedTask, CancellationToken.None));
        Assert.True(await Task.WhenAny(run, Task.Delay(4000)) == run, "恢复后一条龙应继续执行完毕");
        Assert.Equal(1, home.StartCount);
        Assert.True((await run).Success);
    }
}
