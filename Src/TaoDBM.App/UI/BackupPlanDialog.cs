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
                status.Text = "计划已生成（仅预览，未执行任何操作）。";
            }
            catch (Exception ex)
            {
                status.Text = "生成失败：" + ex.Message;
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
            Text = "生成未来 7 天的备份槽位计划与保留清理预览；只做计算预览，不执行备份或删除。",
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.TextSub,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(row1);
        panel.Children.Add(row2);
        panel.Children.Add(output);
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
}