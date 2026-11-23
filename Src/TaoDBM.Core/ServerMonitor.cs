namespace TaoDBM.Core;

/// <summary>服务器监控采样：指标键值对 + 会话清单（可终止）。</summary>
public sealed class MonitorSample
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    /// <summary>指标名 → 值（连接数/活跃会话/慢查询计数等，按引擎可得性给）。</summary>
    public Dictionary<string, string> Metrics { get; init; } = new();
    /// <summary>会话列表：列名与行由引擎决定（会话Id 列约定名 sessionId）。</summary>
    public QueryResult? Sessions { get; init; }
}

/// <summary>
/// 批次三格2：服务器监控指标目录——连接数/活跃会话/慢查询计数按七方言出 SQL，
/// 会话列表带终止草稿（沿用 DiagnosticsDialog 的 KILL 草稿口径，确认后由调用方执行）。
/// 纯逻辑可测试（SQL 生成独立成方法）；采样执行由调用方（ServerMonitorDialog）接管。
/// </summary>
public static class ServerMonitor
{
    /// <summary>指标采样 SQL（单条多结果集口径：引擎支持则一次回多结果，调用方按列名取值）。</summary>
    public static string MetricsSql(DbEngine e, string db) => e switch
    {
        DbEngine.MySQL => "SHOW GLOBAL STATUS WHERE Variable_name IN ('Threads_connected','Threads_running','Slow_queries','Questions');SELECT Id, User, Host, db, Command, Time, State, Info FROM information_schema.PROCESSLIST",
        DbEngine.PostgreSQL => "SELECT count(*) FILTER (WHERE state='idle') AS connections_idle, count(*) FILTER (WHERE state='active') AS sessions_active, count(*) FILTER (WHERE state='active' AND now()-query_start > interval '1 second') AS slow_queries, count(*) AS connections_total FROM pg_stat_activity;SELECT pid AS sessionId, usename, application_name, client_addr, state, now()-query_start AS duration, left(query,120) AS query FROM pg_stat_activity WHERE pid <> pg_backend_pid()",
        DbEngine.SqlServer => "SELECT (SELECT count(*) FROM sys.dm_exec_sessions WHERE is_user_process=1) AS connections_total, (SELECT count(*) FROM sys.dm_exec_sessions WHERE is_user_process=1 AND status='running') AS sessions_active, (SELECT count(*) FROM sys.dm_exec_requests WHERE status='running' AND total_elapsed_time > 1000000) AS slow_queries;SELECT session_id AS sessionId, login_name, host_name, status, start_time, LEFT(COALESCE(text,''),120) AS query FROM sys.dm_exec_sessions s LEFT JOIN sys.dm_exec_requests r ON s.session_id=r.session_id OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) WHERE s.is_user_process=1",
        DbEngine.Oracle => "SELECT (SELECT count(*) FROM V$SESSION) AS connections_total, (SELECT count(*) FROM V$SESSION WHERE status='ACTIVE') AS sessions_active FROM DUAL;SELECT sid || ',' || serial# AS sessionId, username, machine, status, seconds_in_wait FROM V$SESSION WHERE username IS NOT NULL",
        DbEngine.MongoDB => "TAODBMCMD:serverStatus",
        DbEngine.Redis => "INFO clients",
        _ => "SELECT '单文件引擎无服务器进程指标' AS note",   // SQLite：如实提示，不硬造指标
    };

    /// <summary>终止会话草稿（确认后执行；SQLite 无会话概念返回空）。</summary>
    public static string KillSql(DbEngine e, string sessionId) => e switch
    {
        DbEngine.MySQL => $"KILL {sessionId.Replace("'", "")}",
        DbEngine.PostgreSQL => $"SELECT pg_terminate_backend({sessionId.Replace("'", "")})",
        DbEngine.SqlServer => $"KILL {sessionId.Replace("'", "")}",
        DbEngine.Oracle => $"ALTER SYSTEM KILL SESSION '{sessionId.Replace("'", "")}'",
        _ => "",
    };

    /// <summary>从指标结果集提取键值对（列名小写归一；多结果集逐个扫）。</summary>
    public static Dictionary<string, string> ExtractMetrics(IReadOnlyList<QueryResult> results)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
        {
            if (r.Rows.Count == 0) continue;
            // 键值对形态（MySQL SHOW STATUS / Redis INFO 解析后）：第一列=名，第二列=值
            if (r.Columns.Count >= 2 && r.Rows.Count > 1)
            {
                foreach (var row in r.Rows)
                {
                    var k = Convert.ToString(row[0]) ?? "";
                    if (k.Length > 0 && !dict.ContainsKey(k)) dict[k] = Convert.ToString(row[1]) ?? "";
                }
            }
            else if (r.Columns.Count >= 2 && r.Rows.Count == 1)
            {
                for (int c = 0; c < r.Columns.Count; c++)
                    dict[r.Columns[c]] = Convert.ToString(r.Rows[0][c]) ?? "";
            }
            else if (r.Columns.Count == 1)
            {
                dict.TryAdd(r.Columns[0], Convert.ToString(r.Rows[0][0]) ?? "");
            }
        }
        return dict;
    }

    /// <summary>Redis INFO clients 文本解析（INFO 回的是纯文本不是结果集）。</summary>
    public static Dictionary<string, string> ParseRedisInfo(string text)
    {
        var dict = new Dictionary<string, string>();
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            int i = t.IndexOf(':');
            if (i > 0) dict.TryAdd(t[..i].Trim(), t[(i + 1)..].Trim());
        }
        return dict;
    }
}
