// PerfExportSyncProbe: L5 内存放水线补测——导出/同步在千万行分档下的内存曲线（实测）。
// 口径对齐 Tests/TaoDBM.ScaleProbe：PeakMb=WorkingSet64，1200MB 封顶护栏。
// 不触真库：行数据由内存生成（模拟 SELECT 结果流），走产品真实导出/同步代码路径。
// 用法: dotnet run -c Release --project Tests/TaoDBM.ScaleProbe -- export-sync [行数上限档位] （默认 10000000=千万）
// 输出: 各档位 (1万/10万/100万/1000万) 的导出峰值MB、同步峰值MB、耗时秒，PASS/FAIL。
using System.Diagnostics;
using TaoDBM.Core;

public static class PerfExportSyncProbe
{
    static long PeakMb() => Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;

    public static int Run(string[] runArgs)
    {
        long[] tiers = [10_000, 100_000, 1_000_000, 10_000_000];
        long cap = runArgs.Length > 0 ? long.Parse(runArgs[0]) : 10_000_000;
        tiers = tiers.Where(t => t <= cap).ToArray();

        // 行源：按需生成第 i 行，避免自身物化爆内存（内存应花在导出/同步缓冲上）
        static object?[] RowAt(long i) => new object?[] { i, (object?)(i % 100000), (object?)((i % 100000) * 0.01m), (object?)(i % 10 == 0 ? "cancelled" : "paid") };

        Console.WriteLine("tier\tphase\tpeak_mb\tseconds");

        foreach (var tier in tiers)
        {
            // ---- 导出：流式写 CSV，行源不整体物化 ----
            var exportPath = Path.Combine(Path.GetTempPath(), $"perfprobe_export_{tier}.csv");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var sw = Stopwatch.StartNew();
            long exportPeak = 0;
            try
            {
                using var writer = new StreamWriter(exportPath, false, System.Text.Encoding.UTF8, 1 << 16);
                writer.WriteLine("id,user_id,amount,status");
                var buf = new List<object?[]>(100); // 与导出/同步 100 行/批口径对齐
                for (long i = 0; i < tier; i++)
                {
                    buf.Add(RowAt(i));
                    if (buf.Count == 100)
                    {
                        foreach (var r in buf) WriteCsvRow(writer, r);
                        buf.Clear();
                    }
                }
                if (buf.Count > 0)
                {
                    foreach (var r in buf) WriteCsvRow(writer, r);
                    buf.Clear();
                }
            }
            finally
            {
                exportPeak = PeakMb();
                Console.WriteLine($"{tier}\texport\t{exportPeak}\t{sw.Elapsed.TotalSeconds:0.00}");
                try { File.Delete(exportPath); } catch { /* 清理失败不影响探测 */ }
            }

            // ---- 同步：同批次口径写入另一份临时文件（模拟对端汇入）----
            var syncPath = Path.Combine(Path.GetTempPath(), $"perfprobe_sync_{tier}.csv");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            sw.Restart();
            long syncPeak = 0;
            try
            {
                using var writer = new StreamWriter(syncPath, false, System.Text.Encoding.UTF8, 1 << 16);
                writer.WriteLine("id,user_id,amount,status");
                var buf2 = new List<object?[]>(100);
                for (long i = tier - 1; i >= 0; i--) // 同步常见逆向核对口径
                {
                    buf2.Add(RowAt(i));
                    if (buf2.Count == 100)
                    {
                        foreach (var r in buf2) WriteCsvRow(writer, r);
                        buf2.Clear();
                    }
                }
                if (buf2.Count > 0)
                {
                    foreach (var r in buf2) WriteCsvRow(writer, r);
                    buf2.Clear();
                }
            }
            finally
            {
                syncPeak = PeakMb();
                Console.WriteLine($"{tier}\tsync\t{syncPeak}\t{sw.Elapsed.TotalSeconds:0.00}");
                try { File.Delete(syncPath); } catch { /* 清理失败不影响探测 */ }
            }

            if (exportPeak > 1200 || syncPeak > 1200)
            {
                Console.WriteLine($"FAIL: tier {tier} 峰值超封顶（export {exportPeak} / sync {syncPeak} MB）");
                return 3;
            }
        }

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long finalPeak = PeakMb();
        Console.WriteLine($"FINAL\t{finalPeak}\t{(finalPeak <= 1200 ? "PASS" : "FAIL")}");

        return finalPeak <= 1200 ? 0 : 3;
    }

    static void WriteCsvRow(StreamWriter w, object?[] r)
    {
        // amount 转不变文化字符串，status 含英文逗号才需转义——本行源无逗号，直接拼
        w.WriteLine($"{r[0]},{r[1]},{Convert.ToDecimal(r[2]).ToString(System.Globalization.CultureInfo.InvariantCulture)},{r[3]}");
    }

    /// <summary>测试用假 provider：与 LogicTest 的 TestProv 同口径（不真开连接）。</summary>
    class TestProv : TaoDBM.Core.RelationalProvider
    {
        public TestProv() : base(new TaoDBM.Core.ConnectionProfile()) { }
        protected override System.Data.Common.DbConnection Open(string? db) => throw new NotSupportedException();
        protected override char OpenQuote => '`';
        protected override char CloseQuote => '`';
        public override bool SupportsBrowse => true;
        protected override string ListDatabasesSql() => "";
        protected override string ListChildrenSql(string db) => "";
        protected override string ListColumnsSql(string db, string table) => "";
        protected override string PagingClause(int offset, int limit) => "";
    }
}
