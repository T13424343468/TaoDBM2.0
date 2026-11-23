namespace TaoDBM.Core;

/// <summary>支持的数据库引擎。</summary>
public enum DbEngine
{
    MySQL,
    PostgreSQL,
    SqlServer,
    SQLite,
    Oracle,
    MongoDB,
    Redis,
    Odbc,   // 批次四格3（2026-11-23）：ODBC（DSN），加在末尾保存档枚举序兼容
    // 批次七（2026-11-24 老板令瀑布落码）：国产六家，协议兼容引擎复用现有 Provider，方言差异层做增量
    OceanBase,  // MySQL 租户协议（端口 2881）
    GaussDB,    // PostgreSQL 协议（端口 8000/5432）
    Dameng,     // Oracle 兼容（端口 5236），驱动不自带，引导放置 drivers/
    Kingbase,   // PostgreSQL 兼容协议（端口 54321）
    PolarDB,    // MySQL 兼容协议（端口 3306/3006）
    Tdsql,      // MySQL 兼容协议（Proxy）
}

public static class EngineInfo
{
    public static string Name(DbEngine e) => e switch
    {
        DbEngine.MySQL => "MySQL",
        DbEngine.PostgreSQL => "PostgreSQL",
        DbEngine.SqlServer => "SQL Server",
        DbEngine.SQLite => "SQLite",
        DbEngine.Oracle => "Oracle",
        DbEngine.MongoDB => "MongoDB",
        DbEngine.Redis => "Redis",
        DbEngine.Odbc => "ODBC（DSN）",
        DbEngine.OceanBase => "OceanBase",
        DbEngine.GaussDB => "GaussDB",
        DbEngine.Dameng => "达梦 DM",
        DbEngine.Kingbase => "人大金仓 KingbaseES",
        DbEngine.PolarDB => "PolarDB",
        DbEngine.Tdsql => "TDSQL",
        _ => e.ToString(),
    };

    public static int DefaultPort(DbEngine e) => e switch
    {
        DbEngine.MySQL => 3306,
        DbEngine.PostgreSQL => 5432,
        DbEngine.SqlServer => 1433,
        DbEngine.Oracle => 1521,
        DbEngine.Redis => 6379,
        DbEngine.MongoDB => 27017,
        DbEngine.Odbc => 0,
        DbEngine.OceanBase => 2881,
        DbEngine.GaussDB => 8000,
        DbEngine.Dameng => 5236,
        DbEngine.Kingbase => 54321,
        DbEngine.PolarDB => 3306,
        DbEngine.Tdsql => 3306,
        _ => 0,
    };

    public static bool IsFileBased(DbEngine e) => e == DbEngine.SQLite;

    public static bool IsRelational(DbEngine e) =>
        e is not (DbEngine.MongoDB or DbEngine.Redis);

    public static IEnumerable<DbEngine> All =>
        new[] { DbEngine.MySQL, DbEngine.PostgreSQL, DbEngine.SqlServer, DbEngine.SQLite,
                DbEngine.Oracle, DbEngine.MongoDB, DbEngine.Redis, DbEngine.Odbc,
                DbEngine.OceanBase, DbEngine.GaussDB, DbEngine.Dameng, DbEngine.Kingbase,
                DbEngine.PolarDB, DbEngine.Tdsql };

    /// <summary>批次七：国产六家（连接对话框「国产数据库」分组口径，按 All 顺序）。</summary>
    public static IEnumerable<DbEngine> Domestic =>
        new[] { DbEngine.OceanBase, DbEngine.GaussDB, DbEngine.Dameng, DbEngine.Kingbase, DbEngine.PolarDB, DbEngine.Tdsql };

    public static bool IsDomestic(DbEngine e) => Domestic.Contains(e);
}
