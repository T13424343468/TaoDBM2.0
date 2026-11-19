using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace TaoDBM;

/// <summary>窗口本体尺寸与状态记忆（需求合并稿第十五章，2026-11-19 23:52 老板令）：
/// 默认=工作区80%封顶1600×900居中，小屏吃满；最小 960×640；退出保存/启动恢复；显示器不存在回默认居中。</summary>
public partial class MainWindow
{
    private const double MinWidthPx = 960;
    private const double MinHeightPx = 640;
    private const double MaxDefaultW = 1600;
    private const double MaxDefaultH = 900;
    private const double SmallScreenBreakpoint = 1350;

    /// <summary>启动时恢复窗口状态：有有效记录（显示器可见）恢复，否则默认居中。</summary>
    private void ApplyWindowState()
    {
        var saved = Core.WindowStateStore.Load();
        var area = GetWorkArea();
        double w, h;
        bool maximized = false;

        if (Core.WindowStateStore.IsRestorable(saved, new[] { (area.X, area.Y, area.Width, area.Height) }))
        {
            w = Math.Min(Math.Max(saved.Width, MinWidthPx), area.Width);
            h = Math.Min(Math.Max(saved.Height, MinHeightPx), area.Height);
            maximized = saved.Maximized;
        }
        else
        {
            (w, h) = Core.WindowStateStore.DefaultSize(area.Width, area.Height);
        }

        // 小屏（工作区宽<1350）直接吃满工作区
        if (area.Width < SmallScreenBreakpoint) { w = area.Width; h = area.Height; }

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter is null) return;

        if (maximized) { presenter.Maximize(); return; }   // 最大化态下不设尺寸，恢复时由系统还原

        // 非最大化：按记录（或默认）尺寸居中
        var restorable = Core.WindowStateStore.IsRestorable(saved, new[] { (area.X, area.Y, area.Width, area.Height) });
        var x = restorable && !double.IsNaN(saved.X) ? saved.X : area.X + (area.Width - w) / 2;
        var y = restorable && !double.IsNaN(saved.Y) ? saved.Y : area.Y + (area.Height - h) / 2;
        AppWindow.MoveAndResize(new RectInt32((int)x, (int)y, (int)w, (int)h));

        // 恢复分栏宽度与对象树收起态
        if (saved.LeftPaneWidth > 0)
            TreeCol.Width = new GridLength(Core.WindowStateStore.ClampLeftPane(saved.LeftPaneWidth, Content is FrameworkElement fe && fe.ActualWidth > 0 ? fe.ActualWidth : area.Width));
    }

    /// <summary>退出时保存窗口状态：位置/尺寸/最大化/分栏宽度/对象树收起态。</summary>
    private void SaveWindowState()
    {
        try
        {
            var presenter = AppWindow.Presenter as OverlappedPresenter;
            var rect = AppWindow.Position;
            var size = AppWindow.Size;
            var item = new Core.WindowStateItem
            {
                X = rect.X,
                Y = rect.Y,
                Width = size.Width,
                Height = size.Height,
                Maximized = presenter is { State: OverlappedPresenterState.Maximized },
                LeftPaneWidth = TreeCol.Width.IsAbsolute ? TreeCol.Width.Value : 300,
                TreeCollapsed = TreeCol.Width.Value <= 0.5,
            };
            if (item.Maximized)
            {
                // 最大化时保留上次正常态位置，只记状态（下次还原到正常尺寸）
                var prev = Core.WindowStateStore.Load();
                if (prev.Width >= MinWidthPx && prev.Height >= MinHeightPx)
                {
                    item.X = prev.X; item.Y = prev.Y; item.Width = prev.Width; item.Height = prev.Height;
                    item.LeftPaneWidth = prev.LeftPaneWidth > 0 ? prev.LeftPaneWidth : item.LeftPaneWidth;
                }
                else
                {
                    var area = GetWorkArea();
                    (item.Width, item.Height) = Core.WindowStateStore.DefaultSize(area.Width, area.Height);
                    item.X = double.NaN; item.Y = double.NaN;
                }
            }
            Core.WindowStateStore.Save(item);
        }
        catch (Exception ex) { Core.AppLog.Write($"窗口状态保存失败: {ex.Message}"); }
    }

    private (double X, double Y, double Width, double Height) GetWorkArea()
    {
        try
        {
            var dsp = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            return (dsp.WorkArea.X, dsp.WorkArea.Y, dsp.WorkArea.Width, dsp.WorkArea.Height);
        }
        catch
        {
            // 沙箱/无桌面环境兜底：不出错即可（真机走 DisplayArea）
            return (0, 0, 1920, 1080);
        }
    }

    private bool _enforcingMinSize;

    /// <summary>运行时最小尺寸 960×640（DIP）。桩内 OverlappedPresenter 未含 PreferredMinimumSize
    /// （投影二进制核验，真机 2.6.5 亦无此成员），按 SizeChanged 夹取兜底。</summary>
    internal void EnforceMinimumSize(object sender, Microsoft.UI.Xaml.WindowSizeChangedEventArgs args)
    {
        if (_enforcingMinSize) return;
        if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized) return;
        if (args.Size.Width >= MinWidthPx && args.Size.Height >= MinHeightPx) return;

        _enforcingMinSize = true;
        try
        {
            double scale = 1;
            if (Content is FrameworkElement fe && fe.RasterizationScale > 0) scale = fe.RasterizationScale;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)Math.Ceiling(Math.Max(MinWidthPx, args.Size.Width) * scale),
                (int)Math.Ceiling(Math.Max(MinHeightPx, args.Size.Height) * scale)));
        }
        finally { _enforcingMinSize = false; }
    }
}
