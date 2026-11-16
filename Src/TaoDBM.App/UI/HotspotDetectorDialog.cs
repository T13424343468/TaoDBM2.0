using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>热点倾斜检测对话框：输入各分片的负载画像（名称、负载值，口径自定），
/// 引擎按负载 >= 均值 3 倍判热点、不足均值一半判冷清，输出逐分片档位与处置建议；
/// 供再均衡、拆分或加盐前定位问题分片。纯计算，不触库。</summary>
public static class HotspotDetectorDialog
{
    public static Task Show(MainWindow owner, NodeInfo info)
    {
        var status = new TextBlock { FontSize = 12, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var report = new StackPanel { Spacing = 10 };

        var loadsBox = new TextBox
        {
            AcceptsReturn = true,
            Height = 132,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Text = "shard_0,1200\nshard_1,300\nshard_2,350\nshard_3,320",
        };
        var analyzeBtn = new Button { Content = "检测倾斜", CornerRadius = new CornerRadius(4) };
        analyzeBtn.Click += async (_, _) => await RunAnalyzeAsync(loadsBox.Text, report, status);

        var panel = new StackPanel { Spacing = 12, MinWidth = 620 };
        panel.Children.Add(new TextBlock
        {
            Text = "每行一个分片：名称,负载（行数或请求数，口径统一即可）。热点 = 负载不低于均值 3 倍，冷清 = 不足均值一半。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ant.TextSub,
            FontSize = 12,
        });
        panel.Children.Add(loadsBox);
        panel.Children.Add(analyzeBtn);
        panel.Children.Add(new ScrollViewer
        {
            Content = report,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "热点倾斜检测",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
        return Task.CompletedTask;
    }

    private static async Task RunAnalyzeAsync(string text, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        var shards = ParseLoads(text, out var parseError);
        if (parseError.Length > 0)
        {
            status.Text = parseError;
            return;
        }

        var rep = HotspotDetector.Analyze(shards);
        AddSection(report, $"整体结论：均值 {rep.MeanLoad:0.##}，峰值 {rep.PeakRatio:0.##} 倍" + (rep.HasHotSpot ? "（存在热点）" : ""));
        AddSection(report, "逐分片判定");
        foreach (var v in rep.Verdicts)
        {
            var brush = v.Level == "hot" ? Ant.Error : v.Level == "cold" ? Ant.Warn : (Brush?)null;
            AddLine(report, $"  {v.Name}  负载 {v.Load}  均值 {v.RatioToMean:0.##} 倍  [{v.Level}]", brush);
        }

        AddSection(report, "处置建议");
        foreach (var s in rep.Suggestions)
            AddLine(report, "  - " + s);

        status.Text = "检测完成（纯计算，未触库）。";
        await Task.CompletedTask;
    }

    private static List<ShardLoad> ParseLoads(string text, out string error)
    {
        error = "";
        var shards = new List<ShardLoad>();
        var lines = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], out var load))
            {
                error = "格式有误：" + line;
                return new List<ShardLoad>();
            }
            shards.Add(new ShardLoad(parts[0], load));
        }
        if (shards.Count == 0) error = "请先填写至少一个分片的负载数据。";
        return shards;
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
