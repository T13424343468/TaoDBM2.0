﻿using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>智能线第二批：数据体检报告引擎（空值/重复/分布突变体检，异常主动报）。</summary>
/// <remarks>纯引擎免界面稿：画像由采集层逐表逐列填充，体检与对比逻辑引擎无关；「定时」由外部调度触发 Inspect/Compare。</remarks>

/// <summary>单列画像：RowCount=表行数，NullCount=空值数，DuplicateCount=重复行数（除首现外再次出现的行数）。</summary>
public sealed record ColumnProfile(string Table, string Column, long RowCount, long NullCount, long DuplicateCount,
    double? NumericMean = null, Dictionary<string, double>? CategoryShare = null);

/// <summary>体检发现：Code=规则号（H001 空值 / H002 重复 / H003 类别占比突变 / H004 数值均值漂移），Severity=info/warn/danger。</summary>
public sealed record HealthFinding(string Table, string Column, string Code, string Severity, string Message, string? Suggestion = null);

/// <summary>体检报告：Score 为 100 分扣分制（danger -20 / warn -5 / info -1，下限 0）。</summary>
public sealed record HealthReport(int Score, List<HealthFinding> Findings);

/// <summary>体检阈值：可按库特性覆盖默认口径。</summary>
public sealed record HealthThresholds(double NullWarn = 0.10, double NullDanger = 0.50, double DupWarn = 0.30,
    double DriftShare = 0.20, double DriftMean = 0.30);

public static class DataHealthCheck
{
    /// <summary>单次体检：逐列跑空值率（H001）与重复率（H002）规则。</summary>
    public static HealthReport Inspect(IReadOnlyList<ColumnProfile> profiles, HealthThresholds? thresholds = null)
    {
        var th = thresholds ?? new HealthThresholds();
        var findings = new List<HealthFinding>();
        foreach (var p in profiles)
        {
            if (p.RowCount <= 0) continue;
            double nullRate = (double)p.NullCount / p.RowCount;
            if (nullRate >= th.NullDanger)
                findings.Add(new HealthFinding(p.Table, p.Column, "H001", "danger",
                    $"空值率 {nullRate:P0} 达到 {th.NullDanger:P0} 红线",
                    "确认业务语义：补默认值、回填历史数据或收紧写入约束"));
            else if (nullRate >= th.NullWarn)
                findings.Add(new HealthFinding(p.Table, p.Column, "H001", "warn",
                    $"空值率 {nullRate:P0} 偏高", "抽样核对来源，必要时补默认值或校验写入链路"));

            double dupRate = (double)p.DuplicateCount / p.RowCount;
            if (dupRate >= th.DupWarn)
                findings.Add(new HealthFinding(p.Table, p.Column, "H002", "warn",
                    $"重复值占比 {dupRate:P0}（{p.DuplicateCount}/{p.RowCount} 行）",
                    "确认该列是否应唯一；需要时补唯一约束或清理重复数据"));
        }
        return new HealthReport(Score(findings), findings);
    }

    /// <summary>分布突变对比：类别占比漂移（H003）或数值均值漂移（H004）超阈值即报；上次体检与本轮同表同列才比。</summary>
    public static List<HealthFinding> Compare(IReadOnlyList<ColumnProfile> current, IReadOnlyList<ColumnProfile> previous, HealthThresholds? thresholds = null)
    {
        var th = thresholds ?? new HealthThresholds();
        var findings = new List<HealthFinding>();
        var prev = previous.ToDictionary(p => (p.Table, p.Column));
        foreach (var cur in current)
        {
            if (!prev.TryGetValue((cur.Table, cur.Column), out var old)) continue;
            if (cur.CategoryShare != null && old.CategoryShare != null)
            {
                var keys = cur.CategoryShare.Keys.Union(old.CategoryShare.Keys);
                double maxShift = keys.Max(k =>
                    Math.Abs(cur.CategoryShare.GetValueOrDefault(k) - old.CategoryShare.GetValueOrDefault(k)));
                if (maxShift >= th.DriftShare)
                    findings.Add(new HealthFinding(cur.Table, cur.Column, "H003", "warn",
                        $"类别占比突变：最大偏移 {maxShift:P0}",
                        "确认上游是否改了取值口径，或脏数据入仓"));
            }
            if (cur.NumericMean is double cm && old.NumericMean is double om && om != 0)
            {
                double drift = Math.Abs(cm - om) / Math.Abs(om);
                if (drift >= th.DriftMean)
                    findings.Add(new HealthFinding(cur.Table, cur.Column, "H004", "warn",
                        $"数值均值漂移 {drift:P0}（{om:G6} → {cm:G6}）",
                        "抽查增量数据，确认异常写入或单位变化"));
            }
        }
        return findings;
    }

    public static string ToJson(HealthReport report) =>
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });

    public static HealthReport? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<HealthReport>(json); }
        catch (JsonException) { return null; }
        catch (ArgumentNullException) { return null; }
    }

    static int Score(List<HealthFinding> findings) => Math.Max(0,
        100 - 20 * findings.Count(f => f.Severity == "danger")
            - 5 * findings.Count(f => f.Severity == "warn")
            - findings.Count(f => f.Severity == "info"));
}
