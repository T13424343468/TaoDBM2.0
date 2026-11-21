namespace TaoDBM.Core;

/// <summary>复制节点状态：Role=主库/从库；State=正常/延迟偏高/已断开/未知；LagSeconds=null 表示主库或取不到。</summary>
public sealed record TopologyNode(string Name, string Role, string State, double? LagSeconds, string Detail);

/// <summary>B10 主从管理面目录：角色探测、复制节点清单、延迟分级、管理操作草稿（仅生成语句，不执行）。</summary>
/// <remarks>MySQL 主视角走 SHOW REPLICAS、从视角走 SHOW REPLICA STATUS；PG 走 pg_stat_replication / pg_is_in_recovery；
/// SQL Server 走 Always On 可用性组目录视图（sys.dm_hadr_availability_replica_states）。执行由界面层预览+确认。</remarks>
public static class ReplicationCatalog
{
    /// <summary>延迟分级阈值：≥10s 延迟偏高（标黄），≥60s 视为接近断链（danger）。</summary>
    public const double LagWarnSeconds = 10;
    public const double LagDangerSeconds = 60;

    public static bool Supported(DbEngine e) => e is DbEngine.MySQL or DbEngine.PostgreSQL or DbEngine.SqlServer;

    public static string UnsupportedReason(DbEngine e) => e switch
    {
        DbEngine.SQLite => "SQLite 为嵌入式库文件，无主从复制拓扑。",
        DbEngine.MongoDB or DbEngine.Redis => "MongoDB / Redis 的复制体系不适用本管理面（副本集 / 主从另有口径，后续批次评估）。",
        _ => "该引擎的复制管理面下一批补齐。",
    };

    /// <summary>角色探测：MySQL 以是否能读到复制线程状态为准；PG 以 pg_is_in_recovery 为准；
    /// SQL Server 只在本地副本处于 SECONDARY 角色时返回行（界面按「有行即从库」判定）。</summary>
    public static string RoleProbeSql(DbEngine e) => e switch
    {
        DbEngine.MySQL => "SHOW REPLICA STATUS",
        DbEngine.PostgreSQL => "SELECT pg_is_in_recovery() AS in_recovery",
        DbEngine.SqlServer => "SELECT role_desc FROM sys.dm_hadr_availability_replica_states WHERE is_local = 1 AND role_desc = 'SECONDARY'",
        _ => throw new NotSupportedException(UnsupportedReason(e)),
    };

    /// <summary>主视角从库清单（含延迟列）。</summary>
    public static string ReplicasSql(DbEngine e) => e switch
    {
        DbEngine.MySQL => "SHOW REPLICAS",
        DbEngine.PostgreSQL =>
            "SELECT COALESCE(application_name, '-') AS name, COALESCE(client_addr::text, '-') AS addr, state, sync_state, " +
            "COALESCE(EXTRACT(EPOCH FROM COALESCE(replay_lag, write_lag)), 0) AS lag_seconds FROM pg_stat_replication ORDER BY application_name",
        DbEngine.SqlServer =>
            "SELECT COALESCE(r.replica_server_name, '-') AS name, COALESCE(ars.role_desc, '-') AS sync_state, " +
            "COALESCE(ars.connected_state_desc, '-') AS state, CAST(NULL AS FLOAT) AS lag_seconds " +
            "FROM sys.dm_hadr_availability_replica_states ars JOIN sys.availability_replicas r ON r.replica_id = ars.replica_id " +
            "WHERE ars.role_desc = 'SECONDARY' ORDER BY r.replica_server_name",
        _ => throw new NotSupportedException(UnsupportedReason(e)),
    };

    /// <summary>从视角：本从库复制状态（MySQL 单行多列；PG 回放水位延迟；SQL Server 本地副本角色与连接态）。</summary>
    public static string ReplicaSelfStatusSql(DbEngine e) => e switch
    {
        DbEngine.MySQL => "SHOW REPLICA STATUS",
        DbEngine.PostgreSQL =>
            "SELECT COALESCE(EXTRACT(EPOCH FROM now() - pg_last_xact_replay_timestamp()), 0) AS lag_seconds, " +
            "pg_is_wal_receive_paused() AS receive_paused",
        DbEngine.SqlServer =>
            "SELECT role_desc, connected_state_desc, synchronization_health_desc FROM sys.dm_hadr_availability_replica_states WHERE is_local = 1",
        _ => throw new NotSupportedException(UnsupportedReason(e)),
    };

    /// <summary>按列名取值（大小写不敏感；兼容 MySQL 新旧列名由调用方传多个候选名）。</summary>
    public static string? Col(IReadOnlyList<string> columns, object?[] row, params string[] names)
    {
        foreach (var n in names)
        {
            var i = Array.FindIndex(columns.ToArray(), c => c.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && i < row.Length) return row[i]?.ToString();
        }
        return null;
    }

