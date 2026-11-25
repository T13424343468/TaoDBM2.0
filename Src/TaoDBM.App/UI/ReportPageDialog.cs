// ㉘ 报表页（2026-11-25 设计稿冻结）：点选出图即钉住的零配置看板——竞品要先建仪表板工程才能出图，
// TaoDBM 结果区右键「钉到报表页」即出图。多卡网格布局（DashboardDialog 骨架），卡片视觉走 KanbanDialog 格19 重建口径：
// 刷新＝重跑同一 SQL 重新聚合重绘；图型/聚合列卡上可手动切换（切换即落盘）；布局落 report-page.json 重开保持。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>报表页：多卡图表聚合面，两列网格布局，卡片配置持久化（ReportPageStore）。</summary>
public static class ReportPageDialog
{
    public static void Show(MainWindow owner, NodeInfo info)
    {
        var grid = new Grid { MinWidth = 680 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(grid, "ReportPage");
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        var panel = new StackPanel { Spacing = 8, MinWidth = 700, MinHeight = 560 };
        panel.Children.Add(status);
        panel.Children.Add(new Border
        {
            Child = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            BorderBrush = Ant.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg),
            Height = 560,
        });

        var dlg = new ContentDialog
        {
            Title = "报表页（点选出图即钉住）",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };

        // 打开即重建：所有卡片按钉住清单重跑重绘（格19 重建口径）
        dlg.Opened += async (_, _) => await RefreshAllAsync(info, grid, status);
        _ = dlg.ShowThemedAsync();
    }

