using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TaoDBM.UI;

/// <summary>操作手册窗口（2026-11-20 老板令「操作手册」功能）：左侧章节目录 + 右侧正文，
/// 内容离线内置（ManualContent，纯数据同源校验）。入口：顶栏「手册」、左上菜单「操作手册」、全局 F1。
/// 浅色主题遵守视觉对稿纪律，随当前主题象钉定。</summary>
public static class ManualDialog
{
    public static void Show(MainWindow owner)
    {
        var list = new ListBox
        {
            Width = 208,
            Margin = new Thickness(0, 0, 14, 0),
            SelectionMode = SelectionMode.Single,
        };
        foreach (var ch in ManualContent.Chapters)
        {
            list.Items.Add(new ListBoxItem
            {
                Content = new TextBlock { Text = ch.Title, TextWrapping = TextWrapping.Wrap, FontSize = AntTokens.FontBody },
                Padding = new Thickness(10, 7, 6, 7),
            });
        }

        var bodyHost = new StackPanel { Spacing = 12 };
        var scroll = new ScrollViewer
        {
            Content = bodyHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(4, 0, 8, 0),
        };

        var grid = new Grid { MinHeight = 560 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(list, 0);
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(list);
        grid.Children.Add(scroll);

        list.SelectionChanged += (_, _) =>
        {
            bodyHost.Children.Clear();
            if (list.SelectedIndex < 0 || list.SelectedIndex >= ManualContent.Chapters.Length) return;
            var ch = ManualContent.Chapters[list.SelectedIndex];
            foreach (var sec in ch.Sections)
            {
                bodyHost.Children.Add(new TextBlock
                {
                    Text = sec.Heading,
                    FontSize = AntTokens.FontTitle,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Ant.PrimaryDark,
                    Margin = new Thickness(0, 8, 0, 0),
                });
                foreach (var line in sec.Body.Split('\n'))
                {
                    bool bullet = line.StartsWith("· ", StringComparison.Ordinal);
                    bool note = line.StartsWith('！');
                    bodyHost.Children.Add(new TextBlock
                    {
                        Text = bullet ? line[2..] : line,
                        FontSize = AntTokens.FontBody,
                        Foreground = note ? Ant.PrimaryDark : Ant.Text,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(bullet || note ? 18 : 0, 0, 0, 0),
                        LineHeight = 22,
                    });
                }
            }
            scroll.ChangeView(null, 0, null);
        };

        var dlg = new ContentDialog
        {
            Title = "操作手册（F1 随时打开）",
            Content = grid,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        dlg.Resources["ContentDialogMaxWidth"] = 1080d;
        dlg.Resources["ContentDialogMaxHeight"] = 760d;
        list.SelectedIndex = 0;
        _ = dlg.ShowThemedAsync();
    }
}
