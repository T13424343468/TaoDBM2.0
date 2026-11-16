using System.Text;
using System.Text.RegularExpressions;

namespace TaoDBM.Core;

/// <summary>P-3 自然语言问库：把常见中文数据问题解析成可检查的 SELECT，不调用外部模型、不触库。</summary>
/// <remarks>纯引擎免界面稿：问题解析基于确定性的中文模式匹配，生成结果必须经人确认后执行；
/// 解析不了的问题明确报出原因，不猜测、不拼凑。</remarks>

/// <summary>问库结果：Ok=能否生成；Sql=生成的 SELECT；Interpretation=人话解释；Confidence=high/low；
/// UnsupportedReason=解析失败原因（Ok=false 时给出）。</summary>
public sealed record NLQueryResult(bool Ok, string Sql, string Interpretation, string Confidence, string? UnsupportedReason = null);

public static class NaturalLanguageQuery
{
    /// <summary>把中文问题解析成 SELECT。columns 来自 GetColumns，table 为目标表，engine 决定方言细节。</summary>
    public static NLQueryResult Ask(string question, string table, IReadOnlyList<ColumnSpec> columns, DbEngine engine)
    {
        var q = (question ?? "").Trim().Trim('？', '?', '。', '.').Replace(" ", "");
        if (q.Length == 0)
            return Fail("请先输入问题。");
        if (columns.Count == 0)
            return Fail("没有可用的列信息，请先刷新表结构。");

        var cols = columns.Where(c => !string.IsNullOrEmpty(c.Name)).ToList();

        ColumnSpec? FindCol(string text)
        {
            var t = text.Replace(" ", "").Replace("_", "").ToLowerInvariant();
            if (t.Length == 0) return null;
            foreach (var c in cols)
                if (t.Contains(c.Name.Replace("_", "").ToLowerInvariant()))
                    return c;
            return null;
        }

        var mentioned = cols.Where(c => q.Contains(c.Name.Replace("_", "").ToLowerInvariant())).ToList();

        // ---- 聚合词 ----
        bool wantsDistinct = q.Contains("不同") || q.Contains("去重");
        bool wantsCount = Regex.IsMatch(q, "多少|几条|几行|几笔|几只|总数|行数");
        bool wantsAvg = q.Contains("平均");
        bool wantsMax = q.Contains("最大") || q.Contains("最高") || q.Contains("最多") || q.Contains("最晚");
        bool wantsMin = q.Contains("最小") || q.Contains("最低") || q.Contains("最少") || q.Contains("最早");
        bool wantsSum = q.Contains("总和") || q.Contains("合计") || q.Contains("总共多少");
        bool isAgg = wantsAvg || wantsMax || wantsMin || wantsSum;

        int? limit = null;
        var mTop = Regex.Match(q, @"前(\d+|[一二三四五六七八九十]+)名?|([一二三四五六七八九十]+)条");
        if (mTop.Success) limit = ParseNum(mTop.Groups[1].Success ? mTop.Groups[1].Value : mTop.Groups[2].Value);
        bool wantsOrder = wantsMax || wantsMin || q.Contains("排序") || q.Contains("从高到低") || q.Contains("从低到高") || limit is not null;

        // ---- 过滤条件 ----
        var conditions = new List<string>();
        string qt = Quote(engine, table);
        foreach (var c in mentioned)
        {
            string where = BuildConditions(q, c, Quote(engine, c.Name), engine);
            if (!string.IsNullOrEmpty(where)) conditions.Add(where);
        }

        // ---- 聚合目标列 ----
        ColumnSpec? aggCol = null;
        if (wantsAvg || wantsMax || wantsMin || wantsSum)
        {
            aggCol = FindCol(AggText(q)) ?? mentioned.FirstOrDefault(c => !IsTimeCol(c) || wantsMax || wantsMin)
                     ?? cols.FirstOrDefault(c => !IsTimeCol(c) && !c.IsKey);
            if (aggCol is null)
                return Fail("没认出要统计的列，请写清楚列名，例如“orders 表 amount 的平均值是多少”。");
        }

        string select;
        string human;
        if (wantsCount && wantsDistinct)
        {
            var dc = FindCol(DistinctText(q)) ?? mentioned.FirstOrDefault();
            if (dc is null) return Fail("没认出要去重的列，请写“有多少个不同的<列名>”。");
            select = $"SELECT COUNT(DISTINCT {Quote(engine, dc.Name)}) AS cnt FROM {qt}";
            human = $"统计表 {table} 中 {dc.Name} 的去重数量。";
        }
        else if (wantsCount)
        {
            select = $"SELECT COUNT(*) AS cnt FROM {qt}";
            human = $"统计表 {table} 的总行数。";
        }
        else if (isAgg)
        {
            string fn = wantsAvg ? "AVG" : wantsMax ? "MAX" : wantsMin ? "MIN" : "SUM";
            string fnName = wantsAvg ? "平均值" : wantsMax ? "最大值" : wantsMin ? "最小值" : "总和";
            select = $"SELECT {fn}({Quote(engine, aggCol!.Name)}) AS v FROM {qt}";
            human = $"计算表 {table} 中 {aggCol.Name} 的{fnName}。";
        }
        else if (mentioned.Count > 0)
        {
            var oc = wantsOrder ? mentioned[0] : null;
            select = $"SELECT * FROM {qt}";
            human = $"列出表 {table} 中满足条件的记录。";
        }
        else
        {
            select = $"SELECT * FROM {qt}";
            human = $"列出表 {table} 的全部记录。";
        }

        if (conditions.Count > 0)
            select += " WHERE " + string.Join(" AND ", conditions);

        if (wantsOrder && !wantsCount && !wantsAvg && !wantsMax && !wantsMin && !wantsSum)
        {
            var oc = mentioned.FirstOrDefault();
            if (oc is not null)
                select += $" ORDER BY {Quote(engine, oc.Name)} {(wantsMin ? "ASC" : "DESC")}";
        }

        if (limit is > 0 && !wantsCount && !wantsAvg && !wantsMax && !wantsMin && !wantsSum)
        {
            select += engine switch
            {
                DbEngine.SqlServer => $" OFFSET 0 ROWS FETCH NEXT {limit} ROWS ONLY",
                DbEngine.Oracle => $" FETCH FIRST {limit} ROWS ONLY",
                _ => $" LIMIT {limit}",
            };
        }

        return new NLQueryResult(true, select + ";", human,
            mentioned.Count > 0 || wantsCount ? "high" : "low");
    }

