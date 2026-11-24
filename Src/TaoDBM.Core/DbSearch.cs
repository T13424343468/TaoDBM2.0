namespace TaoDBM.Core;

/// <summary>库范围搜索命中：表 / 列 / 命中行数。</summary>
public sealed class DbSearchHit
{
    public string Table { get; init; } = "";
    public string Column { get; init; } = "";
    public long Hits { get; init; }
}

/// <summary>
/// 批次二格5：数据库范围搜索——选库 → 关键词 → 逐表文本类列 LIKE 计数，出「表/列/命中数」清单。
/// 纯逻辑可测试（SQL 生成与列筛选独立成方法）；执行由调用方（DbSearchDialog）接管，超时/取消口径在调用方。
/// 文本类列口径：char/varchar/text/enum 系（含 nchar/nvarchar/ntext/clob），二进制与数值列不搜。
/// </summary>
public static class DbSearch
{
    /// <summary>判断列是否文本类（可被 LIKE 搜索）。类型名大小写不敏感，无类型信息时按可搜处理。</summary>
    public static bool IsTextColumn(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return true;   // 无类型信息宁多搜不漏搜
        var dt = dataType.ToLowerInvariant();
        return dt.Contains("char") || dt.Contains("text") || dt.Contains("clob")
            || dt.Contains("enum") || dt.Contains("string") || dt == "varchar2" || dt.Contains("json");
    }

    /// <summary>单表单列计数 SQL（方言无关的 LIKE 口径）。
    /// ㊼ LIKE 通配符转义：% _ 与转义符 ! 本身按字面匹配（ESCAPE '!' 子句，各方言通用单字符转义），
    /// 避免「100%」这类关键词被当通配符导致命中数虚高；单引号转义保持原口径。</summary>
    public static string CountSql(string quotedTable, string quotedColumn, string keyword)
    {
        string esc = keyword.Replace("'", "''");
        esc = esc.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
        return $"SELECT COUNT(*) AS hits FROM {quotedTable} WHERE {quotedColumn} LIKE '%{esc}%' ESCAPE '!'";
    }

    /// <summary>逐表逐文本列搜索（每列一条 COUNT，超时/取消由调用方注入 token 与表级超时）。</summary>
    public static async Task<List<DbSearchHit>> SearchAsync(IDbProvider provider, string db,
        IReadOnlyList<ColumnSpec> columns, string table, string keyword,
        CancellationToken token, Action<int>? onColumnDone = null)
    {
        var hits = new List<DbSearchHit>();
        var qt = provider.Quote(table);
        foreach (var col in columns)
        {
            token.ThrowIfCancellationRequested();
            if (!IsTextColumn(col.DataType)) { onColumnDone?.Invoke(1); continue; }
            var sql = CountSql(qt, provider.Quote(col.Name), keyword);
            var rs = await provider.Execute(db, sql);
            long n = 0;
            var first = rs.FirstOrDefault();
            if (first is not null && first.Rows.Count > 0)
                long.TryParse(Convert.ToString(first.Rows[0][0]), out n);
            if (n > 0) hits.Add(new DbSearchHit { Table = table, Column = col.Name, Hits = n });
            onColumnDone?.Invoke(1);
        }
        return hits;
    }
}
