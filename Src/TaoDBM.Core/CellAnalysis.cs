using System.Globalization;

namespace TaoDBM.Core;

/// <summary>点选式分析（批次E，2026-11-20 老板令「全覆盖」）：结果网格点选列头/单元格 → 一键概览该列。
/// 纯逻辑层实现，LogicTest 直测；UI 侧只挂右键菜单入口。禁思考链、无外部依赖。</summary>
public static class CellAnalysis
{
    /// <summary>列概览：总数 / 非空 / 去重 / TopN / 数值列的 min-max-avg。</summary>
    public sealed record Summary(string Column, long Total, long NonNull, long Distinct,
        List<(string Value, double Count)> Top, double? Min, double? Max, double? Avg, bool IsNumeric)
    {
        /// <summary>给 UI 一句话概览（右键菜单副标题用）。</summary>
        public string Headline
        {
            get
            {
                var baseText = $"共 {Total} 行，非空 {NonNull}，去重 {Distinct}";
                return IsNumeric && Avg is not null
                    ? $"{baseText}，min {MinText} / max {MaxText} / avg {AvgText}"
                    : baseText;
            }
        }

        string MinText => Min?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-";
        string MaxText => Max?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-";
        string AvgText => Avg?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-";
    }

    /// <summary>单列分析入口。TopN 降序计数（并列按取值字典序稳定）；数值列（前 8 行非空即数）附 min/max/avg。</summary>
    public static Summary Analyze(QueryResult r, string column, int topN = 5)
    {
        int idx = Require(r, column);
        var counts = new Dictionary<string, double>();
        long total = r.Rows.Count, nonNull = 0, distinct;
        var nums = new List<double>();
        bool numeric = true;
        foreach (var row in r.Rows)
        {
            var v = row[idx];
            if (v == null || v is DBNull) continue;
            nonNull++;
            var text = Text(v);
            counts[text] = counts.GetValueOrDefault(text) + 1;
            if (Num(v) is { } d) nums.Add(d); else numeric = false;
        }
        distinct = counts.Count;
        // 数值口径与 ChartSpecBuilder 一致：抽前 8 个非空值判定，避免首行 NULL 误判
        numeric = numeric && SampleNumeric(r, idx);
        if (!numeric) nums.Clear();
        var top = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(Math.Max(topN, 1)).Select(kv => (kv.Key, kv.Value)).ToList();
        return new Summary(column, total, nonNull, distinct, top,
            nums.Count > 0 ? nums.Min() : null, nums.Count > 0 ? nums.Max() : null,
            nums.Count > 0 ? nums.Average() : null, numeric);
    }

    /// <summary>TopN 分类聚合直接接 ChartSpec，出图与看板共用一条规格链。</summary>
    public static ChartSpec ToChart(QueryResult r, string column, int topN = 5) =>
        ChartSpecBuilder.CategoryCount(r, column, topN, $"analysis-{column}");

    static bool SampleNumeric(QueryResult r, int idx)
    {
        int seen = 0;
        foreach (var row in r.Rows)
        {
            var v = row[idx];
            if (v == null || v is DBNull) continue;
            if (Num(v) == null) return false;
            if (++seen >= 8) break;
        }
        return seen > 0;
    }

    static int Require(QueryResult r, string column)
    {
        int idx = r.Columns.IndexOf(column);
        if (idx < 0) throw new ArgumentException($"结果集中不存在列：{column}");
        return idx;
    }

    static string Text(object? v) => v switch
    {
        string s => s,
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v?.ToString() ?? "",
    };

    static double? Num(object? v) => v switch
    {
        double d => double.IsFinite(d) ? d : null,
        float f => double.IsFinite(f) ? f : null,
        decimal m => (double)m,
        int or long or short or byte or sbyte or uint or ulong or ushort => Convert.ToDouble(v),
        string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null,
        _ => null,
    };
}
