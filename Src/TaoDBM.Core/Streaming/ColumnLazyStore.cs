namespace TaoDBM.Core.Streaming;

using System.Data.Common;

/// <summary>
/// 宽表列按需加载（批次F，对账表 v7 真缺口）：大宽表场景网格列按需从结果集取列。
/// 首屏只物化可见列（列优先存储），横向滚动到新列再增量拉取；内存占用只随「已加载列数×行数」增长，
/// 不随总列数线性涨。源读取走底层直连（复用 RelationalProvider.OpenStream），不引入中间层。
/// </summary>
public interface IColumnSource
{
    /// <summary>结果集列名全集（仅元数据，代价可忽略）。</summary>
    IReadOnlyList<string> ColumnNames { get; }

    /// <summary>行数；首读前未知为 -1，首读后固定。</summary>
    int RowCount { get; }

    /// <summary>按列索引整列读取（一次源读覆盖本批全部列）；源断流抛异常，已加载列不受影响。</summary>
    Task<IReadOnlyList<object?[]>> ReadColumnsAsync(int[] columnIndexes, CancellationToken ct);
}

/// <summary>
/// 关系库列源：保留原始 SQL，需要新列时重跑同一语句、只读所请求列的索引（DataReader 逐行按索引取值）。
/// 语义口径：适用于确定性查询（表浏览/SELECT）；非确定性语句重跑可能得到新值，由调用方（浏览场景）保证。
/// </summary>
public sealed class RelationalColumnSource : IColumnSource
{
    private readonly RelationalProvider _provider;
    private readonly string? _db;
    private readonly string _sql;
    private readonly IReadOnlyList<string> _columns;
    private int _rowCount = -1;

    public RelationalColumnSource(RelationalProvider provider, string? db, string sql, IReadOnlyList<string> columnNames)
    {
        _provider = provider; _db = db; _sql = sql; _columns = columnNames;
    }

    public IReadOnlyList<string> ColumnNames => _columns;

    public int RowCount => _rowCount;

    public async Task<IReadOnlyList<object?[]>> ReadColumnsAsync(int[] columnIndexes, CancellationToken ct)
    {
        await using DbConnection conn = _provider.OpenStream(_db);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = _sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var buffers = new List<object?>[columnIndexes.Length];
        for (int b = 0; b < buffers.Length; b++) buffers[b] = new List<object?>();
        int rows = 0;
        while (await reader.ReadAsync(ct))
        {
            for (int b = 0; b < columnIndexes.Length; b++)
            {
                int ci = columnIndexes[b];
                buffers[b].Add(ci < reader.FieldCount ? reader.GetValue(ci) : null);
            }
            rows++;
        }
        if (_rowCount < 0) _rowCount = rows;
        return buffers.Select(b => b.ToArray()).ToArray();
    }

    /// <summary>执行首读已确定行数时由路由回填，供 Store 内存口径与网格布局使用。</summary>
    internal void SetRowCount(int rows) { if (_rowCount < 0) _rowCount = rows; }
}

/// <summary>网格用懒加载视图：QueryResult 子类，携带 Store 与首屏可见列集，GridBuilder 据此走懒加载网格。</summary>
public class LazyQueryResult : QueryResult
{
    public ColumnLazyStore Store { get; }
    /// <summary>首屏已物化的列索引（升序）。</summary>
    public IReadOnlyList<int> VisibleCols { get; }

    public LazyQueryResult(ColumnLazyStore store, IReadOnlyList<int> visibleCols, string title, TimeSpan elapsed)
    {
        Store = store; VisibleCols = visibleCols; Title = title; Elapsed = elapsed;
        Columns.AddRange(visibleCols.Select(i => store.ColumnNames[i]));
        Affected = -1;
        // 可见列按行优先平铺：行数/导出/执行账等既有消费方零改动即可工作
        for (int r = 0; r < store.RowCount; r++)
        {
            var row = new object?[visibleCols.Count];
            for (int c = 0; c < visibleCols.Count; c++) row[c] = store.GetValue(r, visibleCols[c]);
            Rows.Add(row);
        }
    }

    /// <summary>把已加载列物化为行优先 QueryResult（导出/分析菜单兼容路径；未加载列不在视图内）。</summary>
    public QueryResult Materialize(IReadOnlyList<int>? cols = null)
    {
        var pick = (cols ?? VisibleCols).ToList();
        var qr = new QueryResult { Title = Title, Elapsed = Elapsed };
        qr.Columns.AddRange(pick.Select(i => Store.ColumnNames[i]));
        for (int r = 0; r < Store.RowCount; r++)
        {
            var row = new object?[pick.Count];
            for (int c = 0; c < pick.Count; c++) row[c] = Store.GetValue(r, pick[c]);
            qr.Rows.Add(row);
        }
        return qr;
    }
}

