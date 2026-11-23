namespace TaoDBM.Core;

/// <summary>执行选中/当前语句定位内核（批次一）：有选区执行选区，无选区按光标所在语句执行。</summary>
public static class SqlStatementLocator
{
    /// <summary>定位要执行的语句范围：选区非空用选区；否则按分号切段（跳过字符串/注释）取光标所在段，返回去空白后的起止。</summary>
    public static (int Start, int Length) Locate(string text, int caret, int selStart, int selEnd)
    {
        text ??= "";
        if (selEnd > selStart) return (Math.Clamp(selStart, 0, text.Length), selEnd - selStart);
        caret = Math.Clamp(caret, 0, text.Length);

        int segStart = 0, segEnd = text.Length;
        char quote = '\0';
        int i = 0, stmtStart = 0;
        bool inLineComment = false, inBlockComment = false;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (inLineComment) { if (c == '\n') inLineComment = false; continue; }
            if (inBlockComment)
            {
                if (c == '*' && i + 1 < text.Length && text[i + 1] == '/') { inBlockComment = false; i++; }
                continue;
            }
            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == quote) i++;
                    else quote = '\0';
                }
                continue;
            }
            if (c is '\'' or '"' or '`') { quote = c; continue; }
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-') { inLineComment = true; i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { inBlockComment = true; i++; continue; }
            if (c == ';')
            {
                if (i >= caret) { segEnd = i; break; }
                stmtStart = i + 1;
            }
        }
        segStart = stmtStart;
        if (segEnd == text.Length) segEnd = text.Length;

        // 去掉两端空白
        while (segStart < segEnd && char.IsWhiteSpace(text[segStart])) segStart++;
        while (segEnd > segStart && char.IsWhiteSpace(text[segEnd - 1])) segEnd--;
        return (segStart, segEnd - segStart);
    }
}
