// XML / HTML 导出（批次四格2，2026-11-23）：Utf8XmlWriter 流式逐页写，NULL 写 xsi:nil；
// HTML 内联样式表格，NULL 显 (NULL)、二进制显体积（网格显示口径 DisplayFormat）。零第三方包。
using System.Text;
using System.Xml;

namespace TaoDBM.Core;

/// <summary>XML 导出：根 result，columns 声明列名，行元素 row 子元素为列名（非法字符折叠为下划线）。</summary>
public static class XmlExporter
{
    /// <summary>整包写（小结果集）。</summary>
    public static string Write(QueryResult r)
    {
        using var sw = new StringWriter();
        WriteStream(new[] { new Core.Streaming.QueryPage { Rows = r.Rows.Cast<object[]>().ToArray(), HasMore = false } }, r.Columns, sw);
        return sw.ToString();
    }

    /// <summary>流式写：逐页枚举，内存恒定；返回写出行数。</summary>
    public static long WriteStream(IEnumerable<Core.Streaming.QueryPage> pages, IReadOnlyList<string> columns, TextWriter w, CancellationToken ct = default)
    {
        var names = columns.Select(SafeName).ToList();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            NewLineHandling = NewLineHandling.None,
        };
        long written = 0;
        using (var xw = XmlWriter.Create(w, settings))
        {
            xw.WriteStartDocument();
            xw.WriteStartElement("result");
            xw.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            xw.WriteStartElement("columns");
            for (int i = 0; i < columns.Count; i++)
            {
                xw.WriteStartElement("column");
                xw.WriteAttributeString("name", columns[i]);
                xw.WriteAttributeString("element", names[i]);
                xw.WriteEndElement();
            }
            xw.WriteEndElement();
            xw.WriteStartElement("rows");
            foreach (var page in pages)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var row in page.Rows)
                {
                    xw.WriteStartElement("row");
                    for (int c = 0; c < columns.Count; c++)
                    {
                        var v = c < row.Length ? row[c] : null;
                        if (v is null or DBNull)
                        {
                            xw.WriteStartElement(names[c]);
                            xw.WriteAttributeString("xsi", "nil", "http://www.w3.org/2001/XMLSchema-instance", "true");
                            xw.WriteEndElement();
                        }
                        else
                        {
                            xw.WriteElementString(names[c], Fmt(v));
                        }
                    }
                    xw.WriteEndElement();
                    written++;
                }
            }
            xw.WriteEndElement();   // rows
            xw.WriteEndElement();   // result
            xw.WriteEndDocument();
        }
        return written;
    }

    /// <summary>列名 → 合法 XML 元素名：空白/标点折叠为下划线，数字开头补前缀，空名补 col 序号。</summary>
    public static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.') sb.Append(ch);
            else sb.Append('_');
        }
        var s = sb.ToString();
        if (s.Length == 0) return "col";
        if (char.IsDigit(s[0])) s = "_" + s;
        return s;
    }

    /// <summary>值 → 文本（导出口径：二进制 hex、日期 yyyy-MM-dd HH:mm:ss，同 App Exporter.Format，Core 侧独立实现）。</summary>
    internal static string Fmt(object v) => v switch
    {
        byte[] b => Convert.ToHexString(b),
        System.DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
        _ => v.ToString() ?? "",
    };

    /// <summary>显示口径：大二进制（&gt;64B）只显体积，其余同 Fmt（同 App Exporter.DisplayFormat）。</summary>
    internal static string Disp(object v) => v is byte[] b && b.Length > 64
        ? HumanSize.Of(b.Length)
        : Fmt(v);
}

/// <summary>HTML 导出：内联样式表格（Ant 风格静态页），NULL 显 (NULL)、二进制显体积。</summary>
public static class HtmlExporter
{
    /// <summary>整包写（导出走整包口径；流式大结果集建议先落 xlsx/CSV）。</summary>
    public static string Write(QueryResult r, string title)
    {
        var sb = new StringBuilder();
        sb.Append(@"<!DOCTYPE html><html><head><meta charset=""utf-8""><title>").Append(Esc(title)).Append(@"</title><style>
body{font-family:'Segoe UI','Microsoft YaHei',sans-serif;color:#333;margin:24px;background:#F5F6F8}
h1{font-size:18px} .meta{color:#666;font-size:12px;margin-bottom:12px}
table{border-collapse:collapse;width:100%;font-size:13px;background:#fff}
th{background:#E8EEF7;padding:8px;text-align:left;border-bottom:1px solid #D9DEE7}
td{padding:8px;border-bottom:1px solid #E5E7EB} tr:nth-child(even){background:#FAFAFA}
.null{color:#999}
</style></head><body><h1>").Append(Esc(title)).Append("</h1>");
        sb.Append($"<div class=\"meta\">{r.Rows.Count:N0} 行 · {r.Columns.Count} 列 · 生成时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss} · TaoDBM 导出</div>");
        sb.Append("<table><tr>");
        foreach (var c in r.Columns) sb.Append("<th>").Append(Esc(c)).Append("</th>");
        sb.Append("</tr>");
        foreach (var row in r.Rows)
        {
            sb.Append("<tr>");
            for (int c = 0; c < r.Columns.Count; c++)
            {
                var v = c < row.Length ? row[c] : null;
                if (v is null or DBNull) sb.Append("<td class=\"null\">(NULL)</td>");
                else sb.Append("<td>").Append(Esc(XmlExporter.Disp(v))).Append("</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</table></body></html>");
        return sb.ToString();
    }

    private static string Esc(string? s) => (s ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>流式写：逐页枚举（懒加载结果导出用），内存恒定；返回写出行数。</summary>
    public static long WriteStream(IEnumerable<Core.Streaming.QueryPage> pages, IReadOnlyList<string> columns, string title, TextWriter w, CancellationToken ct = default)
    {
        w.Write(@"<!DOCTYPE html><html><head><meta charset=""utf-8""><title>");
        w.Write(Esc(title));
        w.Write(@"</title><style>
body{font-family:'Segoe UI','Microsoft YaHei',sans-serif;color:#333;margin:24px;background:#F5F6F8}
h1{font-size:18px} .meta{color:#666;font-size:12px;margin-bottom:12px}
table{border-collapse:collapse;width:100%;font-size:13px;background:#fff}
th{background:#E8EEF7;padding:8px;text-align:left;border-bottom:1px solid #D9DEE7}
td{padding:8px;border-bottom:1px solid #E5E7EB} tr:nth-child(even){background:#FAFAFA}
.null{color:#999}
</style></head><body><h1>");
        w.Write(Esc(title));
        w.Write("</h1><div class=\"meta\">生成时间 ");
        w.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        w.Write(" · TaoDBM 导出</div><table><tr>");
        foreach (var c in columns) { w.Write("<th>"); w.Write(Esc(c)); w.Write("</th>"); }
        w.Write("</tr>");
        long written = 0;
        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var row in page.Rows)
            {
                w.Write("<tr>");
                for (int c = 0; c < columns.Count; c++)
                {
                    var v = c < row.Length ? row[c] : null;
                    if (v is null or DBNull) w.Write("<td class=\"null\">(NULL)</td>");
                    else { w.Write("<td>"); w.Write(Esc(XmlExporter.Disp(v))); w.Write("</td>"); }
                }
                w.Write("</tr>");
                written++;
            }
        }
        w.Write("</table></body></html>");
        return written;
    }
}
