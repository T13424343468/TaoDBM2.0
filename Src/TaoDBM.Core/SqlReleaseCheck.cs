using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TaoDBM.Core;

/// <summary>一条上线检查发现项。</summary>
public sealed record LintFinding
{
    /// <summary>block=建议拦截 / warn=建议确认 / info=优化建议。</summary>
    public string Level { get; init; } = "info";
    /// <summary>规则标识。</summary>
    public string Rule { get; init; } = "";
    public string Message { get; init; } = "";
    /// <summary>命中的语句（截去字面量后的骨架，前 120 字符）。</summary>
    public string Snippet { get; init; } = "";
}

/// <summary>
/// 安全线引擎：SQL 上线检查评分的纯逻辑部分（第十章「SQL 上线检查评分」）。
/// 对脚本做静态体检并给 0-100 分：危险语句复用 RiskySqlGuard，其余规则自查骨架。
/// 只做静态分析，不连库、不执行；结果供上线确认页与报表展示。
/// </summary>
public static class SqlReleaseCheck
{
    /// <summary>体检脚本：逐条发现项 + 总分（100 起，block -25 / warn -10 / info -3，下限 0）。</summary>
    public static (int Score, IReadOnlyList<LintFinding> Findings) Review(string script, DbEngine engine)
    {
        var findings = new List<LintFinding>();
        var statements = Regex.Split(script, ";")
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        foreach (var raw in statements)
        {
            var st = RiskySqlGuard.Inspect(raw, engine, s => s);

            // 1) 危险语句：复用危险 SQL 拦截引擎的分级
            if (st.Level == "danger")
            {
                findings.Add(new LintFinding
                {
                    Level = "block",
                    Rule = "dangerous-statement",
                    Message = st.Kind switch
                    {
                        "no-where" => "无 WHERE 的改数语句会波及全表，上线前须补充条件或改用分批清理。",
                        "drop-table" => "DROP 属不可逆整表删除，禁止随上线脚本直接执行。",
                        "truncate" => "TRUNCATE 清空全表且多数引擎不可回滚，禁止随上线脚本直接执行。",
                        _ => "删列会连带丢数据，禁止随上线脚本直接执行。",
                    },
                    Snippet = Snip(st.Skeleton),
                });
                continue;
            }

            var skeleton = st.Skeleton;
            var head = Regex.Match(skeleton, @"^\s*(\w+)", RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant();

            // 2) SELECT * 全列拉取（查骨架结构）
            if (head == "SELECT" && Regex.IsMatch(skeleton, @"\bSELECT\s+\*", RegexOptions.IgnoreCase))
                findings.Add(new LintFinding
                {
                    Level = "warn", Rule = "select-star",
                    Message = "SELECT * 全列拉取，建议改为显式列清单，避免列变更破坏下游。",
                    Snippet = Snip(skeleton),
                });

            // 3) 前导通配 LIKE（通配符在字面量里，须查原始语句）
            if (Regex.IsMatch(raw, @"\bLIKE\s+'%", RegexOptions.IgnoreCase))
                findings.Add(new LintFinding
                {
                    Level = "warn", Rule = "leading-wildcard",
                    Message = "LIKE 前导 % 无法走索引，大表上可能全表扫描。",
                    Snippet = Snip(skeleton),
                });

            // 4) 改数语句无 LIMIT（建议显式限批）
            if ((head == "UPDATE" || head == "DELETE") && !Regex.IsMatch(skeleton, @"\bLIMIT\s+\d+", RegexOptions.IgnoreCase))
                findings.Add(new LintFinding
                {
                    Level = "info", Rule = "no-limit",
                    Message = "改数语句未带 LIMIT，建议显式限批以便控制影响范围。",
                    Snippet = Snip(skeleton),
                });

            // 5) 多行更新未包事务
            if ((head == "UPDATE" || head == "DELETE") && !Regex.IsMatch(script, @"\b(BEGIN|START\s+TRANSACTION)\b", RegexOptions.IgnoreCase))
                findings.Add(new LintFinding
                {
                    Level = "info", Rule = "no-transaction",
                    Message = "改数脚本未包事务，失败后无法整体回退。",
                    Snippet = Snip(skeleton),
                });
        }

        int score = 100;
        foreach (var f in findings)
            score -= f.Level switch { "block" => 25, "warn" => 10, _ => 3 };
        return (Math.Max(score, 0), findings);
    }

    /// <summary>分数档位文案：>=90 放行 / >=70 谨慎 / <70 建议整改后重新体检。</summary>
    public static string Verdict(int score) =>
        score >= 90 ? "可上线" : score >= 70 ? "谨慎上线" : "建议整改";

    private static string Snip(string skeleton) =>
        skeleton.Length <= 120 ? skeleton : skeleton[..120] + "…";
}
