using System.Text.RegularExpressions;

namespace TaoDBM.Core;

/// <summary>㉔ 智能提示纯逻辑（2026-11-25 设计稿冻结）：别名感知、候选过滤与引用判定。
/// 输入即弹＋Ctrl+Space 兜底由 WorkTab 承接；本类只做文本判定，LogicTest 直测。</summary>
public static class SqlComplete
{
    /// <summary>候选上限（设计稿口径：候选 ≤8 条）。</summary>
    public const int CandidateLimit = 8;

    /// <summary>元数据缓存秒数（设计稿口径：表/字段名会话缓存 60 秒）。</summary>
    public const int MetaCacheSeconds = 60;

    // FROM/JOIN 表名（可带库前缀；引号表名可含空格）后跟可选 AS 别名或裸别名；
    // 别名负向排除常见子句关键字，防「FROM a JOIN users」把 JOIN 吃成 a 的别名
    static readonly Regex AliasRegex = new(
        @"(?:\bFROM\b|\bJOIN\b)\s+(?:[\w$]+\.){0,2}(?:[`""\[](?<qtable>[^`""\[]+)[`""\]]|(?<table>[\w$]+))(?:\s+(?:AS\s+)?(?<alias>(?!(?:WHERE|ON|GROUP|ORDER|SET|VALUES|LEFT|RIGHT|INNER|OUTER|CROSS|UNION|HAVING|LIMIT|OFFSET|AS|JOIN|FROM|AND|OR|NOT|IS|IN|LIKE|BETWEEN|SELECT|WHEN|THEN|ELSE|END|RETURNING|USING|NATURAL|FULL)\b)[\w$]+))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>光标前文本以「x.」结尾时，解析限定词 x 对应的表名（别名或表名本名）；无归属返回 null。</summary>
    public static string? QualifierTable(string? text, int caret)
    {
        if (string.IsNullOrEmpty(text)) return null;
        caret = Math.Clamp(caret, 0, text.Length);
        if (caret < 2 || text[caret - 1] != '.') return null;
        int end = caret - 1, start = end;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_' || text[start - 1] == '$')) start--;
        if (start == end) return null;
        string qual = text[start..end];
        // 只扫光标之前，取最后一个能对上的定义（子查询就近原则）
        foreach (Match m in AliasRegex.Matches(text[..start]))
        {
            string table = m.Groups["qtable"].Success ? m.Groups["qtable"].Value : m.Groups["table"].Value;
            string alias = m.Groups["alias"].Success ? m.Groups["alias"].Value : "";
            if (alias.Length > 0)
            {
                if (string.Equals(alias, qual, StringComparison.OrdinalIgnoreCase)) return table;
                continue;   // 已有别名且对不上：该定义不归属
            }
            if (string.Equals(table, qual, StringComparison.OrdinalIgnoreCase)) return table;
        }
        return null;
    }

    /// <summary>词根过滤候选：空词根不出候选（避免全量噪音）；前缀匹配、排除全等、去重、上限 CandidateLimit。</summary>
    public static List<string> Filter(IEnumerable<string> pool, string word, int limit = CandidateLimit)
    {
        if (string.IsNullOrEmpty(word)) return new List<string>();
        return pool
            .Where(k => !string.IsNullOrEmpty(k) && k.StartsWith(word, StringComparison.OrdinalIgnoreCase)
                        && !k.Equals(word, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(limit, 1))
            .ToList();
    }

    /// <summary>标识符是否需要引用：含字母数字下划线 $ 之外的字符，或以数字开头。</summary>
    public static bool NeedsQuote(string? ident)
    {
        if (string.IsNullOrEmpty(ident)) return false;
        if (char.IsAsciiDigit(ident[0])) return true;
        return ident.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$'));
    }
}
