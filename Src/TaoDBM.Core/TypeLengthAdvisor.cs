using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>B12 一键诊断优化引擎：识别字段类型与长度的可优化项，生成可勾选执行的优化语句与配套回退语句。</summary>
/// <remarks>纯引擎免界面稿：元数据与宽度统计由界面层/采集层填充；本引擎只做判定与语句生成，不触库。</remarks>

/// <summary>列元数据：DataType=规范化前的原始类型名，Length=声明宽度/精度（无则为 null 或 0），IsKey=主键或关键列，Engine=引擎名（如 SQLite/MySQL/PostgreSQL）。</summary>
public sealed record ColumnMeta(string Table, string Column, string DataType, int? Length, bool IsKey, string Engine);

/// <summary>列值宽度统计：MaxLen=最长值宽度，AvgLen=平均宽度，NonNullCount=非空样本数。</summary>
public sealed record ColumnLen(long MaxLen, long AvgLen, long NonNullCount, string Table, string Column);

/// <summary>优化发现：Code=规则号（O001 文本超宽 / O002 小数精度 / O003 文本过宽 / O004 时间粒度），Severity=info/warn/danger。
/// Suggestion=建议说明，DraftSql=可勾选执行的优化语句（不可自动执行时为 null），RollbackSql=恢复原类型的回退 SQL。</summary>
public sealed record TypeLengthFinding(string Table, string Column, string Code, string Severity, string Message,
    string? Suggestion = null, string? DraftSql = null, string? RollbackSql = null);

/// <summary>诊断优化报告：Score 为 100 分扣分制（danger -20 / warn -5 / info -1，下限 0）。</summary>
public sealed record TypeLengthReport(int Score, List<TypeLengthFinding> Findings);

public static class TypeLengthAdvisor
{
    /// <summary>引擎支持口径：关系型三引擎可诊断；NoSQL 不适用字段类型/长度口径。</summary>
    public static bool Supported(DbEngine e) => e is DbEngine.MySQL or DbEngine.PostgreSQL or DbEngine.SQLite;

    public static string UnsupportedReason(DbEngine e) => e switch
    {
        DbEngine.MongoDB or DbEngine.Redis => "MongoDB / Redis 无固定的表字段类型与长度口径，不适用本诊断。",
        _ => "该引擎的类型与长度诊断下一批补齐。",
    };

