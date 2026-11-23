// DDL 解析器（批次五格4，2026-11-23）：CREATE TABLE 全量解析——列 / 类型 / 主键（行内 + 表级）/ 外键，入模型。
// 独立于 DesignAssistDialog.ParseCorpus 血缘骨架：只认建表语句，输出结构化实体，供三层模型 / 模型比较复用。
using System.Text.RegularExpressions;

namespace TaoDBM.Core;

/// <summary>解析出的实体：列（名 / 类型 / 主键）+ 外键（列 → 目标表.目标列）。</summary>
public class DdlEntity
{
    public string Name { get; set; } = "";
    public List<DdlColumn> Columns { get; set; } = new();
    public List<DdlForeignKey> ForeignKeys { get; set; } = new();
}

public class DdlColumn
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool IsKey { get; set; }
}

public class DdlForeignKey
{
    public string Column { get; set; } = "";
    public string RefTable { get; set; } = "";
    public string RefColumn { get; set; } = "";
}

/// <summary>CREATE TABLE 解析（纯逻辑，可测试）：支持 MySQL / PostgreSQL / SQL Server / SQLite / Oracle 常见写法。</summary>
public static class DdlParser
{
    private static readonly Regex CreateTableRegex = new(
        @"CREATE\s+(?:TEMP(?:ORARY)?\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?[`""\[]?(?<name>[\w\u4e00-\u9fa5.$]+)[`""\]]?\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>解析脚本中全部 CREATE TABLE；无命中返回空表。</summary>
    public static List<DdlEntity> Parse(string sql)
    {
        var result = new List<DdlEntity>();
        if (string.IsNullOrWhiteSpace(sql)) return result;
        var matches = CreateTableRegex.Matches(sql);
        foreach (Match m in matches)
        {
            // 表体：从本段 "(" 起到与之配对的 ")"（括号深度扫描，跳过引号）
            int bodyStart = m.Index + m.Length - 1;
            int bodyEnd = FindClosingParen(sql, bodyStart);
            if (bodyEnd < 0) continue;
            var body = sql[(bodyStart + 1)..bodyEnd];
            var entity = new DdlEntity { Name = CleanIdent(m.Groups["name"].Value) };
            ParseBody(body, entity);
            if (entity.Columns.Count > 0) result.Add(entity);
        }
        return result;
    }

    private static int FindClosingParen(string s, int open)
    {
        int depth = 0;
        bool inString = false;
        for (int i = open; i < s.Length; i++)
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
            else if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static void ParseBody(string body, DdlEntity entity)
    {
        foreach (var item in SplitTopLevel(body))
        {
            var t = item.Trim();
            if (t.Length == 0) continue;
            var head = Regex.Match(t, @"^(\w+)", RegexOptions.IgnoreCase);
            var kw = head.Success ? head.Groups[1].Value.ToUpperInvariant() : "";
            switch (kw)
            {
                case "PRIMARY":   // PRIMARY KEY (a, b)
                {
                    var pk = Regex.Match(t, @"PRIMARY\s+KEY\s*\((?<cols>[^)]+)\)", RegexOptions.IgnoreCase);
                    if (pk.Success)
                        foreach (var c in pk.Groups["cols"].Value.Split(','))
                        {
                            var name = CleanIdent(c.Trim());
                            var col = entity.Columns.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                            if (col is not null) col.IsKey = true;
                        }
                    break;
                }
                case "FOREIGN":   // FOREIGN KEY (col) REFERENCES tbl (refcol)
                {
                    var fk = Regex.Match(t, @"FOREIGN\s+KEY\s*\((?<col>[^)]+)\)\s*REFERENCES\s+[`""\[]?(?<tbl>[\w.$]+)[`""\]]?\s*(?:\((?<ref>[^)]+)\))?", RegexOptions.IgnoreCase);
                    if (fk.Success)
                        entity.ForeignKeys.Add(new DdlForeignKey
                        {
                            Column = CleanIdent(fk.Groups["col"].Value),
                            RefTable = CleanIdent(fk.Groups["tbl"].Value),
                            RefColumn = CleanIdent(fk.Groups["ref"].Success ? fk.Groups["ref"].Value : "id"),
                        });
                    break;
                }
                case "CONSTRAINT":
                {
                    // CONSTRAINT 名 PRIMARY KEY(...) / FOREIGN KEY(...) REFERENCES ...
                    var inner = t.Substring(head.Length).Trim();
                    if (inner.StartsWith("PRIMARY", StringComparison.OrdinalIgnoreCase))
                        ParseBody("PRIMARY " + inner["PRIMARY".Length..].Trim(), entity);
                    else if (inner.StartsWith("FOREIGN", StringComparison.OrdinalIgnoreCase))
                        ParseBody("FOREIGN " + inner["FOREIGN".Length..].Trim(), entity);
                    break;
                }
                case "UNIQUE":
                case "KEY":
                case "INDEX":
                case "CHECK":
                    break;   // 索引 / 约束不入模型（模型只收实体与关系）
                default:   // 列定义：name type [约束...]
                {
                    var col = Regex.Match(t, @"^[`""\[]?(?<name>[\w\u4e00-\u9fa5$]+)[`""\]]?\s+(?<type>[\w]+(?:\s*\([^)]*\))?(?:\s+\w+)*)", RegexOptions.IgnoreCase);
                    if (!col.Success) break;
                    var type = Regex.Replace(col.Groups["type"].Value, @"\s+", " ").Trim();
                    entity.Columns.Add(new DdlColumn
                    {
                        Name = CleanIdent(col.Groups["name"].Value),
                        Type = type,
                        IsKey = Regex.IsMatch(t, @"\bPRIMARY\s+KEY\b", RegexOptions.IgnoreCase),
                    });
                    break;
                }
            }
        }
    }

    /// <summary>按顶层逗号切分（括号深度 0 处），引号内不切。</summary>
    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var parts = new List<string>();
        var sb = new System.Text.StringBuilder();
        int depth = 0;
        bool inString = false;
        foreach (var ch in body)
        {
            if (inString)
            {
                sb.Append(ch);
                if (ch == '\'') inString = false;
                continue;
            }
            switch (ch)
            {
                case '\'': inString = true; sb.Append(ch); break;
                case '(': depth++; sb.Append(ch); break;
                case ')': depth--; sb.Append(ch); break;
                case ',' when depth == 0:
                    parts.Add(sb.ToString());
                    sb.Clear();
                    break;
                default: sb.Append(ch); break;
            }
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }

    private static string CleanIdent(string s) => s.Trim().Trim('`', '"', '[', ']', ' ');
}
