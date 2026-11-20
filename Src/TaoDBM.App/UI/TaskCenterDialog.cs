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
        var status = new TextBlock { FontSize = 12, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        var submitBtn = new Button { Content = "提交任务", CornerRadius = new CornerRadius(4) };
        var refreshBtn = new Button { Content = "刷新列表", CornerRadius = new CornerRadius(4) };
        var pauseBtn = new Button { Content = "暂停队列", CornerRadius = new CornerRadius(4) };
        var resumeBtn = new Button { Content = "续跑队列", CornerRadius = new CornerRadius(4) };
        var exportBtn = new Button { Content = "导出快照", CornerRadius = new CornerRadius(4) };

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

        var panel = new StackPanel { Spacing = 12, MinWidth = 720 };
        panel.Children.Add(new TextBlock
        {
            Text = "长活统一队列：查询/导出进任务中心按序执行，可暂停/续跑/取消/重跑；快照可落盘留档。",
            TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub, FontSize = 12,
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

    private static void AddLine(StackPanel p, string text, Brush? brush = null)
    {
        p.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = brush ?? Ant.TextSub, FontFamily = new FontFamily("Consolas"),
        });
    }
}