    /// <summary>延迟分级：null=主库（—）；≥60 danger「严重滞后」；≥10 warn「延迟偏高」；其余「正常」。</summary>
    public static (string State, string Severity) LagClass(double? lag) => lag switch
    {
        null => ("—", "none"),
        >= LagDangerSeconds => ("严重滞后", "danger"),
        >= LagWarnSeconds => ("延迟偏高", "warn"),
        _ => ("正常", "ok"),
    };

    /// <summary>主视角拓扑构建：本节点为主库，逐个从库节点解析延迟并分级。</summary>
    public static List<TopologyNode> BuildFromPrimary(DbEngine e, IReadOnlyList<string> columns, IEnumerable<object?[]> rows)
    {
        var nodes = new List<TopologyNode>
        {
            new("本机（主库）", "主库", "正常", null, "当前连接节点"),
        };
        foreach (var r in rows)
        {
            if (e == DbEngine.MySQL)
            {
                var name = Col(columns, r, "Server_name", "server_name") is { Length: > 0 } n ? n
                    : Col(columns, r, "Host", "host") is { Length: > 0 } h ? h : "replica";
                nodes.Add(new TopologyNode(name, "从库", "未知", null, "延迟以从库自身视角为准，点重连后在从库连接上查看"));
            }
            else
            {
                var name = Col(columns, r, "name") ?? "-";
                var state = Col(columns, r, "state") ?? "unknown";
                var sync = Col(columns, r, "sync_state") ?? "";
                var lag = double.TryParse(Col(columns, r, "lag_seconds"), out var l) ? l : (double?)null;
                var (st, _) = LagClass(lag);
                var shown = state switch
                {
                    "streaming" => st,
                    "startup" or "catchup" => "连接中",
                    _ => "已断开",
                };
                nodes.Add(new TopologyNode(name, "从库", shown, lag, $"{state} / {sync}"));
            }
        }
        return nodes;
    }

    /// <summary>从视角：解析自身状态行（MySQL SHOW REPLICA STATUS 单行多列；PG 单行两列）。</summary>
    public static TopologyNode BuildFromReplica(DbEngine e, IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        if (e == DbEngine.MySQL)
        {
            var io = Col(columns, rows.Count > 0 ? rows[0] : Array.Empty<object?>(), "Replica_IO_Running", "Slave_IO_Running");
            var sql = Col(columns, rows.Count > 0 ? rows[0] : Array.Empty<object?>(), "Replica_SQL_Running", "Slave_SQL_Running");
            var lagStr = Col(columns, rows.Count > 0 ? rows[0] : Array.Empty<object?>(), "Seconds_BehindSource", "Seconds_Behind_Master", "Seconds_BehindSource");
            double? lag = double.TryParse(lagStr, out var l) ? l : null;
            var running = string.Equals(io, "Yes", StringComparison.OrdinalIgnoreCase)
                          && string.Equals(sql, "Yes", StringComparison.OrdinalIgnoreCase);
            var (st, _) = LagClass(running ? lag : 9999);
            if (!running) st = "已断开";
            var host = Col(columns, rows.Count > 0 ? rows[0] : Array.Empty<object?>(), "Source_Host", "Master_Host") ?? "-";
            return new TopologyNode("本机（从库）", "从库", st, lag, $"来源 {host}；IO={io} / SQL={sql}");
        }
        var lagPg = rows.Count > 0 && double.TryParse(Col(columns, rows[0], "lag_seconds"), out var lp) ? lp : (double?)null;
        var (stPg, _) = LagClass(lagPg);
        return new TopologyNode("本机（从库）", "从库", stPg, lagPg, "pg_stat_wal_receiver 视角");
    }

    /// <summary>从视角（SQL Server）：解析本地可用性组副本的角色 / 连接态 / 同步健康。</summary>
    public static TopologyNode BuildFromReplicaSqlServer(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        var role = Col(columns, rows.Count > 0 ? rows[0] : Array.Empty<object?>(), "role_desc") ?? "UNKNOWN";
        var conn = Col(columns, rows.Count > 0 ? rows[0] : Array.Empty<object?>(), "connected_state_desc") ?? "UNKNOWN";
        var health = Col(columns, rows.Count > 0 ? rows[0] : Array.Empty<object?>(), "synchronization_health_desc") ?? "UNKNOWN";
        var st = conn switch
        {
            "CONNECTED" => health == "HEALTHY" ? "正常" : "延迟偏高",
            "CONNECTING" => "连接中",
            _ => "已断开",
        };
        return new TopologyNode("本机（从库）", "从库", st, null, $"Always On 角色 {role}；连接 {conn} / 同步 {health}");
    }

    // ---------- 管理操作草稿（仅生成，不执行） ----------

