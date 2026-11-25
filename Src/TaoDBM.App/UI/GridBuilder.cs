using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;
using TaoDBM.Core.Streaming;

namespace TaoDBM.UI;

/// <summary>把 QueryResult 渲染成表格（纯代码构建，Ant 风格表头）。</summary>
public static class GridBuilder
{
    public static FrameworkElement Build(QueryResult r, Action<int>? onRowTap = null, string? sourceSql = null, string? engine = null,
        Dictionary<string, double>? colWidths = null, string? widthKey = null, MainWindow? owner = null, NodeInfo? info = null)
    {
        // 批次F：宽表懒加载结果（列数超虚拟阈值）走等宽占位网格，横向滚动增量拉列
        if (r is LazyQueryResult lz && lz.Store.TotalColumns > 64)
            return LazyWideGrid.Build(lz, onRowTap, sourceSql, engine);

        if (r.Columns.Count == 0)
        {
            string msg = r.Affected >= 0
                ? $"{(r.Message.Length > 0 ? r.Message + "　" : "")}执行成功，影响 {r.Affected} 行 · {r.Elapsed.TotalMilliseconds:N0} ms"
                : (r.Message.Length > 0 ? r.Message : "执行成功，无结果集。");
            var ok = new TextBlock
            {
                Text = msg,
                Margin = new Thickness(16, 12, 16, 12),
                Foreground = Ant.Success,
                FontSize = AntTokens.FontMinor,
            };
            return new Border { Child = ok, Background = Ant.AltRow, CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg), Margin = new Thickness(4, 8, 4, 8) };
        }

        if (r.Rows.Count > VirtualThreshold)
        {
            // 虚拟滚动（2026-11-20 夜批②）：大数据量窗口化渲染，万行级不卡；列宽规则与普通渲染一致
            return VirtualizedResultGrid.Build(r, onRowTap, sourceSql, engine, owner, info);
        }

