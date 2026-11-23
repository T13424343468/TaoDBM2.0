// Excel（xlsx）导出 / 导入：零第三方包，System.IO.Compression + XmlWriter/XmlReader 自实现 SpreadsheetML。
// 批次四格1（2026-11-23）：导出复用流式底座逐页写 Sheet（内存恒定，1200MB 护栏内）；
// 导入读共享字符串表 + 内联串，首行表头，类型推断复用 CsvImporter 口径，公式单元格读缓存值。
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace TaoDBM.Core;

/// <summary>xlsx 写入：最小 SpreadsheetML 包（Content_Types / rels / workbook / sheet1），字符串走内联串免共享表。</summary>
public static class ExcelWriter
{
    /// <summary>xlsx 单工作表行上限（ECMA-376）。</summary>
    public const long MaxRowsPerSheet = 1_048_576;

    /// <summary>整包写（内存行集，小结果集用）。</summary>
    public static long Write(string path, IReadOnlyList<string> columns, IEnumerable<object?[]> rows, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        return Write(fs, columns, rows, ct);
    }

    /// <summary>整包写（流）。</summary>
    public static long Write(Stream output, IReadOnlyList<string> columns, IEnumerable<object?[]> rows, CancellationToken ct = default)
        => WriteCore(output, columns, Enumerate(rows), ct);

    /// <summary>流式写：逐页枚举（StreamingQuery / ColumnLazyStore 分页），内存恒定。</summary>
    public static long WriteStream(string path, IReadOnlyList<string> columns, IEnumerable<Core.Streaming.QueryPage> pages, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        return WriteCore(fs, columns, PagesToRows(pages, ct), ct);
    }

    private static IEnumerable<object?[]> Enumerate(IEnumerable<object?[]> rows) => rows;

