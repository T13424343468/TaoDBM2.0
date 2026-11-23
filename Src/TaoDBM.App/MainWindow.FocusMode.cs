using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TaoDBM;

// 注：OfType<TabViewItem>() 走 System.Linq；ImplicitUsings 已全局启用（工程口径），无需显式 using。

/// <summary>批次六格3（2026-11-24 老板令瀑布落码）：专注模式。
/// F11 或菜单切换：隐藏顶栏/侧栏/分栏条/状态栏，页签头清空（页签条留白不带文字），编辑器＋结果占满；再按恢复原布局。
/// 状态落 focusmode.json（损坏回落关闭），重启恢复；[专注] on/off 落账。</summary>
public partial class MainWindow
{
    private bool _focusOn;
    private readonly Dictionary<TabViewItem, object> _focusSavedHeaders = new();
    private GridLength _focusSavedTreeWidth;
    private double _focusSavedTreeMinWidth;

    /// <summary>XAML 菜单入口（AutomationId=FocusMode）。</summary>
    private void OnToggleFocusMode(object sender, RoutedEventArgs e) => ToggleFocusMode();

    /// <summary>F11 / 菜单共用切换。</summary>
    internal void ToggleFocusMode()
    {
        ApplyFocus(!_focusOn);
        Core.AppLog.Write($"[专注] {(_focusOn ? "on" : "off")}");
    }

    private void ApplyFocus(bool on)
    {
        _focusOn = on;
        var vis = on ? Visibility.Collapsed : Visibility.Visible;

        // 顶栏 / 状态栏整条隐藏
        HeadBorder.Visibility = vis;
        StatusBorder.Visibility = vis;

        // 侧栏：列宽清零（含 MinWidth，否则 200 下限顶住）＋分栏条隐藏；恢复原宽
        if (on)
        {
            _focusSavedTreeWidth = TreeCol.Width;
            _focusSavedTreeMinWidth = TreeCol.MinWidth;
            TreeCol.MinWidth = 0;
            TreeCol.Width = new GridLength(0);
            TreeSplitter.Visibility = Visibility.Collapsed;
        }
        else
        {
            TreeCol.MinWidth = _focusSavedTreeMinWidth;
            TreeCol.Width = _focusSavedTreeWidth;
            TreeSplitter.Visibility = Visibility.Visible;
        }

        // 页签头清空/恢复（页签条本身 WinUI 不支持整条隐藏，留白不带文字即满足「可见文本只剩编辑器与结果区」）
        foreach (var tab in WorkTabs.TabItems.OfType<TabViewItem>())
        {
            if (on) { _focusSavedHeaders[tab] = tab.Header; tab.Header = ""; }
            else if (_focusSavedHeaders.TryGetValue(tab, out var h)) { tab.Header = h; }
        }
        if (!on) _focusSavedHeaders.Clear();

        Core.FocusModeState.Save(on);
    }

    /// <summary>启动恢复：上次退出时处于专注态则原样进入（构造器在 RestoreSession 之后调用）。</summary>
    internal void RestoreFocusMode()
    {
        if (Core.FocusModeState.Load()) ApplyFocus(true);
    }
}
