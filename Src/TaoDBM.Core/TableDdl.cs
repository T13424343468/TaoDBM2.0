namespace TaoDBM.Core;

/// <summary>表结构设计器里的一列定义。</summary>
public class TableColumnDesign
{
    public string Name { get; set; } = "";
    /// <summary>原始 SQL 类型，如 INT、VARCHAR(50)、TIMESTAMP。</summary>
    public string SqlType { get; set; } = "";
    public bool Nullable { get; set; } = true;
    public bool IsPrimaryKey { get; set; }
    public bool AutoIncrement { get; set; }
    /// <summary>默认值表达式，空表示不设置。</summary>
    public string DefaultValue { get; set; } = "";
    /// <summary>列注释：MySQL 内联进列定义（MODIFY 显式带回不丢）；PG/Oracle 走独立 COMMENT ON 语句；SQLite 不支持。</summary>
    public string Comment { get; set; } = "";

    public bool SameNameAs(string? other) => Name.Equals(other, StringComparison.OrdinalIgnoreCase);
}

/// <summary>表设计器里的一条索引定义（U2：设计器索引分区）。</summary>
public class TableIndexDesign
{
    public string Name { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    public bool IsUnique { get; set; }
}

/// <summary>表设计器里的一条外键定义（U2：设计器外键分区）。</summary>
public class TableForeignKeyDesign
{
    public string Name { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    public string RefTable { get; set; } = "";
    public List<string> RefColumns { get; set; } = new();
    /// <summary>CASCADE / SET NULL / RESTRICT，空表示不指定。</summary>
    public string OnDelete { get; set; } = "";
    public string OnUpdate { get; set; } = "";
}

/// <summary>一张表的列定义集合（新建或改表）。</summary>
public class TableDesign
{
    public string TableName { get; set; } = "";
    /// <summary>表注释（仅 MySQL 落 DDL；空表示不设置 / 改表时未变更）。</summary>
    public string Comment { get; set; } = "";
    /// <summary>字符集（仅 MySQL 落 DDL；空表示不设置 / 改表时未变更）。</summary>
    public string Charset { get; set; } = "";
    public List<TableColumnDesign> Columns { get; set; } = new();
    public List<TableIndexDesign> Indexes { get; set; } = new();
    public List<TableForeignKeyDesign> ForeignKeys { get; set; } = new();

    public static TableDesign FromSpecs(string table, IEnumerable<ColumnSpec> specs) => new()
    {
        TableName = table,
        Columns = specs.Select(s => new TableColumnDesign
        {
            Name = s.Name,
            SqlType = s.DataType,
            Nullable = s.Nullable,
            IsPrimaryKey = s.IsKey,
        }).ToList(),
    };
}

/// <summary>按引擎方言生成建表 / 改表 DDL（纯逻辑，可测试）。</summary>
public static class TableDdl
{
    public static string DefaultType(DbEngine e) => e switch
    {
        DbEngine.Oracle => "VARCHAR2(100)",
        _ => "VARCHAR(100)",
    };

    /// <summary>台账④：按引擎给出常用类型清单（设计器类型下拉口径）；国产六家按协议兼容引擎取清单。</summary>
    public static List<string> TypeChoices(DbEngine e) => e switch
    {
        DbEngine.MySQL or DbEngine.OceanBase or DbEngine.PolarDB or DbEngine.Tdsql =>
            new() { "INT", "BIGINT", "DECIMAL(10,2)", "FLOAT", "DOUBLE", "VARCHAR(255)", "CHAR(36)", "TEXT", "LONGTEXT", "DATE", "DATETIME", "TIMESTAMP", "TIME", "JSON", "BLOB", "BOOLEAN" },
        DbEngine.PostgreSQL or DbEngine.GaussDB or DbEngine.Kingbase =>
            new() { "INTEGER", "BIGINT", "NUMERIC(10,2)", "REAL", "DOUBLE PRECISION", "VARCHAR(255)", "TEXT", "BOOLEAN", "DATE", "TIMESTAMP", "TIMESTAMPTZ", "TIME", "UUID", "JSON", "JSONB", "BYTEA", "SERIAL", "BIGSERIAL" },
        DbEngine.SqlServer =>
            new() { "INT", "BIGINT", "DECIMAL(10,2)", "FLOAT", "BIT", "NVARCHAR(255)", "NVARCHAR(MAX)", "DATE", "DATETIME2", "DATETIME", "TIME", "UNIQUEIDENTIFIER", "VARBINARY(MAX)" },
        DbEngine.Oracle or DbEngine.Dameng =>
            new() { "NUMBER(10)", "NUMBER(18,2)", "NUMBER", "VARCHAR2(255)", "VARCHAR2(4000)", "CLOB", "NCLOB", "DATE", "TIMESTAMP", "TIMESTAMP WITH TIME ZONE", "BLOB", "RAW(2000)" },
        DbEngine.SQLite =>
            new() { "INTEGER", "REAL", "TEXT", "BLOB", "NUMERIC", "BOOLEAN", "DATE", "DATETIME" },
        _ => new() { "VARCHAR(100)", "INTEGER", "TEXT" },
    };

