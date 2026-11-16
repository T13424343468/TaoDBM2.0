using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>读写路由对话框：延迟感知的读路由决策预演。
/// 输入副本节点画像（名称、延迟秒、是否健康）与最大可接受延迟，
/// 引擎只做纯计算：健康且延迟达标的副本按延迟升序作为读节点，全部不达标自动回落主库；
/// 另给每个副本摘除 / 加回建议，供流量开关对照。全程只读，不改任何状态。</summary>
public static class ReadRouterDialog
{
    public static Task Show(MainWindow owner, NodeInfo info)
    {
        var status = new TextBlock { FontSize = 12, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var report = new StackPanel { Spacing = 10 };

        var nodesBox = new TextBox
        {
            AcceptsReturn = true,
            Height = 132,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Text = "副本A,3,1\n副本B,12,1\n副本C,2,0",
        };
        var lagBox = new TextBox { Header = "最大可接受延迟（秒）", Text = "10", Width = 160 };
        var routeBtn = new Button { Content = "路由一次读请求", CornerRadius = new CornerRadius(4) };
        var toggleBtn = new Button { Content = "摘除 / 加回建议", CornerRadius = new CornerRadius(4) };

        routeBtn.Click += async (_, _) => await RunRouteAsync(nodesBox.Text, lagBox.Text, report, status);
        toggleBtn.Click += async (_, _) => await RunToggleAsync(nodesBox.Text, lagBox.Text, report, status);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        btnRow.Children.Add(routeBtn);
        btnRow.Children.Add(toggleBtn);

        var panel = new StackPanel { Spacing = 12, MinWidth = 620 };
        panel.Children.Add(new TextBlock
        {
            Text = "每行一个副本：名称,延迟秒,健康(1/0)。引擎按健康且延迟达标的副本排序选读节点，全部不达标回落主库；纯计算，不触库。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ant.TextSub,
            FontSize = 12,
        });
        panel.Children.Add(nodesBox);
        panel.Children.Add(lagBox);
        panel.Children.Add(btnRow);
        panel.Children.Add(new ScrollViewer
        {
            Content = report,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "读写路由预演",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
        return Task.CompletedTask;
    }

    private static async Task RunRouteAsync(string nodesText, string lagText, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        var nodes = ParseNodes(nodesText, out var parseError);
        if (parseError.Length > 0)
        {
            status.Text = parseError;
            return;
        }
        if (!TryGetLag(lagText, out var maxLag, out var lagError))
        {
            status.Text = lagError;
            return;
        }

        var decision = ReadRouter.Route(nodes, maxLag);
        AddSection(report, "路由决策");
        if (!decision.HasTarget)
        {
            AddLine(report, "无可读节点：副本全部不可用且主库也不可用，请检查连接。", Ant.Error);
        }
        else if (decision.FellBackToPrimary)
        {
            AddLine(report, "本次读请求回落主库：没有健康且延迟达标的副本。", Ant.Warn);
        }
        else
        {
            AddLine(report, "命中副本（按延迟升序）：", Ant.Success);
            foreach (var name in decision.EligibleReplicas)
                AddLine(report, $"  {name}");
        }

        AddSection(report, "读流量分配建议");
        foreach (var (name, shouldServe) in ReadRouter.AutoToggle(nodes, maxLag))
            AddLine(report, $"  {name}：{(shouldServe ? "继续承接读流量" : "建议摘除读流量")}", shouldServe ? null : Ant.Warn);

        status.Text = "路由预演完成（纯计算，未触库）。";
        await Task.CompletedTask;
    }

    private static Task RunToggleAsync(string nodesText, string lagText, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        var nodes = ParseNodes(nodesText, out var parseError);
        if (parseError.Length > 0)
        {
            status.Text = parseError;
            return Task.CompletedTask;
        }
        if (!TryGetLag(lagText, out var maxLag, out var lagError))
        {
            status.Text = lagError;
            return Task.CompletedTask;
        }

        AddSection(report, $"摘除 / 加回建议（阈值 {maxLag} 秒）");
        foreach (var (name, shouldServe) in ReadRouter.AutoToggle(nodes, maxLag))
            AddLine(report, $"  {name}：{(shouldServe ? "加回读流量" : "摘除读流量")}", shouldServe ? Ant.Success : Ant.Warn);
        status.Text = "建议已生成（只读，不改流量开关）。";
        return Task.CompletedTask;
    }

    private static List<ReplicaNode> ParseNodes(string text, out string error)
    {
        error = "";
        var nodes = new List<ReplicaNode>();
        var lines = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || !long.TryParse(parts[1], out var lag))
            {
                error = "格式有误：" + line;
                return new List<ReplicaNode>();
            }
            bool healthy = parts[2] is "1" or "true" or "是" or "健康";
            nodes.Add(new ReplicaNode(parts[0], lag, healthy));
        }
        if (nodes.Count == 0) error = "请先填写至少一个副本节点。";
        return nodes;
    }

    private static bool TryGetLag(string text, out long maxLag, out string error)
    {
        maxLag = 0;
        error = "";
        if (!long.TryParse((text ?? "").Trim(), out maxLag) || maxLag < 0)
        {
            error = "最大延迟需为非负整数（秒）。";
            return false;
        }
        return true;
    }

    private static void AddLine(StackPanel p, string text, Brush? brush = null)
    {
        p.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = brush ?? Ant.TextSub,
            FontFamily = new FontFamily("Consolas"),
        });
    }

    private static void AddSection(StackPanel p, string title)
    {
        p.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ant.Primary,
            Margin = new Thickness(0, 6, 0, 0),
        });
    }
}
