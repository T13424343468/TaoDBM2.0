using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>异常告警中心（设计稿 §11）：规则模板化创建、立即巡检、触发记录可回溯、防轰炸合并。
/// 指标采集只做各引擎真支持的部分（连接数/锁等待/复制状态/复制延迟），不支持的引擎如实标注，
/// 不假承诺。动作默认应用内提醒（P-4），系统通知走通知中心开关。</summary>
public static class AlertCenterDialog
{
    private static string RulesFile => TaoPaths.FilePath("alert-rules.json");
    private static string EventsFile => TaoPaths.FilePath("alert-events.jsonl");

    public static async Task Show(MainWindow owner, NodeInfo info)
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
            foreach (var e in Enumerable.Reverse(events.TakeLast(20).ToList()))
            {
                historyPanel.Children.Add(new TextBlock
                {
                    Text = $"{e.FiredAtUtc[..19].Replace('T', ' ')} · [{(e.Severity == "error" ? "红色" : "提醒")}] {e.RuleName}：{e.Message}",
                    FontSize = AntTokens.FontMinor,
                    Foreground = e.Severity == "error" ? Ant.Error : Ant.Text,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
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
                // P-4：应用内为主；系统通知走通知中心开关（HealthCheck 通道）
                ToastCenter.Publish(new NotifyEvent(NotifyKind.HealthCheck, e.RuleName, $"告警：{e.RuleName}", e.Message, DateTime.Now));
            }
            AlertEngine.SaveRules(RulesFile, rules);
            RefreshRules();
            RefreshHistory();
            status.Text = fired.Count == 0
                ? $"巡检完成：{metrics.Count} 项指标在阈值内，无告警。"
                : $"巡检完成：触发 {fired.Count} 条告警（已合并防轰炸），见触发记录。";
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

        var dlg = new ContentDialog
        {
            Title = "告警中心 — 阈值/事件告警，应用内提醒",
            Content = new ScrollViewer { Content = panel, MaxHeight = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        await dlg.ShowThemedAsync();
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