    private static readonly System.Collections.Generic.HashSet<string> NumericTypeNames = new(StringComparer.OrdinalIgnoreCase)
    { "INT", "INTEGER", "BIGINT", "SMALLINT", "TINYINT", "MEDIUMINT", "DECIMAL", "NUMERIC", "FLOAT", "DOUBLE", "REAL", "NUMBER", "BIT", "SERIAL", "BIGSERIAL" };

    private static readonly System.Collections.Generic.HashSet<string> TemporalTypeNames = new(StringComparer.OrdinalIgnoreCase)
    { "DATE", "TIME", "DATETIME", "DATETIME2", "SMALLDATETIME", "TIMESTAMP", "TIMESTAMPTZ" };

    /// <summary>台账⑤：类型是否数值类（含括号精度截断后判定，如 DECIMAL(10,2)）。</summary>
    public static bool IsNumericType(string sqlType)
    {
        var t = sqlType.Trim();
        int i = t.IndexOf('(');
        if (i > 0) t = t[..i];
        return NumericTypeNames.Contains(t.Trim());
    }

    /// <summary>台账⑤：类型是否时间日期类（默认值提示 CURRENT_TIMESTAMP 口径）。</summary>
    public static bool IsTemporalType(string sqlType)
    {
        var t = sqlType.Trim();
        int i = t.IndexOf('(');
        if (i > 0) t = t[..i];
        return TemporalTypeNames.Contains(t.Trim());
    }

    /// <summary>生成 CREATE TABLE 语句；autoIncrement 列按方言落到对应位置，外键以表级子句内联（SQLite 建表也支持）。</summary>
    public static string BuildCreate(TableDesign t, DbEngine engine, Func<string, string> quote)
    {
        var pks = t.Columns.Where(c => c.IsPrimaryKey).Select(c => quote(c.Name)).ToList();
        bool tableLevelPk = pks.Count > 1;
        var lines = new List<string>();
        foreach (var c in t.Columns)
            lines.Add("  " + ColumnDef(c, engine, quote, tableLevelPk));
        if (tableLevelPk)
            lines.Add("  PRIMARY KEY (" + string.Join(", ", pks) + ")");
        foreach (var fk in t.ForeignKeys.Where(FkValid))
            lines.Add("  " + FkClause(fk, quote));
        // 表级属性（仅 MySQL）：字符集 / 表注释，空不落
        var tail = "";
        if (engine == DbEngine.MySQL)
        {
            if (t.Charset.Trim().Length > 0) tail += $" DEFAULT CHARSET={t.Charset.Trim()}";
            if (t.Comment.Trim().Length > 0) tail += $" COMMENT='{Esc(t.Comment.Trim())}'";
        }
        return $"CREATE TABLE {quote(t.TableName)} (\n{string.Join(",\n", lines)}\n){tail}";
    }

    /// <summary>生成索引 DDL（CREATE INDEX，全引擎通用；空名/空列跳过）。</summary>
    public static List<string> BuildIndexes(TableDesign t, Func<string, string> quote)
    {
        var stmts = new List<string>();
        var table = quote(t.TableName);
        foreach (var ix in t.Indexes)
        {
            var cols = ix.Columns.Where(c => c.Trim().Length > 0).Select(quote).ToList();
            if (ix.Name.Trim().Length == 0 || cols.Count == 0) continue;
            stmts.Add($"CREATE {(ix.IsUnique ? "UNIQUE " : "")}INDEX {quote(ix.Name.Trim())} ON {table} ({string.Join(", ", cols)})");
        }
        return stmts;
    }

