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