    /// <summary>按钉住清单重建两列网格，并逐卡刷新。</summary>
    private static async Task RefreshAllAsync(NodeInfo info, Grid grid, TextBlock status)
    {
        var cards = ReportPageStore.All();
        AppLog.Write($"[报表页] 卡={cards.Count}");   // ㉘ 验收锚点
        if (cards.Count == 0)
        {
            grid.Children.Clear();
            grid.Children.Add(new TextBlock
            {
                Text = "报表页还是空的：在查询结果的列上右键 →「钉到报表页」，点选列名即出图。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Ant.TextSub,
                Margin = new Thickness(12, 10, 0, 0),
            });
            status.Text = "没有钉住的卡片。";
            return;
        }

        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        int done = 0, failed = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            int row = i / 2, col = i % 2;
            if (col == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var chartHost = new StackPanel { Spacing = 4, MinHeight = 40 };
            chartHost.Children.Add(new TextBlock { Text = "刷新中…", FontSize = AntTokens.FontBody, Foreground = Ant.TextSub });

            var typeCombo = new ComboBox { Width = 96, FontSize = AntTokens.FontMinor, SelectedIndex = TypeIndex(card.NormalizedType) };
            foreach (var t in new[] { "柱状", "直方", "折线" }) typeCombo.Items.Add(t);
            var colCombo = new ComboBox { Width = 120, FontSize = AntTokens.FontMinor };
            var refreshBtn = new Button { Content = "刷新", FontSize = AntTokens.FontMinor, Padding = new Thickness(10, 2, 10, 2) };
            var removeBtn = new Button { Content = "移除", FontSize = AntTokens.FontMinor, Padding = new Thickness(10, 2, 10, 2) };

            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            controls.Children.Add(typeCombo);
            controls.Children.Add(colCombo);
            controls.Children.Add(refreshBtn);
            controls.Children.Add(removeBtn);

            var cardPanel = new StackPanel { Spacing = 6 };
            cardPanel.Children.Add(new TextBlock
            {
                Text = card.Title,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Ant.Primary,
                TextWrapping = TextWrapping.Wrap,
            });
            cardPanel.Children.Add(chartHost);
            cardPanel.Children.Add(controls);

            var box = new Border
            {
                Child = cardPanel,
                BorderBrush = Ant.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, col == 0 ? 6 : 0, 8),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, $"ReportCard_{card.CardId}");
            Grid.SetRow(box, row);
            Grid.SetColumn(box, col);
            grid.Children.Add(box);

            // 图型/聚合列切换即落盘并重绘（卡上手动切换口径；发现67：程序重填期间 Tag 摘钩，防 SelectionChanged 回环重跑 SQL）
            typeCombo.SelectionChanged += async (_, _) =>
            {
                if (typeCombo.Tag is true || typeCombo.SelectedIndex < 0) return;
                var updated = card with { ChartType = TypeNameOf(typeCombo.SelectedIndex) };
                ReportPageStore.Upsert(updated);
                await RefreshCardAsync(info, updated, chartHost, colCombo, typeCombo);
            };
            colCombo.SelectionChanged += async (_, _) =>
            {
                if (colCombo.Tag is true) return;   // 发现67：FillCardAsync 重填聚合列触发的 SelectionChanged 不重跑
                if (colCombo.SelectedItem is not string c || c.Length == 0) return;
                var updated = card with { Column = c };
                ReportPageStore.Upsert(updated);
                await RefreshCardAsync(info, updated, chartHost, colCombo, typeCombo);
            };
            refreshBtn.Click += async (_, _) => await RefreshCardAsync(info, card, chartHost, colCombo, typeCombo);
            removeBtn.Click += (_, _) =>
            {
                ReportPageStore.Remove(card.CardId);
                grid.Children.Remove(box);
                AppLog.Write($"[报表页] 移除 卡={card.CardId}");
            };

            try
            {
                await FillCardAsync(info, card, chartHost, colCombo, typeCombo);
                done++;
            }
            catch (Exception ex)
            {
                chartHost.Children.Clear();
                chartHost.Children.Add(new TextBlock { Text = HumanError.Say(ex, "刷新失败"), FontSize = AntTokens.FontBody, Foreground = Ant.Error, TextWrapping = TextWrapping.Wrap });
                failed++;
            }
        }
        status.Text = $"共 {cards.Count} 张卡：出图成功 {done}，失败 {failed}（{DateTimeOffset.Now:HH:mm:ss}）。";
    }

    /// <summary>单卡刷新（格19 重建口径）：重跑 SQL → 按图型/聚合列重新聚合 → 重绘。</summary>
    private static async Task RefreshCardAsync(NodeInfo info, ReportPageStore.Card card, StackPanel chartHost, ComboBox colCombo, ComboBox typeCombo)
    {
        chartHost.Opacity = 0.6;
        try
        {
            await FillCardAsync(info, card, chartHost, colCombo, typeCombo);
        }
        catch (Exception ex)
        {
            chartHost.Opacity = 1.0;
            chartHost.Children.Clear();
            chartHost.Children.Add(new TextBlock { Text = HumanError.Say(ex, "刷新失败"), FontSize = AntTokens.FontBody, Foreground = Ant.Error, TextWrapping = TextWrapping.Wrap });
        }
    }

    /// <summary>执行 SQL 并按卡配置聚合出图；聚合列下拉按结果列重填（保持当前选择）。</summary>
    private static async Task FillCardAsync(NodeInfo info, ReportPageStore.Card card, StackPanel chartHost, ComboBox colCombo, ComboBox typeCombo)
    {
        if (info.Provider is null) throw new InvalidOperationException("无活动连接，暂不能刷新；连上库再打开报表页。");
        var list = await info.Provider.Execute(card.Db ?? "", card.Sql);
        var r = list.FirstOrDefault(x => x.Rows.Count > 0) ?? list.FirstOrDefault();
        if (r is null || r.Rows.Count == 0)
        {
            string msg = r is null || string.IsNullOrEmpty(r.Message) ? "查询成功，0 行。" : r.Message;
            chartHost.Opacity = 1.0;
            chartHost.Children.Clear();
            chartHost.Children.Add(new TextBlock { Text = msg, FontSize = AntTokens.FontBody, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });
            return;
        }

        // 聚合列下拉：结果列全量，当前选择优先（卡上可手动切换聚合列；发现67：重填前 Tag 摘钩，填完恢复）
        string current = colCombo.SelectedItem as string ?? card.Column;
        if (!r.Columns.Contains(current)) current = r.Columns.Contains(card.Column) ? card.Column : r.Columns[0];
        int keep = colCombo.SelectedIndex;
        colCombo.Tag = true;
        try
        {
            colCombo.Items.Clear();
            foreach (var c in r.Columns) colCombo.Items.Add(c);
            colCombo.SelectedIndex = Math.Max(keep, r.Columns.IndexOf(current));
        }
        finally { colCombo.Tag = null; }   // 发现67：异常也恢复摘钩，不卡死手动切换

        string type = typeCombo.SelectedIndex >= 0 ? TypeNameOf(typeCombo.SelectedIndex) : card.NormalizedType;
        var spec = type switch
        {
            "histogram" => ChartSpecBuilder.Histogram(r, current),
            "line" => ChartSpecBuilder.TimeSeries(r, current),
            _ => ChartSpecBuilder.CategoryCount(r, current),
        };

        chartHost.Opacity = 1.0;
        chartHost.Children.Clear();
        chartHost.Children.Add(RenderChart(spec, type));
        string ts = DateTimeOffset.Now.ToString("HH:mm:ss");
        chartHost.Children.Add(new TextBlock { Text = $"{spec.Extra} · {ts}", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });
    }

    /// <summary>ChartSpec 出图（零包自绘）：柱状/直方横条，折线 Polyline 归一化。</summary>
    private static FrameworkElement RenderChart(ChartSpec s, string type)
    {
        if (s.Labels.Count == 0)
            return new TextBlock { Text = "无可聚合数据。", Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        if (type == "line" && s.Labels.Count >= 2)
        {
            double max = Math.Max(s.Values.Max(), 1e-9);
            const double w = 320, h = 120, pad = 8;
            var pts = new PointCollection();
            for (int i = 0; i < s.Values.Count; i++)
            {
                double x = pad + i * (w - 2 * pad) / Math.Max(s.Values.Count - 1, 1);
                double y = h - pad - (s.Values[i] / max) * (h - 2 * pad);
                pts.Add(new Windows.Foundation.Point(x, y));
            }
            var canvas = new Canvas { Width = w, Height = h };
            canvas.Children.Add(new Polyline
            {
                Points = pts,
                Stroke = Ant.Primary,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
            });
            return canvas;
        }

        // 横条：标签列＋比例条（TopN 已由聚合层截断，条数可控）
        double maxV = Math.Max(s.Values.Max(), 1e-9);
        var sp = new StackPanel { Spacing = 4 };
        for (int i = 0; i < s.Labels.Count; i++)
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = s.Labels[i],
                FontSize = AntTokens.FontMinor,
                Foreground = Ant.TextSub,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var bar = new Border
            {
                Height = 14,
                CornerRadius = new CornerRadius(0, 3, 3, 0),
                Background = Ant.Primary,
                Width = Math.Max(4, 160 * s.Values[i] / maxV),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);
            sp.Children.Add(row);
        }
        return sp;
    }

    static int TypeIndex(string type) => type switch { "histogram" => 1, "line" => 2, _ => 0 };
    static string TypeNameOf(int index) => index switch { 1 => "histogram", 2 => "line", _ => "bar" };
}
