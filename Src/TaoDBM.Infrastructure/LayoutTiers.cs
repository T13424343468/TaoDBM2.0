using System;

namespace TaoDBM.Core;

/// <summary>弹性布局分档（需求合并稿第十五章，2026-11-20 批次H；否决整体缩放路线）：
/// 紧凑 &lt;1366 / 标准 &lt;2560 / 宽屏 ≥2560，DIP 口径；高 DPI 先经 EffectiveWidth 换算。</summary>
public enum LayoutTier
{
    /// <summary>紧凑档：窄窗（&lt;1366 DIP），收次级文字、收窄留白。</summary>
    Compact,

    /// <summary>标准档：1366–2559 DIP 主流桌面。</summary>
    Standard,

    /// <summary>宽屏档：≥2560 DIP，放宽留白承宽表。</summary>
    Wide,
}

/// <summary>分档判定的纯逻辑：窗口宽度（DIP）→ 档位；150% DPI 等高倍屏按换算后宽度判定。</summary>
public static class LayoutTiers
{
    /// <summary>紧凑/标准分界（1366 三档口径下界）。</summary>
    public const double CompactBreakpoint = 1366;

    /// <summary>标准/宽屏分界（2560 三档口径上界）。</summary>
    public const double WideBreakpoint = 2560;

    /// <summary>物理像素 → DIP（RasterizationScale 口径；scale 非法时按原值处理）。
    /// 150% 验证：2560 物理 @1.5 = 1706.67 DIP → 标准档。</summary>
    public static double EffectiveWidth(double pixelWidth, double rasterizationScale)
        => rasterizationScale > 0 ? pixelWidth / rasterizationScale : pixelWidth;

    /// <summary>按 DIP 宽度判档；边界值归高档（1366 起即标准，2560 起即宽屏）。</summary>
    public static LayoutTier Resolve(double widthDip)
        => widthDip < CompactBreakpoint ? LayoutTier.Compact
         : widthDip < WideBreakpoint ? LayoutTier.Standard
         : LayoutTier.Wide;
}
