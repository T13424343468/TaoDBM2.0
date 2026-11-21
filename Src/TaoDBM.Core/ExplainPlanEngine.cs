using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>档三：执行计划可视化引擎——把 EXPLAIN 文本计划解析成树，标注瓶颈、生成索引建议。</summary>
/// <remarks>纯引擎免界面稿：兼容 SQLite EXPLAIN QUERY PLAN 多行与 MySQL 树形缩进文本；不做执行，仅解析与提示。</remarks>

/// <summary>计划树节点：原始文本 + 解析出的层级。</summary>
public sealed class ExplainNode
{
    public string Text { get; set; } = "";
    public int Depth { get; set; }
    public List<ExplainNode> Children { get; set; } = new();
}

/// <summary>计划瓶颈发现：P201 全表扫描 / P202 文件排序 / P203 临时表。</summary>
public sealed record PlanFinding(string Node, string Code, string Severity, string Message, string Suggestion);

/// <summary>解析后的计划：根节点列表 + 按前序遍历展开的节点列表。</summary>
public sealed class ExplainPlan
{
    public List<ExplainNode> Roots { get; set; } = new();
    public List<ExplainNode> Flat { get; set; } = new();

    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string ToJson() => JsonSerializer.Serialize(this, Opt);

    public static ExplainPlan? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<ExplainPlan>(json); }
        catch { return null; }
    }
}

public static class ExplainPlanEngine
{
    /// <summary>解析缩进/树形文本计划：按前导空白（Tab 按 4 空格）推断层级，深度跳级收敛到上一级 +1。</summary>
    public static ExplainPlan Parse(string planText)
    {
        var plan = new ExplainPlan();
        if (string.IsNullOrWhiteSpace(planText)) return plan;

        var stack = new List<ExplainNode>();
        foreach (var rawLine in planText.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            var expanded = rawLine.Replace("\t", "    ");
            int depth = 0;
            while (depth < expanded.Length && expanded[depth] == ' ') depth++;
            depth /= 2;
            var text = expanded.Trim().TrimStart('-', '>', '├', '└', '─', '│', ' ').Trim();
            if (text.Length == 0) continue;

            if (depth == 0 || stack.Count == 0)
            {
                var root = new ExplainNode { Text = text, Depth = 0 };
                plan.Roots.Add(root);
                stack.Clear();
                stack.Add(root);
                plan.Flat.Add(root);
            }
            else
            {
                if (depth > stack.Count) depth = stack.Count; // 跳级收敛
                while (stack.Count > depth) stack.RemoveAt(stack.Count - 1);
                var node = new ExplainNode { Text = text, Depth = depth };
                stack[^1].Children.Add(node);
                stack.Add(node);
                plan.Flat.Add(node);
            }
        }
        return plan;
    }

    /// <summary>瓶颈标注：P201 全表扫描 / P202 文件排序 / P203 临时表；全表扫描同时给出索引建议。
    /// 兼容 MySQL filesort / SQLite SCAN / MSSQL SHOWPLAN 文本（Table|Index Scan、Sort、Spool）。</summary>
    public static List<PlanFinding> Annotate(ExplainPlan plan)
    {
        var findings = new List<PlanFinding>();
        foreach (var node in plan.Flat)
        {
            var text = node.Text;
            if (text.Contains("filesort", StringComparison.OrdinalIgnoreCase) || text.Contains("文件排序"))
            {
                findings.Add(new PlanFinding(text, "P202", "danger", "需要额外一轮排序，数据量大时明显拖慢查询", "检查排序列是否已有索引，或让索引顺序同时覆盖过滤与排序列"));
                continue;
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\bSort\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                findings.Add(new PlanFinding(text, "P202", "danger", "需要额外一轮排序，数据量大时明显拖慢查询", "检查排序列是否已有索引，或让索引顺序同时覆盖过滤与排序列"));
                continue;
            }
            if (text.Contains("TEMP", StringComparison.OrdinalIgnoreCase) || text.Contains("临时表")
                || text.Contains("Spool", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new PlanFinding(text, "P203", "warn", "用临时表中转结果，额外消耗内存与落盘", "考虑减少 DISTINCT/GROUP BY 范围或为相关列建覆盖索引"));
                continue;
            }
            var table = ExtractFullScanTable(text);
            if (table != null)
            {
                findings.Add(new PlanFinding(text, "P201", "warn",
                    $"对表 {table} 做全表扫描，行数多时是主要耗时来源",
                    $"确认查询条件能否命中 {table} 的索引，必要时为过滤列建索引"));
            }
        }
        return findings;
    }

    /// <summary>提取全表扫描的目标表名：SQLite "SCAN 表名"、MySQL "Table scan on 表名"、
    /// MSSQL SHOWPLAN "Table Scan(OBJECT:([db].[schema].[table]))" / "Clustered Index Scan(OBJECT:(…))"。</summary>
    public static string? ExtractFullScanTable(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"Table scan on\s+`?(?<t>[\w$]+)`?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups["t"].Value;
        m = System.Text.RegularExpressions.Regex.Match(text, @"SCAN\s+(?!ON\b)`?(?<t>[\w$]+)`?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && !text.Contains("USING INDEX", StringComparison.OrdinalIgnoreCase)) return m.Groups["t"].Value;
        m = System.Text.RegularExpressions.Regex.Match(text, @"(?:Table|Clustered\s+Index|Index)\s+Scan\s*\(\s*OBJECT:\s*\((?<obj>[^\)]*)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var segs = System.Text.RegularExpressions.Regex.Matches(m.Groups["obj"].Value, @"\[([^\[\]]+)\]");
            if (segs.Count > 0) return segs[^1].Groups[1].Value;
        }
        return null;
    }

    /// <summary>取估算执行计划的包装语句：MySQL / PG 前缀 EXPLAIN；MSSQL 用 SET SHOWPLAN_ALL 包裹（估算计划，不执行查询本体）。</summary>
    public static string PlanSql(DbEngine e, string sql)
    {
        var s = sql.Trim().TrimEnd(';');
        return e switch
        {
            DbEngine.MySQL or DbEngine.PostgreSQL => $"EXPLAIN {s}",
            DbEngine.SqlServer => $"SET SHOWPLAN_ALL ON;\n{s};\nSET SHOWPLAN_ALL OFF;",
            _ => throw new NotSupportedException("该引擎暂无统一执行计划包装口径"),
        };
    }
}
