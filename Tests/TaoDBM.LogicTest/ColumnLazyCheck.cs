using TaoDBM.Core;

namespace TaoDBM.LogicTest;

/// <summary>批次F纯逻辑用例（2026-11-20）：宽表列按需加载 ColumnLazyStore——首屏物化、横向按需、
/// LRU 淘汰、内存封顶、空表/单列/重复列名边界。独立文件接线（ComboMatrix.RunAll 家法），
/// 不碰 Program.cs 主体（与真库矩阵批次 RDB-* 错开）。</summary>
public static class ColumnLazyCheck
{
    /// <summary>带默认 detail 的检查委托（与 Program.Check 同形）。</summary>
    public delegate void CheckD(string name, bool ok, string detail = "");

    public static void RunAll(Action<string, bool, string> check)
    {
        CheckD d = (name, ok, detail) => check(name, ok, detail ?? "");
        Console.WriteLine("\n== 批次F 宽表列按需加载 ColumnLazyStore（2026-11-20）==");
        RunBasics(d);
        RunOnDemand(d);
        RunLruBudget(d);
        RunEdges(d);
    }

    /// <summary>测试用列拉取器：数组切片直供，记录调用次数。</summary>
    sealed class ArrFetcher : IColumnFetcher
    {
        readonly object?[][] _cols;
        public int Calls;
        public ArrFetcher(object?[][] cols) => _cols = cols;
        public object?[] FetchColumn(int columnIndex) { Calls++; return _cols[columnIndex]; }
    }

    static object?[][] MakeCols(int colCount, int rowCount)
    {
        var cols = new object?[colCount][];
        for (int c = 0; c < colCount; c++)
        {
            var col = new object?[rowCount];
            for (int r = 0; r < rowCount; r++) col[r] = $"v{c}_{r}";
            cols[c] = col;
        }
        return cols;
    }

    static void RunBasics(CheckD check)
    {
        // 首屏物化：构造时传入已物化列，零拉取直读
        var cols = MakeCols(4, 10);
        var store = new ColumnLazyStore(4, 10, cols);
        check("COL-构造-零拷贝全物化", store.MaterializedColumns == 4 && store.UsedCells == 40);
        check("COL-窄表-非宽表", !store.IsWide);
        check("COL-直读-命中不拉取", store.PeekColumn(2)![5]?.ToString() == "v2_5");

        // 阈值：64 列非宽表，65 列宽表
        check("COL-阈值-64列非宽", !new ColumnLazyStore(64, 1, MakeCols(64, 1)).IsWide);
        check("COL-阈值-65列宽表", new ColumnLazyStore(65, 1, null, new ArrFetcher(MakeCols(65, 1))).IsWide);

        // 单元口径：UsedCells = 行数 × 已物化列数
        var s2 = new ColumnLazyStore(3, 5, MakeCols(3, 5));
        check("COL-口径-单元数计量", s2.UsedCells == 15 && s2.MaterializedColumns == 3);
    }

    static void RunOnDemand(CheckD check)
    {
        var cols = MakeCols(100, 20);
        var fetcher = new ArrFetcher(cols);
        var store = new ColumnLazyStore(100, 20, null, fetcher, cellBudget: 20 * 10);  // 容 10 列

        check("COL-懒载-初始零物化", store.MaterializedColumns == 0 && store.UsedCells == 0);

        // 首屏：只物化可视列（Get 触发按需）
        var first = store.GetColumn(0);
        check("COL-懒载-首列按需拉取", first[0]?.ToString() == "v0_0" && fetcher.Calls == 1 && store.MaterializedColumns == 1);

        // 横向滚动：按需拉取另一列
        var next = store.GetColumn(1);
        check("COL-懒载-横滚再拉一列", next[7]?.ToString() == "v1_7" && fetcher.Calls == 2 && store.MaterializedColumns == 2);

        // 命中缓存：再取不重拉
        _ = store.GetColumn(0);
        check("COL-懒载-命中零成本", fetcher.Calls == 2 && store.MaterializedColumns == 2);

        // Get 单格入口
        check("COL-懒载-单格取值", store.Get(2, 3)?.ToString() == "v2_3" && fetcher.Calls == 3);
    }

