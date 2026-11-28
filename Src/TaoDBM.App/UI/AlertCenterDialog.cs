using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>异常告警中心（设计稿 §11）：规则模板化创建、立即巡检、触发记录可回溯、防轰炸合并。
/// 指标采集只做各引擎真支持的部分（连接数/锁等待/复制状态/复制延迟），不支持的引擎如实标注，
/// 不假承诺。动作默认应用内提醒（P-4），系统通知走通知中心开关。</summary>
public static class AlertCenterDialog
{
    private static string RulesFile => TaoPaths.FilePath("alert-rules.json");
    private static string EventsFile => TaoPaths.FilePath("alert-events.jsonl");

    public static FrameworkElement BuildPanel(MainWindow owner, NodeInfo info)
    {
        var prov = info.Provider ?? ProviderFactory.Create(info.Profile);
        string db = info.Db.Length > 0 ? info.Db : info.Profile.Database;
        var rules = AlertEngine.LoadRules(RulesFile);

        var rulesPanel = new StackPanel { Spacing = 6 };
        var historyPanel = new StackPanel { Spacing = 4 };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var templateBox = new ComboBox { MinWidth = 220, PlaceholderText = "选择规则模板" };
        foreach (var t in AlertEngine.Templates) templateBox.Items.Add(t.Name);

        void RefreshRules()
        {
            rulesPanel.Children.Clear();
            if (rules.Count == 0)
                rulesPanel.Children.Add(new TextBlock { Text = "还没有告警规则：从下方模板新建，或巡检后按需调整。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });
            foreach (var r in rules)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var toggle = new CheckBox { IsChecked = r.Enabled, MinWidth = 0 };
                toggle.Checked += (_, _) => { r.Enabled = true; AlertEngine.SaveRules(RulesFile, rules); AppLog.Write($"[告警规则] 启用: {r.Name}"); };   // 发现145：规则变更补落账
                toggle.Unchecked += (_, _) => { r.Enabled = false; AlertEngine.SaveRules(RulesFile, rules); AppLog.Write($"[告警规则] 停用: {r.Name}"); };
                row.Children.Add(toggle);
                row.Children.Add(new TextBlock
                {
                    Text = $"{r.Name} · {AlertEngine.ConditionText(r)} · 动作：{(r.Action == "inapp" ? "应用内" : "系统通知")} · 冷却 {r.CooldownMinutes} 分 · 已触发 {r.TriggerCount} 次",
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = AntTokens.FontMinor,
                });
                var delBtn = new Button { Content = "删除", FontSize = AntTokens.FontCaption, CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
                delBtn.Click += (_, _) => { rules.Remove(r); AlertEngine.SaveRules(RulesFile, rules); AppLog.Write($"[告警规则] 删除: {r.Name}"); RefreshRules(); };   // 发现145
                row.Children.Add(delBtn);
                rulesPanel.Children.Add(row);
            }
        }

        void RefreshHistory()
        {
            historyPanel.Children.Clear();
            var events = AlertEngine.LoadEvents(EventsFile);
            if (events.Count == 0)
            {
                historyPanel.Children.Add(new TextBlock { Text = "暂无触发记录。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
                return;
            }
            // 503 原型：触发记录为表格——级别（色点）/ 告警内容 / 触发时间 / 来源 / 状态（2026-11-28 拍板补两列）
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string[] headers = { "级别", "告警内容", "触发时间", "来源", "状态" };
            for (int c = 0; c < 5; c++)
            {
                var head = new TextBlock
                {
                    Text = headers[c],
                    FontSize = AntTokens.FontMinor,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Ant.TextSub,
                };
                head.SetValue(Grid.ColumnProperty, c);
                grid.Children.Add(head);
            }
            var rows = Enumerable.Reverse(events.TakeLast(20).ToList()).ToList();
            for (int r = 0; r < rows.Count; r++)
            {
                var e = rows[r];
                grid.RowDefinitions.Add(new RowDefinition());
                var dotWrap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                dotWrap.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = e.Severity == "error" ? Ant.Error : Ant.Warn,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                dotWrap.Children.Add(new TextBlock { Text = e.Severity == "error" ? "错误" : "提醒", FontSize = AntTokens.FontMinor });
                var content = new TextBlock
                {
                    Text = $"{e.RuleName}：{e.Message}",
                    FontSize = AntTokens.FontMinor,
                    TextWrapping = TextWrapping.Wrap,
                };
                var time = new TextBlock
                {
                    Text = e.FiredAtUtc[..19].Replace('T', ' '),
                    FontSize = AntTokens.FontMinor,
                    FontFamily = new FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var source = new TextBlock
                {
                    Text = string.IsNullOrEmpty(e.Source) ? "系统" : e.Source,
                    FontSize = AntTokens.FontMinor,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                // 状态列：未处理可点确认/关闭，已确认可关闭；点击即落盘（拍板口径：状态可流转）
                var statusCell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                statusCell.Children.Add(new TextBlock
                {
                    Text = e.NormalizedStatus,
                    FontSize = AntTokens.FontMinor,
                    Foreground = e.NormalizedStatus == "未处理" ? Ant.Warn : Ant.TextSub,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                if (e.NormalizedStatus != "已关闭")
                {
                    var next = e.NormalizedStatus == "未处理" ? "已确认" : "已关闭";
                    var btn = new Button { Content = next, FontSize = AntTokens.FontCaption, Padding = new Thickness(8, 1, 8, 1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
                    btn.Click += (_, _) =>
                    {
                        var updated = e with { Status = next };
                        var all = AlertEngine.LoadEvents(EventsFile);
                        int idx = all.FindIndex(x => x.FiredAtUtc == e.FiredAtUtc && x.RuleName == e.RuleName);
                        if (idx >= 0) { all[idx] = updated; AlertEngine.SaveEvents(EventsFile, all); AppLog.Write($"[告警中心] 状态流转 {e.RuleName} → {next}"); }
                        RefreshHistory();
                    };
                    statusCell.Children.Add(btn);
                }
                var cells = new Microsoft.UI.Xaml.UIElement[] { dotWrap, content, time, source, statusCell };
                for (int c = 0; c < 5; c++)
                {
                    var cell = new Border { Padding = new Thickness(0, 4, 0, 4), Child = cells[c] };
                    Grid.SetColumn(cell, c);
                    Grid.SetRow(cell, r + 1);
                    grid.Children.Add(cell);
                }
            }
            historyPanel.Children.Add(grid);
        }

        templateBox.SelectionChanged += (_, _) =>
        {
            if (templateBox.SelectedItem is not string name) return;
            var t = AlertEngine.Templates.FirstOrDefault(x => x.Name == name);
            if (t.Name is null) return;
            rules.Add(AlertEngine.FromTemplate(t));
            AlertEngine.SaveRules(RulesFile, rules);
            AppLog.Write($"[告警规则] 新建: {t.Name}");   // 发现145：规则变更补落账
            templateBox.SelectedIndex = -1;
            RefreshRules();
            status.Text = $"已从模板新建规则「{t.Name}」。";
        };

        var inspectBtn = new Button { Content = "立即巡检", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusPill), FontSize = AntTokens.FontMinor };
        inspectBtn.Click += (_, _) => _ = InspectAsync();

        async Task InspectAsync()
        {
            status.Text = "巡检中：采集指标…";
            try
            {
                var metrics = await CollectMetrics(prov, db, status);
                if (metrics.Count == 0)
                {
                    status.Text = "该引擎暂无可用告警指标（连接数/锁等待/复制仅部分引擎支持），已如实跳过。";
                    return;
                }
                var fired = AlertEngine.Evaluate(rules, metrics, DateTime.UtcNow);
                foreach (var e in fired)
                {
                    AlertEngine.AppendEvent(EventsFile, e);
                    // 2026-11-28 拍板：动作真正分流——inapp 走应用内收件箱（NotifyKind.HealthCheck），
                    // system 走系统通知通道（ToastCenter 内部按 NotifyPrefs 判定，默认关）
                    var evt = new NotifyEvent(NotifyKind.HealthCheck, e.RuleName, $"告警：{e.RuleName}", e.Message, DateTime.Now);
                    if (rules.FirstOrDefault(x => x.Name == e.RuleName)?.Action == "system")
                        ToastCenter.Publish(evt);   // system：收件箱＋系统通知（够格才弹）
                    else
                        InboxStore.Append(evt);     // inapp：只进应用内收件箱
                }
                AlertEngine.SaveRules(RulesFile, rules);
                RefreshRules();
                RefreshHistory();
                status.Text = fired.Count == 0
                    ? $"巡检完成：{metrics.Count} 项指标在阈值内，无告警。"
                    : $"巡检完成：触发 {fired.Count} 条告警（已合并防轰炸），见触发记录。";
            }
            catch (Exception ex)
            {
                // 发现116：巡检异常不再静默——采集/落盘失败人话回状态栏并落账
                status.Text = HumanError.Say(ex, "巡检没跑成：指标没采到，请检查连接后重试。");
                AppLog.Write($"[告警中心] 巡检异常: {ex.Message}");
            }
        }

        RefreshRules();
        RefreshHistory();
        var panel = new StackPanel { Spacing = 10, MinWidth = 620 };
        panel.Children.Add(new TextBlock { Text = "规则（模板化创建 · 触发后按冷却窗合并防轰炸）", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        panel.Children.Add(rulesPanel);
        var tmplRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tmplRow.Children.Add(templateBox);
        tmplRow.Children.Add(inspectBtn);
        panel.Children.Add(tmplRow);
        panel.Children.Add(status);
        panel.Children.Add(new TextBlock { Text = "触发记录（可回溯）", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        panel.Children.Add(new ScrollViewer { Content = historyPanel, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        return new ScrollViewer { Content = panel, MaxHeight = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>指标采集：只采各引擎真支持的项，采不到的项如实跳过（不猜数、不凑数）。</summary>
    private static async Task<Dictionary<string, double>> CollectMetrics(IDbProvider prov, string db, TextBlock status)
    {
        var metrics = new Dictionary<string, double>();
        var e = prov.Profile.Engine;

        string? connSql = e switch
        {
            DbEngine.MySQL => "SELECT COUNT(*) FROM information_schema.PROCESSLIST",
            DbEngine.PostgreSQL => "SELECT COUNT(*) FROM pg_stat_activity",
            DbEngine.SqlServer => "SELECT COUNT(*) FROM sys.dm_exec_sessions",
            DbEngine.Oracle => "SELECT COUNT(*) FROM V$SESSION",
            _ => null,
        };
        if (connSql is not null)
        {
            try
            {
                var r = (await prov.Execute(db, connSql)).LastOrDefault();
                if (r is { Rows.Count: > 0 } && r.Rows[0].Length > 0 && r.Rows[0][0] is not null and not DBNull)
                    metrics["connections"] = Convert.ToDouble(r.Rows[0][0]);
            }
            catch { /* 采集失败如实跳过 */ }
        }

        var lockSql = DiagnosticsCatalog.LockWaitQuery(e);
        if (lockSql is not null)
        {
            try
            {
                var r = (await prov.Execute(db, lockSql)).LastOrDefault();
                if (r is not null) metrics["lock_waits"] = r.Rows.Count;
            }
            catch { /* 采集失败如实跳过 */ }
        }

        if (ReplicationCatalog.Supported(e))
        {
            try
            {
                var sql = ReplicationCatalog.ReplicasSql(e);
                var r = (await prov.Execute(db, sql)).LastOrDefault();
                if (r is null || r.Rows.Count == 0)
                {
                    metrics["replication_break"] = 1;   // 从库断流：立即提醒（红色）
                }
                else
                {
                    metrics["replication_break"] = 0;
                    var cols = r.Columns;
                    var lagStr = ReplicationCatalog.Col(cols, r.Rows[0], "seconds_behind", "lag", "lag_seconds");
                    if (lagStr is not null && double.TryParse(lagStr, out var lag))
                        metrics["replication_lag"] = lag;
                }
            }
            catch { /* 采集失败如实跳过 */ }
        }

        return metrics;
    }
}
