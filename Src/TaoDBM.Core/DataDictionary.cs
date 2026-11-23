using System.Globalization;
using System.Text;

namespace TaoDBM.Core;

/// <summary>字段级字典条目：一张表内一列的完整口径。</summary>
public class DictionaryColumn
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool IsKey { get; set; }
    public bool Nullable { get; set; } = true;
    public string? Default { get; set; }
    public string? Comment { get; set; }
}

/// <summary>表级字典条目：表名 + 全部列 + 索引 / 外键段（批次四格7，2026-11-23 补全三段）。</summary>
public class DictionaryTable
{
    public string Name { get; set; } = "";
    public string? Comment { get; set; }
    public List<DictionaryColumn> Columns { get; set; } = new();
    /// <summary>索引段（ObjectTreeCatalog.ListIndexes 口径：表 / 索引名 / 唯一性）。</summary>
    public List<DictionaryIndex> Indexes { get; set; } = new();
    /// <summary>外键段（ObjectTreeCatalog.ListForeignKeys 口径：列 → 引用表.引用列）。</summary>
    public List<DictionaryForeignKey> ForeignKeys { get; set; } = new();
}

/// <summary>字典索引条目。</summary>
public class DictionaryIndex
{
    public string Table { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Unique { get; set; }
}

/// <summary>字典外键条目。</summary>
public class DictionaryForeignKey
{
    public string Table { get; set; } = "";
    public string Column { get; set; } = "";
    public string RefTable { get; set; } = "";
    public string RefColumn { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// 数据字典完整导出引擎（对账表第五章「部分实现→收口」）：
/// 由表结构清单生成字段级字典，HTML 与 Word（WordprocessingML 纯文本 .doc 兼容口径）双格式；
/// 纯逻辑可测试，不连库——列口径由调用方从 ColumnSpec 组装。
/// </summary>
public static class DataDictionary
{
    /// <summary>由表名与列清单组装表级条目（Nullable 缺省 true，调用方可覆盖）。</summary>
    public static DictionaryTable FromSpecs(string table, List<ColumnSpec> cols, string? comment = null) => new()
    {
        Name = table,
        Comment = comment,
        Columns = cols.Select(c => new DictionaryColumn
        {
            Name = c.Name,
            Type = c.DataType,
            IsKey = c.IsKey,
            Nullable = true,
        }).ToList(),
    };

    /// <summary>导出为独立 HTML 数据字典（Ant 风格静态页，逐表分节）。</summary>
    public static string ToHtml(IReadOnlyList<DictionaryTable> tables, string title)
    {
        var pal = ThemePalette.Get(ThemeStore.Load());
        string accent = pal["AntPrimary"], bodyBg = pal["AntBg"], headBg = pal["AntHeadBg"];
        var sb = new StringBuilder();
        sb.Append($@"<!DOCTYPE html><html><head><meta charset=""utf-8""><title>{Esc(title)}</title><style>
body{{font-family:'Segoe UI','Microsoft YaHei',sans-serif;color:#333;margin:24px;background:{bodyBg}}}
h1{{font-size:20px;color:{accent}}} h2{{font-size:16px;color:{accent};margin-top:28px}}
.meta{{color:#666;font-size:12px;margin-bottom:16px}}
table{{border-collapse:collapse;width:100%;font-size:13px;background:#fff;margin-bottom:8px}}
th{{background:{headBg};padding:8px;text-align:left;border-bottom:1px solid #E5E7EB}}
td{{padding:8px;border-bottom:1px solid #E5E7EB}} tr:nth-child(even){{background:#FAFAFA}}
.k{{color:{accent};font-weight:600}} .badge{{display:inline-block;padding:1px 8px;border-radius:10px;font-size:11px;background:{accent};color:#fff;margin-left:6px}}
</style></head><body>
<h1>{Esc(title)}</h1><div class=""meta"">共 {tables.Count} 张表 · 生成时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss} · TaoDBM 数据字典</div>");
        foreach (var t in tables)
        {
            sb.Append($"<h2>{Esc(t.Name)}<span class=\"badge\">{t.Columns.Count} 列</span></h2>");
            if (!string.IsNullOrEmpty(t.Comment)) sb.Append($"<p class=\"meta\">{Esc(t.Comment)}</p>");
            sb.Append("<table><tr><th>列名</th><th>类型</th><th>键</th><th>可空</th><th>默认值</th><th>说明</th></tr>");
            foreach (var c in t.Columns)
            {
                sb.Append($"<tr><td class=\"k\">{Esc(c.Name)}</td><td>{Esc(c.Type)}</td><td>{(c.IsKey ? "🔑 主键" : "")}</td>" +
                    $"<td>{(c.Nullable ? "是" : "否")}</td><td>{Esc(c.Default ?? "-")}</td><td>{Esc(c.Comment ?? "")}</td></tr>");
            }
            sb.Append("</table>");
            if (t.Indexes.Count > 0)
            {
                sb.Append("<p class='meta'>索引（").Append(t.Indexes.Count).Append("）</p>");
                sb.Append("<table><tr><th>索引名</th><th>唯一</th></tr>");
                foreach (var ix in t.Indexes)
                    sb.Append("<tr><td>").Append(Esc(ix.Name)).Append("</td><td>").Append(ix.Unique ? "是" : "否").Append("</td></tr>");
                sb.Append("</table>");
            }
            if (t.ForeignKeys.Count > 0)
            {
                sb.Append("<p class='meta'>外键（").Append(t.ForeignKeys.Count).Append("）</p>");
                sb.Append("<table><tr><th>列</th><th>引用表</th><th>引用列</th><th>约束名</th></tr>");
                foreach (var fk in t.ForeignKeys)
                    sb.Append("<tr><td>").Append(Esc(fk.Column)).Append("</td><td>").Append(Esc(fk.RefTable))
                      .Append("</td><td>").Append(Esc(fk.RefColumn)).Append("</td><td>").Append(Esc(fk.Name)).Append("</td></tr>");
                sb.Append("</table>");
            }
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// 导出为 Word 兼容文档（WordprocessingML 2003 纯 XML 口径，.doc 扩展名 Word/WPS 可直接打开）。
    /// 不依赖第三方包：逐表一节，字段表用 Word 表格。
    /// </summary>
    public static string ToWord(IReadOnlyList<DictionaryTable> tables, string title)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
            "<html xmlns:w=\"urn:schemas-microsoft-com:office:word\"><head><meta charset=\"utf-8\">" +
            "<title>" + EscX(title) + "</title></head><body>");
        sb.Append($"<h1>{EscX(title)}</h1>");
        sb.Append($"<p>共 {tables.Count} 张表 · 生成时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss} · TaoDBM 数据字典</p>");
        foreach (var t in tables)
        {
            sb.Append($"<h2>{EscX(t.Name)}（{t.Columns.Count} 列）</h2>");
            if (!string.IsNullOrEmpty(t.Comment)) sb.Append($"<p>{EscX(t.Comment)}</p>");
            sb.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\" style=\"border-collapse:collapse;font-size:10.5pt\">" +
                "<tr style=\"background:#E8EEF7\"><th><b>列名</b></th><th><b>类型</b></th><th><b>键</b></th>" +
                "<th><b>可空</b></th><th><b>默认值</b></th><th><b>说明</b></th></tr>");
            foreach (var c in t.Columns)
            {
                sb.Append($"<tr><td><b>{EscX(c.Name)}</b></td><td>{EscX(c.Type)}</td><td>{(c.IsKey ? "主键" : "")}</td>" +
                    $"<td>{(c.Nullable ? "是" : "否")}</td><td>{EscX(c.Default ?? "-")}</td><td>{EscX(c.Comment ?? "")}</td></tr>");
            }
            sb.Append("</table>");
            if (t.Indexes.Count > 0)
            {
                sb.Append("<p><b>索引（").Append(t.Indexes.Count).Append("）</b></p>");
                sb.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\" style=\"border-collapse:collapse;font-size:10.5pt\"><tr style=\"background:#E8EEF7\"><th><b>索引名</b></th><th><b>唯一</b></th></tr>");
                foreach (var ix in t.Indexes)
                    sb.Append("<tr><td>").Append(EscX(ix.Name)).Append("</td><td>").Append(ix.Unique ? "是" : "否").Append("</td></tr>");
                sb.Append("</table>");
            }
            if (t.ForeignKeys.Count > 0)
            {
                sb.Append("<p><b>外键（").Append(t.ForeignKeys.Count).Append("）</b></p>");
                sb.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\" style=\"border-collapse:collapse;font-size:10.5pt\"><tr style=\"background:#E8EEF7\"><th><b>列</b></th><th><b>引用表</b></th><th><b>引用列</b></th><th><b>约束名</b></th></tr>");
                foreach (var fk in t.ForeignKeys)
                    sb.Append("<tr><td>").Append(EscX(fk.Column)).Append("</td><td>").Append(EscX(fk.RefTable))
                      .Append("</td><td>").Append(EscX(fk.RefColumn)).Append("</td><td>").Append(EscX(fk.Name)).Append("</td></tr>");
                sb.Append("</table>");
            }
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string Esc(string? s) => (s ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string EscX(string? s) => (s ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
