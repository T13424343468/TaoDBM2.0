namespace TaoDBM.Core;

/// <summary>在线改表一步：阶段（build/alter/backfill/swap/cleanup）、SQL 与执行说明。</summary>
public sealed record OnlineDdlStep(string Phase, string Sql, string Note);

/// <summary>在线改表计划：源表、影子表/旧表名、回填批数与有序步骤清单。</summary>
public sealed record OnlineDdlPlan(string SourceTable, string ShadowTable, string OldTable, long EstimatedRows, int BatchSize, IReadOnlyList<OnlineDdlStep> Steps);

/// <summary>
/// 在线改表引擎（新需求「图形化改表」配套，对标 pt-online-schema-change 的扇出步骤，纯 SQL 计划生成不触库）：
/// 建影子表→施加变更→按主键分段回填→重放追平→单语句原子换名→对账后清理旧表。
/// 换名必须是单语句原子操作；清理旧表前必须先跑 A5-2 数据对账；
/// 回填批数 = ceil(行数/批大小)，行数为零则无回填步。执行层的预览+确认与回滚安排由调用方负责。
/// </summary>
public static class OnlineDdl
{
    /// <summary>
    /// 生成在线改表计划：table/alterSql/pkColumn 非空，estimatedRows ≥ 0，batchSize ≥ 1；
    /// alterSql 为不含 ALTER TABLE 前缀的变更子句。影子表 _osc_{表}_new、旧表 _osc_{表}_old 确定命名。
    /// </summary>
    public static OnlineDdlPlan Plan(string table, string alterSql, string pkColumn, long estimatedRows, int batchSize)
    {
        if (string.IsNullOrWhiteSpace(table)) throw new ArgumentException("表名不能为空", nameof(table));
        if (string.IsNullOrWhiteSpace(alterSql)) throw new ArgumentException("变更子句不能为空", nameof(alterSql));
        if (string.IsNullOrWhiteSpace(pkColumn)) throw new ArgumentException("回填必须携带主键列", nameof(pkColumn));
        if (estimatedRows < 0) throw new ArgumentOutOfRangeException(nameof(estimatedRows), "行数不能为负");
        if (batchSize < 1) throw new ArgumentOutOfRangeException(nameof(batchSize), "批大小必须为正");

        var shadow = $"_osc_{table}_new";
        var old = $"_osc_{table}_old";
        var steps = new List<OnlineDdlStep>
        {
            new("build", $"CREATE TABLE `{shadow}` LIKE `{table}`", "结构与索引原样拷贝，原表继续服务"),
            new("alter", $"ALTER TABLE `{shadow}` {alterSql}", "变更只作用于影子表"),
        };

        long batches = (estimatedRows + batchSize - 1) / batchSize;
        for (long i = 0; i < batches; i++)
            steps.Add(new OnlineDdlStep("backfill",
                $"INSERT INTO `{shadow}` SELECT s.* FROM `{table}` s WHERE s.`{pkColumn}` > @last AND s.`{pkColumn}` <= @last + {batchSize} ORDER BY s.`{pkColumn}` LIMIT {batchSize}",
                $"回填第 {i + 1}/{batches} 批（每批 ≤ {batchSize} 行），@last 为上一批末尾主键值"));

        if (batches > 0)
            steps.Add(new OnlineDdlStep("backfill",
                $"INSERT INTO `{shadow}` SELECT s.* FROM `{table}` s WHERE s.`{pkColumn}` > @last ORDER BY s.`{pkColumn}` LIMIT {batchSize}",
                "回填追平：换名前最后重放一次增量，覆盖回填期间新写入的行"));

        steps.Add(new OnlineDdlStep("swap", $"RENAME TABLE `{table}` TO `{old}`, `{shadow}` TO `{table}`", "单语句原子换名，业务无感"));
        steps.Add(new OnlineDdlStep("cleanup", $"DROP TABLE IF EXISTS `{old}`", "清理前必须先跑 A5-2 数据对账且通过"));
        return new OnlineDdlPlan(table, shadow, old, estimatedRows, batchSize, steps);
    }
}
