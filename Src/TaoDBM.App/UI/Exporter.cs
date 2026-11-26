using System.Text;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>CSV / INSERT 导出。</summary>
public static class Exporter
{
    public static string Csv(QueryResult r)
    {
        var sb = new StringBuilder();
        sb.Append('\uFEFF'); // UTF-8 BOM，Excel 直接打开不乱码
        sb.AppendLine(string.Join(",", r.Columns.Select(EscapeCsv)));
        foreach (var row in r.Rows)
        {
            var cells = new string[row.Length];
            for (int i = 0; i < row.Length; i++) cells[i] = EscapeCsv(Format(row[i]));
            sb.AppendLine(string.Join(",", cells));
        }
        return sb.ToString();
    }

    private static string EscapeCsv(string? s)
    {
        s ??= "";
        // ㊻ CSV 公式注入防护（OWASP CSV Injection）：=+-@ 开头的单元格在 Excel 打开时会被当公式执行，
        // 统一加前缀 ' 转义；纯数字（含负号/加号开头的数值）不算公式，保持原值不破坏数据口径。
        if (IsFormulaRisk(s)) s = "'" + s;
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    /// <summary>㊻ 公式注入风险判定：= 与 @ 开头一律算公式；+/- 开头仅当其余部分不是纯数字才算（避免负数被误转义）。</summary>
    private static bool IsFormulaRisk(string s)
    {
        if (s.Length == 0) return false;
        return s[0] switch
        {
            '=' or '@' => true,
            '+' or '-' => !double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out _),
            _ => false,
        };
    }

    public static string Format(object? v) => v switch
    {
        null => "",
        byte[] b => Convert.ToHexString(b),
        DBNull => "",
        System.DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
        _ => v.ToString() ?? "",
    };

    /// <summary>㊽ 导出脱敏接线：按列名规则对整包结果打码（与 MaskPreviewDialog 同一套 MaskCell/MaskRow 规则）。
    /// 无敏感列时原样返回不复制；有敏感列时返回打码副本（列名与行数不变，敏感列值按档位打码）。</summary>
    public static QueryResult Masked(QueryResult r)
    {
        if (r.Columns.Count == 0) return r;
        var rules = r.Columns.Select(c => MaskEngine.InferRule(c)).ToList();
        if (rules.All(x => x == MaskRule.None)) return r;
        var masked = new QueryResult { Columns = r.Columns, Title = r.Title };
        foreach (var row in r.Rows)
        {
            var cells = new object?[row.Length];
            for (int i = 0; i < row.Length; i++)
                cells[i] = i < rules.Count && rules[i] != MaskRule.None
                    ? MaskEngine.MaskCell(r.Columns[i], Format(row[i]))
                    : row[i];
            masked.Rows.Add(cells);
        }
        return masked;
    }

    /// <summary>整改二批格8：网格显示口径——NULL 显示 (NULL) 而非空白；二进制超 64 字节只显体积（B/KB/MB，阈值 1024 一位小数），
    /// 小 BLOB（≤64B）仍可见 hex；其余走原 Format。CSV/INSERT 导出仍用 Format（空串/hex 口径不变）。</summary>
    public static string DisplayFormat(object? v) => v switch
    {
        null or DBNull => "(NULL)",
        byte[] b => b.Length <= 64 ? Convert.ToHexString(b) : HumanSize(b.Length),
        _ => Format(v),
    };

    static string HumanSize(int n) => Core.HumanSize.Of(n);

    /// <summary>导出为 INSERT 语句（需要表名）。</summary>
    public static string InsertSql(QueryResult r, string table)
    {
        var sb = new StringBuilder();
        foreach (var row in r.Rows)
        {
            var vals = new string[row.Length];
            for (int i = 0; i < row.Length; i++) vals[i] = SqlValue(row[i]);
            sb.AppendLine($"INSERT INTO {table} ({string.Join(", ", r.Columns)}) VALUES ({string.Join(", ", vals)});");
        }
        return sb.ToString();
    }

    private static string SqlValue(object? v) => v switch
    {
        null or DBNull => "NULL",
        byte[] b => "X'" + Convert.ToHexString(b) + "'",
        bool b => b ? "TRUE" : "FALSE",
        System.DateTime dt => "'" + dt.ToString("yyyy-MM-dd HH:mm:ss") + "'",
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => v.ToString() ?? "NULL",
        _ => "'" + v.ToString()?.Replace("'", "''") + "'",
    };

    /// <summary>把界面输入转成 SQL 字面量：空 → NULL，数字列不加引号，二进制列走 X'hex'，其余转义加引号。</summary>
    public static string Literal(ColumnSpec col, string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "NULL";
        // 发现65（2026-11-25）：二进制列不再当字符串写坏——编辑值须为偶数位十六进制，还原为 X'...' 二进制字面量
        if (IsBinaryColumn(col.DataType)) return BinaryLiteral(input);
        if (CsvImporter.IsNumeric(col.DataType) && double.TryParse(input, System.Globalization.CultureInfo.InvariantCulture, out _))
            return input.Trim();
        return "'" + input.Replace("'", "''") + "'";
    }

