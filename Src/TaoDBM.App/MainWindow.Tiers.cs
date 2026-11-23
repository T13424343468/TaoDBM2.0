using Microsoft.UI.Xaml;
using TaoDBM.Core;
using TaoDBM.UI;

namespace TaoDBM;

/// <summary>弹性布局分档挂接（需求合并稿第十五章，批次H）：
/// 按窗口 DIP 宽度判档，紧凑档收次级元素，宽屏档放宽标题；否决整体缩放。
/// 纯判定逻辑在 Core.LayoutTiers（LogicTest 覆盖），此处只做 UI 侧应用。</summary>
public partial class MainWindow
{
    private LayoutTier? _currentTier;
    private bool _autoSidebarCollapsed;   // 批次六格2：<1000 DIP 自动收起侧栏（区别于用户手动收起，回宽自动还原）

    /// <summary>当前生效档位（测试与走查读取；null=尚未应用）。</summary>
    internal LayoutTier? CurrentTier => _currentTier;

    /// <summary>按内容区实际宽度应用分档；档位不变时幂等跳过。</summary>
    internal void ApplyLayoutTier()
    {
        if (Content is not FrameworkElement fe) return;
        double w = fe.ActualWidth > 0 ? fe.ActualWidth : 0;
        if (w <= 0) return;

        var tier = Core.LayoutTiers.Resolve(w);
        if (_currentTier == tier) return;
        _currentTier = tier;

        switch (tier)
        {
            case LayoutTier.Compact:
                AppTitleText.Visibility = Visibility.Collapsed;   // 窄窗收标题文字，留工具钮
                break;
            case LayoutTier.Standard:
                AppTitleText.Visibility = Visibility.Visible;
                AppTitleText.FontSize = AntTokens.FontTitle;
                break;
            case LayoutTier.Wide:
                AppTitleText.Visibility = Visibility.Visible;
                AppTitleText.FontSize = AntTokens.FontPage;                       // 宽屏放宽标题
                break;
        }

        // 批次六格2：窗口 <1000 DIP 断点——侧栏自动收起（列宽清零＋分栏条隐藏），回宽自动还原；
        // 用户手动收起态（列宽已为 0）不重复干预
        if (w < 1000 && !_autoSidebarCollapsed && TreeCol.Width.Value > 0.5)
        {
            _autoSidebarCollapsed = true;
            TreeCol.MinWidth = 0;
            TreeCol.Width = new GridLength(0);
            TreeSplitter.Visibility = Visibility.Collapsed;
        }
        else if (w >= 1000 && _autoSidebarCollapsed)
        {
            _autoSidebarCollapsed = false;
            TreeCol.MinWidth = 200;
            TreeCol.Width = new GridLength(300);
            TreeSplitter.Visibility = Visibility.Visible;
        }

        Core.AppLog.Write($"布局分档切换: {tier} @ {w:0} DIP");
    }

    private void OnSizeTierChanged(object sender, Microsoft.UI.Xaml.WindowSizeChangedEventArgs args)
        => ApplyLayoutTier();
}
