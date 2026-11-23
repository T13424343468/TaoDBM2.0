namespace TaoDBM.Core;

/// <summary>代码折叠内核（批次一，文本级口径）：选中多行折叠为一行摘要标记，可原样展开还原。</summary>
/// <remarks>RichEditBox 无原生折叠条，本批先做文本级折叠（折叠/展开不丢字符、可叠加多处）；
/// 编辑器原生折叠条随后续自研编辑控件批次升级。</remarks>
public sealed class SqlFolder
{
    private sealed record FoldEntry(string MarkerId, string Original);
    private readonly List<FoldEntry> _folds = new();

    /// <summary>折叠标记格式：-- ▸ 已折叠 N 行 [fold:id]（行内唯一 id，展开时按 id 找回原文）。</summary>
    public const string MarkerPrefix = "-- ▸ 已折叠 ";

    /// <summary>折叠选区（扩到整行，至少 2 行才折叠）。返回新文本与标记行选区。</summary>
    public (string Text, int SelStart, int SelLength) Fold(string text, int selStart, int selEnd)
    {
        text ??= "";
        if (selStart > selEnd) (selStart, selEnd) = (selEnd, selStart);
        selStart = Math.Clamp(selStart, 0, text.Length);
        selEnd = Math.Clamp(selEnd, 0, text.Length);
        if (selEnd > selStart) selStart = text.LastIndexOf('\n', Math.Max(0, selStart - 1)) + 1;
        if (selEnd < text.Length)
        {
            // 选区端点扩到行尾并含换行，保证整行折叠、还原后结构不变
            int le = text.IndexOf('\n', selEnd);
            selEnd = le < 0 ? text.Length : le + 1;
        }

        string block = text[selStart..selEnd];
        // 行数按内容行计：块尾换行不算一行
        int lineCount = block.Count(c => c == '\n') + (block.EndsWith("\n") ? 0 : 1);
        if (lineCount < 2) return (text, selStart, selEnd - selStart);   // 单行不折叠

        string id = Guid.NewGuid().ToString("N")[..8];
        string marker = $"{MarkerPrefix}{lineCount} 行 [fold:{id}]";
        _folds.Add(new FoldEntry(id, block));
        string newText = text[..selStart] + marker + text[selEnd..];
        return (newText, selStart, marker.Length);
    }

    /// <summary>展开光标所在折叠标记（按标记 id 找回原文）。不在标记上返回原文并给 false。</summary>
    public (string Text, int SelStart, int SelLength) Unfold(string text, int caret)
    {
        text ??= "";
        caret = Math.Clamp(caret, 0, text.Length);
        int lineStart = text.LastIndexOf('\n', Math.Max(0, caret - 1)) + 1;
        int lineEnd = text.IndexOf('\n', lineStart);
        lineEnd = lineEnd < 0 ? text.Length : lineEnd;
        string line = text[lineStart..lineEnd];

        int idAt = line.IndexOf("[fold:", StringComparison.Ordinal);
        if (idAt < 0) return (text, caret, 0);
        string id = line[(idAt + 6)..].Split(']')[0];
        var fold = _folds.FirstOrDefault(f => f.MarkerId == id);
        if (fold is null) return (text, caret, 0);

        string newText = text[..lineStart] + fold.Original + text[lineEnd..];
        _folds.RemoveAll(f => f.MarkerId == id);
        return (newText, lineStart, fold.Original.Length);
    }

    /// <summary>当前未展开的折叠数。</summary>
    public int PendingCount => _folds.Count;
}
