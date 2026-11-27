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

        // 整改三批三-11（2026-11-24 老板令「抓紧时间」）：演示式接真实数据——候选键画像从真实表结构采样，
        // 不再只靠手填。只读查询（COUNT/COUNT DISTINCT），带行数上限保护，失败如实提示不阻断手填路径。
        var sampleTableBox = new TextBox { PlaceholderText = "真实表名（留空用当前上下文）", MinWidth = 220 };
        var sampleBtn = new Button { Content = "从真实表采样画像", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        sampleBtn.Click += async (_, _) => await RunRealSampleAsync(owner, info, sampleTableBox.Text, candBox, report, status);
        var sampleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sampleRow.Children.Add(sampleTableBox);
        sampleRow.Children.Add(sampleBtn);

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
        panel.Children.Add(new TextBlock
        {
            Text = "接真实数据：填表名（留空用当前上下文表）点采样，画像按真实行数/去重数生成后回填候选框。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ant.TextSub,
            FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(sampleRow);
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

    /// <summary>三-11：真实表采样——读真实列结构，对每列跑只读 COUNT/COUNT DISTINCT 生成画像行回填候选框。</summary>
    private static async Task RunRealSampleAsync(MainWindow owner, NodeInfo info, string tableText, TextBox candBox, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        try
        {
            var provider = info.Provider ?? ProviderFactory.Create(info.Profile);
            string table = (tableText ?? "").Trim();
            if (table.Length == 0 && info.Kind == NodeKind.Table) table = info.Name;
            if (table.Length == 0) { status.Text = "请先填表名（或选中表后再进本窗口）。"; return; }
            string db = info.Kind == NodeKind.Table ? info.Db : (info.Db.Length > 0 ? info.Db : info.Name);
            var cols = await provider.GetColumns(db, table);
            if (cols.Count == 0) { status.Text = $"未读到 {table} 的列结构。"; return; }

            var lines = new List<string>();
            int sampled = 0;
            foreach (var c in cols)
            {
                if (sampled >= 8) break;   // 采样上限：前 8 列，避免大表全列扫描
                string q = provider.Quote(c.Name);
                QueryResult? stat = null;
                try
                {
                    var rs = await provider.Execute(db, $"SELECT COUNT(*) AS cnt, COUNT(DISTINCT {q}) AS dist FROM {provider.Quote(table)}");
                    stat = rs.FirstOrDefault(x => x.Rows.Count > 0);
                }
                catch { /* 单列采样失败不阻断其余列 */ }
                if (stat is null) continue;
                long total = Convert.ToInt64(stat.Rows[0][0]);
                long dist = Convert.ToInt64(stat.Rows[0][1]);
                if (total == 0) continue;
                double skew = Math.Round((double)total / Math.Max(dist, 1), 2);
                // 谓词命中/单调不可廉价采样，如实填 0 并在说明里标注「未采样」
                lines.Add($"{c.Name},{dist},{skew},0,0");
                sampled++;
            }
            if (lines.Count == 0) { status.Text = "采样未得到可用画像（空表或列均不可采样）。"; return; }
            candBox.Text = string.Join("\n", lines);
            AddSection(report, $"真实采样：{table}（前 {sampled} 列）");
            AddLine(report, "  画像已回填候选框：倾斜比＝平均重复度（行数/去重数）；谓词命中与单调未采样，填 0。");
            status.Text = "采样完成，点「评估分片键」出结论。";
            AppLog.Write($"[分片键采样] table={table} cols={sampled}");
        }
        catch (Exception ex)
        {
            status.Text = HumanError.Say(ex, "采样失败");
            AppLog.Write($"[分片键采样] error={ex.Message}");
        }
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

        // 发现308原型：结论卡——首键结论成卡，可用绿 / 待斟酌黄
        var top = verdicts[0];
        bool ok = top.Score >= 80;
        report.Children.Add(new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg),
            BorderThickness = new Thickness(1),
            BorderBrush = ok ? Ant.Success : Ant.Warn,
            Background = ok ? (Brush?)Ant.SuccessBg : (Brush?)Ant.WarnBg,
            Child = new TextBlock
            {
                Text = $"{(ok ? "✓" : "⚠")} 结论：{top.Column} 得分 {top.Score}/100 · 建议 {top.SuggestedStrategy}" + (verdicts.Count > 1 ? "；其余键见逐键结论。" : ""),
                FontSize = AntTokens.FontMinor,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ok ? (Brush?)Ant.Success : (Brush?)Ant.Warn,
                TextWrapping = TextWrapping.Wrap,
            },
        });

        // 发现308原型：逐键结论表——候选键 / 得分 / 建议策略 / 问题，表头底色
        AddSection(report, "逐键结论");
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        string[] headers = { "候选键", "得分", "建议策略", "问题" };
        for (int c = 0; c < 4; c++)
        {
            var headCell = new Border
            {
                Padding = new Thickness(0, 4, 0, 4),
                Background = Ant.HeadBg,
                Child = new TextBlock
                {
                    Text = headers[c],
                    FontSize = AntTokens.FontMinor,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Ant.TextSub,
                },
            };
            Grid.SetColumn(headCell, c);
            grid.Children.Add(headCell);
        }
        for (int r = 0; r < verdicts.Count; r++)
        {
            var v = verdicts[r];
            grid.RowDefinitions.Add(new RowDefinition());
            var name = new TextBlock { Text = v.Column, FontSize = AntTokens.FontMinor, FontFamily = new FontFamily("Consolas") };
            var scoreBrush = v.Score >= 80 ? Ant.Success : v.Score >= 40 ? Ant.Warn : Ant.Error;
            var score = new TextBlock { Text = $"{v.Score}/100", FontSize = AntTokens.FontMinor, Foreground = scoreBrush };
            var strategy = new TextBlock { Text = v.SuggestedStrategy, FontSize = AntTokens.FontMinor };
            var issues = new TextBlock
            {
                Text = v.Issues.Count > 0 ? string.Join("；", v.Issues) : "—",
                FontSize = AntTokens.FontMinor,
                Foreground = Ant.TextSub,
                TextWrapping = TextWrapping.Wrap,
            };
            var cells = new UIElement[] { name, score, strategy, issues };
            for (int c = 0; c < 4; c++)
            {
                var cell = new Border { Padding = new Thickness(0, 4, 0, 4), Child = cells[c] };
                Grid.SetColumn(cell, c);
                Grid.SetRow(cell, r + 1);
                grid.Children.Add(cell);
            }
        }
        report.Children.Add(grid);
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
        // 发现308原型：落点带——分片格一字排开，命中格主色高亮
        var band = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        for (int i = 0; i < shardCount && i < 16; i++)
        {
            bool hit = i == shard;
            band.Children.Add(new Border
            {
                Width = 28,
                Height = 24,
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusSm),
                Background = hit ? (Brush?)Ant.Primary : (Brush?)Ant.Bg,
                BorderThickness = new Thickness(1),
                BorderBrush = hit ? Ant.Primary : Ant.Border,
                Child = new TextBlock
                {
                    Text = i.ToString(),
                    FontSize = AntTokens.FontCaption,
                    Foreground = hit ? Ant.OnPrimary : Ant.TextSub,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
        report.Children.Add(band);
        if (shardCount > 16) AddLine(report, $"（仅展示前 16 个分片格，共 {shardCount} 片）");
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
