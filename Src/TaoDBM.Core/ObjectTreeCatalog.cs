using System.Text;
using TaoDBM.Infrastructure;

namespace TaoDBM.Core;

/// <summary>档三：对象树补齐引擎——按数据库引擎生成视图/索引/外键/存储过程与函数/触发器的元数据查询语句。</summary>
/// <remarks>纯引擎免界面稿：只负责生成目录查询语句，不做执行；对象树拿到结果后挂到对应节点。</remarks>

public static class ObjectTreeCatalog
{
    /// <summary>当前引擎是否支持对象树目录查询（七库全覆盖：关系库走 SQL 元数据，Mongo 走 TAODBMCMD 命令，Redis 走空清单 SCAN）。</summary>
    public static bool Supported(DbEngine e) => e is DbEngine.MySQL or DbEngine.PostgreSQL or DbEngine.SqlServer
        or DbEngine.SQLite or DbEngine.Oracle or DbEngine.MongoDB or DbEngine.Redis;

    /// <summary>视图列表。</summary>
    public static string ListViews(DbEngine e, string db) => e switch
    {
        DbEngine.MySQL => $"SELECT TABLE_NAME AS name, VIEW_DEFINITION AS definition FROM information_schema.VIEWS WHERE TABLE_SCHEMA = '{Esc(db)}' ORDER BY TABLE_NAME",
        DbEngine.PostgreSQL => $"SELECT viewname AS name, definition FROM pg_views WHERE schemaname = '{Esc(db)}' ORDER BY viewname",
        DbEngine.SqlServer => $"SELECT name, OBJECT_DEFINITION(object_id) AS definition FROM sys.views ORDER BY name",
        DbEngine.SQLite => $"SELECT name, sql AS definition FROM sqlite_master WHERE type = 'view' ORDER BY name",
        DbEngine.Oracle => $"SELECT VIEW_NAME AS name, TEXT AS definition FROM USER_VIEWS ORDER BY VIEW_NAME",
        DbEngine.MongoDB => "TAODBMCMD:listViews",
        DbEngine.Redis => "SCAN 0 MATCH __taodbm_none__* COUNT 100",
        _ => throw new NotSupportedException("该引擎不支持视图查询"),
    };

    /// <summary>索引列表。</summary>
    public static string ListIndexes(DbEngine e, string db) => e switch
    {
        DbEngine.MySQL => $"SELECT TABLE_NAME AS table_name, INDEX_NAME AS index_name, NON_UNIQUE AS non_unique FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = '{Esc(db)}' GROUP BY TABLE_NAME, INDEX_NAME, NON_UNIQUE ORDER BY TABLE_NAME, INDEX_NAME",
        DbEngine.PostgreSQL => $"SELECT tablename AS table_name, indexname AS index_name, indexdef FROM pg_indexes WHERE schemaname = '{Esc(db)}' ORDER BY tablename, indexname",
        DbEngine.SqlServer => $"SELECT t.name AS table_name, i.name AS index_name, i.is_unique FROM sys.indexes i JOIN sys.tables t ON i.object_id = t.object_id ORDER BY t.name, i.name",
        DbEngine.SQLite => $"SELECT tbl_name AS table_name, name AS index_name, 1 AS non_unique FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL ORDER BY tbl_name, name",
        DbEngine.Oracle => $"SELECT TABLE_NAME AS table_name, INDEX_NAME AS index_name, CASE UNIQUENESS WHEN 'UNIQUE' THEN 0 ELSE 1 END AS non_unique FROM USER_INDEXES ORDER BY TABLE_NAME, INDEX_NAME",
        DbEngine.MongoDB => "TAODBMCMD:listIndexes",
        DbEngine.Redis => "SCAN 0 MATCH __taodbm_none__* COUNT 100",
        _ => throw new NotSupportedException("该引擎不支持索引查询"),
    };

    /// <summary>外键列表。</summary>
    public static string ListForeignKeys(DbEngine e, string db) => e switch
    {
        DbEngine.MySQL => $"SELECT TABLE_NAME AS table_name, COLUMN_NAME AS column_name, REFERENCED_TABLE_NAME AS ref_table, REFERENCED_COLUMN_NAME AS ref_column, CONSTRAINT_NAME FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA = '{Esc(db)}' AND REFERENCED_TABLE_NAME IS NOT NULL ORDER BY TABLE_NAME",
        DbEngine.PostgreSQL => $"SELECT tc.table_name, kcu.column_name, ccu.table_name AS ref_table, ccu.column_name AS ref_column, tc.constraint_name FROM information_schema.table_constraints tc JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = '{Esc(db)}' ORDER BY tc.table_name",
        DbEngine.SqlServer => $"SELECT fk.name AS constraint_name, tp.name AS table_name, cp.name AS column_name, tr.name AS ref_table, cr.name AS ref_column FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id JOIN sys.tables tp ON fkc.parent_object_id = tp.object_id JOIN sys.columns cp ON fkc.parent_object_id = cp.object_id AND fkc.parent_column_id = cp.column_id JOIN sys.tables tr ON fkc.referenced_object_id = tr.object_id JOIN sys.columns cr ON fkc.referenced_object_id = cr.object_id AND fkc.referenced_column_id = cr.column_id ORDER BY tp.name",
        DbEngine.SQLite => $"SELECT m.name AS table_name, p.\"table\" AS ref_table, p.\"from\" AS column_name, p.\"to\" AS ref_column FROM sqlite_master m JOIN pragma_foreign_key_list(m.name) p WHERE m.type = 'table' ORDER BY m.name",
        DbEngine.Oracle => $"SELECT c.TABLE_NAME AS table_name, cc.COLUMN_NAME AS column_name, rc.TABLE_NAME AS ref_table, rcc.COLUMN_NAME AS ref_column, c.CONSTRAINT_NAME FROM USER_CONSTRAINTS c JOIN USER_CONS_COLUMNS cc ON c.CONSTRAINT_NAME = cc.CONSTRAINT_NAME LEFT JOIN USER_CONSTRAINTS rc ON c.R_CONSTRAINT_NAME = rc.CONSTRAINT_NAME LEFT JOIN USER_CONS_COLUMNS rcc ON rc.CONSTRAINT_NAME = rcc.CONSTRAINT_NAME WHERE c.CONSTRAINT_TYPE = 'R' ORDER BY c.TABLE_NAME",
        DbEngine.MongoDB => "TAODBMCMD:none",
        DbEngine.Redis => "SCAN 0 MATCH __taodbm_none__* COUNT 100",
        _ => throw new NotSupportedException("该引擎不支持外键查询"),
    };

