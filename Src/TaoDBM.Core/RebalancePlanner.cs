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

    /// <summary>计算搬迁计划：只搬整数负载，搬迁量为源超出与目标缺口的较小值；无超差时 Balanced=true。
    /// 发现122：输入校验——空名/负负载直接拒绝；发现123：均衡判定改 double 容差比较，不再截断。</summary>
    public static RebalancePlan Plan(IEnumerable<RebalanceShard> shards)
    {
        var list = shards.ToList();
        if (list.Count == 0)
            return new RebalancePlan(0, Array.Empty<RebalanceMove>(), Balanced: true);

        // 发现122：空名与负负载当场拒绝，重复名拒绝（口径含糊无法对账）
        foreach (var s in list)
        {
            if (string.IsNullOrWhiteSpace(s.Name))
                throw new ArgumentException("分片名不能为空，请给每个分片起名后再算均衡。");
            if (s.Load < 0)
                throw new ArgumentException($"分片「{s.Name}」的负载为负数（{s.Load}），请检查输入。");
        }
        if (list.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != list.Count)
            throw new ArgumentException("分片名有重复，请核对后再算均衡。");

        double mean = list.Average(s => (double)s.Load);
        // 发现123：用 double 容差比较，(long)mean 截断会把「已均衡」误判成要搬
        const double eps = 1e-9;
        var excess = list.Where(s => s.Load > mean + eps)
            .Select(s => new Bucket(s.Name, (long)Math.Ceiling(s.Load - mean)))
            .OrderByDescending(b => b.Remaining)
            .ThenBy(b => b.Name, StringComparer.Ordinal)
            .ToList();
        var deficit = list.Where(s => s.Load < mean - eps)
            .Select(s => new Bucket(s.Name, (long)Math.Floor(mean - s.Load)))
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

        // 发现123：Balanced 只在负载完全一致（差距为 0）时成立；
        // 有差距但整数搬迁无可行解（如 {1,2}）时 Balanced=false，由界面文案归真「无可行整数搬迁，仍存在差距」
        var spread = list.Max(s => (double)s.Load) - list.Min(s => (double)s.Load);
        return new RebalancePlan(mean, moves, Balanced: moves.Count == 0 && spread <= eps);
    }
}
