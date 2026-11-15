namespace TaoDBM.Core;

/// <summary>
/// 分库分区分表底座引擎：分区 DDL 方言生成（与 A5-5 滚动计划配套）。
/// 把滚动/归档计划翻译成 MySQL（RANGE TO_DAYS）与 PostgreSQL（声明式分区）的 ADD/DROP 语句；
/// 不支持分区的方言（如 SQLite）返回 null，由调用方降级为普通表操作。
/// </summary>
public static class PartitionDdl
{
    /// <summary>生成新增分区 DDL：MySQL 用 RANGE TO_DAYS 上界；PG 用 FROM/TO 半开区间；不支持分区返回 null。</summary>
    public static string? AddSql(DbEngine engine, string table, TimePartition partition)
    {
        var start = partition.StartInclusive;
        var end = partition.EndExclusive;
        return engine switch
        {
            DbEngine.MySQL => $"ALTER TABLE `{table}` ADD PARTITION (PARTITION {partition.Name} VALUES LESS THAN (TO_DAYS('{end:yyyy-MM-dd}')))",
            DbEngine.PostgreSQL => $"CREATE TABLE {table}_{partition.Name} PARTITION OF {table} FOR VALUES FROM ('{start:yyyy-MM-dd HH:mm:ss}') TO ('{end:yyyy-MM-dd HH:mm:ss}')",
            _ => null,
        };
    }

    /// <summary>生成归档分区 DDL：MySQL DROP PARTITION；PG 直接 DROP 对应子表；不支持分区返回 null。</summary>
    public static string? DropSql(DbEngine engine, string table, string partitionName)
    {
        return engine switch
        {
            DbEngine.MySQL => $"ALTER TABLE `{table}` DROP PARTITION {partitionName}",
            DbEngine.PostgreSQL => $"DROP TABLE IF EXISTS {table}_{partitionName}",
            _ => null,
        };
    }

    /// <summary>把整份滚动计划批量翻译为指定方言的有序 DDL 清单（先归档后新增）；不支持分区返回空表。</summary>
    public static IReadOnlyList<string> PlanSql(DbEngine engine, string table, RollPlan plan)
    {
        var list = new List<string>();
        foreach (var name in plan.ToArchive)
        {
            var sql = DropSql(engine, table, name);
            if (sql != null) list.Add(sql);
        }
        foreach (var p in plan.ToAdd)
        {
            var sql = AddSql(engine, table, p);
            if (sql != null) list.Add(sql);
        }
        return list;
    }
}
