namespace TaoDBM.Core;

/// <summary>SQL 美化内核（批次一）：按子句换行＋缩进，字符串与注释原样保留。</summary>
public static class SqlBeautifier
{
    /// <summary>在这些关键字前换行（大写比较，原文大小写不敏感）。</summary>
    private static readonly HashSet<string> BreakBefore = new(new[]
    {
        "FROM", "WHERE", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "JOIN", "ON",
        "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "UNION", "EXCEPT", "INTERSECT",
        "VALUES", "SET", "AND", "OR",
    }, StringComparer.OrdinalIgnoreCase);

    /// <summary>把 SQL 重排为多行：子句各占一行（顶格）、AND/OR 随行、括号内加一层缩进。空输入原样返回。</summary>
    public static string Beautify(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return sql ?? "";
        var tokens = SqlHighlighter.Tokenize(sql);
        if (tokens.Count == 0) return sql;

        var sb = new System.Text.StringBuilder();
        int parenDepth = 0;      // 未闭合括号层数
        bool atLineStart = true;
        bool pendingSpace = false;   // 跨 token 的待补空格（词法器把空白与标识符切成相邻 token）

        foreach (var t in tokens)
        {
            string word = sql.Substring(t.Start, t.Length);
            if (t.Kind == TokenKind.Plain)
            {
                // 空白与标点：空白压成单空格，括号记深度，标识符原样保留
                foreach (char c in word)
                {
                    if (char.IsWhiteSpace(c)) { if (!atLineStart) pendingSpace = true; continue; }
                    if (pendingSpace && !atLineStart) sb.Append(' ');
                    pendingSpace = false;
                    if (c == '(') { parenDepth++; sb.Append('('); }
                    else if (c == ')') { parenDepth = Math.Max(0, parenDepth - 1); sb.Append(')'); }
                    else sb.Append(c);
                    atLineStart = false;
                }
                continue;
            }

            // 关键字/词元前补空格：仅当上一段不是刚换行或行首
            bool isBreak = t.Kind == TokenKind.Keyword && BreakBefore.Contains(word.TrimEnd('(')) && !atLineStart;
            if (isBreak)
            {
                sb.Append('\n');
                sb.Append(new string(' ', 4 * parenDepth));
            }
            else if (!atLineStart && pendingSpace)
            {
                sb.Append(' ');
            }
            pendingSpace = false;

            sb.Append(word);
            atLineStart = false;
        }

        return sb.ToString().Trim();
    }
}
