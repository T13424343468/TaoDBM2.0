namespace TaoDBM.Core;

/// <summary>备份可用性演练：把备份文件恢复到临时库，逐表计数与备份时点对账，出通过/失败清单。
/// 纯逻辑层（可测试）：临时库命名、SQLite 文件头完整性、还原规格路由、计数对账与报告渲染；
/// 实际执行（进程调用 / 连接执行）由 UI 层编排。</summary>
public static class BackupDrill
{
    /// <summary>各引擎临时库名长度上限（MySQL 64 / SQL Server 128 / PG 63，留安全余量）。</summary>
    public static int NameLimit(DbEngine e) => e switch
    {
        DbEngine.PostgreSQL => 56,
        DbEngine.SqlServer => 120,
        _ => 56,
    };

    /// <summary>临时库名：taodrill_ + 净化后的源库名 + 时间戳；超限截断，保证可复现且不撞原名。</summary>
    public static string TempDbName(DbEngine e, string baseName, DateTimeOffset at)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in baseName.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch == '_') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var stem = sb.ToString().TrimEnd('_');
        if (stem.Length == 0) stem = "main";
        var name = $"taodrill_{stem}_{at:yyyyMMddHHmmss}";
        var limit = NameLimit(e);
        return name.Length <= limit ? name : name[..limit];
    }

    // ---------- SQLite：临时副本 + 文件头完整性 ----------

    /// <summary>SQLite 文件头魔数（前 16 字节）。</summary>
    public static ReadOnlySpan<byte> SqliteMagic => new byte[]
    {
        0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20, 0x66, 0x6F, 0x72, 0x6D, 0x61, 0x74, 0x20, 0x33, 0x00,
    };

    /// <summary>校验文件头是否为合法 SQLite 数据库（演练完整性检查的第一道闸）。</summary>
    public static bool SqliteHeaderOk(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            Span<byte> buf = stackalloc byte[16];
            var read = fs.Read(buf);
            if (read < 16) return false;
            return buf.SequenceEqual(SqliteMagic);
        }
        catch { return false; }
    }

    /// <summary>SQLite 演练恢复：备份文件拷贝到临时路径（不覆盖原库），拷出后复验文件头。</summary>
    public static void SqliteDrillCopy(string backupFile, string tempFile)
    {
        if (!File.Exists(backupFile)) throw new FileNotFoundException("备份文件不存在：" + backupFile);
        Directory.CreateDirectory(Path.GetDirectoryName(tempFile) ?? ".");
        File.Copy(backupFile, tempFile, overwrite: true);
        if (!SqliteHeaderOk(tempFile)) throw new InvalidDataException("临时副本文件头校验失败，备份疑似损坏或不完整。");
    }

    // ---------- 服务端引擎：还原规格与建/删临时库 ----------

    /// <summary>还原到临时库的命令规格：MySQL / PostgreSQL 复用 BackupTools（密码走环境变量）；其余引擎返回 null。</summary>
    public static ToolSpec? RestoreSpec(DbEngine e, string host, int port, string user, string password, string tempDb, string backupFile)
        => e switch
        {
            DbEngine.MySQL => BackupTools.MysqlRestore(host, port, user, password, tempDb, backupFile),
            DbEngine.PostgreSQL => BackupTools.PgRestore(host, port, user, password, tempDb, backupFile),
            _ => null,
        };

    /// <summary>SQL Server 走 T-SQL（经连接执行，路径为服务器侧路径）；其他引擎走命令行规格。</summary>
    public static string SqlServerRestoreSql(string tempDb, string backupFile)
        => BackupTools.SqlServerRestoreSql(tempDb, backupFile);

    /// <summary>建临时库：MySQL 用字符集显式声明；PG / SQL Server 简单建库；SQLite / 不适用返回 null。</summary>
    public static string? CreateTempDbSql(DbEngine e, string tempDb) => e switch
    {
        DbEngine.MySQL => $"CREATE DATABASE IF NOT EXISTS {BackupTools.QuoteArg(tempDb).Replace('"', '`')} CHARACTER SET utf8mb4;",
        DbEngine.PostgreSQL => $"CREATE DATABASE \"{tempDb.Replace("\"", "\"\"")}\";",
        DbEngine.SqlServer => $"IF DB_ID(N'{tempDb.Replace("'", "''")}') IS NULL CREATE DATABASE [{tempDb.Replace("]", "]]")}];",
        _ => null,
    };

    /// <summary>删临时库（演练收尾）；SQLite / 不适用返回 null。</summary>
    public static string? DropTempDbSql(DbEngine e, string tempDb) => e switch
    {
        DbEngine.MySQL => $"DROP DATABASE IF EXISTS {BackupTools.QuoteArg(tempDb).Replace('"', '`')};",
        DbEngine.PostgreSQL => $"DROP DATABASE IF EXISTS \"{tempDb.Replace("\"", "\"\"")}\";",
        DbEngine.SqlServer => $"IF DB_ID(N'{tempDb.Replace("'", "''")}') IS NOT NULL DROP DATABASE [{tempDb.Replace("]", "]]")}];",
        _ => null,
    };

    /// <summary>逐表计数 SQL（对账用）。</summary>
    public static string CountSql(DbEngine e, string table) => e switch
    {
        DbEngine.MySQL => $"SELECT COUNT(*) AS c FROM `{table.Replace("`", "``")}`;",
        DbEngine.SqlServer => $"SELECT COUNT(*) AS c FROM [{table.Replace("]", "]]")}];",
        _ => $"SELECT COUNT(*) AS c FROM \"{table.Replace("\"", "\"\"")}\";",
    };

    // ---------- 对账清单与报告 ----------

    /// <summary>解析对账清单：每行 `表名=行数`（支持空行、# 注释、全角/中文等号）；解析失败的行进 issues。</summary>
    public static (List<(string Table, long Count)> Items, List<string> Issues) ParseExpectedCounts(string text)
    {
        var items = new List<(string, long)>();
        var issues = new List<string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r').Replace('＝', '=');
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var sep = line.IndexOf('=');
            if (sep < 0) { issues.Add($"对账清单格式不对（缺等号）：{line}"); continue; }
            var table = line[..sep].Trim();
            var num = line[(sep + 1)..].Trim();
            if (table.Length == 0 || !long.TryParse(num, out var cnt) || cnt < 0)
            {
                issues.Add($"对账清单格式不对（表名或行数非法）：{line}");
                continue;
            }
            if (items.Any(i => i.Item1 == table)) { issues.Add($"对账清单重复表：{table}"); continue; }
            items.Add((table, cnt));
        }
        return (items, issues);
    }

    /// <summary>逐表对账：actual 缺表或计数不符都算该表不过；报告整体通过 = 无异常且逐表全过。</summary>
    public static BackupDrillReport VerifyCounts(
        IReadOnlyList<(string Table, long Count)> expected,
        IReadOnlyDictionary<string, long> actual,
        IEnumerable<string>? issues = null)
    {
        var report = new BackupDrillReport();
        if (issues != null) foreach (var i in issues) report.Issues.Add(i);
        foreach (var (table, want) in expected)
        {
            if (!actual.TryGetValue(table, out var got))
            {
                report.Entries.Add(new BackupDrillEntry(table, want, null));
                report.Issues.Add($"临时库中找不到表 {table}，恢复可能不完整。");
                continue;
            }
            report.Entries.Add(new BackupDrillEntry(table, want, got));
        }
        return report;
    }
}

