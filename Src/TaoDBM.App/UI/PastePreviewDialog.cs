using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>批次二格4：粘贴数据预览——解析剪贴板 TSV/INSERT，列名匹配映射（未匹配列置 NULL 并提示），
/// 预览前 20 行，确认后逐批 INSERT（跳错行/取消口径与 CSV 导入一致）。跨连接天然支持（剪贴板为载体）。</summary>
public static class PastePreviewDialog
{
    /// <summary>解析剪贴板文本：INSERT 语句行（复制为 INSERT 端）或 TSV（首行列名可选）。</summary>
    public static (List<string> Columns, List<string?[]> Rows, string Note) Parse(string clip)
    {
        var lines = clip.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return (new List<string>(), new List<string?[]>(), "剪贴板为空。");
        if (lines[0].StartsWith("INSERT INTO", StringComparison.OrdinalIgnoreCase))
        {
            // INSERT 端：逐条解析列名与 VALUES（自写轻量解析，值内逗号按引号保护）
            var cols = new List<string>();
            var rows = new List<string?[]>();
            foreach (var line in lines)
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"INSERT INTO\s+\S+\s*\(([^)]+)\)\s*VALUES\s*\((.*)\)\s*;?\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                if (cols.Count == 0)
                    cols = m.Groups[1].Value.Split(',').Select(c => c.Trim().Trim('"', '`', '[', ']', '\'')).ToList();
                rows.Add(SplitValues(m.Groups[2].Value, cols.Count));
            }
            return (cols, rows, $"识别 INSERT 语句 {rows.Count} 条。");
        }
        // TSV 端：首行若与常见列名形态一致（非纯数字）按列名行处理
        var head = lines[0].Split('\t');
        bool hasHeader = head.Any(h => !double.TryParse(h, out _));
        var tsvCols = hasHeader ? head.ToList() : new List<string>();
        var dataLines = hasHeader ? lines.Skip(1) : lines;
        int width = lines.Max(l => l.Split('\t').Length);
        var tsvRows = dataLines.Select(l =>
        {
            var cells = l.Split('\t');
            var row = new string?[width];
            for (int i = 0; i < width; i++)
                row[i] = i < cells.Length && cells[i].Length > 0 ? cells[i] : null;   // 空串按 NULL（与导入口径一致）
            return row;
        }).ToList();
        return (tsvCols, tsvRows, hasHeader ? $"识别 TSV，首行列名 {tsvCols.Count} 列。" : $"识别 TSV（无列名行），{width} 列。");
    }

    /// <summary>VALUES 拆值（引号内逗号保护；NULL 字面量置 null）。</summary>
    static string?[] SplitValues(string s, int width)
    {
        var vals = new string?[width];
        var cur = new System.Text.StringBuilder();
        bool inStr = false;
        int idx = 0;
        for (int i = 0; i < s.Length && idx < width; i++)
        {
            char c = s[i];
            if (c == '\'' ) { inStr = !inStr; if (!(inStr && i + 1 < s.Length && s[i + 1] == '\'')) cur.Append(c); else { cur.Append(c); i++; } }
            else if (c == ',' && !inStr) { vals[idx++] = NullOrText(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        if (cur.Length > 0 && idx < width) vals[idx] = NullOrText(cur.ToString());
        return vals;
    }

    static string? NullOrText(string t) => t.Trim().Equals("NULL", StringComparison.OrdinalIgnoreCase) ? null : t.Trim();

    /// <summary>列名匹配映射：目标表列 ← 来源列（同名优先，大小写不敏感）；未匹配置 NULL。</summary>
    public static (List<string> TargetCols, List<int> SourceIdx) MapColumns(IReadOnlyList<ColumnSpec> targetCols, List<string> srcCols)
    {
        var tCols = new List<string>();
        var sIdx = new List<int>();
        foreach (var t in targetCols)
        {
            int i = srcCols.FindIndex(s => s.Equals(t.Name, StringComparison.OrdinalIgnoreCase));
            tCols.Add(t.Name);
            sIdx.Add(i);
        }
        return (tCols, sIdx);
    }

    public static async Task Show(MainWindow owner, WorkTab tab, IDbProvider provider, string db, string table)
    {
        string clip = "";
        try
        {
            var dp = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (dp.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                clip = await dp.GetTextAsync();
        }
        catch { }
        if (clip.Length == 0) { owner.SetStatus("剪贴板没有可粘贴的数据。"); return; }

        var (srcCols, srcRows, note) = Parse(clip);
        if (srcRows.Count == 0) { owner.SetStatus("剪贴板内容无法识别为 INSERT 或 TSV 数据。"); return; }

        var cols = await provider.GetColumns(db, table);
        var (tCols, sIdx) = MapColumns(cols, srcCols);
        int unmatched = sIdx.Count(i => i < 0);

        var preview = new System.Text.StringBuilder();
        preview.AppendLine(string.Join(" | ", tCols));
        foreach (var r in srcRows.Take(20))
        {
            var cells = tCols.Zip(sIdx, (t, i) => i >= 0 && i < r.Length ? (r[i] ?? "NULL") : "NULL");
            preview.AppendLine(string.Join(" | ", cells));
        }

        // 原型207：统计徽标行——将插入行数 / 已映射列 / 未映射列
        var statLine = new TextBlock
        {
            Text = $"将插入 {srcRows.Count:N0} 行 · 已映射 {tCols.Count - unmatched} 列 · 未映射 {unmatched} 列（存 NULL）",
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.Text,
        };
        var dlg = new ContentDialog
        {
            Title = $"粘贴数据到 {table}",
            Content = new StackPanel { Spacing = 8,
                Children = {
                    statLine,
                    new TextBlock { Text = $"{note} 预览前 {Math.Min(20, srcRows.Count)} 行：", TextWrapping = TextWrapping.Wrap, FontSize = AntTokens.FontMinor },
                    new ScrollViewer { Content = new TextBlock { Text = preview.ToString(), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = AntTokens.FontMinor }, MaxHeight = 300 },
                } },
            PrimaryButtonText = $"插入 {srcRows.Count} 行",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = owner.Content.XamlRoot,
        };
        if (await dlg.ShowThemedAsync() != ContentDialogResult.Primary) return;

        // 逐批 INSERT（跳错行口径与 CSV 导入一致：失败记数继续，结束汇报）
        int ok = 0, skip = 0;
        var qt = provider.Quote(table);
        for (int ri = 0; ri < srcRows.Count; ri++)
        {
            var vals = new List<string>();
            var names = new List<string>();
            for (int c = 0; c < tCols.Count; c++)
            {
                int si = sIdx[c];
                var col = cols[c];
                if (si < 0 || si >= srcRows[ri].Length || srcRows[ri][si] is null) { if (!col.Nullable && !col.IsKey) { skip++; goto next; } continue; }
                names.Add(provider.Quote(col.Name));
                vals.Add(ResultGridOps.SqlLiteral(srcRows[ri][si]!.ToString()));
            }
            next:
            if (names.Count == 0) { skip++; continue; }
            try
            {
                await provider.Execute(db, $"INSERT INTO {qt} ({string.Join(", ", names)}) VALUES ({string.Join(", ", vals)})");
                ok++;
            }
            catch { skip++; }
        }
        owner.SetStatus($"粘贴完成：成功 {ok} 行，跳过 {skip} 行。");
        AppLog.Write($"[粘贴数据] 行={ok} 跳过={skip}");
        await tab.RefreshAsync();
    }
}
