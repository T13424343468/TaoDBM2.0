﻿using System.Globalization;
using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>智能线第二批：查询结果出图聚合引擎（免界面稿，产出可持久化 JSON 的图表规格）。</summary>
/// <remarks>「一键出图」= 推荐图型 + 聚合；「看板可存」= ChartSpec JSON 往返；「定时刷新」= 对新结果重跑同一聚合。</remarks>

/// <summary>图表规格：Type=bar/pie/histogram/line；Labels 与 Values 一一对应；Extra 记录分桶区间等说明。</summary>
public sealed record ChartSpec(string ChartId, string Type, string Title, List<string> Labels, List<double> Values,
    string? Extra = null, DateTimeOffset CreatedAt = default);

public static class ChartSpecBuilder
{
    /// <summary>按列取值类型推荐图型：时间列→line，数值列→histogram，其余（分类）→bar。</summary>
    public static string RecommendType(QueryResult r, string column)
    {
        int idx = IndexOf(r, column);
        if (idx < 0 || r.Rows.Count == 0) return "bar";
        if (IsTimeColumn(r, idx)) return "line";
        if (IsNumericColumn(r, idx)) return "histogram";
        return "bar";
    }

    /// <summary>分类聚合：非空取值计数降序取 TopN（并列按取值字典序稳定排序）。</summary>
    public static ChartSpec CategoryCount(QueryResult r, string column, int topN = 10, string? chartId = null)
    {
        int idx = Require(r, column);
        var counts = new Dictionary<string, double>();
        foreach (var row in r.Rows)
        {
            var v = Text(row[idx]);
            if (v == null) continue;
            counts[v] = counts.GetValueOrDefault(v) + 1;
        }
        var top = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(Math.Max(topN, 1)).ToList();
        return new ChartSpec(chartId ?? $"bar-{column}", "bar", $"{column} 分布",
            top.Select(kv => kv.Key).ToList(), top.Select(kv => kv.Value).ToList(),
            $"共 {counts.Count} 类，取前 {top.Count}", DateTimeOffset.UtcNow);
    }

    /// <summary>数值直方图：min..max 等宽 bins；区间标签 [a,b)，末桶闭区间含右端点；单值退化为单桶。</summary>
    public static ChartSpec Histogram(QueryResult r, string column, int bins = 10, string? chartId = null)
    {
        int idx = Require(r, column);
        var nums = r.Rows.Select(row => Num(row[idx])).Where(v => v != null).Select(v => v!.Value).ToList();
        if (nums.Count == 0)
            return new ChartSpec(chartId ?? $"histogram-{column}", "histogram", $"{column} 直方图",
                new List<string>(), new List<double>(), "无数值", DateTimeOffset.UtcNow);
        double min = nums.Min(), max = nums.Max();
        int n = Math.Max(bins, 1);
        if (max <= min) max = min + 1;
        double width = (max - min) / n;
        var counts = new double[n];
        foreach (var v in nums)
        {
            int b = (int)((v - min) / width);
            if (b >= n) b = n - 1;
            counts[b]++;
        }
        var labels = Enumerable.Range(0, n).Select(i =>
        {
            string a = Fmt(min + i * width), b2 = Fmt(min + (i + 1) * width);
            return $"[{a},{b2}{(i == n - 1 ? "]" : ")")}";
        }).ToList();
        return new ChartSpec(chartId ?? $"histogram-{column}", "histogram", $"{column} 直方图",
            labels, counts.ToList(), $"{nums.Count} 个数值", DateTimeOffset.UtcNow);
    }

    /// <summary>时间序列：时间列按 day/week/month 分桶；给 valueColumn 则按桶求和，否则按桶计数。</summary>
    public static ChartSpec TimeSeries(QueryResult r, string timeColumn, string? valueColumn = null, string bucket = "day", string? chartId = null)
    {
        int tIdx = Require(r, timeColumn);
        int? vIdx = valueColumn == null ? null : Require(r, valueColumn);
        if (bucket is not ("day" or "week" or "month")) bucket = "day";
        var map = new SortedDictionary<DateTimeOffset, double>();
        foreach (var row in r.Rows)
        {
            var t = Time(row[tIdx]);
            if (t == null) continue;
            var key = BucketOf(t.Value, bucket);
            double add = vIdx == null ? 1 : Num(row[vIdx.Value]) ?? 0;
            map[key] = map.GetValueOrDefault(key) + add;
        }
        var keys = map.Keys.ToList();
        return new ChartSpec(chartId ?? $"line-{timeColumn}", "line",
            valueColumn == null ? $"{timeColumn} 趋势（计数）" : $"{valueColumn} 按 {timeColumn} 汇总",
            keys.Select(k => k.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList(),
            keys.Select(k => map[k]).ToList(), $"粒度={bucket}", DateTimeOffset.UtcNow);
    }

    public static string ToJson(ChartSpec spec) =>
        JsonSerializer.Serialize(spec, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>报表页时间范围过滤口径：找结果集中第一个时间列，没有返回 -1。</summary>
    public static int FindTimeColumn(QueryResult r)
    {
        for (int i = 0; i < r.Columns.Count; i++)
            if (IsTimeColumn(r, i)) return i;
        return -1;
    }

    /// <summary>报表页 KPI 口径公开取数：数值解析（Num 公开口）。</summary>
    public static double? TryNum(object? v) => Num(v);

    /// <summary>报表页 KPI 口径公开取数：时间解析（Time 公开口）。</summary>
    public static DateTimeOffset? TryTime(object? v) => Time(v);

    public static ChartSpec? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<ChartSpec>(json); }
        catch (JsonException) { return null; }
        catch (ArgumentNullException) { return null; }
    }

    static DateTimeOffset BucketOf(DateTimeOffset t, string bucket) => bucket switch
    {
        "week" => t.AddDays(-(double)(int)t.DayOfWeek).Date,
        "month" => new DateTimeOffset(t.Year, t.Month, 1, 0, 0, 0, t.Offset),
        _ => t.Date,
    };

    static int IndexOf(QueryResult r, string column) => r.Columns.IndexOf(column);

    static int Require(QueryResult r, string column)
    {
        int idx = IndexOf(r, column);
        if (idx < 0) throw new ArgumentException($"结果集中不存在列：{column}");
        return idx;
    }

    static bool IsNumericColumn(QueryResult r, int idx)
    {
        int seen = 0;
        foreach (var row in r.Rows)
        {
            if (row[idx] == null || row[idx] is DBNull) continue;
            if (Num(row[idx]) == null) return false;
            if (++seen >= 8) break;
        }
        return seen > 0;
    }

    static bool IsTimeColumn(QueryResult r, int idx)
    {
        int seen = 0;
        foreach (var row in r.Rows)
        {
            if (row[idx] == null || row[idx] is DBNull) continue;
            if (Time(row[idx]) == null) return false;
            if (++seen >= 8) break;
        }
        return seen > 0;
    }

    static double? Num(object? v) => v switch
    {
        null or DBNull => null,
        double d => double.IsFinite(d) ? d : null,
        float f => double.IsFinite(f) ? f : null,
        decimal m => (double)m,
        int or long or short or byte or sbyte or uint or ulong or ushort => Convert.ToDouble(v),
        string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null,
        _ => null,
    };

    static DateTimeOffset? Time(object? v) => v switch
    {
        null or DBNull => null,
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, dt.Kind == DateTimeKind.Utc ? DateTimeKind.Utc : DateTimeKind.Unspecified)),
        string s => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null,
        _ => null,
    };

    static string? Text(object? v) => v switch
    {
        null or DBNull => null,
        string s => s,
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
