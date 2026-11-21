using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;
using TaoDBM.Core.Streaming;

namespace TaoDBM.UI;

/// <summary>宽表列懒加载网格（批次F）：全部列按固定列宽占位（横向滚动条真实反映总列数），
/// 行维度走 VirtualWindowMath 窗口化；横向滚到未加载列 → ColumnLazyStore.EnsureColumnsAsync 增量拉取后重画，
/// 未加载列渲染空单元格。单元格视觉与 GridBuilder.BuildRow 一致（等宽 160px）。</summary>
public static class LazyWideGrid
{
    public const double ColWidth = 160;

    public static FrameworkElement Build(LazyQueryResult lz, Action<int>? onRowTap = null, string? sourceSql = null, string? engine = null)
    {
        var store = lz.Store;
        int nCols = store.TotalColumns;
        int total = Math.Max(store.RowCount, lz.Rows.Count);
        double rowH = VirtualWindowMath.RowHeight;

        var header = BuildHeader(store, sourceSql, engine);
        header.Height = rowH;

        var rowsHost = new Grid { VerticalAlignment = VerticalAlignment.Top };
        var viewport = new Border
        {
            Width = nCols * ColWidth,
            Height = VirtualWindowMath.TotalHeight(total),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, rowH, 0, 0),
            Child = rowsHost,
        };
        var root = new Grid
        {
            Width = nCols * ColWidth,
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
        int lastFirst = int.MinValue, lastLast = int.MinValue, lastColFrom = int.MinValue, lastColTo = int.MinValue;

        void Reflow()
        {
            double top = Math.Max(0, scroll.VerticalOffset - rowH);
            double vh = Math.Max(0, scroll.ViewportHeight - rowH);
            var (first, last) = VirtualWindowMath.VisibleWindow(top, vh, total);
            int colFrom = (int)Math.Floor(Math.Max(0, scroll.HorizontalOffset) / ColWidth);
            int visCols = (int)Math.Ceiling(scroll.ViewportWidth / ColWidth);
            int colTo = Math.Min(nCols - 1, colFrom + visCols + 1);
            if (top == lastTop && first == lastFirst && last == lastLast && colFrom == lastColFrom && colTo == lastColTo) return;
            lastTop = top; lastFirst = first; lastLast = last; lastColFrom = colFrom; lastColTo = colTo;

            // 横向窗口内未加载列 → 增量拉取（Store 内部去重），完成后窗口重画
            var missing = new List<int>();
            for (int c = colFrom; c <= colTo; c++)
                if (!store.IsLoaded(c)) missing.Add(c);
            if (missing.Count > 0)
                _ = store.EnsureColumnsAsync(missing).ContinueWith(_ => scroll.DispatcherQueue?.TryEnqueue(() => Reflow()));

            rowsHost.Children.Clear();
            for (int rowIdx = first; rowIdx <= last; rowIdx++)
            {
                var el = BuildRow(store, rowIdx, colFrom, colTo, onRowTap, rowH);
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

    private static Grid BuildHeader(ColumnLazyStore store, string? sourceSql, string? engine)
    {
        var header = new Grid { Background = Ant.HeadBg };
        for (int i = 0; i < store.TotalColumns; i++)
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColWidth) });
        for (int i = 0; i < store.TotalColumns; i++)
        {
            var tb = new TextBlock
            {
                Text = store.ColumnNames[i],
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Ant.Text,
                Margin = new Thickness(10, 8, 10, 8),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var cell = new Border { Child = tb, BorderBrush = Ant.Border, BorderThickness = new Thickness(0, 0, 1, 1) };
            Grid.SetColumn(cell, i);
            header.Children.Add(cell);
        }
        return header;
    }

    private static Grid BuildRow(ColumnLazyStore store, int rowIdx, int colFrom, int colTo, Action<int>? onRowTap, double rowH)
    {
        var rowGrid = new Grid { Background = rowIdx % 2 == 1 ? Ant.AltRow : null, Height = rowH };
        for (int i = colFrom; i <= colTo; i++)
        {
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColWidth) });
            string text = store.IsLoaded(i) ? Exporter.Format(store.GetValue(rowIdx, i)) : "";
            var tb = new TextBlock
            {
                Text = text,
                Margin = new Thickness(10, 6, 10, 6),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Ant.Text,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var cell = new Border { Child = tb, BorderBrush = Ant.Border, BorderThickness = new Thickness(0, 0, 1, 1) };
            Grid.SetColumn(cell, i - colFrom);
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