    /// <summary>生成对已有表加外键的 DDL；SQLite 不支持 ADD CONSTRAINT 外键，记入 skipped。</summary>
    public static (List<string> Statements, List<string> Skipped) BuildForeignKeys(TableDesign t, DbEngine engine, Func<string, string> quote)
    {
        var stmts = new List<string>();
        var skipped = new List<string>();
        if (engine == DbEngine.SQLite)
        {
            if (t.ForeignKeys.Any(FkValid))
                skipped.Add("SQLite: 不支持对已有表 ADD CONSTRAINT 外键（官方限制），外键需在新建表时定义。");
            return (stmts, skipped);
        }
        var table = quote(t.TableName);
        foreach (var fk in t.ForeignKeys.Where(FkValid))
            stmts.Add($"ALTER TABLE {table} ADD CONSTRAINT {quote(fk.Name.Trim())} {FkClause(fk, quote)}");
        return (stmts, skipped);
    }

    private static bool FkValid(TableForeignKeyDesign fk) =>
        fk.Name.Trim().Length > 0 && fk.RefTable.Trim().Length > 0
        && fk.Columns.Any(c => c.Trim().Length > 0) && fk.RefColumns.Any(c => c.Trim().Length > 0);

    private static string FkClause(TableForeignKeyDesign fk, Func<string, string> quote)
    {
        var cols = string.Join(", ", fk.Columns.Where(c => c.Trim().Length > 0).Select(quote));
        var refs = string.Join(", ", fk.RefColumns.Where(c => c.Trim().Length > 0).Select(quote));
        var acts = "";
        if (fk.OnDelete.Trim().Length > 0) acts += $" ON DELETE {fk.OnDelete.Trim()}";
        if (fk.OnUpdate.Trim().Length > 0) acts += $" ON UPDATE {fk.OnUpdate.Trim()}";
        return $"FOREIGN KEY ({cols}) REFERENCES {quote(fk.RefTable.Trim())} ({refs}){acts}";
    }

    /// <summary>生成 ALTER 语句：新增列 / 修改列 / 删除列（与 existingNames 对比）。
    /// SQLite 不支持修改列类型，记入 skipped。modifyExisting=false 只加列不改列；
    /// dropMissing=false 不删除目标侧多余列（结构同步默认不开删）。</summary>
    public static (List<string> Statements, List<string> Skipped) BuildAlter(
        TableDesign t, IEnumerable<string> existingNames, DbEngine engine, Func<string, string> quote,
        bool modifyExisting = true, bool dropMissing = true)
    {
        var stmts = new List<string>();
        var skipped = new List<string>();
        var table = quote(t.TableName);
        var existing = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keep = t.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 新增列
        foreach (var c in t.Columns.Where(c => c.Name.Length > 0 && !existing.Contains(c.Name)))
        {
            if (c.AutoIncrement && engine is DbEngine.SqlServer or DbEngine.Oracle)
            {
                skipped.Add($"{engine}: 无法对已有表新增 IDENTITY/自增列（{c.Name}），请手动处理。");
                c.AutoIncrement = false;
            }
            var def = ColumnDef(c, engine, quote, tableLevelPk: false);
            stmts.Add(engine == DbEngine.Oracle
                ? $"ALTER TABLE {table} ADD ({def})"
                : $"ALTER TABLE {table} ADD {def}");
        }

        // 修改已有列（发现74：自增属性按读回原样带回，不再静默丢；发现75：默认值/注释带回）
        if (modifyExisting)
        foreach (var c in t.Columns.Where(c => c.Name.Length > 0 && existing.Contains(c.Name)))
        {
            if (engine == DbEngine.SQLite)
            {
                skipped.Add("SQLite: 不支持修改列类型/属性，仅支持加列（SQLite 官方限制）。");
                continue;
            }
            // 发现74：SQL Server 不支持 ALTER COLUMN 修改 IDENTITY 列，明示跳过防静默失败
            if (engine == DbEngine.SqlServer && c.AutoIncrement)
            {
                skipped.Add($"SQL Server: 自增列（IDENTITY）不支持修改（{c.Name}），已跳过。");
                continue;
            }
            string type = TypeOr(c, engine);
            string nullable = !c.Nullable ? " NOT NULL" : "";
            stmts.Add(engine switch
            {
                // 发现74：withIdentity 带回自增；withPk=false 不重复声明主键（MODIFY 再声明 PRIMARY KEY 报 1068）；rawDefault 库内表达式原样带回
                DbEngine.MySQL => $"ALTER TABLE {table} MODIFY COLUMN {ColumnDef(c, engine, quote, tableLevelPk: false, withIdentity: true, withPk: false, rawDefault: true)}",
                DbEngine.SqlServer => $"ALTER TABLE {table} ALTER COLUMN {quote(c.Name)} {type}{nullable}",
                // 发现75：Oracle MODIFY 子句内联带回默认值（库内读回的原样表达式直接用）
                DbEngine.Oracle => $"ALTER TABLE {table} MODIFY ({quote(c.Name)} {type}{nullable}{DefaultTail(c.DefaultValue)})",
                _ => "", // PostgreSQL 在下方单独拼（可能拆多条）
            });
            if (engine == DbEngine.PostgreSQL)
            {
                stmts.Add($"ALTER TABLE {table} ALTER COLUMN {quote(c.Name)} TYPE {type}");
                stmts.Add(c.Nullable
                    ? $"ALTER TABLE {table} ALTER COLUMN {quote(c.Name)} DROP NOT NULL"
                    : $"ALTER TABLE {table} ALTER COLUMN {quote(c.Name)} SET NOT NULL");
                // 发现75：PG 改列不落 DEFAULT 子句会静默丢默认值（含 serial 的 nextval 序列），原样带回
                if (c.DefaultValue.Trim().Length > 0)
                    stmts.Add($"ALTER TABLE {table} ALTER COLUMN {quote(c.Name)} SET DEFAULT {DefaultForModify(c.DefaultValue)}");
            }
            // 发现75：SQL Server ALTER COLUMN 不支持 DEFAULT，需单独 ADD DEFAULT（列已有默认时新约束后建先生效，值一致无副作用）
            if (engine == DbEngine.SqlServer && c.DefaultValue.Trim().Length > 0)
                stmts.Add($"ALTER TABLE {table} ADD DEFAULT {DefaultForModify(c.DefaultValue)} FOR {quote(c.Name)}");
        }

        // 删除列
        if (dropMissing)
            foreach (var name in existingNames.Where(n => n.Length > 0 && !keep.Contains(n)))
                stmts.Add($"ALTER TABLE {table} DROP COLUMN {quote(name)}");

        // 表级属性变更（仅 MySQL）：Comment / Charset 非空才落（调用方只在有变更时填）
        if (engine == DbEngine.MySQL)
        {
            if (t.Comment.Trim().Length > 0) stmts.Add($"ALTER TABLE {table} COMMENT='{Esc(t.Comment.Trim())}'");
            if (t.Charset.Trim().Length > 0) stmts.Add($"ALTER TABLE {table} CHARACTER SET = {t.Charset.Trim()}");
        }

        return (stmts.Where(s => s.Length > 0).ToList(), skipped);
    }

