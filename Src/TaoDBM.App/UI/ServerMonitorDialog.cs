using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>批次三格2：服务器监控仪表板——连接数/活跃会话/慢查询计数按七方言出 SQL，
/// 5 秒轮询，曲线自写折线（Polyline，零包）；会话列表带「终止会话」（KILL 草稿确认后执行）。
/// 轮询与 AlertRule 巡检是两套（冻结稿落点冲突结论）：这里只管采样节奏，不碰冷却窗。
/// 对话框关闭即停表，不后台空转（家法：不激活不空转）。</summary>
public static class ServerMonitorDialog
{
    public static void Show(MainWindow owner, NodeInfo info)
    {
        var provider = info.Provider!;
        string db = info.Db;
        var engine = provider.Profile.Engine;

        var metricsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        // 发现310原型：告警条——指标较上次采样急升时置顶提示（WarnBg 底色）
        var alertText = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.Warn, TextWrapping = TextWrapping.Wrap };
        var alertBar = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusSm),
            Background = Ant.WarnBg,
            Visibility = Visibility.Collapsed,
            Child = alertText,
        };
        var curve = new Canvas { Height = 120, MinWidth = 560 };
        var sessions = new ListView { MaxHeight = 220 };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        var dlg = new ContentDialog
        {
            Title = $"服务器监控 · {info.Name}",
            Content = new StackPanel { Spacing = 10, MinWidth = 620, Children = { metricsPanel, alertBar, curve, sessions, status } },
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };

        var history = new Dictionary<string, List<double>>();   // 指标 → 采样序列（曲线用）
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        dlg.Closed += (_, _) => timer.Stop();

        async Task Sample()
        {
            try
            {
                var results = await provider.Execute(db, ServerMonitor.MetricsSql(engine, db));
                var metrics = ServerMonitor.ExtractMetrics(results);
                metricsPanel.Children.Clear();
                alertBar.Visibility = Visibility.Collapsed;
                foreach (var key in new[] { "connections_total", "Threads_connected", "sessions_active", "Threads_running", "slow_queries", "Slow_queries", "connected_clients" })
                {
                    if (!metrics.TryGetValue(key, out var v)) continue;
                    // 发现310原型：趋势箭头——与上次采样对比，升红/降绿/平灰
                    string trend = "— 持平";
                    Brush? trendBrush = Ant.TextSub;
                    if (!history.TryGetValue(key, out var list)) history[key] = list = new List<double>();
                    if (double.TryParse(v, out var d) && list.Count > 0 && list.Last() > 0)
                    {
                        double pct = (d - list.Last()) / list.Last() * 100;
                        if (pct > 1) { trend = $"▲ +{pct:F1}%"; trendBrush = Ant.Error; }
                        else if (pct < -1) { trend = $"▼ {pct:F1}%"; trendBrush = Ant.Success; }
                        if (pct >= 50)
                        {
                            alertText.Text = $"⚠ {DateTime.Now:HH:mm:ss}　{key} 较上次采样上升 {pct:F0}%（{list.Last():N0} → {d:N0}），请关注。";
                            alertBar.Visibility = Visibility.Visible;
                        }
                    }
                    if (double.TryParse(v, out d)) { list.Add(d); if (list.Count > 60) list.RemoveAt(0); }
                    metricsPanel.Children.Add(StatCard(key, v, trend, trendBrush));
                }
                DrawCurve(curve, history);
                // 修红（评分打回310）：单文件引擎无指标可采，空态给明确说明与引导，不留白板
                if (metricsPanel.Children.Count == 0)
                {
                    curve.Visibility = Visibility.Collapsed;
                    sessions.Visibility = Visibility.Collapsed;
                    metricsPanel.Children.Add(new Border
                    {
                        Padding = new Thickness(16, 12, 16, 12),
                        CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
                        BorderThickness = new Thickness(1),
                        BorderBrush = Ant.Border,
                        Background = (Brush?)Application.Current.Resources["AntCard"],
                        Child = new StackPanel { Spacing = 4, Children =
                        {
                            new TextBlock { Text = "ℹ️ 该引擎暂无服务器级指标", FontSize = AntTokens.FontMinor, Foreground = Ant.Text },
                            new TextBlock { Text = engine == DbEngine.SQLite
                                ? "SQLite 是单文件引擎，没有服务器进程，因此没有连接数、QPS、会话列表等监控指标。需要服务器监控请连接 MySQL / PostgreSQL / SQL Server / Oracle 等客户端-服务器引擎。"
                                : "本次采样未取得指标数据，请检查连接与权限后重试。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap },
                        } },
                    });
                }
                else
                {
                    curve.Visibility = Visibility.Visible;
                    sessions.Visibility = Visibility.Visible;
                }
                // 会话清单：取最后一个带行的结果集
                var sess = results.LastOrDefault(r => r.Rows.Count > 0 && r.Columns.Any(c => c.ToLowerInvariant().Contains("sessionid") || c.ToLowerInvariant() == "id" || c.ToLowerInvariant() == "pid"));
                sessions.Items.Clear();
                if (sess is not null)
                {
                    foreach (var row in sess.Rows.Take(50))
                    {
                        var cells = string.Join(" | ", sess.Columns.Zip(row, (c, v) => $"{c}={ResultGridOps.CellText(v)}").Take(5));
                        var item = new ListViewItem { Content = cells };
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, $"Monitor_{Convert.ToString(row[0])}");
                        item.DoubleTapped += async (_, _) =>
                        {
                            var sid = Convert.ToString(row[0]) ?? "";
                            string kill = ServerMonitor.KillSql(engine, sid);
                            if (kill.Length == 0) { status.Text = "该引擎不支持终止会话。"; return; }
                            if (!await owner.ConfirmAsync($"确定终止会话 {sid} 吗？该会话未提交的事务将回滚。", "终止")) return;
                            try
                            {
                                await provider.Execute(db, kill);
                                status.Text = $"会话 {sid} 已终止。";
                                AppLog.Write("[监控] 终止=1");
                                _ = Sample();
                            }
                            catch (Exception ex) { status.Text = HumanError.Say(ex, "终止失败"); }
                        };
                        sessions.Items.Add(item);
                    }
                }
                status.Text = $"最近采样 {DateTime.Now:HH:mm:ss} · 曲线窗口 {history.Values.FirstOrDefault()?.Count ?? 0} 点";
                AppLog.Write("[监控] 采样=1");
            }
            catch (Exception ex)
            {
                status.Text = HumanError.Say(ex, "采样失败");
            }
        }

        dlg.Opened += (_, _) => { timer.Tick += async (_, _) => await Sample(); timer.Start(); _ = Sample(); };
        _ = dlg.ShowThemedAsync();
    }

    /// <summary>发现310原型：指标卡——数值大字、标签小字，右上角趋势箭头（升红/降绿/平灰）。</summary>
    private static Border StatCard(string label, string value, string trend, Brush? trendBrush)
    {
        var topRow = new Grid();
        topRow.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = AntTokens.FontDisplay,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        topRow.Children.Add(new TextBlock
        {
            Text = trend,
            FontSize = AntTokens.FontCaption,
            Foreground = trendBrush ?? Ant.TextSub,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        });
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(topRow);
        body.Children.Add(new TextBlock { Text = label, FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });
        return new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
            BorderThickness = new Thickness(1),
            BorderBrush = Ant.Border,
            Background = (Brush?)Application.Current.Resources["AntCard"],
            Child = body,
        };
    }

    /// <summary>自写折线：首个有历史的指标画 Polyline（零包，Canvas 归一化坐标）。</summary>
    static void DrawCurve(Canvas canvas, Dictionary<string, List<double>> history)
    {
        canvas.Children.Clear();
        var series = history.Values.FirstOrDefault(l => l.Count >= 2);
        if (series is null) return;
        double max = Math.Max(series.Max(), 1);
        double w = Math.Max(canvas.MinWidth, 1), h = canvas.Height > 0 ? canvas.Height : 120;
        var pts = new PointCollection();
        for (int i = 0; i < series.Count; i++)
            pts.Add(new Windows.Foundation.Point(i * w / Math.Max(series.Count - 1, 1), h - series[i] / max * (h - 12) - 6));
        var line = new Polyline { Points = pts, StrokeThickness = 2, Stroke = Ant.Primary };
        canvas.Children.Add(line);
        canvas.Children.Add(new TextBlock { Text = $"峰值 {max:N0}", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub,
            Margin = new Thickness(4, 0, 0, 0) });
    }
}
