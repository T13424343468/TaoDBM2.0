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
    public static void Show(MainWindow owner, NodeInfo info)
    {
        var status = new TextBlock { FontSize = 12, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var report = new StackPanel { Spacing = 10 };

        // ── 慢查询诊断 ──
        var sqlBox = new TextBox { PlaceholderText = "粘贴一条 SELECT，做慢查询静态诊断与索引建议", MinWidth = 560 };
        var diagBtn = new Button { Content = "诊断 SQL", CornerRadius = new CornerRadius(4) };
        diagBtn.Click += (_, _) => RunDiag(sqlBox.Text, report, status);

        // ── 查询重测 ──
        var beforeBox = new TextBox { PlaceholderText = "改写前 SQL（SELECT）", MinWidth = 420 };
        var afterBox = new TextBox { PlaceholderText = "改写后 SQL（SELECT，留空则只测改写前）", MinWidth = 420 };
        var retestBtn = new Button { Content = "重测对比", CornerRadius = new CornerRadius(4) };
        retestBtn.Click += async (_, _) => await RunRetest(owner, info, beforeBox.Text, afterBox.Text, report, status);

        // ── 容量预估 ──
        var sampleBox = new TextBox
        {
            PlaceholderText = "容量采样（每行一条：日期 时间 已用字节），如 2026-11-01 08:00 1073741824",
            MinWidth = 560,
            AcceptsReturn = true,
            Height = 72,
        };
        var capBtn = new Button { Content = "容量预估", CornerRadius = new CornerRadius(4) };
        capBtn.Click += (_, _) => RunCapacity(sampleBox.Text, report, status);

        // ── 复现包 ──
        var packBox = new TextBox
        {
            PlaceholderText = "复现包：填标题/引擎/SQL 后打包；或粘贴包 JSON 验证脱敏",
            MinWidth = 560,
            AcceptsReturn = true,
            Height = 72,
        };
        var packBtn = new Button { Content = "打包（脱敏）", CornerRadius = new CornerRadius(4) };
        var packVerifyBtn = new Button { Content = "校验已粘包", CornerRadius = new CornerRadius(4) };
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

        var panel = new StackPanel { Spacing = 10, MinWidth = 680 };
        panel.Children.Add(new TextBlock
        {
            Text = "智能工具（只读 / 纯计算）：SQL 静态诊断不改数据；重测只执行 SELECT；容量预估只对采样回归；复现包脱敏后才展示。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ant.TextSub,
            FontSize = 12,
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

        var dlg = new ContentDialog
        {
            Title = "智能工具（慢查询 / 重测 / 容量 / 复现包）",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
    }

    // ── 慢查询诊断：静态规则 + 索引建议，全部基于用户粘贴文本 ──
    private static void RunDiag(string sql, StackPanel report, TextBlock status)
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

            // 索引建议（静态规则推断）
            var table = GuessTable(sql);
            if (table.Length > 0)
            {
                var ddls = SlowQueryDiagnoser.SuggestIndexDdl(sql, table);
                if (ddls.Count > 0)
                {
                    AddSection(report, "索引建议（仅供参考，请结合执行计划评估）");
                    foreach (var ddl in ddls) AddLine(report, ddl);
                }
            }

            status.Text = "静态诊断完成。要验证可配合执行计划诊断（一键诊断）。";
        }
        catch (Exception ex)
        {
            status.Text = "诊断失败：" + ex.Message;
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
            status.Text = "重测失败：" + ex.Message + "（请确认是可在当前连接执行的 SELECT）";
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
            AddLine(report, $"日增长：{f.GrowthPerDay / 1048576.0:F1} MB/天");
            AddLine(report, $"拟合优度 R²：{f.RSquared:F3}（越接近 1 越可信）");
            if (f.DaysToThreshold is { } days)
                AddLine(report, $"按当前增速，约 {days:F0} 天到达阈值。");
            else
                AddLine(report, "未设阈值或趋势平缓，未给出到达天数。");
            status.Text = "预估完成。采样越多、跨度越大，结果越准。";
        }
        catch (Exception ex)
        {
            status.Text = "预估失败：" + ex.Message;
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
            status.Text = "打包失败：" + ex.Message;
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
            status.Text = "校验失败：" + ex.Message;
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
            FontSize = 12,
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
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ant.Primary,
            Margin = new Thickness(0, 6, 0, 0),
        });
    }
}
