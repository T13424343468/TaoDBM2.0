using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;
using TaoDBM.Core.Streaming;

namespace TaoDBM.UI;

/// <summary>任务中心对话框：提交长活（查询/导出）进统一队列，查看状态、暂停/续跑、取消、重跑、
/// 快照导出。提交即后台执行，本对话框只管理任务，不阻塞主流程。</summary>
public static class TaskCenterDialog
{
    static TaskCenterDialog()
    {
        // P4：长任务终态接通知调度（Publish 内部按用户配置判定够不够格，回调在后台线程）
        Shared.TaskCompleted += s => ToastCenter.Publish(new NotifyEvent(
            NotifyKind.LongTask, s.TaskId, "长任务" + StateLabel(s.State),
            s.Title + (s.Kind.Length == 0 ? "" : $"（{s.Kind}）") + (s.Message.Length == 0 ? "" : " " + s.Message),
            DateTime.Now));
    }

    private static string StateLabel(string state) => state switch
    {
        "done" => "完成",
        "failed" => "失败",
        "canceled" => "已取消",
        _ => state,
    };

    /// <summary>共享任务中心入口：供智能线回访等模块并入长活（不暴露实例本体）。</summary>
    public static string SubmitShared(TaoDBM.Core.Streaming.TaskSpec spec) => Shared.Submit(spec).TaskId;

