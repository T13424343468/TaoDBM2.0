using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>大数据量结果的窗口化虚拟渲染（二期对账缺口，2026-11-20 夜批②）：
/// 只物化可视区（含缓冲）的行，行容器按统一行高预留总高——滚动条长度真实反映数据量，万行级不卡。
/// 行视觉与 GridBuilder.BuildRow 完全一致；窗口计算走 VirtualWindowMath（LogicTest 已覆盖边界）。</summary>
public static class VirtualizedResultGrid
{
    public static FrameworkElement Build(QueryResult r, Action<int>? onRowTap = null, string? sourceSql = null, string? engine = null)
    {
        int nCols = Math.Min(r.Columns.Count, 64);
        int total = r.Rows.Count;
        double rowH = VirtualWindowMath.RowHeight;

        // 表头：与数据行同列宽规则、同行高（虚拟位移以一个行高为偏移基准）
        // 修红 2026-11-22：表头 Grid 此前未定义列，所有列头叠在第 0 列——真机截图实锤，与 GridBuilder 同批修
        var header = new Grid { Background = Ant.HeadBg };
        for (int i = 0; i < nCols; i++)
            header.ColumnDefinitions.Add(GridBuilder.ColDef(r, i));
        for (int i = 0; i < nCols; i++)
        {
            var tb = new TextBlock
            {
                Text = r.Columns[i],
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Ant.Text,
                Margin = new Thickness(10, 8, 10, 8),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var cell = new Border { Child = tb, BorderBrush = Ant.Border, BorderThickness = new Thickness(0, 0, 1, 1) };
            ResultGridMenu.Attach(cell, r, i, sourceSql, engine);   // 批次E：列头右键 → 分析/钉住
            Grid.SetColumn(cell, i);
            header.Children.Add(cell);
        }
        header.Height = rowH;

        var rowsHost = new Grid { VerticalAlignment = VerticalAlignment.Top };
        var viewport = new Border
        {
            Height = VirtualWindowMath.TotalHeight(total),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, rowH, 0, 0),
            Child = rowsHost,
        };
        var root = new Grid
        {
            Height = rowH + VirtualWindowMath.TotalHeight(total),
            VerticalAlignment = VerticalAlignment.Top,
        };
        root.Children.Add(header);
        root.Children.Add(viewport);

        var scroll = new ScrollViewer
        {
            Content = root,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        double lastTop = double.NaN;
        int lastFirst = int.MinValue, lastLast = int.MinValue;

        // 只重画窗口变化：滚动/尺寸变化高频触发，窗口未动时零重建
        void Reflow()
        {
            double top = Math.Max(0, scroll.VerticalOffset - rowH);          // 头部滚出视口的偏移
            double vh = Math.Max(0, scroll.ViewportHeight - rowH);           // 视口内数据区高度
            var (first, last) = VirtualWindowMath.VisibleWindow(top, vh, total);
            if (top == lastTop && first == lastFirst && last == lastLast) return;
            lastTop = top; lastFirst = first; lastLast = last;
            rowsHost.Children.Clear();
            for (int rowIdx = first; rowIdx <= last; rowIdx++)
            {
                var el = GridBuilder.BuildRow(r, rowIdx, nCols, onRowTap, rowH, sourceSql, engine);
                el.VerticalAlignment = VerticalAlignment.Top;
                el.Margin = new Thickness(0, rowIdx * rowH, 0, 0);
                rowsHost.Children.Add(el);
            }
        }

        scroll.ViewChanged += (s, e) => Reflow();
        scroll.SizeChanged += (s, e) => Reflow();
        scroll.Loaded += (s, e) => Reflow();
        Reflow();
        return scroll;
    }
}
