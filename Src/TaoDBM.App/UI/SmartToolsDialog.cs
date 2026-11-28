using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>智能工具对话框：SmartLine 工具集的连接级入口。
/// 慢查询诊断（SlowQueryDiagnoser 静态规则 + 执行计划补充）、查询重测（QueryRetest 中位数计时对比）、
/// 容量预估（CapacityForecaster 对采样点做线性回归）、复现包（QueryReproPack 脱敏打包 + 往返校验）。
/// 全部只读 / 纯计算：重测只执行用户粘贴的 SELECT，容量预估只对用户提供的采样数据回归，不改任何数据。</summary>
public static class SmartToolsDialog
{
    public static FrameworkElement BuildPanel(MainWindow owner, NodeInfo info)
    {
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var report = new StackPanel { Spacing = 10 };

        // ── 慢查询诊断 ──
        var sqlBox = new TextBox { PlaceholderText = "粘贴一条 SELECT，做慢查询静态诊断与索引建议", MinWidth = 560 };
        var diagBtn = new Button { Content = "诊断 SQL", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        diagBtn.Click += (_, _) => RunDiag(owner, info, sqlBox.Text, report, status);

        // ── 查询重测 ──
        var beforeBox = new TextBox { PlaceholderText = "改写前 SQL（SELECT）", MinWidth = 420 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(beforeBox, "smart-before");
        var afterBox = new TextBox { PlaceholderText = "改写后 SQL（SELECT，留空则只测改写前）", MinWidth = 420 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(afterBox, "smart-after");
        var retestBtn = new Button { Content = "重测对比", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        retestBtn.Click += async (_, _) => await RunRetest(owner, info, beforeBox.Text, afterBox.Text, report, status);

        // ── 容量预估 ──
        var sampleBox = new TextBox
        {
            PlaceholderText = "容量采样（每行一条：日期 时间 已用字节），如 2026-11-01 08:00 1073741824",
            MinWidth = 560,
            AcceptsReturn = true,
            Height = 72,
        };
        var capBtn = new Button { Content = "容量预估", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        capBtn.Click += (_, _) => RunCapacity(sampleBox.Text, report, status);

        // ── 复现包 ──
        var packBox = new TextBox
        {
            PlaceholderText = "复现包：填标题/引擎/SQL 后打包；或粘贴包 JSON 验证脱敏",
            MinWidth = 560,
            AcceptsReturn = true,
            Height = 72,
        };
        var packBtn = new Button { Content = "打包（脱敏）", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var packVerifyBtn = new Button { Content = "校验已粘包", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        packBtn.Click += (_, _) => RunPack(packBox.Text, report, status);
        packVerifyBtn.Click += (_, _) => RunPackVerify(packBox.Text, report, status);

        var diagRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        diagRow.Children.Add(diagBtn);

        var retestRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        retestRow.Children.Add(retestBtn);

        var capRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        capRow.Children.Add(capBtn);

        var packRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        packRow.Children.Add(packBtn);
        packRow.Children.Add(packVerifyBtn);

        // ── 优化回访（闭环）：登记 → 到期复测 → 前后对比卡 ──
        var followTitleBox = new TextBox { PlaceholderText = "回访标题，如 orders 改写优化", MinWidth = 260 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(followTitleBox, "smart-follow-title");
        var followIntervalBox = new TextBox { PlaceholderText = "间隔天（默认 1）", MinWidth = 120 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(followIntervalBox, "smart-follow-interval");
        var followRegisterBtn = new Button { Content = "登记回访", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(followRegisterBtn, "smart-register");
        var followDueBtn = new Button { Content = "查到期", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var followRetestBtn = new Button { Content = "复测并出对比卡", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var followCenterBtn = new Button { Content = "并入任务中心", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(followCenterBtn, "smart-center");
        followRegisterBtn.Click += async (_, _) =>
        {
            var sql = afterBox.Text.Length > 0 ? afterBox.Text : beforeBox.Text;
            await RegisterFollowupAsync(followTitleBox.Text, sql, info, followIntervalBox.Text, report, status, followRegisterBtn);
        };
        followDueBtn.Click += (_, _) => ShowDueFollowups(report, status);
        followRetestBtn.Click += async (_, _) => await RunFollowupRetest(owner, info, followTitleBox.Text, report, status);
        followCenterBtn.Click += (_, _) => AttachFollowupToCenter(followTitleBox.Text, report, status);

        var followRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        followRow.Children.Add(followRegisterBtn);
        followRow.Children.Add(followDueBtn);
        followRow.Children.Add(followRetestBtn);
        followRow.Children.Add(followCenterBtn);

        var panel = new StackPanel { Spacing = 10, MinWidth = 680 };
        panel.Children.Add(new TextBlock
        {
            Text = "智能工具（只读 / 纯计算）：SQL 静态诊断不改数据；重测只执行 SELECT；容量预估只对采样回归；复现包脱敏后才展示；回访复测沿用同一口径。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ant.TextSub,
            FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(sqlBox);
        panel.Children.Add(diagRow);
        panel.Children.Add(beforeBox);
        panel.Children.Add(afterBox);
        panel.Children.Add(retestRow);
        panel.Children.Add(sampleBox);
        panel.Children.Add(capRow);
        panel.Children.Add(packBox);
        panel.Children.Add(packRow);
        panel.Children.Add(followTitleBox);
        panel.Children.Add(followIntervalBox);
        panel.Children.Add(followRow);

        return panel;
    }

    // ── 慢查询诊断：静态规则 + 索引建议，全部基于用户粘贴文本 ──
    private static void RunDiag(MainWindow owner, NodeInfo info, string sql, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        if (string.IsNullOrWhiteSpace(sql))
        {
            status.Text = "请先粘贴要诊断的 SQL。";
            return;
        }

        status.Text = "诊断中…";
        try
        {
            var findings = SlowQueryDiagnoser.Analyze(sql);
            AddSection(report, "静态诊断");
            if (findings.Count == 0)
                AddLine(report, "✓ 未检出常见慢查询模式");
            foreach (var f in findings)
            {
                var line = $"[{f.Severity}] {f.Code}: {f.Message}";
                AddLine(report, line, f.Severity == "danger" ? Ant.Error : f.Severity == "warn" ? Ant.Warn : Ant.Primary);
                if (!string.IsNullOrEmpty(f.Suggestion))
                    AddLine(report, "  建议：" + f.Suggestion, indent: 1);
            }

            // 索引建议（静态规则推断）+ 一键生成索引 DDL（设计稿 §4 侧栏）
            var table = GuessTable(sql);
            if (table.Length > 0)
            {
                var ddls = SlowQueryDiagnoser.SuggestIndexDdl(sql, table);
                if (ddls.Count > 0)
                {
                    AddSection(report, "索引建议（仅供参考，请结合执行计划评估）");
                    foreach (var ddl in ddls) AddLine(report, ddl);
                    var ddlBtn = new Button
                    {
                        Content = "一键生成索引 DDL → 编辑器",
                        CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
                        FontSize = AntTokens.FontMinor,
                    };
                    var ddlText = string.Join(";\n", ddls) + ";";
                    ddlBtn.Click += (_, _) =>
                    {
                        owner.OpenQueryTab(info, "-- 一键索引 DDL（来自慢查询诊断建议，执行前请核对）\n" + ddlText);
                        status.Text = "索引 DDL 已打开到查询编辑器，确认后再执行。";
                    };
                    report.Children.Add(ddlBtn);
                }
            }

            status.Text = "静态诊断完成。要验证可配合执行计划诊断（一键诊断）。";
        }
        catch (Exception ex)
        {
            status.Text = HumanError.Say(ex, "诊断失败");
        }
    }

    private static string GuessTable(string sql)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            sql, @"\bFROM\s+([`\""\[\w\d_\.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim('`', '[', ']', '"', ';') : "";
    }

    // ── 查询重测：只执行 SELECT，走查询窗口确认 ──
    private static async Task RunRetest(MainWindow owner, NodeInfo info, string beforeSql, string afterSql,
        StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        if (string.IsNullOrWhiteSpace(beforeSql))
        {
            status.Text = "请先粘贴改写前的 SELECT。";
            return;
        }

        // 发现125：重测通道硬守门——非 SELECT（含多语句、注释夹带写语句）一律拒绝执行，与界面承诺对齐。
        if (!QueryRetest.IsSelectOnly(beforeSql) || (!string.IsNullOrWhiteSpace(afterSql) && !QueryRetest.IsSelectOnly(afterSql)))
        {
            AppLog.Write("[智能工具] retest-guard rejected non-select");   // 发现125 守门留痕
            status.Text = "重测只执行 SELECT：粘贴的内容包含非查询语句或多条语句，已拒绝执行。";
            return;
        }

        status.Text = "重测对比中…（默认 5 轮取中位数）";
        try
        {
            var prov = info.Provider ?? ProviderFactory.Create(info.Profile);
            Task Exec(string sql, CancellationToken ct) => prov.Execute(CurrentDb(info), sql).ContinueWith(_ => Task.CompletedTask).Unwrap();

            if (string.IsNullOrWhiteSpace(afterSql))
            {
                double ms = await QueryRetest.MedianMsAsync(Exec, beforeSql);
                AddSection(report, "单条重测");
                AddLine(report, $"中位耗时：{ms:F1} ms（5 轮）");
                status.Text = "单条重测完成。";
            }
            else
            {
                var r = await QueryRetest.CompareAsync(Exec, beforeSql, afterSql);
                AddSection(report, "改写前后对比");
                AddLine(report, $"改写前：{r.BeforeMs:F1} ms");
                AddLine(report, $"改写后：{r.AfterMs:F1} ms");
                AddLine(report, $"提速：{r.Speedup:F2} 倍（{r.Iterations} 轮取中位）");
                status.Text = r.Speedup >= 1.2 ? "改写后有提升。" : r.Speedup <= 0.85 ? "改写后反而变慢，建议回退。" : "差异不大，可保持原样。";
            }
        }
        catch (Exception ex)
        {
            status.Text = HumanError.Say(ex, "重测失败") + "（请确认是可在当前连接执行的 SELECT）";
        }
    }

    // ── 容量预估：对采样点线性回归 ──
    private static void RunCapacity(string sampleText, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        var samples = new List<CapacityForecaster.CapacitySample>();
        foreach (var raw in sampleText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3
                || !DateTimeOffset.TryParse(parts[0] + " " + parts[1], System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var at)
                || !double.TryParse(parts[2], out var bytes))
            {
                status.Text = "采样格式：每行「日期 时间 已用字节」，如 2026-11-01 08:00 1073741824";
                return;
            }
            samples.Add(new CapacityForecaster.CapacitySample(at, bytes));
        }

        if (samples.Count < 3)
        {
            status.Text = "至少 3 条采样才能可靠回归。";
            return;
        }

        try
        {
            var f = CapacityForecaster.Forecast(samples);
            AddSection(report, "容量趋势（线性回归）");
            AddLine(report, $"日增长：{Core.HumanSize.Of((long)f.GrowthPerDay)}/天");
            AddLine(report, $"拟合优度 R²：{f.RSquared:F3}（越接近 1 越可信）");
            if (f.DaysToThreshold is { } days)
                AddLine(report, $"按当前增速，约 {days:F0} 天到达阈值。");
            else
                AddLine(report, "未设阈值或趋势平缓，未给出到达天数。");
            status.Text = "预估完成。采样越多、跨度越大，结果越准。";
        }
        catch (Exception ex)
        {
            status.Text = HumanError.Say(ex, "预估失败");
        }
    }

    // ── 复现包：脱敏打包 / 粘包校验 ──
    private static void RunPack(string text, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        if (string.IsNullOrWhiteSpace(text))
        {
            status.Text = "请粘贴 SQL（打包用）或包 JSON（校验用）。";
            return;
        }

        try
        {
            // 粘的是已有包 JSON 时自动转校验
            if (text.TrimStart().StartsWith("{"))
            {
                RunPackVerify(text, report, status);
                return;
            }

            var raw = new QueryReproPack.ReproPack("1", "未命名复现包", "MySQL", text,
                new Dictionary<string, string>(), new List<string>(), null, System.DateTimeOffset.UtcNow);
            var clean = QueryReproPack.Sanitize(raw);
            var json = QueryReproPack.ToJson(clean);
            AddSection(report, "脱敏复现包");
            AddLine(report, "已剔除注释与凭据类字段，可安全外发。包 JSON：");
            AddLine(report, json);
            status.Text = "打包完成（脱敏）。";
        }
        catch (Exception ex)
        {
            status.Text = HumanError.Say(ex, "打包失败");
        }
    }

    private static void RunPackVerify(string text, StackPanel report, TextBlock status)
    {
        try
        {
            var parsed = QueryReproPack.FromJson(text);
            if (parsed is null)
            {
                status.Text = "不是有效的复现包 JSON。";
                return;
            }

            var clean = QueryReproPack.Sanitize(parsed);
            AddSection(report, "包校验（脱敏复核）");
            AddLine(report, $"标题：{clean.Title}");
            AddLine(report, $"引擎：{clean.Engine}");
            AddLine(report, $"版本：{clean.PackVersion}");
            AddLine(report, "SQL：");
            AddLine(report, clean.Sql, indent: 1);
            status.Text = "校验完成。以上内容为脱敏后版本。";
        }
        catch (Exception ex)
        {
            status.Text = HumanError.Say(ex, "校验失败");
        }
    }

    // ── 优化回访（闭环）：登记 / 到期 / 复测对比卡 / 并入任务中心 ──
    // 登记账落盘（FollowupLedger，重启不丢；对话框为静态工具口）。
    private static readonly List<OptimizationFollowup.FollowupTask> Followups = FollowupLedger.Load();

    private static OptimizationFollowup.FollowupTask? FindFollowup(string title) =>
        Followups.LastOrDefault(f => f.Title == title.Trim());

    private static async Task RegisterFollowupAsync(string title, string sql, NodeInfo info, string intervalText,
        StackPanel report, TextBlock status, Button registerBtn)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(sql))
        {
            status.Text = "请先填回访标题和 SQL（重测框里的改写后/改写前均可）。";
            return;
        }
        int interval = 1;
        if (!string.IsNullOrWhiteSpace(intervalText) &&
            (!int.TryParse(intervalText.Trim(), out interval) || interval <= 0))
        {
            status.Text = "间隔天须为正整数。";
            return;
        }

        // 基准 = 登记瞬间的静态诊断 +（SQLite 引擎时顺带计划体检）+ 中位耗时（真执行，走重测口径）
        var baselineCodes = SlowQueryDiagnoser.Analyze(sql).Select(x => x.Code).ToList();
        double? baselineMs = null;
        registerBtn.IsEnabled = false;
        status.Text = "基准计时中…（5 轮取中位）";
        try
        {
            var prov = info.Provider ?? ProviderFactory.Create(info.Profile);
            Task Exec(string s, CancellationToken ct) => prov.Execute(CurrentDb(info), s).ContinueWith(_ => Task.CompletedTask).Unwrap();
            baselineMs = await QueryRetest.MedianMsAsync(Exec, sql);
        }
        catch (Exception ex)
        {
            status.Text = HumanError.Say(ex, "登记时基准计时失败（不影响登记）");
        }
        finally
        {
            registerBtn.IsEnabled = true;
        }
        var task = OptimizationFollowup.Register(title, sql, "SQLite", baselineCodes,
            new List<string>(), baselineMs, interval);
        Followups.Add(task);
        FollowupLedger.Save(Followups);
        AppLog.Write($"[智能工具] 回访登记 title={task.Title} baselineMs={baselineMs}");   // 走查 S26 实效断言锚点
        AddSection(report, "回访已登记");
        foreach (var line in OptimizationFollowup.CompareCard(task)) AddLine(report, line);
        status.Text = $"已登记，{interval} 天后到期可复测。";
    }

    private static void ShowDueFollowups(StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        var due = OptimizationFollowup.DueOf(Followups);
        AddSection(report, $"到期回访（{due.Count} 条）");
        if (due.Count == 0) AddLine(report, "暂无到期待复测的回访。");
        foreach (var t in due)
            foreach (var line in OptimizationFollowup.CompareCard(t))
                AddLine(report, line);
        status.Text = due.Count > 0 ? "有到期待复测的回访，可执行复测。" : "无到期回访。";
    }

    private static async Task RunFollowupRetest(MainWindow owner, NodeInfo info, string title,
        StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        var t = FindFollowup(title);
        if (t is null)
        {
            status.Text = "没有找到该标题的回访，请先登记。";
            return;
        }
        if (t.Verdict != "pending")
        {
            status.Text = "该回访已出结论，不可重复复测。";
            foreach (var line in OptimizationFollowup.CompareCard(t)) AddLine(report, line);
            return;
        }
        if (!OptimizationFollowup.IsDue(t))
        {
            status.Text = "尚未到期，请到期后再复测。";
            foreach (var line in OptimizationFollowup.CompareCard(t)) AddLine(report, line);
            return;
        }

        status.Text = "复测中…（重跑诊断项，5 轮取中位）";
        try
        {
            var prov = info.Provider ?? ProviderFactory.Create(info.Profile);
            Task Exec(string sql, CancellationToken ct) => prov.Execute(CurrentDb(info), sql).ContinueWith(_ => Task.CompletedTask).Unwrap();
            double ms = await QueryRetest.MedianMsAsync(Exec, t.Sql);
            var codes = SlowQueryDiagnoser.Analyze(t.Sql).Select(x => x.Code).ToList();
            var done = OptimizationFollowup.Retest(t, ms, codes);
            Followups[Followups.FindIndex(f => f.TaskId == t.TaskId)] = done;
            FollowupLedger.Save(Followups);
            AddSection(report, "回访前后对比卡");
            foreach (var line in OptimizationFollowup.CompareCard(done)) AddLine(report, line);
            status.Text = done.Verdict == "passed" ? "复测通过。" : "复测未通过，建议回看优化动作。";
        }
        catch (Exception ex)
        {
            status.Text = HumanError.Say(ex, "复测失败");
        }
    }

    private static void AttachFollowupToCenter(string title, StackPanel report, TextBlock status)
    {
        var t = FindFollowup(title);
        if (t is null)
        {
            status.Text = "没有找到该标题的回访，请先登记。";
            return;
        }
        try
        {
            var taskId = TaskCenterDialog.SubmitShared(new TaoDBM.Core.Streaming.TaskSpec
            {
                Kind = "retest",
                Title = $"优化回访复测：{t.Title}",
                Db = "",
                Sql = t.Sql,
            });
            AddSection(report, "并入任务中心");
            AddLine(report, $"任务号：{taskId}");
            status.Text = "已并入任务中心，可在任务中心查看进度。";
        }
        catch (Exception ex)
        {
            status.Text = HumanError.Say(ex, "并入失败");
        }
    }

    private static string CurrentDb(NodeInfo info)
    {
        if (info.Db.Length > 0) return info.Db;
        if (info.Profile.Database.Length > 0) return info.Profile.Database;
        return "";
    }

    private static void AddLine(StackPanel p, string text, Brush? brush = null, int indent = 0)
    {
        p.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = AntTokens.FontMinor,
            Margin = new Thickness(indent * 16, 0, 0, 0),
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