    private static IEnumerable<object?[]> PagesToRows(IEnumerable<Core.Streaming.QueryPage> pages, CancellationToken ct)
    {
        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var row in page.Rows) yield return row;
        }
    }

    private static long WriteCore(Stream output, IReadOnlyList<string> columns, IEnumerable<object?[]> rows, CancellationToken ct)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
        WriteEntry(zip, "[Content_Types].xml",
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "</Types>");
        WriteEntry(zip, "_rels/.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");
        WriteEntry(zip, "xl/workbook.xml",
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        WriteEntry(zip, "xl/_rels/workbook.xml.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "</Relationships>");

        long written = 0;
        var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
        using (var es = entry.Open())
        using (var xw = XmlWriter.Create(es, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Async = false, NewLineHandling = NewLineHandling.None }))
        {
            xw.WriteStartDocument();
            xw.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            xw.WriteStartElement("sheetData");
            // 首行表头
            xw.WriteStartElement("row");
            xw.WriteAttributeString("r", "1");
            for (int c = 0; c < columns.Count; c++) WriteCell(xw, c, 0, columns[c]);
            xw.WriteEndElement();
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                written++;
                if (written + 1 > MaxRowsPerSheet)
                    throw new InvalidOperationException($"xlsx 单工作表上限 {MaxRowsPerSheet:N0} 行，超出部分未写入。");
                xw.WriteStartElement("row");
                xw.WriteAttributeString("r", (written + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                for (int c = 0; c < row.Length && c < columns.Count; c++)
                {
                    if (row[c] is null or DBNull) continue;   // NULL 单元格缺省即空
                    WriteCell(xw, c, written, row[c]!);
                }
                xw.WriteEndElement();
            }
            xw.WriteEndElement();   // sheetData
            xw.WriteEndElement();   // worksheet
            xw.WriteEndDocument();
            xw.Flush();
        }
        return written;
    }

    private static void WriteCell(XmlWriter xw, int colIdx, long rowIdx, object v)
    {
        xw.WriteStartElement("c");
        xw.WriteAttributeString("r", ColumnName(colIdx) + (rowIdx + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        switch (v)
        {
            case bool b:
                xw.WriteAttributeString("t", "b");
                xw.WriteElementString("v", b ? "1" : "0");
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                xw.WriteElementString("v", Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "0");
                break;
            default:
                xw.WriteAttributeString("t", "inlineStr");
                xw.WriteStartElement("is");
                xw.WriteElementString("t", XmlExporter.Fmt(v));
                xw.WriteEndElement();
                break;
        }
        xw.WriteEndElement();
    }

    /// <summary>0 → A，25 → Z，26 → AA（ECMA-376 列号）。</summary>
    public static string ColumnName(int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        var sb = new StringBuilder();
        int n = index;
        while (n >= 26)
        {
            sb.Insert(0, (char)('A' + n % 26));
            n = n / 26 - 1;
        }
        sb.Insert(0, (char)('A' + n));
        return sb.ToString();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}

/// <summary>xlsx 读取：首行表头 + 数据行（字符串口径，与 CsvImporter.Parse 对齐，供 BuildInserts 复用）。</summary>
public static class ExcelReader
{
    /// <summary>读首个工作表：返回表头与数据行。公式单元格读缓存值（t="str"/默认 v 文本）。</summary>
    public static (List<string> Headers, List<string?[]> Rows) Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
        var sheet = ReadFirstSheetPath(zip);
        var shared = ReadSharedStrings(zip);

        var entry = zip.GetEntry(sheet) ?? throw new InvalidDataException("xlsx 缺少工作表内容。");
        using var es = entry.Open();
        using var xr = XmlReader.Create(es, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true });

        var headers = new List<string>();
        var rows = new List<string?[]>();
        List<string?>? curRow = null;
        while (xr.Read())
        {
            if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "row")
            {
                curRow = new List<string?>();
            }
            else if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "c" && curRow is not null)
            {
                string? cellType = xr.GetAttribute("t");
                // 列号：按 r 属性定位（缺省按顺序），保证稀疏行对齐
                string? cellRef = xr.GetAttribute("r");
                int colIndex = curRow.Count;
                if (!string.IsNullOrEmpty(cellRef))
                {
                    int parsed = ColumnIndexFromRef(cellRef);
                    if (parsed >= 0) colIndex = parsed;
                }
                string? value = ReadCellValue(xr, cellType, shared);
                while (curRow.Count <= colIndex) curRow.Add(null);
                curRow[colIndex] = value;
            }
            else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "row" && curRow is not null)
            {
                if (headers.Count == 0)
                {
                    headers.AddRange(curRow.Select(c => c ?? ""));
                    // 全空表头行视为空行跳过
                    if (headers.All(string.IsNullOrWhiteSpace)) { headers.Clear(); }
                }
                else rows.Add(curRow.ToArray());
                curRow = null;
            }
        }
        // 列数对齐表头，不足补 null，超出截断（与 CsvImporter 口径一致）
        var aligned = new List<string?[]>();
        foreach (var r in rows)
        {
            var a = new string?[headers.Count];
            for (int i = 0; i < headers.Count && i < r.Length; i++) a[i] = r[i];
            aligned.Add(a);
        }
        return (headers, aligned);
    }

    private static string? ReadCellValue(XmlReader xr, string? cellType, List<string> shared)
    {
        // 子元素：<v>（值/公式缓存）或 <is><t>（内联串）。
        // 用 ReadSubtree 限定在 <c> 子树内读，避免 ReadElementContentAsString 推进过界串读后续单元格。
        string? vText = null, inlineText = null;
        using (var sub = xr.ReadSubtree())
        {
            sub.Read();   // 定位到 <c>
            while (sub.Read())
            {
                if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "v")
                    vText = sub.ReadElementContentAsString();
                else if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "is")
                {
                    var sb = new StringBuilder();
                    using (var isSub = sub.ReadSubtree())
                    {
                        isSub.Read();   // 定位到 <is>
                        while (isSub.Read())
                        {
                            if (isSub.NodeType == XmlNodeType.Element && isSub.LocalName == "t")
                                sb.Append(isSub.ReadElementContentAsString());
                        }
                    }
                    inlineText = sb.ToString();
                }
            }
        }
        return cellType switch
        {
            "s" => int.TryParse(vText, out int i) && i >= 0 && i < shared.Count ? shared[i] : vText,
            "inlineStr" => inlineText,
            "b" => vText == "1" ? "1" : "0",
            _ => vText,   // 公式缓存（t="str"）与数值/默认均读 v 文本
        };
    }

    private static string ReadFirstSheetPath(ZipArchive zip)
    {
        var wb = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("xlsx 缺少 workbook.xml。");
        string rid;
        using (var s = wb.Open())
        using (var xr = XmlReader.Create(s))
        {
            rid = "";
            while (xr.Read())
            {
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "sheet")
                {
                    rid = xr.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") ?? "";
                    break;
                }
            }
        }
        var rels = zip.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidDataException("xlsx 缺少 workbook 关系表。");
        using (var s = rels.Open())
        using (var xr = XmlReader.Create(s))
        {
            while (xr.Read())
            {
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "Relationship" && xr.GetAttribute("Id") == rid)
                {
                    var target = xr.GetAttribute("Target") ?? "worksheets/sheet1.xml";
                    if (target.StartsWith("/", StringComparison.Ordinal)) return target.TrimStart('/');
                    return "xl/" + target.TrimStart('.', '/');
                }
            }
        }
        return "xl/worksheets/sheet1.xml";
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var e = zip.GetEntry("xl/sharedStrings.xml");
        if (e is null) return list;
        using var s = e.Open();
        using var xr = XmlReader.Create(s);
        var sb = new StringBuilder();
        while (xr.Read())
        {
            if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "si")
            {
                sb.Clear();
                int depth = xr.Depth;
                while (xr.Read())
                {
                    if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t") sb.Append(xr.ReadElementContentAsString());
                    else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "si" && xr.Depth == depth) break;
                }
                list.Add(sb.ToString());
            }
        }
        return list;
    }

    /// <summary>"A1" → 0，"AB12" → 27（列部分）。</summary>
    public static int ColumnIndexFromRef(string cellRef)
    {
        int n = 0;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') n = n * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') n = n * 26 + (ch - 'a' + 1);
            else break;
        }
        return n - 1;
    }
}
