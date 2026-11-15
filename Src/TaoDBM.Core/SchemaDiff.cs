using System.Text;
namespace TaoDBM.Core;

/// <summary>
/// A3 结构差异对比选项：控制差异预览的比较口径（第五章「差异预览 + 双向同步」纯引擎）。
/// </summary>
public sealed class SchemaDiffOptions
{
    /// <summary>比较列类型（默认开：类型不一致是同步的主要对象）。</summary>
    public bool CompareType { get; init; } = true;
    /// <summary>比较可空性（默认开）。</summary>
    public bool CompareNullable { get; init; } = true;
    /// <summary>比较默认值（默认关：默认值多为装饰性，避免预览噪音）。</summary>
    public bool CompareDefault { get; init; }
    /// <summary>比较主键标记（默认开）。</summary>
    public bool ComparePrimaryKey { get; init; } = true;
    /// <summary>报告目标侧多余列（仅提示，不进同步计划：默认不删列）。</summary>
    public bool ReportExtraColumns { get; init; } = true;
    /// <summary>报告目标侧多余的表（仅提示）。</summary>
    public bool ReportExtraTables { get; init; } = true;
}

/// <summary>一条结构差异：预览列表的一行，也是同步计划的原料。</summary>
public sealed record SchemaDiffItem
{
    public string Table { get; init; } = "";
    /// <summary>table-missing / column-missing / column-extra / table-extra /
    /// type-mismatch / nullable-mismatch / default-mismatch / pk-mismatch。</summary>
    public string Kind { get; init; } = "";
    public string Column { get; init; } = "";
    /// <summary>人类可读描述（差异预览直接展示）。</summary>
    public string Detail { get; init; } = "";
    /// <summary>源侧取值（信息性，预览对照用）。</summary>
    public string Source { get; init; } = "";
    /// <summary>目标侧取值（信息性，预览对照用）。</summary>
    public string Target { get; init; } = "";
    /// <summary>能否自动生成同步语句（多余表/多余列仅为提示，不删目标侧对象）。</summary>
    public bool Actionable { get; init; }
}

/// <summary>一张表的结构快照：表名 + 列定义（差异对比与同步计划的输入）。</summary>
public sealed class SchemaTableSnapshot
{
    public string Table { get; init; } = "";
    public List<TableColumnDesign> Columns { get; init; } = new();

    /// <summary>从列规格（对象树/设计器通用结构）构造快照。</summary>
    public static SchemaTableSnapshot FromSpecs(string table, IEnumerable<ColumnSpec> specs) => new()
    {
        Table = table,
        Columns = specs.Select(s => new TableColumnDesign
        {
            Name = s.Name,
            SqlType = s.DataType,
            Nullable = s.Nullable,
            IsPrimaryKey = s.IsKey,
        }).ToList(),
    };
}

