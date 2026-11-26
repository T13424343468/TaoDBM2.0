using TaoDBM.Core;

namespace TaoDBM.LogicTest;

/// <summary>界面批 B 测试锚（2026-11-27，发现138/143/144/147/149/152/153）：纯逻辑可测项逐条落断言；
/// 界面层条目（连接对话框页签/设置页签/备份树右键等）本地沙箱编不了 App XAML，归云测首验，此处锚定其下沉 Core 的口径。
/// 独立文件接线（ComboMatrix 家法），经 BatchCheckSweep.RunAll 调入，不碰 Program.cs。</summary>
public static class UiBatchBCheck
{
    public delegate void CheckD(string name, bool ok, string detail = "");

    public static void RunAll(Action<string, bool, string> check)
    {
        CheckD d = (name, ok, detail) => check(name, ok, detail ?? "");
        Console.WriteLine("\n== 界面批 B 纯逻辑用例（2026-11-27）==");
        RunBackupPlanPolicy(d);      // 发现153
        RunBackupPlanFile(d);        // 发现153/144
        RunBackupPlanRun(d);         // 发现153（SQLite 全链路）
        RunFontPrefs(d);             // 发现152
        RunShortcutPrefs(d);         // 发现152
        RunSyncItemLabel(d);         // 发现147
        RunDiagnosticsAnchor(d);     // 发现149
        RunBackupTreeLabel(d);       // 发现144
        RunAiTableEntry(d);          // 发现138
        RunConnRemark(d);            // 发现143
    }

    // 发现143：连接备注——JSON 往返一致，旧配置缺字段回空串不闪退
    static void RunConnRemark(CheckD d)
    {
        var opt = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        string json = System.Text.Json.JsonSerializer.Serialize(new ConnectionProfile { DisplayName = "测试", Remark = "压测环境专用" }, opt);
        var back = System.Text.Json.JsonSerializer.Deserialize<ConnectionProfile>(json);
        d("143 备注序列化往返一致", back is not null && back.Remark == "压测环境专用", json);
        var old = System.Text.Json.JsonSerializer.Deserialize<ConnectionProfile>("{\"Id\":\"x\",\"DisplayName\":\"老配置\"}");
        d("143 旧配置缺备注回空串", old is not null && old.Remark.Length == 0, "");
    }

    // 发现153：策略 JSON 解析——合法往返、损坏人话、越界人话
    static void RunBackupPlanPolicy(CheckD d)
    {
        string json = BackupPlanRunner.PolicyJson(168, 24, 3);
        var p = BackupPlanRunner.ParsePolicy(json);
        d("153 策略 JSON 往返一致", p.FullHours == 168 && p.IncrHours == 24 && p.RetainFulls == 3, json);
        d("153 策略 JSON 不含凭据字段", !json.Contains("Password", StringComparison.OrdinalIgnoreCase) && !json.Contains("User", StringComparison.OrdinalIgnoreCase), json);
        bool bad1 = false, bad2 = false, bad3 = false;
        try { BackupPlanRunner.ParsePolicy("{ not json"); } catch (ArgumentException ex) { bad1 = ex.Message.Contains("读不出来"); }
        try { BackupPlanRunner.ParsePolicy("{\"FullHours\":0,\"IncrHours\":24,\"RetainFulls\":3}"); } catch (ArgumentException ex) { bad2 = ex.Message.Contains("至少 1 小时"); }
        try { BackupPlanRunner.ParsePolicy("{\"FullHours\":24,\"IncrHours\":-1,\"RetainFulls\":3}"); } catch (ArgumentException ex) { bad3 = ex.Message.Contains("不能是负数"); }
        d("153 损坏 / 越界策略人话报错", bad1 && bad2 && bad3, "");
        d("153 空策略人话报错", AssertArg(() => BackupPlanRunner.ParsePolicy("  "), "为空"), "");
    }

