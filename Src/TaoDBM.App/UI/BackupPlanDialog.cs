using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using System.Text;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>备份计划对话框：基于 BackupPlanner 生成未来 7 天备份槽位计划、下次执行时间与保留清理预览。
/// 纯只读计算，不执行任何备份或删除操作；清理清单仅为预览参考，实际执行由任务中心触发。</summary>
public static class BackupPlanDialog
{
    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var fullBox = new TextBox { PlaceholderText = "全量间隔（小时）", Text = "168", MinWidth = 110 };
        var incrBox = new TextBox { PlaceholderText = "增量间隔（小时）", Text = "24", MinWidth = 110 };
        var retainBox = new TextBox { PlaceholderText = "保留全量份数", Text = "3", MinWidth = 110 };
        var anchorBox = new TextBox { PlaceholderText = "起点 yyyy-MM-dd HH:mm，留空为现在", MinWidth = 220 };
        var output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 280,
            FontFamily = new FontFamily("Consolas"),
            FontSize = AntTokens.FontMinor,
            Text = "填好策略后点「生成计划」。",
        };

        var runBtn = new Button { Content = "生成计划", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        runBtn.Click += (_, _) => Run(fullBox.Text, incrBox.Text, retainBox.Text, anchorBox.Text, output, status);

        // 发现153：挂到任务中心——策略合法即落 SqlScheduler 两条 Kind=backup 条目，到点由任务中心分派 BackupPlanRunner 执行
        var mountBtn = new Button
        {
            Content = "挂到任务中心（每 N 小时全量 / 每 M 小时增量）",
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
        };
        var schedList = new StackPanel { Spacing = 4 };
        var schedStatus = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        mountBtn.Click += async (_, _) =>
        {
            try
            {
                int full = int.TryParse(fullBox.Text?.Trim(), out var f) ? f : 0;
                int incr = int.TryParse(incrBox.Text?.Trim(), out var i) ? i : 0;
                int retain = int.TryParse(retainBox.Text?.Trim(), out var r) ? r : 0;
                string policyJson = BackupPlanRunner.PolicyJson(full, incr, retain);   // 非法策略人话报错，不落调度
                string db = info.Db.Length > 0 ? info.Db : info.Profile.Database;
                var existing = SqlScheduler.List().Where(e => e.Kind == "backup" && e.Db == db).ToList();
                if (existing.Any(e => e.Name.Contains("全量", StringComparison.Ordinal)))
                {
                    schedStatus.Text = $"库 {db} 已有全量备份计划：先在下方列表删掉旧的再挂，避免重复备份。";
                    return;
                }
                var fullEntry = SqlScheduler.Add($"备份计划-全量-{db}", db, policyJson, full * 60, "backup");
                AppLog.Write($"[备份计划] 挂载 full entry={fullEntry.Id} db={db} every={full}h");
                if (incr > 0)
                {
                    var incrEntry = SqlScheduler.Add($"备份计划-增量-{db}", db, policyJson, incr * 60, "backup");
                    AppLog.Write($"[备份计划] 挂载 incr entry={incrEntry.Id} db={db} every={incr}h");
                }
                schedStatus.Text = $"已挂到任务中心：到点自动执行（全量每 {full} 小时{(incr > 0 ? $"，增量每 {incr} 小时" : "")}）。";
                RefreshSched(schedList, db, schedStatus);
            }
            catch (ArgumentException ex) { schedStatus.Text = ex.Message; }
            catch (Exception ex) { schedStatus.Text = HumanError.Say(ex, "挂载失败"); }
            await Task.CompletedTask;
        };

        RefreshSched(schedList, info.Db.Length > 0 ? info.Db : info.Profile.Database, schedStatus);

        static void Run(string fullText, string incrText, string retainText, string anchorText, TextBox output, TextBlock status)
        {
            try
            {
                int full = int.TryParse(fullText.Trim(), out var f) ? f : 0;
                int incr = int.TryParse(incrText.Trim(), out var i) ? i : 0;
                int retain = int.TryParse(retainText.Trim(), out var r) ? r : 0;
                if (full <= 0) { status.Text = "全量间隔必须大于 0。"; return; }
                if (retain < 0) { status.Text = "保留全量份数不能为负。"; return; }

                DateTimeOffset anchor = DateTimeOffset.Now;
                var t = anchorText.Trim();
                if (t.Length > 0)
                {
                    if (!DateTimeOffset.TryParseExact(t, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out anchor)
                        && !DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out anchor))
                    {
                        status.Text = "起点格式不对，用 yyyy-MM-dd HH:mm 或留空表示现在。";
                        return;
                    }
                }

                var policy = new BackupPolicy(full, Math.Max(incr, 0), retain, anchor);
                var now = DateTimeOffset.Now;
                var slots = BackupPlanner.Schedule(policy, now, now.AddDays(7));

                var sb = new StringBuilder();
                sb.AppendLine($"未来 7 天共 {slots.Count} 个备份槽位：");
                foreach (var s in slots.Take(40))
                    sb.AppendLine($"  {s.At:yyyy-MM-dd HH:mm}  {s.Kind}");
                if (slots.Count > 40) sb.AppendLine($"  …（其余 {slots.Count - 40} 个省略）");

                var next = BackupPlanner.NextRun(policy, now);
                sb.AppendLine(next is null ? "下次执行：无" : $"下次执行：{next:yyyy-MM-dd HH:mm}");

                var existing = BackupPlanner.Schedule(policy, now.AddDays(-30), now);
                var prune = BackupPlanner.Prune(existing, policy);
                sb.AppendLine();
                sb.AppendLine($"保留清理预览（按过去 30 天槽位演示）：保留全量 {prune.RetainedFulls} 份、增量 {prune.RetainedIncrs} 份，过期 {prune.Expired.Count} 个：");
                foreach (var e in prune.Expired.Take(10))
                    sb.AppendLine($"  过期 {e.Kind}：{e.At:yyyy-MM-dd HH:mm}");

                output.Text = sb.ToString();
                status.Text = "计划已生成。挂到任务中心后到点自动执行。";   // 发现153：文案归真
            }
            catch (Exception ex)
            {
                status.Text = HumanError.Say(ex, "生成失败");
            }
        }

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row1.Children.Add(fullBox);
        row1.Children.Add(incrBox);
        row1.Children.Add(retainBox);
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row2.Children.Add(anchorBox);
        row2.Children.Add(runBtn);

        var panel = new StackPanel { Spacing = 10, MinWidth = 620 };
        panel.Children.Add(new TextBlock
        {
            Text = "生成未来 7 天的备份槽位计划与保留清理预览；挂到任务中心后到点自动执行（执行走任务中心分派，结果落备份账）。",
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.TextSub,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(row1);
        panel.Children.Add(row2);
        panel.Children.Add(output);
        panel.Children.Add(mountBtn);   // 发现153：挂到任务中心
        panel.Children.Add(schedStatus);
        panel.Children.Add(new ScrollViewer { Content = schedList, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "备份计划",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        await dlg.ShowThemedAsync();
    }

    /// <summary>发现153：已挂备份计划列表（Kind=backup），带停用 / 启用 / 删除，操作落 AppLog。</summary>
    private static void RefreshSched(StackPanel list, string db, TextBlock status)
    {
        list.Children.Clear();
        var entries = SqlScheduler.List().Where(e => e.Kind == "backup" && e.Db == db).ToList();
        if (entries.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "还没有挂载备份计划。填好策略后点「挂到任务中心」。",
                FontSize = AntTokens.FontMinor,
                Foreground = Ant.TextSub,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var e in entries)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock
            {
                Text = $"{e.Name} · 每 {e.IntervalMinutes / 60.0:0.#} 小时 · 下次 {e.NextRunAt:MM-dd HH:mm} · {(e.Enabled ? "启用" : "已停用")}",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = AntTokens.FontMinor,
            });
            var toggleBtn = new Button
            {
                Content = e.Enabled ? "停用" : "启用",
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
                Padding = new Thickness(10, 2, 10, 2),
                FontSize = AntTokens.FontMinor,
            };
            var delBtn = new Button
            {
                Content = "删除",
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
                Padding = new Thickness(10, 2, 10, 2),
                FontSize = AntTokens.FontMinor,
                Foreground = Ant.Error,
            };
            var entryId = e.Id;
            var wasEnabled = e.Enabled;
            toggleBtn.Click += (_, _) =>
            {
                SqlScheduler.Update(entryId, null, null, null, null, !wasEnabled);
                AppLog.Write($"[备份计划] {(wasEnabled ? "停用" : "启用")} entry={entryId}");
                status.Text = wasEnabled ? "已停用：到点不再执行，可再启用。" : "已启用：到点恢复自动执行。";
                RefreshSched(list, db, status);
            };
            delBtn.Click += (_, _) =>
            {
                SqlScheduler.Delete(entryId);
                AppLog.Write($"[备份计划] 删除 entry={entryId}");
                status.Text = "已删除该备份计划。";
                RefreshSched(list, db, status);
            };
            row.Children.Add(toggleBtn);
            row.Children.Add(delBtn);
            list.Children.Add(row);
        }
    }
}