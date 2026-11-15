﻿using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>智能线第二批：离线快照包（查询结果+表结构存成单个自包含 JSON，不带原库即可浏览与再聚合）。</summary>

/// <summary>快照内一张表：采样行全部字符串化保证可移植；TotalRows 记录截断前的真实行数。</summary>
public sealed record SnapshotTable(string Name, List<string> Columns, List<List<string?>> Rows, long TotalRows, string? Ddl = null);

/// <summary>快照包：SourceLabel 只留库标识不含凭据；结构=各表 Ddl 文本，结果=Tables。</summary>
public sealed record SnapshotPack(string PackVersion, string Title, string SourceLabel, DateTimeOffset CreatedAt,
    List<SnapshotTable> Tables, string? Notes = null);

public static class SnapshotPackFile
{
    public const string CurrentVersion = "1.0";

    /// <summary>从查询结果建一张快照表：maxRows 截断采样，TotalRows 保留原行数。</summary>
    public static SnapshotTable FromQueryResult(string tableName, QueryResult r, int maxRows = 1000)
    {
        int take = Math.Min(Math.Max(maxRows, 0), r.Rows.Count);
        var rows = r.Rows.Take(take)
            .Select(row => row.Select(Text).ToList())
            .ToList();
        return new SnapshotTable(tableName, r.Columns.ToList(), rows, r.Rows.Count);
    }

    public static SnapshotPack Create(string title, string sourceLabel, IEnumerable<SnapshotTable> tables, string? notes = null) =>
        new(CurrentVersion, title, sourceLabel, DateTimeOffset.UtcNow, tables.ToList(), notes);

    public static string ToJson(SnapshotPack pack) =>
        JsonSerializer.Serialize(pack, new JsonSerializerOptions { WriteIndented = true });

    public static SnapshotPack? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<SnapshotPack>(json); }
        catch (JsonException) { return null; }
        catch (ArgumentNullException) { return null; }
    }

    /// <summary>完整性指纹：对表名、列、行内容做 SHA256；打开快照时重算比对可发现篡改/截断。</summary>
    public static string Fingerprint(SnapshotPack pack)
    {
        var sb = new StringBuilder();
        foreach (var t in pack.Tables.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            sb.Append(t.Name).Append('|').Append(string.Join(",", t.Columns)).Append('|').Append(t.TotalRows).Append('\n');
            foreach (var row in t.Rows)
                sb.Append(string.Join('\u0001', row.Select(v => v ?? "\u0002"))).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>离线浏览：按表名取快照表，找不到返回 null——全程不需要原库。</summary>
    public static SnapshotTable? GetTable(SnapshotPack pack, string name) =>
        pack.Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>快照表还原为 QueryResult，直接复用出图聚合（ChartSpecBuilder）做离线再聚合。</summary>
    public static QueryResult ToQueryResult(SnapshotTable t) => new()
    {
        Title = t.Name,
        Columns = t.Columns.ToList(),
        Rows = t.Rows.Select(row => row.Cast<object?>().ToArray()).ToList(),
        TotalRows = t.TotalRows,
    };

    static string? Text(object? v) => v switch
    {
        null or DBNull => null,
        string s => s,
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };
}