    private static bool AssertArg(Action a, string contains)
    {
        try { a(); return false; }
        catch (ArgumentException ex) { return ex.Message.Contains(contains); }
        catch { return false; }
    }

    // 发现153/144：产物路径命名——数据根 backups 子目录、档位与扩展名按引擎
    static void RunBackupPlanFile(CheckD d)
    {
        var my = new ConnectionProfile { Engine = DbEngine.MySQL };
        string f1 = BackupPlanRunner.BackupFileFor(my, "orders", "full");
        d("153 MySQL 产物落 backups 子目录 .sql", f1.Contains("backups") && f1.Contains("orders-full-") && f1.EndsWith(".sql"), f1);
        var ss = new ConnectionProfile { Engine = DbEngine.SqlServer };
        string f2 = BackupPlanRunner.BackupFileFor(ss, "orders", "incr");
        d("153 SqlServer 产物 .bak 增量档位", f2.EndsWith(".bak") && f2.Contains("orders-incr-"), f2);
        var sq = new ConnectionProfile { Engine = DbEngine.SQLite };
        string f3 = BackupPlanRunner.BackupFileFor(sq, "", "full");
        d("153 空库名回退 all 档位", System.IO.Path.GetFileName(f3).StartsWith("all-full-"), f3);
    }

    // 发现153：SQLite 全链路（策略→执行→落账）；Oracle 口径人话
    static void RunBackupPlanRun(CheckD d)
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "taodbm-uib-b-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        string src = System.IO.Path.Combine(dir, "src.db");
        System.IO.File.WriteAllText(src, "sqlite-main");
        var profile = new ConnectionProfile { Engine = DbEngine.SQLite, Database = src };
        var entry = new ScheduledSqlEntry { Id = "t1", Name = "备份计划-全量-src", Db = "src", Sql = BackupPlanRunner.PolicyJson(24, 0, 3), Kind = "backup" };
        string result = BackupPlanRunner.RunEntryAsync(entry, new SqliteProbe(profile)).GetAwaiter().GetResult();
        string backupDir = System.IO.Path.Combine(TaoPaths.Root, "backups");
        var produced = System.IO.Directory.Exists(backupDir)
            ? System.IO.Directory.GetFiles(backupDir, "src-full-*").OrderBy(static f => System.IO.File.GetLastWriteTime(f)).LastOrDefault()
            : null;
        d("153 SQLite 计划备份完成且产物落盘", result.StartsWith("备份完成", StringComparison.Ordinal) && produced is not null && System.IO.File.ReadAllText(produced!) == "sqlite-main", result);
        var hist = BackupHistory.All().FirstOrDefault(h => h.Db == "src" && h.Kind == "备份");
        d("153 备份落 tao_backup_history 账", hist is not null && hist.Ok, hist?.File ?? "");
        // 增量条目：SQLite 无增量口径，按全量执行并如实标注
        var entry2 = new ScheduledSqlEntry { Id = "t2", Name = "备份计划-增量-src", Db = "src", Sql = BackupPlanRunner.PolicyJson(24, 6, 3), Kind = "backup" };
        string result2 = BackupPlanRunner.RunEntryAsync(entry2, new SqliteProbe(profile)).GetAwaiter().GetResult();
        d("153 无增量口径引擎如实标注按全量执行", result2.StartsWith("备份完成", StringComparison.Ordinal) && result2.Contains("按全量执行"), result2);
        // Oracle：人话不支持，不静默
        bool oracle = false;
        try
        {
            _ = BackupPlanRunner.RunEntryAsync(
                new ScheduledSqlEntry { Id = "t3", Name = "备份计划-全量-orcl", Db = "orcl", Sql = BackupPlanRunner.PolicyJson(24, 0, 3), Kind = "backup" },
                new OracleProbe()).GetAwaiter().GetResult();
        }
        catch (NotSupportedException ex) { oracle = ex.Message.Contains("Oracle") && ex.Message.Contains("目录对象"); }
        d("153 Oracle 自动定时人话不支持", oracle, "");
        try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* 临时目录清理失败不阻断 */ }
    }

    private sealed class SqliteProbe : SqliteProvider
    {
        public SqliteProbe(ConnectionProfile p) : base(p) { }
    }

    private sealed class OracleProbe : OracleProvider
    {
        public OracleProbe() : base(new ConnectionProfile { Engine = DbEngine.Oracle, Host = "h", User = "u", Password = "p" }) { }
    }

    // 发现152：外观偏好——钳制、损坏回默认、往返
    static void RunFontPrefs(CheckD d)
    {
        d("152 字号钳制 12-24", UiFontPrefs.ClampFontSize(5) == 12 && UiFontPrefs.ClampFontSize(99) == 24 && UiFontPrefs.ClampFontSize(16) == 16, "");
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uifonts-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        UiFontPrefs.UseFile(path);
        try
        {
            new UiFontPrefs { EditorFontFamily = "Cascadia Code", EditorFontSize = 99, GridFontSize = 3 }.Save();
            var loaded = UiFontPrefs.Load();
            d("152 外观偏好保存钳制后读回", loaded.EditorFontFamily == "Cascadia Code" && loaded.EditorFontSize == 24 && loaded.GridFontSize == 12,
                $"{loaded.EditorFontFamily}/{loaded.EditorFontSize}/{loaded.GridFontSize}");
        }
        finally { UiFontPrefs.UseFile(""); System.IO.File.Delete(path); }
        string bad = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uifonts-bad-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        System.IO.File.WriteAllText(bad, "{ corrupt");
        UiFontPrefs.UseFile(bad);
        try
        {
            var def = UiFontPrefs.Load();
            d("152 损坏外观回默认不闪退", def.EditorFontFamily == "Consolas" && def.EditorFontSize == 13, "");
        }
        finally { UiFontPrefs.UseFile(""); System.IO.File.Delete(bad); }
    }

    // 发现152：快捷键——解析归一、冲突人话不落盘、复位、损坏回默认
    static void RunShortcutPrefs(CheckD d)
    {
        var (ok1, _, norm1) = ShortcutPrefs.Parse("ctrl+shift+n");
        d("152 绑定解析归一修饰键顺序", ok1 && norm1 == "Ctrl+Shift+N", norm1);
        var (ok2, err2, _) = ShortcutPrefs.Parse("hyper+n");
        d("152 未知修饰键人话", !ok2 && err2.Contains("Ctrl"), err2);
        var (ok3, err3, _) = ShortcutPrefs.Parse("ctrl+");
        d("152 空主键人话", !ok3 && err3.Length > 0, err3);

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sc-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        ShortcutPrefs.UseFile(path);
        try
        {
            string conflict = ShortcutPrefs.Rebind("newquery", "Ctrl+W");
            d("152 改绑冲突人话不落盘", conflict.Contains("关闭当前页签") && ShortcutPrefs.Effective("newquery") == "Ctrl+N", conflict);
            string okBind = ShortcutPrefs.Rebind("newquery", "ctrl+alt+q");
            d("152 改绑成功落盘生效", okBind.Length == 0 && ShortcutPrefs.Effective("newquery") == "Ctrl+Alt+Q", okBind);
            ShortcutPrefs.Reset("newquery");
            d("152 复位回出厂", ShortcutPrefs.Effective("newquery") == "Ctrl+N", "");
            d("152 编辑器内置口径标记", ShortcutPrefs.IsEditorBuiltin("run") && !ShortcutPrefs.IsEditorBuiltin("manual"), "");
        }
        finally { ShortcutPrefs.UseFile(""); System.IO.File.Delete(path); }

        string bad = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sc-bad-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        System.IO.File.WriteAllText(bad, "[broken");
        ShortcutPrefs.UseFile(bad);
        try { d("152 损坏快捷键回默认不闪退", ShortcutPrefs.Effective("manual") == "F1", ""); }
        finally { ShortcutPrefs.UseFile(""); System.IO.File.Delete(bad); }
    }

    // 发现147：逐条勾选行文案——类型徽标＋表名＋摘要
    static void RunSyncItemLabel(CheckD d)
    {
        d("147 CREATE 语句行文案带表名", UiBatchB.SyncItemLabel("CREATE TABLE [orders] (id INT)").StartsWith("【orders】CREATE · ", StringComparison.Ordinal), "");
        d("147 ALTER 语句行文案带表名", UiBatchB.SyncItemLabel("ALTER TABLE `orders` ADD memo VARCHAR(50)").StartsWith("【orders】ALTER · ", StringComparison.Ordinal), "");
        d("147 长语句摘要截断", UiBatchB.SyncItemLabel("CREATE TABLE t (" + new string('x', 200) + ")").Length <= 110, "");
        d("147 表名抠取剥引号", UiBatchB.ExtractTable("CREATE TABLE t2 (id INT)") == "t2", "");
    }

    // 发现149：自动刷新周期与不支持引擎人话
    static void RunDiagnosticsAnchor(CheckD d)
    {
        d("149 进程页自动刷新 10 秒口径", UiBatchB.AutoRefreshSeconds == 10, "");
        d("149 SQLite 变量页人话", UiBatchB.KeyValueUnsupported(DbEngine.SQLite).Contains("单文件库"), "");
        d("149 MongoDB 变量页人话", UiBatchB.KeyValueUnsupported(DbEngine.MongoDB).Contains("mongosh"), "");
        d("149 Redis 变量页人话", UiBatchB.KeyValueUnsupported(DbEngine.Redis).Contains("redis-cli"), "");
        d("149 支持引擎不给人话", UiBatchB.KeyValueUnsupported(DbEngine.MySQL).Length == 0, "");
    }

    // 发现144：备份树条目文案——成败 / 大小 / 文件缺失置灰标注
    static void RunBackupTreeLabel(CheckD d)
    {
        var e = new BackupHistory.Entry("2026-11-27 10:00:00", "MySQL", "orders", "备份", "orders.sql", true, 1536, "备份完成");
        d("144 条目文案时间档位大小", UiBatchB.BackupEntryLabel(e, fileExists: true).Contains("2026-11-27 10:00:00") && UiBatchB.BackupEntryLabel(e, true).Contains("✓"), "");
        d("144 文件缺失置灰标注", UiBatchB.BackupEntryLabel(e, fileExists: false).Contains("文件已不在磁盘"), "");
        var fail = new BackupHistory.Entry("2026-11-27 10:00:00", "MySQL", "orders", "还原", "orders.sql", false, 0, "失败");
        d("144 失败条目 ✗ 标注", UiBatchB.BackupEntryLabel(fail, true).Contains("✗"), "");
        d("144 空态文案口径", UiBatchB.BackupTreeEmptyText.Contains("备份 / 还原"), "");
    }

    // 发现138：AI 建表入口与引导文案
    static void RunAiTableEntry(CheckD d)
    {
        var (_, acts) = ObjectActionBarModel.Build("table");
        d("138 AI 建表入口可点击", acts.First(a => a.Key == "ai").Enabled, "");
        d("138 引导文案说人话", ObjectActionBarModel.AiTableGuide.Contains("还没接入") && ObjectActionBarModel.AiTableGuide.Contains("新建表"), "");
        bool saved = ObjectActionBarModel.AiTableAvailable;
        try
        {
            ObjectActionBarModel.AiTableAvailable = true;
            d("138 开关可置 true 走 AI 流程入口", ObjectActionBarModel.AiTableAvailable, "");
        }
        finally { ObjectActionBarModel.AiTableAvailable = saved; }
    }
}
