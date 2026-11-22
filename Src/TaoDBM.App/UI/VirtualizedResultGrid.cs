using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>大数据量结果的窗口化虚拟渲染（二期对账缺口，2026-11-20 夜批②）：
/// 只物化可视区（含缓冲）的行，行容器按统一行高预留总高——滚动条长度真实反映数据量，万行级不卡。
/// 行视觉与 GridBuilder.BuildRow 完全一致；窗口计算走 VirtualWindowMath（LogicTest 已覆盖边界）。
/// 整改二批 #03（2026-11-23）：表头点击循环排序（内存序映射，不回库）＋表头冻结（滚动时钉在视口顶部）。</summary>
public static class VirtualizedResultGrid
{
    public static FrameworkElement Build(QueryResult r, Action<int>? onRowTap = null, string? sourceSql = null, string? engine = null)
    {
        int nCols = Math.Min(r.Columns.Count, 64);
        int total = r.Rows.Count;
        double rowH = VirtualWindowMath.RowHeight;

        var order = Enumerable.Range(0, total).ToArray();
        int sortCol = -1, sortMode = ResultGridOps.SortOriginal;
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
        var scroll = new ScrollViewer
        {
            Content = root,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        // 表头：与数据行同列宽规则、同行高（虚拟位移以一个行高为偏移基准）；含排序指示与点击循环
        var header = new Grid { Background = Ant.HeadBg };
        var headerTt = new TranslateTransform();
        header.RenderTransform = headerTt;
        var arrows = new TextBlock[nCols];
        for (int i = 0; i < nCols; i++)
        {
            var arrow = new TextBlock { Foreground = Ant.Primary, FontSize = 11, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            arrows[i] = arrow;
            var headPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headPanel.Children.Add(new TextBlock
            {
                Text = r.Columns[i],
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Ant.Text,
                Margin = new Thickness(10, 8, 0, 8),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            headPanel.Children.Add(arrow);
            var cell = new Border { Child = headPanel, BorderBrush = Ant.Border, BorderThickness = new Thickness(0, 0, 1, 1), Background = Ant.HeadBg };
            ResultGridMenu.Attach(cell, r, i, sourceSql, engine);   // 列头右键 → 分析/钉住
            int colIdx = i;
            cell.Tapped += (_, _) => CycleSort(colIdx);
            Grid.SetColumn(cell, i);
            header.Children.Add(cell);
        }
        header.Height = rowH;
        Canvas.SetZIndex(header, 10);   // 冻结：数据行从表头下滚过
        root.Children.Add(header);
        root.Children.Add(viewport);

        double lastTop = double.NaN;
        int lastFirst = int.MinValue, lastLast = int.MinValue;

        // 只重画窗口变化：滚动/尺寸变化高频触发，窗口未动时零重建。
        // 表头已冻结钉在视口顶部：可见数据区 = 内容 y [V+rowH, V+Vh] → 数据偏移 top=V、高度 Vh-rowH
        void Reflow()
        {
            double top = Math.Max(0, scroll.VerticalOffset);                 // 表头钉住后数据区首行偏移
            double vh = Math.Max(0, scroll.ViewportHeight - rowH);           // 视口内数据区高度
            var (first, last) = VirtualWindowMath.VisibleWindow(top, vh, total);
            if (top == lastTop && first == lastFirst && last == lastLast) return;
            lastTop = top; lastFirst = first; lastLast = last;
            rowsHost.Children.Clear();
            for (int rowIdx = first; rowIdx <= last; rowIdx++)
            {
                var el = GridBuilder.BuildRow(r, order[rowIdx], nCols, onRowTap, rowH, sourceSql, engine, rowIdx);
                el.VerticalAlignment = VerticalAlignment.Top;
                el.Margin = new Thickness(0, rowIdx * rowH, 0, 0);
                rowsHost.Children.Add(el);
            }
        }

        void CycleSort(int colIdx)
        {
            if (sortCol != colIdx) { sortCol = colIdx; sortMode = ResultGridOps.SortAsc; }
            else if (sortMode == ResultGridOps.SortAsc) sortMode = ResultGridOps.SortDesc;
            else { sortCol = -1; sortMode = ResultGridOps.SortOriginal; }
            order = ResultGridOps.SortOrder(r.Rows, colIdx, sortMode);
            for (int i = 0; i < nCols; i++)
                arrows[i].Text = i == colIdx && sortMode != ResultGridOps.SortOriginal ? (sortMode == ResultGridOps.SortAsc ? "▲" : "▼") : "";
            lastTop = double.NaN; lastFirst = int.MinValue; lastLast = int.MinValue;   // 强制重画窗口
            Reflow();
        }

        scroll.ViewChanged += (s, e) =>
        {
            // 表头冻结：内容随滚动上移 V，表头补回 +V 抵消（横向同理）
            headerTt.X = scroll.HorizontalOffset;
            headerTt.Y = scroll.VerticalOffset;
            Reflow();
        };
        scroll.SizeChanged += (s, e) => Reflow();
        scroll.Loaded += (s, e) => Reflow();
        Reflow();
        return scroll;
    }
}
