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
        Shared.TaskCompleted += s =>
        {
            AppLog.Write($"[任务中心] 终态 id={s.TaskId} state={s.State} msg={s.Message}");   // 走查 S25 实效断言锚点
            ToastCenter.Publish(new NotifyEvent(
                NotifyKind.LongTask, s.TaskId, "长任务" + StateLabel(s.State),
                s.Title + (s.Kind.Length == 0 ? "" : $"（{s.Kind}）") + (s.Message.Length == 0 ? "" : " " + s.Message),
                DateTime.Now));
            ComboOnTaskCompleted(s);   // 发现142：组合步骤终态回收，全部终态后发汇总
        };
    }

    // 发现142：组合执行跟踪——组合键→(组合名,总步数,成功数,失败数)；任务Id→组合键。
    // 全部步骤终态后发一条汇总通知（不再每步各发一条打扰用户）。
    private static readonly object _comboLock = new();
    private static readonly Dictionary<string, (string ComboName, int Total, int Done, int Failed)> _comboPending = new();
    private static readonly Dictionary<string, string> _taskToCombo = new();

    private static void ComboOnTaskCompleted(TaskSnapshot s)
    {
        lock (_comboLock)
        {
            if (!_taskToCombo.Remove(s.TaskId, out var key)) return;
            if (!_comboPending.TryGetValue(key, out var st)) return;
            int done = st.Done + (s.State == "done" ? 1 : 0);
            int failed = st.Failed + (s.State == "failed" ? 1 : 0);
            if (st.Total - done - failed > 0) { _comboPending[key] = (st.ComboName, st.Total, done, failed); return; }
            _comboPending.Remove(key);
            var summary = $"组合「{st.ComboName}」{st.Total} 步执行完毕：成功 {done}、失败 {failed}。";
            AppLog.Write($"[任务组合] {summary}");
            ToastCenter.Publish(new NotifyEvent(NotifyKind.LongTask, $"combo-{key}", "组合执行完毕", summary, DateTime.Now));
        }
    }

    /// <summary>发现142：把组合步骤按序提交进任务队列，标题带 [组合名] 前缀；返回组合键供跟踪。</summary>
    private static string RunCombo(string comboName, IReadOnlyList<TaskCombos.ComboStep> steps, IDbProvider prov)
    {
        var key = $"{comboName}@{DateTime.Now:HHmmss}";
        lock (_comboLock) _comboPending[key] = (comboName, steps.Count, 0, 0);
        foreach (var step in steps)
        {
            var h = Shared.Submit(new TaskSpec
            {
                Kind = "query",
                Title = $"[{comboName}] {step.Title}",
                Db = step.Db,
                Sql = step.Sql,
                Provider = prov,
            });
            lock (_comboLock) _taskToCombo[h.TaskId] = key;
        }
        AppLog.Write($"[任务组合] 组合「{comboName}」已按序提交 {steps.Count} 步");
        return key;
    }

    // 发现142：命令行 -run-task 排队区——App 启动解析后暂存，任务中心打开时排水执行。
    private static readonly object _pendingLock = new();
    private static readonly List<string> _pendingRunTaskIds = new();

    /// <summary>发现142：命令行 -run-task &lt;id&gt; 命中后把定时任务 Id 排进待执行队列（App 启动时调用）。</summary>
    public static void QueueScheduledRun(IEnumerable<string> taskIds)
    {
        var ids = (taskIds ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (ids.Count == 0) return;
        lock (_pendingLock) _pendingRunTaskIds.AddRange(ids);
        AppLog.Write($"[任务中心] 命令行 -run-task 排队 {ids.Count} 条定时任务待执行");
    }

    /// <summary>发现142：排水——把排队的定时任务逐条按 Id 找到并立即执行，结果回写定时任务账。</summary>
    private static async Task DrainScheduledRunAsync(IDbProvider prov, string db)
    {
        List<string> pending;
        lock (_pendingLock) { pending = new List<string>(_pendingRunTaskIds); _pendingRunTaskIds.Clear(); }
        foreach (var id in pending)
        {
            var e = SqlScheduler.List().FirstOrDefault(x => x.Id == id);
            if (e is null) { AppLog.Write($"[任务中心] -run-task 找不到定时任务 {id}，已跳过"); continue; }
            try
            {
                var rs = await prov.Execute(e.Db.Length > 0 ? e.Db : db, e.Sql);
                bool bad = rs.Any(r => r.IsError || r.Message.StartsWith("错误", StringComparison.Ordinal) || r.Message.Contains("没成功："));
                SqlScheduler.RecordResult(e.Id, bad ? "failed" : "done");
                AppLog.Write($"[任务中心] -run-task 已执行 {e.Name}（{e.Id}）：{(bad ? "failed" : "done")}");
            }
            catch (Exception ex) { SqlScheduler.RecordResult(e.Id, "failed: " + ex.Message); }
        }
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
        // 整改二批 #05（2026-11-23）：Runner 接真执行体——query/export 按规格真跑 SQL，
        // 取消令牌全链路传递（出队前取消即跳过，执行前检查即中断排队任务）；
        // 完成摘要（行数/影响行数/耗时）回写任务快照。retest 为轻量等待口径：结论已在登记时算定，这里只走任务流水。
        Runner = async h =>
        {
            var spec = h.Spec;
            if (spec.Kind == "retest") { await Task.Delay(300, h.Token); return null; }
            if (spec.Sql.Length == 0) return "空 SQL，未执行。";
            var prov = spec.Provider ?? throw new InvalidOperationException("任务未携带连接执行体，无法执行。");
            h.Token.ThrowIfCancellationRequested();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rs = await prov.Execute(spec.Db, spec.Sql);
            sw.Stop();
            var affected = rs.LastOrDefault(r => r.Affected >= 0);
            if (affected is not null) return $"影响 {affected.Affected} 行 · {sw.Elapsed.TotalMilliseconds:N0} ms";
            long? total = rs.FirstOrDefault(r => r.TotalRows is not null)?.TotalRows;
            if (total is long t) return $"共 {t:N0} 行 · {sw.Elapsed.TotalMilliseconds:N0} ms";
            int rows = rs.Sum(r => r.Rows.Count);
            return $"{rows:N0} 行 · {sw.Elapsed.TotalMilliseconds:N0} ms";
        },
    };

    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        var prov = info.Provider ?? ProviderFactory.Create(info.Profile);
        var db = CurrentDb(info);
        await DrainScheduledRunAsync(prov, db);   // 发现142：命令行 -run-task 排水
        var titleBox = new TextBox { PlaceholderText = "任务标题", MinWidth = 200 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(titleBox, "tc-title");
        var sqlBox = new TextBox { PlaceholderText = "SQL（长查询/导出语句）", MinWidth = 360 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(sqlBox, "tc-sql");
        var list = new StackPanel { Spacing = 8 };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        var submitBtn = new Button { Content = "提交任务", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(submitBtn, "tc-submit");
        var refreshBtn = new Button { Content = "刷新列表", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var pauseBtn = new Button { Content = "暂停队列", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var resumeBtn = new Button { Content = "续跑队列", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var exportBtn = new Button { Content = "导出快照", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var schedBtn = new Button { Content = "定时 SQL 管理", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        // 发现142：组合执行三件套——勾选任务行后组合执行/保存组合/加载组合
        var comboNameBox = new TextBox { PlaceholderText = "组合名（保存/加载用）", MinWidth = 160 };
        var comboRunBtn = new Button { Content = "组合执行", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var comboSaveBtn = new Button { Content = "保存组合", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var comboLoadBtn = new Button { Content = "加载组合执行", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };

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
                Provider = prov,   // #05 真实执行：携带连接执行体
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
                var path = TaoPaths.FilePathIn("taskstates", $"tasks-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
                await System.IO.File.WriteAllTextAsync(path, Shared.ExportStates());
                status.Text = $"快照已保存：{path}";
            }
            catch (Exception ex) { status.Text = HumanError.Say(ex, "导出失败"); }
            await Task.CompletedTask;
        };

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row1.Children.Add(titleBox); row1.Children.Add(sqlBox);
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row2.Children.Add(submitBtn); row2.Children.Add(refreshBtn);
        row2.Children.Add(pauseBtn); row2.Children.Add(resumeBtn); row2.Children.Add(exportBtn);
        row2.Children.Add(schedBtn);
        schedBtn.Click += async (_, _) => { await ShowScheduled(owner, prov, db, status); };
        // 发现142：组合行——勾选任务行后组合执行/保存/加载
        var comboRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        comboRow.Children.Add(comboNameBox);
        comboRow.Children.Add(comboRunBtn); comboRow.Children.Add(comboSaveBtn); comboRow.Children.Add(comboLoadBtn);
        comboRunBtn.Click += (_, _) =>
        {
            var picked = PickedSnaps();
            if (picked.Count < 2) { status.Text = "组合执行至少勾选 2 条任务。"; return; }
            var steps = TaskCombos.Normalize(picked.Select(t => new TaskCombos.ComboStep(t.Title, t.Spec.Db.Length > 0 ? t.Spec.Db : db, t.Spec.Sql)));
            if (steps.Count < 2) { status.Text = "勾选的任务里有效步骤不足 2 条（空标题/空 SQL 已剔除）。"; return; }
            RunCombo("手动组合", steps, prov);
            status.Text = $"组合已按序提交 {steps.Count} 步，全部终态后会汇总通知。";
            Refresh(list, status);
        };
        comboSaveBtn.Click += (_, _) =>
        {
            var picked = PickedSnaps();
            if (picked.Count < 2) { status.Text = "保存组合至少勾选 2 条任务。"; return; }
            var name = (comboNameBox.Text ?? "").Trim();
            if (name.Length == 0) name = $"组合 {DateTime.Now:yyyyMMdd-HHmmss}";
            var steps = TaskCombos.Normalize(picked.Select(t => new TaskCombos.ComboStep(t.Title, t.Spec.Db.Length > 0 ? t.Spec.Db : db, t.Spec.Sql)));
            if (steps.Count < 2) { status.Text = "勾选的任务里有效步骤不足 2 条（空标题/空 SQL 已剔除）。"; return; }
            try
            {
                TaskCombos.Save(new TaskCombos.TaskCombo(name, steps));
                status.Text = $"已保存组合「{name}」（{steps.Count} 步），在「加载组合执行」填同名即可按序重放。";
            }
            catch (Exception ex) { status.Text = HumanError.Say(ex, "保存组合失败"); }
        };
        comboLoadBtn.Click += (_, _) =>
        {
            var name = (comboNameBox.Text ?? "").Trim();
            if (name.Length == 0)
            {
                var names = TaskCombos.List();
                status.Text = names.Count == 0 ? "还没有保存过组合：勾选任务行后填组合名点「保存组合」。"
                    : "已保存的组合：" + string.Join("、", names) + "。填名字再点本按钮即可执行。";
                return;
            }
            var combo = TaskCombos.Load(name);
            if (combo is null) { status.Text = $"组合「{name}」读不出来（可能已损坏或已删除）。"; return; }
            var steps = TaskCombos.Normalize(combo.Steps.Select(s => new TaskCombos.ComboStep(s.Title, s.Db.Length > 0 ? s.Db : db, s.Sql)));
            if (steps.Count == 0) { status.Text = $"组合「{name}」里没有有效步骤。"; return; }
            RunCombo(combo.Name, steps, prov);
            status.Text = $"组合「{combo.Name}」已按序提交 {steps.Count} 步，全部终态后会汇总通知。";
            Refresh(list, status);
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 720 };
        panel.Children.Add(new TextBlock
        {
            Text = "长活统一队列：查询/导出进任务中心按序执行，可暂停/续跑/取消/重跑；快照可落盘留档。勾选任务行可组合执行或保存为命名组合。",
            TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(row1);
        panel.Children.Add(row2);
        panel.Children.Add(comboRow);
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

    // 发现142：任务行勾选区——Refresh 重建时同步维护，组合执行/保存组合从这里取勾选快照
    private static readonly List<(Microsoft.UI.Xaml.Controls.CheckBox Box, TaskSnapshot Snap)> _rows = new();

    private static List<TaskSnapshot> PickedSnaps() =>
        _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Snap).ToList();

    private static void Refresh(StackPanel list, TextBlock status)
    {
        list.Children.Clear();
        _rows.Clear();
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
            var line = $"[{t.State}] {t.TaskId} {t.Title}（{t.Kind}） {t.StartedAt:HH:mm:ss}{(t.Message.Length == 0 ? "" : " " + t.Message)}";
            // 发现142：每行带勾选框，供组合执行/保存组合勾选
            var cb = new Microsoft.UI.Xaml.Controls.CheckBox { VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(cb);
            row.Children.Add(new TextBlock
            {
                Text = line, FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap,
                Foreground = brush ?? Ant.TextSub, FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            list.Children.Add(row);
            _rows.Add((cb, t));
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
        // 批次三格1：任务类型扩容（query/backup/sync/import/export/dictionary/faker）
        var kindBox = new ComboBox { MinWidth = 110, ItemsSource = new List<string> { "查询 query", "备份 backup", "同步 sync", "导入 import", "导出 export", "字典 dictionary", "拟真 faker" }, SelectedIndex = 0 };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        var addBtn = new Button { Content = "新增定时任务", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var runDueBtn = new Button { Content = "执行到期任务", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var refreshBtn = new Button { Content = "刷新", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };

        addBtn.Click += async (_, _) =>
        {
            try
            {
                if (!int.TryParse(ivBox.Text?.Trim(), out int iv)) { status.Text = "间隔请填数字（分钟）。"; return; }
                var kind = (kindBox.SelectedIndex switch { 1 => "backup", 2 => "sync", 3 => "import", 4 => "export", 5 => "dictionary", 6 => "faker", _ => "query" });
                var e = SqlScheduler.Add(nameBox.Text ?? "", db, sqlBox.Text ?? "", iv, kind);
                status.Text = $"已新增定时任务 {e.Name}（{e.Id}，{e.Kind}），下次执行 {e.NextRunAt:MM-dd HH:mm}。";
                RefreshSched(list, prov, status);
            }
            catch (Exception ex) { status.Text = HumanError.Say(ex, "新增失败"); }
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
                    // 发现148：sync 类条目的 Sql 存的是同步配置名，走配置化同步执行，不再当 SQL 跑
                    if (e.Kind == "sync")
                    {
                        var profile = SyncProfiles.Load(e.Sql);
                        if (profile is null)
                        {
                            SqlScheduler.RecordResult(e.Id, "failed: 同步配置「" + e.Sql + "」读不出来（可能已删除）");
                            continue;
                        }
                        var err = await DataSyncDialog.ExecuteProfileAsync(profile);
                        SqlScheduler.RecordResult(e.Id, err.Length == 0 ? "done" : "failed: " + err);
                        if (err.Length == 0) ok++;
                        continue;
                    }
                    // 发现153：backup 类条目走备份计划执行器（策略现取凭据，产物落数据根 backups/，历史落备份账）；
                    // 执行器返回「备份完成」前缀才算成功，Redis BGSAVE 等口径与备份账一致
                    if (e.Kind == "backup")
                    {
                        var r = await BackupPlanRunner.RunEntryAsync(e, prov);
                        bool backupOk = r.StartsWith("备份完成", StringComparison.Ordinal);
                        SqlScheduler.RecordResult(e.Id, backupOk ? "done" : "failed: " + r);
                        if (backupOk) ok++;
                        continue;
                    }
                    // 批次三格1：按 Kind 分派——query 走 Execute；backup/sync/dictionary 等引擎函数类任务
                    // 以任务 SQL 为参数说明落账（引擎函数执行器随批接入，先保证旧 query 条目口径不变）
                    var rs = await prov.Execute(e.Db.Length > 0 ? e.Db : db, e.Sql);
                    bool bad = rs.Any(r => r.IsError || r.Message.StartsWith("错误", StringComparison.Ordinal) || r.Message.Contains("没成功："));   // 发现113：接结构化 IsError，不再只靠文案子串
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
        row.Children.Add(nameBox); row.Children.Add(sqlBox); row.Children.Add(ivBox); row.Children.Add(kindBox);
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row2.Children.Add(addBtn); row2.Children.Add(runDueBtn); row2.Children.Add(refreshBtn);

        var panel = new StackPanel { Spacing = 12, MinWidth = 720 };
        panel.Children.Add(new TextBlock
        {
            Text = "定时 SQL：到点自动执行（间隔分钟），结果回写最近状态；任务持久化到本机，重启不丢。",
            TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor,
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
                FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var toggleBtn = new Button
            {
                Content = e.Enabled ? "停用" : "启用",
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusBadge), FontSize = AntTokens.FontCaption, Padding = new Thickness(8, 2, 8, 2),
            };
            toggleBtn.Click += (_, _) => { SqlScheduler.Update(e.Id, null, null, null, null, !e.Enabled); RefreshSched(list, prov, status); };
            var delBtn = new Button
            {
                Content = "删除",
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusBadge), FontSize = AntTokens.FontCaption, Padding = new Thickness(8, 2, 8, 2),
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
