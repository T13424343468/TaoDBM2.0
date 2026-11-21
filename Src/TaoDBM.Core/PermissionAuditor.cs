using System.Text.RegularExpressions;

namespace TaoDBM.Core;

/// <summary>档三：权限审计引擎（GRANT 语句解析成账号-权限清单，风险授权发现与对等 REVOKE 生成）。</summary>
/// <remarks>纯引擎免界面稿：MySQL host@user 口径为主，兼容 TO PUBLIC；不做执行，仅审计与建议。</remarks>

/// <summary>授权记录：Grantee 形如 'user'@'host' 或 PUBLIC；Object 为 *.* 或 库.表。</summary>
public sealed record GrantRecord(string Grantee, List<string> Privileges, string Object, bool WithGrantOption, string RawSql);

/// <summary>权限发现：Code=R101 全库全权给宽账号 / R102 可转授 / R103 高危权限给非管理账号，Severity=warn/danger。</summary>
public sealed record PermissionFinding(string Grantee, string Code, string Severity, string Message, string RawSql);

public static class PermissionAuditor
{
    private static readonly string[] HighRisk = { "FILE", "SUPER", "DROP", "SHUTDOWN", "GRANT OPTION", "SYSTEM_USER", "CONTROL" };
    private static readonly string[] AdminPrivs = { "ALL", "ALL PRIVILEGES", "SUPER", "SHUTDOWN", "SYSTEM_USER" };

    /// <summary>解析 GRANT 语句：GRANT p1, p2 ON obj TO 'u'@'h', PUBLIC [WITH GRANT OPTION]。</summary>
    public static List<GrantRecord> Parse(IEnumerable<string> grantSqls)
    {
        var records = new List<GrantRecord>();
        foreach (var raw in grantSqls.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var m = Regex.Match(raw.Trim().TrimEnd(';'), @"^\s*GRANT\s+(?<privs>.+?)\s+ON\s+(?<obj>[\w*`""\[\]\.]+)\s+TO\s+(?<grantees>.+?)(?<wgo>\s+WITH\s+GRANT\s+OPTION)?\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) continue;
            var privs = m.Groups["privs"].Value.Split(',')
                .Select(p => p.Trim().ToUpperInvariant()).Where(p => p.Length > 0).ToList();
            var obj = m.Groups["obj"].Value.Trim('`', '"', '[', ']');
            foreach (var g in SplitGrantees(m.Groups["grantees"].Value))
                records.Add(new GrantRecord(g, privs, obj, m.Groups["wgo"].Success, raw.Trim()));
        }
        return records;
    }

    /// <summary>审计：宽账号（@'%'/PUBLIC）拿全库全权、可转授、高危权限给普通账号逐条报出。</summary>
    public static List<PermissionFinding> Audit(IReadOnlyList<GrantRecord> records)
    {
        var findings = new List<PermissionFinding>();
        foreach (var r in records)
        {
            bool broad = r.Grantee.Equals("PUBLIC", StringComparison.OrdinalIgnoreCase)
                         || r.Grantee.EndsWith("'@'%'", StringComparison.OrdinalIgnoreCase)
                         || r.Grantee.EndsWith("@%", StringComparison.OrdinalIgnoreCase);
            bool allPriv = r.Privileges.Any(p => p.StartsWith("ALL", StringComparison.Ordinal));
            if (broad && allPriv && r.Object is "*.*" or "*")
                findings.Add(new PermissionFinding(r.Grantee, "R101", "danger",
                    "全库全权授予了不限主机的宽账号，等于放弃权限控制", r.RawSql));
            if (r.WithGrantOption)
                findings.Add(new PermissionFinding(r.Grantee, "R102", "warn",
                    "带 WITH GRANT OPTION，被授权方可再扩散权限", r.RawSql));
            var risky = r.Privileges.Where(p => HighRisk.Contains(p, StringComparer.Ordinal)).ToList();
            if (risky.Count > 0 && !LooksLikeAdmin(r.Grantee))
                findings.Add(new PermissionFinding(r.Grantee, "R103", "warn",
                    $"高危权限 {string.Join("/", risky)} 授给了普通业务账号", r.RawSql));
        }
        return findings;
    }

    /// <summary>对等回收：按记录生成 REVOKE（privs ON obj FROM grantee），宽账号危险项建议一并回收转授。</summary>
    public static List<string> RevokeSql(GrantRecord record)
    {
        string grantee = record.Grantee.Equals("PUBLIC", StringComparison.OrdinalIgnoreCase) ? "PUBLIC" : record.Grantee;
        return new List<string>
        {
            $"REVOKE {string.Join(", ", record.Privileges)} ON {record.Object} FROM {grantee};",
        };
    }

    internal static List<string> SplitGrantees(string text)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\'') depth = depth == 0 ? 1 : 0;
            if (text[i] == ',' && depth == 0)
            {
                parts.Add(text[start..i].Trim()); start = i + 1;
            }
        }
        parts.Add(text[start..].Trim());
        return parts.Where(p => p.Length > 0).Select(NormalizeGrantee).ToList();
    }

    static string NormalizeGrantee(string g)
    {
        var m = Regex.Match(g, @"^(?:'(?<u>[^']*)'|[\w$]+)(?:@'(?<h>[^']*)'|@[\w$.%\*]+)?$", RegexOptions.IgnoreCase);
        if (!m.Success) return g;
        var user = m.Groups["u"].Success ? $"'{m.Groups["u"].Value}'" : g.Split('@')[0];
        var host = m.Groups["h"].Success ? $"'{m.Groups["h"].Value}'" : null;
        if (user.StartsWith('\'')) return host == null ? user : $"{user}@{host}";
        return g; // PUBLIC 等裸词原样
    }

    static bool LooksLikeAdmin(string grantee) =>
        grantee.Contains("admin", StringComparison.OrdinalIgnoreCase) || grantee.Contains("root", StringComparison.OrdinalIgnoreCase);
}