    static void RunLruBudget(CheckD check)
    {
        // 预算 3 列：反复横扫 5 列，LRU 应保持 ≤3 列且重拉最久列
        var cols = MakeCols(10, 8);
        var fetcher = new ArrFetcher(cols);
        var store = new ColumnLazyStore(10, 8, null, fetcher, cellBudget: 8 * 3);

        for (int round = 0; round < 2; round++)
            for (int c = 0; c < 5; c++) _ = store.GetColumn(c);

        check("COL-LRU-封顶不超预算", store.UsedCells <= 8 * 3 && store.MaterializedColumns <= 3);
        check("COL-LRU-淘汰后重拉", fetcher.Calls > 5, $"calls={fetcher.Calls}");   // 第二轮前几列已被逐出重拉

        // 触摸续命：反复读第0列保它不被逐出
        var keep = new ColumnLazyStore(10, 8, null, new ArrFetcher(cols), cellBudget: 8 * 2);
        _ = keep.GetColumn(0);
        _ = keep.GetColumn(1);
        _ = keep.GetColumn(0);   // touch 0
        _ = keep.GetColumn(2);   // 应逐出 1（最久未用），保 0
        check("COL-LRU-触摸续命", keep.PeekColumn(0) != null && keep.PeekColumn(1) == null);

        // 预算恰好一列：可行
        var one = new ColumnLazyStore(4, 6, null, new ArrFetcher(cols), cellBudget: 6);
        _ = one.GetColumn(3);
        check("COL-预算-恰好一列可行", one.MaterializedColumns == 1 && one.UsedCells == 6);
    }

    static void RunEdges(CheckD check)
    {
        // 空表：0 行，取列返回空数组不抛
        var empty = new ColumnLazyStore(3, 0, null, new ArrFetcher(MakeCols(3, 0)));
        bool emptyOk = false;
        try { emptyOk = empty.GetColumn(0).Length == 0; } catch { emptyOk = false; }
        check("COL-边界-空表取列不抛", emptyOk && empty.UsedCells == 0);

        // 单列表
        var single = new ColumnLazyStore(1, 4, MakeCols(1, 4));
        check("COL-边界-单列直读", single.GetColumn(0)[2]?.ToString() == "v0_2");

        // 重复列名：按索引寻址不受影响（列名对账由上层 QueryResult.Columns 承担）
        var dup = new ColumnLazyStore(2, 2, MakeCols(2, 2));
        check("COL-边界-重复列名按索引", dup.Get(0, 1)?.ToString() == "v0_1" && dup.Get(1, 1)?.ToString() == "v1_1");

        // 越界：列/行越界抛
        bool colThrow = false, rowThrow = false;
        try { dup.GetColumn(5); } catch (ArgumentOutOfRangeException) { colThrow = true; }
        try { dup.Get(0, 99); } catch (ArgumentOutOfRangeException) { rowThrow = true; }
        check("COL-边界-列越界抛", colThrow);
        check("COL-边界-行越界抛", rowThrow);

        // 无拉取器且未物化：取列抛业务异常（不静默）
        var noFetcher = new ColumnLazyStore(3, 2, null, null);
        bool fetchThrow = false;
        try { noFetcher.GetColumn(1); } catch (InvalidOperationException) { fetchThrow = true; }
        check("COL-边界-无拉取器抛", fetchThrow);

        // 拉取器返回空：抛业务异常
        var badFetcher = new ColumnLazyStore(2, 2, null, new NullFetcher());
        bool nullThrow = false;
        try { badFetcher.GetColumn(0); } catch (InvalidOperationException) { nullThrow = true; }
        check("COL-边界-拉取返回空抛", nullThrow);
    }

    sealed class NullFetcher : IColumnFetcher
    {
        public object?[] FetchColumn(int columnIndex) => null!;
    }
}
