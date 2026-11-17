using System.Threading.Channels;

namespace TaoDBM.Core.Streaming;

/// <summary>任务规格：提交长活时的描述。</summary>
public sealed class TaskSpec
{
    public string Kind { get; init; } = "query";   // query / export / sync / import
    public string Title { get; init; } = "";
    public string Db { get; init; } = "";
    public string Sql { get; init; } = "";
}

/// <summary>任务句柄：取消与查询快照的唯一凭据。</summary>
public sealed class TaskHandle
{
    public string TaskId { get; init; } = "";
    public TaskSpec Spec { get; init; } = new();
    internal CancellationTokenSource Cts { get; } = new();
    public CancellationToken Token => Cts.Token;
}

/// <summary>任务快照：任务中心列表可见的一行。</summary>
public sealed record TaskSnapshot
{
    public string TaskId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public TaskSpec Spec { get; init; } = new();   // A2 重跑：快照携带原规格
    public string State { get; init; } = "";       // running / done / failed / canceled
    public string Message { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
}

/// <summary>
/// 统一任务中心：所有长活（大查询/导出/同步/导入）都进队列串行执行，
/// 任务状态进内存快照表（方案书 2.1）。A2 实体化：暂停/续跑、重跑、快照持久化。
/// </summary>
public sealed class TaskCenter
{
    private readonly Channel<TaskHandle> _queue;
    private readonly Dictionary<string, TaskSnapshot> _states = new();
    private readonly object _lock = new();
    private volatile bool _paused;
    private volatile TaskCompletionSource _gate = NewGate();

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCenter()
    {
        _queue = Channel.CreateUnbounded<TaskHandle>();
        _ = Task.Run(LoopAsync); // 常驻消费循环：进程生命周期内存续，无页面级退订
    }

    public TaskHandle Submit(TaskSpec spec)
    {
        var h = new TaskHandle { TaskId = Guid.NewGuid().ToString("N")[..12], Spec = spec };
        lock (_lock) _states[h.TaskId] = new TaskSnapshot
        {
            TaskId = h.TaskId, Kind = spec.Kind, Title = spec.Title, Spec = spec,
            State = "running", StartedAt = DateTimeOffset.Now,
        };
        _queue.Writer.TryWrite(h);
        return h;
    }

    /// <summary>暂停：不再从队列取新任务（已运行中的任务不受影响）；返回 false 表示此前已暂停。</summary>
    public bool Pause()
    {
        if (_paused) return false;
        _paused = true;
        return true;
    }

    /// <summary>续跑：恢复队列消费，暂停期间积压的任务按序开跑。</summary>
    public void Resume()
    {
        _paused = false;
        _gate.TrySetResult();
        _gate = NewGate();
    }

    /// <summary>重跑：终态任务（done/failed/canceled）按原规格开新任务；running 任务不可重跑返回 null。</summary>
    public TaskHandle? Retry(string taskId)
    {
        TaskSnapshot? s;
        lock (_lock) _states.TryGetValue(taskId, out s);
        if (s is null || s.State == "running") return null;
        return Submit(s.Spec);
    }

    /// <summary>快照导出：全量任务状态序列化（调用方拿去走 AtomicWrite 落盘，重启后回放历史）。</summary>
    public string ExportStates()
    {
        lock (_lock) return System.Text.Json.JsonSerializer.Serialize(_states.Values);
    }

    /// <summary>快照回放：把历史快照并入列表（仅补缺，不覆盖本地已有状态，运行态不受影响）；损坏输入静默忽略。</summary>
    public void ImportStates(string json)
    {
        try
        {
            var items = System.Text.Json.JsonSerializer.Deserialize<List<TaskSnapshot>>(json);
            if (items is null) return;
            lock (_lock)
                foreach (var it in items)
                    if (!_states.ContainsKey(it.TaskId)) _states[it.TaskId] = it;
        }
        catch (System.Text.Json.JsonException) { /* 损坏快照：忽略，任务中心照常工作 */ }
    }

    public IReadOnlyList<TaskSnapshot> List()
    {
        lock (_lock) return _states.Values.OrderByDescending(s => s.StartedAt).ToList();
    }

    public bool Cancel(string taskId)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(taskId, out var s) || s.State != "running") return false;
            _states[taskId] = s with { State = "canceled", EndedAt = DateTimeOffset.Now };
        }
        return true;
    }

    internal void Complete(string taskId, string state, string message)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(taskId, out var s)) return;
            _states[taskId] = s with { State = state, Message = message, EndedAt = DateTimeOffset.Now };
        }
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            var h = await _queue.Reader.ReadAsync();
            // A2 暂停闸：先出队再验闸，避免「循环已阻塞在 ReadAsync、暂停后入队任务被立即执行」的竞态；
            // 暂停期间出队的任务挂起在闸前，续跑后按序开跑
            if (_paused) await _gate.Task;
            var snapshot = List().FirstOrDefault(s => s.TaskId == h.TaskId);
            // Cancel 已把任务标记 canceled 的：直接跳过执行
            if (snapshot is null || snapshot.State != "running") { h.Cts.Dispose(); continue; }
            try
            {
                await RunAsync(h);
                Complete(h.TaskId, "done", "");
            }
            catch (OperationCanceledException)
            {
                Complete(h.TaskId, "canceled", "已取消");
            }
            catch (Exception ex)
            {
                Complete(h.TaskId, "failed", "错误：" + ex.Message);
            }
            h.Cts.Dispose();
        }
    }

    /// <summary>执行体由宿主注入：把 TaskHandle 变成实际工作（查询/导出/同步/导入）。</summary>
    public Func<TaskHandle, Task>? Runner { get; set; }
    private Task RunAsync(TaskHandle h) => Runner is null ? Task.CompletedTask : Runner(h);
}
