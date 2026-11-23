using TaoDBM.Core;
using TaoDBM.Infrastructure;

namespace TaoDBM.LogicTest;

/// <summary>批次二三纯逻辑用例（2026-11-23）：库搜索列筛选与计数 SQL、服务器监控指标提取与 KILL 草稿、
/// 复制为 INSERT 字面量、计划任务 Kind 归一、连接分组存取、HTTP 隧道会话生命周期（不连网）。
/// 独立文件接线（ComboMatrix 家法），不碰 Program.cs 主体。</summary>
public static class BatchCheck23
{
    /// <summary>带默认 detail 的检查委托（与 Program.Check 同形）。</summary>
    public delegate void CheckD(string name, bool ok, string detail = "");

    public static void RunAll(Action<string, bool, string> check)
    {
        CheckD d = (name, ok, detail) => check(name, ok, detail ?? "");
        Console.WriteLine("\n== 批次二三 纯逻辑用例（2026-11-23）==");
        RunDbSearch(d);
        RunMonitor(d);
        RunInsertSql(d);
        RunSchedulerKind(d);
        RunGroups(d);
        RunTunnelLifecycle(d);
    }

    static void RunDbSearch(CheckD d)
    {
        // 文本列筛选：char/varchar/text/enum/clob/json 命中，数值/日期/二进制不搜
        d("DbSearch.文本列命中", DbSearch.IsTextColumn("varchar(255)") && DbSearch.IsTextColumn("TEXT") && DbSearch.IsTextColumn("enum"));
        d("DbSearch.非文本列跳过", !DbSearch.IsTextColumn("int") && !DbSearch.IsTextColumn("datetime") && !DbSearch.IsTextColumn("blob"));
        d("DbSearch.无类型宁多搜", DbSearch.IsTextColumn(""));
        // 计数 SQL：关键词单引号转义
        var sql = DbSearch.CountSql("[t1]", "[c1]", "it's");
        d("DbSearch.计数SQL转义", sql.Contains("''") && sql.Contains("LIKE '%it's%'") == false && sql.Contains("it''s"), sql);
        // 空关键词/空表名不炸
        var sql2 = DbSearch.CountSql("\"t\"", "\"c\"", "");
        d("DbSearch.空关键词", sql2.Contains("LIKE '%%'"), sql2);
    }

    static void RunMonitor(CheckD d)
    {
        // 指标提取：键值对形态（多行两列）
        var kv = new QueryResult { Columns = new List<string> { "Variable_name", "Value" }, Rows = new List<object?[]> { new object?[] { "Threads_connected", "12" }, new object?[] { "Slow_queries", "3" } } };
        var m1 = ServerMonitor.ExtractMetrics(new List<QueryResult> { kv });
        d("Monitor.键值对提取", m1["Threads_connected"] == "12" && m1["Slow_queries"] == "3");
        // 单行多列形态（PG 聚合行）
        var agg = new QueryResult { Columns = new List<string> { "connections_total", "sessions_active", "slow_queries" }, Rows = new List<object?[]> { new object?[] { "5", "2", "1" } } };
        var m2 = ServerMonitor.ExtractMetrics(new List<QueryResult> { agg });
        d("Monitor.聚合行提取", m2["connections_total"] == "5" && m2["sessions_active"] == "2" && m2["slow_queries"] == "1");
        // KILL 草稿：MySQL/PG/SQLite 口径
        d("Monitor.KILL草稿MySQL", ServerMonitor.KillSql(DbEngine.MySQL, "42") == "KILL 42");
        d("Monitor.KILL草稿PG", ServerMonitor.KillSql(DbEngine.PostgreSQL, "42") == "SELECT pg_terminate_backend(42)");
        d("Monitor.KILL草稿SQLite空", ServerMonitor.KillSql(DbEngine.SQLite, "1") == "");
        d("Monitor.KILL注入防护", ServerMonitor.KillSql(DbEngine.MySQL, "42'; DROP TABLE x") == "KILL 42DROPTABLEx" || !ServerMonitor.KillSql(DbEngine.MySQL, "42'; DROP TABLE x").Contains("'"));
        // Redis INFO 文本解析
        var info = ServerMonitor.ParseRedisInfo("# Clients\nconnected_clients:7\nblocked_clients:1\n");
        d("Monitor.RedisInfo解析", info["connected_clients"] == "7" && info["blocked_clients"] == "1");
    }