public sealed class ColumnLazyStore
{
    private readonly IColumnSource _source;
    private readonly object _gate = new();
    private readonly object?[][] _data;      // 列优先：_data[col] = 该列整行值
    private readonly bool[] _loaded;
    private readonly Dictionary<int, Task> _inflight = new();

    public ColumnLazyStore(IColumnSource source)
    {
        _source = source;
        _data = new object?[source.ColumnNames.Count][];
        _loaded = new bool[source.ColumnNames.Count];
    }

    public IReadOnlyList<string> ColumnNames => _source.ColumnNames;
    public int TotalColumns => _source.ColumnNames.Count;
    public int RowCount => _source.RowCount;

    /// <summary>已物化列数；内存口径的列维度。</summary>
    public int LoadedCount { get; private set; }

    /// <summary>已物化单元格总数＝Σ(已加载列长)；首屏后＝可见列数×行数，不随总列数增长。</summary>
    public long LoadedCells { get; private set; }

    public bool IsLoaded(int col)
    {
        lock (_gate) return col >= 0 && col < _loaded.Length && _loaded[col];
    }

    /// <summary>把执行首读已取到的列直接落位（避免同列二次源读）；仅初始化期使用，列须未加载。</summary>
    public void SeedColumns(int[] colIndexes, object?[][] chunks)
    {
        lock (_gate)
        {
            for (int b = 0; b < colIndexes.Length; b++)
            {
                int ci = colIndexes[b];
                if (_loaded[ci] || chunks[b].Length < (_source.RowCount < 0 ? 0 : _source.RowCount)) continue;
                _data[ci] = chunks[b];
                _loaded[ci] = true;
                LoadedCount++;
                LoadedCells += chunks[b].Length;
            }
        }
    }

    /// <summary>
    /// 确保指定列已加载。重复滚动（与已加载/在途列重叠）自动去重：全命中零源读，部分命中只拉缺失列；
    /// 同列并发请求合并在途任务，不重复源读。源断流时已加载列保持有效，可重试恢复。
    /// </summary>
    public async Task EnsureColumnsAsync(IEnumerable<int> colIndexes, CancellationToken ct = default)
    {
        List<Task> waits = new();
        lock (_gate)
        {
            int[] want = colIndexes
                .Where(i => i >= 0 && i < _loaded.Length && !_loaded[i] && !_inflight.ContainsKey(i))
                .Distinct().ToArray();
            if (want.Length > 0)
            {
                var fetch = FetchAndStoreAsync(want, ct);
                foreach (int ci in want) _inflight[ci] = fetch;
                if (fetch.IsCompleted)
                {
                    // 同步早退（源同步抛出/立即完成）时 FetchAndStoreAsync 的 finally 先于注册执行，
                    // 这里补清理，避免僵尸在途任务堵死后续重试
                    foreach (int ci in want) _inflight.Remove(ci);
                }
                waits.Add(fetch);
            }
            // 与在途列重叠的请求合并在途任务，不重复源读
            foreach (int ci in colIndexes)
                if (ci >= 0 && ci < _loaded.Length && !_loaded[ci] && _inflight.TryGetValue(ci, out var t) && !waits.Contains(t))
                    waits.Add(t);
        }
        if (waits.Count > 0) await Task.WhenAll(waits);
    }

    private async Task FetchAndStoreAsync(int[] cols, CancellationToken ct)
    {
        try
        {
            var chunks = await _source.ReadColumnsAsync(cols, ct);
            lock (_gate)
            {
                for (int b = 0; b < cols.Length; b++)
                {
                    int ci = cols[b];
                    if (_loaded[ci]) continue;
                    _data[ci] = chunks[b];
                    _loaded[ci] = true;
                    LoadedCount++;
                    LoadedCells += chunks[b].Length;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                foreach (int ci in cols) _inflight.Remove(ci);
            }
        }
    }

    /// <summary>取已加载列的单元格值；未加载列抛 InvalidOperationException（网格只渲染已加载列）。</summary>
    public object? GetValue(int row, int col)
    {
        lock (_gate)
        {
            if (!_loaded[col])
                throw new InvalidOperationException($"列 {ColumnNames[col]} 未加载，先 EnsureColumnsAsync。");
            return _data[col][row];
        }
    }

    /// <summary>首屏可见列集：前 n 列（网格首屏从最左开始横向布局）。</summary>
    public static IReadOnlyList<int> InitialVisible(int totalColumns, int visibleCount) =>
        Enumerable.Range(0, Math.Clamp(visibleCount, 0, totalColumns)).ToArray();
}
