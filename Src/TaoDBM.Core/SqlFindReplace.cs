using System.Text.RegularExpressions;

namespace TaoDBM.Core;

/// <summary>查找选项：正则、全字匹配、区分大小写（批次一）。</summary>
public sealed class SqlFindOptions
{
    public bool Regex { get; init; }
    public bool WholeWord { get; init; }
    public bool MatchCase { get; init; }
}

/// <summary>查找替换内核（批次一）：正则＋全字＋区分大小写，替换预览与全部替换。</summary>
public static class SqlFindReplace
{
    /// <summary>找出全部匹配；非法正则抛 ArgumentException（界面捕获后提示）。</summary>
    public static List<(int Start, int Length)> FindAll(string text, string query, SqlFindOptions opt)
    {
        var hits = new List<(int, int)>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return hits;
        var rx = BuildRegex(query, opt);
        foreach (Match m in rx.Matches(text))
        {
            if (m.Length > 0) hits.Add((m.Index, m.Length));
            if (hits.Count >= 10_000) break;   // 防御：超大文档匹配上限
        }
        return hits;
    }

    /// <summary>全部替换：非正则按字面替换；正则支持 $1 分组引用。返回新文本与替换次数。</summary>
    public static (string Text, int Count) ReplaceAll(string text, string query, string replacement, SqlFindOptions opt)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return (text ?? "", 0);
        var rx = BuildRegex(query, opt);
        int count = rx.Matches(text).Count;
        if (count == 0) return (text, 0);
        string replaced = opt.Regex
            ? rx.Replace(text, replacement ?? "")
            : rx.Replace(text, MatchEvaluatorLiteral(replacement ?? ""));
        return (replaced, count);
    }

    /// <summary>替换预览：给出前 N 个匹配及其上下文行（用于替换前确认）。</summary>
    public static List<string> Preview(string text, string query, SqlFindOptions opt, int take = 5)
    {
        var lines = new List<string>();
        foreach (var (start, len) in FindAll(text, query, opt))
        {
            int ls = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
            int le = text.IndexOf('\n', start + len);
            le = le < 0 ? text.Length : le;
            lines.Add(text[ls..le].Trim());
            if (lines.Count >= take) break;
        }
        return lines;
    }

    private static Regex BuildRegex(string query, SqlFindOptions opt)
    {
        string pattern = opt.Regex
            ? query
            : "(" + Regex.Escape(query) + ")";
        if (opt.WholeWord && !opt.Regex) pattern = @"\b" + pattern + @"\b";
        var o = RegexOptions.CultureInvariant;
        if (!opt.Regex) o |= RegexOptions.ExplicitCapture;   // 字面模式不产生分组；正则模式保留 $1 引用
        if (!opt.MatchCase) o |= RegexOptions.IgnoreCase;
        try { return new Regex(pattern, o); }
        catch (ArgumentException ex) { throw new ArgumentException("正则表达式无效：" + ex.Message); }
    }

    private static MatchEvaluator MatchEvaluatorLiteral(string replacement) =>
        m => replacement;
}
