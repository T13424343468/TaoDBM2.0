namespace TaoDBM.Core;

/// <summary>结果网格纯逻辑（整改二批 #03，2026-11-23）：TSV 复制与内存排序。
/// 普通网格（GridBuilder）与虚拟网格（VirtualizedResultGrid）共用同一套口径，避免分叉；
/// 排序纯内存不回库、不改 SQL；LogicTest 覆盖 TSV 转义、排序比较器与空值位次。</summary>
public static class ResultGridOps
{
    /// <summary>单次复制行数上限：超过提示改走导出，防大结果集撑爆剪贴板。</summary>
    public const int CopyRowLimit = 10000;

    /// <summary>行集转 TSV（Excel 可直贴）：制表符分隔；值内换行折叠为空格、制表符转四空格。</summary>
    public static string ToTsv(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows,
        IEnumerable<int> rowIndices, bool includeHeader = true)
    {
        var sb = new System.Text.StringBuilder();
        if (includeHeader && columns.Count > 0)
            sb.AppendLine(string.Join("\t", columns.Select(EscapeCell)));
        int n = 0;
        foreach (var i in rowIndices)
        {
            if (i < 0 || i >= rows.Count) continue;
            var row = rows[i];
            var cells = new string[columns.Count];
            for (int c = 0; c < columns.Count; c++)
                cells[c] = EscapeCell(CellText(c < row.Length ? row[c] : null));
            sb.AppendLine(string.Join("\t", cells));
            n++;
            if (n >= CopyRowLimit) break;
        }
        return sb.ToString();
    }

    static string EscapeCell(string s) => s.Replace("\r", " ").Replace("\n", " ").Replace("\t", "    ");

    /// <summary>单元格文本化口径（与界面 Exporter.Format 一致的展示语义）。</summary>
    public static string CellText(object? v) => v switch
    {
        null or DBNull => "",
        byte[] b => "0x" + Convert.ToHexString(b),
        bool x => x ? "TRUE" : "FALSE",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
        _ => v.ToString() ?? "",
    };

    /// <summary>排序模式：0 原序 / 1 升序 / 2 降序（表头点击循环）。</summary>
    public const int SortOriginal = 0, SortAsc = 1, SortDesc = 2;

    /// <summary>内存排序：返回「显示位次 → 原行号」映射。
    /// 全列可解析为数值时按数值比较，否则按字符串序（不区分大小写）；空值恒排最后。</summary>
    public static int[] SortOrder(IReadOnlyList<object?[]> rows, int colIdx, int mode)
    {
        var idx = Enumerable.Range(0, rows.Count).ToArray();
        if (mode == SortOriginal || colIdx < 0 || rows.Count == 0) return idx;

        bool anyNum = false, allNum = true;
        foreach (var row in rows)
        {
            var cell = CellAt(row, colIdx);
            if (IsEmptyCell(cell)) continue;
            if (TryNum(cell, out _)) anyNum = true;
            else allNum = false;
        }
        bool numeric = anyNum && allNum;

        IEnumerable<int> ordered = numeric
            ? idx.OrderBy(i => NumKey(rows[i], colIdx))
            : idx.OrderBy(i => TextKey(rows[i], colIdx), StringComparer.OrdinalIgnoreCase);
        if (mode == SortDesc) ordered = ordered.Reverse();
        return ordered.ToArray();
    }

    static object? CellAt(object?[] row, int colIdx) => colIdx < row.Length ? row[colIdx] : null;

    static bool IsEmptyCell(object? v) => v is null || v is DBNull || (v is string s && s.Length == 0);

    static bool TryNum(object? v, out double d)
    {
        d = 0;
        return v is not DBNull && double.TryParse(v?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out d);
    }

    /// <summary>数值排序键：空值 +∞ 恒排最后（升序）。</summary>
    static double NumKey(object?[] row, int colIdx) =>
        TryNum(CellAt(row, colIdx), out var d) ? d : double.PositiveInfinity;

    /// <summary>字符串排序键：空值排最后（\uFFFF 上界）。</summary>
    static string TextKey(object?[] row, int colIdx)
    {
        var v = CellAt(row, colIdx);
        return IsEmptyCell(v) ? "\uFFFF" : (v?.ToString() ?? "");
    }
}
