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
            $"CREATE TABLE {raw}_pnew (LIKE {raw} INCLUDING ALL) PARTITION BY HASH ({rk});",
        };
        for (int i = 0; i < n; i++)
            plan.Add($"CREATE TABLE {raw}_p{i} PARTITION OF {raw}_pnew FOR VALUES WITH (MODULUS {n}, REMAINDER {i});");
        plan.Add($"-- 迁移：INSERT INTO {raw}_pnew SELECT * FROM {raw}; 校验后换名（旧表改名保留即回滚路径）");
        return plan;
    }

    /// <summary>分片策略：按范围 / 按哈希 / 按类别（列表），四步向导第 2 步点选。</summary>
    public enum ShardStrategy { Range, Hash, List }

    /// <summary>策略显示名（向导点选与确认页共用，顺序与 ShardStrategy 一致）。</summary>
    public static readonly string[] StrategyNames = ["按范围（RANGE）", "按哈希（HASH）", "按类别（LIST）"];

    /// <summary>按范围分片方案：bounds 为各分片升序上界（数字原样、其余按字符串字面量转义），末段自动补 MAXVALUE；
    /// MySQL 一条 ALTER RANGE，PG 走声明式分区建新表迁移。</summary>
    public static IReadOnlyList<string> RangePartitionSql(DbEngine e, string table, string keyColumn, IReadOnlyList<string> bounds)
    {
        if (bounds.Count < 2) throw new ArgumentOutOfRangeException(nameof(bounds), "范围分片至少需要 2 个上界");
        string t = Qe(e, table), k = Qe(e, keyColumn), raw = table.Replace("'", "''"), rk = keyColumn.Replace("'", "''");
        var toks = bounds.Select(BoundToken).ToList();
        return e switch
        {
            DbEngine.MySQL => BuildMysqlRange(t, k, raw, rk, toks),
            DbEngine.PostgreSQL => BuildPgRange(raw, rk, t, k, toks),
            _ => throw new NotSupportedException(UnsupportedReason(e)),
        };
    }

    private static List<string> BuildMysqlRange(string t, string k, string raw, string rk, List<string> toks)
    {
        var plan = new List<string>();
        var defs = new List<string>();
        for (int i = 0; i < toks.Count; i++)
            defs.Add($"PARTITION p{i} VALUES LESS THAN ({toks[i]})");
        defs.Add("PARTITION pn VALUES LESS THAN (MAXVALUE)");
        plan.Add($"ALTER TABLE {t} PARTITION BY RANGE ({k}) (\n{string.Join(",\n", defs)});");
        plan.Add($"SELECT PARTITION_NAME, TABLE_ROWS FROM information_schema.PARTITIONS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{raw}' ORDER BY PARTITION_ORDINAL_POSITION;");
        return plan;
    }

    private static List<string> BuildPgRange(string raw, string rk, string t, string k, List<string> toks)
    {
        var plan = new List<string>
        {
            $"-- PostgreSQL 声明式分区：按 {rk} 范围 {toks.Count + 1} 段，需建新表迁移数据（可与在线改表流程配合）",
            $"CREATE TABLE {raw}_pnew (LIKE {raw} INCLUDING ALL) PARTITION BY RANGE ({rk});",
        };
        for (int i = 0; i < toks.Count; i++)
            plan.Add($"CREATE TABLE {raw}_p{i} PARTITION OF {raw}_pnew FOR VALUES FROM ({(i == 0 ? "MINVALUE" : toks[i - 1])}) TO ({toks[i]});");
        plan.Add($"-- 迁移：INSERT INTO {raw}_pnew SELECT * FROM {raw}; 校验后换名（旧表改名保留即回滚路径）");
        return plan;
    }

    /// <summary>按类别（列表）分片方案：parts 为类别组（组名 + 取值清单，数字原样、其余按字符串字面量转义）；
    /// MySQL 一条 ALTER LIST，PG 走声明式分区建新表迁移。</summary>
    public static IReadOnlyList<string> ListPartitionSql(DbEngine e, string table, string keyColumn,
        IReadOnlyList<(string Name, IReadOnlyList<string> Values)> parts)
    {
        if (parts.Count < 2) throw new ArgumentOutOfRangeException(nameof(parts), "类别分片至少需要 2 个类别组");
        var groups = parts.Select(p =>
        {
            if (string.IsNullOrWhiteSpace(p.Name)) throw new ArgumentException("类别组名不能为空", nameof(parts));
            if (p.Values.Count == 0) throw new ArgumentException($"类别组 {p.Name} 至少需要一个取值", nameof(parts));
            return (CleanName(p.Name), p.Values.Select(BoundToken).ToList());
        }).ToList();
        string t = Qe(e, table), k = Qe(e, keyColumn), raw = table.Replace("'", "''"), rk = keyColumn.Replace("'", "''");
        return e switch
        {
            DbEngine.MySQL => BuildMysqlList(t, k, raw, groups),
            DbEngine.PostgreSQL => BuildPgList(rk, t, k, groups),
            _ => throw new NotSupportedException(UnsupportedReason(e)),
        };
    }

    private static List<string> BuildMysqlList(string t, string k, string raw, List<(string Name, List<string> Values)> groups)
    {
        var defs = groups.Select(g => $"PARTITION {g.Name} VALUES IN ({string.Join(", ", g.Values)})").ToList();
        return new List<string>
        {
            $"ALTER TABLE {t} PARTITION BY LIST ({k}) (\n{string.Join(",\n", defs)});",
            $"SELECT PARTITION_NAME, TABLE_ROWS FROM information_schema.PARTITIONS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{raw}' ORDER BY PARTITION_ORDINAL_POSITION;",
        };
    }

    private static List<string> BuildPgList(string rk, string t, string k, List<(string Name, List<string> Values)> groups)
    {
        var raw = t.Replace("\"", "");
        var plan = new List<string>
        {
            $"-- PostgreSQL 声明式分区：按 {rk} 类别 {groups.Count} 组，需建新表迁移数据（可与在线改表流程配合）",
            $"CREATE TABLE {raw}_pnew (LIKE {raw} INCLUDING ALL) PARTITION BY LIST ({rk});",
        };
        foreach (var g in groups)
            plan.Add($"CREATE TABLE {raw}_{g.Name} PARTITION OF {raw}_pnew FOR VALUES IN ({string.Join(", ", g.Values)});");
        plan.Add($"-- 迁移：INSERT INTO {raw}_pnew SELECT * FROM {raw}; 校验后换名（旧表改名保留即回滚路径）");
        return plan;
    }

    /// <summary>边界取值规范化：整数原样；带引号的字符串去引号后重转义；其余按字符串字面量转义。</summary>
    private static string BoundToken(string rawBound)
    {
        var v = rawBound.Trim();
        if (v.Length == 0) throw new ArgumentException("分片边界取值不能为空");
        bool numeric = v.All(char.IsAsciiDigit) || (v[0] == '-' && v.Length > 1 && v[1..].All(char.IsAsciiDigit));
        if (numeric) return v;
        if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') v = v[1..^1].Replace("''", "'");
        return "'" + v.Replace("'", "''") + "'";
    }

    /// <summary>类别组名规范化：剥掉标识符引用符并去空白，保留字母数字下标字符。</summary>
    private static string CleanName(string name)
    {
        var v = name.Trim().Replace("`", "").Replace("\"", "");
        if (v.Length == 0) throw new ArgumentException("类别组名不能为空");
        return new string(v.Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } s ? s : throw new ArgumentException($"类别组名 {name} 无有效字符");
    }

    /// <summary>分片键依据的元数据查询：返回主键 + 普通索引列的 DISTINCT 清单（MySQL 走 STATISTICS，PG 走 pg_index）。</summary>
    public static string IndexColumnsSql(DbEngine e, string schema, string table) => e switch
    {
        DbEngine.MySQL =>
            "SELECT DISTINCT COLUMN_NAME AS col FROM information_schema.STATISTICS " +
            $"WHERE TABLE_SCHEMA = '{schema.Replace("'", "''")}' AND TABLE_NAME = '{table.Replace("'", "''")}' ORDER BY COLUMN_NAME",
        DbEngine.PostgreSQL =>
            "SELECT DISTINCT a.attname AS col FROM pg_index i JOIN pg_class c ON c.oid = i.indrelid " +
            "JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = ANY (i.indkey) " +
            $"WHERE c.relname = '{table.Replace("'", "''")}' AND a.attnum > 0 ORDER BY a.attname",
        _ => throw new NotSupportedException(UnsupportedReason(e)),
    };

    /// <summary>分片键依据判定：列出现在索引元数据（主键或普通索引）内即有依据，列名不区分大小写匹配。</summary>
    public static bool KeyBacked(string column, IEnumerable<string> indexedColumns) =>
        indexedColumns.Any(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase));

    /// <summary>熟手解锁动作的审计行：解锁全部字段时写入 permops.log 的统一格式（时间戳由调用方补）。</summary>
    public static string UnlockAudit(string table, string reason)
    {
        var t = table.Replace("\t", " ").Trim();
        if (t.Length == 0) throw new ArgumentException("表名不能为空", nameof(table));
        var r = reason.Replace("\t", " ").Trim();
        return $"SHARDKEY-UNLOCK\t{t}\t{(r.Length == 0 ? "解锁全部字段（熟手）" : r)}";
    }

    /// <summary>读写分离顺路接入提示：分片方案落地后读流量可接入 ReadRouter 延迟感知路由。</summary>
    public static string ReadSplitHint(int shardCount) =>
        $"分片方案共 {shardCount} 片：读流量建议接入读路由（读写分离）按副本延迟分发，写流量按分片键落到对应分片。";

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
