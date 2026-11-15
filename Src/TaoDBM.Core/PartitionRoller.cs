using System.Globalization;

namespace TaoDBM.Core;

/// <summary>时间分区：名字与左闭右开区间 [StartInclusive, EndExclusive)。</summary>
public sealed record TimePartition(string Name, DateTime StartInclusive, DateTime EndExclusive);

/// <summary>滚动计划：需新建分区、建议归档分区（超出保留窗口）与是否已覆盖目标时刻。</summary>
public sealed record RollPlan(IReadOnlyList<TimePartition> ToAdd, IReadOnlyList<string> ToArchive, bool CoversHorizon);

/// <summary>
/// 分库分区分表底座引擎：时间分区自动滚动（新需求「图形化分库分区分表」配套）。
/// 按固定时间粒度对齐分区槽位，从既有分区末尾向前滚动补齐到目标时刻；
/// 超出保留窗口的旧分区给出归档清单。纯计算不触库，DDL 由执行层按方言生成。
/// </summary>
public static class PartitionRoller
{
    /// <summary>
    /// 计算滚动计划：新增分区从既有分区末尾（无分区则从目标时刻所在槽位）连续铺到覆盖 horizon；
    /// 归档 EndExclusive ≤ horizon − grain × retainCount 的既有分区。
    /// </summary>
    public static RollPlan EnsureCoverage(IReadOnlyList<TimePartition> existing, DateTime horizon, TimeSpan grain, int retainCount)
    {
        if (grain <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(grain), "分区粒度必须为正");
        if (retainCount < 0) throw new ArgumentOutOfRangeException(nameof(retainCount), "保留窗口不能为负");

        var covered = new HashSet<DateTime>();
        DateTime lastEnd = DateTime.MinValue;
        foreach (var p in existing)
        {
            covered.Add(Floor(p.StartInclusive, grain));
            if (p.EndExclusive > lastEnd) lastEnd = p.EndExclusive;
        }

        var toAdd = new List<TimePartition>();
        var slot = lastEnd == DateTime.MinValue ? Floor(horizon, grain) : lastEnd;
        while (slot <= horizon)
        {
            if (covered.Add(slot))
                toAdd.Add(new TimePartition(PartitionName(slot, grain), slot, slot + grain));
            slot += grain;
        }

        var cutoff = horizon - grain * retainCount;
        var toArchive = existing.Where(p => p.EndExclusive <= cutoff).Select(p => p.Name).ToList();
        bool covers = (lastEnd != DateTime.MinValue && horizon < lastEnd) ||
                      toAdd.Any(p => p.StartInclusive <= horizon && horizon < p.EndExclusive) ||
                      existing.Any(p => p.StartInclusive <= horizon && horizon < p.EndExclusive);
        return new RollPlan(toAdd, toArchive, covers);
    }

    /// <summary>把时刻向下对齐到粒度槽位起点。</summary>
    public static DateTime Floor(DateTime t, TimeSpan grain) =>
        new(t.Ticks - t.Ticks % grain.Ticks, t.Kind);

    /// <summary>分区名：按粒度取 p_yyyyMMdd（天及以上）或 p_yyyyMMdd_HHmm（更细粒度）。</summary>
    public static string PartitionName(DateTime slotStart, TimeSpan grain) => grain >= TimeSpan.FromDays(1)
        ? "p_" + slotStart.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
        : "p_" + slotStart.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);
}
