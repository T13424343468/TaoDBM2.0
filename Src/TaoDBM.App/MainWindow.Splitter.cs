using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace TaoDBM;

/// <summary>对象树↔主区分栏拖动条（#135 修红）：
/// WinUI 3 无内置 GridSplitter，工程亦不引 CommunityToolkit，改为零依赖 Border 自绘拖动；
/// 拖完即存（SaveWindowState 在 WindowStateLifecycle partial），重启按第十五章恢复。</summary>
public partial class MainWindow
{
    private bool _splitterDragging;
    private double _splitterStartX;
    private double _splitterStartWidth;

    private void OnSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        fe.CapturePointer(e.Pointer);
        _splitterDragging = true;
        _splitterStartX = e.GetCurrentPoint(fe).Position.X;
        _splitterStartWidth = TreeCol.Width.IsAbsolute ? TreeCol.Width.Value : TreeCol.ActualWidth;
    }

    private void OnSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging || sender is not FrameworkElement fe) return;
        double dx = e.GetCurrentPoint(fe).Position.X - _splitterStartX;
        double maxW = 480;
        if (WorkTabs.Parent is Grid inner && inner.ActualWidth > 0)
            maxW = inner.ActualWidth - 480 - fe.ActualWidth;
        TreeCol.Width = new GridLength(Math.Clamp(_splitterStartWidth + dx, 200, Math.Max(200, maxW)));
    }

    private void OnSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        _splitterDragging = false;
        if (sender is FrameworkElement fe) fe.ReleasePointerCapture(e.Pointer);
        SaveWindowState();
    }
}
