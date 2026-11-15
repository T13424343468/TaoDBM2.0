using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TaoDBM.Core;

/// <summary>大表分批清理计划：逐批 DELETE 语句与批次数（第十章「大表分批清理」）。</summary>
public sealed record BatchCleanupPlan
{
    public string Table { get; init; } = "";
    /// <summary>清理条件（可为空=整表清空，计划会带警示标记）。</summary>
    public string? Where { get; init; }
    /// <summary>主键列（非 MySQL 方言按主键子查询分批时必填）。</summary>
    public string PkColumn { get; init; } = "";
    public int BatchSize { get; init; }
    public long EstimatedRows { get; init; }
    /// <summary>预估批次数（按 EstimatedRows 上取整；估计偏大时靠执行期提前收敛）。</summary>
    public int TotalBatches { get; init; }
    /// <summary>无 WHERE 的整表清空计划（供确认页加强提示）。</summary>
    public bool WholeTable { get; init; }
    public IReadOnlyList<string> BatchSqls { get; init; } = Array.Empty<string>();
}

/// <summary>分批清理执行结果。</summary>
public sealed record CleanupRunResult
{
    public long RowsDeleted { get; init; }
    public int BatchesDone { get; init; }
    /// <summary>是否按计划跑满（false=某批删除 0 行提前收敛）。</summary>
    public bool Exhausted { get; init; }
}

/// <summary>
/// 安全线引擎：大表分批清理的纯逻辑部分（第十章）。
/// 把一次性大 DELETE 拆成有界小批，控制锁占用与 undo 膨胀；
/// 只生成语句与推进批序，execute 回调由执行入口提供（受确认弹窗与任务中心管控）。
/// </summary>
public static class BatchCleanup
{
    /// <summary>生成分批清理计划；estimatedRows/batchSize 非正返回空计划。</summary>
    public static BatchCleanupPlan Plan(
        string table, string? where, string pkColumn, long estimatedRows, int batchSize, DbEngine engine)
    {
        if (estimatedRows <= 0 || batchSize <= 0)
            return new BatchCleanupPlan { Table = table, Where = where, PkColumn = pkColumn, BatchSize = Math.Max(batchSize, 0), EstimatedRows = Math.Max(estimatedRows, 0) };

        var total = (int)Math.Ceiling(estimatedRows / (double)batchSize);
        var whole = string.IsNullOrWhiteSpace(where);
        var sqls = new List<string>(total);
        for (int i = 0; i < total; i++)
            sqls.Add(BatchSql(table, where, pkColumn, batchSize, engine));
        return new BatchCleanupPlan
        {
            Table = table,
            Where = where,
            PkColumn = pkColumn,
            BatchSize = batchSize,
            EstimatedRows = estimatedRows,
            TotalBatches = total,
            WholeTable = whole,
            BatchSqls = sqls,
        };
    }

    /// <summary>
    /// 逐批执行：execute 返回实际删除行数；返回 0 视为无可清数据，提前收敛。
    /// </summary>
    public static CleanupRunResult Run(BatchCleanupPlan plan, Func<string, long> execute, int? maxBatches = null)
    {
        long rows = 0;
        int done = 0;
        var limit = maxBatches ?? plan.BatchSqls.Count;
        foreach (var sql in plan.BatchSqls)
        {
            if (done >= limit) break;
            var affected = execute(sql);
            done++;
            rows += affected;
            if (affected == 0)
                return new CleanupRunResult { RowsDeleted = rows, BatchesDone = done, Exhausted = false };
        }
        return new CleanupRunResult { RowsDeleted = rows, BatchesDone = done, Exhausted = done >= plan.BatchSqls.Count };
    }

    private static string BatchSql(string table, string? where, string pkColumn, int batchSize, DbEngine engine)
    {
        var t = Id(table, engine);
        var w = string.IsNullOrWhiteSpace(where) ? "" : $" WHERE {where}";
        if (engine == DbEngine.MySQL)
            return $"DELETE FROM {t}{w} LIMIT {batchSize.ToString(CultureInfo.InvariantCulture)}";
        var pk = Id(string.IsNullOrWhiteSpace(pkColumn) ? "id" : pkColumn.Trim(), engine);
        var sub = $"SELECT {pk} FROM {t}{w} ORDER BY {pk} LIMIT {batchSize.ToString(CultureInfo.InvariantCulture)}";
        return $"DELETE FROM {t} WHERE {pk} IN ({sub})";
    }

    private static string Id(string identifier, DbEngine engine)
    {
        var q = engine == DbEngine.MySQL ? '`' : '"';
        return q + identifier.Replace(q.ToString(), q.ToString() + q.ToString(), StringComparison.Ordinal) + q;
    }
}