    /// <summary>逐列跑类型与长度优化规则：O001 文本超宽 / O002 小数精度 / O003 文本过宽声明 / O004 时间粒度。</summary>
    public static TypeLengthReport Inspect(IReadOnlyList<ColumnMeta> columns, IReadOnlyList<ColumnLen> lens,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? samples = null)
    {
        var findings = new List<TypeLengthFinding>();
        var byKey = lens.ToDictionary(l => (l.Table, l.Column));
        foreach (var m in columns)
        {
            if (m.IsKey) continue;
            if (!byKey.TryGetValue((m.Table, m.Column), out var len)) continue;
            if (len.NonNullCount <= 0) continue;

            string type = m.DataType.ToUpperInvariant();
            string qt = Q(m.Engine, m.Table), qc = Q(m.Engine, m.Column);
            string orig = OrigType(m);

            // O001 文本长度越界：实际最大宽度超过声明宽度
            if (IsText(type) && m.Length is int decl && decl > 0 && len.MaxLen > decl)
            {
                string suggestion = IsNoEnforce(m.Engine)
                    ? $"实际最长值 {len.MaxLen} 字符，超出声明 {type}({decl})（该引擎不强制截断）：建议改为 TEXT，避免临时加宽成 VARCHAR({len.MaxLen})。"
                    : $"实际最长值 {len.MaxLen} 字符，超出声明宽度 {type}({decl})，严格模式下会截断或报错：请加宽列宽（如 {type}({len.MaxLen + 10})）。";
                findings.Add(new TypeLengthFinding(m.Table, m.Column, "O001", "warn",
                    $"实际最大宽度 {len.MaxLen} 超出声明宽度 {decl}", suggestion,
                    DraftFor(m.Engine, m.Table, m.Column, SuggestType(len.MaxLen)),
                    BuildRollback(m.Engine, m.Table, m.Column, orig)));
            }

            // O002 小数精度：DECIMAL/NUMERIC 建议固定 2 位小数，按实际值宽度收紧
            else if ((type is "DECIMAL" or "NUMERIC" or "NUMBER") && m.Length is int p && p >= 2
                && len.MaxLen > 0 && len.MaxLen <= p + 3)
            {
                string sug = $"建议精度收紧为 {type}({p},2)";
                findings.Add(new TypeLengthFinding(m.Table, m.Column, "O002", "info",
                    $"实际值最大宽度 {len.MaxLen}，可固定两位小数", sug,
                    DraftFor(m.Engine, m.Table, m.Column, $"DECIMAL({p},2)"),
                    BuildRollback(m.Engine, m.Table, m.Column, orig)));
            }

            // O003 文本声明过宽：声明很宽而实际值远小于声明，可收紧省存储
            else if (IsText(type) && m.Length is int d2 && d2 >= 100 && len.MaxLen * 4 <= d2)
            {
                findings.Add(new TypeLengthFinding(m.Table, m.Column, "O003", "info",
                    $"声明 {type}({d2}) 明显宽于实际（最长 {len.MaxLen}）", $"建议收紧为 {type}({Math.Max(1, len.MaxLen * 2)})",
                    DraftSqlFor(m.Engine, m.Table, m.Column, $"{type}({Math.Max(1, len.MaxLen * 2)})"),
                    BuildRollback(m.Engine, m.Table, m.Column, orig)));
            }

            // O004 时间粒度：最长 19 位说明秒以下未使用；10 位说明时间部分全空
            else if (IsTime(type) && (len.MaxLen == 10 || len.MaxLen == 19))
            {
                string sug;
                if (len.MaxLen == 10)
                    sug = $"时间部分未使用，建议改为 DATE";
                else
                {
                    bool allMidnight = false;
                    string key = $"{m.Table}.{m.Column}";
                    if (samples != null && samples.TryGetValue(key, out var s) && s.Count > 0)
                        allMidnight = s.All(v => v.Length >= 19 && v.Substring(11, 8) == "00:00:00");
                    sug = allMidnight
                        ? "时间部分恒为 00:00:00，建议改为 DATE"
                        : "秒以下未使用，建议改为 DATETIME(0)（秒级即可）；若时间部分恒为 00:00:00，可进一步用 DATE。";
                }
                findings.Add(new TypeLengthFinding(m.Table, m.Column, "O004", "info",
                    $"时间粒度可收紧（最长值 {len.MaxLen} 字符）", sug,
                    DraftFor(m.Engine, m.Table, m.Column, len.MaxLen == 10 ? "DATE" : "DATETIME(0)"),
                    BuildRollback(m.Engine, m.Table, m.Column, orig)));
            }
        }
        return new TypeLengthReport(Score(findings), findings);
    }

