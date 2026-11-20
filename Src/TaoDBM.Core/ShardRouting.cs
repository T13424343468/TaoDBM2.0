using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>分片计划（向导生成后可持久化）：表 + 分片键 + 策略 + 分片参数，供路由联动与拓扑接入复用。</summary>
public sealed record ShardPlan(
    string Table,
    string KeyColumn,
    ShardingWizard.ShardStrategy Strategy,
    int ShardCount,
    IReadOnlyList<string> Bounds,
    IReadOnlyList<ShardCategory> Categories);

/// <summary>类别分片的类别组（List 策略）。</summary>
public sealed record ShardCategory(string Name, IReadOnlyList<string> Values);

/// <summary>一次分片路由预览：样本键值的落片清单 + 读流量建议（延迟感知读路由决策）。</summary>
public sealed record ShardRoutePreview(
    IReadOnlyList<(string Key, int Shard)> Routes,
    ReadRouteDecision ReadDecision,
    string Note);

/// <summary>
/// P-6 联动一：分片落地后的查询/写入路由纯逻辑。
/// 写路径按分片键落片（Hash 取模 / Range 升序上界 / List 类别匹配）；
/// 读路径把命中的分片主库交给 ReadRouter 做延迟感知读路由（从健康副本挑读节点，无合格副本回落分片主库）。
/// 本引擎只做决策，不执行 SQL、不改连接状态；落库由执行层按决策分发。
/// </summary>
public static class ShardRouting
{
    /// <summary>写入路由：样本键值落到分片号（0 基）。数值键稳定取模，非数值键用稳定序数哈希，同键恒同片。</summary>
    public static int RouteToShard(ShardPlan plan, string keyValue)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (plan.ShardCount < 2) throw new ArgumentException("分片数至少为 2", nameof(plan));
        var raw = (keyValue ?? "").Trim();
        if (raw.Length == 0) throw new ArgumentException("分片键值不能为空", nameof(keyValue));
        return plan.Strategy switch
        {
            ShardingWizard.ShardStrategy.Hash => HashShard(raw, plan.ShardCount),
            ShardingWizard.ShardStrategy.Range => RangeShard(raw, plan),
            ShardingWizard.ShardStrategy.List => ListShard(raw, plan),
            _ => throw new NotSupportedException("未知的分片策略"),
        };
    }

    private static int HashShard(string raw, int count)
        => long.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? (int)Math.Abs(n % count)
            : (int)(StableHash(raw) % (uint)count);

    private static int RangeShard(string raw, ShardPlan plan)
    {
        if (!long.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v))
            throw new ArgumentException($"范围分片键值 {raw} 需为数值（与升序上界可比）");
        for (int i = 0; i < plan.Bounds.Count; i++)
            if (long.TryParse(plan.Bounds[i], System.Globalization.CultureInfo.InvariantCulture, out var b) && v < b)
                return i;
        return plan.ShardCount - 1; // MAXVALUE 兜底段
    }

    private static int ListShard(string raw, ShardPlan plan)
    {
        for (int i = 0; i < plan.Categories.Count; i++)
            if (plan.Categories[i].Values.Any(v => v.Trim() == raw))
                return i;
        throw new ArgumentException($"键值 {raw} 不在任何类别组内（List 分片不落地的值会被拒写）");
    }

    private static uint StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in s) { h ^= c; h *= 16777619; }
            return h;
        }
    }

    /// <summary>路由预览：一批样本键值逐个落片，读流量统一交 ReadRouter 延迟感知决策（每分片主库视为一个可读节点名，由执行层映射副本）。</summary>
    public static ShardRoutePreview Preview(ShardPlan plan, IEnumerable<string> sampleKeys,
        IEnumerable<ReplicaNode> replicas, long maxLagSeconds)
    {
        var routes = sampleKeys.Select(k => (k, RouteToShard(plan, k))).ToList();
        var read = ReadRouter.Route(replicas, maxLagSeconds);
        string note = read.FellBackToPrimary
            ? "读路由：无健康达标副本，读流量回落分片主库。"
            : $"读路由：健康副本 {string.Join("、", read.EligibleReplicas)}（延迟升序），写流量按上表落片。";
        return new ShardRoutePreview(routes, read, note);
    }

    /// <summary>分片计划摘要（确认页与路由预览共用一行口径）。</summary>
    public static string PlanSummary(ShardPlan plan) => plan.Strategy switch
    {
        ShardingWizard.ShardStrategy.Hash => $"表 {plan.Table}｜分片键 {plan.KeyColumn}｜按哈希 {plan.ShardCount} 片",
        ShardingWizard.ShardStrategy.Range => $"表 {plan.Table}｜分片键 {plan.KeyColumn}｜按范围 {plan.ShardCount} 段（上界 {string.Join("/", plan.Bounds)} + MAXVALUE）",
        ShardingWizard.ShardStrategy.List => $"表 {plan.Table}｜分片键 {plan.KeyColumn}｜按类别 {plan.ShardCount} 组（{string.Join("、", plan.Categories.Select(c => c.Name))}）",
        _ => $"表 {plan.Table}｜未知策略",
    };

    // ---------- 拓扑接入（联动二）：分片节点并入主从/多节点视图 ----------

    /// <summary>把分片计划渲染为拓扑节点行（角色=分片，状态=已注册），插在主从节点之后展示。</summary>
    public static IReadOnlyList<TopologyNode> TopologyRows(ShardPlan plan)
    {
        var rows = new List<TopologyNode>();
        for (int i = 0; i < plan.ShardCount; i++)
        {
            var detail = plan.Strategy switch
            {
                ShardingWizard.ShardStrategy.Hash => $"HASH 取模 {plan.ShardCount} 第 {i} 片",
                ShardingWizard.ShardStrategy.Range => $"RANGE 第 {i} 段（上界 {(i < plan.Bounds.Count ? plan.Bounds[i] : "MAXVALUE")}）",
                ShardingWizard.ShardStrategy.List => $"LIST 组 {plan.Categories[i].Name}（{string.Join(",", plan.Categories[i].Values)}）",
                _ => "",
            };
            rows.Add(new TopologyNode($"{plan.Table}.p{i}", "分片", "已注册", null, detail));
        }
        return rows;
    }

    // ---------- 计划持久化（联动开关打开时落盘，路由预览/拓扑读取复用） ----------

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string PlanFile(string table) => TaoPaths.FilePath($"shard-route-{Sanitize(table)}.json");

    private static string Sanitize(string s)
        => new(s.Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } v ? v : "plan";

    /// <summary>把分片计划持久化到用户配置目录（按表名一文件），覆盖旧计划。</summary>
    public static void SavePlan(ShardPlan plan)
        => File.WriteAllText(PlanFile(plan.Table), JsonSerializer.Serialize(plan, JsonOpts), System.Text.Encoding.UTF8);

    /// <summary>读取某表已注册的分片计划；无计划或解析失败返回 null（联动未开启属正常态）。</summary>
    public static ShardPlan? LoadPlan(string table)
    {
        try
        {
            var p = PlanFile(table);
            return File.Exists(p) ? JsonSerializer.Deserialize<ShardPlan>(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    /// <summary>枚举全部已注册的分片计划（供拓扑视图与路由页联动展示；损坏文件跳过）。</summary>
    public static IReadOnlyList<ShardPlan> AllPlans()
    {
        var plans = new List<ShardPlan>();
        try
        {
            var dir = Path.GetDirectoryName(PlanFile("plan"));
            if (dir is null || !Directory.Exists(dir)) return plans;
            foreach (var f in Directory.GetFiles(dir, "shard-route-*.json"))
            {
                try
                {
                    var plan = JsonSerializer.Deserialize<ShardPlan>(File.ReadAllText(f));
                    if (plan is not null) plans.Add(plan);
                }
                catch { /* 损坏计划跳过，不阻塞其余计划展示 */ }
            }
        }
        catch { /* 目录不可读按无计划处理 */ }
        return plans;
    }
}