/// <summary>
/// 结构差异对比（纯逻辑，不碰连接）：源/目标两份表快照 → 差异条目列表；
/// 再由 PlanSync 按目标方言把可执行差异落成 CREATE/ALTER 语句计划。
/// </summary>
public static class SchemaDiff
{
    /// <summary>对比源与目标结构，返回差异条目（按表名、类型稳定排序）。</summary>
    public static List<SchemaDiffItem> Diff(
        IReadOnlyList<SchemaTableSnapshot> source, IReadOnlyList<SchemaTableSnapshot> target,
        SchemaDiffOptions? options = null)
    {
        options ??= new SchemaDiffOptions();
        var items = new List<SchemaDiffItem>();
        var src = ByName(source);
        var dst = ByName(target);

        foreach (var (name, s) in src)
        {
            if (!dst.TryGetValue(name, out var t))
            {
                items.Add(new SchemaDiffItem
                {
                    Table = s.Table, Kind = "table-missing", Actionable = true,
                    Detail = "目标库缺表，可按源结构建表",
                });
                continue;
            }
            DiffTable(s, t, options, items);
        }
        if (options.ReportExtraTables)
            foreach (var (name, t) in dst)
                if (!src.ContainsKey(name))
                    items.Add(new SchemaDiffItem
                    {
                        Table = t.Table, Kind = "table-extra", Actionable = false,
                        Detail = "目标侧多出的表（同步不处理，删除需人工确认）",
                    });

        return items
            .OrderBy(d => d.Table, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Kind, StringComparer.Ordinal)
            .ThenBy(d => d.Column, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>把差异条目落成目标方言的同步语句计划（可执行差异 → CREATE/ALTER；多余对象不删）。
    /// dropMissing=true 时目标多余列进 DROP（双向同步的删侧，默认关）。</summary>
    public static (List<string> Statements, List<string> Skipped) PlanSync(
        IReadOnlyList<SchemaTableSnapshot> source, IReadOnlyList<SchemaTableSnapshot> target,
        IReadOnlyList<SchemaDiffItem> diffs, DbEngine engine, Func<string, string> quote,
        bool dropMissing = false)
    {
        var stmts = new List<string>();
        var skipped = new List<string>();
        var src = ByName(source);
        var dst = ByName(target);

        foreach (var tableGroup in diffs.GroupBy(d => d.Table, StringComparer.OrdinalIgnoreCase))
        {
            if (!src.TryGetValue(tableGroup.Key, out var s)) continue;   // 多余表/孤儿差异：无源结构不落计划
            bool needCreate = tableGroup.Any(d => d.Kind == "table-missing");
            if (needCreate)
            {
                stmts.Add(TableDdl.BuildCreate(ToDesign(s), engine, quote));
                continue;
            }
            if (!dst.TryGetValue(tableGroup.Key, out var t)) continue;

            var kindSet = tableGroup.Select(d => d.Kind).ToHashSet();
            bool modify = kindSet.Contains("type-mismatch") || kindSet.Contains("nullable-mismatch")
                || kindSet.Contains("default-mismatch") || kindSet.Contains("pk-mismatch");
            if (!modify && !tableGroup.Any(d => d.Kind == "column-missing") && !dropMissing) continue;

            // 克隆源列定义再进 BuildAlter：BuildAlter 会改写 AutoIncrement，不能污染共享快照
            var design = ToDesign(s);
            var (ss, sk) = TableDdl.BuildAlter(design,
                t.Columns.Select(c => c.Name), engine, quote,
                modifyExisting: modify, dropMissing: dropMissing);
            stmts.AddRange(ss);
            skipped.AddRange(sk);
        }
        return (stmts, skipped);
    }

    private static void DiffTable(SchemaTableSnapshot s, SchemaTableSnapshot t,
        SchemaDiffOptions o, List<SchemaDiffItem> items)
    {
        var srcCols = ByCol(s.Columns);
        var dstCols = ByCol(t.Columns);

        foreach (var (cname, sc) in srcCols)
        {
            if (!dstCols.TryGetValue(cname, out var tc))
            {
                items.Add(new SchemaDiffItem
                {
                    Table = s.Table, Kind = "column-missing", Column = sc.Name, Actionable = true,
                    Source = TypeOr(sc), Detail = "目标表缺列，可补列",
                });
                continue;
            }
            if (o.CompareType && !NormType(sc.SqlType).Equals(NormType(tc.SqlType), StringComparison.OrdinalIgnoreCase))
                items.Add(new SchemaDiffItem
                {
                    Table = s.Table, Kind = "type-mismatch", Column = sc.Name, Actionable = true,
                    Source = TypeOr(sc), Target = TypeOr(tc), Detail = "列类型不一致",
                });
            if (o.CompareNullable && sc.Nullable != tc.Nullable)
                items.Add(new SchemaDiffItem
                {
                    Table = s.Table, Kind = "nullable-mismatch", Column = sc.Name, Actionable = true,
                    Source = sc.Nullable ? "NULL" : "NOT NULL", Target = tc.Nullable ? "NULL" : "NOT NULL",
                    Detail = "可空性不一致",
                });
            if (o.CompareDefault && !NormDefault(sc.DefaultValue).Equals(NormDefault(tc.DefaultValue), StringComparison.OrdinalIgnoreCase))
                items.Add(new SchemaDiffItem
                {
                    Table = s.Table, Kind = "default-mismatch", Column = sc.Name, Actionable = true,
                    Source = sc.DefaultValue, Target = tc.DefaultValue, Detail = "默认值不一致",
                });
            if (o.ComparePrimaryKey && sc.IsPrimaryKey != tc.IsPrimaryKey)
                items.Add(new SchemaDiffItem
                {
                    Table = s.Table, Kind = "pk-mismatch", Column = sc.Name, Actionable = true,
                    Source = sc.IsPrimaryKey ? "主键" : "非主键", Target = tc.IsPrimaryKey ? "主键" : "非主键",
                    Detail = "主键标记不一致",
                });
        }
        if (o.ReportExtraColumns)
            foreach (var (cname, tc) in dstCols)
                if (!srcCols.ContainsKey(cname))
                    items.Add(new SchemaDiffItem
                    {
                        Table = t.Table, Kind = "column-extra", Column = tc.Name, Actionable = false,
                        Target = TypeOr(tc), Detail = "目标侧多余列（同步不删，删除需人工确认）",
                    });
    }

    private static Dictionary<string, SchemaTableSnapshot> ByName(IReadOnlyList<SchemaTableSnapshot> list) =>
        list.Where(x => x.Table.Length > 0)
            .GroupBy(x => x.Table, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, TableColumnDesign> ByCol(IEnumerable<TableColumnDesign> cols) =>
        cols.Where(c => c.Name.Length > 0)
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>快照 → 表设计（深拷贝列定义，供 BuildCreate/BuildAlter 使用）。</summary>
    private static TableDesign ToDesign(SchemaTableSnapshot s) => new()
    {
        TableName = s.Table,
        Columns = s.Columns.Select(c => new TableColumnDesign
        {
            Name = c.Name, SqlType = c.SqlType, Nullable = c.Nullable,
            IsPrimaryKey = c.IsPrimaryKey, AutoIncrement = c.AutoIncrement, DefaultValue = c.DefaultValue,
        }).ToList(),
    };

    private static SchemaTableSnapshot Clone(SchemaTableSnapshot s) => new()
    {
        Table = s.Table,
        Columns = s.Columns.Select(c => new TableColumnDesign
        {
            Name = c.Name, SqlType = c.SqlType, Nullable = c.Nullable,
            IsPrimaryKey = c.IsPrimaryKey, AutoIncrement = c.AutoIncrement, DefaultValue = c.DefaultValue,
        }).ToList(),
    };

    private static string TypeOr(TableColumnDesign c) =>
        string.IsNullOrWhiteSpace(c.SqlType) ? "(未指定类型)" : c.SqlType.Trim();

    private static string NormType(string t) => (t ?? "").Trim().TrimEnd(';');

    private static string NormDefault(string v) => (v ?? "").Trim();
}
