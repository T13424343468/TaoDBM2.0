using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>看板管理弹窗（批次E，2026-11-20 老板令「全覆盖」）：钉住清单 + 到期状态 + 摘除/立即刷新。
/// 刷新执行走当前连接提供者（流式底座 QueryRouter 分流，禁整表物化）；对话框关闭即停表，不后台空转。
/// 钉住入口在结果网格右键（ResultGridMenu），本弹窗是看板的统一管理面。</summary>
public static class KanbanDialog
{
    public static void Show(FrameworkElement owner, IDbProvider? provider)
    {
        var pinsHost = new StackPanel { Spacing = 10, MinWidth = 640 };
        var status = new TextBlock { FontSize = 12, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var dlg = new ContentDialog
        {
            Title = "看板（钉住与定时刷新）",
            Content = new ScrollViewer { Content = pinsHost, MaxHeight = 520 },
            CloseButtonText = "关闭",
            XamlRoot = owner.XamlRoot,
        };

        // 对话框打开期间才挂刷新表：关窗即停，不后台空转（家法：不激活不空转）
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        dlg.Closed += (_, _) => timer.Stop();
        dlg.Opened += (_, _) =>
        {
            timer.Tick += async (_, _) => await RefreshDueAsync(provider, status);
            timer.Start();
        };

        Rebuild(pinsHost, provider, status);
        _ = dlg.ShowThemedAsync();
    }

    static void Rebuild(StackPanel pinsHost, IDbProvider? provider, TextBlock status)
    {
        pinsHost.Children.Clear();
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
                FontSize = 12,
                Foreground = Ant.Text,
                TextWrapping = TextWrapping.Wrap,
            };
            var refreshBtn = new Button { Content = "立即刷新", CornerRadius = new CornerRadius(4), IsEnabled = provider != null };
            refreshBtn.Click += async (_, _) => await RunPinAsync(provider, pin, status);
            var unpinBtn = new Button { Content = "摘除", CornerRadius = new CornerRadius(4) };
            unpinBtn.Click += (_, _) => { KanbanPinStore.Unpin(pin.PinId); Rebuild(pinsHost, provider, status); };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(refreshBtn);
            row.Children.Add(unpinBtn);
            var card = new StackPanel { Spacing = 6 };
            card.Children.Add(info);
            card.Children.Add(row);
            var box = new Border
            {
                Child = card,
                BorderBrush = Ant.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
            };
            pinsHost.Children.Add(box);
        }
    }

    static async Task RefreshDueAsync(IDbProvider? provider, TextBlock status)
    {
        foreach (var pin in KanbanPinStore.Due(DateTimeOffset.UtcNow))
        {
            await RunPinAsync(provider, pin, status);   // 取数走流式底座（QueryRouter 分流），单卡重跑
        }
    }

    static async Task RunPinAsync(IDbProvider? provider, KanbanPinStore.Pin pin, TextBlock status)
    {
        if (provider is null)
        {
            status.Text = "无活动连接，暂不能刷新；连上库再点。";
            return;
        }
        try
        {
            await provider.Execute(pin.Db ?? "", pin.Sql);
            KanbanPinStore.Touch(pin.PinId, DateTimeOffset.UtcNow);   // 重跑成功即推水位
            status.Text = $"「{pin.Title}」已刷新 {DateTimeOffset.Now:HH:mm:ss}。";
        }
        catch (Exception ex)
        {
            status.Text = $"「{pin.Title}」刷新失败：{ex.Message}";
        }
    }
}