    /// <summary>列注释独立语句（PG / Oracle 用 COMMENT ON COLUMN；MySQL 内联在列定义里无需；SQLite 不支持返回空）。
    /// onlyNames 非空时只为这些列出语句（改表场景：新增＋被修改的列）。</summary>
    public static List<string> BuildColumnComments(TableDesign t, DbEngine engine, Func<string, string> quote, IEnumerable<string>? onlyNames = null)
    {
        var stmts = new List<string>();
        if (engine is not (DbEngine.PostgreSQL or DbEngine.Oracle)) return stmts;
        var only = onlyNames?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var table = quote(t.TableName);
        foreach (var c in t.Columns)
        {
            if (c.Name.Trim().Length == 0 || c.Comment.Trim().Length == 0) continue;
            if (only != null && !only.Contains(c.Name.Trim())) continue;
            stmts.Add($"COMMENT ON COLUMN {table}.{quote(c.Name.Trim())} IS '{Esc(c.Comment.Trim())}'");
        }
        return stmts;
    }

    /// <summary>DDL 字符串字面量转义（注释 / 默认值共用）。</summary>
    private static string Esc(string s) => s.Replace("'", "''");

    private static string TypeOr(TableColumnDesign c, DbEngine e) =>
        string.IsNullOrWhiteSpace(c.SqlType) ? DefaultType(e) : c.SqlType.Trim();