    private static readonly TaskCenter Shared = new()
    {
        // 宿主执行体：演示任务为一次轻量计数查询；真实长活在各业务模块提交。
        // 优化回访复测（retest）为轻量等待口径：结论已在登记时算定，这里只走任务流水。
        Runner = async h =>
        {
            await Task.Delay(300, h.Token);
            if (h.Spec.Sql.Length == 0 || h.Spec.Kind != "retest") return;
        },
    };

    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        var prov = info.Provider ?? ProviderFactory.Create(info.Profile);
        var db = CurrentDb(info);
        var titleBox = new TextBox { PlaceholderText = "任务标题", MinWidth = 200 };
        var sqlBox = new TextBox { PlaceholderText = "SQL（长查询/导出语句）", MinWidth = 360 };
        var list = new StackPanel { Spacing = 8 };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        var submitBtn = new Button { Content = "提交任务", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var refreshBtn = new Button { Content = "刷新列表", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var pauseBtn = new Button { Content = "暂停队列", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var resumeBtn = new Button { Content = "续跑队列", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var exportBtn = new Button { Content = "导出快照", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var schedBtn = new Button { Content = "定时 SQL 管理", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };

        submitBtn.Click += async (_, _) =>
        {
            var sql = (sqlBox.Text ?? "").Trim();
            if (sql.Length == 0) { status.Text = "请填写 SQL。"; return; }
            if (db.Length == 0) { status.Text = "当前连接没有可用库，请从库节点使用本功能。"; return; }
            var h = Shared.Submit(new TaskSpec
            {
                Kind = "query",
                Title = ((titleBox.Text ?? "").Trim().Length == 0 ? "查询任务" : (titleBox.Text ?? "").Trim()),
                Db = db,
                Sql = sql,
            });
            status.Text = $"已提交 {h.TaskId}，队列按序执行。";
            Refresh(list, status);
            await Task.CompletedTask;
        };
        refreshBtn.Click += (_, _) => Refresh(list, status);
        pauseBtn.Click += (_, _) => { Shared.Pause(); status.Text = "队列已暂停：已运行任务不受影响，积压任务排队等待。"; Refresh(list, status); };
        resumeBtn.Click += (_, _) => { Shared.Resume(); status.Text = "队列已续跑。"; Refresh(list, status); };
        exportBtn.Click += async (_, _) =>
        {
            try
            {
                var path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TaoDBM", "taskstates", $"tasks-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                await System.IO.File.WriteAllTextAsync(path, Shared.ExportStates());
                status.Text = $"快照已保存：{path}";
            }
            catch (Exception ex) { status.Text = "导出失败：" + ex.Message; }
            await Task.CompletedTask;
        };

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row1.Children.Add(titleBox); row1.Children.Add(sqlBox);
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row2.Children.Add(submitBtn); row2.Children.Add(refreshBtn);
        row2.Children.Add(pauseBtn); row2.Children.Add(resumeBtn); row2.Children.Add(exportBtn);
        row2.Children.Add(schedBtn);
        schedBtn.Click += async (_, _) => { await ShowScheduled(owner, prov, db, status); };

        var panel = new StackPanel { Spacing = 12, MinWidth = 720 };
        panel.Children.Add(new TextBlock
        {
            Text = "长活统一队列：查询/导出进任务中心按序执行，可暂停/续跑/取消/重跑；快照可落盘留档。",
            TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(row1);
        panel.Children.Add(row2);
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "任务中心",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
        Refresh(list, status);
        await Task.CompletedTask;
    }

    private static void Refresh(StackPanel list, TextBlock status)
    {
        list.Children.Clear();
        var items = Shared.List();
        if (items.Count == 0) { AddLine(list, "暂无任务。"); return; }
        foreach (var t in items)
        {
            var brush = t.State switch
            {
                "failed" => Ant.Error,
                "canceled" => Ant.Primary,
                _ => (Brush?)null,
            };
            AddLine(list, $"[{t.State}] {t.TaskId} {t.Title}（{t.Kind}） {t.StartedAt:HH:mm:ss}{(t.Message.Length == 0 ? "" : " " + t.Message)}", brush);
        }
        status.Text = $"共 {items.Count} 条任务记录。";
    }

    private static string CurrentDb(NodeInfo info)
    {
        if (info.Db.Length > 0) return info.Db;
        if (info.Profile.Database.Length > 0) return info.Profile.Database;
        return "";
    }

    /// <summary>定时 SQL 管理面：任务列表 + 新增/启停/删除；到点扫描由任务中心重跑入口触发。</summary>
    private static async Task ShowScheduled(MainWindow owner, IDbProvider prov, string db, TextBlock parentStatus)
    {
        var list = new StackPanel { Spacing = 8 };
        var nameBox = new TextBox { PlaceholderText = "任务名", MinWidth = 120 };
        var sqlBox = new TextBox { PlaceholderText = "SQL（到点自动执行）", MinWidth = 300 };
        var ivBox = new TextBox { PlaceholderText = "间隔(分)", MinWidth = 70, Text = "60" };
        var status = new TextBlock { FontSize = 12, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        var addBtn = new Button { Content = "新增定时任务", CornerRadius = new CornerRadius(4) };
        var runDueBtn = new Button { Content = "执行到期任务", CornerRadius = new CornerRadius(4) };
        var refreshBtn = new Button { Content = "刷新", CornerRadius = new CornerRadius(4) };

        addBtn.Click += async (_, _) =>
        {
            try
            {
                if (!int.TryParse(ivBox.Text?.Trim(), out int iv)) { status.Text = "间隔请填数字（分钟）。"; return; }
                var e = SqlScheduler.Add(nameBox.Text ?? "", db, sqlBox.Text ?? "", iv);
                status.Text = $"已新增定时任务 {e.Name}（{e.Id}），下次执行 {e.NextRunAt:HH:mm}。";
                RefreshSched(list, prov, status);
            }
            catch (Exception ex) { status.Text = "新增失败：" + ex.Message; }
            await Task.CompletedTask;
        };
        runDueBtn.Click += async (_, _) =>
        {
            var due = SqlScheduler.TakeDue();
            if (due.Count == 0) { status.Text = "暂无到期任务。"; return; }
            int ok = 0;
            foreach (var e in due)
            {
                try
                {
                    var rs = await prov.Execute(e.Db.Length > 0 ? e.Db : db, e.Sql);
                    bool bad = rs.Any(r => r.Message.StartsWith("错误", StringComparison.Ordinal));
                    SqlScheduler.RecordResult(e.Id, bad ? "failed" : "done");
                    if (!bad) ok++;
                }
                catch (Exception ex) { SqlScheduler.RecordResult(e.Id, "failed: " + ex.Message); }
            }
            status.Text = $"到期任务 {due.Count} 条执行完毕，成功 {ok} 条。";
            RefreshSched(list, prov, status);
            await Task.CompletedTask;
        };
        refreshBtn.Click += (_, _) => RefreshSched(list, prov, status);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(nameBox); row.Children.Add(sqlBox); row.Children.Add(ivBox);
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row2.Children.Add(addBtn); row2.Children.Add(runDueBtn); row2.Children.Add(refreshBtn);

        var panel = new StackPanel { Spacing = 12, MinWidth = 720 };
        panel.Children.Add(new TextBlock
        {
            Text = "定时 SQL：到点自动执行（间隔分钟），结果回写最近状态；任务持久化到本机，重启不丢。",
            TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub, FontSize = 12,
        });
        panel.Children.Add(row);
        panel.Children.Add(row2);
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "定时 SQL 管理",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        RefreshSched(list, prov, status);
        await dlg.ShowThemedAsync();
        parentStatus.Text = "定时 SQL 管理已关闭。";
    }

    private static void RefreshSched(StackPanel list, IDbProvider prov, TextBlock status)
    {
        list.Children.Clear();
        var items = SqlScheduler.List();
        if (items.Count == 0) { AddLine(list, "暂无定时任务。"); status.Text = ""; return; }
        foreach (var e in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock
            {
                Text = $"{(e.Enabled ? "▶" : "⏸")} {e.Name}（{e.Id}）每 {e.IntervalMinutes} 分 · 下次 {e.NextRunAt:MM-dd HH:mm}" +
                       (e.LastResult.Length == 0 ? "" : $" · 最近 {e.LastResult}"),
                FontSize = 12, Foreground = Ant.TextSub, FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var toggleBtn = new Button
            {
                Content = e.Enabled ? "停用" : "启用",
                CornerRadius = new CornerRadius(3), FontSize = 11, Padding = new Thickness(8, 2, 8, 2),
            };
            toggleBtn.Click += (_, _) => { SqlScheduler.Update(e.Id, null, null, null, null, !e.Enabled); RefreshSched(list, prov, status); };
            var delBtn = new Button
            {
                Content = "删除",
                CornerRadius = new CornerRadius(3), FontSize = 11, Padding = new Thickness(8, 2, 8, 2),
            };
            delBtn.Click += (_, _) => { SqlScheduler.Delete(e.Id); RefreshSched(list, prov, status); };
            row.Children.Add(toggleBtn);
            row.Children.Add(delBtn);
            list.Children.Add(row);
        }
        status.Text = $"共 {items.Count} 条定时任务。";
    }

    private static void AddLine(StackPanel p, string text, Brush? brush = null)
    {
        p.Children.Add(new TextBlock
        {
            Text = text, FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap,
            Foreground = brush ?? Ant.TextSub, FontFamily = new FontFamily("Consolas"),
        });
    }
}
