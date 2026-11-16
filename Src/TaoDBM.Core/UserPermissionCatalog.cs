using System.Text.RegularExpressions;

namespace TaoDBM.Core;

/// <summary>B3 接线：用户与权限清单目录 SQL。账号清单 + 每账号授权文本（喂给 PermissionAuditor.Parse），不执行任何授权/回收。</summary>
/// <remarks>MySQL 走 SHOW GRANTS FOR；PostgreSQL 从目录合成等价 GRANT 文本；SQLite 无账号体系；SQL Server 目录语法差异大，下一批补齐。</remarks>
public static class UserPermissionCatalog
{
    public static bool Supported(DbEngine e) => e is DbEngine.MySQL or DbEngine.PostgreSQL;

    public static string UnsupportedReason(DbEngine e) => e switch
    {
        DbEngine.SQLite => "SQLite 无服务端账号体系（库文件级加密即权限边界），无权限可审。",
        DbEngine.MongoDB or DbEngine.Redis => "MongoDB / Redis 的账号体系不适用 GRANT 审计口径。",
        _ => "该引擎的权限清单接线下一批补齐，暂用数据库自带工具查看。",
    };

    /// <summary>账号清单：两列（user, host）。MySQL 用 mysql.user（需相应读权）；PG 统一 host 列为 '-'。</summary>
    public static string ListAccountsSql(DbEngine e) => e switch
    {
        DbEngine.MySQL => "SELECT user, host FROM mysql.user ORDER BY user, host",
        DbEngine.PostgreSQL => "SELECT usename AS user, '-' AS host FROM pg_user ORDER BY usename",
        _ => throw new NotSupportedException(UnsupportedReason(e)),
    };

    /// <summary>取某账号的授权文本。MySQL 返回 SHOW GRANTS 结果（单列「Grants for …」）；PG 从目录合成 GRANT 语句文本。</summary>
    public static string GrantsSql(DbEngine e, string user, string host) => e switch
    {
        DbEngine.MySQL => $"SHOW GRANTS FOR '{Q(user)}'@'{Q(host)}'",
        DbEngine.PostgreSQL => PostgresGrantsSql(user),
        _ => throw new NotSupportedException(UnsupportedReason(e)),
    };

    /// <summary>从 SHOW GRANTS 单列结果里抠出 GRANT 语句原文（兼容客户端「Grants for …: 」展示前缀）。</summary>
    public static List<string> ExtractGrantTexts(IEnumerable<object?[]> rows) =>
        rows.Select(r => r.LastOrDefault(v => v is not null)?.ToString() ?? "")
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(StripGrantPrefix)
            .Where(t => t.StartsWith("GRANT", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static string StripGrantPrefix(string raw)
    {
        var t = raw.Trim().TrimEnd(';');
        if (t.StartsWith("Grants for", StringComparison.OrdinalIgnoreCase))
        {
            int i = t.IndexOf(':');
            if (i >= 0 && t[(i + 1)..].TrimStart().StartsWith("GRANT", StringComparison.OrdinalIgnoreCase))
                t = t[(i + 1)..].Trim();
        }
        return t;
    }

    /// <summary>账号清单行 → (user, host)。</summary>
    public static List<(string User, string Host)> ExtractAccounts(IEnumerable<object?[]> rows) =>
        rows.Where(r => r.Length >= 2 && r[0]?.ToString()?.Length > 0)
            .Select(r => (r[0]!.ToString()!, r[1]?.ToString() ?? "-"))
            .ToList();

    /// <summary>PG：把表级 / 库级 / schema 级授权合成回 GRANT 文本（grantee 加单引号，配合 Parse）。</summary>
    private static string PostgresGrantsSql(string user)
    {
        var u = Q(user);
        return
            "SELECT 'GRANT ' || privilege_type || ' ON ' || table_schema || '.' || table_name || ' TO ''' || grantee || ''''" +
            " || CASE WHEN is_grantable = 'YES' THEN ' WITH GRANT OPTION' ELSE '' END" +
            $" FROM information_schema.table_privileges WHERE grantee = '{u}'" +
            " UNION ALL " +
            "SELECT 'GRANT ' || privilege_type || ' ON DATABASE ' || table_catalog || ' TO ''' || grantee || ''''" +
            $" FROM information_schema.usage_privileges WHERE grantee = '{u}'" +
            " UNION ALL " +
            "SELECT 'GRANT ' || privilege_type || ' ON DATABASE ' || catalog_name || ' TO ''' || grantee || ''''" +
            $" FROM information_schema.schema_privileges WHERE grantee = '{u}' ORDER BY 1";
    }

    private static string Q(string s) => s.Replace("'", "''");

    /// <summary>账号显示名（列表用）：'u'@'h' 口径；PG 无 host 则只显名字。</summary>
    public static string AccountLabel(DbEngine e, string user, string host) =>
        e == DbEngine.MySQL ? $"'{user}'@'{host}'" : user;

    /// <summary>从 GrantRecord（MySQL 口径 Grantee='u'@'h'）反查 SHOW GRANTS 的账号键；防注入只回显合法字符。</summary>
    public static (string User, string Host)? SplitGrantee(string grantee)
    {
        var m = Regex.Match(grantee, @"^'(?<u>[^']*)'@'(?<h>[^']*)'$");
        return m.Success ? (m.Groups["u"].Value, m.Groups["h"].Value) : null;
    }
}
