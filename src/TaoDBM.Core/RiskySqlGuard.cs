using System.Text;
using System.Text.RegularExpressions;

namespace TaoDBM.Core;

/// <summary>一条语句的风险检查结果。</summary>
public sealed record RiskCheck
{
    /// <summary>ok / warn（可执行但建议确认）/ danger（建议拦截，需预览+确认）。</summary>
    public string Level { get; init; } = "ok";
    /// <summary>ok / no-where / drop-table / truncate / drop-column / unknown-write。</summary>
    public string Kind { get; init; } = "ok";
    public string Detail { get; init; } = "";
    public string Table { get; init; } = "";
    /// <summary>影响行数预估 SQL（danger 的 no-where 场景给出，供预览展示）。</summary>
    public string? EstimateSql { get; init; }
    /// <summary>截去字面量与注释后的骨架语句（预览/日志用）。</summary>
    public string Skeleton { get; init; } = "";
}

/// <summary>
/// 安全线引擎：危险 SQL 拦截的纯逻辑部分（第十章「危险 SQL 拦截：无 WHERE 预估影响行数」）。
/// 只做结构判断与预估语句生成，不连接数据库、不执行；弹窗确认与执行入口在界面层。
/// </summary>
public static partial class RiskySqlGuard
{
    /// <summary>逐条检查脚本中的语句（按分号切分，忽略空语句）。</summary>
    public static IReadOnlyList<RiskCheck> InspectScript(string script, DbEngine engine, Func<string, string> quote) =>
        SplitStatements(script).Select(s => Inspect(s, engine, quote)).ToList();

    /// <summary>脚本里最危险的一档（脚本级闸用）：全 ok 返回 ok。</summary>
    public static RiskCheck Worst(IReadOnlyList<RiskCheck> checks)
    {
        if (checks.Any(c => c.Level == "danger")) return checks.First(c => c.Level == "danger");
        if (checks.Any(c => c.Level == "warn")) return checks.First(c => c.Level == "warn");
        return checks.FirstOrDefault(new RiskCheck());
    }

    /// <summary>单条语句检查。</summary>
    public static RiskCheck Inspect(string sql, DbEngine engine, Func<string, string> quote)
    {
        var skeleton = StripLiteralsAndComments(sql).Trim();
        if (skeleton.Length == 0) return new RiskCheck { Skeleton = "" };
        var head = Regex.Match(skeleton, @"^\s*(\w+)", RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant();

        switch (head)
        {
            case "DROP":
                return new RiskCheck
                {
                    Level = "danger", Kind = "drop-table",
                    Table = ObjectName(skeleton, "TABLE"),
                    Detail = "DROP 会整表删除且结构不可回滚，必须预览+二次确认。",
                    Skeleton = skeleton,
                };
            case "TRUNCATE":
                return new RiskCheck
                {
                    Level = "danger", Kind = "truncate",
                    Table = ObjectName(skeleton, "TABLE"),
                    Detail = "TRUNCATE 清空全表且多数引擎不可回滚，必须预览+二次确认。",
                    Skeleton = skeleton,
                };
            case "ALTER":
                if (Regex.IsMatch(skeleton, @"\bDROP\s+COLUMN\b", RegexOptions.IgnoreCase))
                    return new RiskCheck
                    {
                        Level = "danger", Kind = "drop-column",
                        Table = ObjectName(skeleton, "TABLE"),
                        Detail = "删列会连带丢数据，必须预览+二次确认。",
                        Skeleton = skeleton,
                    };
                return new RiskCheck { Level = "warn", Kind = "ok", Detail = "改表结构建议在确认弹窗过目后执行。", Skeleton = skeleton };
            case "UPDATE" or "DELETE":
                bool hasWhere = Regex.IsMatch(skeleton, @"\bWHERE\b", RegexOptions.IgnoreCase);
                var table = ObjectName(skeleton, head);
                if (hasWhere)
                    return new RiskCheck
                    {
                        Level = "ok", Kind = "ok", Table = table, Skeleton = skeleton,
                        Detail = "带 WHERE 的改数语句，请在确认弹窗过目条件后执行。",
                    };
                return new RiskCheck
                {
                    Level = "danger", Kind = "no-where", Table = table, Skeleton = skeleton,
                    Detail = "无 WHERE 的改数语句会波及全表，请先看影响行数再决定。",
                    EstimateSql = $"SELECT COUNT(*) FROM {quote(table)}",
                };
            default:
                return new RiskCheck { Level = "ok", Kind = "ok", Detail = "", Skeleton = skeleton };
        }
    }

    /// <summary>带 WHERE 的改数语句生成影响行数预估 SQL（EXPLAIN 前的 COUNT 版本，跨引擎通用）。</summary>
    public static string? EstimateRowsSql(string sql, DbEngine engine, Func<string, string> quote)
    {
        var skeleton = StripLiteralsAndComments(sql);
        var head = Regex.Match(skeleton, @"^\s*(\w+)", RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant();
        if (head is not ("UPDATE" or "DELETE")) return null;
        var table = ObjectName(skeleton, head);
        var where = Regex.Match(skeleton, @"\bWHERE\b(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return where.Success
            ? $"SELECT COUNT(*) FROM {quote(table)} WHERE{where.Groups[1].Value}"
            : $"SELECT COUNT(*) FROM {quote(table)}";
    }

    /// <summary>去掉字符串/引号字面量与 -- 及块注释，只留结构骨架。</summary>
    internal static string StripLiteralsAndComments(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (c == '\'' || c == '"' || c == '`')
            {
                char q = c; i++;
                while (i < sql.Length)
                {
                    if (sql[i] == q)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == q) { i += 2; continue; } // 转义双写
                        break;
                    }
                    i++;
                }
                sb.Append(q == '`' ? ' ' : '?');   // 标识符保留占位，字面量变 ?
            }
            else if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
            }
            else if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i++;
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>骨架里取关键字后的目标表名（支持 schema.table 与引擎引用符已剥除的形态）。</summary>
    private static string ObjectName(string skeleton, string keyword)
    {
        var m = Regex.Match(skeleton, $@"\b{keyword}\s+(?:FROM\s+|INTO\s+)?(?:IF\s+EXISTS\s+)?([\w\.\[\]`""']+)",
            RegexOptions.IgnoreCase);
        if (!m.Success) return "";
        var name = m.Groups[1].Value.Trim('[', ']', '`', '"', '\'');
        if (name.EndsWith(".*", StringComparison.Ordinal)) name = name[..^2];
        var parts = name.Split('.');
        return parts[^1];
    }

    private static IReadOnlyList<string> SplitStatements(string script) =>
        script.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
}