    /// <summary>优化建议文本：汇总发现与执行/回退路径说明。</summary>
    public static string Report(TypeLengthReport report, bool withRestore)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"诊断优化报告：健康分 {report.Score}/100，共 {report.Findings.Count} 条可优化项。");
        foreach (var f in report.Findings)
            sb.AppendLine($"[{f.Code}|{f.Severity}] {f.Table}.{f.Column}：{f.Message}{(string.IsNullOrEmpty(f.Suggestion) ? "" : $" ⇒ {f.Suggestion}")}");
        if (withRestore)
            sb.Append("在诊断对话框勾选条目后点「执行已选优化」，每次执行都会留痕；万一改完不对劲，点「一键还原」按回退日志恢复原类型。");
        return sb.ToString().TrimEnd();
    }

    public static string ToJson(TypeLengthReport report) =>
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });

    public static TypeLengthReport? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<TypeLengthReport>(json); }
        catch (JsonException) { return null; }
        catch (ArgumentNullException) { return null; }
    }

    /// <summary>列元数据目录查询：统一四列（列名/类型名/文本最大长度或 null/主键标记或空串），引擎方言收敛在此。</summary>
    public static string ColumnCatalogSql(DbEngine e, string db, string table) => e switch
    {
        DbEngine.MySQL =>
            "SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, COLUMN_KEY, NUMERIC_PRECISION FROM information_schema.COLUMNS " +
            $"WHERE TABLE_SCHEMA = '{db.Replace("'", "''")}' AND TABLE_NAME = '{table.Replace("'", "''")}' ORDER BY ORDINAL_POSITION",
        DbEngine.PostgreSQL =>
            "SELECT column_name, data_type, character_maximum_length, NULL, numeric_precision FROM information_schema.columns " +
            $"WHERE table_schema = 'public' AND table_name = '{table.Replace("'", "''")}' ORDER BY ordinal_position",
        DbEngine.SQLite => $"PRAGMA table_info({table.Replace(";", "")})",
        _ => throw new NotSupportedException(UnsupportedReason(e)),
    };

    /// <summary>单列值宽度统计 SQL：两列（最大宽度/平均宽度）加非空计数，共三列。</summary>
    public static string WidthStatSql(DbEngine e, string table, string column) => e switch
    {
        DbEngine.MySQL =>
            $"SELECT COALESCE(MAX(CHAR_LENGTH({Q(e, column)})),0), COALESCE(AVG(CHAR_LENGTH({Q(e, column)})),0), COUNT({Q(e, column)}) FROM {Q(e, table)}",
        DbEngine.PostgreSQL =>
            $"SELECT COALESCE(MAX(LENGTH({Q(e, column)})),0), COALESCE(AVG(LENGTH({Q(e, column)})),0), COUNT({Q(e, column)}) FROM {Q(e, table)}",
        DbEngine.SQLite =>
            $"SELECT COALESCE(MAX(LENGTH({Q(e, column)})),0), COALESCE(AVG(LENGTH({Q(e, column)})),0), COUNT({Q(e, column)}) FROM {Q(e, table)}",
        _ => throw new NotSupportedException(UnsupportedReason(e)),
    };

    /// <summary>声明类型原文解析："VARCHAR(50)" → (VARCHAR, 50)，"datetime" → (DATETIME, null)。</summary>
    public static (string Name, int? Length) ParseDeclared(string raw)
    {
        var s = (raw ?? "").Trim();
        int i = s.IndexOf('(');
        if (i > 0 && s.EndsWith(")"))
        {
            if (int.TryParse(s[(i + 1)..^1].Trim(), out int n)) return (s[..i].Trim().ToUpperInvariant(), n);
        }
        return (s.ToUpperInvariant(), null);
    }

    static bool IsText(string t) => t.Contains("CHAR") || t == "TEXT" || t.Contains("TEXT");

    /// <summary>超宽文本的加宽建议类型：实际最大宽度再加 10 的余量。</summary>
    static string SuggestType(long maxLen) => $"VARCHAR({maxLen + 10})";

    static bool IsTime(string t) => t.Contains("TIMESTAMP") || t is "DATETIME" or "DATETIME2";
    static bool IsNoEnforce(string engine) => engine.Equals("SQLite", StringComparison.OrdinalIgnoreCase);

    static string OrigType(ColumnMeta m) =>
        m.Length is int n && n > 0 ? $"{m.DataType.ToUpperInvariant()}({n})" : m.DataType.ToUpperInvariant();

    /// <summary>按引擎返回可执行改列草稿；SQLite 等无法自动改列的返回 null（仅保留建议说明）。</summary>
    static string? DraftFor(string engine, string table, string column, string newType)
    {
        string et = engine.ToUpperInvariant();
        return et switch
        {
            "MYSQL" => $"ALTER TABLE {Q(engine, table)} MODIFY COLUMN {Q(engine, column)} {newType};",
            "POSTGRESQL" => $"ALTER TABLE {Q(engine, table)} ALTER COLUMN {Q(engine, column)} TYPE {newType};",
            _ => null,
        };
    }

    static string? DraftSqlFor(string engine, string table, string column, string newType) => DraftFor(engine, table, column, newType);

    static string BuildRollback(string engine, string table, string column, string origType)
    {
        string et = engine.ToUpperInvariant();
        return et switch
        {
            "MYSQL" => $"-- 回退：恢复 {table}.{column} 为 {origType}\nALTER TABLE {Q(engine, table)} MODIFY COLUMN {Q(engine, column)} {origType};",
            "POSTGRESQL" => $"-- 回退：恢复 {table}.{column} 为 {origType}\nALTER TABLE {Q(engine, table)} ALTER COLUMN {Q(engine, column)} TYPE {origType};",
            _ => $"-- 回退：恢复 {table}.{column} 为 {origType}\n-- SQLite 改类型需重建表，请按原建表语句处理后回填;",
        };
    }

    static string Q(string engine, string ident) => engine.ToUpperInvariant() switch
    {
        "MYSQL" => "`" + ident.Replace("`", "") + "`",
        _ => "\"" + ident.Replace("\"", "") + "\"",
    };

    static string Q(DbEngine engine, string ident) => Q(engine.ToString(), ident);

    static int Score(List<TypeLengthFinding> findings) => Math.Max(0,
        100 - 20 * findings.Count(f => f.Severity == "danger")
            - 5 * findings.Count(f => f.Severity == "warn")
            - findings.Count(f => f.Severity == "info"));
}