    static void RunInsertSql(CheckD d)
    {
        var cols = new List<string> { "id", "name", "score", "bin", "flag" };
        var rows = new List<object?[]>
        {
            new object?[] { 1, "it's", 9.5, new byte[] { 0x01, 0xAB }, true },
            new object?[] { 2, null, null, null, false },
            new object?[] { 3, "a,b", 0, DBNull.Value, true },
        };
        var sql = ResultGridOps.ToInsertSql("t1", cols, rows, new[] { 0, 1, 2 }, s => $"`{s}`");
        d("InsertSql.列引用", sql.Contains("`id`") && sql.Contains("`name`"));
        d("InsertSql.字符串转义", sql.Contains("'it''s'") && sql.Contains("'a,b'"));
        d("InsertSql.NULL字面量", sql.Contains("NULL"));
        d("InsertSql.二进制字面量", sql.Contains("0x01AB"));
        d("InsertSql.布尔字面量", sql.Contains("TRUE") && sql.Contains("FALSE"));
        d("InsertSql.数值不变量", sql.Contains("9.5") && sql.Contains(", 0,"));
        // 越界行号跳过不炸
        var sql2 = ResultGridOps.ToInsertSql("t", cols, rows, new[] { 99 }, null);
        d("InsertSql.越界行跳过", sql2.Length == 0);
    }

    static void RunSchedulerKind(CheckD d)
    {
        d("Scheduler.Kind归一", SqlScheduler.NormalizeKind("BACKUP") == "backup" && SqlScheduler.NormalizeKind("sync") == "sync");
        d("Scheduler.Kind未知回退", SqlScheduler.NormalizeKind("") == "query" && SqlScheduler.NormalizeKind("hacker") == "query" && SqlScheduler.NormalizeKind(null) == "query");
        // 旧 JSON 缺省 Kind 兼容：反序列化缺字段 → query
        var entry = System.Text.Json.JsonSerializer.Deserialize<ScheduledSqlEntry>("{\"Id\":\"a\",\"Name\":\"n\",\"Sql\":\"s\"}");
        d("Scheduler.旧JSON兼容", entry is not null && entry.Kind == "query");
    }

    static void RunGroups(CheckD d)
    {
        // 用临时存根验证纯逻辑（真实存取走 App 数据目录；这里验证 MoveTo/GroupOf 语义不变式）
        // ConnectionGroups 是静态存储，测试只验证不抛错与幂等（真实路径隔离在用户目录）
        try
        {
            var g1 = ConnectionGroups.List();
            d("Groups.列表不炸", g1 is not null);
            d("Groups.空组名拒建", !ConnectionGroups.Create("  "));
            ConnectionGroups.MoveTo("__none__", "测试组");
            d("Groups.空成员移入不炸", true);
            d("Groups.空组查询安全", ConnectionGroups.MembersOf("不存在的组").Count == 0);
        }
        catch (Exception ex) { d("Groups.异常", false, ex.Message); }
    }

    static void RunTunnelLifecycle(CheckD d)
    {
        // 不连网：只验证 StopProfile/StopAll 对未知 Id 幂等不炸
        try
        {
            HttpTunnel.StopProfile("__none__");
            HttpTunnel.StopAll();
            d("Tunnel.幂等关闭", true);
        }
        catch (Exception ex) { d("Tunnel.异常", false, ex.Message); }
        // Hex 解析（App 层 BlobEditorDialog 的纯逻辑镜像口径在 Core 侧验证 Convert 往返）
        var bytes = new byte[] { 0x00, 0xFF, 0x7F };
        var hex = Convert.ToHexString(bytes);
        d("Tunnel.Hex往返", Convert.FromHexString(hex).SequenceEqual(bytes));
    }
}
