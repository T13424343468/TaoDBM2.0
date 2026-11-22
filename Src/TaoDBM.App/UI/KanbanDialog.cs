using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>看板管理弹窗（批次E，2026-11-20 老板令「全覆盖」）：钉住清单 + 到期状态 + 摘除/立即刷新。
/// 刷新执行走当前连接提供者（流式底座 QueryRouter 分流，禁整表物化）；对话框关闭即停表，不后台空转。
/// P0 整改 04（2026-11-22）：刷新不再只推水位——重跑后把结果摘要在卡片上就地更新（单值大字显示，多行给行数＋首行快照），
/// 自动刷新与手动刷新同一条更新链路，卡片数字随数据变化。
/// 钉住入口在结果网格右键（ResultGridMenu），本弹窗是看板的统一管理面。</summary>
public static class KanbanDialog
{
    public static void Show(MainWindow owner, IDbProvider? provider)
    {
        var pinsHost = new StackPanel { Spacing = 10, MinWidth = 640 };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var cardData = new Dictionary<string, TextBlock>();   // PinId → 卡片数据行，刷新后就地更新
        var dlg = new ContentDialog
        {
            Title = "看板（钉住与定时刷新）",
            Content = new ScrollViewer { Content = pinsHost, MaxHeight = 520 },
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };

        // 对话框打开期间才挂刷新表：关窗即停，不后台空转（家法：不激活不空转）
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        dlg.Closed += (_, _) => timer.Stop();
        dlg.Opened += (_, _) =>
        {
            timer.Tick += async (_, _) => await RefreshDueAsync(provider, status, cardData);
            timer.Start();
        };

        Rebuild(pinsHost, provider, status, cardData);
        _ = dlg.ShowThemedAsync();
    }

    static void Rebuild(StackPanel pinsHost, IDbProvider? provider, TextBlock status, Dictionary<string, TextBlock> cardData)
    {
        pinsHost.Children.Clear();
        cardData.Clear();
        var pins = KanbanPinStore.All();
        var due = KanbanPinStore.Due(DateTimeOffset.UtcNow);
        if (pins.Count == 0)
        {
            pinsHost.Children.Add(new TextBlock
            {
                Text = "看板还是空的：在查询结果上右键 →「钉住到看板」，即可把图表/卡片留在这里。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Ant.TextSub,
            });
            return;
        }
        foreach (var pin in pins)
        {
            bool isDue = due.Any(d => d.PinId == pin.PinId);
            var info = new TextBlock
            {
                Text = $"{pin.Title}\n{pin.Sql}\n{pin.Engine}{(string.IsNullOrEmpty(pin.Db) ? "" : " · " + pin.Db)}"
                    + (pin.AutoRefresh ? $" · 每 {pin.IntervalSeconds}s 刷新" : " · 不自动刷新")
                    + (isDue ? " · 已到期待刷新" : ""),
                FontSize = AntTokens.FontMinor,
                Foreground = Ant.Text,
                TextWrapping = TextWrapping.Wrap,
            };
            var data = new TextBlock
            {
                Text = "尚未刷新，点「立即刷新」取数。",
                FontSize = AntTokens.FontBody,
                Foreground = Ant.Text,
                TextWrapping = TextWrapping.Wrap,
            };
            cardData[pin.PinId] = data;
            var refreshBtn = new Button { Content = "立即刷新", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), IsEnabled = provider != null };
            refreshBtn.Click += async (_, _) => await RunPinAsync(provider, pin, status, cardData);
            var unpinBtn = new Button { Content = "摘除", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
            unpinBtn.Click += (_, _) => { KanbanPinStore.Unpin(pin.PinId); Rebuild(pinsHost, provider, status, cardData); };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(refreshBtn);
            row.Children.Add(unpinBtn);
            var card = new StackPanel { Spacing = 6 };
            card.Children.Add(info);
            card.Children.Add(data);
            card.Children.Add(row);
            var box = new Border
            {
                Child = card,
                BorderBrush = Ant.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg),
                Padding = new Thickness(12, 10, 12, 10),
            };
            pinsHost.Children.Add(box);
        }
    }

    static async Task RefreshDueAsync(IDbProvider? provider, TextBlock status, Dictionary<string, TextBlock> cardData)
    {
        foreach (var pin in KanbanPinStore.Due(DateTimeOffset.UtcNow))
        {
            await RunPinAsync(provider, pin, status, cardData);   // 取数走流式底座（QueryRouter 分流），单卡重跑
        }
    }

    static async Task RunPinAsync(IDbProvider? provider, KanbanPinStore.Pin pin, TextBlock status, Dictionary<string, TextBlock> cardData)
    {
        if (provider is null)
        {
            status.Text = "无活动连接，暂不能刷新；连上库再点。";
            return;
        }
        try
        {
            var list = await provider.Execute(pin.Db ?? "", pin.Sql);
            KanbanPinStore.Touch(pin.PinId, DateTimeOffset.UtcNow);   // 重跑成功即推水位
            if (cardData.TryGetValue(pin.PinId, out var data))
                data.Text = Summarize(list);
            status.Text = $"「{pin.Title}」已刷新 {DateTimeOffset.Now:HH:mm:ss}。";
        }
        catch (Exception ex)
        {
            status.Text = $"「{pin.Title}」刷新失败：{ex.Message}";
        }
    }

    /// <summary>结果摘要：单值查询直接给值；多行多列给行数＋首行快照（列=值），空结果如实显示。</summary>
    static string Summarize(List<QueryResult> list)
    {
        var r = list.FirstOrDefault(x => x.Rows.Count > 0) ?? list.FirstOrDefault();
        if (r is null) return "无结果。";
        if (r.Rows.Count == 0) return string.IsNullOrEmpty(r.Message) ? "查询成功，0 行。" : r.Message;
        if (r.Rows.Count == 1 && r.Columns.Count == 1)
            return $"{Convert.ToString(r.Rows[0][0]) ?? ""}（{DateTimeOffset.Now:HH:mm:ss}）";
        var head = new System.Text.StringBuilder();
        for (int c = 0; c < r.Columns.Count && c < 4; c++)
        {
            if (c > 0) head.Append(" · ");
            head.Append(r.Columns[c]).Append('=').Append(Convert.ToString(r.Rows[0][c]) ?? "");
        }
        return $"{r.Rows.Count:N0} 行 · 首行 {head}（{DateTimeOffset.Now:HH:mm:ss}）";
    }
}
