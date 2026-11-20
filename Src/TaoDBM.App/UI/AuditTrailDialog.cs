using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>留痕审计对话框（设计稿 §8）：改数留痕列表 + 查看差异 + 单条回滚。
/// 账目来自 ChangeAudit JSONL（行编辑/删除时捕获）；回滚生成逆 SQL → 预览 → 危险确认后执行；
/// 超过回滚窗（7 天）的账目如实标灰提示人工核对，不假承诺。</summary>
public static class AuditTrailDialog
{
    /// <summary>回滚窗天数：超过标灰提示（事务日志可能已清理，回滚前先人工核对）。</summary>
    public const int UndoWindowDays = 7;

    private static string File => TaoPaths.FilePath("change-audit.jsonl");

    /// <summary>追加一条留痕账（JSONL 一行一账，损坏不影响既有账目）。</summary>
    public static void Append(AuditEntry entry)
    {
        if (!entry.HasChanges) return;
        System.IO.File.AppendAllText(File, System.Text.Json.JsonSerializer.Serialize(entry, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) + "\n");
    }

    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        var prov = info.Provider ?? ProviderFactory.Create(info.Profile);
        var listPanel = new StackPanel { Spacing = 6 };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        async Task Refresh()
        {
            listPanel.Children.Clear();
            var entries = ChangeAudit.LoadJsonl(File);
            if (entries.Count == 0)
            {
                listPanel.Children.Add(new TextBlock { Text = "还没有留痕账：在数据网格里编辑或删除行后会自动记在这里。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });
            }
            foreach (var e in Enumerable.Reverse(entries.ToList()))
            {
                var ageDays = (DateTime.UtcNow - DateTime.Parse(e.TimestampUtc, null, System.Globalization.DateTimeStyles.RoundtripKind)).TotalDays;
                bool expired = ageDays >= UndoWindowDays;
                var row = new StackPanel { Spacing = 4, Orientation = Orientation.Vertical };
                var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                head.Children.Add(new TextBlock
                {
                    Text = $"{e.TimestampUtc[..19].Replace('T', ' ')} · {e.Table} · {e.PkColumn}={e.PkValue} · {e.Changes.Count} 列",
                    FontSize = AntTokens.FontMinor,
                    Foreground = expired ? Ant.TextSub : Ant.Text,
                });
                row.Children.Add(head);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var diffBtn = new Button { Content = "查看差异", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), FontSize = AntTokens.FontCaption };
                diffBtn.Click += (_, _) => ShowDiff(owner, e);
                actions.Children.Add(diffBtn);
                var undoBtn = new Button
                {
                    Content = expired ? "回滚（已过回滚窗）" : "回滚",
                    CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
                    FontSize = AntTokens.FontCaption,
                    Foreground = expired ? Ant.TextSub : Ant.Error,
                };
                undoBtn.Click += (_, _) => _ = UndoAsync(owner, prov, info.Db, e, status, Refresh);
                actions.Children.Add(undoBtn);
                row.Children.Add(actions);
                if (expired)
                    row.Children.Add(new TextBlock { Text = "已过回滚窗（事务日志可能已清理），回滚前请先人工核对当前值。", FontSize = AntTokens.FontCaption, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });
                listPanel.Children.Add(new Border { Child = row, Padding = new Thickness(8), BorderBrush = Ant.Border, BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg) });
            }
            status.Text = $"共 {entries.Count} 条留痕账。";
        }

        await Refresh();
        var panel = new StackPanel { Spacing = 10, MinWidth = 560 };
        panel.Children.Add(new ScrollViewer { Content = listPanel, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(status);
        var dlg = new ContentDialog
        {
            Title = "留痕审计 — 每笔改动可查、可回滚",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        await dlg.ShowThemedAsync();
    }

    private static void ShowDiff(MainWindow owner, AuditEntry e)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
        foreach (var c in e.Changes)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{c.Column}：\n  改前：{c.OldValue ?? "NULL"}\n  改后：{c.NewValue ?? "NULL"}",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = AntTokens.FontMinor,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        panel.Children.Add(new TextBlock { Text = $"语句：{e.Statement}", FontSize = AntTokens.FontCaption, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });
        var dlg = new ContentDialog { Title = "改动差异", Content = new ScrollViewer { Content = panel, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, CloseButtonText = "关闭", XamlRoot = owner.Content.XamlRoot };
        _ = dlg.ShowThemedAsync();
    }

    private static StackPanel BuildUndoConfirmPanel(string undo)
    {
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(new TextBlock { Text = "将执行以下逆 SQL 把该行改回旧值：", FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap });
        p.Children.Add(new TextBlock { Text = undo, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap });
        p.Children.Add(new TextBlock { Text = "回滚前请确认当前值未被他人改过。", FontSize = AntTokens.FontCaption, Foreground = Ant.Error, TextWrapping = TextWrapping.Wrap });
        return p;
    }

    private static async Task UndoAsync(MainWindow owner, IDbProvider prov, string db, AuditEntry e, TextBlock status, Func<Task> refresh)
    {
        var undo = ChangeAudit.UndoSql(e);
        if (undo.Length == 0) { status.Text = "该账没有可回滚的改动。"; return; }
        var confirm = new ContentDialog
        {
            Title = "回滚确认（危险操作）",
            Content = BuildUndoConfirmPanel(undo),
            PrimaryButtonText = "执行回滚",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = owner.Content.XamlRoot,
        };
        if (await confirm.ShowThemedAsync() != ContentDialogResult.Primary) return;
        try
        {
            await prov.Execute(db, undo);
            AppLog.Write($"[留痕回滚] table={e.Table} pk={e.PkColumn}={e.PkValue}");
            status.Text = "回滚已执行。";
            await refresh();
        }
        catch (Exception ex)
        {
            status.Text = "回滚失败：" + ex.Message;
        }
    }
}
