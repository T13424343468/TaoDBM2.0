using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;
using TaoDBM.Infrastructure.Providers;

namespace TaoDBM.UI;

/// <summary>批量清理对话框：为删除类操作生成小批量分批计划（行数估算+逐批 SQL），
/// 默认先执行少量批次验证。执行走 DraftOps 逐条勾选确认，不静默删数据。</summary>
public static class BatchCleanupDialog
{
    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        var prov = info.Provider ?? ProviderFactory.Create(info.Profile);
        var db = CurrentDb(info);
        var tableBox = new TextBox { PlaceholderText = "表名（当前库）", MinWidth = 160 };
        var whereBox = new TextBox { PlaceholderText = "WHERE 条件（留空=全表）", MinWidth = 280 };
        var pkBox = new TextBox { PlaceholderText = "主键列（如 id）", MinWidth = 140 };
        var batchBox = new TextBox { PlaceholderText = "每批行数（默认 1000）", MinWidth = 160 };
        var report = new StackPanel { Spacing = 8 };
        var status = new TextBlock { FontSize = 12, Foreground = Ant.Error, TextWrapping = TextWrapping.Wrap };

        var planBtn = new Button { Content = "生成分批计划", CornerRadius = new CornerRadius(4) };
        var execBtn = new Button { Content = "按批执行（草稿确认）", CornerRadius = new CornerRadius(4), IsEnabled = false };
        BatchCleanupPlan? plan = null;

        planBtn.Click += async (_, _) =>
        {
            var table = (tableBox.Text ?? "").Trim();
            var pk = (pkBox.Text ?? "").Trim();
            if (table.Length == 0) { status.Text = "请填写表名。"; return; }
            if (pk.Length == 0) { status.Text = "请填写主键列：分批删除必须按主键推进。"; return; }
            if (db.Length == 0) { status.Text = "当前连接没有可用库，请从库节点使用本功能。"; return; }
            status.Text = "估算行数（只读 COUNT）…";
            report.Children.Clear();
            try
            {
                var where = (whereBox.Text ?? "").Trim();
                var whereSql = where.Length == 0 ? "" : " WHERE " + where;
                long est = await ScalarLong(prov, db, $"SELECT COUNT(*) FROM {prov.Quote(table)}{whereSql}");
                int batch = int.TryParse((batchBox.Text ?? "").Trim(), out var b) && b > 0 ? b : 1000;
                plan = BatchCleanup.Plan(table, where.Length == 0 ? null : where, pk, est, batch, prov.Profile.Engine);
                if (plan.BatchSqls.Count == 0) { status.Text = "估算行数为 0，没有可清理的数据。"; return; }
                execBtn.IsEnabled = true;
                AddSection(report, $"表 {table}：约 {est} 行待清理，共 {plan.TotalBatches} 批 × {batch} 行");
                AddLine(report, $"首批语句：{plan.BatchSqls[0]}");
                AddLine(report, "执行策略：默认每次执行 1 批，跑完复核行数再继续；全部语句都要逐条勾选确认。");
                status.Text = "计划就绪：确认无误后点「按批执行」。";
            }
            catch (Exception ex) { status.Text = "计划失败：" + ex.Message; }
            await Task.CompletedTask;
        };
        execBtn.Click += async (_, _) =>
        {
            if (plan is null || plan.BatchSqls.Count == 0) { status.Text = "请先生成分批计划。"; return; }
            var drafts = plan.BatchSqls.Take(1).ToList();
            await DraftOps.ConfirmExecuteAsync(owner, prov, $"批量清理 {plan.Table}（首批草稿）", drafts, status);
            status.Text = "首批执行完毕（如确认过）：复核行数后再继续下一批；全表清理请分多次执行。";
            await Task.CompletedTask;
        };

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row1.Children.Add(tableBox); row1.Children.Add(whereBox); row1.Children.Add(pkBox); row1.Children.Add(batchBox);
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row2.Children.Add(planBtn); row2.Children.Add(execBtn);

        var panel = new StackPanel { Spacing = 12, MinWidth = 720 };
        panel.Children.Add(new TextBlock
        {
            Text = "危险操作：删除类语句全部走草稿逐条确认；分批执行降低长事务与锁表风险，默认首批一小步。",
            TextWrapping = TextWrapping.Wrap, Foreground = Ant.Error, FontSize = 12,
        });
        panel.Children.Add(row1);
        panel.Children.Add(row2);
        panel.Children.Add(new ScrollViewer { Content = report, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "批量清理",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
        await Task.CompletedTask;
    }

    private static string CurrentDb(NodeInfo info)
    {
        if (info.Db.Length > 0) return info.Db;
        if (info.Profile.Database.Length > 0) return info.Profile.Database;
        return "";
    }

    private static async Task<long> ScalarLong(IDbProvider prov, string db, string sql)
    {
        var rs = await prov.Execute(db, sql);
        if (rs.Count == 0 || rs[0].Rows.Count == 0) return 0;
        var v = rs[0].Rows[0][0];
        return v is null ? 0 : long.TryParse(v.ToString(), out var n) ? n : 0;
    }

    private static void AddLine(StackPanel p, string text, Brush? brush = null, int indent = 0)
    {
        p.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12, Margin = new Thickness(indent * 16, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap, Foreground = brush ?? Ant.TextSub,
            FontFamily = new FontFamily("Consolas"),
        });
    }

    private static void AddSection(StackPanel p, string title)
    {
        p.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ant.Primary, Margin = new Thickness(0, 6, 0, 0),
        });
    }
}
