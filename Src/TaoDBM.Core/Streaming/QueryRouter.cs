using System.Data.Common;

namespace TaoDBM.Core.Streaming;

/// <summary>分流入口：按 StreamingSwitch 把查询接到流式或旧整包通道（方案书并存口径）。</summary>
public static class QueryRouter
{
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
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var page = await sq.NextPageAsync(2000, ct);
                    foreach (var row in page.Rows) qr.Rows.Add(row);
                    if (!page.HasMore) break;
                }
                Safe.Run(() => qr.Affected = sq.Affected, "读取受影响行数");
                if (qr.Columns.Count == 0 && qr.Affected < 0) qr.Affected = 0;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                qr.Message = "错误：" + ex.Message;
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
                    while (await reader.ReadAsync(ct))
                    {
                        ct.ThrowIfCancellationRequested();
                        for (int b = 0; b < nVis; b++) bufs[b].Add(reader.GetValue(vis[b]));
                        rows++;
                    }
                    Safe.Run(() => qr.Affected = reader.RecordsAffected, "读取受影响行数");
                    if (qr.Affected < 0) qr.Affected = 0;

                    var source = new RelationalColumnSource(provider, db, sql, names);
                    source.SetRowCount(rows);
                    var store = new ColumnLazyStore(source);
                    store.SeedColumns(vis, bufs.Select(b => b.ToArray()).ToArray());
                    sw.Stop();
                    results.Add(new LazyQueryResult(store, vis, qr.Title, sw.Elapsed));
                    continue;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                qr.Message = "错误：" + ex.Message;
            }
            sw.Stop();
            qr.Elapsed = sw.Elapsed;
            results.Add(qr);
        }
        return results;
    }
}