    /// <summary>存储过程与函数列表（SQLite 无此概念，返回空提示语句）。</summary>
    public static string ListRoutines(DbEngine e, string db) => e switch
    {
        DbEngine.MySQL => $"SELECT ROUTINE_NAME AS name, ROUTINE_TYPE AS routine_type, ROUTINE_DEFINITION AS definition FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{Esc(db)}' ORDER BY ROUTINE_TYPE, ROUTINE_NAME",
        DbEngine.PostgreSQL => $"SELECT proname AS name, CASE prokind WHEN 'f' THEN 'FUNCTION' WHEN 'p' THEN 'PROCEDURE' END AS routine_type, pg_get_functiondef(oid) AS definition FROM pg_proc WHERE pronamespace = (SELECT oid FROM pg_namespace WHERE nspname = '{Esc(db)}') ORDER BY proname",
        DbEngine.SqlServer => $"SELECT name, CASE WHEN type_desc LIKE '%FUNCTION%' THEN 'FUNCTION' ELSE 'PROCEDURE' END AS routine_type, OBJECT_DEFINITION(object_id) AS definition FROM sys.objects WHERE type IN ('P', 'FN') ORDER BY name",
        DbEngine.Oracle => $"SELECT NAME AS name, TYPE AS routine_type, '' AS definition FROM USER_SOURCE WHERE LINE = 1 ORDER BY TYPE, NAME",
        DbEngine.MongoDB => "TAODBMCMD:none",
        DbEngine.Redis => "SCAN 0 MATCH __taodbm_none__* COUNT 100",
        _ => "SELECT '' AS name, '' AS routine_type, 'SQLite 不支持存储过程与函数' AS definition WHERE 0",
    };

    /// <summary>触发器列表。</summary>
    public static string ListTriggers(DbEngine e, string db) => e switch
    {
        DbEngine.MySQL => $"SELECT TRIGGER_NAME AS name, EVENT_OBJECT_TABLE AS table_name, ACTION_STATEMENT AS definition FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = '{Esc(db)}' ORDER BY TRIGGER_NAME",
        DbEngine.PostgreSQL => $"SELECT tgname AS name, c.relname AS table_name, pg_get_triggerdef(t.oid) AS definition FROM pg_trigger t JOIN pg_class c ON t.tgrelid = c.oid WHERE NOT t.tgisinternal AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = '{Esc(db)}') ORDER BY tgname",
        DbEngine.SqlServer => $"SELECT tr.name, t.name AS table_name, OBJECT_DEFINITION(tr.object_id) AS definition FROM sys.triggers tr JOIN sys.tables t ON tr.parent_id = t.object_id ORDER BY tr.name",
        DbEngine.SQLite => $"SELECT name, tbl_name AS table_name, sql AS definition FROM sqlite_master WHERE type = 'trigger' ORDER BY name",
        DbEngine.Oracle => $"SELECT TRIGGER_NAME AS name, TABLE_NAME AS table_name, TRIGGER_BODY AS definition FROM USER_TRIGGERS ORDER BY TRIGGER_NAME",
        DbEngine.MongoDB => "TAODBMCMD:none",
        DbEngine.Redis => "SCAN 0 MATCH __taodbm_none__* COUNT 100",
        _ => throw new NotSupportedException("该引擎不支持触发器查询"),
    };

    /// <summary>查看对象定义（存储过程/函数/触发器/视图通用入口）。</summary>
    public static string ShowDefinition(DbEngine e, string name) => e switch
    {
        DbEngine.MySQL => $"SELECT ROUTINE_DEFINITION FROM information_schema.ROUTINES WHERE ROUTINE_NAME = '{Esc(name)}'",
        DbEngine.PostgreSQL => $"SELECT pg_get_functiondef('{Esc(name)}'::regproc)",
        DbEngine.SqlServer => $"SELECT OBJECT_DEFINITION(OBJECT_ID('{Esc(name)}'))",
        DbEngine.SQLite => $"SELECT sql FROM sqlite_master WHERE name = '{Esc(name)}'",
        DbEngine.Oracle => $"SELECT TEXT FROM USER_SOURCE WHERE NAME = '{Esc(name)}' ORDER BY LINE",
        DbEngine.MongoDB => $"TAODBMCMD:collInfo:{Esc(name)}",
        DbEngine.Redis => $"TYPE {name}",
        _ => throw new NotSupportedException("该引擎不支持对象定义查询"),
    };

    private static string Esc(string s) => s.Replace("'", "''");
}
