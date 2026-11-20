using System.Text;

namespace TaoDBM.Core.Streaming;

/// <summary>导入限流选项：批次大小与行数上限兜底。</summary>
public sealed class ImportOptions
{
    /// <summary>每批行数，与导出/同步 100 行/批口径对齐。</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>行数上限兜底：解析到该行数即停止并标记截断，
    /// 防止异常超大文件拖垮内存或数据库；≤0 表示不限制。
    /// 口径对齐（对账表「导入分块口径对齐」收口）：默认与 QueryRouter.MaxRowsPerStatement
    /// 同值——导入侧解析行数上限 = 查询侧内存封顶阈值，两侧口径统一由同一常量锚定。</summary>
    public long MaxRows { get; init; } = 200_000;
}

/// <summary>一批待导入数据行。拉取式消费下，内存中任意时刻最多只保留一个批次。</summary>
public sealed class ImportBatch
{
    /// <summary>表头（首个批次携带，后续批次引用同一列表）。</summary>
    public IReadOnlyList<string>? Headers { get; init; }

    /// <summary>本批数据行（≤ BatchSize）。</summary>
    public IReadOnlyList<string?[]> Rows { get; init; } = Array.Empty<string?[]>();

    /// <summary>截至本批累计数据行数（不含表头）。</summary>
    public long CumulativeRows { get; init; }

    /// <summary>达到 MaxRows 上限被截断。</summary>
    public bool Truncated { get; init; }
}

/// <summary>汇入摘要。</summary>
public sealed class ImportSummary
{
    public long TotalRows { get; init; }
    public long BatchCount { get; init; }
    public bool Truncated { get; init; }
}

/// <summary>
/// CSV 行源：从 TextReader 逐字符增量解析，语义与 CsvImporter.Parse 严格一致——
/// BOM 剥离、RFC 4180 引号与跨行值、首个非空行为表头、数据行按表头对齐（不足补 null、超出截断）。
/// 内存中仅保留当前行缓冲，不持有全文。
/// </summary>
public sealed class CsvRowReader
{
    private readonly List<string> _headers = new();

    /// <summary>解析出的表头；读到表头前为空表。</summary>
    public IReadOnlyList<string> Headers => _headers;

    /// <summary>拉取式产出对齐后的数据行。</summary>
    public IEnumerable<string?[]> Rows(TextReader reader)
    {
        var cur = new StringBuilder();
        var rowBuf = new List<string?>();
        bool inQuotes = false, fieldQuoted = false;

        int i = reader.Read();
        if (i == 0xFEFF) i = reader.Read();   // 剥离 BOM

        while (i >= 0)
        {
            char c = (char)i;
            if (inQuotes)
            {
                if (c == '"')
                {
                    i = reader.Read();
                    if (i == '"') { cur.Append('"'); i = reader.Read(); }   // "" 转义
                    else inQuotes = false;
                    continue;
                }
                cur.Append(c);
                i = reader.Read();
                continue;
            }
            if (c == '"') { inQuotes = true; fieldQuoted = true; i = reader.Read(); continue; }
            if (c == ',') { EndField(rowBuf, cur, ref fieldQuoted); i = reader.Read(); continue; }
            if (c == '\n' || c == '\r')
            {
                i = reader.Read();
                if (c == '\r' && i == '\n') i = reader.Read();
                var done = EndRow(rowBuf, cur, ref fieldQuoted, _headers);
                if (done != null) yield return done;
                continue;
            }
            cur.Append(c);
            i = reader.Read();
        }
        if (cur.Length > 0 || rowBuf.Count > 0 || inQuotes)
        {
            var row = EndRow(rowBuf, cur, ref fieldQuoted, _headers);
            if (row != null) yield return row;
        }
    }

    private static void EndField(List<string?> rowBuf, StringBuilder cur, ref bool fieldQuoted)
    {
        rowBuf.Add(fieldQuoted || cur.Length > 0 ? cur.ToString() : null);
        cur.Clear();
        fieldQuoted = false;
    }

    private static string?[]? EndRow(List<string?> rowBuf, StringBuilder cur, ref bool fieldQuoted, List<string> headers)
    {
        EndField(rowBuf, cur, ref fieldQuoted);
        if (rowBuf.Count == 1 && rowBuf[0] is null && headers.Count == 0)
        {
            rowBuf.Clear();
            return null;   // 跳过表头前空行
        }
        if (headers.Count == 0)
        {
            headers.AddRange(rowBuf.Select(c => c ?? ""));
            rowBuf.Clear();
            return null;
        }
        var r = new string?[headers.Count];
        for (int k = 0; k < headers.Count; k++) r[k] = k < rowBuf.Count ? rowBuf[k] : null;
        rowBuf.Clear();
        return r;
    }
}

/// <summary>
/// 导入流式化：替代「全文解析→全量攒语句」的一次性物化路径。
/// 消费方逐批执行即可实现 100 行/批与内存封顶口径对齐；
/// 需要断点续传时用 CsvIngest 落入 ISpillStore。
/// </summary>
public static class ImportStreaming
{
    /// <summary>拉取式产出 CSV 导入批次；达到 MaxRows 停止解析并在末批标记截断。</summary>
    public static IEnumerable<ImportBatch> CsvBatches(TextReader reader, ImportOptions? opts = null)
    {
        opts ??= new ImportOptions();
        int batchSize = Math.Max(1, opts.BatchSize);
        long maxRows = opts.MaxRows;
        long total = 0;
        bool truncated = false;
        var rowReader = new CsvRowReader();
        var batch = new List<string?[]>(batchSize);

        foreach (var row in rowReader.Rows(reader))
        {
            if (maxRows > 0 && total >= maxRows) { truncated = true; break; }
            batch.Add(row);
            total++;
            if (batch.Count >= batchSize)
            {
                yield return new ImportBatch
                {
                    Headers = rowReader.Headers,
                    Rows = batch.ToArray(),
                    CumulativeRows = total,
                };
                batch = new List<string?[]>(batchSize);
            }
        }

        if (truncated || batch.Count > 0)
        {
            yield return new ImportBatch
            {
                Headers = rowReader.Headers,
                Rows = batch.ToArray(),
                CumulativeRows = total,
                Truncated = truncated,
            };
        }
    }

    /// <summary>CSV 汇入溢出暂存：逐批写入 ISpillStore，返回汇总信息；内存中仅保留当前批。</summary>
    public static ImportSummary CsvIngest(TextReader reader, ISpillStore spill, ImportOptions? opts = null)
    {
        long total = 0;
        long batchCount = 0;
        bool truncated = false;
        foreach (var b in CsvBatches(reader, opts))
        {
            if (b.Headers != null) spill.WriteBatch(b.Headers, b.Rows);
            total = b.CumulativeRows;
            batchCount++;
            truncated |= b.Truncated;
        }
        return new ImportSummary { TotalRows = total, BatchCount = batchCount, Truncated = truncated };
    }
}
