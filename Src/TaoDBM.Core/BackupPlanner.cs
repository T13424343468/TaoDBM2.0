using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>档三：定时备份计划引擎（全量/增量节奏生成、下次执行时间、按保留策略出过期清理清单）。</summary>
/// <remarks>纯引擎免界面稿：槽位为确定性计算，实际执行由任务中心触发，校验和/压缩由存储层接。</remarks>

/// <summary>备份策略：全量每 FullEveryHours 一次；两次全量之间每 IncrEveryHours 一次增量；RetainFulls 保留最近 N 份全量。</summary>
public sealed record BackupPolicy(int FullEveryHours, int IncrEveryHours, int RetainFulls, DateTimeOffset AnchorAt);

/// <summary>备份槽位：Kind=full/incr；Incr 槽挂在其前一个全量（Seq 序号）之后。</summary>
public sealed record BackupSlot(DateTimeOffset At, string Kind, int Seq);

/// <summary>保留清理清单：过期满量与挂在其后的过期增量一并出列。</summary>
public sealed record BackupPrunePlan(List<BackupSlot> Expired, int RetainedFulls, int RetainedIncrs);

public static class BackupPlanner
{
    /// <summary>生成 (fromExclusive, until] 内的备份槽位：全量从锚点按整间隔铺开（窗口外槽位跳过），增量铺满两个全量之间（不含全量时点）。</summary>
    public static List<BackupSlot> Schedule(BackupPolicy policy, DateTimeOffset fromExclusive, DateTimeOffset until)
    {
        if (policy.FullEveryHours <= 0) throw new ArgumentOutOfRangeException(nameof(policy), "全量间隔必须大于 0");
        var slots = new List<BackupSlot>();
        if (until <= fromExclusive) return slots;
        double delta = (fromExclusive - policy.AnchorAt).TotalHours;
        int k = delta > 0 ? (int)Math.Floor(delta / policy.FullEveryHours) + 1 : 0;
        for (var t = policy.AnchorAt.AddHours((double)k * policy.FullEveryHours); t <= until; k++, t = policy.AnchorAt.AddHours((double)k * policy.FullEveryHours))
        {
            slots.Add(new BackupSlot(t, "full", k));
            if (policy.IncrEveryHours > 0)
                for (var ti = t.AddHours(policy.IncrEveryHours);
                     ti < t.AddHours(policy.FullEveryHours) && ti <= until;
                     ti = ti.AddHours(policy.IncrEveryHours))
                    slots.Add(new BackupSlot(ti, "incr", k));
        }
        return slots.OrderBy(s => s.At).ToList();
    }

    /// <summary>下次执行时间：now 之后的最近槽位（全量或增量）。</summary>
    public static DateTimeOffset? NextRun(BackupPolicy policy, DateTimeOffset now)
        => Schedule(policy, now, now.AddHours(Math.Max(policy.FullEveryHours, 1)) + TimeSpan.FromHours(1))
            .Select(s => (DateTimeOffset?)s.At).FirstOrDefault();

    /// <summary>保留清理：超出 RetainFulls 的最旧全量及其挂靠增量出列（恢复链完整性优先，增量不脱离其全量单独保留）。</summary>
    public static BackupPrunePlan Prune(IReadOnlyList<BackupSlot> existing, BackupPolicy policy)
    {
        var fulls = existing.Where(s => s.Kind == "full").OrderByDescending(s => s.At).ToList();
        var expiredSeqs = fulls.Skip(Math.Max(policy.RetainFulls, 0)).Select(s => s.Seq).ToHashSet();
        var expired = existing.Where(s => s.Kind == "full" && expiredSeqs.Contains(s.Seq)
            || s.Kind == "incr" && expiredSeqs.Contains(s.Seq)).ToList();
        int expiredCount = expired.Count(s => s.Kind == "incr");
        return new BackupPrunePlan(expired.OrderBy(s => s.At).ToList(),
            fulls.Count - expiredSeqs.Count, existing.Count(s => s.Kind == "incr") - expiredCount);
    }

    public static string ToJson(BackupPolicy policy) =>
        JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true });

    public static BackupPolicy? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<BackupPolicy>(json); }
        catch (JsonException) { return null; }
        catch (ArgumentNullException) { return null; }
    }
}
