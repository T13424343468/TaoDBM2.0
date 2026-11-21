using System.Diagnostics;
using TaoDBM.Core;

// B11 后千万档回归探针：对真库（MySQL/MariaDB 与 PostgreSQL）各跑千万行口径，测产品真实代码路径
// （ProviderFactory 建连接 → Execute / Browse），记录耗时与进程内存峰值；不走 UI，不做写操作。
// 用法：dotnet run -c Release --project Tests/TaoDBM.ScaleProbe -- <engine:mysql|pgsql> <host> <port> <user> <password> <database>
// 子命令：export-sync [行数上限档位] —— 走 PerfExportSyncProbe（导出/同步内存曲线，不触真库）

if (args.Length > 0 && args[0] == "export-sync")
    return PerfExportSyncProbe.Run(args.Skip(1).ToArray());

long PeakMb() => Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
var sw = Stopwatch.StartNew();

var engine = args.Length > 0 && args[0] == "pgsql" ? DbEngine.PostgreSQL : DbEngine.MySQL;
var profile = new ConnectionProfile
{
    DisplayName = "scale-probe",
    Engine = engine,
    Host = args.Length > 1 ? args[1] : "127.0.0.1",
    Port = args.Length > 2 ? int.Parse(args[2]) : (engine == DbEngine.MySQL ? 3306 : 5432),
    User = args.Length > 3 ? args[3] : "reg",
    Password = args.Length > 4 ? args[4] : "reg_local_only",
    Database = args.Length > 5 ? args[5] : "taodbm_reg",
};

Console.WriteLine($"== 千万档探针 {engine} {profile.Host}:{profile.Port}/{profile.Database} ==");
using var prov = ProviderFactory.Create(profile);
await prov.TestConnection();
Console.WriteLine($"E0 连接 OK，峰值内存 {PeakMb()} MB");

long Count(string sql)
{
    var rs = prov.Execute(profile.Database, sql).GetAwaiter().GetResult();
    return Convert.ToInt64(rs[0].Rows[0][0]);
}

// E1 全表计数（真库聚合扫描）
var cnt = Count("SELECT COUNT(*) FROM orders");
Console.WriteLine($"E1 计数 {cnt}，{sw.Elapsed.TotalSeconds:0.0}s，峰值 {PeakMb()} MB");
if (cnt != 10_000_000) { Console.WriteLine("E1 失败：行数不符"); return 2; }

// E2 分页浏览（产品真实 Browse 路径）：浅/中/深三档翻页
double maxPageMs = 0;
foreach (var offset in new[] { 0, 2_500_000, 5_000_000, 7_500_000, 9_999_500 })
{
    var t = Stopwatch.StartNew();
    var r = prov.Browse(profile.Database, "orders", offset, 500).GetAwaiter().GetResult();
    maxPageMs = Math.Max(maxPageMs, t.Elapsed.TotalMilliseconds);
    if (r.Rows.Count != 500) { Console.WriteLine($"E2 失败：offset {offset} 仅 {r.Rows.Count} 行"); return 2; }
}
Console.WriteLine($"E2 分页 5 档最慢 {maxPageMs:0}ms，峰值 {PeakMb()} MB");

// E3 聚合分组（全表 GROUP BY）
sw.Restart();
var agg = prov.Execute(profile.Database, "SELECT status, COUNT(*) FROM orders GROUP BY status").GetAwaiter().GetResult();
var aggRows = agg[0].Rows.Count;
Console.WriteLine($"E3 分组 {aggRows} 组，{agg[0].Elapsed.TotalSeconds:0.00}s，峰值 {PeakMb()} MB");
if (aggRows == 0) { Console.WriteLine("E3 失败：无分组结果"); return 2; }

// E4 深排序 TopN（无索引 ORDER BY）
var top = prov.Execute(profile.Database, "SELECT id, amount FROM orders ORDER BY amount DESC LIMIT 100").GetAwaiter().GetResult();
Console.WriteLine($"E4 TopN {top[0].Rows.Count} 行，{top[0].Elapsed.TotalSeconds:0.00}s，峰值 {PeakMb()} MB");

// E5 大结果集拉取：10 万行查询，断言 5000 行护栏生效（防界面物化爆内存），并测内存峰值
sw.Restart();
var big = prov.Execute(profile.Database, engine == DbEngine.MySQL
    ? "SELECT id, user_id, amount, status, created_at, memo FROM orders WHERE id <= 100000"
    : "SELECT id, user_id, amount, status, created_at, memo FROM orders WHERE id <= 100000").GetAwaiter().GetResult();
var bigRows = big[0].Rows.Count;
Console.WriteLine($"E5 拉取 {bigRows} 行，{big[0].Elapsed.TotalSeconds:0.00}s，峰值 {PeakMb()} MB");
if (bigRows != 5000 || !big[0].Message.Contains("5000")) { Console.WriteLine("E5 失败：护栏行为不符"); return 2; }

// 内存封顶判定（沙箱 3.9GB 总内存，探针进程上限 1.2GB）
long peak = PeakMb();
Console.WriteLine(peak <= 1200 ? $"PASS {engine}: 峰值 {peak} MB ≤ 1200 MB" : $"FAIL {engine}: 峰值 {peak} MB 超封顶");
return peak <= 1200 ? 0 : 3;
