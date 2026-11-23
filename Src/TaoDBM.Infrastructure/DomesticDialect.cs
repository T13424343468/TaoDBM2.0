namespace TaoDBM.Core;

/// <summary>
/// 批次七（2026-11-24 老板令瀑布落码）：国产六家方言档。
/// 底座策略＝协议兼容引擎复用现有 Provider（OceanBase/PolarDB/TDSQL 走 MySQL 协议，
/// GaussDB/金仓走 PostgreSQL 协议，达梦走 Oracle 兼容），方言差异层做增量，不逐家重写驱动。
/// 每家一档：标识符引号、分页语法、系统目录查询、执行计划语句、备份命令映射。
/// 纯逻辑、无 IO 依赖；真库云机不可得项按「协议兼容验证，真库待老板环境补测」口径入台账。
/// </summary>
public sealed class DomesticDialect
{
    /// <summary>兼容的既有引擎（Provider 复用口径）。</summary>
    public DbEngine CompatibleEngine { get; init; }

    /// <summary>标识符引号（与兼容引擎 Provider 一致）。</summary>
    public char OpenQuote { get; init; }
    public char CloseQuote { get; init; }

    /// <summary>系统目录：列清单查询（db/table 已由调用方转义）。</summary>
    public Func<string, string, string> ListColumnsSql { get; init; } = (_, _) => "";

    /// <summary>执行计划语句模板：{0} 占位原语句。</summary>
    public Func<string, string> ExplainSql { get; init; } = sql => sql;

    /// <summary>备份命令映射：兼容引擎对应实现（mysqldump/pg_dump/exp 等），{0} 库名 {1} 输出文件。</summary>
    public Func<string, string, string> BackupCommand { get; init; } = (_, _) => "";

    /// <summary>驱动指引：非空表示需要用户放置厂商驱动（如达梦 dmjdbc），值为说人话指引文案。</summary>
    public string? DriverHint { get; init; }

    /// <summary>连接串用户名解析（OceanBase 租户格式 user@tenant#cluster）；null 表示原样使用。</summary>
    public Func<string, string>? UserRewrite { get; init; }

