using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>把 QueryResult 渲染成表格（纯代码构建，Ant 风格表头）。</summary>
public static class GridBuilder
{
    public static FrameworkElement Build(QueryResult r, Action<int>? onRowTap = null, string? sourceSql = null, string? engine = null)
    {
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
                FontSize = 12,
            };
            return new Border { Child = ok, Background = Ant.AltRow, CornerRadius = new CornerRadius(6), Margin = new Thickness(4, 8, 4, 8) };
        }

        if (r.Rows.Count > VirtualThreshold)
        {
            // 虚拟滚动（2026-11-20 夜批②）：大数据量窗口化渲染，万行级不卡；列宽规则与普通渲染一致
            return VirtualizedResultGrid.Build(r, onRowTap, sourceSql, engine);
        }

        var grid = new Grid { RowSpacing = 0 };
        int nCols = Math.Min(r.Columns.Count, 64);
        for (int i = 0; i < nCols; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(80 + r.Columns[i].Length * 8, 120, 280)) });

        // 表头
        var header = new Grid { Background = Ant.HeadBg };
        for (int i = 0; i < nCols; i++)
        {
            var tb = new TextBlock
            {
                Text = r.Columns[i],
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Ant.Text,
                Margin = new Thickness(10, 8, 10, 8),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var cell = new Border { Child = tb, BorderBrush = Ant.Border, BorderThickness = new Thickness(0, 0, 1, 1) };
            ResultGridMenu.Attach(cell, r, i, sourceSql, engine);   // 批次E：列头右键 → 分析/钉住
            Grid.SetColumn(cell, i);
            header.Children.Add(cell);
        }
        Grid.SetRow(header, grid.RowDefinitions.Count);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(header);

        // 数据行（小结果集直渲染；虚拟阈值之上走 VirtualizedResultGrid）
        int shown = Math.Min(r.Rows.Count, 1000);
        for (int rowIdx = 0; rowIdx < shown; rowIdx++)
        {
            var rowGrid = BuildRow(r, rowIdx, nCols, onRowTap);
            Grid.SetRow(rowGrid, grid.RowDefinitions.Count);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(rowGrid);
        }

        var outer = new Grid();
        var scroll = new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        outer.Children.Add(scroll);

        if (r.Rows.Count > shown)
        {
            var note = new TextBlock
            {
                Text = $"共 {r.Rows.Count} 行，界面渲染前 {shown} 行（导出不受影响）。",
                Margin = new Thickness(12, 4, 4, 4),
                Foreground = Ant.TextSub,
                FontSize = 12,
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
    /// sourceSql/engine 非空时给单元格挂右键分析/钉住（批次E）；null 保持原状（浏览详情等只读场景）。</summary>
    public static Grid BuildRow(QueryResult r, int rowIdx, int nCols, Action<int>? onRowTap, double? fixedHeight = null,
        string? sourceSql = null, string? engine = null)
    {
        var row = r.Rows[rowIdx];
        var rowGrid = new Grid { Background = rowIdx % 2 == 1 ? Ant.AltRow : null };
        if (fixedHeight is double h) rowGrid.Height = h;
        for (int i = 0; i < nCols; i++)
        {
            string text = Exporter.Format(i < row.Length ? row[i] : null);
            var tb = new TextBlock
            {
                Text = text,
                Margin = new Thickness(10, 6, 10, 6),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Ant.Text,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var cell = new Border { Child = tb, BorderBrush = Ant.Border, BorderThickness = new Thickness(0, 0, 1, 1) };
            if (sourceSql != null) ResultGridMenu.Attach(cell, r, i, sourceSql, engine);
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
}
