﻿using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Text;
using TaoDBM.Core;
using TaoDBM.Infrastructure;

namespace TaoDBM.UI;

/// <summary>
/// 备份可用性演练（整改三批三-15：弹窗改页面——重功能从模态弹窗升级为内嵌主窗口页签，参照 U2 D-13 表设计器口径）。
/// 选备份文件 → 恢复到临时库（SQLite 拷贝+文件头校验；服务端走既有连接层建临时库恢复）
/// → 逐表计数与备份时点对账 → 出验证报告。全程只碰临时库/临时副本，不动原库。
/// </summary>
public class BackupDrillTab : Grid
{
    private readonly NodeInfo _info;
    private readonly string _db;
    private readonly TextBox _fileBox = new() { PlaceholderText = "备份文件路径（.db/.sqlite 副本 / mysqldump / pg_dump / .bak）", MinWidth = 520 };
    private readonly TextBox _countsBox = new()
    {
        PlaceholderText = "对账清单，每行 表名=行数，如 orders=5000（# 开头为注释）",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 110,
        FontFamily = new FontFamily("Consolas"),
        FontSize = AntTokens.FontMinor,
    };
    private readonly TextBox _output = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 320,
        FontFamily = new FontFamily("Consolas"),
        FontSize = AntTokens.FontMinor,
    };
    private readonly TextBlock _status = new() { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
    private readonly Button _runBtn = new() { Content = "开始演练", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };

    public BackupDrillTab(MainWindow owner, NodeInfo info)
    {
        _info = info;
        _db = info.Db.Length > 0 ? info.Db : info.Profile.Database;
        var engine = info.Profile.Engine;

        Margin = new Thickness(16);
        RowSpacing = 10;

        var supported = BackupTools.Supported(engine);
        var runBtn = _runBtn;
        runBtn.IsEnabled = supported;
        runBtn.Click += async (_, _) => await RunAsync();

        // 三-14：演示式接真实数据——对账清单从当前连接真实表行数生成，不再只靠手填。
        var genBtn = new Button { Content = "从当前连接读对账清单", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(genBtn, "从当前连接读对账清单");
        genBtn.Click += async (_, _) => await RunRealCountsAsync();
        var genRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        genRow.Children.Add(genBtn);

        var runRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        runRow.Children.Add(runBtn);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(runBtn, "开始演练");

        if (!supported)
        {
            _status.Text = BackupTools.UnsupportedReason(engine);
        }

        Children.Add(BuildRow(new TextBlock
        {
            Text = $"备份可用性演练 — {_db}@{engine}：恢复到临时库并逐表对账，验证备份真的可用；过程不触碰原库。",
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.TextSub,
            TextWrapping = TextWrapping.Wrap,
        }));
        Children.Add(BuildRow(_fileBox));
        Children.Add(BuildRow(_countsBox));
        Children.Add(BuildRow(genRow));
        Children.Add(BuildRow(runRow));
        Children.Add(BuildRow(_output));
        Children.Add(BuildRow(_status));
        _output.Text = supported
            ? "填备份文件路径与对账清单后点「开始演练」。演练只在临时副本/临时库上操作。"
            : "当前引擎不支持备份演练。";
    }

    private static FrameworkElement BuildRow(FrameworkElement child)
    {
        child.HorizontalAlignment = HorizontalAlignment.Stretch;
        return child;
    }

    private async Task RunAsync()
    {
        var engine = _info.Profile.Engine;
        var (expected, parseIssues) = BackupDrill.ParseExpectedCounts(_countsBox.Text);
        _runBtn.IsEnabled = false;
        _status.Text = "演练进行中…";
        try
        {
            var report = engine == DbEngine.SQLite
                ? await RunSqliteAsync(_info, _fileBox.Text.Trim(), expected, parseIssues)
                : await RunServerAsync(_info, _db, _fileBox.Text.Trim(), expected, parseIssues);
            _output.Text = report.Render($"备份演练（{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}，{_fileBox.Text.Trim()}）");
            _status.Text = report.Pass ? "演练通过：备份可用。" : "演练未通过，见报告明细。";
            AppLog.Write($"[备份演练页签] db={_db} pass={report.Pass}");
        }
        catch (Exception ex)
        {
            _status.Text = "演练失败：" + ex.Message;
            _output.Text += $"\n[中止] {ex.Message}";
            AppLog.Write($"[备份演练页签] error={ex.Message}");
        }
        finally
        {
            _runBtn.IsEnabled = true;
        }
    }

    /// <summary>三-14：真实对账清单——读当前连接的表清单与行数，按「表名=行数」回填对账清单框。</summary>
    private async Task RunRealCountsAsync()
    {
        try
        {
            var provider = _info.Provider ?? ProviderFactory.Create(_info.Profile);
            var tables = await provider.GetChildren(_db);
            if (tables.Count == 0) { _status.Text = $"未读到 {_db} 的表清单。"; return; }

            var lines = new List<string>();
            int taken = 0;
            foreach (var t in tables)
            {
                if (taken >= 12) break;   // 行数采样上限：前 12 张表，避免大库全量扫描
                try
                {
                    var rs = await provider.Execute(_db, $"SELECT COUNT(*) FROM {provider.Quote(t)}");
                    var stat = rs.FirstOrDefault(x => x.Rows.Count > 0);
                    if (stat is null) continue;
                    lines.Add($"{t}={Convert.ToInt64(stat.Rows[0][0])}");
                    taken++;
                }
                catch { /* 单表计数失败不阻断其余表 */ }
            }
            if (lines.Count == 0) { _status.Text = "行数采样未得到可用表目（均不可计数）。"; return; }
            _countsBox.Text = string.Join("\n", lines);
            _status.Text = $"对账清单已回填（{lines.Count} 张表），填备份文件路径后点「开始演练」。";
            AppLog.Write($"[备份对账清单] db={_db} tables={lines.Count}");
        }
        catch (Exception ex)
        {
            _status.Text = "对账清单生成失败：" + ex.Message;
            AppLog.Write($"[备份对账清单] error={ex.Message}");
        }
    }

    // ---------- SQLite：拷贝 → 文件头校验 → 临时副本上逐表计数 ----------

    private static async Task<BackupDrillReport> RunSqliteAsync(NodeInfo info, string backupFile,
        IReadOnlyList<(string Table, long Count)> expected, List<string> parseIssues)
    {
        if (backupFile.Length == 0) throw new InvalidOperationException("请填备份文件路径。");
        if (!File.Exists(backupFile)) throw new FileNotFoundException("备份文件不存在：" + backupFile);

        var tempFile = Path.Combine(Path.GetTempPath(),
            "taodbm-drill-" + DateTimeOffset.Now.ToString("yyyyMMddHHmmss") + Path.GetExtension(backupFile));
        try
        {
            BackupDrill.SqliteDrillCopy(backupFile, tempFile);
            var actual = new Dictionary<string, long>();
            var issues = new List<string>(parseIssues);
            var prov = new SqliteProvider(new ConnectionProfile { Engine = DbEngine.SQLite, Database = tempFile });
            foreach (var (table, _) in expected)
            {
                try
                {
                    var rs = await prov.Execute("main", BackupDrill.CountSql(DbEngine.SQLite, table));
                    var val = rs.Count > 0 && rs[0].Rows.Count > 0 ? Convert.ToInt64(rs[0].Rows[0][0]) : -1;
                    actual[table] = val;
                }
                catch (Exception ex)
                {
                    issues.Add($"计数失败 {table}：{ex.Message}");
                }
            }
            return BackupDrill.VerifyCounts(expected, actual, issues);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* 临时副本清理失败不阻断报告 */ }
        }
    }

    // ---------- 服务端：建临时库 → 还原 → 逐表计数 → 删临时库 ----------

    private static async Task<BackupDrillReport> RunServerAsync(NodeInfo info, string db, string backupFile,
        IReadOnlyList<(string Table, long Count)> expected, List<string> parseIssues)
    {
        if (backupFile.Length == 0) throw new InvalidOperationException("请填备份文件路径。");
        if (!File.Exists(backupFile)) throw new FileNotFoundException("备份文件不存在：" + backupFile);
        if (info.Provider is not RelationalProvider prov) throw new InvalidOperationException("连接不可用，请先连接数据库。");

        var engine = info.Profile.Engine;
        var p = info.Profile;
        var tempDb = BackupDrill.TempDbName(engine, db, DateTimeOffset.Now);
        var issues = new List<string>(parseIssues);
        var actual = new Dictionary<string, long>();
        bool created = false;
        try
        {
            if (engine == DbEngine.SqlServer)
            {
                await prov.ExecuteSingle(db, BackupDrill.SqlServerRestoreSql(tempDb, backupFile));
                created = true;
            }
            else
            {
                var createSql = BackupDrill.CreateTempDbSql(engine, tempDb);
                if (createSql is not null)
                {
                    await prov.ExecuteSingle(db, createSql);
                    created = true;
                }
                if (engine is DbEngine.MySQL or DbEngine.PostgreSQL)
                {
                    var spec = BackupDrill.RestoreSpec(engine, p.Host, p.Port, p.User, p.Password, tempDb, backupFile)
                        ?? throw new InvalidOperationException("该引擎暂无命令行还原规格。");
                    var (code, outText) = await RunAsync(spec);
                    if (code != 0) throw new InvalidOperationException($"还原失败（exit {code}）：{Truncate(outText)}");
                }
                else
                {
                    issues.Add("该引擎还原规格未接命令行，演练仅建库未恢复。");
                }
            }

            foreach (var (table, _) in expected)
            {
                try
                {
                    var qr = await prov.ExecuteSingle(tempDb, BackupDrill.CountSql(engine, table));
                    if (qr.Message.StartsWith("错误", StringComparison.Ordinal))
                        throw new InvalidOperationException(qr.Message);
                    actual[table] = qr.Rows.Count > 0 ? Convert.ToInt64(qr.Rows[0][0]) : -1;
                }
                catch (Exception ex)
                {
                    issues.Add($"计数失败 {table}：{ex.Message}");
                }
            }
        }
        finally
        {
            if (created)
            {
                try
                {
                    var dropSql = BackupDrill.DropTempDbSql(engine, tempDb);
                    if (dropSql is not null) await prov.ExecuteSingle(db, dropSql);
                }
                catch { /* 收尾清理失败不阻断报告 */ }
            }
        }
        return BackupDrill.VerifyCounts(expected, actual, issues);
    }

    private static string Truncate(string s)
    {
        s = s.Length > 400 ? s[..400] + "…" : s;
        return s;
    }

    /// <summary>命令行执行（与 BackupDialog 同口径：密码经环境变量，不进命令行）。</summary>
    private static async Task<(int Code, string Output)> RunAsync(ToolSpec spec)
    {
        var psi = new ProcessStartInfo(spec.FileName, spec.Arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var kv in spec.Env) psi.EnvironmentVariables[kv.Key] = kv.Value;
        using var p = new Process { StartInfo = psi };
        p.Start();
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (spec.InputFile is not null)
        {
            var input = await File.ReadAllTextAsync(spec.InputFile);
            await p.StandardInput.WriteAsync(input);
            p.StandardInput.Close();
        }
        await p.WaitForExitAsync();
        var text = (await outTask + await errTask).Trim();
        return (p.ExitCode, text);
    }
}
