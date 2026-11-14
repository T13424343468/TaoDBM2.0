using System.Text;

namespace TaoDBM.Core;

/// <summary>结构同步 + 数据传输（跨库 ETL）的纯逻辑部分。</summary>
public static class DataSync
{
    /// <summary>按主表结构生成目标侧补齐语句：只加缺失列，不删列；
    /// alignTypes=true 时同时对齐共有列类型（有覆盖风险，默认关）。</summary>
    public static (List<string> Statements, List<string> Skipped) SyncColumns(
        string table, List<ColumnSpec> sourceCols, List<string> targetColNames,
        DbEngine targetEngine, Func<string, string> quote, bool alignTypes)
    {
        var design = TableDesign.FromSpecs(table, sourceCols);
        // 只让缺失列进入 ADD 流程
        var missing = design.Columns
            .Where(c => !targetColNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var addOnly = new TableDesign { TableName = table, Columns = missing };
        var (stmts, skipped) = TableDdl.BuildAlter(addOnly, Enumerable.Empty<string>(), targetEngine, quote,
            modifyExisting: false, dropMissing: false);
        if (alignTypes)
        {
            var shared = design.Columns
                .Where(c => targetColNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            var sharedDesign = new TableDesign { TableName = table, Columns = shared };
            var (mods, sk2) = TableDdl.BuildAlter(sharedDesign, targetColNames, targetEngine, quote,
                modifyExisting: true, dropMissing: false);
            stmts.AddRange(mods);
            skipped.AddRange(sk2);
        }
        return (stmts, skipped);
    }

    /// <summary>目标表不存在时，按源结构 + 目标方言生成 CREATE TABLE。</summary>
    public static string CreateForTarget(string table, List<ColumnSpec> sourceCols,
        DbEngine targetEngine, Func<string, string> quote)
        => TableDdl.BuildCreate(TableDesign.FromSpecs(table, sourceCols), targetEngine, quote);

    /// <summary>把查询结果转成目标侧批量 INSERT，每批 batch 行一个语句元素（列名经 quote 引用）。</summary>
    public static List<string> InsertBatches(QueryResult r, string quotedTable,
        Func<string, string> quote, int batch = 200)
    {
        var result = new List<string>();
        if (r.Rows.Count == 0) return result;
        var names = r.Columns.Select(quote).ToList();
        var sb = new StringBuilder();
        int inBatch = 0;
        foreach (var row in r.Rows)
        {
            var vals = new string[row.Length];
            for (int i = 0; i < row.Length; i++)
                vals[i] = SqlValue(row[i]);
            sb.Append($"INSERT INTO {quotedTable} ({string.Join(", ", names)}) VALUES ({string.Join(", ", vals)});\n");
            if (++inBatch >= batch)
            {
                result.Add(sb.ToString());
                sb.Clear();
                inBatch = 0;
            }
        }
        if (inBatch > 0) result.Add(sb.ToString());
        return result;
    }

    /// <summary>SQL 字面量：NULL / 二进制 X'' / 布尔 / 日期 / 数字原样 / 字符串转义。</summary>
    public static string SqlValue(object? v) => v switch
    {
        null or DBNull => "NULL",
        byte[] b => "X'" + Convert.ToHexString(b) + "'",
        bool b => b ? "TRUE" : "FALSE",
        System.DateTime dt => "'" + dt.ToString("yyyy-MM-dd HH:mm:ss") + "'",
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => v.ToString() ?? "NULL",
        _ => "'" + v.ToString()?.Replace("'", "''") + "'",
    };
}

/// <summary>
/// A1 同步断点水位：记录某表传输进度（已完整落库的批次数）。
/// 中断后凭水位续传，只补剩余批次，已落库数据不重灌。
/// 持久化由调用方走 AtomicWrite（本类保持纯逻辑，不碰文件）。
/// </summary>
public sealed class SyncWatermark
{
    public string Table { get; init; } = "";
    public int BatchDone { get; init; }              // 已完整落库的批次数（断点）
    public string LastKey { get; init; } = "";       // 水位键（源表有序键值，信息性）
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public static SyncWatermark Empty(string table) => new() { Table = table, BatchDone = 0, LastKey = "" };
}

public static class SyncCheckpoint
{
    /// <summary>续传：跳过水位之前的批次，返回剩余待执行语句；每发一批回调最新水位供调用方持久化。</summary>
    public static IReadOnlyList<string> Resume(IReadOnlyList<string> batches, SyncWatermark w,
        Action<SyncWatermark>? onBatchDone = null)
    {
        var rest = new List<string>();
        for (int i = w.BatchDone; i < batches.Count; i++)
        {
            rest.Add(batches[i]);
            onBatchDone?.Invoke(new SyncWatermark
            {
                Table = w.Table, BatchDone = i + 1, LastKey = w.LastKey,
            });
        }
        return rest;
    }

    /// <summary>水位回读容错：JSON 合法则还原，损坏/为空返回零值水位（不抛，同步可从头重来）。</summary>
    public static SyncWatermark FailSafeParse(string table, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return SyncWatermark.Empty(table);
        try
        {
            var w = System.Text.Json.JsonSerializer.Deserialize<SyncWatermark>(json);
            return w ?? SyncWatermark.Empty(table);
        }
        catch (System.Text.Json.JsonException) { return SyncWatermark.Empty(table); }
    }

    /// <summary>水位序列化（调用方拿去走 AtomicWrite 落盘）。</summary>
    public static string ToJson(SyncWatermark w) =>
        System.Text.Json.JsonSerializer.Serialize(w);
}
