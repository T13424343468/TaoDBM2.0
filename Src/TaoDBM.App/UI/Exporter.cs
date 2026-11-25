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
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    public static string Format(object? v) => v switch
    {
        null => "",
        byte[] b => Convert.ToHexString(b),
        DBNull => "",
        System.DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
        _ => v.ToString() ?? "",
    };

    /// <summary>整改二批格8：网格显示口径——NULL 显示 (NULL) 而非空白；二进制超 64 字节只显体积（B/KB/MB，阈值 1024 一位小数），
    /// 小 BLOB（≤64B）仍可见 hex；其余走原 Format。CSV/INSERT 导出仍用 Format（空串/hex 口径不变）。</summary>
    public static string DisplayFormat(object? v) => v switch
    {
        null or DBNull => "(NULL)",
        byte[] b => b.Length <= 64 ? Convert.ToHexString(b) : HumanSize(b.Length),
        _ => Format(v),
    };

    static string HumanSize(int n) => n < 1024 ? $"{n} B"
        : n < 1024 * 1024 ? $"{n / 1024.0:0.#} KB"
        : $"{n / 1024.0 / 1024.0:0.#} MB";

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

    /// <summary>
    /// A1 流式导出（CSV）：逐页写 TextWriter，不整包攒串，大结果集内存恒定。
    /// 页序列来自 StreamingQuery / QueryRouter 的分页枚举；返回写出行数。
    /// </summary>
    public static long CsvStream(IEnumerable<Core.Streaming.QueryPage> pages, IReadOnlyList<string> columns, TextWriter w, CancellationToken ct = default)
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

    /// <summary>A1 流式导出（INSERT）：逐页写 TextWriter，不整包攒串；返回写出行数。</summary>
    public static long InsertSqlStream(IEnumerable<Core.Streaming.QueryPage> pages, IReadOnlyList<string> columns, string table, TextWriter w, CancellationToken ct = default)
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
}
