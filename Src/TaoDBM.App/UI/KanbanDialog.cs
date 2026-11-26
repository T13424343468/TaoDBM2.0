using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>看板管理弹窗（批次E，2026-11-20 老板令「全覆盖」）：钉住清单 + 到期状态 + 摘除/立即刷新。
/// 刷新执行走当前连接提供者（流式底座 QueryRouter 分流，禁整表物化）；对话框关闭即停表，不后台空转。
/// P0 整改 04（2026-11-22）：刷新不再只推水位——重跑后把结果摘要在卡片上就地更新（单值大字显示，多行给行数＋首行快照），
/// 自动刷新与手动刷新同一条更新链路，卡片数字随数据变化。
/// 整改二批格19（2026-11-23）：刷新后重建卡片视觉——单值大数字（28pt）＋趋势箭头（↑绿↓红）＋头部时间戳；
/// 重建走「清空 StackPanel→按最新结果重建子树」；刷新中卡片置灰＋加载点。AppLog 锚点 [看板] 刷新 pin= 保持。
/// 钉住入口在结果网格右键（ResultGridMenu），本弹窗是看板的统一管理面。
/// 发现127（2026-11-25）：刷新加重入闸——定时器与手动按钮可能并发重跑同一张卡，按 PinId 跳过在途；
/// 整轮到期刷新未结束时不再叠开第二轮，避免慢查询堆积把连接打满。</summary>
public static class KanbanDialog
{
    // 发现127：在途闸——同一 PinId 的刷新未返回前，定时器/按钮再触发一律跳过；整轮到期刷新同理
    static readonly HashSet<string> InFlight = new();
    static bool RefreshingDue;

    public static void Show(MainWindow owner, IDbProvider? provider)
    {
        var pinsHost = new StackPanel { Spacing = 10, MinWidth = 640 };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var cardData = new Dictionary<string, StackPanel>();   // PinId → 卡片数据宿主，刷新后清空重建子树（格19）
        var lastValues = new Dictionary<string, string>();     // PinId → 上次数值（趋势箭头对比用）
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
            timer.Tick += async (_, _) => await RefreshDueAsync(provider, status, cardData, lastValues);
            timer.Start();
        };

        Rebuild(pinsHost, provider, status, cardData, lastValues);
        _ = dlg.ShowThemedAsync();
    }

    static void Rebuild(StackPanel pinsHost, IDbProvider? provider, TextBlock status, Dictionary<string, StackPanel> cardData, Dictionary<string, string> lastValues)
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
            var data = new StackPanel { Spacing = 2 };   // 格19：数据宿主，刷新后清空重建子树
            cardData[pin.PinId] = data;
            var refreshBtn = new Button { Content = "立即刷新", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), IsEnabled = provider != null };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(refreshBtn, "kanban-refresh");
            refreshBtn.Click += async (_, _) => await RunPinAsync(provider, pin, status, cardData, lastValues);
            var unpinBtn = new Button { Content = "摘除", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
            unpinBtn.Click += (_, _) => { KanbanPinStore.Unpin(pin.PinId); Rebuild(pinsHost, provider, status, cardData, lastValues); };
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
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, $"KanbanCard_{pin.PinId}");   // 格19：卡片锚点
            pinsHost.Children.Add(box);
        }
    }

    static async Task RefreshDueAsync(IDbProvider? provider, TextBlock status, Dictionary<string, StackPanel> cardData, Dictionary<string, string> lastValues)
    {
        if (RefreshingDue) return;   // 发现127：上一轮到期刷新还没跑完，不叠开第二轮
        RefreshingDue = true;
        try
        {
            foreach (var pin in KanbanPinStore.Due(DateTimeOffset.UtcNow))
            {
                await RunPinAsync(provider, pin, status, cardData, lastValues);   // 取数走流式底座（QueryRouter 分流），单卡重跑
            }
        }
        finally { RefreshingDue = false; }
    }

    static async Task RunPinAsync(IDbProvider? provider, KanbanPinStore.Pin pin, TextBlock status, Dictionary<string, StackPanel> cardData, Dictionary<string, string> lastValues)
    {
        if (provider is null)
        {
            status.Text = "无活动连接，暂不能刷新；连上库再点。";
            return;
        }
        if (!InFlight.Add(pin.PinId)) return;   // 发现127：该卡已在途（定时器或手动触发过），跳过本次
        try
        {
            // 格19：刷新中卡片置灰＋加载点，避免旧数据被误读为最新
            if (cardData.TryGetValue(pin.PinId, out var host))
            {
                host.Opacity = 0.6;
                host.Children.Clear();
                host.Children.Add(new TextBlock { Text = "刷新中…", FontSize = AntTokens.FontBody, Foreground = Ant.TextSub });
            }
            var list = await provider.Execute(pin.Db ?? "", pin.Sql);
            KanbanPinStore.Touch(pin.PinId, DateTimeOffset.UtcNow);   // 重跑成功即推水位
            if (cardData.TryGetValue(pin.PinId, out host))
            {
                host.Opacity = 1.0;
                host.Children.Clear();   // 格19：清空重建子树，避免旧控件残留
                BuildCardVisual(host, pin, list, lastValues);
            }
            status.Text = $"「{pin.Title}」已刷新 {DateTimeOffset.Now:HH:mm:ss}。";
            AppLog.Write($"[看板] 刷新 pin={pin.Title} summary={Summarize(list)}");   // 走查 S24/S33/S86 实效断言锚点（保持不变）
        }
        catch (Exception ex)
        {
            if (cardData.TryGetValue(pin.PinId, out var host))
            {
                host.Opacity = 1.0;
                host.Children.Clear();
                host.Children.Add(new TextBlock { Text = HumanError.Say(ex, "刷新失败"), FontSize = AntTokens.FontBody, Foreground = Ant.Error, TextWrapping = TextWrapping.Wrap });
            }
            status.Text = HumanError.Say(ex, $"「{pin.Title}」刷新失败");
        }
        finally { InFlight.Remove(pin.PinId); }   // 发现127：无论成败都放闸，下次可再刷
    }

    /// <summary>格19：按最新结果重建卡片视觉——单值大数字（28pt）＋趋势箭头（↑绿↓红，对比上次值）；多行行数大字＋首行快照；头部带刷新时间戳。</summary>
    static void BuildCardVisual(StackPanel host, KanbanPinStore.Pin pin, List<QueryResult> list, Dictionary<string, string> lastValues)
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
            sub = $"{pin.Title} · {ts}";
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
        var big = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        big.Children.Add(new TextBlock
        {
            Text = headline,
            FontSize = AntTokens.FontDisplayLg,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ant.Text,
            VerticalAlignment = VerticalAlignment.Bottom,
        });
        // 趋势箭头：上次值可解析为数值且不同时，↑绿↓红
        string prev = lastValues.GetValueOrDefault(pin.PinId, "");
        lastValues[pin.PinId] = headline;
        if (prev.Length > 0 && prev != headline &&
            double.TryParse(prev.TrimEnd('%'), out var pv) && double.TryParse(headline.TrimEnd('%'), out var nv))
        {
            bool up = nv > pv;
            big.Children.Add(new TextBlock
            {
                Text = up ? "↑" : "↓",
                FontSize = AntTokens.FontTitle,
                Foreground = up ? Ant.Success : Ant.Error,
                VerticalAlignment = VerticalAlignment.Bottom,
            });
        }
        host.Children.Add(big);
        host.Children.Add(new TextBlock { Text = sub, FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });
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
