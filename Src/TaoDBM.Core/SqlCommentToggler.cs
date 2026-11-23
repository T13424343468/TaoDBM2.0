namespace TaoDBM.Core;

/// <summary>SQL 注释切换内核（批次一）：选中行整体加/去 <c>--</c> 行注释，与 Navicat Ctrl+/ 同口径。</summary>
public static class SqlCommentToggler
{
    /// <summary>把选区扩到整行后切换注释：全部已是注释则去掉，否则整批加上。空行不加注释。</summary>
    /// <returns>新文本、调整后的选区起点与长度。</returns>
    public static (string Text, int SelStart, int SelLength) Toggle(string text, int selStart, int selEnd)
    {
        text ??= "";
        if (selStart > selEnd) (selStart, selEnd) = (selEnd, selStart);
        selStart = Math.Clamp(selStart, 0, text.Length);
        selEnd = Math.Clamp(selEnd, 0, text.Length);

        // 无选区时取光标所在整行
        if (selEnd == selStart)
        {
            selStart = text.LastIndexOf('\n', Math.Max(0, selStart - 1)) + 1;
            selEnd = text.IndexOf('\n', selStart);
            if (selEnd < 0) selEnd = text.Length;
        }
        else
        {
            selStart = text.LastIndexOf('\n', Math.Max(0, selStart - 1)) + 1;
            selEnd = text.IndexOf('\n', Math.Max(0, selEnd - 1));
            selEnd = selEnd < 0 ? text.Length : selEnd;
        }

        string block = text[selStart..selEnd];
        var lines = block.Split('\n');
        bool allCommented = lines.Where(l => l.Trim().Length > 0)
                                 .All(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal));
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            lines[i] = allCommented
                ? RemoveComment(lines[i])
                : lines[i].Insert(FirstNonWs(lines[i]), "-- ");
        }
        string newBlock = string.Join("\n", lines);
        string newText = text[..selStart] + newBlock + text[selEnd..];
        return (newText, selStart, newBlock.Length);
    }

    private static int FirstNonWs(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    /// <summary>去注释：优先摘「-- 」整段（含空格），退而摘裸「--」。</summary>
    private static string RemoveComment(string line)
    {
        int p = line.IndexOf("-- ", StringComparison.Ordinal);
        if (p >= 0) return line.Remove(p, 3);
        p = line.IndexOf("--", StringComparison.Ordinal);
        return p >= 0 ? line.Remove(p, 2) : line;
    }
}
