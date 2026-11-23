using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>结果网格右键菜单（批次E，2026-11-20 老板令「全覆盖」）：列头/单元格右键 →
/// 「分析本列」（计数/非空/去重/TopN，数值列附 min/max/avg，CellAnalysis 纯逻辑 LogicTest 已覆盖）→
/// 「钉住到看板」（KanbanPinStore 持久化，可设定时刷新间隔，到期重跑由任务中心/流式底座承接）。
/// 整改二批 #03（2026-11-23）：数据单元格追加「复制该行 / 复制该单元格 / 复制全部」，
/// TSV 格式（ResultGridOps，Excel 可直贴），单次上限 10000 行。
/// 本类只挂菜单与弹层，不碰主窗口逻辑；sourceSql 为空（浏览页等无归属语句）时只出分析与复制。</summary>
public static class ResultGridMenu
{
    /// <summary>给表头或行单元格挂右键菜单；host 须已入可视树（XamlRoot 就绪）。
    /// rowIdx 非空＝数据单元格（追加复制项）；空＝表头（只分析/钉住）。</summary>
    public static void Attach(FrameworkElement host, QueryResult r, int colIdx, string? sourceSql, string? engine, int? rowIdx = null)
    {
        if (colIdx < 0 || colIdx >= r.Columns.Count) return;
        var menu = new MenuFlyout();
        if (rowIdx is int ri)
        {
            menu.Items.Add(MenuItem("复制该行", () => CopyRows(r, new[] { ri })));
            menu.Items.Add(MenuItem("复制该单元格", () => CopyCell(r, ri, colIdx)));
            menu.Items.Add(MenuItem($"复制全部（前 {ResultGridOps.CopyRowLimit:N0} 行）", () => CopyRows(r, Enumerable.Range(0, r.Rows.Count))));
        }
        menu.Items.Add(MenuItem("分析本列", () => _ = ShowAnalysisAsync(host, r, colIdx)));
        if (!string.IsNullOrWhiteSpace(sourceSql))
            menu.Items.Add(MenuItem("钉住到看板", () => _ = ShowPinAsync(host, r, colIdx, sourceSql, engine)));
        menu.PinTheme();
        host.ContextFlyout = menu;
    }

    static void CopyCell(QueryResult r, int rowIdx, int colIdx)
    {
        var row = r.Rows[rowIdx];
        SetClipboard(Exporter.DisplayFormat(colIdx < row.Length ? row[colIdx] : null));   // 格8：复制单元格与网格显示口径一致
    }

    static void CopyRows(QueryResult r, IEnumerable<int> rowIndices)
    {
        int n = rowIndices.Count();
        if (n > ResultGridOps.CopyRowLimit) rowIndices = rowIndices.Take(ResultGridOps.CopyRowLimit);
        SetClipboard(ResultGridOps.ToTsv(r.Columns, r.Rows, rowIndices));
        AppLog.Write($"[网格复制] rows={Math.Min(n, ResultGridOps.CopyRowLimit)}");
    }

    static void SetClipboard(string text)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage { RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy };
        dp.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    static MenuFlyoutItem MenuItem(string text, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => { try { onClick(); } catch (Exception ex) { AppLog.Write($"[右键分析] error={ex.Message}"); } };
        return item;
    }

    static async Task ShowAnalysisAsync(FrameworkElement host, QueryResult r, int colIdx)
    {
        if (host.XamlRoot is null) return;
        var s = CellAnalysis.Analyze(r, r.Columns[colIdx]);
        var sp = new StackPanel { Spacing = 8, MinWidth = 320 };
        sp.Children.Add(new TextBlock { Text = s.Headline, TextWrapping = TextWrapping.Wrap, Foreground = Ant.Text });
        if (s.Top.Count > 0)
        {
            sp.Children.Add(new TextBlock { Text = "TopN（计数降序）", Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor });
            foreach (var (val, cnt) in s.Top)
                sp.Children.Add(new TextBlock
                {
                    Text = $"{val} × {cnt}",
                    Foreground = Ant.Text,
                    Margin = new Thickness(12, 0, 0, 0),
                });
        }
        var dlg = new ContentDialog
        {
            Title = $"「{r.Columns[colIdx]}」列概览",
            Content = new ScrollViewer { Content = sp, MaxHeight = 420 },
            CloseButtonText = "关闭",
            XamlRoot = host.XamlRoot,
        };
        await dlg.ShowThemedAsync();
    }

    static async Task ShowPinAsync(FrameworkElement host, QueryResult r, int colIdx, string sourceSql, string? engine)
    {
        if (host.XamlRoot is null) return;
        var column = r.Columns[colIdx];
        var combo = new ComboBox
        {
            Header = "定时刷新",
            SelectedIndex = 0,
            Items =
            {
                "仅钉住（不自动刷新）",
                "每 5 秒", "每 30 秒", "每 1 分钟", "每 5 分钟",
            },
        };
        var dlg = new ContentDialog
        {
            Title = $"钉住「{column}」到看板",
            Content = combo,
            PrimaryButtonText = "钉住",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = host.XamlRoot,
        };
        if (await dlg.ShowThemedAsync() != ContentDialogResult.Primary) return;
        int interval = combo.SelectedIndex switch
        {
            1 => 5, 2 => 30, 3 => 60, 4 => 300,
            _ => 0,
        };
        var pinId = PinIdOf(sourceSql, column);
        KanbanPinStore.Upsert(new KanbanPinStore.Pin(
            pinId, $"{column} · {Summarize(sourceSql)}", sourceSql, engine ?? "SQLite",
            CellAnalysis.ToChart(r, column).ChartId, interval, DateTimeOffset.UtcNow));
        AppLog.Write($"[看板] 钉住 pin={pinId} col={column}");   // 走查 S26 实效断言锚点
    }

    /// <summary>同 SQL 同列 → 同一 PinId：重复钉住即更新（改间隔/换标题），不堆重复卡片。</summary>
    internal static string PinIdOf(string sql, string column)
    {
        long h = 17;
        foreach (var c in sql) { h = h * 31 + c; }
        foreach (var c in column) { h = h * 31 + c; }
        return $"pin-{h & 0x7FFFFFFF:X8}";
    }

    static string Summarize(string sql)
    {
        var t = sql.Trim();
        if (t.Length <= 32) return t;
        return t[..32] + "…";
    }
}
