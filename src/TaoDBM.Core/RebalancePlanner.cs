namespace TaoDBM.Core;

/// <summary>再均衡输入：分片名与当前负载（行数或请求数，口径由调用方统一定）。</summary>
public sealed record RebalanceShard(string Name, long Load);

/// <summary>一条搬迁建议：从源分片搬多少负载到目标分片。</summary>
public sealed record RebalanceMove(string From, string To, long Rows);

/// <summary>再均衡计划：均值、搬迁清单与是否已均衡。</summary>
public sealed record RebalancePlan(double MeanLoad, IReadOnlyList<RebalanceMove> Moves, bool Balanced);

/// <summary>
/// 分库分区分表底座引擎：数据再均衡计划（新需求「图形化分库分区分表」配套）。
/// 依据负载画像纯计算：热点分片按超出均值的部分移出，冷清分片按缺口承接，
/// 贪心配对（热点按超出量降序、冷清按名字定序）保证确定序；实际搬数由执行层按 A5-2 对账防丢。
/// </summary>
public static class RebalancePlanner
{
    private sealed class Bucket
    {
        public string Name;
        public long Remaining;
        public Bucket(string name, long remaining) { Name = name; Remaining = remaining; }
    }

    /// <summary>计算搬迁计划：只搬整数负载，搬迁量为源超出与目标缺口的较小值；无超差时 Balanced=true。</summary>
    public static RebalancePlan Plan(IEnumerable<RebalanceShard> shards)
    {
        var list = shards.ToList();
        if (list.Count == 0)
            return new RebalancePlan(0, Array.Empty<RebalanceMove>(), Balanced: true);

        double mean = list.Average(s => (double)s.Load);
        var excess = list.Where(s => s.Load > (long)mean)
            .Select(s => new Bucket(s.Name, s.Load - (long)mean))
            .OrderByDescending(b => b.Remaining)
            .ThenBy(b => b.Name, StringComparer.Ordinal)
            .ToList();
        var deficit = list.Where(s => s.Load < (long)mean)
            .Select(s => new Bucket(s.Name, (long)mean - s.Load))
            .Where(b => b.Remaining > 0)
            .OrderBy(b => b.Name, StringComparer.Ordinal)
            .ToList();

        var moves = new List<RebalanceMove>();
        foreach (var hot in excess)
        {
            foreach (var cold in deficit)
            {
                if (hot.Remaining <= 0 || cold.Remaining <= 0) continue;
                var amount = Math.Min(hot.Remaining, cold.Remaining);
                moves.Add(new RebalanceMove(hot.Name, cold.Name, amount));
                hot.Remaining -= amount;
                cold.Remaining -= amount;
            }
        }

        return new RebalancePlan(mean, moves, Balanced: moves.Count == 0);
    }
}