/// <summary>单表对账结论。</summary>
public sealed class BackupDrillEntry
{
    public string Table { get; }
    public long Expected { get; }
    public long? Actual { get; }
    public bool Pass => Actual is long a && a == Expected;

    public BackupDrillEntry(string table, long expected, long? actual)
    {
        Table = table; Expected = expected; Actual = actual;
    }

    public string Line => Pass
        ? $"  ✔ {Table}: {Expected} 行，与备份时点一致"
        : Actual is long a
            ? $"  ✘ {Table}: 备份时点 {Expected} 行，临时库 {a} 行"
            : $"  ✘ {Table}: 备份时点 {Expected} 行，临时库未取到计数";
}

/// <summary>演练报告：整体结论 + 逐表清单 + 异常清单。</summary>
public sealed class BackupDrillReport
{
    public List<BackupDrillEntry> Entries { get; } = new();
    public List<string> Issues { get; } = new();
    public bool Pass => Issues.Count == 0 && Entries.Count > 0 && Entries.All(e => e.Pass);

    public string Render(string header)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine(Pass ? "演练结论：通过 ✔（备份可用）" : "演练结论：未通过 ✘（见下）");
        if (Entries.Count > 0)
        {
            sb.AppendLine($"逐表对账（{Entries.Count} 张）：");
            foreach (var e in Entries) sb.AppendLine(e.Line);
        }
        if (Issues.Count > 0)
        {
            sb.AppendLine($"异常（{Issues.Count} 条）：");
            foreach (var i in Issues.Take(20)) sb.AppendLine("  - " + i);
        }
        return sb.ToString();
    }
}