        // 整改二批 #03（2026-11-23）：表头点击循环排序（内存序，不回库）＋表头冻结（滚动时钉在视口顶部）
        // 整改二批格9（2026-11-23）：列宽拖拽可调——用户宽度优先，无记录回退自动宽公式
        var grid = new Grid { RowSpacing = 0 };
        int nCols = Math.Min(r.Columns.Count, 64);
        string WKey(int i) => $"{widthKey ?? sourceSql ?? ""}|{r.Columns[i]}";
        double[] widths = new double[nCols];
        for (int i = 0; i < nCols; i++)
        {
            widths[i] = colWidths != null && colWidths.TryGetValue(WKey(i), out double uw) ? uw : AutoWidth(r.Columns[i]);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[i]) });
        }

        int sortCol = -1, sortMode = ResultGridOps.SortOriginal;
        var order = Enumerable.Range(0, r.Rows.Count).ToArray();
        var rowsHost = new StackPanel { Orientation = Orientation.Vertical };

        // 表头（含排序指示与点击循环）
        var header = new Grid { Background = Ant.HeadBg };
        var headerTt = new TranslateTransform();
        header.RenderTransform = headerTt;
        var arrows = new TextBlock[nCols];
        for (int i = 0; i < nCols; i++)
            header.ColumnDefinitions.Add(ColDef(r, i, widths[i]));
        for (int i = 0; i < nCols; i++)
        {
            var arrow = new TextBlock { Foreground = Ant.Primary, FontSize = AntTokens.FontCaption, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            arrows[i] = arrow;
            var headPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headPanel.Children.Add(new TextBlock
            {
                Text = r.Columns[i],
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Ant.Text,
                Margin = new Thickness(10, 8, 0, 8),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            headPanel.Children.Add(arrow);
            // 格9：表头右缘 6px 拖拽分隔条（Pointer 捕获拖拽，双击恢复自动宽；与 Tapped 排序互不触发）
            var cellHost = new Grid();
            cellHost.Children.Add(headPanel);
            // WinUI 3 Border 为 sealed 且无公开光标 API，悬停光标特性无走查依赖，撤除保零警硬门禁
            var splitter = new Border
            {
                Width = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(splitter, $"ColSplitter_{r.Columns[i]}");
            cellHost.Children.Add(splitter);
            var cell = new Border { Child = cellHost, BorderBrush = Ant.Border, BorderThickness = new Thickness(0, 0, 1, 1), Background = Ant.HeadBg };
            AttachSplitter(splitter, i, r, grid, header, rowsHost, widths, colWidths, WKey);
            ResultGridMenu.Attach(cell, r, i, sourceSql, engine, null, owner, info);   // 列头右键 → 分析/钉住；三-10 设计助手入口
            int colIdx = i;
            cell.Tapped += (_, _) => CycleSort(colIdx);
            Grid.SetColumn(cell, i);
            header.Children.Add(cell);
        }
        Grid.SetRow(header, grid.RowDefinitions.Count);
        Grid.SetColumnSpan(header, nCols);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(header);
        Canvas.SetZIndex(header, 10);   // 冻结：数据行从表头下滚过

        // 数据行（排序后按显示序物化；小结果集直渲染，虚拟阈值之上走 VirtualizedResultGrid）
        Grid.SetRow(rowsHost, grid.RowDefinitions.Count);
        Grid.SetColumnSpan(rowsHost, nCols);   // 修红 2026-11-22 同批：行宿主跨全列，避免被首列宽度裁切
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(rowsHost);

        void PopulateRows()
        {
            rowsHost.Children.Clear();
            int shown = Math.Min(r.Rows.Count, 1000);
            for (int d = 0; d < shown; d++)
                rowsHost.Children.Add(BuildRow(r, order[d], nCols, onRowTap, sourceSql: sourceSql, engine: engine, widths: widths, owner: owner, info: info));
        }
        PopulateRows();

        void CycleSort(int colIdx)
        {
            if (sortCol != colIdx) { sortCol = colIdx; sortMode = ResultGridOps.SortAsc; }
            else if (sortMode == ResultGridOps.SortAsc) sortMode = ResultGridOps.SortDesc;
            else { sortCol = -1; sortMode = ResultGridOps.SortOriginal; }
            order = ResultGridOps.SortOrder(r.Rows, colIdx, sortMode);
            AppLog.Write($"[网格] 排序 col={r.Columns[colIdx]} mode={sortMode} rows={r.Rows.Count}");   // 走查 S22 实效断言锚点
            for (int i = 0; i < nCols; i++)
                arrows[i].Text = i == colIdx && sortMode != ResultGridOps.SortOriginal ? (sortMode == ResultGridOps.SortAsc ? "▲" : "▼") : "";
            PopulateRows();
        }

        var outer = new Grid();
        var scroll = new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        // 表头冻结：滚动时表头平移钉在视口左上角（内容随滚动上移 V，表头补回 +V 抵消），数据行从其下滚过
        scroll.ViewChanged += (s, e) =>
        {
            headerTt.X = scroll.HorizontalOffset;
            headerTt.Y = scroll.VerticalOffset;
        };
        outer.Children.Add(scroll);

        if (r.Rows.Count > 1000)
        {
            var note = new TextBlock
            {
                Text = $"共 {r.Rows.Count} 行，界面渲染前 1000 行（导出不受影响）。",
                Margin = new Thickness(12, 4, 4, 4),
                Foreground = Ant.TextSub,
                FontSize = AntTokens.FontMinor,
            };
            var sp = new StackPanel { Orientation = Orientation.Vertical };
            sp.Children.Add(scroll);
            sp.Children.Add(note);
            outer.Children.Clear();
            outer.Children.Add(sp);
        }
        return outer;
    }

    /// <summary>虚拟渲染的行数阈值：之上走窗口化虚拟滚动（万行级不卡）。</summary>
    public const int VirtualThreshold = 500;

    /// <summary>单行物化（普通渲染与虚拟渲染共用）：nCols 列单元格 + 隔行底色 + 可选行点选。
    /// fixedHeight 供虚拟渲染锁行高（位移映射需要统一行高）；null 保持原自适应高度。
    /// sourceSql/engine 非空时给单元格挂右键分析/钉住（批次E）；null 保持原状（浏览详情等只读场景）。
    /// 整改二批 #03：单元格一律挂右键菜单（复制该行/单元格/全部＋分析）；displayIdx 提供时按显示位次取隔行底色（排序后斑马纹不乱）。</summary>
    public static Grid BuildRow(QueryResult r, int rowIdx, int nCols, Action<int>? onRowTap, double? fixedHeight = null,
        string? sourceSql = null, string? engine = null, int? displayIdx = null, double[]? widths = null, MainWindow? owner = null, NodeInfo? info = null)
    {
        var row = r.Rows[rowIdx];
        var rowGrid = new Grid { Background = (displayIdx ?? rowIdx) % 2 == 1 ? Ant.AltRow : null };
        if (fixedHeight is double h) rowGrid.Height = h;
        for (int i = 0; i < nCols; i++)
            rowGrid.ColumnDefinitions.Add(ColDef(r, i, widths is null ? double.NaN : widths[i]));   // 修红 2026-11-22：行 Grid 补列定义；格9 沿用用户宽度
        for (int i = 0; i < nCols; i++)
        {
            string text = Exporter.DisplayFormat(i < row.Length ? row[i] : null);   // 格8：网格显示口径（NULL→(NULL)、大 BLOB 显体积）；CSV/INSERT 导出仍走 Format
            var tb = new TextBlock
            {
                Text = text,
                Margin = new Thickness(10, 6, 10, 6),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Ant.Text,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var cell = new Border { Child = tb, BorderBrush = Ant.Border, BorderThickness = new Thickness(0, 0, 1, 1) };
            ResultGridMenu.Attach(cell, r, i, sourceSql, engine, rowIdx, owner, info);   // #03：复制该行/单元格/全部 ＋ 分析/钉住；三-10 设计助手入口
            Grid.SetColumn(cell, i);
            rowGrid.Children.Add(cell);
        }
        if (onRowTap != null)
        {
            int idx = rowIdx;
            rowGrid.Tapped += (_, _) => onRowTap(idx);
        }
        return rowGrid;
    }

    /// <summary>列宽规则唯一出口：外层网格、表头、数据行共用，保证三处列宽一致；width 非 NaN 时用指定宽度（格9 用户宽度）。</summary>
    internal static ColumnDefinition ColDef(QueryResult r, int i, double width = double.NaN) =>
        new() { Width = new GridLength(double.IsNaN(width) ? AutoWidth(r.Columns[i]) : width) };

    /// <summary>自动宽公式（格9 回退口径）：按列样例长度估算，夹在 120-280。</summary>
    internal static double AutoWidth(string col) => Math.Clamp(80 + col.Length * 8, 120, 280);

    /// <summary>格9：列宽拖拽——PointerPressed 捕获拖拽改三处列宽（外层/表头/行），位移阈值 4px 与排序 Tapped 互不触发；
    /// 双击恢复自动宽并清除用户记录；拖拽结束记 AppLog [列宽]。</summary>
    private static void AttachSplitter(Border splitter, int i, QueryResult r, Grid outer, Grid header, StackPanel rowsHost,
        double[] widths, Dictionary<string, double>? colWidths, Func<int, string> wkey)
    {
        bool dragging = false;
        double startX = 0, startW = 0, moved = 0;

        // C② affordance：悬停高亮＋加宽（WinUI 3 Border 为 sealed 且无公开光标 API，↔光标撤除，与主分栏条同一套规范）
        splitter.PointerEntered += (_, _) =>
        {
            splitter.Width = 8;
            splitter.Background = Ant.Primary;
        };
        splitter.PointerExited += (_, _) =>
        {
            if (dragging) return;
            splitter.Width = 6;
            splitter.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        };

        void ApplyWidth(double w)
        {
            widths[i] = w;
            outer.ColumnDefinitions[i].Width = new GridLength(w);
            header.ColumnDefinitions[i].Width = new GridLength(w);
            foreach (var child in rowsHost.Children)
                if (child is Grid rowGrid && i < rowGrid.ColumnDefinitions.Count)
                    rowGrid.ColumnDefinitions[i].Width = new GridLength(w);
        }

        splitter.PointerPressed += (_, e) =>
        {
            dragging = true;
            moved = 0;
            startX = e.GetCurrentPoint(splitter).Position.X;
            startW = widths[i];
            splitter.CapturePointer(e.Pointer);
            e.Handled = true;   // 拦下指针，避免触发表头 Tapped 排序
        };
        splitter.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            double dx = e.GetCurrentPoint(splitter).Position.X - startX;
            moved = Math.Abs(dx);
            if (moved < 4) return;
            ApplyWidth(Math.Clamp(startW + dx, 60, 800));
            e.Handled = true;
        };
        splitter.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            splitter.ReleasePointerCapture(e.Pointer);
            splitter.Width = 6;
            splitter.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (moved >= 4)
            {
                if (colWidths != null) colWidths[wkey(i)] = widths[i];
                AppLog.Write($"[列宽] col={r.Columns[i]} width={widths[i]:F0}");   // 走查 S76 实效断言锚点
            }
            e.Handled = true;
        };
        splitter.DoubleTapped += (_, e) =>
        {
            ApplyWidth(AutoWidth(r.Columns[i]));
            colWidths?.Remove(wkey(i));
            AppLog.Write($"[列宽] col={r.Columns[i]} 自动");
            e.Handled = true;
        };
    }
}