    static string AggText(string q)
    {
        // “amount 的平均值”“平均 amount”“amount平均值”
        var m = Regex.Match(q, @"([\w\u4e00-\u9fa5]+?)的?(平均值|平均|最大值|最大|最高|最多|最晚|最小值|最小|最低|最少|最早|总和|合计)");
        return m.Success ? m.Groups[1].Value : "";
    }

    static string DistinctText(string q)
    {
        var m = Regex.Match(q, @"不同的([\w\u4e00-\u9fa5]+)");
        return m.Success ? m.Groups[1].Value : "";
    }

    static string BuildConditions(string q, ColumnSpec c, string qc, DbEngine engine)
    {
        var conds = new List<string>();
        bool isTime = IsTimeCol(c);
        bool isNum = IsNumCol(c);

        // 数值比较：列 大于/小于/超过/不低于 N（列名需出现在比较词之前）
        foreach (var op in new[] { "大于", "超过", "高于", "不少于", "不低于", "小于", "低于", "不多于" })
        {
            var m = Regex.Match(q, Regex.Escape(c.Name) + op + @"(\d+(?:\.\d+)?)");
            if (!m.Success) continue;
            string op2 = op is "大于" or "超过" or "高于" ? ">" : op is "小于" or "低于" ? "<" : ">=";
            conds.Add($"{qc} {op2} {m.Groups[1].Value}");
            break;
        }
        if (conds.Count == 0 && isNum)
        {
            var m = Regex.Match(q, @"(\d+(?:\.\d+)?)(以上|以下)");
            if (m.Success) conds.Add($"{qc} {(m.Groups[2].Value == "以上" ? ">=" : "<=")} {m.Groups[1].Value}");
        }

        // 相等：列 是/等于/为 X（仅文本列）
        var me = Regex.Match(q, Regex.Escape(c.Name) + @"(?:是|等于|为)([\w\u4e00-\u9fa5]+)");
        if (me.Success && !isNum && !isTime)
            conds.Add($"{qc} = '{me.Groups[1].Value.Replace("'", "''")}'");

        // 包含：列 包含/含有/含 X（仅文本列）
        var mc = Regex.Match(q, Regex.Escape(c.Name) + @"(?:包含|含有|含)([\w\u4e00-\u9fa5]+)");
        if (mc.Success && !isNum && !isTime)
            conds.Add($"{qc} LIKE '%{mc.Groups[1].Value.Replace("'", "''")}%'");

        // 时间条件：交给日期列
        if (isTime)
        {
            string? range = TimeRange(q, engine, qc);
            if (range is not null) conds.Add(range);
        }

        return string.Join(" AND ", conds);
    }

    static string? TimeRange(string q, DbEngine engine, string qc)
    {
        var days = Regex.Match(q, @"(?:最近|近|过去)(\d+)天");
        if (days.Success)
        {
            int n = int.Parse(days.Groups[1].Value);
            string expr = engine == DbEngine.MySQL
                ? $"NOW() - INTERVAL {n} DAY"
                : $"CURRENT_DATE - INTERVAL '{n} DAY'";
            return $"{qc} >= {expr}";
        }
        if (q.Contains("今天") || q.Contains("今日"))
            return engine == DbEngine.MySQL ? $"{qc} >= CURDATE()" : $"{qc} >= CURRENT_DATE";
        if (q.Contains("昨天") || q.Contains("昨日"))
            return engine == DbEngine.MySQL
                ? $"{qc} >= CURDATE() - INTERVAL 1 DAY"
                : $"{qc} >= CURRENT_DATE - INTERVAL '1 DAY'";
        return null;
    }

    static NLQueryResult Fail(string reason) => new NLQueryResult(false, "", "", "none", reason);

    static bool IsTimeCol(ColumnSpec c) =>
        c.DataType.ToLowerInvariant().Contains("date") || c.DataType.ToLowerInvariant().Contains("time") ||
        c.Name.ToLowerInvariant() is "created_at" or "updated_at" || c.Name.Contains("时间") || c.Name.Contains("日期");

    static bool IsNumCol(ColumnSpec c) =>
        new[] { "int", "decimal", "numeric", "float", "double", "real", "bigint", "smallint", "number" }
            .Any(k => c.DataType.ToLowerInvariant().Contains(k));

    static int? ParseNum(string s)
    {
        if (int.TryParse(s, out int n)) return n;
        return s switch
        {
            "一" => 1, "二" => 2, "两" => 2, "三" => 3, "四" => 4, "五" => 5,
            "六" => 6, "七" => 7, "八" => 8, "九" => 9, "十" => 10,
            _ => null,
        };
    }

    static string Quote(DbEngine engine, string ident) => engine switch
    {
        DbEngine.MySQL => "`" + ident.Replace("`", "") + "`",
        _ => "\"" + ident.Replace("\"", "") + "\"",
    };
}
