namespace TaoDBM.Core;

/// <summary>一条等待边：waiter 在 resource 上等 holder。</summary>
public sealed record LockWait(string Waiter, string Holder, string Resource);

/// <summary>会话画像：现场还原与牺牲者选择用。</summary>
public sealed record SessionInfo(string SessionId, string User, string Statement, long RowsExamined);

/// <summary>检出的死锁环（会话按环上顺序排列）。</summary>
public sealed record DeadlockCycle(IReadOnlyList<string> SessionIds, IReadOnlyList<string> Resources);

/// <summary>
/// 安全线引擎：锁与死锁现场还原 + 终止会话的纯逻辑部分（第十章）。
/// 输入等待快照（等待边 + 会话画像），检出死锁环、推荐牺牲者并生成跨方言 KILL 语句；
/// 快照采集（performance_schema/pg_locks 等查询）在 Infrastructure 执行层完成。
/// </summary>
public static class LockAnalyzer
{
    /// <summary>检出等待图中的所有死锁环（有向图 waiter→holder；确定序遍历，结果稳定）。</summary>
    public static IReadOnlyList<DeadlockCycle> FindDeadlocks(IEnumerable<LockWait> waits)
    {
        var graph = waits
            .GroupBy(w => w.Waiter, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var cycles = new List<DeadlockCycle>();
        var done = new HashSet<string>(StringComparer.Ordinal);

        foreach (var start in graph.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (done.Contains(start)) continue;
            // 从 start 出发沿「等待对象是谁」走一步的链：waiter → holder（若 holder 也在等别人则继续）
            var path = new List<string>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var current = start;
            while (graph.ContainsKey(current) && !index.TryGetValue(current, out _))
            {
                index[current] = path.Count;
                path.Add(current);
                current = graph[current].First().Holder;   // 简化现场：同一 waiter 多资源时取第一等待边
            }
            if (index.TryGetValue(current, out var cycleStart))
            {
                var ids = path[cycleStart..];
                var resources = ids
                    .Select(id => graph[id].First().Resource)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                cycles.Add(new DeadlockCycle(ids, resources));
                foreach (var id in ids) done.Add(id);
            }
            else
            {
                foreach (var id in path) done.Add(id);
            }
        }
        return cycles;
    }

    /// <summary>牺牲者推荐：环上回滚代价最小者（扫描行数最少；并列取会话号小者）。</summary>
    public static string? PickVictim(DeadlockCycle cycle, IReadOnlyDictionary<string, SessionInfo> sessions)
    {
        return cycle.SessionIds
            .Select(id => (Id: id, Info: sessions.TryGetValue(id, out var s) ? s : new SessionInfo(id, "", "", long.MaxValue)))
            .OrderBy(x => x.Info.RowsExamined)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => x.Id)
            .FirstOrDefault();
    }

    /// <summary>跨方言终止会话语句；该引擎无会话终止能力时返回 null。</summary>
    public static string? KillSql(string sessionId, DbEngine engine) => engine switch
    {
        DbEngine.MySQL => $"KILL {sessionId}",
        DbEngine.SqlServer => $"KILL {sessionId}",
        DbEngine.PostgreSQL => $"SELECT pg_terminate_backend({sessionId});",
        _ => null,
    };

    /// <summary>现场还原：等待边按等待时长倒序（等得最久的排最前，供锁页时间线展示）。</summary>
    public static IReadOnlyList<LockWait> Timeline(IEnumerable<LockWait> waits, IReadOnlyDictionary<string, long> waitSeconds) =>
        waits.OrderByDescending(w => waitSeconds.GetValueOrDefault(w.Waiter)).ToList();
}
