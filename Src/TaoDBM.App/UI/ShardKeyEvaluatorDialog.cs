using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>分片键评估对话框：输入候选分片键画像（列名、基数、倾斜比、谓词命中、是否单调），
/// 引擎给出得分、扣分问题与建议分片策略（hash/range）；并提供哈希 / 范围路由预演，
/// 预演某行数据的落点。纯计算，不触库，不产生任何写操作。</summary>
public static class ShardKeyEvaluatorDialog
{
    public static Task Show(MainWindow owner, NodeInfo info)
    {
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var report = new StackPanel { Spacing = 10 };

        var candBox = new TextBox
        {
            AcceptsReturn = true,
            Height = 120,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = AntTokens.FontMinor,
            Text = "user_id,1000000,1.2,5000,0\norder_time,50000,2.0,3000,1\ncity,300,1.1,0,0",
        };
        var evalBtn = new Button { Content = "评估分片键", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };

        var hashKeyBox = new TextBox { PlaceholderText = "路由键值，如 user_123", MinWidth = 180 };
        var shardCountBox = new TextBox { PlaceholderText = "分片数", MinWidth = 80, Text = "8" };
        var hashBtn = new Button { Content = "哈希落点", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };

        var rangeValueBox = new TextBox { PlaceholderText = "路由数值", MinWidth = 100 };
        var rangeBoundBox = new TextBox { PlaceholderText = "上界,如 100,200,300", MinWidth = 180 };
        var rangeBtn = new Button { Content = "范围落点", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };

        evalBtn.Click += async (_, _) => await RunEvaluateAsync(candBox.Text, report, status);
        hashBtn.Click += async (_, _) => await RunHashAsync(hashKeyBox.Text, shardCountBox.Text, report, status);
        rangeBtn.Click += async (_, _) => await RunRangeAsync(rangeValueBox.Text, rangeBoundBox.Text, report, status);

        var hashRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        hashRow.Children.Add(hashKeyBox);
        hashRow.Children.Add(shardCountBox);
        hashRow.Children.Add(hashBtn);

        var rangeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rangeRow.Children.Add(rangeValueBox);
        rangeRow.Children.Add(rangeBoundBox);
        rangeRow.Children.Add(rangeBtn);

        var panel = new StackPanel { Spacing = 12, MinWidth = 640 };
        panel.Children.Add(new TextBlock
        {
            Text = "候选键每行一条：列名,基数,倾斜比,谓词命中,单调(1/0)。评估只依据所填画像，不做实时采样；路由预演不触库。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ant.TextSub,
            FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(candBox);
        panel.Children.Add(evalBtn);
        panel.Children.Add(hashRow);
        panel.Children.Add(rangeRow);
        panel.Children.Add(new ScrollViewer
        {
            Content = report,
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "分片键评估",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
        return Task.CompletedTask;
    }

    private static async Task RunEvaluateAsync(string text, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        var candidates = ParseCandidates(text, out var parseError);
        if (parseError.Length > 0)
        {
            status.Text = parseError;
            return;
        }

        var verdicts = ShardKeyEvaluator.Evaluate(candidates);
        AddSection(report, "候选键评估（按得分降序）");
        foreach (var v in verdicts)
        {
            var brush = v.Score >= 80 ? Ant.Success : v.Score >= 40 ? (Brush?)null : Ant.Error;
            AddLine(report, $"  {v.Column}  得分 {v.Score}/100  建议 {v.SuggestedStrategy}", brush);
            foreach (var issue in v.Issues)
                AddLine(report, $"      · {issue}");
        }
        status.Text = "评估完成。";
        await Task.CompletedTask;
    }

    private static async Task RunHashAsync(string key, string countText, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        key = (key ?? "").Trim();
        if (key.Length == 0) { status.Text = "请先填写路由键值。"; return; }
        if (!int.TryParse((countText ?? "").Trim(), out var shardCount) || shardCount <= 0)
        {
            status.Text = "分片数需为正整数。";
            return;
        }

        int shard = ShardKeyEvaluator.HashRoute(key, shardCount);
        AddSection(report, "哈希路由预演");
        AddLine(report, $"键值 {key} → 分片 {shard}（共 {shardCount} 个分片，下标从 0 起）");
        status.Text = "哈希路由完成。";
        await Task.CompletedTask;
    }

    private static async Task RunRangeAsync(string valueText, string boundText, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        if (!long.TryParse((valueText ?? "").Trim(), out var value))
        {
            status.Text = "路由数值需为整数。";
            return;
        }
        var boundaries = new List<long>();
        foreach (var part in (boundText ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, out var b))
            {
                status.Text = "分片上界需为整数，逗号分隔且升序。";
                return;
            }
            boundaries.Add(b);
        }
        if (boundaries.Count == 0)
        {
            status.Text = "请先填写各分片上界。";
            return;
        }

        int index = ShardKeyEvaluator.RangeRoute(value, boundaries);
        AddSection(report, "范围路由预演");
        if (index < 0)
            AddLine(report, $"数值 {value} 超出全部上界，需扩分片或改用哈希。", Ant.Warn);
        else
            AddLine(report, $"数值 {value} → 分片 {index}（上界 {boundaries[index]}）");
        status.Text = "范围路由完成。";
        await Task.CompletedTask;
    }

    private static List<ShardKeyCandidate> ParseCandidates(string text, out string error)
    {
        error = "";
        var candidates = new List<ShardKeyCandidate>();
        var lines = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 5
                || !long.TryParse(parts[1], out var cardinality)
                || !double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var skew)
                || !long.TryParse(parts[3], out var hits))
            {
                error = "格式有误：" + line;
                return new List<ShardKeyCandidate>();
            }
            bool monotonic = parts[4] is "1" or "true" or "是";
            candidates.Add(new ShardKeyCandidate(parts[0], cardinality, skew, hits, monotonic));
        }
        if (candidates.Count == 0) error = "请先填写至少一个候选分片键。";
        return candidates;
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
