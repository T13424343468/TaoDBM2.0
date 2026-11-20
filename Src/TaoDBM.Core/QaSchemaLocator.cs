using System.Text.RegularExpressions;

namespace TaoDBM.Core;

/// <summary>
/// 问库对象锚定（2026-11-20 23:29 老板令）：问库无论本地规则层还是模型层，
/// 第一跳先从当前已连接对象解析库/表/字段，作为上下文喂给后续每一跳；
/// 出口校验生成 SQL 引用的表必须真实存在于当前连接，不存在即打回——防幻觉、限范围、供上下文三合一。
/// </summary>
public static class QaSchemaLocator
{
    /// <summary>定位结果：Table=候选表；Score=匹配分（表名命中 10，列名命中每列 2）；MatchedColumns=命中的列名。</summary>
    public sealed record TableMatch(MultiTableSpec Table, int Score, IReadOnlyList<string> MatchedColumns);

    private static string Norm(string s) => (s ?? "").Replace("_", "").ToLowerInvariant();

    /// <summary>按问题文本在已连接表集合中定位候选表，按匹配分降序返回；无命中返回空表。</summary>
    public static IReadOnlyList<TableMatch> Locate(string? question, IEnumerable<MultiTableSpec> tables)
    {
        var q = Norm(question ?? "");
        if (q.Length == 0 || tables is null)
            return Array.Empty<TableMatch>();
        var matches = new List<TableMatch>();
        foreach (var t in tables)
        {
            if (t is null) continue;
            int score = 0;
            var hitCols = new List<string>();
            if (t.Name.Length > 0 && q.Contains(Norm(t.Name)))
                score += 10;
            foreach (var c in t.Columns)
            {
                if (string.IsNullOrEmpty(c.Name)) continue;
                if (q.Contains(Norm(c.Name)))
                {
                    score += 2;
                    hitCols.Add(c.Name);
                }
            }
            if (score > 0)
                matches.Add(new TableMatch(t, score, hitCols));
        }
        return matches.OrderByDescending(m => m.Score).ThenBy(m => m.Table.Name, StringComparer.Ordinal).ToList();
    }

    private static readonly Regex TableRef = new(
        @"\b(?:from|join)\s+(?:[`""\[]?)(\w+(?:\.\w+)*)(?:[`""\]]?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>出口校验：SQL 中 FROM/JOIN 引用的表必须都在已连接对象里（忽略大小写与下划线差异），
    /// 否则打回。schema.table 点号引用取末段比对。空 SQL 或无表引用视为不通过。</summary>
    public static (bool Ok, string? Reason) ValidateExit(string? sql, IEnumerable<MultiTableSpec> tables)
    {
        var s = (sql ?? "").Trim();
        if (s.Length == 0)
            return (false, "没有生成任何 SQL，请换一种问法。");
        var known = new HashSet<string>(tables?.Select(t => Norm(t.Name)) ?? Enumerable.Empty<string>());
        var refs = TableRef.Matches(s).Select(m => m.Groups[1].Value).Distinct().ToList();
        if (refs.Count == 0)
            return (false, "生成的 SQL 没有引用任何表，已打回。");
        foreach (var r in refs)
        {
            var last = r.Split('.').Last();
            if (!known.Contains(Norm(last)))
                return (false, $"SQL 引用了当前连接里不存在的表「{r}」，已打回。");
        }
        return (true, null);
    }
}
