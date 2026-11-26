using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>再均衡计划对话框：输入各分片当前负载（名称、负载值，口径自定），
/// 引擎按热点分片超出均值部分、冷清分片缺口贪心配对生成搬迁清单。
/// 本对话框只展示计划，不执行任何搬迁；实际搬数由执行层按对账防丢。</summary>
public static class RebalancePlannerDialog
{
    public static Task Show(MainWindow owner, NodeInfo info)
    {
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var report = new StackPanel { Spacing = 10 };

        var loadsBox = new TextBox
        {
            AcceptsReturn = true,
            Height = 132,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = AntTokens.FontMinor,
            Text = "shard_0,1200\nshard_1,300\nshard_2,350\nshard_3,320",
        };
        var planBtn = new Button { Content = "生成再均衡计划", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        planBtn.Click += async (_, _) => await RunPlanAsync(loadsBox.Text, report, status);

        var panel = new StackPanel { Spacing = 12, MinWidth = 620 };
        panel.Children.Add(new TextBlock
        {
            Text = "每行一个分片：名称,负载（行数或请求数，口径统一即可）。引擎生成搬迁清单，只展示计划，不执行搬迁。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ant.TextSub,
            FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(loadsBox);
        panel.Children.Add(planBtn);
        panel.Children.Add(new ScrollViewer
        {
            Content = report,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "再均衡计划",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
        return Task.CompletedTask;
    }

    private static async Task RunPlanAsync(string text, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        var shards = ParseLoads(text, out var parseError);
        if (parseError.Length > 0)
        {
            status.Text = parseError;
            return;
        }

        RebalancePlan plan;
        try
        {
            plan = RebalancePlanner.Plan(shards);
        }
        catch (ArgumentException ex)
        {
            // 发现122：输入校验（空名/负负载/重复名）人话报错，不崩不静默
            status.Text = ex.Message;
            return;
        }
        AddSection(report, "再均衡计划");
        if (plan.Balanced)
        {
            AddLine(report, "✓ 各分片负载已均衡，无需搬迁。", Ant.Success);
        }
        else if (plan.Moves.Count == 0)
        {
            // 发现123：有差距但整数搬迁无可行解时文案归真，不再假称「已均衡」
            AddLine(report, $"均值负载 {plan.MeanLoad:0.##}。各分片间仍有差距，但负载是整数、没有可行的整数搬迁方案（差距不足 1 的部分搬不动），无法靠搬迁进一步均衡。", Ant.Warn);
        }
        else
        {
            AddLine(report, $"均值负载 {plan.MeanLoad:0.##}，共 {plan.Moves.Count} 条搬迁建议：");
            foreach (var m in plan.Moves)
                AddLine(report, $"  从 {m.From} 搬 {m.Rows} 到 {m.To}", Ant.Warn);
            AddLine(report, "以上为建议清单，实际搬数由执行层按对账防丢执行，本工具不执行搬迁。", Ant.Primary);
        }

        status.Text = "计划已生成（只读，未执行任何搬迁）。";
        await Task.CompletedTask;
    }

    private static List<RebalanceShard> ParseLoads(string text, out string error)
    {
        error = "";
        var shards = new List<RebalanceShard>();
        var lines = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], out var load))
            {
                error = "格式有误：" + line;
                return new List<RebalanceShard>();
            }
            shards.Add(new RebalanceShard(parts[0], load));
        }
        if (shards.Count == 0) error = "请先填写至少一个分片的负载数据。";
        return shards;
    }

    private static void AddLine(StackPanel p, string text, Brush? brush = null)
    {
        p.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = AntTokens.FontMinor,
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
            FontSize = AntTokens.FontBody,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ant.Primary,
            Margin = new Thickness(0, 6, 0, 0),
        });
    }
}
