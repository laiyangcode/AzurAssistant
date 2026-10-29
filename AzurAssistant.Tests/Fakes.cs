using AzurAssistant.Contracts;

namespace AzurAssistant.Tests;

/// <summary>Records every interaction the runtime has with the input backend.</summary>
internal sealed class RecordingGameInput : IGameInput
{
    private readonly List<string> _calls = [];
    public IReadOnlyList<string> Calls => _calls;
    public int ReleaseAllCount { get; private set; }
    public Func<string, Task>? OnCall { get; init; }

    private async Task Record(string name)
    {
        lock (_calls) _calls.Add(name);
        if (OnCall is { } hook) await hook(name);
    }

    public Task ActivateAsync(CancellationToken token) => Record("activate");
    public Task ClickAsync(FrameSnapshot observation, ClientPoint point, CancellationToken token) => Record($"click:{point.X},{point.Y}");
    public void ReleaseAll() => ReleaseAllCount++;
}

internal sealed class EmptyFrameFeed(FrameSnapshot frame) : IFrameFeed
{
    public Task<FrameSnapshot> ReadAfterAsync(TimeSpan timestamp, CancellationToken token) => Task.FromResult(frame);
}

internal sealed class RecordingFailureReporter : ITaskFailureReporter
{
    private readonly List<string> _reports = [];
    public IReadOnlyList<string> Reports => _reports;
    public List<(string Id, string Name)> Tasks { get; } = [];
    public void SetTask(string id, string name) => Tasks.Add((id, name));
    public Task ReportAsync(string reason, Exception? error = null)
    {
        lock (_reports) _reports.Add(reason);
        return Task.CompletedTask;
    }
}

/// <summary>A scripted child task: result, delay, thrown error, or cancellation are all configurable.</summary>
internal sealed class FakeTask(string id, string name, Func<CancellationToken, Task<TaskResult>> body) : IGameTask
{
    public int StartCount { get; private set; }
    public string Id { get; } = id;
    public string Name { get; } = name;

    public Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        StartCount++;
        return body(token);
    }

    public static FakeTask Succeeds(string name, string message = "完成") =>
        new(name, name, _ => Task.FromResult(new TaskResult(true, message)));

    public static FakeTask Fails(string name, string message = "识别超时") =>
        new(name, name, _ => Task.FromResult(new TaskResult(false, message)));

    public static FakeTask Throws(string name, string message = "崩溃") =>
        new(name, name, _ => Task.FromException<TaskResult>(new InvalidOperationException(message)));
}

internal static class TaskWait
{
    /// <summary>Waits until the predicate holds, so pause/resume assertions do not depend on wall-clock luck.</summary>
    public static async Task UntilAsync(Func<bool> condition, string because, int timeoutMilliseconds = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"等待条件超时：{because}");
            await Task.Delay(10);
        }
    }

    public static async Task<TimeSpan> CompletedWithinAsync(Task task, int milliseconds)
    {
        var started = Environment.TickCount64;
        var finished = await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(false);
        if (finished != task) throw new TimeoutException($"在 {milliseconds}ms 内完成，实际仍在等待");
        await task.ConfigureAwait(false);
        return TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
    }

    public static bool IsNotCompletedBefore(Task task, int milliseconds)
    {
        var finished = Task.WhenAny(task, Task.Delay(milliseconds)).GetAwaiter().GetResult();
        return finished != task;
    }
}
