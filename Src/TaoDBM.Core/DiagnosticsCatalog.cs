using System;
using System.Collections.Generic;

namespace TaoDBM.Core;

/// <summary>档四：诊断引擎目录——按引擎生成只读体检命令（版本 / 规模 / 锁等待 / 健康快照），不做执行。</summary>
/// <remarks>纯引擎免界面稿：七库全覆盖。关系库走 SQL 元数据与 v$ / pg_stat 视图，
/// MongoDB 走 TAODBMCMD:serverStatus 命令，Redis 走 INFO 分区命令；命令由提供者执行。</remarks>
public static class DiagnosticsCatalog
{
    /// <summary>该引擎是否有深度体检口径（连通性检查之外还有版本/规模/健康采集）。</summary>
    public static bool Supported(DbEngine e) => e is DbEngine.MySQL or DbEngine.PostgreSQL or DbEngine.SQLite
        or DbEngine.Oracle or DbEngine.MongoDB or DbEngine.Redis or DbEngine.SqlServer;

    /// <summary>版本查询（Mongo 返回 TAODBMCMD 命令；Redis 返回 INFO 分区）。</summary>
    public static string VersionQuery(DbEngine e) => e switch
    {
        DbEngine.MySQL => "SELECT VERSION()",
        DbEngine.PostgreSQL => "SELECT version()",
        DbEngine.SQLite => "SELECT sqlite_version()",
        DbEngine.SqlServer => "SELECT @@VERSION AS v",
        DbEngine.Oracle => "SELECT banner FROM v$version WHERE ROWNUM = 1",
        DbEngine.MongoDB => "TAODBMCMD:serverStatus",
        DbEngine.Redis => "INFO server",
        _ => throw new NotSupportedException("该引擎不支持版本查询"),
    };

    /// <summary>规模最大的表（前 8）；Mongo/Redis 无关系表规模口径，返回 null 跳过。</summary>
    public static string? TopTablesQuery(DbEngine e, string db) => e switch
    {
        DbEngine.MySQL => "SELECT table_name AS t, IFNULL(table_rows, 0) AS r FROM information_schema.tables " +
            $"WHERE table_schema = '{Esc(db)}' ORDER BY table_rows DESC LIMIT 8",
        DbEngine.PostgreSQL => "SELECT relname AS t, n_live_tup AS r FROM pg_stat_user_tables ORDER BY n_live_tup DESC LIMIT 8",
        DbEngine.SqlServer => "SELECT TOP 8 t.name AS t, SUM(p.rows) AS r FROM sys.tables t " +
            "JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1) " +
            "GROUP BY t.name ORDER BY r DESC",
        DbEngine.Oracle => "SELECT TABLE_NAME AS t, NUM_ROWS AS r FROM USER_TABLES ORDER BY NUM_ROWS DESC FETCH FIRST 8 ROWS ONLY",
        DbEngine.MongoDB => "TAODBMCMD:topCollections",
        _ => null,
    };

    /// <summary>锁等待查询；无锁视图的引擎返回 null（SQLite / Mongo / Redis）。</summary>
    public static string? LockWaitQuery(DbEngine e) => e switch
    {
        DbEngine.MySQL => "SELECT REQUESTING_ENGINE_TRANSACTION_ID AS waiter, BLOCKING_ENGINE_TRANSACTION_ID AS holder, " +
            "IFNULL(OBJECT_NAME, '') AS resource FROM performance_schema.data_lock_waits",
        DbEngine.PostgreSQL => "SELECT pid::text, array_to_string(pg_blocking_pids(pid), ',') FROM pg_stat_activity " +
            "WHERE cardinality(pg_blocking_pids(pid)) > 0",
        DbEngine.SqlServer => "SELECT w.session_id AS waiter, b.session_id AS holder, " +
            "COALESCE(w.wait_resource, '') AS resource FROM sys.dm_exec_requests w " +
            "JOIN sys.dm_exec_sessions b ON b.session_id = w.blocking_session_id WHERE w.blocking_session_id > 0",
        DbEngine.Oracle => "SELECT s1.SID || ',' || s1.SERIAL# AS waiter, s2.SID || ',' || s2.SERIAL# AS holder, " +
            "w.CTIME AS ctime FROM V$LOCK w JOIN V$SESSION s1 ON w.SID = s1.SID LEFT JOIN V$LOCK l2 ON w.ID1 = l2.ID1 " +
            "AND l2.REQUEST = 0 LEFT JOIN V$SESSION s2 ON l2.SID = s2.SID WHERE w.REQUEST > 0",
        _ => null,
    };

    /// <summary>健康快照命令（Mongo serverStatus 摘要 / Redis INFO 分区）；关系库无独立健康命令返回 null。</summary>
    public static string? HealthCommand(DbEngine e) => e switch
    {
        DbEngine.MongoDB => "TAODBMCMD:serverStatus",
        DbEngine.Redis => "INFO stats",
        _ => null,
    };

    private static string Esc(string s) => s.Replace("'", "''");
}
