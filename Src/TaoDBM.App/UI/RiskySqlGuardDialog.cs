using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>高危 SQL 拦截对话框：粘贴脚本逐条过 RiskySqlGuard，按危险/警告/通过分级展示，
/// 附行数预估 SQL。本对话框只做检查与预览，不代执行任何语句。</summary>
public static class RiskySqlGuardDialog
{
    public static FrameworkElement BuildPanel(MainWindow owner, NodeInfo info)
    {
        var prov = info.Provider ?? ProviderFactory.Create(info.Profile);
        var input = new TextBox
        {
            PlaceholderText = "粘贴待执行的 SQL 脚本，逐条检查风险（DROP / TRUNCATE / 无 WHERE 更新删除等）",
            MinHeight = 140,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
        };
        var report = new StackPanel { Spacing = 8 };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        var checkBtn = new Button { Content = "开始检查", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        checkBtn.Click += async (_, _) =>
        {
            var script = input.Text ?? "";
            if (script.Trim().Length == 0) { status.Text = "请先粘贴 SQL 脚本。"; return; }
            status.Text = "检查中…";
            report.Children.Clear();
            try
            {
                var engine = prov.Profile.Engine;
                var checks = RiskySqlGuard.InspectScript(script, engine, prov.Quote);
                if (checks.Count == 0) { status.Text = "没有可检查的语句。"; return; }
                var worst = RiskySqlGuard.Worst(checks);
                AddSection(report, $"共 {checks.Count} 条语句，最危险档位：{WorstLabel(worst.Level)}");
                foreach (var c in checks)
                {
                    var brush = c.Level switch { "danger" => Ant.Error, "warn" => Ant.Primary, _ => (Brush?)null };
                    var head = c.Level == "ok" ? "[通过]" : $"[{c.Level}·{c.Kind}]";
                    AddLine(report, $"{head} {Trunc(c.Skeleton, 160)}", brush);
                    if (c.Level != "ok")
                    {
                        AddLine(report, $"  说明：{c.Detail}", brush, 1);
                        if (!string.IsNullOrEmpty(c.Table)) AddLine(report, $"  涉及表：{c.Table}", brush, 1);
                        if (!string.IsNullOrEmpty(c.EstimateSql))
                            AddLine(report, $"  行数预估（可复制到查询窗口执行）：{c.EstimateSql}", null, 1);
                    }
                }
                status.Text = worst.Level == "danger"
                    ? "检查完成：存在高危语句，请逐条确认后再执行。"
                    : worst.Level == "warn" ? "检查完成：存在警告项，建议复核。" : "检查完成：全部通过。";
            }
            catch (Exception ex) { status.Text = HumanError.Say(ex, "检查失败"); }
            await Task.CompletedTask;
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 680 };
        panel.Children.Add(new TextBlock
        {
            Text = "只检查不执行：逐条标注风险档位与原因；行数预估语句请自行复制到查询窗口查看。",
            TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(input);
        panel.Children.Add(checkBtn);
        panel.Children.Add(new ScrollViewer { Content = report, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(status);
        return panel;
    }

    private static string WorstLabel(string level) => level switch
    {
        "danger" => "高危",
        "warn" => "警告",
        _ => "通过",
    };

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static void AddLine(StackPanel p, string text, Brush? brush = null, int indent = 0)
    {
        p.Children.Add(new TextBlock
        {
            Text = text, FontSize = AntTokens.FontMinor, Margin = new Thickness(indent * 16, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap, Foreground = brush ?? Ant.TextSub,
            FontFamily = new FontFamily("Consolas"),
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