    /// <summary>暂停 / 恢复复制（MySQL 停启复制线程；PG 暂停/恢复回放；SQL Server 暂停/恢复可用性组数据移动）。</summary>
    public static List<string> PauseReplicaSql(DbEngine e, bool pause) => e switch
    {
        DbEngine.MySQL => pause
            ? new List<string> { "STOP REPLICA;", "SELECT @@server_id AS node_id, NOW() AS paused_at;" }
            : new List<string> { "START REPLICA;", "SELECT @@server_id AS node_id, NOW() AS resumed_at;" },
        DbEngine.PostgreSQL => pause
            ? new List<string> { "SELECT pg_wal_replay_pause();" }
            : new List<string> { "SELECT pg_wal_replay_resume();" },
        DbEngine.SqlServer => pause
            ? new List<string> { "ALTER AVAILABILITY GROUP [<可用组名现场填入>] PAUSE;", "SELECT role_desc, connected_state_desc FROM sys.dm_hadr_availability_replica_states WHERE is_local = 1;" }
            : new List<string> { "ALTER AVAILABILITY GROUP [<可用组名现场填入>] RESUME;", "SELECT role_desc, connected_state_desc FROM sys.dm_hadr_availability_replica_states WHERE is_local = 1;" },
        _ => throw new NotSupportedException(UnsupportedReason(e)),
    };

    /// <summary>重建复制通道模板（通道参数需 DBA 按现场补齐，模板只作起点）。</summary>
    public static List<string> ReconnectTemplateSql(DbEngine e, string sourceHost, int sourcePort, string replicaUser, string dbName)
    {
        string h = sourceHost.Replace("'", "''"), u = replicaUser.Replace("'", "''"), d = dbName.Replace("'", "''");
        return e switch
        {
            DbEngine.MySQL => new List<string>
            {
                $"STOP REPLICA;",
                $"CHANGE REPLICATION SOURCE TO SOURCE_HOST='{h}', SOURCE_PORT={sourcePort}, SOURCE_USER='{u}', SOURCE_PASSWORD='<口令现场填入>', SOURCE_LOG_FILE='<来源二进制日志文件>', SOURCE_LOG_POS=<位点>;",
                "START REPLICA;",
                "SHOW REPLICA STATUS;",
            },
            DbEngine.PostgreSQL => new List<string>
            {
                "-- PostgreSQL 从节点重建通常在数据目录层完成（pg_basebackup），以下为校验语句：",
                $"SELECT now(), '{d}' AS database, '{h}:{sourcePort}' AS source, pg_is_in_recovery() AS in_recovery;",
                "SELECT status, sender_host, latest_end_lsn FROM pg_stat_wal_receiver;",
            },
            DbEngine.SqlServer => new List<string>
            {
                "-- SQL Server 从副本重建通常走备份/恢复 + 加入可用组，以下为校验语句：",
                $"SELECT DB_NAME() AS database_name, '{h}:{sourcePort}' AS source, '{d}' AS target_db;",
                "SELECT role_desc, connected_state_desc, synchronization_health_desc FROM sys.dm_hadr_availability_replica_states WHERE is_local = 1;",
                "SELECT replica_server_name, role_desc FROM sys.dm_hadr_availability_replica_states ars JOIN sys.availability_replicas r ON r.replica_id = ars.replica_id;",
            },
            _ => throw new NotSupportedException(UnsupportedReason(e)),
        };
    }

    /// <summary>切换主库草稿（高危）：MySQL 提升从库三步并显式停旧通道；PG 基于回放完成的 promote；SQL Server 走可用组强制故障转移。</summary>
    public static List<string> SwitchoverDraftSql(DbEngine e, string oldSourceHost)
    {
        string h = oldSourceHost.Replace("'", "''");
        return e switch
        {
            DbEngine.MySQL => new List<string>
            {
                "STOP REPLICA;",
                "RESET REPLICA ALL;",
                "-- 提升后请立刻为新从库建复制账号，并调整应用连接串指向本节点；",
                $"-- 旧主库 '{h}' 需以从库身份重新接入，避免双主写入。",
            },
            DbEngine.PostgreSQL => new List<string>
            {
                "SELECT pg_promote(wait_seconds := 30);",
                $"SELECT pg_is_in_recovery() AS still_replica;  -- 期望 f；旧主库 '{h}' 需重建为从库后接入",
            },
            DbEngine.SqlServer => new List<string>
            {
                "ALTER AVAILABILITY GROUP [<可用组名现场填入>] FORCE_FAILOVER_ALLOW_DATA_LOSS;",
                "SELECT role_desc, connected_state_desc FROM sys.dm_hadr_availability_replica_states WHERE is_local = 1;  -- 期望 PRIMARY",
                $"-- 旧主库 '{h}' 恢复后需以 SECONDARY 身份重新接入可用组，避免双主写入。",
            },
            _ => throw new NotSupportedException(UnsupportedReason(e)),
        };
    }
}
