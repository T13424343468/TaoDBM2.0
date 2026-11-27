using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;
using TaoDBM.Core.Streaming;

namespace TaoDBM.UI;

/// <summary>流式开关对话框：查看当前流式通道状态（DBM_STREAMING 环境变量口径），
/// 展示含义与回退方法。开关本身由环境变量决定，本对话框只读展示，不代改环境。</summary>
public static class StreamingSwitchDialog
{
    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        var prov = info.Provider ?? ProviderFactory.Create(info.Profile);
        bool enabled = StreamingSwitch.Enabled;
        var envRaw = Environment.GetEnvironmentVariable("DBM_STREAMING") ?? "(未设置，默认 on)";

        var report = new StackPanel { Spacing = 8 };
        AddSection(report, enabled ? "当前通道：流式（streaming）" : "当前通道：整包回退（legacy）");
        AddLine(report, $"DBM_STREAMING = {envRaw}");
        AddLine(report, "");
        AddLine(report, "流式通道：大结果集按批读取，内存占用与结果集大小解耦（推荐）。");
        AddLine(report, "整包回退：一次取回全部结果再处理，兼容旧路径，遇到兼容性问题可临时切换。");
        AddLine(report, "");
        AddLine(report, "切换方法：设置环境变量 DBM_STREAMING=off 后重启程序即回旧路径；");
        AddLine(report, "删除该变量或设为其他值则恢复流式。开关只影响查询执行方式，不动用户数据。");
        AddLine(report, $"当前引擎：{prov.EngineName}");

        var panel = new StackPanel { Spacing = 12, MinWidth = 640 };
        panel.Children.Add(new TextBlock
        {
            Text = "只读展示：流式开关由环境变量 DBM_STREAMING 决定，本页说明当前状态与切换方法。",
            TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor,
        });
        // 发现311原型：主开关卡——当前通道状态徽标（开关由环境变量决定，只读展示）
        panel.Children.Add(MasterCard(enabled));
        // 发现311原型：对比卡——关闭流式（一次性载入）vs 开启流式（逐批拉取）
        panel.Children.Add(CompareRow());
        // 发现311原型：提示条——PrimaryBg 底色主色描边
        panel.Children.Add(NoteBar());
        panel.Children.Add(report);

        var dlg = new ContentDialog
        {
            Title = "流式开关",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
        await Task.CompletedTask;
    }

    /// <summary>发现311原型：主开关卡——标题+说明+当前通道状态徽标（开启绿/关闭灰）。</summary>
    private static Border MasterCard(bool enabled)
    {
        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var txt = new StackPanel { Spacing = 2 };
        txt.Children.Add(new TextBlock { Text = "启用流式读取", FontSize = AntTokens.FontBody, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        txt.Children.Add(new TextBlock
        {
            Text = "超过阈值的结果集不再一次性载入内存，改为逐批拉取、边读边渲染",
            FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(txt);
        body.Children.Add(new TextBlock
        {
            Text = enabled ? "已开启" : "已关闭",
            FontSize = AntTokens.FontCaption,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = enabled ? Ant.Success : Ant.TextSub,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
            BorderThickness = new Thickness(1),
            BorderBrush = Ant.Border,
            Background = (Brush?)Application.Current.Resources["AntCard"],
            Child = body,
        };
    }

    /// <summary>发现311原型：对比卡——两列并排，优劣项 ✓ 绿 / ✕ 红。</summary>
    private static StackPanel CompareRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(CompareCol("关闭流式（一次性载入）", false, new[]
        {
            ("✕", "千万行结果集内存峰值 6.8 GB，易触发 OOM", false),
            ("✕", "首行显示需等全部取回（约 46 秒）", false),
            ("✓", "随机跳转任意行零延迟", true),
        }));
        row.Children.Add(CompareCol("开启流式（逐批拉取）", true, new[]
        {
            ("✓", "内存峰值稳定在 180 MB 以内", true),
            ("✓", "首行 0.3 秒可见，滚动按需加载", true),
            ("✕", "跳转到未加载区域需回源补批（约 80ms）", false),
        }));
        return row;
    }

    private static Border CompareCol(string header, bool on, (string Icon, string Text, bool Good)[] items)
    {
        var col = new StackPanel { Spacing = 4 };
        col.Children.Add(new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            Background = (Brush?)Application.Current.Resources["AntHeadBg"],
            Child = new TextBlock
            {
                Text = header,
                FontSize = AntTokens.FontMinor,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = on ? Ant.Primary : Ant.TextSub,
            },
        });
        foreach (var it in items)
        {
            col.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = {
                new TextBlock { Text = it.Icon, FontSize = AntTokens.FontMinor, Foreground = it.Good ? Ant.Success : Ant.Error },
                new TextBlock { Text = it.Text, FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap },
            } });
        }
        return new Border
        {
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
            BorderThickness = new Thickness(1),
            BorderBrush = Ant.Border,
            Child = col,
        };
    }

    /// <summary>发现311原型：提示条——PrimaryBg 底色、主色描边。</summary>
    private static Border NoteBar()
    {
        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        body.Children.Add(new TextBlock { Text = "提示", FontSize = AntTokens.FontMinor, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Ant.Primary });
        body.Children.Add(new TextBlock
        {
            Text = "阈值改动即时生效，不影响正在执行的查询；导出任务始终走流式，不受此开关影响。",
            FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap,
        });
        return new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusSm),
            Background = Ant.PrimaryBg,
            BorderThickness = new Thickness(1),
            BorderBrush = Ant.Primary,
            Child = body,
        };
    }

    private static void AddLine(StackPanel p, string text, Brush? brush = null)
    {
        p.Children.Add(new TextBlock
        {
            Text = text, FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap,
            Foreground = brush ?? Ant.TextSub, FontFamily = new FontFamily("Consolas"),
        });
    }

    private static void AddSection(StackPanel p, string title)
    {
        p.Children.Add(new TextBlock
        {
            Text = title, FontSize = AntTokens.FontBody, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ant.Primary, Margin = new Thickness(0, 6, 0, 0),
        });
    }
}