    public static DomesticDialect Get(DbEngine e) => e switch
    {
        // 七-1 OceanBase：MySQL 租户协议，SHOW 系列同 MySQL，EXPLAIN 同形
        DbEngine.OceanBase => new DomesticDialect
        {
            CompatibleEngine = DbEngine.MySQL,
            OpenQuote = '`', CloseQuote = '`',
            ListColumnsSql = (db, table) =>
                "SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY FROM information_schema.COLUMNS" +
                $" WHERE TABLE_SCHEMA = '{db.Replace("'", "''")}' AND TABLE_NAME = '{table.Replace("'", "''")}' ORDER BY ORDINAL_POSITION",
            ExplainSql = sql => "EXPLAIN " + sql,
            BackupCommand = (db, file) => $"mysqldump --single-transaction --databases {db} --result-file=\"{file}\"",
            UserRewrite = user => NormalizeObUser(user),
        },
        // 七-2 GaussDB：PostgreSQL 协议，schema=库+用户双空间，EXPLAIN 同 PG
        DbEngine.GaussDB => new DomesticDialect
        {
            CompatibleEngine = DbEngine.PostgreSQL,
            OpenQuote = '"', CloseQuote = '"',
            ListColumnsSql = (db, table) =>
                "SELECT c.column_name, c.data_type, c.is_nullable, CASE WHEN tc.constraint_type = 'PRIMARY KEY' THEN 'PRI' ELSE '' END" +
                " FROM information_schema.columns c" +
                " LEFT JOIN information_schema.key_column_usage k ON k.table_schema = c.table_schema AND k.table_name = c.table_name AND k.column_name = c.column_name" +
                " LEFT JOIN information_schema.table_constraints tc ON tc.constraint_name = k.constraint_name AND tc.constraint_type = 'PRIMARY KEY'" +
                $" WHERE c.table_schema = '{db.Replace("'", "''")}' AND c.table_name = '{table.Replace("'", "''")}' ORDER BY c.ordinal_position",
            ExplainSql = sql => "EXPLAIN " + sql,
            BackupCommand = (db, file) => $"gs_dump -F c -f \"{file}\" {db}（缺 gs_dump 时用 PG 兼容路径 pg_dump）",
        },
        // 七-3 达梦：Oracle 兼容，PAGE 分页（OFFSET…ROWS FETCH），系统目录走 Oracle 兼容视图；驱动不自带
        DbEngine.Dameng => new DomesticDialect
        {
            CompatibleEngine = DbEngine.Oracle,
            OpenQuote = '"', CloseQuote = '"',
            ListColumnsSql = (_, table) =>
                "SELECT column_name, data_type || CASE WHEN data_type IN ('VARCHAR2','NVARCHAR2','CHAR') THEN '(' || data_length || ')' ELSE '' END," +
                " CASE WHEN nullable = 'Y' THEN 'YES' ELSE 'NO' END, ''" +
                " FROM user_tab_columns WHERE table_name = UPPER('" + table.Replace("'", "''") + "') ORDER BY column_id",
            ExplainSql = sql => "EXPLAIN " + sql,
            BackupCommand = (db, file) => $"dexp USERID=SYSDBA/SYSDBA FILE=\"{file}\" SCHEMAS={db}",
            DriverHint = "达梦使用厂商私有驱动，本程序不自带：请从达梦官网获取 DmProvider.dll，放到程序安装目录的 drivers 文件夹后重新连接。",
        },
        // 七-4 人大金仓：PostgreSQL 兼容协议，大小写敏感模式开关走附加参数
        DbEngine.Kingbase => new DomesticDialect
        {
            CompatibleEngine = DbEngine.PostgreSQL,
            OpenQuote = '"', CloseQuote = '"',
            ListColumnsSql = (db, table) =>
                "SELECT c.column_name, c.data_type, c.is_nullable, CASE WHEN tc.constraint_type = 'PRIMARY KEY' THEN 'PRI' ELSE '' END" +
                " FROM information_schema.columns c" +
                " LEFT JOIN information_schema.key_column_usage k ON k.table_schema = c.table_schema AND k.table_name = c.table_name AND k.column_name = c.column_name" +
                " LEFT JOIN information_schema.table_constraints tc ON tc.constraint_name = k.constraint_name AND tc.constraint_type = 'PRIMARY KEY'" +
                $" WHERE c.table_schema = '{db.Replace("'", "''")}' AND c.table_name = '{table.Replace("'", "''")}' ORDER BY c.ordinal_position",
            ExplainSql = sql => "EXPLAIN " + sql,
            BackupCommand = (db, file) => $"sys_dump -F c -f \"{file}\" {db}（缺 sys_dump 时用 PG 兼容路径 pg_dump）",
        },
        // 七-5 PolarDB：MySQL 兼容协议，集群地址走主机字段，只读节点识别走 innodb_read_only 探针
        DbEngine.PolarDB => new DomesticDialect
        {
            CompatibleEngine = DbEngine.MySQL,
            OpenQuote = '`', CloseQuote = '`',
            ListColumnsSql = (db, table) =>
                "SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY FROM information_schema.COLUMNS" +
                $" WHERE TABLE_SCHEMA = '{db.Replace("'", "''")}' AND TABLE_NAME = '{table.Replace("'", "''")}' ORDER BY ORDINAL_POSITION",
            ExplainSql = sql => "EXPLAIN " + sql,
            BackupCommand = (db, file) => $"mysqldump --single-transaction --databases {db} --result-file=\"{file}\"",
        },
        // 七-6 TDSQL：MySQL 兼容协议，Proxy 连接串，分片表元数据只读（information_schema 兼容口径），分布式执行计划透传
        DbEngine.Tdsql => new DomesticDialect
        {
            CompatibleEngine = DbEngine.MySQL,
            OpenQuote = '`', CloseQuote = '`',
            ListColumnsSql = (db, table) =>
                "SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY FROM information_schema.COLUMNS" +
                $" WHERE TABLE_SCHEMA = '{db.Replace("'", "''")}' AND TABLE_NAME = '{table.Replace("'", "''")}' ORDER BY ORDINAL_POSITION",
            ExplainSql = sql => "EXPLAIN " + sql,
            BackupCommand = (db, file) => $"mysqldump --single-transaction --databases {db} --result-file=\"{file}\"",
        },
        _ => throw new NotSupportedException("非国产库引擎请走对应 Provider 方言"),
    };

    /// <summary>OceanBase 租户用户名归一：user / user@tenant / user@tenant#cluster 均接受，
    /// 缺租户段时原样返回（社区版常为单租户直连）；只做格式校验不改写语义。</summary>
    public static string NormalizeObUser(string user)
    {
        var u = user.Trim();
        if (u.Length == 0) return u;
        var at = u.IndexOf('@');
        if (at < 0) return u;                       // 纯用户名：单租户直连
        var tenant = u[(at + 1)..];
        var hash = tenant.IndexOf('#');
        if (hash >= 0 && hash == 0) throw new FormatException("OceanBase 用户名格式应为 用户@租户#集群，如 root@prod#cluster1");
        return u;
    }

    /// <summary>分页语法（与兼容引擎 Provider 的 PagingClause 同口径）。</summary>
    public string Paging(DbEngine e, int offset, int limit) => e switch
    {
        DbEngine.OceanBase or DbEngine.PolarDB or DbEngine.Tdsql => $"LIMIT {limit} OFFSET {offset}",
        DbEngine.GaussDB or DbEngine.Kingbase => $"LIMIT {limit} OFFSET {offset}",
        DbEngine.Dameng => $"OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY",
        _ => throw new NotSupportedException(),
    };
}
