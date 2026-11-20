using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>一键诊断对话框：面向连接 / 库的只读体检入口，把闲置引擎接到真实使用路径上。
/// 内容：连通性与版本、对象规模、锁等待与死锁检出（LockAnalyzer）、数据体检抽样（DataHealthCheck）、
/// SQL 执行计划诊断（ExplainPlanEngine）。全部只读；发现的阻塞会话只生成 KILL 草稿，
/// 以初始 SQL 打开查询窗口，由用户确认后才执行，不静默改库。</summary>
public static class DiagnosticsDialog
{
    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        var profile = info.Profile;
        var prov = info.Provider ?? ProviderFactory.Create(profile);
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var report = new StackPanel { Spacing = 10 };

        var runBtn = new Button { Content = "开始诊断", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var sqlBox = new TextBox
        {
            PlaceholderText = "粘贴一条 SELECT / UPDATE 等 SQL，跑执行计划并标注瓶颈",
            MinWidth = 520,
        };
        var planBtn = new Button { Content = "诊断这条 SQL", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };

        runBtn.Click += async (_, _) => await RunDiagAsync(owner, info, prov, report, status);
        planBtn.Click += async (_, _) => await RunPlanAsync(prov, CurrentDb(info), sqlBox.Text, report, status);

        var topBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        topBar.Children.Add(runBtn);

        var sqlRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sqlRow.Children.Add(sqlBox);
        sqlRow.Children.Add(planBtn);

        var panel = new StackPanel { Spacing = 12, MinWidth = 640 };
        panel.Children.Add(new TextBlock
        {
            Text = "只读体检：连通性 / 版本 / 规模 / 锁等待与死锁 / 数据抽样体检；SQL 计划诊断只跑 EXPLAIN，不改数据。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ant.TextSub,
            FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(topBar);
        panel.Children.Add(new ScrollViewer
        {
            Content = report,
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        panel.Children.Add(sqlRow);
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "一键诊断",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
        _ = RunDiagAsync(owner, info, prov, report, status);
    }

    private static string CurrentDb(NodeInfo info)
    {
        if (info.Db.Length > 0) return info.Db;
        if (info.Profile.Database.Length > 0) return info.Profile.Database;
        return "";
    }

    private static string S(object? v) => v is null ? "" : v.ToString() ?? "";

    private static long ToLong(object? v) => v is null ? 0 : long.TryParse(v.ToString(), out var n) ? n : 0;

    private static Brush Sev(string severity) => severity == "danger" ? Ant.Error : Ant.Primary;

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

    private static async Task RunDiagAsync(MainWindow owner, NodeInfo info, IDbProvider prov,
        StackPanel report, TextBlock status)
    {
        var profile = info.Profile;
        report.Children.Clear();
        status.Text = "诊断中…";

        string dbName = CurrentDb(info);
        if (string.IsNullOrEmpty(dbName) && profile.Engine != DbEngine.SQLite)
        {
            try
            {
                var dbs = await prov.GetDatabases();
                if (dbs.Count > 0) dbName = dbs[0];
            }
            catch { /* 后续各步骤会单独报错 */ }
        }

        // 1. 连通性
        try
        {
            await prov.TestConnection();
            AddLine(report, "✓ 连接正常");
        }
        catch (Exception ex)
        {
            AddLine(report, "✕ 连接失败：" + ex.Message, Ant.Error);
            status.Text = "诊断中止：连接不可用。";
            return;
        }

        // 2. 版本 / 规模
        try
        {
            if (profile.Engine == DbEngine.MySQL)
            {
                var ver = await Scalar(prov, dbName, "SELECT VERSION()");
                AddLine(report, "数据库版本：" + ver);
                var top = await Rows(prov, dbName,
                    "SELECT table_name AS t, IFNULL(table_rows, 0) AS r FROM information_schema.tables " +
                    "WHERE table_schema = DATABASE() ORDER BY table_rows DESC LIMIT 8");
                AddSection(report, "规模最大的表（前 8）");
                foreach (var row in top) AddLine(report, $"  {S(row[0])}  约 {ToLong(row[1])} 行");
                await SampleHealth(prov, profile.Engine, dbName, top, report);
            }
            else if (profile.Engine == DbEngine.PostgreSQL)
            {
                var ver = await Scalar(prov, dbName, "SELECT version()");
                if (ver.Length > 80) ver = ver.Substring(0, 80) + "…";
                AddLine(report, "数据库版本：" + ver);
                var top = await Rows(prov, dbName,
                    "SELECT relname AS t, n_live_tup AS r FROM pg_stat_user_tables ORDER BY n_live_tup DESC LIMIT 8");
                AddSection(report, "规模最大的表（前 8）");
                foreach (var row in top) AddLine(report, $"  {S(row[0])}  约 {ToLong(row[1])} 行");
                await SampleHealth(prov, profile.Engine, dbName, top, report);
            }
            else if (profile.Engine == DbEngine.SQLite)
            {
                var ver = await Scalar(prov, dbName, "SELECT sqlite_version()");
                AddLine(report, "SQLite 版本：" + ver);
                AddLine(report, "SQLite 为单文件库，锁与体检深度检查不适用。");
            }
            else if (DiagnosticsCatalog.Supported(profile.Engine))
            {
                // 批次 N：Oracle / MongoDB / Redis 深度体检口径（目录引擎生成只读命令）
                var ver = await Scalar(prov, dbName, DiagnosticsCatalog.VersionQuery(profile.Engine));
                if (ver.Length > 80) ver = ver.Substring(0, 80) + "…";
                AddLine(report, $"数据库版本：{ver}");
                var topQ = DiagnosticsCatalog.TopTablesQuery(profile.Engine, dbName);
                if (topQ is not null)
                {
                    var top = await Rows(prov, dbName, topQ);
                    AddSection(report, "规模最大的表（前 8）");
                    foreach (var row in top) AddLine(report, $"  {S(row[0])}  约 {ToLong(row[1])} 行");
                }
                var health = DiagnosticsCatalog.HealthCommand(profile.Engine);
                if (health is not null)
                {
                    var hs = await prov.Execute(dbName, health);
                    AddSection(report, "健康快照");
                    if (hs.Count > 0)
                        foreach (var row in hs[0].Rows)
                            AddLine(report, $"  {S(row[0])}  {S(row.Length > 1 ? row[1] : null)}");
                }
            }
            else
            {
                AddLine(report, "该引擎暂无深度体检口径，仅做连通性检查。");
            }
        }
        catch (Exception ex)
        {
            AddLine(report, "规模 / 版本信息采集受限：" + ex.Message, Ant.Primary);
        }

        // 3. 锁等待与死锁检出（LockAnalyzer；口径来自诊断目录引擎）
        if (DiagnosticsCatalog.LockWaitQuery(profile.Engine) is not null)
        {
            await CheckLocks(owner, info, prov, dbName, report, status);
        }

        status.Text = "诊断完成（只读，未改任何数据）。";
    }

    private static async Task CheckLocks(MainWindow owner, NodeInfo info, IDbProvider prov,
        string dbName, StackPanel report, TextBlock status)
    {
        var waits = new List<LockWait>();
        try
        {
            if (info.Profile.Engine == DbEngine.MySQL)
            {
                List<QueryResult> rs;
                try
                {
                    rs = await prov.Execute(dbName,
                        "SELECT REQUESTING_ENGINE_TRANSACTION_ID AS waiter, BLOCKING_ENGINE_TRANSACTION_ID AS holder, " +
                        "IFNULL(OBJECT_NAME, '') AS resource FROM performance_schema.data_lock_waits");
                }
                catch
                {
                    rs = await prov.Execute(dbName,
                        "SELECT REQUESTING_TRX_ID, BLOCKING_TRX_ID, IFNULL(LOCK_TABLE, '') FROM information_schema.innodb_lock_waits");
                }
                if (rs.Count > 0)
                    foreach (var row in rs[0].Rows)
                        waits.Add(new LockWait(S(row[0]), S(row[1]), S(row[2])));
            }
            else if (info.Profile.Engine == DbEngine.Oracle)
            {
                // 批次 N：Oracle v$ 锁等待口径（会话级等待边）
                var rs = await prov.Execute(dbName, DiagnosticsCatalog.LockWaitQuery(DbEngine.Oracle)!);
                if (rs.Count > 0)
                    foreach (var row in rs[0].Rows)
                        waits.Add(new LockWait(S(row[0]), S(row[1]), "等 " + ToLong(row.Length > 2 ? row[2] : null) + " 秒"));
            }
            else
            {
                var rs = await prov.Execute(dbName,
                    "SELECT pid::text, array_to_string(pg_blocking_pids(pid), ',') FROM pg_stat_activity " +
                    "WHERE cardinality(pg_blocking_pids(pid)) > 0");
                if (rs.Count > 0)
                    foreach (var row in rs[0].Rows)
                    {
                        var waiter = S(row[0]);
                        var holders = S(row[1]).Split(',', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var h in holders) waits.Add(new LockWait(waiter, h, "会话 " + waiter));
                    }
            }
        }
        catch (Exception ex)
        {
            AddLine(report, "锁等待快照不可用（权限或版本原因）：" + ex.Message, Ant.Primary);
            return;
        }

        AddSection(report, "锁等待 / 死锁");
        if (waits.Count == 0)
        {
            AddLine(report, "✓ 当前无锁等待。");
            return;
        }

        AddLine(report, $"共有 {waits.Count} 条等待边：");
        foreach (var w in waits) AddLine(report, $"  {w.Waiter} 等 {w.Holder}（{w.Resource}）");

        // s16：冻结快照 + 终止会话（危险确认+留痕）+ 复制诊断包
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        var freezeBtn = new Button { Content = "冻结快照", FontSize = AntTokens.FontCaption, CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        freezeBtn.Click += (_, _) => _ = FreezeSnapshotAsync(info, prov, dbName, waits, status);
        actions.Children.Add(freezeBtn);
        foreach (var id in waits.Select(w => w.Holder).Distinct().Take(5))
        {
            var killBtn = new Button { Content = $"终止会话 {id}", FontSize = AntTokens.FontCaption, CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
            var sid = id;
            killBtn.Click += (_, _) => _ = KillSessionAsync(owner, info, prov, dbName, sid, status);
            actions.Children.Add(killBtn);
        }
        if (ReplicationCatalog.Supported(info.Profile.Engine))
        {
            var packBtn = new Button { Content = "复制诊断包", FontSize = AntTokens.FontCaption, CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
            packBtn.Click += (_, _) => _ = ReplicationPackAsync(info, prov, dbName, waits, status);
            actions.Children.Add(packBtn);
        }
        report.Children.Add(actions);

        var cycles = LockAnalyzer.FindDeadlocks(waits);
        if (cycles.Count == 0)
        {
            AddLine(report, "未检出死锁环（纯链式等待会自行解开）。");
            return;
        }

        AddLine(report, $"⚠ 检出 {cycles.Count} 个死锁环：", Ant.Error);
        var drafts = new List<string>();
        foreach (var c in cycles)
        {
            AddLine(report, "  环：" + string.Join(" → ", c.SessionIds), Ant.Error);
            foreach (var id in c.SessionIds)
            {
                var k = LockAnalyzer.KillSql(id, info.Profile.Engine);
                if (!string.IsNullOrEmpty(k)) drafts.Add(k);
            }
        }
        AddLine(report, "建议终止回滚代价最小的会话解开环。以下草稿已放进查询窗口，确认后再执行：", Ant.Primary);
        foreach (var d in drafts) AddLine(report, "  " + d, Ant.Primary);
        if (drafts.Count > 0) owner.OpenQueryTab(info, string.Join(";\n", drafts));
    }

    /// <summary>s16 冻结快照：把当前锁等待现场原样落盘（含时间戳），供事后回溯比对。</summary>
    private static async Task FreezeSnapshotAsync(NodeInfo info, IDbProvider prov, string dbName,
        List<LockWait> waits, TextBlock status)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# TaoDBM 锁等待冻结快照 {DateTime.Now:yyyy-MM-dd HH:mm:ss} engine={info.Profile.Engine} db={dbName}");
            foreach (var w in waits) sb.AppendLine($"{w.Waiter}\t等\t{w.Holder}\t{w.Resource}");
            var file = TaoPaths.FilePath($"lock-snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(file, sb.ToString());
            AppLog.Write($"[锁冻结快照] waits={waits.Count} file={Path.GetFileName(file)}");
            status.Text = "冻结快照已落盘：" + file;
        }
        catch (Exception ex) { status.Text = "冻结快照失败：" + ex.Message; }
    }

    /// <summary>s16 终止会话：高风险操作，显式输入会话 ID 解除（分级确认），执行后留痕。</summary>
    private static async Task KillSessionAsync(MainWindow owner, NodeInfo info, IDbProvider prov, string dbName,
        string sessionId, TextBlock status)
    {
        var confirm = new ContentDialog
        {
            Title = "终止会话（危险操作）",
            Content = new StackPanel { Spacing = 8, MinWidth = 380 },
            PrimaryButtonText = "终止",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = owner.Content.XamlRoot,
        };
        var box = new TextBox { PlaceholderText = $"输入会话 ID {sessionId} 以确认" };
        ((StackPanel)confirm.Content).Children.Add(new TextBlock
        {
            Text = $"将执行 {LockAnalyzer.KillSql(sessionId, info.Profile.Engine)}，回滚可能需要时间，且该会话未提交事务会丢失。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.Error,
        });
        ((StackPanel)confirm.Content).Children.Add(box);
        if (await confirm.ShowThemedAsync() != ContentDialogResult.Primary) return;
        if (box.Text.Trim() != sessionId) { status.Text = "会话 ID 不符，未执行。"; return; }
        try
        {
            var kill = LockAnalyzer.KillSql(sessionId, info.Profile.Engine);
            if (string.IsNullOrEmpty(kill)) { status.Text = "该引擎没有可用的终止会话语句。"; return; }
            await prov.Execute(dbName, kill);
            AppLog.Write($"[终止会话] engine={info.Profile.Engine} session={sessionId}");
            status.Text = $"会话 {sessionId} 已终止（回滚由数据库自行进行）。";
        }
        catch (Exception ex) { status.Text = "终止会话失败：" + ex.Message; }
    }

    /// <summary>s16 复制诊断包：复制状态 + 锁等待现场打包落盘，供贴给 DBA 排查。</summary>
    private static async Task ReplicationPackAsync(NodeInfo info, IDbProvider prov, string dbName,
        List<LockWait> waits, TextBlock status)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# TaoDBM 复制诊断包 {DateTime.Now:yyyy-MM-dd HH:mm:ss} engine={info.Profile.Engine} db={dbName}");
            foreach (var sql in new[] { ReplicationCatalog.ReplicasSql(info.Profile.Engine), ReplicationCatalog.ReplicaSelfStatusSql(info.Profile.Engine) })
            {
                if (sql is null) continue;
                sb.AppendLine($"## {sql}");
                try
                {
                    var rs = await prov.Execute(dbName, sql);
                    var r = rs.LastOrDefault();
                    if (r is null) { sb.AppendLine("（无结果）"); continue; }
                    sb.AppendLine(string.Join("\t", r.Columns));
                    foreach (var row in r.Rows) sb.AppendLine(string.Join("\t", row.Select(S)));
                }
                catch (Exception ex) { sb.AppendLine("（查询失败：" + ex.Message + "）"); }
            }
            sb.AppendLine("## 锁等待现场");
            foreach (var w in waits) sb.AppendLine($"{w.Waiter}\t等\t{w.Holder}\t{w.Resource}");
            var file = TaoPaths.FilePath($"repl-pack-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(file, sb.ToString());
            AppLog.Write($"[复制诊断包] file={Path.GetFileName(file)}");
            status.Text = "复制诊断包已落盘：" + file;
        }
        catch (Exception ex) { status.Text = "复制诊断包失败：" + ex.Message; }
    }

    private static async Task SampleHealth(IDbProvider prov, DbEngine engine, string dbName,
        List<object?[]> topRows, StackPanel report)
    {
        if (topRows.Count == 0) return;
        var profiles = new List<ColumnProfile>();
        foreach (var row in topRows)
        {
            var table = S(row[0]);
            if (table.Length == 0) continue;
            long rows = ToLong(row[1]);
            if (rows <= 0 || rows > 5_000_000) continue;   // 大表不抽样计数，避免全表扫
            List<ColumnSpec> cols;
            try { cols = await prov.GetColumns(dbName, table); }
            catch { continue; }
            foreach (var col in cols.Take(2))
            {
                long nulls = ToLong(await Scalar(prov, dbName,
                    $"SELECT COUNT(*) FROM {prov.Quote(table)} WHERE {prov.Quote(col.Name)} IS NULL"));
                profiles.Add(new ColumnProfile(table, col.Name, rows, nulls, 0));
            }
        }
        if (profiles.Count == 0)
        {
            AddSection(report, "数据体检（抽样）");
            AddLine(report, "抽样范围内没有可体检的表 / 列。");
            return;
        }
        var hr = DataHealthCheck.Inspect(profiles);
        AddSection(report, $"数据体检（抽样）：健康分 {hr.Score}/100");
        if (hr.Findings.Count == 0)
        {
            AddLine(report, "✓ 抽样列未发现空值率 / 重复率异常。");
            return;
        }
        foreach (var f in hr.Findings)
        {
            AddLine(report, $"  [{f.Code}] {f.Table}.{f.Column}：{f.Message}", Sev(f.Severity));
            if (!string.IsNullOrEmpty(f.Suggestion)) AddLine(report, "      建议：" + f.Suggestion, Ant.TextSub);
        }
    }

    private static async Task RunPlanAsync(IDbProvider prov, string dbName, string sql,
        StackPanel report, TextBlock status)
    {
        sql = (sql ?? "").Trim();
        if (sql.Length == 0) { status.Text = "请先粘贴要诊断的 SQL。"; return; }
        string? explainSql = prov.Profile.Engine switch
        {
            DbEngine.MySQL => "EXPLAIN FORMAT=TREE " + sql,
            DbEngine.PostgreSQL => "EXPLAIN " + sql,
            DbEngine.SQLite => "EXPLAIN QUERY PLAN " + sql,
            _ => null,
        };
        if (explainSql is null)
        {
            status.Text = "该引擎不支持执行计划诊断。";
            return;
        }

        AddSection(report, "执行计划诊断");
        try
        {
            var rs = await prov.Execute(dbName, explainSql);
            if (rs.Count == 0 || rs[0].Rows.Count == 0)
            {
                AddLine(report, "（执行计划为空）", Ant.Primary);
                return;
            }

            int ci = 0;
            var cols = rs[0].Columns;
            for (int i = 0; i < cols.Count; i++)
            {
                var n = (cols[i] ?? "").ToLowerInvariant();
                if (n.Contains("detail") || n.Contains("plan") || n.Contains("explain")) { ci = i; break; }
            }

            var lines = new List<string>();
            foreach (var row in rs[0].Rows)
                if (row is not null && row.Length > ci)
                    lines.Add(S(row[ci]));

            var plan = ExplainPlanEngine.Parse(string.Join("\n", lines));
            var findings = ExplainPlanEngine.Annotate(plan);
            foreach (var node in plan.Flat)
                AddLine(report, node.Text, null, Math.Min(node.Depth, 8));

            if (findings.Count == 0)
            {
                AddLine(report, "✓ 未发现全表扫描 / 文件排序 / 临时表等瓶颈。", Ant.Primary);
            }
            else
            {
                foreach (var f in findings)
                {
                    AddLine(report, $"  [{f.Code}] {f.Message}", Sev(f.Severity));
                    AddLine(report, "      建议：" + f.Suggestion, Ant.TextSub);
                }
            }
            status.Text = "执行计划诊断完成（只跑 EXPLAIN，未执行原 SQL）。";
        }
        catch (Exception ex)
        {
            AddLine(report, "执行计划诊断失败：" + ex.Message, Ant.Error);
            status.Text = "执行计划诊断失败，详见报告区。";
        }
    }

    private static async Task<string> Scalar(IDbProvider prov, string db, string sql)
    {
        var rs = await prov.Execute(db, sql);
        if (rs.Count == 0 || rs[0].Rows.Count == 0) return "";
        var row = rs[0].Rows[0];
        return row is not null && row.Length > 0 ? S(row[0]) : "";
    }

    private static async Task<List<object?[]>> Rows(IDbProvider prov, string db, string sql)
    {
        var rs = await prov.Execute(db, sql);
        if (rs.Count == 0) return new List<object?[]>();
        return rs[0].Rows;
    }
}
