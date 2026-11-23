// ODBC 提供者（批次四格3，2026-11-23）：继承 RelationalProvider，元数据走 INFORMATION_SCHEMA 通用口径。
// 连接经 OdbcCore.OdbcDbConnection（odbc32 P/Invoke），DSN 名存 ConnectionProfile.Database。
using System.Data.Common;

namespace TaoDBM.Core;

/// <summary>ODBC（DSN）提供者：通用 INFORMATION_SCHEMA 元数据，分页走 ANSI OFFSET/FETCH。</summary>
public class OdbcProvider : RelationalProvider
{
    public OdbcProvider(ConnectionProfile p) : base(p) { }

    public override bool SupportsBrowse => true;

    protected override DbConnection Open(string? db) =>
        new OdbcCore.OdbcDbConnection(OdbcCore.BuildConnectionString(Profile, db));

    protected override char OpenQuote => '"';
    protected override char CloseQuote => '"';

    protected override string ListDatabasesSql() =>
        "SELECT DISTINCT CATALOG_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE CATALOG_NAME IS NOT NULL";

    protected override string ListChildrenSql(string db) =>
        "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '" + Esc(db) + "' " +
        "AND TABLE_TYPE IN ('TABLE','BASE TABLE','VIEW') ORDER BY TABLE_NAME";

    protected override string ListColumnsSql(string db, string table) =>
        "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS " +
        "WHERE TABLE_SCHEMA = '" + Esc(db) + "' AND TABLE_NAME = '" + Esc(table) + "' ORDER BY ORDINAL_POSITION";

    protected override string PagingClause(int offset, int limit) =>
        $"OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";

    private static string Esc(string s) => s.Replace("'", "''");
}
