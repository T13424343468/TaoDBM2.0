using System.Text;

namespace TaoDBM.Core;

/// <summary>档三：ER 正向生成引擎——由 ER 模型直接生成建库 DDL（MySQL 方言），可校验、可执行。</summary>
/// <remarks>纯引擎免界面稿：只做模型→SQL 的确定性翻译，不连库执行；类型原样透传，主键取 IsKey 列，外键来自 ErdEdge（指向目标表主键列）。</remarks>

/// <summary>正向生成结果：Ok=false 时 Ddl 为空，Errors 给出逐条原因。</summary>
public sealed record ErdForwardResult(bool Ok, List<string> Ddl, List<string> Errors)
{
    public static ErdForwardResult Fail(string error) => new(false, new List<string>(), new List<string> { error });
}

public static class ErdForward
{
    /// <summary>由 ER 模型生成 MySQL 建库脚本：CREATE TABLE（含主键/非空）+ FK 约束；表按依赖排序，被引用表在前。</summary>
    public static ErdForwardResult Generate(ErdModel? model)
    {
        if (model is null) return ErdForwardResult.Fail("模型为空");
        var errors = new List<string>();

        var tables = model.Tables.Where(t => !string.IsNullOrWhiteSpace(t.Name)).ToList();
        foreach (var g in tables.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"表名重复：{g.Key}");
        foreach (var t in model.Tables.Where(t => string.IsNullOrWhiteSpace(t.Name)))
            errors.Add("存在未命名的表");
        if (errors.Count > 0) return new ErdForwardResult(false, new List<string>(), errors);

        var byName = tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        // 校验边：来源列存在、目标表存在且目标列存在
        foreach (var e in model.Edges)
        {
            if (!byName.ContainsKey(e.FromTable)) { errors.Add($"关系来源表不存在：{e.FromTable}"); continue; }
            if (!byName.TryGetValue(e.ToTable, out var target)) { errors.Add($"关系目标表不存在：{e.ToTable}"); continue; }
            var from = byName[e.FromTable];
            if (!from.Columns.Any(c => c.Name.Equals(e.FromCol, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"{e.FromTable}.{e.FromCol} 列不存在，无法建外键");
            else if (!target.Columns.Any(c => c.IsKey))
                errors.Add($"{e.ToTable} 无主键列，无法作为外键引用目标");
        }
        if (errors.Count > 0) return new ErdForwardResult(false, new List<string>(), errors);

        // 无主键的表拒绝生成（MySQL 建表可以有隐式主键，但建模语义上要求明确）
        foreach (var t in tables.Where(t => !t.Columns.Any(c => c.IsKey)))
            errors.Add($"{t.Name} 未设置主键列");
        if (errors.Count > 0) return new ErdForwardResult(false, new List<string>(), errors);

        // 拓扑排序：被 FK 引用的表排前面（同表自引用与环不报错，MySQL 建表时用可延迟约束语义兜底）
        var ordered = TopoOrderByReference(tables, model.Edges);

        var ddl = ordered.Select(t => TableDdl(t, model.Edges)).ToList();
        return new ErdForwardResult(true, ddl, errors);
    }

    private static List<ErdTable> TopoOrderByReference(List<ErdTable> tables, List<ErdEdge> edges)
    {
        var index = tables.Select((t, i) => (t.Name, i)).ToDictionary(p => p.Name, p => p.i, StringComparer.OrdinalIgnoreCase);
        var dependents = tables.Select(_ => new List<int>()).ToArray();  // dependents[i]：依赖表 i 的表
        var inDeg = new int[tables.Count];
        foreach (var e in edges)
        {
            if (!index.TryGetValue(e.ToTable, out var to) || !index.TryGetValue(e.FromTable, out var from)) continue;
            if (to == from) continue; // 自引用不参与排序
            dependents[to].Add(from);
            inDeg[from]++;
        }
        var result = new List<ErdTable>();
        var queue = new Queue<int>(Enumerable.Range(0, tables.Count).Where(i => inDeg[i] == 0));
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            result.Add(tables[i]);
            foreach (var next in dependents[i])
                if (--inDeg[next] == 0) queue.Enqueue(next);
        }
        // 成环时剩余表按原顺序补齐（MySQL FK 允许后建约束，不影响语句合法性）
        foreach (var t in tables)
            if (!result.Any(r => r.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase))) result.Add(t);
        return result;
    }

    private static string TableDdl(ErdTable table, List<ErdEdge> edges)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(Quote(table.Name)).Append(" (\n");
        var lines = new List<string>();
        foreach (var c in table.Columns)
        {
            var type = string.IsNullOrWhiteSpace(c.Type) ? "TEXT" : c.Type.Trim();
            var line = $"  {Quote(c.Name)} {type}{(c.IsKey ? " NOT NULL" : " NULL")}";
            lines.Add(line);
        }
        var keys = table.Columns.Where(c => c.IsKey).Select(c => Quote(c.Name)).ToList();
        if (keys.Count > 0) lines.Add($"  PRIMARY KEY ({string.Join(", ", keys)})");

        foreach (var g in edges
            .Where(e => e.FromTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.FromCol, StringComparer.OrdinalIgnoreCase))
        {
            var fkName = $"fk_{table.Name}_{g.Key}".Replace('`', '_');
            lines.Add($"  CONSTRAINT {Quote(fkName)} FOREIGN KEY ({Quote(g.Key)}) REFERENCES {Quote(g.First().ToTable)} ({Quote("id")})");
        }
        sb.Append(string.Join(",\n", lines)).Append("\n) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
        return sb.ToString();
    }

    private static string Quote(string name) => "`" + name.Replace("`", "``") + "`";
}
