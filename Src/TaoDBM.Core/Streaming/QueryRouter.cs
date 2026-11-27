using System.Data.Common;

namespace TaoDBM.Core.Streaming;

/// <summary>分流入口：按 StreamingSwitch 把查询接到流式或旧整包通道（方案书并存口径）。</summary>
public static class QueryRouter
{
    /// <summary>内存封顶阈值（对账表「内存封顶」收口）：单语句结果行数超过该值即截断并落账 Message；
    /// 0 = 不封顶。默认 200_000 行，防异常大结果集拖垮内存。</summary>
    public static long MaxRowsPerStatement { get; set; } = 200_000;

    /// <summary>单行物化字节数估算（评分打回311补落）：对象头/引用 64B＋每格 32B＋字符串 2B/字符。
    /// 估算是口径不是精测——封顶判定宁可略早，不拖垮内存。</summary>
    internal static long EstimateRowBytes(object?[] row)
    {
        long bytes = 64;
        foreach (var v in row) bytes += 32 + (v?.ToString()?.Length ?? 0) * 2;
        return bytes;
    }

    /// <summary>流式执行：跑完返回整包结果（与旧 Execute 同形），内部逐页取数，无 5000 截断。</summary>
    public static async Task<List<QueryResult>> ExecuteAsync(RelationalProvider provider, string? db, string script, CancellationToken ct)
    {
        var results = new List<QueryResult>();
        var statements = SqlSplitter.Split(script);
        if (statements.Count == 0) return results;
        await using var conn = provider.OpenStream(db);
        foreach (var sql in statements)
        {
            var qr = new QueryResult { Title = sql.Length > 60 ? sql[..60] + "…" : sql };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await using var sq = await StreamingQuery.StartAsync(conn, sql, ct);
            try
            {
                qr.Columns.AddRange(sq.Columns);
                long rows = 0;
                bool capped = false;
                long memBytes = 0;
                bool memCapped = false;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var page = await sq.NextPageAsync(2000, ct);
                    // 同 ExecuteLazyAsync：快产快消通道不能独占调用线程，取消与 UI 交互要进得来
                    await Task.Yield();
                    foreach (var row in page.Rows)
                    {
                        if (MaxRowsPerStatement > 0 && rows >= MaxRowsPerStatement) { capped = true; break; }
                        // 评分打回311补落：单查询内存上限（MB）——物化字节数估算超限即封顶，与行数封顶同口径落账
                        memBytes += EstimateRowBytes(row);
                        if (memBytes >= (long)StreamingPrefs.MemoryLimitMb * 1024 * 1024) { capped = true; memCapped = true; break; }
                        qr.Rows.Add(row);
                        rows++;
                    }
                    if (capped || !page.HasMore) break;
                }
                if (capped) qr.Message = memCapped
                    ? $"结果已达单查询内存上限 {StreamingPrefs.MemoryLimitMb} MB，超出部分未取（可加 WHERE/TOP/LIMIT 收窄后重查）。"
                    : $"结果已达内存封顶 {MaxRowsPerStatement:N0} 行，超出部分未取（可加 WHERE/TOP/LIMIT 收窄后重查）。";
                StreamingPrefs.NoteLargeResult(rows, qr.Title);
                Safe.Run(() => qr.Affected = sq.Affected, "读取受影响行数");
                if (qr.Columns.Count == 0 && qr.Affected < 0) qr.Affected = 0;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                qr.Message = HumanError.Say(ex, "执行");
            }
            sw.Stop();
            qr.Elapsed = sw.Elapsed;
            results.Add(qr);
        }
        return results;
    }

    /// <summary>
    /// 流式执行·宽表列懒加载（批次F）：结果集语句首屏只物化前 visibleColumns 列，
    /// 其余列保留在源（RelationalColumnSource），网格横向滚动到新列时经 Store 增量拉取。
    /// 执行期内存 ∝ 可见列数×行数，不随总列数线性涨；写语句与非结果集语句与整包通道同形。
    /// </summary>
    public static async Task<List<QueryResult>> ExecuteLazyAsync(RelationalProvider provider, string? db, string script, int visibleColumns, CancellationToken ct)
    {
        var results = new List<QueryResult>();
        var statements = SqlSplitter.Split(script);
        if (statements.Count == 0) return results;
        await using var conn = provider.OpenStream(db);
        await conn.OpenAsync(ct);
        foreach (var sql in statements)
        {
            var qr = new QueryResult { Title = sql.Length > 60 ? sql[..60] + "…" : sql };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (reader.FieldCount == 0)
                {
                    // 非结果集语句（DDL/DML）：与整包通道同形，只带受影响行数
                    Safe.Run(() => qr.Affected = reader.RecordsAffected, "读取受影响行数");
                    if (qr.Affected < 0) qr.Affected = 0;
                }
                else
                {
                    var names = new List<string>();
                    for (int i = 0; i < reader.FieldCount; i++) names.Add(reader.GetName(i));
                    int nVis = Math.Clamp(visibleColumns, 0, names.Count);
                    var vis = Enumerable.Range(0, nVis).ToArray();
                    var bufs = new List<object?>[nVis];
                    for (int b = 0; b < nVis; b++) bufs[b] = new List<object?>();
                    int rows = 0;
                    bool capped = false;
                    long memBytes = 0;
                    bool memCapped = false;
                    // 云测 11-27 两轮实锤（run 46009158635/46011556518 S20 簇，run346 同计划同矩阵全绿旁证）：
                    // 稀疏行长查（递归 CTE 十万迭代出一行）512 行才让一次＝两次让出间纯计算 5120 万次迭代，
                    // 慢机上一饿几十秒——停止钮 UIA 六秒找不到、点了 cancelled 落账超 20 秒窗，段超时被掐。
                    // 让出改双口径：行数节奏保留（快产快消低开销），加时间节奏 40ms 必让，饿死量子与行产速脱钩
                    var sinceYield = System.Diagnostics.Stopwatch.StartNew();
                    while (await reader.ReadAsync(ct))
                    {
                        ct.ThrowIfCancellationRequested();
                        // 内存封顶与整包通道 ExecuteAsync 同口径：#273 实锤无封顶时 1 亿行递归 CTE 把应用吃爆（0xC0000407 fail-fast）
                        if (MaxRowsPerStatement > 0 && rows >= MaxRowsPerStatement) { capped = true; break; }
                        // 评分打回311补落：单查询内存上限（MB）——物化字节数按可见列估算，超限即封顶
                        memBytes += nVis * 48;
                        if (memBytes >= (long)StreamingPrefs.MemoryLimitMb * 1024 * 1024) { capped = true; memCapped = true; break; }
                        for (int b = 0; b < nVis; b++) bufs[b].Add(reader.GetValue(vis[b]));
                        rows++;
                        // 本地库快产快消时 ReadAsync 几乎全同步完成，await 连续同步续接会独占调用线程
                        //（UI 线程被饿死＝停止按钮点不动，#273 S20 崩溃共因）：定期让出消息泵，取消与交互才进得来
                        if ((rows & 511) == 0 || sinceYield.ElapsedMilliseconds >= 40) { await Task.Yield(); sinceYield.Restart(); }
                    }
                    Safe.Run(() => qr.Affected = reader.RecordsAffected, "读取受影响行数");
                    if (qr.Affected < 0) qr.Affected = 0;

                    var source = new RelationalColumnSource(provider, db, sql, names);
                    source.SetRowCount(rows);
                    var store = new ColumnLazyStore(source);
                    store.SeedColumns(vis, bufs.Select(b => b.ToArray()).ToArray());
                    sw.Stop();
                    var lazy = new LazyQueryResult(store, vis, qr.Title, sw.Elapsed);
                    if (capped) lazy.Message = memCapped
                        ? $"结果已达单查询内存上限 {StreamingPrefs.MemoryLimitMb} MB，超出部分未取（可加 WHERE/TOP/LIMIT 收窄后重查）。"
                        : $"结果已达内存封顶 {MaxRowsPerStatement:N0} 行，超出部分未取（可加 WHERE/TOP/LIMIT 收窄后重查）。";
                    StreamingPrefs.NoteLargeResult(rows, qr.Title);
                    results.Add(lazy);
                    continue;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                qr.Message = HumanError.Say(ex, "执行");
            }
            sw.Stop();
            qr.Elapsed = sw.Elapsed;
            results.Add(qr);
        }
        return results;
    }
}
