using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>上线审查对话框：对粘贴的脚本跑 SqlReleaseCheck 评分与逐条发现，只读审查，
/// 不执行脚本。结论档位（可上线/需复核/不建议）按引擎评分给出。</summary>
public static class SqlReleaseCheckDialog
{
    public static FrameworkElement BuildPanel(MainWindow owner, NodeInfo info)
    {
        var prov = info.Provider ?? ProviderFactory.Create(info.Profile);
        var input = new TextBox
        {
            PlaceholderText = "粘贴准备上线的 SQL 脚本，自动评分并逐条列出问题",
            MinHeight = 140,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
        };
        var report = new StackPanel { Spacing = 8 };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        var btn = new Button { Content = "开始审查", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        btn.Click += async (_, _) =>
        {
            var script = input.Text ?? "";
            if (script.Trim().Length == 0) { status.Text = "请先粘贴脚本。"; return; }
            status.Text = "审查中…";
            report.Children.Clear();
            try
            {
                var (score, findings) = SqlReleaseCheck.Review(script, prov.Profile.Engine);
                var verdict = SqlReleaseCheck.Verdict(score);
                AddSection(report, $"得分 {score} —— {verdict}");
                if (findings.Count == 0)
                {
                    AddLine(report, "没有发现问题。");
                }
                else
                {
                    foreach (var f in findings)
                    {
                        var brush = f.Level == "error" ? Ant.Error : f.Level == "warn" ? Ant.Primary : (Brush?)null;
                        AddLine(report, $"[{f.Level}·{f.Rule}] {f.Message}", brush);
                        if (!string.IsNullOrEmpty(f.Snippet)) AddLine(report, $"  {f.Snippet}", brush, 1);
                    }
                }
                status.Text = "审查完成：本对话框只读审查，不执行脚本。";
            }
            catch (Exception ex) { status.Text = HumanError.Say(ex, "审查失败"); }
            await Task.CompletedTask;
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 680 };
        // 507 原型：面板标题行
        panel.Children.Add(new TextBlock { Text = "SQL 上线检查", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = AntTokens.FontBody });
        panel.Children.Add(new TextBlock
        {
            Text = "只读审查：按评分给结论（可上线 / 需复核 / 不建议），逐条列出发现项，不执行任何语句。",
            TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(input);
        panel.Children.Add(btn);
        panel.Children.Add(new ScrollViewer { Content = report, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(status);

        return panel;
    }

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
