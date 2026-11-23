// 备份转 SQL（批次四格5，2026-11-23）：解析 SQLite / MySQL dump 文本流，逐 INSERT / 建表语句重排输出。
// 两遍流式逐行：一遍收建表（小），二遍按原顺序流 INSERT，大文件内存恒定不爆栈。
using System.Text;

namespace TaoDBM.Core;

/// <summary>dump → SQL 重排：建表语句先行（原顺序），INSERT 按原顺序随后；SET / LOCK / 注释 / DROP 丢弃。</summary>
public static class BackupToSql
{
    public sealed class ConvertResult
    {
        public int Statements { get; init; }
        public int Tables { get; init; }
        public int Inserts { get; init; }
    }

    /// <summary>转换 dump 文件到 SQL 文件。流式两遍，返回语句计数。</summary>
    public static ConvertResult ConvertFile(string dumpPath, string outPath, CancellationToken ct = default)
    {
        using var outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w = new StreamWriter(outStream, new UTF8Encoding(false));
        return Convert(dumpPath, w, ct);
    }

    public static ConvertResult Convert(string dumpPath, TextWriter w, CancellationToken ct = default)
    {
        // 一遍：收 CREATE TABLE（文本小，可驻内存）与表顺序
        var creates = new List<string>();
        var inserts = new List<string>();   // 二遍重写：INSERT 逐条流式输出，这里只存计数
        int insertCount = 0;
        foreach (var stmt in StreamStatements(dumpPath, ct))
        {
            var head = Head(stmt);
            if (head.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase)) creates.Add(stmt);
        }

        // 输出建表
        foreach (var c in creates)
        {
            ct.ThrowIfCancellationRequested();
            w.WriteLine(c.TrimEnd());
            w.WriteLine();
        }

        // 二遍：按原顺序流式输出 INSERT
        foreach (var stmt in StreamStatements(dumpPath, ct))
        {
            ct.ThrowIfCancellationRequested();
            var head = Head(stmt);
            if (head.StartsWith("INSERT INTO", StringComparison.OrdinalIgnoreCase) ||
                head.StartsWith("INSERT OR", StringComparison.OrdinalIgnoreCase))
            {
                w.WriteLine(stmt.TrimEnd());
                insertCount++;
            }
        }
        return new ConvertResult { Statements = creates.Count + insertCount, Tables = creates.Count, Inserts = insertCount };
    }

    private static string Head(string stmt)
    {
        var s = stmt.TrimStart();
        int nl = s.IndexOfAny(new[] { '\r', '\n', '(' });
        return (nl >= 0 ? s[..nl] : s).Trim();
    }

    /// <summary>流式逐行扫描 dump，按语句边界（引号外分号）产出完整语句；注释 / SET / LOCK / DROP / PRAGMA / 事务控制丢弃。</summary>
    private static IEnumerable<string> StreamStatements(string path, CancellationToken ct)
    {
        var sb = new StringBuilder();
        bool inString = false;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (sb.Length == 0)
            {
                var t = line.TrimStart();
                // 整行即可判定的噪音：注释、SET、LOCK、UNLOCK、DROP、PRAGMA、事务控制、空行
                if (t.Length == 0
                    || t.StartsWith("--", StringComparison.Ordinal)
                    || t.StartsWith("/*", StringComparison.Ordinal)
                    || t.StartsWith("#", StringComparison.Ordinal)
                    || t.StartsWith("SET ", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("LOCK ", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("UNLOCK ", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("DROP ", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("START TRANSACTION", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("DELIMITER", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            sb.AppendLine(line);
            // 扫描本行：引号外分号 = 语句边界（'' 转义内不切）
            var s = line;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (c == '\'')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '\'') i++;
                        else inString = false;
                    }
                }
                else if (c == '\'') inString = true;
                else if (c == '-' && i + 1 < s.Length && s[i + 1] == '-') break;   // 行注释，剩余不扫
                else if (c == ';')
                {
                    var stmt = sb.ToString().Trim();
                    sb.Clear();
                    if (stmt.Length > 0 && IsWanted(stmt)) yield return stmt;
                    break;
                }
            }
        }
        var tail = sb.ToString().Trim();
        if (tail.Length > 0 && IsWanted(tail)) yield return tail;
    }

    private static bool IsWanted(string stmt)
    {
        var head = Head(stmt);
        return head.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("INSERT INTO", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("INSERT OR", StringComparison.OrdinalIgnoreCase);
    }
}
