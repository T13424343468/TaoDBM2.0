namespace TaoDBM.Core;

/// <summary>列拉取委托：给定列索引，返回该列整列值（行序与 QueryResult.Rows 对齐）。App 层可按连接能力实现
/// 「重查该列」（SELECT col FROM (原查询) 一列流式拉取）；Core 测试用数组切片直供。</summary>
public interface IColumnFetcher
{
    /// <summary>拉取整列。实现方保证与行数一致的返回；异常向上抛由调用方降级全列物化。</summary>
    object?[] FetchColumn(int columnIndex);
}

/// <summary>
/// 宽表列按需加载（二期对账缺口，批次 F，2026-11-20 老板令「全覆盖」）：
/// 宽表查询结果不再一次性物化全部列——首屏只物化可视列，横向滚动/取值按需拉取该列，
/// LRU 列缓存 + 内存封顶（与既有内存封顶同口径：单元数计量），超顶淘汰最久未用列。
/// 纯逻辑、无 UI 依赖；线程不安全（UI 线程访问，与结果网格同口径）。
/// </summary>
public sealed class ColumnLazyStore
{
    /// <summary>触发按需列模式的宽表阈值：列数超过此值走懒加载（与网格 64 列上限同档）。</summary>
    public const int WideColumnThreshold = 64;

    /// <summary>默认内存封顶（单元数）：约等于 2000 行 × 64 列，与既有整包截断档对齐。</summary>
    public const int DefaultCellBudget = 128_000;

    private readonly int _columnCount;
    private readonly int _rowCount;
    private readonly int _cellBudget;
    private readonly IColumnFetcher? _fetcher;
    private readonly object?[]?[] _materialized;   // 与全列物化对齐的槽位；null=未物化
    private readonly List<int> _lru = new();              // 最近使用列索引，末尾最新
    private int _usedCells;

    /// <summary>构造：rows 可空（懒加载模式走 fetcher）；传 rows 时列数据零拷贝复用（窄表/全物化场景零开销）。</summary>
    public ColumnLazyStore(int columnCount, int rowCount, object?[]?[]? materializedColumns = null,
        IColumnFetcher? fetcher = null, int cellBudget = DefaultCellBudget)
    {
        if (columnCount < 0) throw new ArgumentOutOfRangeException(nameof(columnCount));
        if (rowCount < 0) throw new ArgumentOutOfRangeException(nameof(rowCount));
        if (cellBudget < columnCount * Math.Max(rowCount, 1) && materializedColumns != null)
        {
            // 全列已物化但仍传预算：以实际数据为准，预算抬到恰好容纳（不裁剪已交付数据）
            cellBudget = Math.Max(cellBudget, columnCount * Math.Max(rowCount, 1));
        }
        _columnCount = columnCount;
        _rowCount = rowCount;
        _cellBudget = Math.Max(cellBudget, rowCount);   // 至少容一整列
        _fetcher = fetcher;
        _materialized = materializedColumns ?? new object?[]?[columnCount];
        for (int i = 0; i < columnCount; i++)
            if (_materialized[i] is not null)
            {
                _usedCells += rowCount;
                _lru.Add(i);
            }
    }

    public int ColumnCount => _columnCount;
    public int RowCount => _rowCount;
    /// <summary>当前已物化列数。</summary>
    public int MaterializedColumns => _lru.Count;
    /// <summary>当前内存占用（单元数口径）。</summary>
    public int UsedCells => _usedCells;
    /// <summary>是否宽表（应走懒加载模式）。</summary>
    public bool IsWide => _columnCount > WideColumnThreshold;

    /// <summary>取第 col 列整列（按需物化）。命中缓存零成本；未命中拉取并按 LRU+封顶换页。</summary>
    public object?[] GetColumn(int col)
    {
        if (col < 0 || col >= _columnCount) throw new ArgumentOutOfRangeException(nameof(col));
        if (_materialized[col] is { } hit)
        {
            Touch(col);
            return hit;
        }
        var fetcher = _fetcher ?? throw new InvalidOperationException($"列 {col} 未物化且无拉取器，无法按需取列");
        var data = fetcher.FetchColumn(col)
            ?? throw new InvalidOperationException($"列 {col} 拉取返回空");
        Materialize(col, data);
        return _materialized[col]!;
    }

    /// <summary>取单元值（网格单格渲染入口）：列未物化时触发按需拉取。</summary>
    public object? Get(int col, int row)
    {
        if (row < 0 || row >= _rowCount) throw new ArgumentOutOfRangeException(nameof(row));
        return GetColumn(col)[row];
    }

    /// <summary>已物化列的零拷贝直读（网格逐行渲染热点路径）；未物化列返回 null（调用方先 GetColumn）。</summary>
    public object?[]? PeekColumn(int col)
        => col < 0 || col >= _columnCount ? throw new ArgumentOutOfRangeException(nameof(col)) : _materialized[col];

    private void Materialize(int col, object?[] data)
    {
        int cost = _rowCount;
        // 封顶换页：预算不够时先逐出最久未用列（保留正在取的这一列，至多逐到恰好容纳）
        while (_usedCells + cost > _cellBudget && _lru.Count > 0)
        {
            if (_lru[0] == col) break;   // 理论不可达：预算≥一整列
            Evict(_lru[0]);
        }
        if (_usedCells + cost > _cellBudget && _lru.Count == 0)
            throw new InvalidOperationException($"内存封顶 {_cellBudget} 单元小于一列 {cost}，无法按需加载（预算过小）");
        _materialized[col] = data;
        _usedCells += cost;
        _lru.Add(col);
    }

    private void Touch(int col)
    {
        _lru.Remove(col);
        _lru.Add(col);
    }

    private void Evict(int col)
    {
        _materialized[col] = null;
        _lru.Remove(col);
        _usedCells -= _rowCount;
    }

    /// <summary>从已物化的 QueryResult 挂接（窄表零开销路径）：全列槽位直接引用原数据。</summary>
    public static ColumnLazyStore FromResult(QueryResult r, int cellBudget = DefaultCellBudget)
    {
        var cols = new object?[]?[r.Columns.Count];
        for (int i = 0; i < r.Columns.Count; i++)
        {
            var data = new object?[r.Rows.Count];
            for (int row = 0; row < r.Rows.Count; row++) data[row] = r.Rows[row][i];
            cols[i] = data;
        }
        return new ColumnLazyStore(r.Columns.Count, r.Rows.Count, cols, cellBudget: cellBudget);
    }
}
