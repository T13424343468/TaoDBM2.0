namespace TaoDBM.Core;

/// <summary>只读副本节点画像：复制延迟与健康态（延迟感知读路由）。</summary>
public sealed record ReplicaNode(string Name, long LagSeconds, bool Healthy, int Weight = 1);

/// <summary>一次读路由决策：命中副本列表 + 是否回落主库。</summary>
public sealed record ReadRouteDecision(IReadOnlyList<string> EligibleReplicas, bool FellBackToPrimary)
{
    /// <summary>路由为空表示无可读节点（主库也标记不可用时由调用方报错）。</summary>
    public bool HasTarget => EligibleReplicas.Count > 0 || FellBackToPrimary;
}

/// <summary>
/// 主从底座引擎：延迟感知的读路由（第十四章读写分离的调度纯逻辑）。
/// 只从健康且延迟达标的副本里挑读节点，全部不达标自动回落主库并打标；
/// 节点健康探测由执行层提供，本引擎只做决策。
/// </summary>
public static class ReadRouter
{
    /// <summary>
    /// 路由一次读请求：健康且 lag ≤ maxLagSeconds 的副本按延迟升序（并列按名字）返回；
    /// 无合格副本时回落主库（FellBackToPrimary=true）。
    /// </summary>
    public static ReadRouteDecision Route(IEnumerable<ReplicaNode> nodes, long maxLagSeconds)
    {
        var eligible = nodes
            .Where(n => n.Healthy && n.LagSeconds <= maxLagSeconds)
            .OrderBy(n => n.LagSeconds)
            .ThenBy(n => n.Name, StringComparer.Ordinal)
            .Select(n => n.Name)
            .ToList();
        return new ReadRouteDecision(eligible, FellBackToPrimary: eligible.Count == 0);
    }

    /// <summary>
    /// 自动摘除/加回：返回每个副本期望的健康态——延迟超阈值或探活失败摘读，恢复后自动加回。
    /// 供执行层对照调整流量开关（本引擎不改状态）。
    /// </summary>
    public static IReadOnlyList<(string Name, bool ShouldServeReads)> AutoToggle(
        IEnumerable<ReplicaNode> nodes, long maxLagSeconds) =>
        nodes.Select(n => (n.Name, ShouldServeReads: n.Healthy && n.LagSeconds <= maxLagSeconds)).ToList();
}
