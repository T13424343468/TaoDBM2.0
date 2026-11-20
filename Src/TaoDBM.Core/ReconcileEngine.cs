using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TaoDBM.Core;

/// <summary>对账结论档位：match=两端一致 / count-mismatch=行数不一致 / checksum-mismatch=行数同但内容漂移。</summary>
public sealed record ReconcileVerdict(string Level, string Message);

/// <summary>
/// 主从底座引擎：迁移/同步后的数据对账（新需求「再均衡/搬迁」配套）。
/// 生成两端 COUNT 与校验和 SQL，比较结果给结论与下一步建议；
/// 只生成语句与判定，采集由执行层完成。
/// </summary>
public static class ReconcileEngine
{
    /// <summary>行数对账 SQL。</summary>
    public static string CountSql(string table, string? where, DbEngine engine) =>
        $"SELECT COUNT(*) AS row_count FROM {Id(table, engine)}{Where(where)}";

    /// <summary>
    /// 内容校验和 SQL：MySQL 用 CRC32(CONCAT_WS)；PostgreSQL 用 md5 截段求和；
    /// SQL Server 用 CHECKSUM 多列求和；Oracle 用 STANDARD_HASH(MD5) 截段求和；
    /// SQLite 无内置哈希函数，返回 null（调用方退化为仅行数对账，抽样核对仍可用）。
    /// </summary>
    public static string? ChecksumSql(string table, IReadOnlyList<string> columns, string? where, DbEngine engine)
    {
        if (columns.Count == 0) return null;
        var cols = string.Join(", ", columns.Select(c => Id(c, engine)));
        return engine switch
        {
            DbEngine.MySQL =>
                $"SELECT COUNT(*) AS row_count, COALESCE(SUM(CRC32(CONCAT_WS('|', {cols}))), 0) AS checksum FROM {Id(table, engine)}{Where(where)}",
            DbEngine.PostgreSQL =>
                $"SELECT COUNT(*) AS row_count, COALESCE(SUM(('x' || substr(md5(concat_ws('|', {cols})), 1, 8))::bit(32)::bigint), 0) AS checksum FROM {Id(table, engine)}{Where(where)}",
            DbEngine.SqlServer =>
                $"SELECT COUNT(*) AS row_count, COALESCE(SUM(CHECKSUM({cols})), 0) AS checksum FROM {Id(table, engine)}{Where(where)}",
            DbEngine.Oracle =>
                $"SELECT COUNT(*) AS row_count, COALESCE(SUM(TO_NUMBER(SUBSTR(STANDARD_HASH({OracleConcat(columns, engine)}, 'MD5'), 1, 8), 'XXXXXXXX')), 0) AS checksum FROM {Id(table, engine)}{Where(where)}",
            _ => null,
        };
    }

    private static string OracleConcat(IReadOnlyList<string> columns, DbEngine engine) =>
        string.Join(" || '|' || ", columns.Select(c => $"NVL({Id(c, engine)}, '~')"));

    /// <summary>比较两端采集结果，给出结论与建议。</summary>
    public static ReconcileVerdict Compare(long srcCount, long dstCount, long? srcChecksum, long? dstChecksum)
    {
        if (srcCount != dstCount)
            return new ReconcileVerdict("count-mismatch",
                $"两端行数不一致（源 {srcCount} / 目标 {dstCount}，差 {Math.Abs(srcCount - dstCount)}），请按主键区间补同步后重跑对账。");
        if (srcChecksum.HasValue && dstChecksum.HasValue && srcChecksum.Value != dstChecksum.Value)
            return new ReconcileVerdict("checksum-mismatch",
                "行数一致但内容校验和不同，存在同值不同位或更新漂移，请抽样比对并重放差异。");
        if (srcChecksum.HasValue != dstChecksum.HasValue)
            return new ReconcileVerdict("checksum-mismatch", "一端未采集校验和，无法确认内容一致，请补齐两端校验和后复判。");
        return new ReconcileVerdict("match", "行数与内容校验和一致，对账通过。");
    }

    /// <summary>抽样核对 SQL：按主键排序取样，供 checksum-mismatch 时人工定位。
    /// MySQL / PG / SQLite 用 LIMIT；SQL Server 用 TOP；Oracle 用 FETCH FIRST。</summary>
    public static string SampleSql(string table, string pkColumn, int limit, DbEngine engine)
    {
        var n = limit.ToString(CultureInfo.InvariantCulture);
        return engine switch
        {
            DbEngine.SqlServer => $"SELECT TOP {n} * FROM {Id(table, engine)} ORDER BY {Id(pkColumn, engine)}",
            DbEngine.Oracle => $"SELECT * FROM {Id(table, engine)} ORDER BY {Id(pkColumn, engine)} FETCH FIRST {n} ROWS ONLY",
            _ => $"SELECT * FROM {Id(table, engine)} ORDER BY {Id(pkColumn, engine)} LIMIT {n}",
        };
    }

    private static string Where(string? where) =>
        string.IsNullOrWhiteSpace(where) ? "" : $" WHERE {Regex.Replace(where, ";", "", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim()}";

    private static string Id(string identifier, DbEngine engine)
    {
        if (engine == DbEngine.SqlServer)
            return "[" + identifier.Replace("[", "").Replace("]", "") + "]";
        var q = engine == DbEngine.MySQL ? '`' : '"';
        return q + identifier.Replace(q.ToString(), q.ToString() + q.ToString(), StringComparison.Ordinal) + q;
    }
}