    /// <summary>发现65：按列类型判定二进制列（blob/binary/bytea/image/raw 等）。</summary>
    public static bool IsBinaryColumn(string? dataType)
    {
        if (string.IsNullOrEmpty(dataType)) return false;
        var dt = dataType.ToLowerInvariant();
        return dt.Contains("blob") || dt.Contains("binary") || dt.Contains("bytea")
            || dt.Contains("image") || dt.StartsWith("raw") || dt.Contains("bfile")
            || dt.Contains("oleobject") || dt.Contains("general");
    }

    /// <summary>发现65：十六进制串还原为 X'...' 二进制字面量；非法输入（奇数位/非十六进制字符）抛 ArgumentException。</summary>
    public static string BinaryLiteral(string input)
    {
        var hex = input.Trim();
        if (hex.Length == 0 || hex.Length % 2 != 0 || hex.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException($"不是有效的十六进制串（长度 {hex.Length}）");
        return "X'" + hex.ToUpperInvariant() + "'";
    }

    // 发现50：流式导出重入保护——页序列可能共享连接/游标状态，同一时刻只允许一个流式导出
    private static int _streamBusy;

    private static void EnterStream()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _streamBusy, 1, 0) != 0)
            throw new InvalidOperationException("已有导出在进行中，请等它完成后再开始新的导出。");
    }

    private static void ExitStream() => System.Threading.Interlocked.Exchange(ref _streamBusy, 0);

    /// <summary>
    /// A1 流式导出（CSV）：逐页写 TextWriter，不整包攒串，大结果集内存恒定。
    /// 页序列来自 StreamingQuery / QueryRouter 的分页枚举；返回写出行数。
    /// </summary>
    public static long CsvStream(IEnumerable<Core.Streaming.QueryPage> pages, IReadOnlyList<string> columns, TextWriter w, CancellationToken ct = default)
    {
        EnterStream();
        try
        {
            w.Write('\uFEFF'); // UTF-8 BOM，Excel 直接打开不乱码（与整包版一致）
            w.WriteLine(string.Join(",", columns.Select(EscapeCsv)));
            long written = 0;
            foreach (var page in pages)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var row in page.Rows)
                {
                    var cells = new string[row.Length];
                    for (int i = 0; i < row.Length; i++) cells[i] = EscapeCsv(Format(row[i]));
                    w.WriteLine(string.Join(",", cells));
                    written++;
                }
            }
            return written;
        }
        finally { ExitStream(); }
    }

    /// <summary>A1 流式导出（INSERT）：逐页写 TextWriter，不整包攒串；返回写出行数。</summary>
    public static long InsertSqlStream(IEnumerable<Core.Streaming.QueryPage> pages, IReadOnlyList<string> columns, string table, TextWriter w, CancellationToken ct = default)
    {
        EnterStream();
        try
        {
            long written = 0;
            foreach (var page in pages)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var row in page.Rows)
                {
                    var vals = new string[row.Length];
                    for (int i = 0; i < row.Length; i++) vals[i] = SqlValue(row[i]);
                    w.WriteLine($"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", vals)});");
                    written++;
                }
            }
            return written;
        }
        finally { ExitStream(); }
    }

    /// <summary>发现150：JSON 导出（整包）——对象数组形，字符串/数值/布尔/null 分型，控制字符转义。</summary>
    public static string Json(QueryResult r)
    {
        var sb = new StringBuilder();
        JsonStream(new[] { new Core.Streaming.QueryPage { Rows = r.Rows.Select(x => (object[])x).ToArray(), HasMore = false } }, r.Columns, new StringWriter(sb));
        return sb.ToString();
    }

    /// <summary>发现150：JSON 流式导出——逐页写 TextWriter 不整包攒串；返回写出行数。</summary>
    public static long JsonStream(IEnumerable<Core.Streaming.QueryPage> pages, IReadOnlyList<string> columns, TextWriter w, CancellationToken ct = default)
    {
        w.Write('[');
        long written = 0;
        bool firstRow = true;
        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var row in page.Rows)
            {
                if (!firstRow) w.Write(',');
                w.Write("\n{");
                for (int i = 0; i < columns.Count && i < row.Length; i++)
                {
                    if (i > 0) w.Write(',');
                    w.Write(JsonString(columns[i]));
                    w.Write(':');
                    w.Write(JsonValue(row[i]));
                }
                w.Write('}');
                written++;
                firstRow = false;
            }
        }
        w.WriteLine("\n]");
        return written;
    }

    /// <summary>发现150：JSON 值分型——整数原样，浮点/小数走不变区域性，布尔小写，null/DBNull 落 null，其余按字符串转义。</summary>
    private static string JsonValue(object? v) => v switch
    {
        null or DBNull => "null",
        bool b => b ? "true" : "false",
        byte or sbyte or short or ushort or int or uint or long or ulong => v.ToString() ?? "null",
        float or double or decimal => IsFiniteNumber(v) ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "null" : JsonString(Format(v)),
        _ => JsonString(Format(v)),
    };

    private static bool IsFiniteNumber(object v)
    {
        try
        {
            var d = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
            return !double.IsNaN(d) && !double.IsInfinity(d);
        }
        catch { return false; }
    }

    /// <summary>发现150：JSON 字符串转义——引号/反斜杠/控制字符按 RFC 8259（控制字符落 \u00XX）。</summary>
    private static string JsonString(string? s)
    {
        s ??= "";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
