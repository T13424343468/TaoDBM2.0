using System;

namespace TaoDBM.Core;

/// <summary>虚拟滚动窗口计算（二期全量对账缺口，2026-11-20 夜批②）：
/// 大数据量结果集不做全量渲染，只物化可视区上下缓冲内的行；万行级不卡。
/// 纯函数、无 UI 依赖，LogicTest 直测边界：空集 / 单行 / 超万行 / 越界偏移。</summary>
public static class VirtualWindowMath
{
    /// <summary>虚拟行统一行高（DIP）：与结果网格行 Border 显式 Height 一致，位移映射按此换算。</summary>
    public const double RowHeight = 32;

    /// <summary>视口外上下各缓存的行数：滚动时不闪空。</summary>
    public const int BufferRows = 5;

    /// <summary>可视窗口闭区间 [First, Last]：滚动偏移 + 视口高 → 应物化的行号范围。
    /// 空集返回 (0, -1)（零行）；单行返回 (0, 0)；偏移越界、NaN、负值全部夹取，绝不越界。</summary>
    public static (int First, int Last) VisibleWindow(double scrollOffset, double viewportHeight, int totalRows, double rowHeight = RowHeight, int buffer = BufferRows)
    {
        if (totalRows <= 0) return (0, -1);
        if (double.IsNaN(rowHeight) || double.IsInfinity(rowHeight) || rowHeight <= 0) rowHeight = RowHeight;
        if (buffer < 0) buffer = 0;
        double top = double.IsNaN(scrollOffset) || double.IsInfinity(scrollOffset) ? 0 : Math.Max(0, scrollOffset);
        double vh = double.IsNaN(viewportHeight) || double.IsInfinity(viewportHeight) ? 0 : Math.Max(0, viewportHeight);
        int first = Clamp((int)Math.Floor(top / rowHeight) - buffer, 0, totalRows - 1);
        int last = Clamp((int)Math.Ceiling((top + vh) / rowHeight) + buffer, first, totalRows - 1);
        return (first, last);
    }

    /// <summary>虚拟区总高：所有行按统一行高预留（滚动条长度真实反映数据量）。</summary>
    public static double TotalHeight(int totalRows, double rowHeight = RowHeight)
        => totalRows <= 0 ? 0 : (rowHeight <= 0 || double.IsNaN(rowHeight) ? RowHeight : totalRows * rowHeight);

    /// <summary>窗口行数上界：视口再高，物化行数也有确定上限（防大视口退化全量）。</summary>
    public static int MaxMaterialized(double viewportHeight, double rowHeight = RowHeight, int buffer = BufferRows)
    {
        if (double.IsNaN(viewportHeight) || double.IsInfinity(viewportHeight) || viewportHeight < 0) viewportHeight = 0;
        if (double.IsNaN(rowHeight) || double.IsInfinity(rowHeight) || rowHeight <= 0) rowHeight = RowHeight;
        if (buffer < 0) buffer = 0;
        return (int)Math.Ceiling(viewportHeight / rowHeight) + 2 * buffer + 1;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
}