    /// <summary>单列定义片段。withIdentity=false 时忽略自增（改表场景）；withPk=false 不内联 PRIMARY KEY（MODIFY 再声明主键会报错）；
    /// rawDefault=true 时默认值按改表口径原样带回（发现75）。</summary>
    private static string ColumnDef(TableColumnDesign c, DbEngine e, Func<string, string> quote, bool tableLevelPk, bool withIdentity = true, bool withPk = true, bool rawDefault = false)
    {
        string name = quote(c.Name);
        string type = TypeOr(c, e);
        bool inc = withIdentity && c.AutoIncrement;
        var parts = new List<string>();

        if (inc && e == DbEngine.SQLite)
        {
            // SQLite 自增只支持 INTEGER PRIMARY KEY AUTOINCREMENT
            return $"{name} INTEGER PRIMARY KEY AUTOINCREMENT";
        }
        parts.Add(name);
        parts.Add(type);
        if (inc)
        {
            switch (e)
            {
                case DbEngine.MySQL: parts.Add("AUTO_INCREMENT"); break;
                case DbEngine.PostgreSQL: parts.Add("GENERATED BY DEFAULT AS IDENTITY"); break;
                case DbEngine.SqlServer: parts.Add("IDENTITY(1,1)"); break;
                case DbEngine.Oracle: parts.Add("GENERATED ALWAYS AS IDENTITY"); break;
            }
            // 自增列隐式非空；单列主键仍需内联 PRIMARY KEY
            if (c.IsPrimaryKey && !tableLevelPk && withPk) parts.Add("PRIMARY KEY");
        }
        else
        {
            if (c.IsPrimaryKey && !tableLevelPk && withPk) parts.Add("PRIMARY KEY");
            else if (!c.Nullable) parts.Add("NOT NULL");
        }
        if (c.DefaultValue.Trim().Length > 0) parts.Add("DEFAULT " + (rawDefault ? DefaultForModify(c.DefaultValue) : FormatDefault(c.DefaultValue.Trim())));
        // 列注释内联（仅 MySQL）：MODIFY COLUMN 显式带回原注释，改表不丢属性
        if (e == DbEngine.MySQL && c.Comment.Trim().Length > 0) parts.Add($"COMMENT '{Esc(c.Comment.Trim())}'");
        return string.Join(" ", parts);
    }

    /// <summary>改表场景默认值片段（发现75）：库内读回的原样表达式（带引号/括号/类型转换）直接带回，
    /// 用户手填的裸值沿用 FormatDefault 口径加引号。</summary>
    private static string DefaultForModify(string v)
    {
        var t = v.Trim();
        if (t.StartsWith("'") && t.EndsWith("'") && t.Length >= 2) return t;
        if (t.Contains('(') || t.Contains("::")) return t;
        return FormatDefault(t);
    }

    /// <summary>Oracle MODIFY 子句内的默认值尾段（发现75）：空返回空串，否则 " DEFAULT 表达式"。</summary>
    private static string DefaultTail(string v) =>
        v.Trim().Length == 0 ? "" : " DEFAULT " + DefaultForModify(v);

    /// <summary>建表语句包装为语句列表（统一执行入口用）。</summary>
    public static List<string> AsStatements(string sql) => new() { sql.TrimEnd(';') };

    /// <summary>生成删除索引语句；不支持的引擎返回空串（改表模式勾选删除时用）。</summary>
    public static string BuildDropIndex(DbEngine engine, string table, string indexName, Func<string, string> quote) => engine switch
    {
        DbEngine.MySQL => $"DROP INDEX {quote(indexName)} ON {quote(table)}",
        DbEngine.SqlServer => $"DROP INDEX {quote(indexName)} ON {quote(table)}",
        DbEngine.SQLite => $"DROP INDEX {quote(indexName)}",
        DbEngine.PostgreSQL => $"DROP INDEX {quote(indexName)}",
        DbEngine.Oracle => $"DROP INDEX {quote(indexName)}",
        _ => "",
    };

    /// <summary>生成删除外键约束语句；SQLite 不支持单独删约束，返回空串。</summary>
    public static string BuildDropForeignKey(DbEngine engine, string table, string constraintName, Func<string, string> quote) => engine switch
    {
        DbEngine.MySQL => $"ALTER TABLE {quote(table)} DROP FOREIGN KEY {quote(constraintName)}",
        DbEngine.SqlServer => $"ALTER TABLE {quote(table)} DROP CONSTRAINT {quote(constraintName)}",
        DbEngine.PostgreSQL => $"ALTER TABLE {quote(table)} DROP CONSTRAINT {quote(constraintName)}",
        DbEngine.Oracle => $"ALTER TABLE {quote(table)} DROP CONSTRAINT {quote(constraintName)}",
        _ => "",
    };

    /// <summary>默认值：NULL / 数字 / 常见时间表达式原样，其余加单引号。</summary>
    public static string FormatDefault(string v)
    {
        if (v.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return "NULL";
        if (decimal.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out _)) return v;
        if (System.Text.RegularExpressions.Regex.IsMatch(v,
                @"^(CURRENT_\w+|NOW\(\)|GETDATE\(\)|SYSDATE|SYSTIMESTAMP|RAND\(\)|UUID\(\))$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return v;
        return "'" + v.Replace("'", "''") + "'";
    }
}
