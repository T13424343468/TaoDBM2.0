namespace TaoDBM.Core;

/// <summary>B11 图形化分库分区分表向导：分片 DDL 方案生成 + 分区现状目录 + 行数分布倾斜判定（纯逻辑，执行由界面层预览+确认）。</summary>
public static class ShardingWizard
{
    public static bool Supported(DbEngine e) => e is DbEngine.MySQL or DbEngine.PostgreSQL;

    public static string UnsupportedReason(DbEngine e) => e switch
    {
        DbEngine.SQLite => "SQLite 无分区 / 分表方言。",
        DbEngine.MongoDB or DbEngine.Redis => "MongoDB / Redis 的分片体系不适用 SQL 分区向导。",
        _ => "该引擎的分片向导下一批补齐。",
    };

    /// <summary>按键取模 n 分片的建分区方案。MySQL 原生 HASH 分区一条 ALTER；PG 用声明式分区（需建新表迁移，注释随方案给出）。</summary>
    public static IReadOnlyList<string> HashPartitionSql(DbEngine e, string table, string keyColumn, int shardCount)
    {
        if (shardCount < 2) throw new ArgumentOutOfRangeException(nameof(shardCount), "分片数至少为 2");
        string t = Qe(e, table), k = Qe(e, keyColumn), raw = table.Replace("'", "''"), rk = keyColumn.Replace("'", "''");
        return e switch
        {
            DbEngine.MySQL => new List<string>
            {
                $"ALTER TABLE {t} PARTITION BY HASH ({k}) PARTITIONS {shardCount};",
                $"SELECT PARTITION_NAME, TABLE_ROWS FROM information_schema.PARTITIONS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{raw}' ORDER BY PARTITION_ORDINAL_POSITION;",
            },
            DbEngine.PostgreSQL => BuildPostgresPlan(raw, rk, t, k, shardCount),
            _ => throw new NotSupportedException(UnsupportedReason(e)),
        };
    }

    private static List<string> BuildPostgresPlan(string raw, string rk, string t, string k, int n)
    {
        var plan = new List<string>
        {
            $"-- PostgreSQL 声明式分区改造：按 {rk} 取模 {n} 分片，需建新表迁移数据（可与在线改表流程配合）",
            $"CREATE TABLE {t}_pnew (LIKE {t} INCLUDING ALL) PARTITION BY HASH ({k});",
        };
        for (int i = 0; i < n; i++)
            plan.Add($"CREATE TABLE {t}_p{i} PARTITION OF {t}_pnew FOR VALUES WITH (MODULUS {n}, REMAINDER {i});");
        plan.Add($"-- 迁移：INSERT INTO {t}_pnew SELECT * FROM {t}; 校验后换名（旧表改名保留即回滚路径）");
        return plan;
    }

    /// <summary>分区 / 分表现状目录：两列（分区名，行数）。</summary>
    public static string PartitionsInfoSql(DbEngine e, string schema, string table) => e switch
    {
        DbEngine.MySQL =>
            $"SELECT PARTITION_NAME AS pname, COALESCE(TABLE_ROWS, 0) AS rows FROM information_schema.PARTITIONS " +
            $"WHERE TABLE_SCHEMA = '{schema.Replace("'", "''")}' AND TABLE_NAME = '{table.Replace("'", "''")}' ORDER BY PARTITION_ORDINAL_POSITION",
        DbEngine.PostgreSQL =>
            "SELECT c.relname AS pname, COALESCE(c.reltuples::bigint, 0) AS rows FROM pg_inherits i " +
            "JOIN pg_class c ON c.oid = i.inhrelid JOIN pg_class p ON p.oid = i.inhparent " +
            $"WHERE p.relname = '{table.Replace("'", "''")}' AND p.relkind = 'p' ORDER BY c.relname",
        _ => throw new NotSupportedException(UnsupportedReason(e)),
    };

    /// <summary>分布倾斜判定：max > skewFactor × min 即倾斜（分片多于一个才有意义），行数按降序返回。</summary>
    public static (List<(string Name, long Rows)> Parts, bool Skewed) AnalyzeDistribution(
        IEnumerable<(string Name, long Rows)> parts, double skewFactor = 1.5)
    {
        var list = parts.OrderByDescending(p => p.Rows).ToList();
        if (list.Count == 0) return (list, false);
        long min = list.Min(p => p.Rows);
        long max = list.Max(p => p.Rows);
        bool skewed = list.Count > 1 && max > min * skewFactor;
        return (list, skewed);
    }

    /// <summary>热点盐化摘要：盐桶数与打散后的键列表（接 HotspotSalting 引擎，展示用）。</summary>
    public static string SaltingSummary(string hotKey, int saltCount)
    {
        var plan = HotspotSalting.Plan(hotKey, saltCount);
        return $"热点键 {hotKey}：建议盐桶数 {saltCount}，写入按桶打散为 {plan.SaltedKeys.Count} 个键：{string.Join("、", plan.SaltedKeys)}";
    }

    private static string Qe(DbEngine e, string ident) => e switch
    {
        DbEngine.MySQL => "`" + ident.Replace("`", "") + "`",
        _ => "\"" + ident.Replace("\"", "") + "\"",
    };
}
