// 批次五格6（2026-11-23）：仪表板组合——钉住查询卡片两列网格，打开即刷新（走流式底座单卡重跑），
// 布局（卡片顺序）落 dashboard-layout.json（TaoPaths + AtomicWrite，损坏回落默认顺序）。
// 卡片视觉复用 KanbanDialog 格19 重建口径：单值大数字＋趋势箭头＋时间戳；刷新中置灰。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>仪表板：钉住卡片聚合面，两列网格布局，顺序可持久化。</summary>
public static class DashboardDialog
{
    private static string LayoutFile => TaoPaths.FilePath("dashboard-layout.json");

    /// <summary>布局账本：卡片顺序（PinId 列表）。文件缺失/损坏回落空表，绝不闪退。</summary>
    private static List<string> LoadOrder()
    {
        try
        {
            var json = System.IO.File.ReadAllText(LayoutFile);
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    private static void SaveOrder(List<string> order)
    {
        try
        {
            AtomicWrite.Text(LayoutFile, System.Text.Json.JsonSerializer.Serialize(order,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch { /* 布局落盘失败不闪退，下次打开回落默认顺序 */ }
    }

    public static void Show(MainWindow owner, NodeInfo info)
    {
        var provider = info.Provider;
        var grid = new Grid { MinWidth = 680 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(grid, "DashGrid");
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
            Title = "仪表板（钉住卡片组合）",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };

        // 打开即刷新：所有卡片按钉住清单重跑一遍（走流式底座，禁整表物化）
        dlg.Opened += async (_, _) => await RefreshAllAsync(provider, grid, status);
        _ = dlg.ShowThemedAsync();
    }

    /// <summary>按布局顺序（新钉的排后面）重建两列网格，并逐卡刷新。</summary>
    private static async Task RefreshAllAsync(IDbProvider? provider, Grid grid, TextBlock status)
    {
        var pins = KanbanPinStore.All();
        AppLog.Write($"[仪表板] 卡={pins.Count}");   // 批次五格6 验收锚点
        if (pins.Count == 0)
        {
            grid.Children.Clear();
            grid.Children.Add(new TextBlock
            {
                Text = "仪表板还是空的：在查询结果上右键 →「钉住到看板」，钉住的卡片会出现在这里。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Ant.TextSub,
                Margin = new Thickness(12, 10, 0, 0),
            });
            status.Text = "没有钉住的卡片。";
            return;
        }
        // 布局顺序：dashboard-layout.json 记录的 PinId 顺序优先，新钉的按钉住清单顺序排后面
        var order = LoadOrder();
        var ordered = order.Select(id => pins.FirstOrDefault(p => p.PinId == id))
            .Where(p => p is not null).Select(p => p!)
            .Concat(pins.Where(p => !order.Contains(p.PinId)))
            .ToList();
        SaveOrder(ordered.Select(p => p.PinId).ToList());

        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        var dataHosts = new Dictionary<string, StackPanel>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var pin = ordered[i];
            int row = i / 2, col = i % 2;
            if (col == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var data = new StackPanel { Spacing = 2, Opacity = 0.6 };
            data.Children.Add(new TextBlock { Text = "刷新中…", FontSize = AntTokens.FontBody, Foreground = Ant.TextSub });
            dataHosts[pin.PinId] = data;
            var title = new TextBlock
            {
                Text = pin.Title,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Ant.Primary,
                TextWrapping = TextWrapping.Wrap,
            };
            var card = new StackPanel { Spacing = 6 };
            card.Children.Add(title);
            card.Children.Add(data);
            var box = new Border
            {
                Child = card,
                BorderBrush = Ant.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, col == 0 ? 6 : 0, 8),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, $"DashCard_{pin.PinId}");
            Grid.SetRow(box, row);
            Grid.SetColumn(box, col);
            grid.Children.Add(box);
        }

        int done = 0, failed = 0;
        foreach (var pin in ordered)
        {
            var host = dataHosts[pin.PinId];
            if (provider is null)
            {
                host.Opacity = 1.0;
                host.Children.Clear();
                host.Children.Add(new TextBlock { Text = "无活动连接，暂不能刷新；连上库再打开仪表板。", FontSize = AntTokens.FontBody, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });
                failed++;
                continue;
            }
            try
            {
                var list = await provider.Execute(pin.Db ?? "", pin.Sql);
                KanbanPinStore.Touch(pin.PinId, DateTimeOffset.UtcNow);
                host.Opacity = 1.0;
                host.Children.Clear();
                BuildCardVisual(host, pin, list);
                done++;
            }
            catch (Exception ex)
            {
                host.Opacity = 1.0;
                host.Children.Clear();
                host.Children.Add(new TextBlock { Text = $"刷新失败：{ex.Message}", FontSize = AntTokens.FontBody, Foreground = Ant.Error, TextWrapping = TextWrapping.Wrap });
                failed++;
            }
        }
        status.Text = $"共 {ordered.Count} 张卡：刷新成功 {done}，失败 {failed}（{DateTimeOffset.Now:HH:mm:ss}）。";
    }

    /// <summary>卡片视觉（格19 口径）：单值大数字（28pt）＋时间戳；多行行数大字＋首行快照；空结果如实显示。</summary>
    private static void BuildCardVisual(StackPanel host, KanbanPinStore.Pin pin, List<QueryResult> list)
    {
        string ts = DateTimeOffset.Now.ToString("HH:mm:ss");
        var r = list.FirstOrDefault(x => x.Rows.Count > 0) ?? list.FirstOrDefault();
        if (r is null || r.Rows.Count == 0)
        {
            string empty = r is null ? "无结果。" : (string.IsNullOrEmpty(r.Message) ? "查询成功，0 行。" : r.Message);
            host.Children.Add(new TextBlock { Text = $"{empty}（{ts}）", FontSize = AntTokens.FontBody, Foreground = Ant.Text, TextWrapping = TextWrapping.Wrap });
            return;
        }
        string headline, sub;
        if (r.Rows.Count == 1 && r.Columns.Count == 1)
        {
            headline = Convert.ToString(r.Rows[0][0]) ?? "";
            sub = $"{ts}";
        }
        else
        {
            headline = $"{r.Rows.Count:N0} 行";
            var head = new System.Text.StringBuilder();
            for (int c = 0; c < r.Columns.Count && c < 4; c++)
            {
                if (c > 0) head.Append(" · ");
                head.Append(r.Columns[c]).Append('=').Append(Convert.ToString(r.Rows[0][c]) ?? "");
            }
            sub = $"首行 {head} · {ts}";
        }
        host.Children.Add(new TextBlock
        {
            Text = headline,
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ant.Text,
        });
        host.Children.Add(new TextBlock { Text = sub, FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });
    }
}
