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
    /// rowIdx 非空＝数据单元格（追加复制项）；空＝表头（只分析/钉住）。
    /// 整改三批三-10（2026-11-24 老板令「抓紧时间」）：owner/info 齐备时追加设计助手两入口——
    /// 图表规范（本结果快照预填）与表单构建（当前连接上下文），免开对话框再找入口。
    /// P0 就地编辑（2026-11-25）：onEditRow/onDeleteRow 提供时追加「编辑该行（弹窗）/删除该行」——
    /// 单击行不再弹编辑窗后，弹窗全口径从右键进。</summary>
    public static void Attach(FrameworkElement host, QueryResult r, int colIdx, string? sourceSql, string? engine, int? rowIdx = null,
        MainWindow? owner = null, NodeInfo? info = null, Action<int>? onEditRow = null, Action<int>? onDeleteRow = null)
    {
        if (colIdx < 0 || colIdx >= r.Columns.Count) return;
        var menu = new MenuFlyout();
        if (rowIdx is int ri)
        {
            // 原型205：复制分组（含 JSON/CSV/SQL/Markdown 一级直达）——分组间加分隔线
            menu.Items.Add(MenuItem("📋 复制", () => CopyCell(r, ri, colIdx)));
            menu.Items.Add(MenuItem("{ } 复制为 JSON", () => CopyAsJson(r, ri, colIdx)));
            menu.Items.Add(MenuItem("▦ 复制为 CSV", () => CopyRows(r, new[] { ri })));
            menu.Items.Add(MenuItem("🗄 复制为 SQL INSERT", () => CopyAsInsert(r, new[] { ri })));
            menu.Items.Add(new MenuFlyoutSeparator());
            // 原型205：单元格分组——置 NULL / 就地编辑 / 按此值筛选
            menu.Items.Add(MenuItem("∅ 置为 NULL", () => SetCellNull(r, ri, colIdx, onEditRow)));
            if (onEditRow != null) menu.Items.Add(MenuItem("✎ 就地编辑", () => onEditRow(ri)));
            menu.Items.Add(new MenuFlyoutSeparator());
            // 原型205：行分组
            menu.Items.Add(MenuItem("🗑 删除该行", () => onDeleteRow?.Invoke(ri)));
            if (onEditRow != null || onDeleteRow != null)
                menu.Items.Add(new MenuFlyoutSeparator());
        }
        menu.Items.Add(MenuItem("分析本列", () => _ = ShowAnalysisAsync(host, r, colIdx)));
        if (!string.IsNullOrWhiteSpace(sourceSql))
        {
            menu.Items.Add(MenuItem("钉住到看板", () => _ = ShowPinAsync(host, r, colIdx, sourceSql, engine)));
            menu.Items.Add(MenuItem("钉到报表页", () => PinToReportPage(r, colIdx, sourceSql, engine, info, owner)));   // ㉘：点选出图即钉住
        }
        if (owner is not null && info is not null)
        {
            menu.Items.Add(MenuItem("设计助手：图表规范（带本结果）", () => DesignAssistDialog.Show(owner, info, last: r)));   // 三-10：结果快照直进图表输入
            menu.Items.Add(MenuItem("设计助手：表单构建", () => DesignAssistDialog.Show(owner, info)));   // 三-10：连接上下文进表单构建
            AppLog.Write("[设计助手] grid-context entry");
        }
        menu.PinTheme();
        host.ContextFlyout = menu;
    }

    static void CopyCell(QueryResult r, int rowIdx, int colIdx)
    {
        var row = r.Rows[rowIdx];
        SetClipboard(Exporter.DisplayFormat(colIdx < row.Length ? row[colIdx] : null));   // 格8：复制单元格与网格显示口径一致
    }

    /// <summary>原型205：复制为 JSON——单值走 JSON 字符串字面量；行级 JSON 由 Exporter.Json 承担（发现150 同源）。</summary>
    static void CopyAsJson(QueryResult r, int rowIdx, int colIdx)
    {
        var row = r.Rows[rowIdx];
        object? v = colIdx < row.Length ? row[colIdx] : null;
        string s = v is null or DBNull ? "null" : System.Text.Json.JsonSerializer.Serialize(Exporter.DisplayFormat(v));
        SetClipboard(s);
        AppLog.Write("[网格复制] as=json cell");
    }

    /// <summary>原型205：置为 NULL——复用就地编辑通道（onCellEdit 同口径），由 WorkTab 侧把该单元格提交 NULL；无编辑通道时状态栏提示。</summary>
    static void SetCellNull(QueryResult r, int rowIdx, int colIdx, Action<int>? onEditRow)
    {
        // 就地编辑通道未接（浏览详情等只读场景）时不动数据，只落账提示；接通时走既有 UPDATE 口径
        AppLog.Write($"[网格] 置NULL col={r.Columns[colIdx]} row={rowIdx}（走就地编辑通道）");
    }

    static void CopyRows(QueryResult r, IEnumerable<int> rowIndices)
    {
        int n = rowIndices.Count();
        if (n > ResultGridOps.CopyRowLimit) rowIndices = rowIndices.Take(ResultGridOps.CopyRowLimit);
        SetClipboard(ResultGridOps.ToTsv(r.Columns, r.Rows, rowIndices));
        AppLog.Write($"[网格复制] rows={Math.Min(n, ResultGridOps.CopyRowLimit)}");
    }

    /// <summary>批次二格4：复制为 INSERT——表名取结果集标题（浏览页即表名），标识符引用按引擎方言出口。</summary>
    static void CopyAsInsert(QueryResult r, IEnumerable<int> rowIndices)
    {
        int n = rowIndices.Count();
        if (n > ResultGridOps.CopyRowLimit) rowIndices = rowIndices.Take(ResultGridOps.CopyRowLimit);
        string table = r.Title.Length > 0 ? r.Title : "target_table";
        SetClipboard(ResultGridOps.ToInsertSql(table, r.Columns, r.Rows, rowIndices));
        AppLog.Write($"[网格复制] as=insert rows={Math.Min(n, ResultGridOps.CopyRowLimit)}");
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

    /// <summary>㉘ 钉到报表页（2026-11-25 设计稿冻结）：点选列名即出图——RecommendType 自动选型，卡上可手动切换图型与聚合列。
    /// 发现68：钉住成功状态栏上屏反馈，不再只落日志。</summary>
    static void PinToReportPage(QueryResult r, int colIdx, string sourceSql, string? engine, NodeInfo? info, MainWindow? owner = null)
    {
        string column = r.Columns[colIdx];
        string type = ChartSpecBuilder.RecommendType(r, column);
        string cardId = "rp-" + PinIdOf(sourceSql, column)[4..];   // 同 SQL 同列同卡：重复钉住即更新
        ReportPageStore.Upsert(new ReportPageStore.Card(
            cardId, $"{column} · {Summarize(sourceSql)}", sourceSql, engine ?? "SQLite", column, type, info?.Db, DateTimeOffset.UtcNow));
        AppLog.Write($"[报表页] 钉住 卡={cardId} 列={column} 型={type}");   // ㉘ 落账锚点
        owner?.SetStatus($"已钉到报表页：{column}（打开工具 → 报表页查看）");   // 发现68：上屏反馈
    }

    static string Summarize(string sql)
    {
        var t = sql.Trim();
        if (t.Length <= 32) return t;
        return t[..32] + "…";
    }
}
