namespace TaoDBM.Core;

/// <summary>括号匹配内核（批次一）：光标落在括号旁时找配对位置，跳过字符串与注释。</summary>
public static class SqlBracketMatcher
{
    private const string Opens = "([{";
    private const string Closes = ")]}";

    /// <summary>光标在括号旁（caret 处或前一字符）时返回配对两端的绝对位置；无配对返回 null。</summary>
    public static (int Open, int Close)? Match(string text, int caret)
    {
        text ??= "";
        if (text.Length == 0) return null;
        caret = Math.Clamp(caret, 0, text.Length);

        if (TryAnchor(text, caret, out var hit) || TryAnchor(text, caret - 1, out hit)) return hit;
        return null;
    }

    private static bool TryAnchor(string text, int pos, out (int Open, int Close) hit)
    {
        hit = default;
        if (pos < 0 || pos >= text.Length) return false;
        char c = text[pos];
        int openIdx = Opens.IndexOf(c);
        if (openIdx >= 0) return MatchForward(text, pos, Opens[openIdx], Closes[openIdx], out hit);
        int closeIdx = Closes.IndexOf(c);
        if (closeIdx >= 0) return MatchBackward(text, pos, Opens[closeIdx], Closes[closeIdx], out hit);
        return false;
    }

    private static bool MatchForward(string text, int pos, char open, char close, out (int Open, int Close) hit)
    {
        hit = default;
        char quote = '\0';
        bool inLine = false, inBlock = false;
        int depth = 0;
        for (int i = pos; i < text.Length; i++)
        {
            char c = text[i];
            if (inLine) { if (c == '\n') inLine = false; continue; }
            if (inBlock) { if (c == '*' && i + 1 < text.Length && text[i + 1] == '/') { inBlock = false; i++; } continue; }
            if (quote != '\0') { if (c == quote) { if (i + 1 < text.Length && text[i + 1] == quote) i++; else quote = '\0'; } continue; }
            if (c is '\'' or '"' or '`') { quote = c; continue; }
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-') { inLine = true; i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { inBlock = true; i++; continue; }
            if (c == open) depth++;
            else if (c == close) { depth--; if (depth == 0) { hit = (pos, i); return true; } }
        }
        return false;
    }

    private static bool MatchBackward(string text, int pos, char open, char close, out (int Open, int Close) hit)
    {
        hit = default;
        // 正向扫到 pos 之前建括号栈（带字符串/注释状态）；pos 处即待配对的闭括号
        var stack = new List<int>();
        char quote = '\0';
        bool inLine = false, inBlock = false;
        for (int i = 0; i < pos; i++)
        {
            char c = text[i];
            if (inLine) { if (c == '\n') inLine = false; continue; }
            if (inBlock) { if (c == '*' && i + 1 < text.Length && text[i + 1] == '/') { inBlock = false; i++; } continue; }
            if (quote != '\0') { if (c == quote) { if (i + 1 < text.Length && text[i + 1] == quote) i++; else quote = '\0'; } continue; }
            if (c is '\'' or '"' or '`') { quote = c; continue; }
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-') { inLine = true; i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { inBlock = true; i++; continue; }
            int openIdx = Opens.IndexOf(c);
            if (openIdx >= 0) { stack.Add(i); continue; }
            if (Closes.IndexOf(c) >= 0 && stack.Count > 0) stack.RemoveAt(stack.Count - 1);
        }
        if (stack.Count == 0) return false;
        int openPos = stack[^1];
        if (text[openPos] != open) return false;
        hit = (openPos, pos);
        return true;
    }
}
