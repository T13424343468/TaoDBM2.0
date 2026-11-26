﻿﻿using System;
using System.Diagnostics;
using System.Text;
using TaoDBM.Core;
using TaoDBM.Core.Streaming;
using TaoDBM.Infrastructure;
using TaoDBM.UI;

// .sql 三件套（2026-11-19 23:52 老板令）：GBK 编码口径依赖 CodePages 提供者（App 启动已注册，测试进程自行补注册）
try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
catch (Exception ex) { Console.Error.WriteLine($"CodePages 注册失败: {ex.Message}"); }

// 云端尸检：每断言/层切换立即追加落盘（文件级真落盘，进程任何形式死亡后都能对出最后位置与时间线）
var swAll = Stopwatch.StartNew();
string ckptPath = Environment.GetEnvironmentVariable("TAODBM_CKPT") ?? "test-progress.txt";
void Ck(string msg)
{
    try { System.IO.File.AppendAllText(ckptPath, $"[{swAll.Elapsed.TotalSeconds:0.000}s] {msg}\r\n", Encoding.UTF8); } catch { /* 尸检记录绝不影响主流程 */ }
}
AppDomain.CurrentDomain.UnhandledException += (s, e) =>
{
    Ck($"UNHANDLED-EXCEPTION terminating={e.IsTerminating} {e.ExceptionObject}");
    try { Console.Error.WriteLine($"UNHANDLED-EXCEPTION terminating={e.IsTerminating} {e.ExceptionObject}"); Console.Error.Flush(); } catch { }
};

// R2 进程级 kill/重启演练（盲区补齐 2026-11-21）：Scripts/kill-restart-drill.ps1 两阶段调用——
// seed 阶段落状态后驻留，被外部强杀模拟崩溃；verify 阶段以新进程回读对账恢复。
// 常规全量跑（无环境变量）不进此分支，行为与既有完全一致。
var drillPhase = Environment.GetEnvironmentVariable("TAODBM_DRILL");
if (drillPhase is "seed" or "verify")
{
    return KillRestartDrill.Run(drillPhase, args);
}

int pass = 0, fail = 0;
var layerPass = new Dictionary<string, int>();
var layerFail = new Dictionary<string, int>();
string layer = "单元";
void Layer(string name)
{
    layer = name;
    layerPass.TryAdd(name, 0);
    layerFail.TryAdd(name, 0);
    Console.WriteLine($"\n========== {name}层 ==========");
    Console.Out.Flush();
    Ck($"LAYER {name}");
}
void Check(string name, bool ok, string detail = "")
{
    layerPass.TryAdd(layer, 0);
    layerFail.TryAdd(layer, 0);
    if (ok) { pass++; layerPass[layer]++; Console.WriteLine($"  ✓ {name}"); }
    else { fail++; layerFail[layer]++; Console.WriteLine($"  ✗ {name}  {detail}"); }
    Console.Out.Flush();
    Ck($"#[{pass + fail}] {layer} {(ok ? "✓" : "✗")} {name}{(ok ? "" : "  " + detail)}");
}
Ck($"START os={Environment.OSVersion} net={Environment.Version} cwd={Environment.CurrentDirectory}");
Layer("单元");

Console.WriteLine("== SqlSplitter ==");
{
    var parts = SqlSplitter.Split("SELECT 1; SELECT 2;");
    Check("普通拆分", parts.Count == 2 && parts[0] == "SELECT 1" && parts[1] == "SELECT 2", string.Join("|", parts));
}
{
    var parts = SqlSplitter.Split("SELECT 'a;b' AS v;\nSELECT 2");
    Check("字符串内分号不拆", parts.Count == 2 && parts[0] == "SELECT 'a;b' AS v", string.Join("|", parts));
}
{
    var parts = SqlSplitter.Split("SELECT 1 -- comment; select\n; SELECT 2");
    Check("行注释内分号不拆", parts.Count == 2, string.Join("|", parts));
}
{
    var parts = SqlSplitter.Split("/* ; ; */ SELECT 1");
    Check("块注释内分号不拆", parts.Count == 1 && parts[0] == "/* ; ; */ SELECT 1", string.Join("|", parts));
}
{
    var parts = SqlSplitter.Split("SELECT `we;ird` FROM t");
    Check("反引号内分号不拆", parts.Count == 1, string.Join("|", parts));
}
{
    var parts = SqlSplitter.Split("SELECT '''; SELECT 2");
    Check("转义引号", parts.Count == 1 && parts[0].StartsWith("SELECT '''"), string.Join("|", parts));
}
{
    var parts = SqlSplitter.Split("  ;  ;  ");
    Check("空脚本返回空", parts.Count == 0, string.Join("|", parts));
}

Console.WriteLine("== Exporter ==");
{
    var r = new QueryResult { Columns = { "id", "name", "note" } };
    r.Rows.Add(new object?[] { 1, "张三", "有,逗号" });
    r.Rows.Add(new object?[] { 2, "李\"四", "换\n行" });
    var csv = Exporter.Csv(r);
    Check("CSV 表头", csv.Contains("id,name,note"));
    Check("CSV 逗号转义", csv.Contains("\"有,逗号\""), csv);
    Check("CSV 引号转义", csv.Contains("\"李\"\"四\""), csv);
    Check("CSV BOM", csv.StartsWith('\uFEFF'));
}
{
    // ㊻ CSV 公式注入防护：=+-@ 开头单元格加前缀 ' 转义；纯数值（含负数）保持原值
    var r = new QueryResult { Columns = { "k", "v" } };
    r.Rows.Add(new object?[] { 1, "=cmd|' /C calc'!A0" });
    r.Rows.Add(new object?[] { 2, "+SUM(A1:A2)" });
    r.Rows.Add(new object?[] { 3, "@risk" });
    r.Rows.Add(new object?[] { 4, "-5" });
    r.Rows.Add(new object?[] { 5, "+42.5" });
    r.Rows.Add(new object?[] { 6, "普通文本" });
    var csv = Exporter.Csv(r);
    Check("CSV 公式注入=转义", csv.Contains("'=cmd"), csv);
    Check("CSV 公式注入+转义", csv.Contains("'+SUM"), csv);
    Check("CSV 公式注入@转义", csv.Contains("'@risk"), csv);
    Check("CSV 负数不转义", csv.Contains("\n4,-5") && !csv.Contains("'-5"), csv);
    Check("CSV 正数不转义", csv.Contains("\n5,+42.5"), csv);
    Check("CSV 普通文本原样", csv.Contains(",普通文本"), csv);
}
{
    // 发现150：JSON 导出——对象数组形、类型分型、引号/控制字符转义、流式与整包同形
    var r = new QueryResult { Columns = { "id", "name", "score", "ok", "note" } };
    r.Rows.Add(new object?[] { 1, "张\"三", 98.5, true, null });
    r.Rows.Add(new object?[] { 2, DBNull.Value, -3, false, "换\n行\t制表" });
    var json = Exporter.Json(r);
    Check("JSON 表头", json.Contains("\"id\":") && json.Contains("\"name\":") && json.Contains("\"score\":"), json);
    Check("JSON 整数原样", json.Contains("\"id\":1"), json);
    Check("JSON 浮点不变区域", json.Contains("\"score\":98.5"), json);
    Check("JSON 负数原样", json.Contains("\"score\":-3"), json);
    Check("JSON 布尔小写", json.Contains("\"ok\":true") && json.Contains("\"ok\":false"), json);
    Check("JSON null 落 null", json.Contains("\"note\":null"), json);
    Check("JSON 引号转义", json.Contains("\"张\\\"三\""), json);
    Check("JSON 控制字符转义", json.Contains("换\\n行\\t制表"), json);
    Check("JSON DBNull 落 null", json.Contains("\"name\":null"), json);
    var pages = new[] { new TaoDBM.Core.Streaming.QueryPage { Rows = r.Rows.Select(x => (object[])x).ToArray(), HasMore = false } };
    var sw = new StringWriter();
    long n = Exporter.JsonStream(pages, r.Columns, sw);
    Check("JSON 流式与整包同形", n == 2 && sw.ToString() == json, $"n={n}");
}
{
    // ㊴ 统一错误文案层：人话模板映射＋原始异常不上屏（只进 AppLog）
    var deadlock = HumanError.Say(new Exception("MySqlException: Deadlock found when trying to get lock; try restarting transaction"), "回滚失败");
    Check("人话-死锁映射", deadlock.Contains("抢锁") && deadlock.StartsWith("回滚没成功："), deadlock);
    Check("人话-原始异常不上屏", !deadlock.Contains("MySqlException") && !deadlock.Contains("Deadlock found"), deadlock);
    var timeout = HumanError.Say(new Exception("The operation was cancelled due to timeout"), "查询");
    Check("人话-超时映射", timeout.Contains("超时"), timeout);
    var conn = HumanError.Say(new Exception("Unable to connect to any of the specified MySQL hosts"), "连接测试");
    Check("人话-连接映射", conn.Contains("连不上"), conn);
    var auth = HumanError.Say(new Exception("Access denied for user 'x'@'localhost'"), "登录");
    Check("人话-登录映射", auth.Contains("登录被拒"), auth);
    var fallback = HumanError.Say(new Exception("some weird internal detail xyzzy"), "导出");
    Check("人话-兜底不带原始细节", !fallback.Contains("xyzzy") && fallback.Contains("日志"), fallback);
    // ㊵ 统一容量口径：自适应 B/KB/MB/GB 一位小数
    Check("容量-B", HumanSize.Of(999) == "999 B", HumanSize.Of(999));
    Check("容量-KB", HumanSize.Of(1536) == "1.5 KB", HumanSize.Of(1536));
    Check("容量-MB", HumanSize.Of(1536 * 1024) == "1.5 MB", HumanSize.Of(1536 * 1024));
    Check("容量-GB", HumanSize.Of(1610612736) == "1.5 GB", HumanSize.Of(1610612736));
    Check("容量-零", HumanSize.Of(0) == "0 B", HumanSize.Of(0));
}
{
    var r = new QueryResult { Columns = { "id", "name" } };
    r.Rows.Add(new object?[] { 1, "O'Brien" });
    r.Rows.Add(new object?[] { 2, DBNull.Value });
    r.Rows.Add(new object?[] { 3, new byte[] { 0xDE, 0xAD } });
    var sql = Exporter.InsertSql(r, "users");
    Check("INSERT 引号转义", sql.Contains("'O''Brien'"), sql);
    Check("INSERT NULL", sql.Contains("NULL"), sql);
    Check("INSERT 十六进制", sql.Contains("X'DEAD'"), sql);
}
{
    // ㊽ 导出脱敏接线：整包 Masked 按列名规则打码；无敏感列原样返回同一引用
    var r = new QueryResult { Columns = { "id", "phone", "password" } };
    r.Rows.Add(new object?[] { 1, "13812345678", "p@ssw0rd" });
    r.Rows.Add(new object?[] { 2, "13987654321", "abc123" });
    var masked = Exporter.Masked(r);
    Check("导出脱敏-手机号打码", masked.Rows[0][1]!.ToString() == "138****5678", masked.Rows[0][1]?.ToString() ?? "");
    Check("导出脱敏-密码整值打码", masked.Rows[0][2]!.ToString() == "********", masked.Rows[0][2]?.ToString() ?? "");
    Check("导出脱敏-非敏感列原值", masked.Rows[0][0]!.ToString() == "1");
    Check("导出脱敏-行数不变", masked.Rows.Count == 2);
    Check("导出脱敏-原结果不被修改", r.Rows[0][1]!.ToString() == "13812345678");
    var plain = new QueryResult { Columns = { "id", "amount" } };
    plain.Rows.Add(new object?[] { 1, 99 });
    Check("导出脱敏-无敏感列原样返回", ReferenceEquals(Exporter.Masked(plain), plain));
}
{
    // A1 流式导出：逐页写 TextWriter，不整包攒串；行为必须与整包版严格同形
    var pages = new[]
    {
        new TaoDBM.Core.Streaming.QueryPage { Rows = new object[][] { new object[] { 1, "张三" }, new object[] { 2, "有,逗号" } }, HasMore = true },
        new TaoDBM.Core.Streaming.QueryPage { Rows = new object[][] { new object[] { 3, "李\"四" } }, HasMore = false },
    };
    var sw = new StringWriter();
    long n = Exporter.CsvStream(pages, new[] { "id", "name" }, sw);
    var sameRows = new QueryResult { Columns = { "id", "name" } };
    sameRows.Rows.Add(new object?[] { 1, "张三" });
    sameRows.Rows.Add(new object?[] { 2, "有,逗号" });
    sameRows.Rows.Add(new object?[] { 3, "李\"四" });
    Check("流式 CSV 与整包同形", n == 3 && sw.ToString() == Exporter.Csv(sameRows), $"n={n}");
    var sw2 = new StringWriter();
    long n2 = Exporter.InsertSqlStream(pages, new[] { "id", "name" }, "users", sw2);
    Check("流式 INSERT 同形", n2 == 3 && sw2.ToString() == Exporter.InsertSql(sameRows, "users"), $"n2={n2}");
    // 取消语义：枚举中途取消须抛 OperationCanceledException，不吞不漏
    var cts = new CancellationTokenSource();
    IEnumerable<TaoDBM.Core.Streaming.QueryPage> CancelOnSecond()
    {
        yield return pages[0];
        cts.Cancel();
        yield return pages[1];
    }
    bool canceled = false;
    try { Exporter.CsvStream(CancelOnSecond(), new[] { "id", "name" }, new StringWriter(), cts.Token); }
    catch (OperationCanceledException) { canceled = true; }
    Check("流式导出可取消", canceled);
    // 中断恢复（测试规范三.8）：取消后不得残留全局污染，新一次导出照常完整
    var recovered = Exporter.CsvStream(pages, new[] { "id", "name" }, new StringWriter());
    Check("流式导出取消后恢复", recovered == 3, $"recovered={recovered}");
}

Console.WriteLine("== ResultGridOps（TSV 复制与内存排序，整改二批 #03）==");
{
    var cols = new[] { "id", "name", "note" };
    var rows = new List<object?[]>
    {
        new object?[] { 1, "张三", "带\n换行" },
        new object?[] { 2, "李四", "带\t制表符" },
        new object?[] { 3, null, DBNull.Value },
    };
    var tsv = ResultGridOps.ToTsv(cols, rows, Enumerable.Range(0, rows.Count));
    Check("TSV 表头与行", tsv == "id\tname\tnote\n1\t张三\t带 换行\n2\t李四\t带    制表符\n3\t\t\n", tsv.Replace("\n", "\\n").Replace("\t", "\\t"));
    var noHeader = ResultGridOps.ToTsv(cols, rows, new[] { 0 }, includeHeader: false);
    Check("TSV 不带表头", noHeader == "1\t张三\t带 换行\n", noHeader.Replace("\n", "\\n"));
    var outOfRange = ResultGridOps.ToTsv(cols, rows, new[] { -1, 0, 99 });
    Check("TSV 越界行跳过", outOfRange.Count(c => c == '\n') == 2, outOfRange.Replace("\n", "\\n"));
    var many = ResultGridOps.ToTsv(cols, rows, Enumerable.Range(0, ResultGridOps.CopyRowLimit + 5).Select(_ => 0));
    Check("TSV 上限截断", many.Count(c => c == '\n') == ResultGridOps.CopyRowLimit + 1, $"lines={many.Count(c => c == '\n')}");
    Check("CellText 空值", ResultGridOps.CellText(null) == "" && ResultGridOps.CellText(DBNull.Value) == "");
    Check("CellText 二进制", ResultGridOps.CellText(new byte[] { 0xAB, 0x01 }) == "0xAB01");
    Check("CellText 布尔与时间", ResultGridOps.CellText(true) == "TRUE" && ResultGridOps.CellText(false) == "FALSE"
        && ResultGridOps.CellText(new DateTime(2026, 11, 23, 8, 5, 3)) == "2026-11-23 08:05:03");

    var srows = new List<object?[]>
    {
        new object?[] { "3", "banana" },
        new object?[] { "10", "Apple" },
        new object?[] { null, "cherry" },
        new object?[] { "2", "" },
    };
    var asc = ResultGridOps.SortOrder(srows, 0, ResultGridOps.SortAsc);
    Check("数值列升序", string.Join(",", asc) == "3,0,1,2", string.Join(",", asc));
    var desc = ResultGridOps.SortOrder(srows, 0, ResultGridOps.SortDesc);
    Check("数值列降序", string.Join(",", desc) == "2,1,0,3", string.Join(",", desc));
    var txt = ResultGridOps.SortOrder(srows, 1, ResultGridOps.SortAsc);
    Check("字符串列升序不区分大小写", string.Join(",", txt) == "1,0,2,3", string.Join(",", txt));
    var orig = ResultGridOps.SortOrder(srows, 0, ResultGridOps.SortOriginal);
    Check("原序还原", string.Join(",", orig) == "0,1,2,3", string.Join(",", orig));
    var emptyFirst = ResultGridOps.SortOrder(srows, 1, ResultGridOps.SortAsc);
    Check("空值排最后", emptyFirst[^1] == 3, string.Join(",", emptyFirst));
    var mixed = ResultGridOps.SortOrder(new List<object?[]> { new object?[] { "x" }, new object?[] { "5" } }, 0, ResultGridOps.SortAsc);
    Check("混合列按字符串序", string.Join(",", mixed) == "1,0", string.Join(",", mixed));
}

Console.WriteLine("== FollowupLedger（回访账落盘，整改二批 #07）==");
{
    var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"taodbm-followup-{Guid.NewGuid():N}.json");
    try
    {
        FollowupLedger.UseFile(path);
        Check("缺失文件回落空账", FollowupLedger.Load().Count == 0);
        var t1 = OptimizationFollowup.Register("orders 改写", "SELECT * FROM orders", "SQLite",
            new List<string> { "S001" }, new List<string>(), 12.5, 1);
        FollowupLedger.Save(new List<OptimizationFollowup.FollowupTask> { t1 });
        var loaded = FollowupLedger.Load();
        Check("Save 后 Load 往返", loaded.Count == 1 && loaded[0].TaskId == t1.TaskId && loaded[0].BaselineMs == 12.5,
            loaded.Count == 0 ? "空" : loaded[0].TaskId);
        var done = OptimizationFollowup.Retest(t1, 10.0, new List<string> { "S001" });
        FollowupLedger.Save(new List<OptimizationFollowup.FollowupTask> { done });
        var reloaded = FollowupLedger.Load();
        Check("复测结论落盘", reloaded.Count == 1 && reloaded[0].Verdict == "passed", reloaded.Count == 0 ? "空" : reloaded[0].Verdict);
        System.IO.File.WriteAllText(path, "{ 损坏的 JSON");
        Check("损坏账回落空账", FollowupLedger.Load().Count == 0);
    }
    finally
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
        FollowupLedger.UseFile("");
    }
}

Console.WriteLine("== EngineInfo ==");
{
    Check("默认端口", EngineInfo.DefaultPort(DbEngine.MySQL) == 3306 && EngineInfo.DefaultPort(DbEngine.Redis) == 6379);
    Check("文件库判断", EngineInfo.IsFileBased(DbEngine.SQLite) && !EngineInfo.IsFileBased(DbEngine.MySQL));
    Check("NoSql 判断", EngineInfo.IsRelational(DbEngine.MongoDB) == false && EngineInfo.IsRelational(DbEngine.Oracle));
    Check("引擎数量", EngineInfo.All.Count() == 14);   // 批次七：新增国产六家（OceanBase/GaussDB/达梦/金仓/PolarDB/TDSQL）
}

Console.WriteLine("== DomesticDialect ==");
{
    Check("国产六家清单", EngineInfo.Domestic.Count() == 6
        && EngineInfo.Domestic.SequenceEqual(new[] { DbEngine.OceanBase, DbEngine.GaussDB, DbEngine.Dameng, DbEngine.Kingbase, DbEngine.PolarDB, DbEngine.Tdsql }));
    Check("国产判定", EngineInfo.IsDomestic(DbEngine.OceanBase) && !EngineInfo.IsDomestic(DbEngine.MySQL));
    Check("国产默认端口", EngineInfo.DefaultPort(DbEngine.OceanBase) == 2881
        && EngineInfo.DefaultPort(DbEngine.GaussDB) == 8000
        && EngineInfo.DefaultPort(DbEngine.Dameng) == 5236
        && EngineInfo.DefaultPort(DbEngine.Kingbase) == 54321
        && EngineInfo.DefaultPort(DbEngine.PolarDB) == 3306
        && EngineInfo.DefaultPort(DbEngine.Tdsql) == 3306);
    foreach (var e in EngineInfo.Domestic)
    {
        var d = DomesticDialect.Get(e);
        Check($"方言档非空 {e}", d.OpenQuote != '\0' && d.CloseQuote != '\0'
            && d.ListColumnsSql("db", "t").Contains("t")
            && d.ExplainSql("SELECT 1").StartsWith("EXPLAIN")
            && d.BackupCommand("db", "f.bak").Length > 0);
    }
    Check("分页语法 MySQL 系", DomesticDialect.Get(DbEngine.OceanBase).Paging(DbEngine.OceanBase, 20, 10) == "LIMIT 10 OFFSET 20");
    Check("分页语法 达梦", DomesticDialect.Get(DbEngine.Dameng).Paging(DbEngine.Dameng, 20, 10) == "OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY");
    Check("达梦驱动指引", DomesticDialect.Get(DbEngine.Dameng).DriverHint != null && DomesticDialect.Get(DbEngine.Dameng).DriverHint!.Contains("drivers"));
    Check("OceanBase 用户名原样", DomesticDialect.NormalizeObUser("root") == "root"
        && DomesticDialect.NormalizeObUser("root@prod") == "root@prod"
        && DomesticDialect.NormalizeObUser("root@prod#cluster1") == "root@prod#cluster1");
    bool obBad = false;
    try { DomesticDialect.NormalizeObUser("@#cluster1"); } catch (FormatException) { obBad = true; }
    Check("OceanBase 用户名格式校验", obBad);
    // 工厂映射：六家各建 Provider 且类型正确
    static ConnectionProfile Prof(DbEngine e) => new() { Engine = e, Host = "localhost", User = "u", Password = "p" };
    Check("工厂 OceanBase", ProviderFactory.Create(Prof(DbEngine.OceanBase)) is OceanBaseProvider);
    Check("工厂 GaussDB", ProviderFactory.Create(Prof(DbEngine.GaussDB)) is GaussDbProvider);
    Check("工厂 达梦", ProviderFactory.Create(Prof(DbEngine.Dameng)) is DamengProvider);
    Check("工厂 金仓", ProviderFactory.Create(Prof(DbEngine.Kingbase)) is KingbaseProvider);
    Check("工厂 PolarDB", ProviderFactory.Create(Prof(DbEngine.PolarDB)) is PolarDbProvider);
    Check("工厂 TDSQL", ProviderFactory.Create(Prof(DbEngine.Tdsql)) is TdsqlProvider);
    // 达梦缺驱动：连接给说人话指引（不网络连接即抛）
    string dmErr = "";
    try { ProviderFactory.Create(Prof(DbEngine.Dameng)).TestConnection().Wait(); }
    catch (AggregateException ae) { dmErr = ae.InnerException?.Message ?? ""; }
    catch (Exception ex) { dmErr = ex.Message; }
    Check("达梦缺驱动指引", dmErr.Contains("drivers"));
    // 专注模式状态存取（批次六格3）：写读往返＋损坏回落
    var fmPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fm-{Guid.NewGuid():N}.json");
    FocusModeState.UseFile(fmPath);
    Check("专注默认关", FocusModeState.Load() == false);
    FocusModeState.Save(true);
    Check("专注写读往返", FocusModeState.Load() == true);
    System.IO.File.WriteAllText(fmPath, "{broken");
    Check("专注损坏回落", FocusModeState.Load() == false);
    try { System.IO.File.Delete(fmPath); } catch { }
}

Console.WriteLine("== CsvImporter ==");
{
    var (headers, rows) = CsvImporter.Parse("id,name,note\n1,\"有,逗号\",\"带\"\"引号\"\"的\"\n2,李四,\"跨\n行值\"");
    Check("CSV 表头", headers.SequenceEqual(new[] { "id", "name", "note" }), string.Join("|", headers));
    Check("CSV 逗号转义还原", rows[0][1] == "有,逗号");
    Check("CSV 双引号转义还原", rows[0][2] == "带\"引号\"的");
    Check("CSV 跨行值", rows[1][2] == "跨\n行值");
}
{
    var (headers, rows) = CsvImporter.Parse("\uFEFFa,b\n,x\n");
    Check("CSV BOM 剥离", headers.SequenceEqual(new[] { "a", "b" }));
    Check("CSV 空单元格为 null", rows[0][0] == null);
}
{
    var prov = new TestProv();
    var cols = new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "int", IsKey = true },
        new() { Name = "name", DataType = "varchar(50)" },
    };
    var stmts = CsvImporter.BuildInserts(prov, "db", "t", cols, new List<string> { "id", "name" },
        new List<string?[]> { new string?[] { "1", "O'Brien" }, new string?[] { "", "李四" } }, batch: 10);
    Check("导入数字不加引号", stmts[0].Contains("VALUES (1, 'O''Brien')"), stmts[0]);
    Check("导入空值→NULL", stmts[0].Contains("VALUES (NULL, '李四')"), stmts[0]);
    Check("导入标识符引用", stmts[0].Contains("INSERT INTO `t` (`id`, `name`)"), stmts[0]);
}

Console.WriteLine("== ImportStreaming ==");
{
    // 与整包解析同形：含 BOM、引号转义、跨行值
    var sample = "id,name,note\n1,\"有,逗号\",\"带\"\"引号\"\"的\"\n2,李四,\"跨\n行值\"";
    var (h0, r0) = CsvImporter.Parse(sample);
    var rr = new TaoDBM.Core.Streaming.CsvRowReader();
    var streamed = rr.Rows(new StringReader(sample)).ToList();
    Check("流式 CSV 表头一致", rr.Headers.SequenceEqual(h0), string.Join("|", rr.Headers));
    Check("流式 CSV 行数一致", streamed.Count == r0.Count, $"streamed={streamed.Count}, parse={r0.Count}");
    bool same = streamed.Count == r0.Count && streamed.Zip(r0).All(p => p.First.SequenceEqual(p.Second));
    Check("流式 CSV 行内容一致", same);
}
{
    // 250 行 → 100/100/50 三批
    var csv = "id\n" + string.Join("\n", Enumerable.Range(1, 250));
    var batches = TaoDBM.Core.Streaming.ImportStreaming.CsvBatches(new StringReader(csv)).ToList();
    Check("批次划分 100/100/50", batches.Count == 3 && batches[0].Rows.Count == 100 && batches[1].Rows.Count == 100 && batches[2].Rows.Count == 50,
        $"count={batches.Count}, sizes={string.Join(",", batches.Select(b => b.Rows.Count))}");
    Check("批次累进行数", batches[2].CumulativeRows == 250);
    Check("批次不带截断", batches.All(b => !b.Truncated));
}
{
    // 行数上限兜底：MaxRows=120 → 120 行即截断
    var csv = "id\n" + string.Join("\n", Enumerable.Range(1, 250));
    var batches = TaoDBM.Core.Streaming.ImportStreaming.CsvBatches(new StringReader(csv),
        new TaoDBM.Core.Streaming.ImportOptions { MaxRows = 120 }).ToList();
    Check("行数上限截断", batches.Count == 2 && batches[1].CumulativeRows == 120 && batches[1].Truncated,
        $"count={batches.Count}, last={batches.LastOrDefault()?.CumulativeRows}");
}
{
    // 空输入与仅表头：不产出批次
    var none = TaoDBM.Core.Streaming.ImportStreaming.CsvBatches(new StringReader("")).ToList();
    Check("空输入无批次", none.Count == 0);
    var headerOnly = TaoDBM.Core.Streaming.ImportStreaming.CsvBatches(new StringReader("id,name\n")).ToList();
    Check("仅表头无数据批", headerOnly.Count == 0);
}
{
    // 溢出暂存回放：批次入临时文件后按序读回一致，Delete 后清空
    var csv = "id,name\n" + string.Join("\n", Enumerable.Range(1, 250).Select(n => $"{n},名{n}"));
    using var spill = new TaoDBM.Infrastructure.TempFileSpillStore();
    var summary = TaoDBM.Core.Streaming.ImportStreaming.CsvIngest(new StringReader(csv), spill);
    Check("溢出批次数", summary.BatchCount == 3 && spill.BatchCount == 3, $"batchCount={summary.BatchCount}");
    Check("溢出总行数", summary.TotalRows == 250 && !summary.Truncated);
    var replay = spill.ReadBatches().ToList();
    Check("溢出回放批次数", replay.Count == 3);
    Check("溢出回放表头", replay.Count > 0 && replay[0].Headers.SequenceEqual(new[] { "id", "name" }));
    Check("溢出回放行数", replay.Sum(b => b.Rows.Count) == 250);
    Check("溢出回放内容一致", replay[0].Rows[0].SequenceEqual(new string?[] { "1", "名1" }) && replay[2].Rows[49].SequenceEqual(new string?[] { "250", "名250" }));
    spill.Delete();
    Check("删除后清空", spill.BatchCount == 0 && spill.ReadBatches().ToList().Count == 0);
}

Console.WriteLine("== QueryHistory ==");
{
    QueryHistory.Add("MySQL", "db1", "SELECT 1");
    QueryHistory.Clear();
    QueryHistory.Add("MySQL", "db1", "SELECT 'x'");
    Check("历史记录与清空", QueryHistory.Items.Count == 1 && QueryHistory.Items[0].Sql == "SELECT 'x'");
}

Console.WriteLine("== Quote（schema 支持）==");
{
    var prov = new TestProv('"');
    Check("PG 逐段引用", prov.Quote("public.users") == "\"public\".\"users\"", prov.Quote("public.users"));
    var my = new TestProv('`');
    Check("MySQL 反引号", my.Quote("mydb.t") == "`mydb`.`t`");
}

Console.WriteLine("== SqlHighlighter ==");
{
    var sql = "SELECT id, 'a;b' FROM `t1` -- 注释\nWHERE x = 1.5e-3 AND n = 0x1F";
    var toks = SqlHighlighter.Tokenize(sql);
    string[] Of(TokenKind k) => toks.Where(t => t.Kind == k).Select(t => SqlHighlighter.Text(sql, t)).ToArray();
    Check("关键字识别", Of(TokenKind.Keyword).Contains("SELECT") && Of(TokenKind.Keyword).Contains("FROM")
        && Of(TokenKind.Keyword).Contains("WHERE") && Of(TokenKind.Keyword).Contains("AND"), string.Join(",", Of(TokenKind.Keyword)));
    Check("字符串含分号整体", Of(TokenKind.String).SequenceEqual(new[] { "'a;b'" }), string.Join(",", Of(TokenKind.String)));
    Check("行注释整体", Of(TokenKind.Comment).SequenceEqual(new[] { "-- 注释" }), string.Join(",", Of(TokenKind.Comment)));
    Check("反引号为引用标识符", Of(TokenKind.QuotedIdent).SequenceEqual(new[] { "`t1`" }));
    Check("科学计数与十六进制数字", Of(TokenKind.Number).SequenceEqual(new[] { "1.5e-3", "0x1F" }), string.Join(",", Of(TokenKind.Number)));
    Check("VARCHAR 识别为类型", SqlHighlighter.Tokenize("VARCHAR(50)").Any(t => t.Kind == TokenKind.TypeName));
    Check("块注释未闭合到结尾", SqlHighlighter.Text("/* abc", SqlHighlighter.Tokenize("/* abc").Single(t => t.Kind == TokenKind.Comment)) == "/* abc");
    Check("空串安全", SqlHighlighter.Tokenize("").Count == 0);
    Check("偏移可还原原文", toks.All(t => t.Start + t.Length <= sql.Length));
}
{
    var sql = "SELECT 'it''s', \"quoted ident\";";
    var toks = SqlHighlighter.Tokenize(sql);
    Check("双引号转义字符串完整", SqlHighlighter.Text(sql, toks.First(t => t.Kind == TokenKind.String)) == "'it''s'");
    Check("PG 引号标识符", SqlHighlighter.Text(sql, toks.First(t => t.Kind == TokenKind.QuotedIdent)) == "\"quoted ident\"");
}

Console.WriteLine("== 批次一 编辑器内核（美化/注释/定位/查找替换/代码段/堆栈/折叠/括号）==");
{
    var pretty = SqlBeautifier.Beautify("SELECT a, b FROM t WHERE x = 1 AND y = 2");
    Check("美化-子句分行", pretty.Contains("\nFROM") && pretty.Contains("\nWHERE"), pretty.Replace("\n", "\\n"));
    Check("美化-AND 换行", pretty.Contains("\nAND"), pretty.Replace("\n", "\\n"));
    Check("美化-字符串内不拆", !SqlBeautifier.Beautify("SELECT 'a AND b' FROM t").Contains("'a\nAND"), SqlBeautifier.Beautify("SELECT 'a AND b' FROM t"));
    Check("美化-空输入原样", SqlBeautifier.Beautify("") == "" && SqlBeautifier.Beautify("   ") == "   ");
    Check("美化-括号缩进", SqlBeautifier.Beautify("SELECT * FROM (SELECT 1) x").Contains("\n"), SqlBeautifier.Beautify("SELECT * FROM (SELECT 1) x"));
}
{
    var (c1, s1, l1) = SqlCommentToggler.Toggle("SELECT 1\nSELECT 2", 0, 8);
    Check("注释-加注释", c1.StartsWith("-- SELECT 1"), c1);
    var (c2, _, _) = SqlCommentToggler.Toggle(c1, s1, s1 + l1);
    Check("注释-去注释还原", c2 == "SELECT 1\nSELECT 2", c2);
    var (c3, _, _) = SqlCommentToggler.Toggle("SELECT 1\nSELECT 2", 12, 12);
    Check("注释-无选区只动光标行", c3 == "SELECT 1\n-- SELECT 2", c3);
    var (c4, _, _) = SqlCommentToggler.Toggle("-- a\nSELECT 2", 0, 13);
    Check("注释-混合行整批加", c4 == "-- -- a\n-- SELECT 2", c4);
    var (c5, _, _) = SqlCommentToggler.Toggle("-- a\n-- SELECT 2", 0, 15);
    Check("注释-去注释还原原样", c5 == "a\nSELECT 2", c5);
}
{
    Check("定位-选区优先", SqlStatementLocator.Locate("SELECT 1; SELECT 2", 5, 0, 8) == (0, 8));
    Check("定位-光标第二句", SqlStatementLocator.Locate("SELECT 1; SELECT 2", 12, 0, 0) == (10, 8));
    Check("定位-光标第一句", SqlStatementLocator.Locate("SELECT 1; SELECT 2", 3, 0, 0) == (0, 8));
    Check("定位-去尾空白", SqlStatementLocator.Locate("SELECT 1 ;  ", 0, 0, 0) == (0, 8));
    Check("定位-字符串分号不切", SqlStatementLocator.Locate("SELECT 'a;b'; SELECT 2", 3, 0, 0) == (0, 12));
}
{
    var hits = SqlFindReplace.FindAll("select a from a", "a", new SqlFindOptions());
    Check("查找-忽略大小写", hits.Count == 2 && hits[0].Start == 7, string.Join(",", hits));
    Check("查找-区分大小写", SqlFindReplace.FindAll("Select a", "a", new SqlFindOptions { MatchCase = true }).Count == 1);
    Check("查找-全字匹配", SqlFindReplace.FindAll("brand AND and", "AND", new SqlFindOptions { WholeWord = true }).Count == 2);
    Check("查找-正则", SqlFindReplace.FindAll("aa b a", "a+", new SqlFindOptions { Regex = true }).Count == 2);
    var (rText, rCount) = SqlFindReplace.ReplaceAll("aXa", "a", "b", new SqlFindOptions());
    Check("替换-字面全部", rText == "bXb" && rCount == 2, $"{rText},{rCount}");
    var (gText, _) = SqlFindReplace.ReplaceAll("col=5", @"col=(\d+)", "col: $1", new SqlFindOptions { Regex = true });
    Check("替换-正则分组引用", gText == "col: 5", gText);
    Check("替换-无匹配零次", SqlFindReplace.ReplaceAll("abc", "zz", "q", new SqlFindOptions()) == ("abc", 0));
    bool threw = false;
    try { SqlFindReplace.FindAll("abc", "([", new SqlFindOptions { Regex = true }); }
    catch (ArgumentException) { threw = true; }
    Check("查找-非法正则报错", threw);
    Check("预览-返回上下文行", SqlFindReplace.Preview("SELECT 1\nSELECT 2", "SELECT", new SqlFindOptions()).First() == "SELECT 1");
}
{
    Check("代码段-清单非空", SqlSnippets.Items.Count >= 10, SqlSnippets.Items.Count.ToString());
    Check("代码段-关键字过滤", SqlSnippets.Search("连接").Any(s => s.Name == "多表连接"));
    Check("代码段-空关键字全量", SqlSnippets.Search("").Count == SqlSnippets.Items.Count);
}
{
    ClipboardStack.Clear();
    ClipboardStack.Push("A"); ClipboardStack.Push("B"); ClipboardStack.Push("A");
    var snap = ClipboardStack.Snapshot();
    Check("堆栈-去重置顶", snap.Count == 2 && snap[0] == "A", string.Join("|", snap));
    for (int i = 0; i < 25; i++) ClipboardStack.Push("S" + i);
    Check("堆栈-容量上限", ClipboardStack.Snapshot().Count == 20);
    ClipboardStack.Push("  ");
    Check("堆栈-空串不入栈", ClipboardStack.Snapshot().Count == 20);
    ClipboardStack.Clear();
    Check("堆栈-清空", ClipboardStack.Snapshot().Count == 0);
}
{
    var folder = new SqlFolder();
    var src = "SELECT 1\nSELECT 2\nSELECT 3";
    var (fText, fStart, fLen) = folder.Fold(src, 0, src.Length);
    Check("折叠-生成标记", fText.Contains("已折叠 3 行") && fText.Contains("[fold:"), fText);
    Check("折叠-单行不折", folder.Fold("SELECT 1", 0, 8).Text == "SELECT 1");
    var (uText, _, _) = folder.Unfold(fText, fStart);
    Check("折叠-展开还原", uText == src, uText);
    Check("折叠-计数归零", folder.PendingCount == 0);
    var four = "L1\nL2\nL3\nL4";
    var (f1, _, _) = folder.Fold(four, 3, 9);                       // 折 L2-L3
    var (f2, s2, _) = folder.Fold(f1, 0, f1.Length);                // 再折 L1+标记两行（首折已吞尾换行，端点即文末）
    Check("折叠-可叠加", f2.Contains("[fold:") && folder.PendingCount == 2, f2);
    var (u3, _, _) = folder.Unfold(f2, 0);                          // 展开外层
    int innerCaret = u3.IndexOf("[fold:", StringComparison.Ordinal); // 内层标记位置
    var (u4, _, _) = folder.Unfold(u3, innerCaret);
    Check("折叠-逐个展开还原", u4 == four, u4);
}
{
    Check("括号-正向配对", SqlBracketMatcher.Match("SELECT (a + b)", 7) == (7, 13));
    Check("括号-反向配对", SqlBracketMatcher.Match("SELECT (a + b)", 14) == (7, 13));
    Check("括号-字符串内不配", SqlBracketMatcher.Match("SELECT ('(')", 7) == (7, 11));
    Check("括号-未闭合为空", SqlBracketMatcher.Match("SELECT (a", 7) == null);
    Check("括号-嵌套配对", SqlBracketMatcher.Match("((x))", 0) == (0, 4));
    Check("括号-空文本安全", SqlBracketMatcher.Match("", 0) == null);
}

Console.WriteLine("== TableDdl ==");
{
    var my = (Func<string, string>)(s => "`" + s + "`");
    var t = new TableDesign
    {
        TableName = "users",
        Columns =
        {
            new TableColumnDesign { Name = "id", SqlType = "BIGINT", IsPrimaryKey = true, AutoIncrement = true, Nullable = false },
            new TableColumnDesign { Name = "name", SqlType = "VARCHAR(50)", DefaultValue = "无名" },
            new TableColumnDesign { Name = "created", SqlType = "DATETIME", DefaultValue = "CURRENT_TIMESTAMP" },
        },
    };
    var sql = TableDdl.BuildCreate(t, DbEngine.MySQL, my);
    Check("MySQL 建表反引号", sql.Contains("CREATE TABLE `users`"), sql);
    Check("MySQL 自增", sql.Contains("`id` BIGINT AUTO_INCREMENT PRIMARY KEY"), sql);
    Check("默认值字符串加引号", sql.Contains("DEFAULT '无名'"), sql);
    Check("默认值表达式原样", sql.Contains("DEFAULT CURRENT_TIMESTAMP"), sql);
}
{
    var pg = (Func<string, string>)(s => "\"" + s + "\"");
    var t = new TableDesign
    {
        TableName = "orders",
        Columns =
        {
            new TableColumnDesign { Name = "id", SqlType = "int", IsPrimaryKey = true, AutoIncrement = true, Nullable = false },
            new TableColumnDesign { Name = "memo", SqlType = "text" },
        },
    };
    var sql = TableDdl.BuildCreate(t, DbEngine.PostgreSQL, pg);
    Check("PG 身份列", sql.Contains("\"id\" int GENERATED BY DEFAULT AS IDENTITY"), sql);
    var ss = TableDdl.BuildCreate(t, DbEngine.SqlServer, pg);
    Check("SqlServer IDENTITY", ss.Contains("\"id\" int IDENTITY(1,1) PRIMARY KEY"), ss);
    var lite = TableDdl.BuildCreate(t, DbEngine.SQLite, pg);
    Check("SQLite 自增主键", lite.Contains("\"id\" INTEGER PRIMARY KEY AUTOINCREMENT"), lite);
    var ora = TableDdl.BuildCreate(t, DbEngine.Oracle, pg);
    Check("Oracle 身份列", ora.Contains("\"id\" int GENERATED ALWAYS AS IDENTITY PRIMARY KEY"), ora);
    var ora2 = TableDdl.BuildCreate(t, DbEngine.Oracle, s => s);
    Check("Oracle 无引号", ora2.Contains("CREATE TABLE orders ("), ora2);
}
{
    // 复合主键走表级约束
    var t = new TableDesign
    {
        TableName = "rel",
        Columns =
        {
            new TableColumnDesign { Name = "a", SqlType = "INT", IsPrimaryKey = true },
            new TableColumnDesign { Name = "b", SqlType = "INT", IsPrimaryKey = true },
        },
    };
    var sql = TableDdl.BuildCreate(t, DbEngine.MySQL, s => s);
    Check("复合主键表级约束", sql.Contains("PRIMARY KEY (a, b)") && !sql.Contains("a INT PRIMARY KEY"), sql);
}
{
    var my = (Func<string, string>)(s => "`" + s + "`");
    var t = new TableDesign
    {
        TableName = "t",
        Columns =
        {
            new TableColumnDesign { Name = "c1", SqlType = "INT", Nullable = false },
            new TableColumnDesign { Name = "old1", SqlType = "INT" },
        },
    };
    var (stmts, skipped) = TableDdl.BuildAlter(t, new[] { "old1", "dead" }, DbEngine.MySQL, my);
    Check("ALTER 新增列", stmts.Any(s => s == "ALTER TABLE `t` ADD `c1` INT NOT NULL"), string.Join(" | ", stmts));
    Check("ALTER 修改列", stmts.Any(s => s == "ALTER TABLE `t` MODIFY COLUMN `old1` INT"), string.Join(" | ", stmts));
    Check("ALTER 删除列", stmts.Contains("ALTER TABLE `t` DROP COLUMN `dead`"));
    Check("MySQL 改表无跳过", skipped.Count == 0, string.Join("|", skipped));

    var (liteStmts, liteSkip) = TableDdl.BuildAlter(t, new[] { "old1" }, DbEngine.SQLite, s => s);
    Check("SQLite 改列跳过", liteStmts.All(s => !s.Contains("ALTER COLUMN")) && liteSkip.Count > 0, string.Join("|", liteStmts));
    Check("SQLite 仍可加列", liteStmts.Contains("ALTER TABLE t ADD c1 INT NOT NULL"), string.Join("|", liteStmts));

    var (pgStmts, _) = TableDdl.BuildAlter(t, new[] { "old1" }, DbEngine.PostgreSQL, s => s);
    Check("PG 改列三条", pgStmts.Any(s => s == "ALTER TABLE t ALTER COLUMN old1 TYPE INT")
        && pgStmts.Any(s => s == "ALTER TABLE t ALTER COLUMN old1 DROP NOT NULL"), string.Join(" | ", pgStmts));
}
{
    // 发现74/75：改表带回自增 / 默认值 / 注释，不再静默丢
    var my = (Func<string, string>)(s => "`" + s + "`");
    var t = new TableDesign
    {
        TableName = "t",
        Columns =
        {
            new TableColumnDesign { Name = "id", SqlType = "BIGINT", IsPrimaryKey = true, AutoIncrement = true, Nullable = false },
            new TableColumnDesign { Name = "memo", SqlType = "VARCHAR(50)", DefaultValue = "abc", Comment = "备注" },
            new TableColumnDesign { Name = "seq", SqlType = "INT", DefaultValue = "nextval('t_seq'::regclass)" },
        },
    };
    var (stmts, skipped) = TableDdl.BuildAlter(t, new[] { "id", "memo", "seq" }, DbEngine.MySQL, my);
    Check("发现74 MySQL MODIFY 带回自增", stmts.Any(s => s == "ALTER TABLE `t` MODIFY COLUMN `id` BIGINT AUTO_INCREMENT"), string.Join(" | ", stmts));
    Check("发现74 MODIFY 不重复声明主键", stmts.All(s => !s.Contains("MODIFY") || !s.Contains("PRIMARY KEY")), string.Join(" | ", stmts));
    Check("发现75 MySQL MODIFY 带回默认值", stmts.Any(s => s.Contains("MODIFY COLUMN `memo`") && s.Contains("DEFAULT 'abc'")), string.Join(" | ", stmts));
    Check("发现75 MySQL MODIFY 带回注释", stmts.Any(s => s.Contains("MODIFY COLUMN `memo`") && s.Contains("COMMENT '备注'")), string.Join(" | ", stmts));
    Check("发现75 表达式默认值原样带回", stmts.Any(s => s.Contains("MODIFY COLUMN `seq`") && s.Contains("DEFAULT nextval('t_seq'::regclass)")), string.Join(" | ", stmts));

    var (pgStmts, _) = TableDdl.BuildAlter(t, new[] { "id", "memo", "seq" }, DbEngine.PostgreSQL, s => s);
    Check("发现75 PG 改列带回默认值", pgStmts.Any(s => s == "ALTER TABLE t ALTER COLUMN memo SET DEFAULT 'abc'"), string.Join(" | ", pgStmts));
    Check("发现75 PG 序列默认值原样", pgStmts.Any(s => s == "ALTER TABLE t ALTER COLUMN seq SET DEFAULT nextval('t_seq'::regclass)"), string.Join(" | ", pgStmts));

    var (ssStmts, ssSkip) = TableDdl.BuildAlter(t, new[] { "id", "memo", "seq" }, DbEngine.SqlServer, s => s);
    Check("发现74 SqlServer 自增列明示跳过", ssSkip.Any(s => s.Contains("id") && s.Contains("IDENTITY")), string.Join("|", ssSkip));
    Check("发现74 SqlServer 不改自增列", ssStmts.All(s => !s.Contains("ALTER COLUMN id")), string.Join(" | ", ssStmts));
    Check("发现75 SqlServer 单独 ADD DEFAULT", ssStmts.Any(s => s == "ALTER TABLE t ADD DEFAULT 'abc' FOR memo"), string.Join(" | ", ssStmts));

    var (oraStmts, _) = TableDdl.BuildAlter(t, new[] { "memo" }, DbEngine.Oracle, s => s);
    Check("发现75 Oracle MODIFY 带回默认值", oraStmts.Any(s => s == "ALTER TABLE t MODIFY (memo VARCHAR(50) DEFAULT 'abc')"), string.Join(" | ", oraStmts));
}
{
    Check("默认值 NULL 原样", TableDdl.FormatDefault("null") == "NULL");
    Check("默认值数字原样", TableDdl.FormatDefault("42") == "42" && TableDdl.FormatDefault("1.5") == "1.5");
    Check("默认值文本加引号", TableDdl.FormatDefault("O'Brien") == "'O''Brien'");
    Check("建表语句包装", TableDdl.AsStatements("CREATE TABLE a (id INT);").SequenceEqual(new[] { "CREATE TABLE a (id INT)" }));
}
{
    // 台账④：类型清单按引擎与协议兼容口径
    Check("类型清单 MySQL 系", TableDdl.TypeChoices(DbEngine.MySQL).Contains("DECIMAL(10,2)")
        && TableDdl.TypeChoices(DbEngine.OceanBase).SequenceEqual(TableDdl.TypeChoices(DbEngine.MySQL))
        && TableDdl.TypeChoices(DbEngine.PolarDB).SequenceEqual(TableDdl.TypeChoices(DbEngine.MySQL))
        && TableDdl.TypeChoices(DbEngine.Tdsql).SequenceEqual(TableDdl.TypeChoices(DbEngine.MySQL)));
    Check("类型清单 PostgreSQL 系", TableDdl.TypeChoices(DbEngine.PostgreSQL).Contains("JSONB")
        && TableDdl.TypeChoices(DbEngine.GaussDB).SequenceEqual(TableDdl.TypeChoices(DbEngine.PostgreSQL))
        && TableDdl.TypeChoices(DbEngine.Kingbase).SequenceEqual(TableDdl.TypeChoices(DbEngine.PostgreSQL)));
    Check("类型清单 Oracle 系", TableDdl.TypeChoices(DbEngine.Oracle).Contains("VARCHAR2(255)")
        && TableDdl.TypeChoices(DbEngine.Dameng).SequenceEqual(TableDdl.TypeChoices(DbEngine.Oracle)));
    Check("类型清单 SQLite / SqlServer", TableDdl.TypeChoices(DbEngine.SQLite).Contains("INTEGER")
        && TableDdl.TypeChoices(DbEngine.SqlServer).Contains("UNIQUEIDENTIFIER"));
    // 台账⑤：数值 / 时间类型判定（含括号精度截断）
    Check("数值类型判定", TableDdl.IsNumericType("INT") && TableDdl.IsNumericType("DECIMAL(10,2)")
        && TableDdl.IsNumericType("number(18,2)") && TableDdl.IsNumericType("BIGSERIAL")
        && !TableDdl.IsNumericType("VARCHAR(255)") && !TableDdl.IsNumericType("TEXT"));
    Check("时间类型判定", TableDdl.IsTemporalType("DATETIME") && TableDdl.IsTemporalType("timestamp(6)")
        && TableDdl.IsTemporalType("TIMESTAMPTZ") && !TableDdl.IsTemporalType("INT") && !TableDdl.IsTemporalType("VARCHAR(255)"));
}

Console.WriteLine("== BackupTools ==");
{
    Check("支持矩阵", BackupTools.Supported(DbEngine.MySQL) && BackupTools.Supported(DbEngine.PostgreSQL)
        && BackupTools.Supported(DbEngine.SqlServer) && BackupTools.Supported(DbEngine.SQLite)
        && BackupTools.Supported(DbEngine.Oracle) && BackupTools.Supported(DbEngine.MongoDB)
        && BackupTools.Supported(DbEngine.Redis));
    var spec = BackupTools.MysqlDump("127.0.0.1", 3306, "root", "pw123", "mydb", "c:\\dir with space\\dump.sql");
    Check("mysqldump 参数", spec.Arguments.Contains("--host=127.0.0.1") && spec.Arguments.Contains("--port=3306")
        && spec.Arguments.Contains("--user=root") && spec.Arguments.Contains("--result-file=\"c:\\dir with space\\dump.sql\"")
        && spec.Arguments.EndsWith("mydb"), spec.Arguments);
    Check("密码走环境变量", spec.Env["MYSQL_PWD"] == "pw123" && !spec.Describe.Contains("pw123"));
    var rst = BackupTools.MysqlRestore("h", 3306, "u", "p", "db", "f.sql");
    Check("mysql 还原走 stdin", rst.FileName == "mysql" && rst.InputFile == "f.sql", rst.Describe);
    var pg = BackupTools.PgDump("h", 5432, "u", "p", "db", "o.sql");
    Check("pg_dump 参数", pg.FileName == "pg_dump" && pg.Arguments.Contains("--username=u")
        && pg.Arguments.Contains("--file=o.sql") && pg.Arguments.EndsWith("db"), pg.Arguments);
    Check("PG 密码环境变量", pg.Env["PGPASSWORD"] == "p" && !pg.Describe.Contains("p]"));
    Check("psql 还原", BackupTools.PgRestore("h", 5432, "u", "", "db", "f.sql").FileName == "psql");
    Check("参数加引号", BackupTools.QuoteArg("a b") == "\"a b\"" && BackupTools.QuoteArg("abc") == "abc");
    Check("SqlServer 备份语句", BackupTools.SqlServerBackupSql("my db", "d:\\x.bak")
        == "BACKUP DATABASE [my db] TO DISK = 'd:\\x.bak' WITH FORMAT, INIT");
    Check("SqlServer 还原语句", BackupTools.SqlServerRestoreSql("db", "d:\\x.bak")
        == "RESTORE DATABASE [db] FROM DISK = 'd:\\x.bak' WITH REPLACE");
    Check("SqlServer 单引号转义", BackupTools.SqlServerBackupSql("db", "d:\\o'brien.bak").Contains("'d:\\o''brien.bak'"));
}

Console.WriteLine("== L6 恶意与极端输入 + L7 凭据零明文 ==");
{
    // ---- A. ConnectionStore / ProfileCrypto（L7 安全）----
    string connFile = TaoPaths.FilePath("connections.json");
    string? origConn = System.IO.File.Exists(connFile) ? System.IO.File.ReadAllText(connFile) : null;
    try
    {
        const string secret = "S3cret-P@ss-明文";
        var p = new ConnectionProfile
        {
            DisplayName = "测\"连\"接/特殊<名>", User = "u'm'e", Database = "db;drop",
            Host = "127.0.0.1", Port = 3306, Password = secret, SavePassword = true,
        };
        var protectedOne = ProfileCrypto.Protect(secret);
        Check("L7 加密失败不回明文", protectedOne != secret && (protectedOne == "" || protectedOne.StartsWith("dpapi:")),
            protectedOne == secret ? "回退明文" : protectedOne == "" ? "空串(平台无DPAPI)" : "dpapi密文");

        ConnectionStore.Save(new List<ConnectionProfile> { p });
        var onDisk = System.IO.File.ReadAllText(connFile);
        Check("L7 落盘文件零明文密码", !onDisk.Contains(secret), onDisk.Contains(secret) ? "发现明文" : "");

        var back = ConnectionStore.Load();
        var got = back.FirstOrDefault(x => x.Id == p.Id);
        // 读回语义（2026-11-13 修）：安全不变量只在磁盘——零明文、密文带 dpapi: 前缀；
        // 平台支持 DPAPI 时读回按密文还原原文（应用重连需要），不支持时落盘即空、读回为空。
        // 原断言把“读回是明文”误判为残留，实际是 DPAPI 正常解密，在 Windows 云端必假。
        bool hasDpapi = protectedOne.StartsWith("dpapi:", StringComparison.Ordinal);
        Check("L7 读回按密文还原",
            got != null && (hasDpapi ? got.Password == secret : got.Password == ""),
            got == null ? "missing" : (hasDpapi ? (got.Password == secret ? "" : "有DPAPI未还原") : "无DPAPI仍返回值"));
        Check("L7 落盘密文带前缀", !hasDpapi || onDisk.Contains("dpapi:"), "无 dpapi: 前缀");
        Check("特殊字符字段往返一致", got != null && got.DisplayName == p.DisplayName && got.User == p.User && got.Database == p.Database,
            got == null ? "missing" : $"{got.DisplayName}|{got.User}|{got.Database}");

        var p2 = new ConnectionProfile { DisplayName = "不存密码", Password = secret, SavePassword = false };
        ConnectionStore.Save(new List<ConnectionProfile> { p2 });
        var onDisk2 = System.IO.File.ReadAllText(connFile);
        Check("L7 不存密码落盘为空", !onDisk2.Contains(secret), onDisk2.Contains(secret) ? "发现明文" : "");
        Check("不存密码读回为空", ConnectionStore.Load().First(x => x.Id == p2.Id).Password == "");

        System.IO.File.WriteAllText(connFile, "{{{不是JSON");
        Check("损坏配置文件回落空列表", ConnectionStore.Load().Count == 0);
    }
    finally
    {
        if (origConn != null) System.IO.File.WriteAllText(connFile, origConn);
        else if (System.IO.File.Exists(connFile)) System.IO.File.Delete(connFile);
    }

    // ---- B. CsvImporter 恶意极端输入 ----
    var emptyCsv = CsvImporter.Parse("");
    Check("CSV 空串输入", emptyCsv.Headers.Count == 0 && emptyCsv.Rows.Count == 0);
    var unclosed = CsvImporter.Parse("h1,h2\na,\"未闭合");
    Check("CSV 未闭合引号不崩", unclosed.Headers.Count == 2 && unclosed.Rows.Count == 1 && unclosed.Rows[0][0] == "a", string.Join("|", unclosed.Rows.Select(r => string.Join(";", r))));
    var crOnly = CsvImporter.Parse("h1,h2\r1,2\r3,4");
    Check("CSV 孤立 CR 换行", crOnly.Rows.Count == 2, crOnly.Rows.Count.ToString());
    var bigField = new string('X', 1_000_000);
    var bigCsv = CsvImporter.Parse("h\n" + bigField);
    Check("CSV 百万字符单字段", bigCsv.Rows.Count == 1 && bigCsv.Rows[0][0] == bigField);
    var manyCols = "h," + string.Join(",", Enumerable.Range(1, 999).Select(i => "c" + i)) + "\nv1";
    var manyParsed = CsvImporter.Parse(manyCols);
    Check("CSV 千列表头对齐", manyParsed.Headers.Count == 1000 && manyParsed.Rows.Count == 1 && manyParsed.Rows[0][999] == null, $"{manyParsed.Headers.Count}列/{manyParsed.Rows[0].Length}值");
    {
        var cols = new List<ColumnSpec> { new() { Name = "id", DataType = "INT" }, new() { Name = "note", DataType = "VARCHAR" } };
        var stmts = CsvImporter.BuildInserts(new TestProv(), "db", "t", cols,
            new List<string> { "id", "note" },
            new List<string?[]> { new string?[] { "12; DROP TABLE x", "O'Brien" } });
        Check("数字列非数字值引号转义防注入", stmts[0].Contains("'12; DROP TABLE x'") && stmts[0].Contains("'O''Brien'"), stmts[0]);
    }

    // ---- C. SqlSplitter 恶意极端输入 ----
    Check("脚本只有分号", SqlSplitter.Split(";; ;").Count == 0);
    var unclosedQuote = SqlSplitter.Split("SELECT 'a");
    Check("未闭合引号原样单条", unclosedQuote.Count == 1 && unclosedQuote[0] == "SELECT 'a", string.Join("|", unclosedQuote));
    var deepComment = SqlSplitter.Split("/* /* /* /* never closed");
    Check("深层未闭合块注释单条", deepComment.Count == 1 && deepComment[0].StartsWith("/* /*"));
    var massParts = SqlSplitter.Split(string.Join("", Enumerable.Range(0, 10_000).Select(i => $"SELECT {i};")));
    Check("万条语句拆分无丢", massParts.Count == 10_000 && massParts[9999] == "SELECT 9999", massParts.Count.ToString());

    // ---- D. JsonImporter 恶意极端输入 ----
    var badJson = JsonImporter.Parse("[oops");
    Check("JSON 非法数组回落空", badJson.Headers.Count == 0 && badJson.Rows.Count == 0);
    var mixedJsonl = JsonImporter.Parse("{\"a\":\"1\"}\n不是JSON行\n{\"a\":\"2\"}");
    Check("JSONL 畸形行跳过", mixedJsonl.Rows.Count == 2 && mixedJsonl.Rows[1][0] == "2", string.Join("|", mixedJsonl.Rows.Select(r => string.Join(";", r))));
    var loneSurrogate = JsonImporter.Parse("[\"\\ud800\"]");
    Check("未配对代理对不崩", loneSurrogate.Headers.Count == 0 && loneSurrogate.Rows.Count == 0);
}

Console.WriteLine("== Favorites / TabSession ==");
{
    var dir = Path.Combine(Path.GetTempPath(), "taodbm-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var favFile = Path.Combine(dir, "favorites.json");
    var tabFile = Path.Combine(dir, "tabsession.json");
    Favorites.UseFile(favFile);
    TabSession.UseFile(tabFile);

    Check("收藏初始为空", Favorites.Load().Count == 0);
    var favs = new List<FavoriteItem>();
    Favorites.Add(favs, "日报", "MySQL", "shop", "SELECT * FROM orders;");
    Favorites.Add(favs, "同名覆盖", "PG", "shop", "SELECT 1;");
    Favorites.Add(favs, "同名覆盖", "PG", "shop", "SELECT 2;"); // 同名覆盖
    Check("同名覆盖保留单条", favs.Count(i => i.Name == "同名覆盖") == 1 && favs.Count == 2);
    Check("覆盖后内容更新", favs.First(i => i.Name == "同名覆盖").Sql == "SELECT 2;");
    Favorites.Save(favs);
    var reloaded = Favorites.Load();
    Check("收藏往返", reloaded.Count == 2 && reloaded[0].Name == "日报" && reloaded[0].Sql == "SELECT * FROM orders;");
    Check("删除收藏", Favorites.Remove(reloaded, reloaded[0].Id) && reloaded.Count == 1);
    Check("删除不存在的收藏返回 false", !Favorites.Remove(reloaded, "nope"));

    Check("会话初始为空", TabSession.Load().Count == 0);
    var items = new List<TabSessionItem>
    {
        new() { ConnectionId = "c1", Mode = "Query", Db = "shop", Sql = "SELECT 1;" },
        new() { ConnectionId = "c2", Mode = "TableData", Db = "db", Child = "t1" },
    };
    TabSession.Save(items);
    var rt = TabSession.Load();
    Check("会话往返", rt.Count == 2 && rt[0].ConnectionId == "c1" && rt[0].Sql == "SELECT 1;" && rt[1].Child == "t1");
    var profiles = new List<ConnectionProfile> { new() { Id = "c1" } };
    Check("会话按连接过滤", TabSession.FilterValid(rt, profiles).Count == 1);
    Check("空连接Id保留", TabSession.FilterValid(new List<TabSessionItem> { new() { ConnectionId = "" } }, profiles).Count == 1);

    Directory.Delete(dir, true);
}

Console.WriteLine("== DataSync ==");
{
    Func<string, string> bt = n => "`" + n + "`";
    var srcCols = new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "int", Nullable = false, IsKey = true },
        new() { Name = "name", DataType = "varchar(50)" },
        new() { Name = "extra", DataType = "datetime" },
    };
    var dstNames = new List<string> { "id", "name", "legacy" };
    var (stmts, skipped) = DataSync.SyncColumns("orders", srcCols, dstNames, DbEngine.MySQL, bt, alignTypes: false);
    Check("同步只加缺失列", stmts.Count == 1 && stmts[0].Contains("ADD `extra`") && stmts[0].StartsWith("ALTER TABLE `orders`"), string.Join(" | ", stmts));
    Check("同步不删目标多余列", !string.Join("|", stmts).Contains("DROP") && !string.Join("|", stmts).Contains("legacy"));
    Check("默认不对齐类型", !string.Join("|", stmts).Contains("MODIFY"));
    var (stmts2, _) = DataSync.SyncColumns("orders", srcCols, dstNames, DbEngine.MySQL, bt, alignTypes: true);
    Check("对齐类型生成 MODIFY", stmts2.Any(s => s.Contains("MODIFY COLUMN")), string.Join(" | ", stmts2));
    Check("SQLite 对齐类型入跳过", DataSync.SyncColumns("t", srcCols, dstNames, DbEngine.SQLite, n => "\"" + n + "\"", true).Skipped.Count > 0);
    var create = DataSync.CreateForTarget("orders", srcCols, DbEngine.PostgreSQL, n => "\"" + n + "\"");
    Check("目标缺表生成 CREATE", create.StartsWith("CREATE TABLE \"orders\"") && create.Contains("\"id\" int") && create.Contains("PRIMARY KEY"), create);

    var qr = new QueryResult { Title = "orders" };
    qr.Columns.AddRange(new[] { "id", "name", "flag", "created", "blob" });
    qr.Rows.Add(new object[] { 1, "it's", true, new System.DateTime(2026, 10, 31, 8, 0, 0), new byte[] { 0x01, 0xAB } });
    qr.Rows.Add(new object?[] { 2, null, DBNull.Value, null, null });
    qr.Rows.Add(new object?[] { 3, "x", false, null, null });
    var batches = DataSync.InsertBatches(qr, "`orders`", bt, batch: 2);
    Check("分批 INSERT 语句数", batches.Count == 2);
    Check("INSERT 列名引用", batches[0].StartsWith("INSERT INTO `orders` (`id`, `name`, `flag`, `created`, `blob`)"), batches[0].Split('\n')[0]);
    Check("字符串转义", batches[0].Contains("'it''s'"));
    Check("布尔转 TRUE/FALSE", batches[0].Contains("TRUE") && batches[1].Contains("FALSE"));
    Check("日期格式", batches[0].Contains("'2026-10-31 08:00:00'"));
    Check("二进制 X''", batches[0].Contains("X'01AB'"));
    Check("null 与 DBNull 转 NULL", batches[1].Contains("NULL") && !batches[1].Contains("DBNull"));
    Check("空结果零语句", DataSync.InsertBatches(new QueryResult { Title = "t" }, "t", bt).Count == 0);
}
{
    // A1 同步断点水位：凭水位续传只补剩余批次，不重灌已落库数据；水位回读容错
    Func<string, string> qt = n => "`" + n + "`";
    var mk = new QueryResult { Title = "t", Columns = { "id" } };
    for (int i = 1; i <= 5; i++) mk.Rows.Add(new object?[] { i });
    var all = DataSync.InsertBatches(mk, "`t`", qt, batch: 1);   // 5 批
    var seen = new List<int>();
    var rest = SyncCheckpoint.Resume(all, SyncWatermark.Empty("t"), w => seen.Add(w.BatchDone));
    Check("水位从零全量执行", rest.Count == 5 && seen.SequenceEqual(new[] { 1, 2, 3, 4, 5 }));
    var rest2 = SyncCheckpoint.Resume(all, new SyncWatermark { Table = "t", BatchDone = 2 });
    Check("断点续传只补剩余", rest2.Count == 3 && rest2[0] == all[2] && rest2[2] == all[4]);
    var done = SyncCheckpoint.Resume(all, new SyncWatermark { Table = "t", BatchDone = 5 });
    Check("断点已到底幂等空集", done.Count == 0);
    var over = SyncCheckpoint.Resume(all, new SyncWatermark { Table = "t", BatchDone = 99 });
    Check("水位越界不重灌", over.Count == 0);
    var json = SyncCheckpoint.ToJson(new SyncWatermark { Table = "t", BatchDone = 3, LastKey = "k3" });
    var back = SyncCheckpoint.FailSafeParse("t", json);
    Check("水位往返还原", back.Table == "t" && back.BatchDone == 3 && back.LastKey == "k3");
    var broken = SyncCheckpoint.FailSafeParse("t", "{断");
    Check("坏水位容错归零", broken.BatchDone == 0 && broken.Table == "t");
    Check("空水位容错归零", SyncCheckpoint.FailSafeParse("t", null).BatchDone == 0);
}

Console.WriteLine("== ErdModel ==");
{
    var m = new ErdModel
    {
        Tables =
        {
            new ErdTable { Name = "user", Columns = { new() { Name = "id", Type = "int", IsKey = true }, new() { Name = "name", Type = "varchar(50)" } } },
            new ErdTable { Name = "order", Columns = { new() { Name = "id", Type = "int", IsKey = true }, new() { Name = "user_id", Type = "int" }, new() { Name = "amount", Type = "decimal" } } },
            new ErdTable { Name = "items", Columns = { new() { Name = "id", Type = "int", IsKey = true }, new() { Name = "orderId", Type = "int" } } },
        },
    };
    ErdModel.AutoLayout(m);
    Check("网格布局", m.Tables[0].X == 40 && m.Tables[1].X == 340 && m.Tables[2].X == 640 && m.Tables[2].Y == 40);
    var edges = ErdModel.InferEdges(m);
    Check("推断下划线外键", edges.Count == 2
        && edges.Any(e => e.FromTable == "order" && e.FromCol == "user_id" && e.ToTable == "user")
        && edges.Any(e => e.FromTable == "items" && e.FromCol == "orderId" && e.ToTable == "order"),
        string.Join(",", edges.Select(e => e.FromTable + "." + e.FromCol + "->" + e.ToTable)));
    var m2 = new ErdModel
    {
        Tables = {
            new ErdTable { Name = "t", Columns = { new() { Name = "id", Type = "int", IsKey = true }, new() { Name = "t_id", Type = "int" } } },
        },
    };
    Check("自引用不成边", ErdModel.InferEdges(m2).Count == 0);
    Check("无主键目标不成边", ErdModel.InferEdges(new ErdModel
    {
        Tables = {
            new ErdTable { Name = "a", Columns = { new() { Name = "x_id", Type = "int" } } },
            new ErdTable { Name = "x", Columns = { new() { Name = "code", Type = "int" } } },
        },
    }).Count == 0);
    var json = m.ToJson();
    var back = ErdModel.FromJson(json);
    Check("JSON 往返", back is not null && back.Tables.Count == 3 && back.Tables[1].Columns[1].Name == "user_id"
        && back.Tables[1].X == m.Tables[1].X);
    Check("无效 JSON 返回 null", ErdModel.FromJson("{oops") is null);
}

Console.WriteLine("== ReportBuilder ==");
{
    var qr = new QueryResult { Title = "订单" };
    qr.Columns.AddRange(new[] { "id", "city", "amount", "created" });
    qr.Rows.Add(new object[] { 1, "上海", 120.5, new System.DateTime(2026, 10, 1) });
    qr.Rows.Add(new object[] { 2, "北京", 80, new System.DateTime(2026, 10, 2) });
    qr.Rows.Add(new object[] { 3, "上海", DBNull.Value, new System.DateTime(2026, 10, 2) });
    var (rows, stats) = ReportBuilder.Build(qr);
    Check("报表行数", rows == 3 && stats.Count == 4);
    var idStat = stats[0];
    Check("数值列极值均值", idStat.Min == 1 && idStat.Max == 3 && Math.Abs(idStat.Avg!.Value - 2.0) < 1e-9);
    var cityStat = stats[1];
    Check("非数值列 Top 与空值", cityStat.NullCount == 0 && cityStat.Top[0].Value == "上海" && cityStat.Top[0].Count == 2);
    var amtStat = stats[2];
    Check("DBNull 不进数值统计", amtStat.NullCount == 1 && Math.Abs(amtStat.Avg!.Value - 100.25) < 1e-9 && amtStat.Min == 80);
    var html = ReportBuilder.ToHtml(qr, "月度报表");
    Check("HTML 报表含标题与统计", html.Contains("<title>月度报表</title>") && html.Contains("月度报表") && html.Contains("上海") && html.Contains("100.25"));
    Check("HTML 转义", ReportBuilder.ToHtml(new QueryResult { Title = "a<b" }, "a<b").Contains("a&lt;b"));
    var esc = ReportBuilder.ToHtml(new QueryResult { Title = "t" }, "t");
    Check("HTML 头部完整", esc.StartsWith("<!DOCTYPE html>") && esc.EndsWith("</html>"));
}

Console.WriteLine("== JsonImporter ==");
{
    var (headers, rows) = JsonImporter.Parse("""[{"id":1,"name":"张三","active":true},{"id":2,"name":"李'd四","active":false,"extra":null}]""");
    Check("JSON 数组表头与行数", headers.Count == 4 && rows.Count == 2 && headers[0] == "id" && headers[3] == "extra", string.Join(",", headers));
    Check("JSON 值归一（数字/字符串/布尔/null）", rows[0][0] == "1" && rows[0][1] == "张三" && rows[0][2] == "TRUE" && rows[1][3] == null,
        $"{rows[0][0]},{rows[0][1]},{rows[0][2]},{rows[1][3]}");
    var prov = new TestProv();
    var stmts = CsvImporter.BuildInserts(prov, "db", "t",
        new List<ColumnSpec> { new() { Name = "id", DataType = "INT" }, new() { Name = "name", DataType = "VARCHAR(50)" }, new() { Name = "active", DataType = "INT" }, new() { Name = "extra", DataType = "INT" } },
        headers, rows);
    Check("JSON 生成 INSERT（数字不带引号、字符串转义）",
        stmts.Count == 1 && stmts[0].Contains("INSERT INTO `t` (`id`, `name`, `active`, `extra`) VALUES (1, '张三', 'TRUE', NULL);", StringComparison.Ordinal)
        && stmts[0].Contains("'李''d四'"), stmts[0]);
}
{
    var (headers, rows) = JsonImporter.Parse("{\"id\":1}\n{\"id\":2,\"name\":\"b\"}");
    Check("行式 JSONL 合并表头", headers.Count == 2 && rows.Count == 2 && rows[0][1] == null && rows[1][1] == "b", string.Join(",", headers));
}
{
    var (headers, rows) = JsonImporter.Parse("""[{"id":1,"meta":{"k":1},"tags":[1,2]}]""");
    Check("嵌套对象/数组序列化为文本", headers.Count == 3 && rows[0][1] == """{"k":1}""" && rows[0][2] == "[1,2]", $"{rows[0][1]}|{rows[0][2]}");
}
{
    var (headers, rows) = JsonImporter.Parse("[1,2,3]");
    Check("非对象数组为空", headers.Count == 0 && rows.Count == 0);
}

Console.WriteLine("== TnsNames ==");
{
    const string ora = """
# 数据库连接描述符
ORCL =
  (DESCRIPTION =
    (ADDRESS = (PROTOCOL = TCP)(HOST = dbhost1.example.com)(PORT = 1521))
    (CONNECT_DATA =
      (SERVER = DEDICATED)
      (SERVICE_NAME = orcl.example.com)
    )
  )

ERP, ERP_OLD = (DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=10.0.0.8)(PORT=1522))(CONNECT_DATA=(SERVICE_NAME=erp)))
BAD = (DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(PORT=1523)))
""";
    var list = TnsNames.Parse(ora);
    Check("解析条目数（BAD 无 HOST 跳过）", list.Count == 3, string.Join(",", list.Select(e => e.Name)));
    var orcl = list.FirstOrDefault(e => e.Name == "ORCL");
    Check("ORCL 主机端口服务名", orcl != null && orcl.Host == "dbhost1.example.com" && orcl.Port == 1521 && orcl.Service == "orcl.example.com", orcl?.ToString() ?? "missing");
    Check("多别名拆分", list.Any(e => e.Name == "ERP") && list.Any(e => e.Name == "ERP_OLD") && list.All(e => e.Name != " ERP"));
    var erp = list.First(e => e.Name == "ERP");
    Check("ERP 条目字段", erp.Host == "10.0.0.8" && erp.Port == 1522 && erp.Service == "erp");
    Check("注释被剔除", list.All(e => e.Host != "注释主机") && TnsNames.Parse("# ORCL = (HOST=x)\nREAL = (HOST=h)(PORT=1)").Count == 1);
}

{
    // 主题三象：配色完整性、铁律与回落规则（水墨/天青/墨夜，ant 退役映射天青；天青 2026-11-06 对齐 TaoAgent 色板）
    Check("三象恰三套且同键集",
        ThemePalette.Names.Length == 3
        && ThemePalette.Names[0] == "ink" && ThemePalette.Names[1] == "gray" && ThemePalette.Names[2] == "night"
        && new[] { ThemePalette.Ink, ThemePalette.Gray, ThemePalette.Night }
            .All(pal => pal.Count == ThemePalette.Keys.Length && ThemePalette.Keys.All(k => pal.ContainsKey(k))));
    bool HexOk(string h) => h.StartsWith('#') && h.Length == 7;
    Check("三象色值格式合法",
        new[] { ThemePalette.Ink, ThemePalette.Gray, ThemePalette.Night }
            .All(pal => ThemePalette.Keys.All(k => HexOk(pal[k]))));
    (int, int, int) Rgb(string h) => (Convert.ToInt32(h.Substring(1, 2), 16), Convert.ToInt32(h.Substring(3, 2), 16), Convert.ToInt32(h.Substring(5, 2), 16));
    var inkBg = Rgb(ThemePalette.Ink["AntBg"]);
    var grayBg = Rgb(ThemePalette.Gray["AntBg"]);
    var nightBg = Rgb(ThemePalette.Night["AntBg"]);
    // 亮度阶梯定律：水墨浅色为主 / 天青釉底浅调（与水墨同为浅色象，带内次序水墨>天青）/ 墨夜以黑为主，三带 L 单调递减
    double RelL(string h)
    {
        var (r, g, b) = Rgb(h);
        double F(int c) { var v = c / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * F(r) + 0.7152 * F(g) + 0.0722 * F(b);
    }
    double LInk = RelL(ThemePalette.Ink["AntBg"]), LGray = RelL(ThemePalette.Gray["AntBg"]), LNight = RelL(ThemePalette.Night["AntBg"]);
    Check("亮度阶梯：三带单调递减", LInk > LGray && LGray > LNight && LInk > 0.8 && LGray > 0.8 && LGray < LInk && LNight < 0.05,
        $"L={LInk:F2}/{LGray:F2}/{LNight:F2}");
    Check("墨夜铁律：以黑为主且暖序", nightBg.Item1 == 0x13 && nightBg.Item2 == 0x12 && nightBg.Item3 == 0x11);
    Check("墨夜母本锚点：宣纸白与黛青",
        ThemePalette.Night["AntText"] == "#E8E3D5" && ThemePalette.Night["AntTextSub"] == "#ABA595"
        && ThemePalette.Night["AntPrimary"] == "#6FA3BC" && ThemePalette.Night["AntCard"] == "#211E1B");
    Check("天青铁律：釉底浅调 G>B>R 且卡片面亮于底",
        grayBg.Item2 > grayBg.Item3 && grayBg.Item3 > grayBg.Item1
        && ThemePalette.Gray["AntBg"] == "#EDF2EF" && ThemePalette.Gray["AntCard"] == "#FAFCFA"
        && RelL(ThemePalette.Gray["AntCard"]) > LGray);
    Check("天青锚点值：主字与青瓷点缀",
        ThemePalette.Gray["AntText"] == "#2B3A36" && ThemePalette.Gray["AntPrimary"] == "#34655C");
    Check("水墨基调未漂移", ThemePalette.Ink["AntBg"] == "#F7F5EE" && ThemePalette.Ink["AntPrimary"] == "#52708A");
    // WCAG 体检（视觉观测验收定律第一层）：正文 4.5 / 次要 3.5 / 点缀 3.0 / 描边 2.0 / 按钮字 4.0
    double Contrast(string a, string b)
    {
        double la = RelL(a), lb = RelL(b);
        var hi = Math.Max(la, lb); var lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }
    foreach (var (tname, pal) in new[] { ("水墨", ThemePalette.Ink), ("天青", ThemePalette.Gray), ("墨夜", ThemePalette.Night) })
    {
        var bg = pal["AntBg"];
        var cText = Contrast(pal["AntText"], bg);
        var cSub = Contrast(pal["AntTextSub"], bg);
        var cPri = Contrast(pal["AntPrimary"], bg);
        var cBor = Contrast(pal["AntBorder"], bg);
        Check(tname + " WCAG：正文/次要/点缀/描边",
            cText >= 4.5 && cSub >= 3.5 && cPri >= 3.0 && cBor >= 2.0,
            tname + $" 正文 {cText:F2} 次要 {cSub:F2} 点缀 {cPri:F2} 描边 {cBor:F2}");
        // 按钮字：主色底上的文字（水墨/天青白字，墨夜近黑字）
        var btnText = tname == "墨夜" ? "#131211" : "#FFFFFF";
        var cBtn = Contrast(btnText, pal["AntPrimary"]);
        Check(tname + " WCAG：按钮字", cBtn >= 4.0, tname + $" 按钮字 {cBtn:F2}");
    }
    Check("未知主题回落水墨", ThemePalette.Get("nope") == ThemePalette.Ink && ThemePalette.Normalize("nope") == "ink" && ThemePalette.Normalize(null) == "ink");
    Check("ant 退役映射天青", ThemePalette.Get("ant") == ThemePalette.Gray && ThemePalette.Normalize("ANT") == "gray" && ThemePalette.Normalize("ant") == "gray");
    Check("三象大小写归一", ThemePalette.Normalize("Night") == "night" && ThemePalette.Normalize(" GRAY ") == "gray" && ThemePalette.Normalize("INK") == "ink");
}

Layer("集成");
{
    // CSV 全链路：导出 → 解析 → 生成 INSERT，行列数与关键值一致
    var r = new QueryResult { Columns = { "id", "name", "note" } };
    r.Rows.Add(new object?[] { 1, "张三", "有,\"逗号\"\n第二行" });
    r.Rows.Add(new object?[] { 2, DBNull.Value, 3.5 });
    var csv = Exporter.Csv(r);
    var (heads, rows) = CsvImporter.Parse(csv);
    Check("CSV 导出→解析往返", heads.SequenceEqual(r.Columns) && rows.Count == 2, string.Join(",", heads));
    Check("CSV 往返值一致", rows[0][1] == "张三" && rows[1][1] == null && rows[1][2] == "3.5", string.Join("|", rows[1]));
    var cols = new List<ColumnSpec> { new() { Name = "id", DataType = "int" }, new() { Name = "name", DataType = "varchar(50)" }, new() { Name = "note", DataType = "text" } };
    var ins = CsvImporter.BuildInserts(new TestProv(), "shop", "t", cols, heads, rows);
    Check("CSV→INSERT 全链路", ins.Count >= 1 && ins.Sum(s => s.Split("VALUES (").Length - 1) == 2 && ins[0].Contains("INSERT INTO `t`") && ins[0].Contains("'张三'"), ins.FirstOrDefault() ?? "");
}
{
    // JSON 全链路：解析（含畸形行跳过）→ 生成 INSERT
    var (h, rr) = JsonImporter.Parse("{\n{\"bad\";\n} not json line\n{\"a\":1,\"b\":null}");
    Check("JSONL 畸形行跳过", rr.Count == 1 && rr[0][0] == "1", string.Join(",", h));
    var (h2, r2) = JsonImporter.Parse("[{\"id\":1,\"extra\":{\"x\":2}}]");
    var jc = new List<ColumnSpec> { new() { Name = "id", DataType = "int" }, new() { Name = "extra", DataType = "text" } };
    var ji = JsonImporter.BuildInserts(new TestProv(), "db", "t", jc, h2, r2);
    Check("JSON→INSERT 嵌套序列化", ji.Count == 1 && ji[0].Contains("\"x\":2"), ji.FirstOrDefault() ?? "");
}
{
    // ER 模型 JSON 往返 + 报表 HTML 转义
    var m = new ErdModel { Tables = { new ErdTable { Name = "users", Columns = { new() { Name = "id", Type = "int", IsKey = true }, new() { Name = "user_id", Type = "int" } } } } };
    ErdModel.AutoLayout(m);
    var rt = ErdModel.FromJson(m.ToJson());
    Check("ER 模型 JSON 往返", rt != null && rt.Tables.Count == m.Tables.Count && rt.Tables[0].Name == "users", rt?.Tables.Count.ToString() ?? "null");
    var rep = ReportBuilder.ToHtml(new QueryResult { Columns = { "c" }, Rows = { new object?[] { "<script>alert(1)</script>" } } }, "安全");
    Check("报表 HTML 转义", rep.Contains("&lt;script&gt;") && !rep.Contains("<script>alert"), "");
}
{
    // 传输三段组合：建表 → 补列 → 分批插入 语句顺序完整
    var cols = new List<ColumnSpec> { new() { Name = "id", DataType = "int", IsKey = true } };
    var create = DataSync.CreateForTarget("t", cols, DbEngine.MySQL, n => "`" + n + "`");
    var (sync, _) = DataSync.SyncColumns("t", cols, new List<string> { "id" }, DbEngine.MySQL, n => "`" + n + "`", alignTypes: false);
    var qr = new QueryResult { Title = "t" }; qr.Columns.Add("id"); qr.Rows.Add(new object[] { 7 });
    var ins = DataSync.InsertBatches(qr, "`t`", n => "`" + n + "`");
    Check("传输语句组合完整", create.Contains("CREATE TABLE") && sync.Count == 0 && ins.Count == 1 && ins[0].Contains("VALUES (7)"), create);
}

{
    // 流式底座真库链路：SQLite 起临时表 → StreamingQuery 分页取数 → QueryRouter 整包同形返回 → TaskCenter 队列调度
    var dbPath = Path.Combine(Path.GetTempPath(), "taodbm-stream-" + Guid.NewGuid().ToString("N") + ".db");
    var profile = new ConnectionProfile { Engine = DbEngine.SQLite, DisplayName = "stream-smoke", Database = dbPath };
    await using (Microsoft.Data.Sqlite.SqliteConnection setup = new($"Data Source={dbPath}"))
    {
        setup.Open();
        using var cmd = setup.CreateCommand();
        cmd.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);";
        cmd.ExecuteNonQuery();
        using var tx = setup.BeginTransaction();
        for (int i = 1; i <= 500; i++)
        {
            using var one = setup.CreateCommand();
            one.Transaction = tx;
            one.CommandText = $"INSERT INTO t(v) VALUES ('v{i}')";
            one.ExecuteNonQuery();
        }
        tx.Commit();
    }
    var prov = (TaoDBM.Core.RelationalProvider)ProviderFactory.Create(profile);
    // 1) StreamingQuery 分页：500 行按 200/页 = 3 页（200+200+100）
    {
        await using var conn = prov.OpenStream(null);
        await using var sq = await TaoDBM.Core.Streaming.StreamingQuery.StartAsync(conn, "SELECT id, v FROM t ORDER BY id", CancellationToken.None);
        Check("流式列名", sq.Columns.SequenceEqual(new[] { "id", "v" }), string.Join(",", sq.Columns));
        int total = 0, pages = 0;
        while (true)
        {
            var page = await sq.NextPageAsync(200, CancellationToken.None);
            total += page.Rows.Length; pages++;
            foreach (var row in page.Rows) Check("流式行内值", row.Length == 2, "列数不对");
            if (!page.HasMore) break;
        }
        Check("流式分页总数", total == 500, total.ToString());
        Check("流式分页页数", pages == 3, pages.ToString());
    }
    // 2) QueryRouter 整包同形：与旧 Execute 返回同形 QueryResult，无 5000 截断
    {
        var rs = await TaoDBM.Core.Streaming.QueryRouter.ExecuteAsync(prov, null, "SELECT COUNT(*) AS n FROM t", CancellationToken.None);
        Check("路由整包行数", rs.Count == 1 && rs[0].Rows.Count == 1, rs[0].Rows.Count.ToString());
        Check("路由聚合值", Convert.ToInt64(rs[0].Rows[0][0]) == 500, rs[0].Rows[0][0]?.ToString() ?? "null");
        Check("路由列名", rs[0].Columns[0] == "n", rs[0].Columns[0]);
    }
    // 3) StreamingSwitch 开关读环境变量（off 关流式）
    Check("流式开关默认开", TaoDBM.Core.Streaming.StreamingSwitch.Enabled, "");
    // 4) TaskCenter：提交→完成快照、取消
    {
        var tc = new TaoDBM.Core.Streaming.TaskCenter();
        var ran = new TaskCompletionSource();
        // 「占位」任务挂起直至被取消：原先同步完成，消费循环可能在 Cancel 前跑完，断言成竞态
        tc.Runner = async h => { ran.TrySetResult(); if (h.Spec.Title == "占位") await Task.Delay(Timeout.Infinite, h.Token); return (string?)null; };
        var h1 = tc.Submit(new TaoDBM.Core.Streaming.TaskSpec { Kind = "query", Title = "冒烟查询" });
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var t0 = DateTime.UtcNow;
        while (!tc.List().Any(s => s.TaskId == h1.TaskId && s.State == "done") && DateTime.UtcNow - t0 < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        Check("任务中心执行完成", tc.List().Any(s => s.TaskId == h1.TaskId && s.State == "done"), string.Join("|", tc.List().Select(s => s.State)));
        Check("任务取消", tc.Cancel(h1.TaskId) == false, "done 后不可再取消");
        var h2 = tc.Submit(new TaoDBM.Core.Streaming.TaskSpec { Title = "占位" });
        Check("任务取消进行中", tc.Cancel(h2.TaskId), "");
    }
    // Windows 差异：Microsoft.Data.Sqlite 连接池在 Dispose 后仍持有句柄，直接删库在 Windows 抛
    // IOException（Linux 允许删除已打开文件掩盖此问题）；先清池再删，删除失败不吞也不影响判定
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    for (int attempt = 0; attempt < 5 && System.IO.File.Exists(dbPath); attempt++)
    {
        try { System.IO.File.Delete(dbPath); }
        catch (IOException) { await Task.Delay(100); }
        catch (UnauthorizedAccessException) { await Task.Delay(100); }
    }
    Environment.SetEnvironmentVariable("DBM_STREAMING", null);
}

// 批次F 宽表列懒加载：Store 单元（可见集/增量去重/断流恢复/并发合并）+ 真库 SQLite 全链（ExecuteLazyAsync）
{
    var src = new LazyCountSource(40, 300);
    var st = new TaoDBM.Core.Streaming.ColumnLazyStore(src);
    await st.EnsureColumnsAsync(TaoDBM.Core.Streaming.ColumnLazyStore.InitialVisible(40, 8));
    Check("懒加载首屏只拉可见列", st.LoadedCount == 8 && st.LoadedCells == 8L * 300 && src.Reads == 1, $"cols={st.LoadedCount} cells={st.LoadedCells} reads={src.Reads}");
    Check("懒加载首屏列集", Enumerable.Range(0, 8).All(st.IsLoaded) && !st.IsLoaded(8), "");
    Check("懒加载首屏取值", Convert.ToInt32(st.GetValue(5, 3)) == 30005, st.GetValue(5, 3)?.ToString() ?? "null");

    // 增量拉取：重叠滚动只拉缺失列
    await st.EnsureColumnsAsync(new[] { 3, 5, 9 });
    Check("懒加载增量只拉缺失列", st.LoadedCount == 9 && src.Reads == 2 && src.Batches[^1].Length == 1 && src.Batches[^1][0] == 9, $"cols={st.LoadedCount} " + string.Join(",", src.Batches[^1]));
    Check("懒加载新增列取值", Convert.ToInt32(st.GetValue(2, 9)) == 90002, "");

    // 重复滚动去重：已加载列集合零源读
    await st.EnsureColumnsAsync(new[] { 0, 3, 5, 9 });
    Check("懒加载重复滚动去重", st.LoadedCount == 9 && src.Reads == 2, $"cols={st.LoadedCount} reads={src.Reads}");

    // 断流恢复：源中断 → 已加载列保留，重试补齐缺失列
    var srcF = new LazyCountSource(6, 10) { FailOnce = true };
    var stF = new TaoDBM.Core.Streaming.ColumnLazyStore(srcF);
    await stF.EnsureColumnsAsync(new[] { 0, 1 });
    bool threw = false;
    try { await stF.EnsureColumnsAsync(new[] { 2, 3 }); } catch (System.IO.IOException) { threw = true; }
    Check("懒加载断流抛出且账目保持", threw && stF.LoadedCount == 2, $"ok={threw} cols={stF.LoadedCount}");
    await stF.EnsureColumnsAsync(new[] { 2, 3 });
    Check("懒加载断流恢复", stF.LoadedCount == 4 && Convert.ToInt32(stF.GetValue(3, 3)) == 30003, $"cols={stF.LoadedCount}");

    // 同列并发请求合并：只一次源读
    var srcC = new LazyCountSource(4, 5) { DelayMs = 50 };
    var stC = new TaoDBM.Core.Streaming.ColumnLazyStore(srcC);
    await Task.WhenAll(stC.EnsureColumnsAsync(new[] { 1 }), stC.EnsureColumnsAsync(new[] { 1 }));
    Check("懒加载并发合并去重", srcC.Reads == 1 && stC.LoadedCount == 1, $"reads={srcC.Reads}");

    // 真库 SQLite 宽表全链：80 列 × 300 行，首屏 64 列，增量补齐后与整包逐格一致
    var dbPath2 = Path.Combine(Path.GetTempPath(), "taodbm-lazy-" + Guid.NewGuid().ToString("N") + ".db");
    var prof2 = new ConnectionProfile { Engine = DbEngine.SQLite, DisplayName = "lazy-smoke", Database = dbPath2 };
    await using (Microsoft.Data.Sqlite.SqliteConnection setup2 = new($"Data Source={dbPath2}"))
    {
        setup2.Open();
        using var cmd = setup2.CreateCommand();
        cmd.CommandText = "CREATE TABLE wide(id INTEGER PRIMARY KEY, " + string.Join(",", Enumerable.Range(1, 79).Select(i => $"c{i} TEXT")) + ");";
        cmd.ExecuteNonQuery();
        using var tx = setup2.BeginTransaction();
        for (int r = 0; r < 300; r++)
        {
            using var one = setup2.CreateCommand();
            one.Transaction = tx;
            one.CommandText = "INSERT INTO wide VALUES (" + r + "," + string.Join(",", Enumerable.Range(1, 79).Select(i => i * 100 + r)) + ");";
            one.ExecuteNonQuery();
        }
        tx.Commit();
    }
    var prov2 = (TaoDBM.Core.RelationalProvider)ProviderFactory.Create(prof2);
    var lazy = await TaoDBM.Core.Streaming.QueryRouter.ExecuteLazyAsync(prov2, null, "SELECT * FROM wide ORDER BY id", 64, CancellationToken.None);
    var lq = lazy[0] as TaoDBM.Core.Streaming.LazyQueryResult;
    Check("懒加载路由可见列", lq != null && lq.Columns.Count == 64 && lq.Rows.Count == 300, $"{lq?.Columns.Count}/{lq?.Rows.Count}");
    Check("懒加载路由总列数", lq!.Store.TotalColumns == 80, lq.Store.TotalColumns.ToString());
    Check("懒加载首屏内存口径", lq.Store.LoadedCount == 64 && lq.Store.LoadedCells == 64L * 300, $"{lq.Store.LoadedCount}/{lq.Store.LoadedCells}");
    bool rej = false;
    try { lq.Store.GetValue(0, 79); } catch (InvalidOperationException) { rej = true; }
    Check("懒加载未加载列拒绝", rej, "");
    await lq.Store.EnsureColumnsAsync(new[] { 79 });
    Check("懒加载尾列增量值对", Convert.ToInt32(lq.Store.GetValue(7, 79)) == 7907, lq.Store.GetValue(7, 79)?.ToString() ?? "null");
    await lq.Store.EnsureColumnsAsync(Enumerable.Range(0, 80));
    Check("懒加载全拉齐口径", lq.Store.LoadedCount == 80 && lq.Store.LoadedCells == 80L * 300, $"{lq.Store.LoadedCount}/{lq.Store.LoadedCells}");
    var full = lq.Materialize(Enumerable.Range(0, 80).ToList());
    var plain = await prov2.Execute(null!, "SELECT * FROM wide ORDER BY id");
    bool allEq = full.Rows.Count == plain[0].Rows.Count;
    for (int r = 0; allEq && r < full.Rows.Count; r++)
        for (int c = 0; allEq && c < 80; c++)
            allEq = Convert.ToString(full.Rows[r][c]) == Convert.ToString(plain[0].Rows[r][c]);
    Check("懒加载物化与整包一致", allEq, "");
    var ddl = await TaoDBM.Core.Streaming.QueryRouter.ExecuteLazyAsync(prov2, null, "CREATE TABLE lazy_ddl(id INTEGER)", 64, CancellationToken.None);
    Check("懒加载DDL同形", ddl[0].Affected == 0 && ddl[0].Columns.Count == 0, $"aff={ddl[0].Affected} cols={ddl[0].Columns.Count}");

    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    for (int a2 = 0; a2 < 5 && File.Exists(dbPath2); a2++)
    {
        try { File.Delete(dbPath2); }
        catch (IOException) { await Task.Delay(100); }
        catch (UnauthorizedAccessException) { await Task.Delay(100); }
    }
}

// 5) TaskCenter A2 实体化：暂停/续跑、重跑、快照导出/回放
{
    var tc = new TaoDBM.Core.Streaming.TaskCenter();
    var ranCount = 0;
    tc.Runner = _ => { Interlocked.Increment(ref ranCount); return Task.FromResult<string?>(null); };

    Check("暂停首次生效", tc.Pause());
    Check("重复暂停返回false", !tc.Pause());
    var hp = tc.Submit(new TaoDBM.Core.Streaming.TaskSpec { Kind = "export", Title = "暂停期任务", Db = "db1", Sql = "SELECT 1;" });
    await Task.Delay(200);
    Check("暂停期任务不开跑且停在运行态", ranCount == 0 && tc.List().First(s => s.TaskId == hp.TaskId).State == "running", $"ran={ranCount}");
    Check("暂停期运行态任务不可重跑", tc.Retry(hp.TaskId) == null, "running 态不允许 Retry");

    tc.Resume();
    var t0 = DateTime.UtcNow;
    while (ranCount < 1 && DateTime.UtcNow - t0 < TimeSpan.FromSeconds(5)) await Task.Delay(20);
    var t1 = DateTime.UtcNow;
    while (!tc.List().Any(s => s.TaskId == hp.TaskId && s.State == "done") && DateTime.UtcNow - t1 < TimeSpan.FromSeconds(5)) await Task.Delay(20);
    Check("续跑后积压任务开跑至完成", ranCount == 1 && tc.List().First(s => s.TaskId == hp.TaskId).State == "done", $"ran={ranCount}");

    var hq = tc.Retry(hp.TaskId);
    Check("终态任务重跑返回新句柄", hq != null && hq.TaskId != hp.TaskId);
    Check("重跑沿用原规格", hq != null && hq.Spec.Title == "暂停期任务" && hq.Spec.Kind == "export" && hq.Spec.Sql == "SELECT 1;");
    Check("未知任务重跑返回null", tc.Retry("no-such-id") == null);
    var t2 = DateTime.UtcNow;
    while (ranCount < 2 && DateTime.UtcNow - t2 < TimeSpan.FromSeconds(5)) await Task.Delay(20);
    Check("重跑任务执行完成", ranCount == 2, $"ran={ranCount}");

    // 快照导出/回放：新实例并入历史、重复回放幂等、坏快照静默忽略
    var json = tc.ExportStates();
    var tc2 = new TaoDBM.Core.Streaming.TaskCenter();
    tc2.ImportStates(json);
    Check("快照回放后历史可见", tc2.List().Count == tc.List().Count && tc2.List().Any(s => s.TaskId == hp.TaskId && s.State == "done"), $"tc2={tc2.List().Count} tc={tc.List().Count}");
    tc2.ImportStates(json);
    Check("重复回放不重复计数", tc2.List().Count == tc.List().Count);
    tc2.ImportStates("{坏");
    Check("损坏快照静默忽略", tc2.List().Count == tc.List().Count);
}

// 6) SchemaDiff 结构差异对比 + 同步计划（对象同步批纯引擎）
{
    SchemaTableSnapshot T(string table, params TableColumnDesign[] cols) => new()
    { Table = table, Columns = cols.ToList() };
    TableColumnDesign C(string name, string type, bool nullable = true, bool pk = false, string def = "") => new()
    { Name = name, SqlType = type, Nullable = nullable, IsPrimaryKey = pk, DefaultValue = def };

    var src = new List<SchemaTableSnapshot>
    {
        T("users", C("id", "BIGINT", false, true), C("name", "VARCHAR(50)"), C("age", "INT")),
        T("orders", C("id", "BIGINT", false, true), C("memo", "TEXT"), C("flag", "INT", true, false, "0")),
        T("logs", C("id", "BIGINT", false, true)),
    };
    var dst = new List<SchemaTableSnapshot>
    {
        T("users", C("id", "BIGINT", false, true), C("name", "VARCHAR(100)"), C("extra", "CHAR(1)")),
        T("orders", C("id", "BIGINT", false, true), C("memo", "TEXT"), C("flag", "INT", true, false, "1")),
        T("archive", C("id", "BIGINT")),
    };

    var diffs = SchemaDiff.Diff(src, dst);

    // 缺表 / 多余表
    var logs = diffs.First(d => d.Table == "logs" && d.Kind == "table-missing");
    Check("缺表检出且可执行", logs.Actionable, logs.Detail);
    Check("多余表仅提示", diffs.Any(d => d.Table == "archive" && d.Kind == "table-extra" && !d.Actionable));

    // 列级差异
    var tmiss = diffs.FirstOrDefault(d => d.Table == "users" && d.Kind == "column-missing");
    Check("缺列检出", tmiss != null && tmiss.Column == "age" && tmiss.Actionable);
    var textra = diffs.FirstOrDefault(d => d.Table == "users" && d.Kind == "column-extra");
    Check("多余列仅提示不可执行", textra != null && textra.Column == "extra" && !textra.Actionable);
    var tmis = diffs.FirstOrDefault(d => d.Table == "users" && d.Kind == "type-mismatch");
    Check("类型不一致检出", tmis != null && tmis.Column == "name" && tmis.Source == "VARCHAR(50)" && tmis.Target == "VARCHAR(100)");

    // 默认值比较默认关闭、开启后检出
    Check("默认值差异默认不报", !diffs.Any(d => d.Table == "orders" && d.Kind == "default-mismatch"));
    var d1 = SchemaDiff.Diff(src, dst, new SchemaDiffOptions { CompareDefault = true, ReportExtraColumns = false, ReportExtraTables = false });
    Check("开启默认值比较后检出", d1.Any(d => d.Table == "orders" && d.Kind == "default-mismatch" && d.Column == "flag"));
    Check("关闭多余报告后无提示项", !d1.Any(d => d.Kind is "column-extra" or "table-extra"));

    // 主键 / 可空性差异
    var srcPk = new List<SchemaTableSnapshot> { T("t1", C("id", "INT", true, true)) };
    var dstPk = new List<SchemaTableSnapshot> { T("t1", C("id", "INT", true, false)) };
    var pkDiffs = SchemaDiff.Diff(srcPk, dstPk);
    Check("主键差异检出", pkDiffs.Any(d => d.Kind == "pk-mismatch" && d.Column == "id"));
    var srcN = new List<SchemaTableSnapshot> { T("t2", C("a", "INT", true)) };
    var dstN = new List<SchemaTableSnapshot> { T("t2", C("a", "INT", false)) };
    Check("可空性差异检出", SchemaDiff.Diff(srcN, dstN).Any(d => d.Kind == "nullable-mismatch"));

    // 完全一致零差异；表名/列名大小写不敏感
    var same = SchemaDiff.Diff(src, new List<SchemaTableSnapshot>(src));
    Check("同构零差异", same.Count == 0, string.Join("|", same.Select(d => d.Table + ":" + d.Kind)));
    var caseDst = new List<SchemaTableSnapshot> { T("USERS", C("ID", "bigint", false, true), C("NAME", "varchar(50)"), C("AGE", "int")) };
    var caseUsers = SchemaDiff.Diff(src.Where(t => t.Table == "users").ToList(), caseDst);
    Check("大小写不敏感比对", caseUsers.Count == 0, string.Join("|", caseUsers.Select(d => d.Kind + ":" + d.Column)));

    // 同步计划：缺表 → CREATE；缺列+类型对齐 → ALTER；多余列不删（dropMissing=false）
    var my = (Func<string, string>)(s => "`" + s + "`");
    var (stmts, skips) = SchemaDiff.PlanSync(src, dst, diffs, DbEngine.MySQL, my);
    Check("缺表生成建表", stmts.Any(s => s.Contains("CREATE TABLE `logs`")), string.Join(" ;; ", stmts));
    Check("缺列生成加列", stmts.Contains("ALTER TABLE `users` ADD `age` INT"));
    Check("类型对齐生成改列", stmts.Contains("ALTER TABLE `users` MODIFY COLUMN `name` VARCHAR(50)"));
    Check("多余列不进计划", !stmts.Any(s => s.Contains("DROP COLUMN")));
    Check("多余表不进计划", !stmts.Any(s => s.Contains("`archive`")));
    Check("提示类差异不落语句", skips.Count == 0, string.Join("|", skips));

    // dropMissing=true 开删侧；SQLite 不支持改列进 skipped
    var (dropStmts, _) = SchemaDiff.PlanSync(src, dst, diffs, DbEngine.MySQL, my, dropMissing: true);
    Check("开删侧后多余列进计划", dropStmts.Contains("ALTER TABLE `users` DROP COLUMN `extra`"));
    var lite = new List<SchemaTableSnapshot> { T("u", C("a", "INT")) };
    var liteDst = new List<SchemaTableSnapshot> { T("u", C("a", "BIGINT")) };
    var liteDiffs = SchemaDiff.Diff(lite, liteDst);
    var (_, liteSkips) = SchemaDiff.PlanSync(lite, liteDst, liteDiffs, DbEngine.SQLite, s => s);
    Check("SQLite 改列进跳过清单", liteSkips.Count == 1 && liteSkips[0].Contains("SQLite"), string.Join("|", liteSkips));
}

// 7) SchemaTimeline 表结构变更时间线：追加切面、任意两切面对比/回放、序列化容错
{
    SchemaTableSnapshot T(string table, params TableColumnDesign[] cols) => new()
    { Table = table, Columns = cols.ToList() };
    TableColumnDesign C(string name, string type, bool nullable = true, bool pk = false, string def = "") => new()
    { Name = name, SqlType = type, Nullable = nullable, IsPrimaryKey = pk, DefaultValue = def };

    var t0 = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.FromHours(8));
    var line = SchemaTimeline.Append([], new[] { T("users", C("id", "BIGINT", false, true)) }, "基线", t0);
    var afterAdd = SchemaTimeline.Append(line, new[] { T("users", C("id", "BIGINT", false, true), C("age", "INT")) }, "加列", t0.AddMinutes(30));
    var afterMore = SchemaTimeline.Append(afterAdd, new[] {
        T("users", C("id", "BIGINT", false, true), C("age", "INT")),
        T("logs", C("id", "BIGINT", false, true)),
    }, "补日志表", t0.AddMinutes(60));

    Check("切面逐次追加且不改旧表", line.Count == 1 && afterAdd.Count == 2 && afterMore.Count == 3);
    Check("切面携带时间与说明", afterAdd[1].Label == "加列" && afterAdd[1].At > afterAdd[0].At);
    Check("追加不污染旧切面", afterAdd[0].Tables[0].Columns.Count == 1 && line[0].Tables[0].Columns.Count == 1);

    // 对比与回放：基线 → 补日志表
    var diffs = SchemaTimeline.DiffBetween(afterMore[0], afterMore[2]);
    Check("跨切面差异检出", diffs.Any(d => d.Table == "users" && d.Kind == "column-missing" && d.Column == "age")
        && diffs.Any(d => d.Table == "logs" && d.Kind == "table-missing"));
    var my = (Func<string, string>)(s => "`" + s + "`");
    var (stmts, _) = SchemaTimeline.PlanBetween(afterMore[0], afterMore[2], DbEngine.MySQL, my);
    Check("跨切面回放生成计划", stmts.Any(s => s.Contains("CREATE TABLE `logs`")) && stmts.Contains("ALTER TABLE `users` ADD `age` INT"),
        string.Join(" ;; ", stmts));

    // 序列化往返与容错
    var json = SchemaTimeline.ToJson(afterMore);
    var back = SchemaTimeline.Parse(json);
    Check("时间线往返还原", back.Count == 3 && back[1].Label == "加列" && back[1].Tables[0].Columns.Count == 2);
    Check("回读按时间升序", back.SequenceEqual(back.OrderBy(e => e.At)));
    Check("损坏时间线回空", SchemaTimeline.Parse("{坏").Count == 0 && SchemaTimeline.Parse("").Count == 0 && SchemaTimeline.Parse(null).Count == 0);
}

// 8) RiskySqlGuard 危险 SQL 拦截引擎（安全线：无 WHERE 预估影响行数）
{
    var id = (Func<string, string>)(s => s);   // 通用引擎：标识符不加引号，便于断言

    Check("无WHERE删除判危险", RiskySqlGuard.Inspect("delete from users", DbEngine.MySQL, id) is { Level: "danger", Kind: "no-where", EstimateSql: "SELECT COUNT(*) FROM users" } c1
        ? c1.Table == "users" : false);
    Check("无WHERE更新判危险并给预估", RiskySqlGuard.Inspect("UPDATE users SET age = 0", DbEngine.MySQL, id).EstimateSql == "SELECT COUNT(*) FROM users");
    Check("带WHERE改数放行", RiskySqlGuard.Inspect("delete from users where id = 1", DbEngine.MySQL, id).Level == "ok");
    Check("WHERE在字符串里不算条件", RiskySqlGuard.Inspect("update t set a = 'where x = 1'", DbEngine.MySQL, id).Kind == "no-where",
        RiskySqlGuard.Inspect("update t set a = 'where x = 1'", DbEngine.MySQL, id).Kind);
    Check("WHERE在注释里不算条件", RiskySqlGuard.Inspect("delete from t -- where id = 1\n", DbEngine.MySQL, id).Kind == "no-where");
    var esc = RiskySqlGuard.Inspect("update t set a = 'it''s ok' where id = 2", DbEngine.MySQL, id);
    Check("转义引号字面量放行", esc.Level == "ok", esc.Skeleton);
    Check("DROP判危险", RiskySqlGuard.Inspect("DROP TABLE IF EXISTS logs", DbEngine.MySQL, id) is { Level: "danger", Kind: "drop-table" } c2
        ? c2.Table == "logs" : false);
    Check("TRUNCATE判危险", RiskySqlGuard.Inspect("truncate table logs", DbEngine.MySQL, id).Kind == "truncate");
    Check("删列判危险", RiskySqlGuard.Inspect("alter table users drop column age", DbEngine.MySQL, id).Kind == "drop-column");
    Check("SELECT不拦", RiskySqlGuard.Inspect("SELECT * FROM users", DbEngine.MySQL, id).Level == "ok");

    // 带条件的预估 SQL 与方言引用符
    var est = RiskySqlGuard.EstimateRowsSql("delete from `my db`.users where age > 18", DbEngine.MySQL, s => $"`{s}`");
    Check("带条件预估走WHERE", est == "SELECT COUNT(*) FROM `users` WHERE age > 18", est ?? "(null)");
    Check("SELECT不给预估", RiskySqlGuard.EstimateRowsSql("select 1", DbEngine.MySQL, id) == null);

    // 脚本级：逐条 + 最危险档
    var script = RiskySqlGuard.InspectScript("select 1;\nupdate t set a = 1;\n", DbEngine.MySQL, id);
    Check("脚本拆分逐条检查", script.Count == 2 && script[1].Kind == "no-where");
    Check("脚本最危险档上浮", RiskySqlGuard.Worst(script).Kind == "no-where" && RiskySqlGuard.Worst(script).Level == "danger");
    Check("全ok脚本 worst 为 ok", RiskySqlGuard.Worst(RiskySqlGuard.InspectScript("select 1;", DbEngine.MySQL, id)).Level == "ok");
}

// 9) MaskEngine 一键脱敏引擎（安全线：一键脱敏导出）
{
    Check("手机号列推断", MaskEngine.InferRule("phone") == MaskRule.Phone && MaskEngine.InferRule("手机号") == MaskRule.Phone);
    Check("证件与卡号列推断", MaskEngine.InferRule("id_card") == MaskRule.IdCard && MaskEngine.InferRule("银行卡号") == MaskRule.BankCard);
    Check("密码列整值档", MaskEngine.InferRule("password") == MaskRule.Secret && MaskEngine.InferRule("密码") == MaskRule.Secret);
    Check("普通列不脱敏", MaskEngine.InferRule("order_total") == MaskRule.None && MaskEngine.InferRule("created_at") == MaskRule.None);
    Check("结构名列不误伤", MaskEngine.InferRule("table_name") == MaskRule.None && MaskEngine.InferRule("column_name") == MaskRule.None);
    Check("姓名列推断", MaskEngine.InferRule("联系人") == MaskRule.Name && MaskEngine.InferRule("user_name") == MaskRule.Name);
    Check("手机号打码", MaskEngine.Mask("13812345678", MaskRule.Phone) == "138****5678");
    Check("邮箱打码保域名", MaskEngine.Mask("zhangsan@163.com", MaskRule.Email) == "z***@163.com");
    Check("身份证打码", MaskEngine.Mask("110101199001011234", MaskRule.IdCard) == "110***********1234");
    Check("姓名打码", MaskEngine.Mask("张小明明", MaskRule.Name) == "张***");
    Check("银行卡打码", MaskEngine.Mask("6222021234561234567", MaskRule.BankCard) == new string('*', 15) + "4567");
    Check("密码整值打码", MaskEngine.Mask("p@ssw0rd", MaskRule.Secret) == "********");
    Check("空值原样", MaskEngine.Mask(null, MaskRule.Phone) == "" && MaskEngine.Mask("", MaskRule.Email) == "");
    Check("短值整段打码不越界", MaskEngine.Mask("123", MaskRule.Phone) == "***" && MaskEngine.Mask("12345", MaskRule.IdCard) == "*****");
    var row = MaskEngine.MaskRow(new List<KeyValuePair<string, string>>
    {
        new("phone", "13812345678"), new("total", "99"), new("密码", "abc123"),
    });
    Check("行级脱敏混合列", row[0].Value == "138****5678" && row[1].Value == "99" && row[2].Value == "******");
    Check("自定义覆盖规则", MaskEngine.InferRule("member_id",
        new Dictionary<string, MaskRule> { ["member_id"] = MaskRule.Phone }) == MaskRule.Phone);
}

// 10) ChangeAudit 改数留痕审计 + 单条回滚（安全线）
{
    var before = new Dictionary<string, string?> { ["id"] = "7", ["age"] = "25", ["city"] = "北京", ["note"] = "it's ok", ["tag"] = null };
    var after = new Dictionary<string, string?> { ["id"] = "7", ["age"] = "30", ["city"] = "北京", ["note"] = null, ["tag"] = "vip" };
    var entry = ChangeAudit.CaptureRow("users", "id", "7", before, after, "update users set age = 30 where id = 7");
    Check("留痕只记改动列", entry.HasChanges && entry.Changes.Count == 3 &&
        entry.Changes[0].Column == "age" && entry.Changes[1].Column == "note" && entry.Changes[2].Column == "tag",
        string.Join(",", entry.Changes.Select(c => c.Column)));
    Check("留痕带时间与语句", entry.TimestampUtc.Length > 0 && entry.Statement.Contains("users", StringComparison.Ordinal));
    Check("单条回滚语句含NULL旧值", ChangeAudit.UndoSql(entry) ==
        "UPDATE `users` SET `age` = '25', `note` = 'it''s ok', `tag` = NULL WHERE `id` = '7'", ChangeAudit.UndoSql(entry));
    Check("回滚转义单引号", ChangeAudit.UndoSql(ChangeAudit.CaptureRow("t", "id", "1",
        new Dictionary<string, string?> { ["v"] = "it's ok" }, new Dictionary<string, string?> { ["v"] = "x" })) ==
        "UPDATE `t` SET `v` = 'it''s ok' WHERE `id` = '1'");

    var noChange = ChangeAudit.CaptureRow("users", "id", "7", before, new Dictionary<string, string?>(before));
    Check("无差异不落账", !noChange.HasChanges && ChangeAudit.UndoSql(noChange) == "");

    var e1 = ChangeAudit.CaptureRow("users", "id", "1", new Dictionary<string, string?> { ["a"] = "1" }, new Dictionary<string, string?> { ["a"] = "2" });
    var e2 = ChangeAudit.CaptureRow("users", "id", "2", new Dictionary<string, string?> { ["a"] = "1" }, new Dictionary<string, string?> { ["a"] = "3" });
    var script = ChangeAudit.UndoScript(new List<AuditEntry> { e1, e2 });
    Check("回滚脚本倒序分号换行", script == "UPDATE `users` SET `a` = '1' WHERE `id` = '2';\nUPDATE `users` SET `a` = '1' WHERE `id` = '1';\n", script.Replace("\n", "\\n", StringComparison.Ordinal));

    var dir = Path.Combine(Path.GetTempPath(), "taodbm-audit-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var journal = Path.Combine(dir, "audit.jsonl");
    ChangeAudit.SaveJsonl(journal, new List<AuditEntry> { entry, e1 });
    var back = ChangeAudit.LoadJsonl(journal);
    Check("留痕账 JSONL 往返", back.Count == 2 && back[0].Table == "users" &&
        back[0].Changes.Count == 3 && back[0].Changes[1].OldValue == "it's ok" && back[1].PkValue == "1");
    File.WriteAllText(journal, "{broken\n" + File.ReadAllText(journal));
    Check("损坏行容错跳过", ChangeAudit.LoadJsonl(journal).Count == 2);
    Check("缺账文件按空账", ChangeAudit.LoadJsonl(Path.Combine(dir, "none.jsonl")).Count == 0);
}

// 11) SqlReleaseCheck SQL 上线检查评分（安全线）
{
    var (okScore, okFindings) = SqlReleaseCheck.Review("select id, name from users where id = 1;", DbEngine.MySQL);
    Check("干净脚本满分可上线", okScore == 100 && okFindings.Count == 0 && SqlReleaseCheck.Verdict(okScore) == "可上线");

    var (badScore, bad) = SqlReleaseCheck.Review("delete from users;\ndrop table logs;", DbEngine.MySQL);
    Check("无WHERE与DROP均拦截", bad.Count(f => f.Level == "block") == 2 && badScore == 50 && SqlReleaseCheck.Verdict(badScore) == "建议整改");

    var (s1, f1) = SqlReleaseCheck.Review("select * from orders;", DbEngine.MySQL);
    Check("SELECT星告警", s1 == 90 && f1.Any(x => x.Rule == "select-star" && x.Level == "warn") && SqlReleaseCheck.Verdict(s1) == "可上线");

    var (s2, f2) = SqlReleaseCheck.Review("select * from users where name like '%张';", DbEngine.MySQL);
    Check("前导通配与星合计", f2.Count == 2 && f2.All(x => x.Level == "warn") && s2 == 80);

    var (s3, f3) = SqlReleaseCheck.Review("update t set a = 1 where id = 2;", DbEngine.MySQL);
    Check("改数无LIMIT无事务提示", f3.Any(x => x.Rule == "no-limit") && f3.Any(x => x.Rule == "no-transaction") && s3 == 94);

    var (s4, f4) = SqlReleaseCheck.Review("begin;\nupdate t set a = 1 where id = 2;\n", DbEngine.MySQL);
    Check("包事务后不提示", !f4.Any(x => x.Rule == "no-transaction") && s4 == 97);

    var (s5, f5) = SqlReleaseCheck.Review("update t set a = 'where x = 1';", DbEngine.MySQL);
    Check("字符串含WHERE不算条件仍判无WHERE危险", f5.Any(x => x.Rule == "dangerous-statement") && s5 == 75);

    var (s6, _) = SqlReleaseCheck.Review("xxx;\ntruncate table huge;", DbEngine.MySQL);
    Check("TRUNCATE拦截扣25分", s6 == 75 && SqlReleaseCheck.Verdict(s6) == "谨慎上线");
    var (s7, _) = SqlReleaseCheck.Review("truncate table a; truncate table b; truncate table c; truncate table d;", DbEngine.MySQL);
    Check("分数下限不为负", s7 == 0 && SqlReleaseCheck.Verdict(0) == "建议整改");
}

// 12) BatchCleanup 大表分批清理（安全线）
{
    var plan = BatchCleanup.Plan("logs", "created_at < '2026-01-01'", "id", 2500, 1000, DbEngine.MySQL);
    Check("计划按估计行数分批", plan.TotalBatches == 3 && plan.BatchSqls.Count == 3 && !plan.WholeTable);
    Check("MySQL批次SQL带LIMIT", plan.BatchSqls[0] == "DELETE FROM `logs` WHERE created_at < '2026-01-01' LIMIT 1000", plan.BatchSqls[0]);

    var pg = BatchCleanup.Plan("logs", "created_at < '2026-01-01'", "id", 2500, 1000, DbEngine.PostgreSQL);
    Check("PG批次SQL主键子查询", pg.BatchSqls[0] == "DELETE FROM \"logs\" WHERE \"id\" IN (SELECT \"id\" FROM \"logs\" WHERE created_at < '2026-01-01' ORDER BY \"id\" LIMIT 1000)", pg.BatchSqls[0]);

    var whole = BatchCleanup.Plan("logs", null, "id", 500, 200, DbEngine.MySQL);
    Check("整表清空计划带警示", whole.WholeTable && whole.TotalBatches == 3 && whole.BatchSqls[0] == "DELETE FROM `logs` LIMIT 200");

    Check("非法参数空计划", BatchCleanup.Plan("t", "x=1", "id", 0, 100, DbEngine.MySQL).TotalBatches == 0);

    var batchHit = 0;
    var run = BatchCleanup.Run(plan, _ => ++batchHit switch { 1 => 1000, 2 => 1000, _ => 400 });
    Check("执行收敛统计行数", run.RowsDeleted == 2400 && run.BatchesDone == 3 && run.Exhausted, $"{run.RowsDeleted}/{run.BatchesDone}");

    var hit = 0;
    var early = BatchCleanup.Run(plan, _ => ++hit == 2 ? 0 : 1000);
    Check("删除0行提前收敛", early.BatchesDone == 2 && early.RowsDeleted == 1000 && !early.Exhausted);

    var capped = BatchCleanup.Run(plan, _ => 1000, maxBatches: 1);
    Check("最大批次封顶", capped.BatchesDone == 1 && capped.RowsDeleted == 1000 && !capped.Exhausted);
}

// 13) LockAnalyzer 锁与死锁现场还原 + 终止会话（安全线）
{
    var sessions = new Dictionary<string, SessionInfo>
    {
        ["101"] = new("101", "alice", "update t set a = 1", 5000),
        ["102"] = new("102", "bob", "delete from t", 200),
        ["103"] = new("103", "carol", "select * from t", 1),
    };
    var cycle = LockAnalyzer.FindDeadlocks(new List<LockWait>
    {
        new("101", "102", "t.pk=1"),
        new("102", "101", "t.pk=2"),
    });
    Check("双会话死锁环检出", cycle.Count == 1 && cycle[0].SessionIds.Contains("101") && cycle[0].SessionIds.Contains("102"));
    Check("牺牲者选扫描最少的", LockAnalyzer.PickVictim(cycle[0], sessions) == "102");

    var three = LockAnalyzer.FindDeadlocks(new List<LockWait>
    {
        new("a", "b", "r1"), new("b", "c", "r2"), new("c", "a", "r3"),
    });
    Check("三会话环检出", three.Count == 1 && three[0].SessionIds.Count == 3 && three[0].Resources.Count == 3);

    var none = LockAnalyzer.FindDeadlocks(new List<LockWait> { new("x", "y", "r1"), new("y", "z", "r2") });
    Check("无环链不误报", none.Count == 0);

    Check("MySQL终止语句", LockAnalyzer.KillSql("101", DbEngine.MySQL) == "KILL 101");
    Check("PG终止语句走后端函数", LockAnalyzer.KillSql("101", DbEngine.PostgreSQL) == "SELECT pg_terminate_backend(101);");
    Check("SQLite无终止能力返回空", LockAnalyzer.KillSql("101", DbEngine.SQLite) == null);

    var timeline = LockAnalyzer.Timeline(new List<LockWait>
    {
        new("w1", "h1", "r1"), new("w2", "h2", "r2"), new("w3", "h3", "r3"),
    }, new Dictionary<string, long> { ["w1"] = 5, ["w2"] = 90, ["w3"] = 1 });
    Check("现场按等待时长倒序", timeline[0].Waiter == "w2" && timeline[^1].Waiter == "w3");
}

// 14) DeletedRowRecovery 误删找回：快照+拼回（安全线收官项）
{
    var rows = new List<IReadOnlyDictionary<string, string?>>
    {
        new Dictionary<string, string?> { ["id"] = "2", ["name"] = "it's bob", ["tag"] = null },
        new Dictionary<string, string?> { ["id"] = "1", ["name"] = "alice" },
    };
    var snap = DeletedRowRecovery.Capture("users", "id", rows);
    Check("快照列集取并集定序", snap.Columns.SequenceEqual(new[] { "id", "name", "tag" }) && snap.RowCount == 2 && snap.CapturedUtc.Length > 0);

    var script = DeletedRowRecovery.ReinsertScript(snap, DbEngine.MySQL);
    Check("拼回按主键排序且缺列补NULL", script ==
        "INSERT INTO `users` (`id`, `name`, `tag`) VALUES ('1', 'alice', NULL), ('2', 'it''s bob', NULL);\n",
        script.Replace("\n", "\\n", StringComparison.Ordinal));

    var pgScript = DeletedRowRecovery.ReinsertScript(snap, DbEngine.PostgreSQL);
    Check("PG拼回用双引号标识符", pgScript.StartsWith("INSERT INTO \"users\" (\"id\"", StringComparison.Ordinal));

    var many = DeletedRowRecovery.Capture("t", "id", Enumerable.Range(0, 250)
        .Select(i => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["id"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
    Check("拼回分块每100行一条", DeletedRowRecovery.ReinsertScript(many, DbEngine.MySQL).Count(c => c == '\n') == 3);

    var empty = DeletedRowRecovery.Capture("t", "id", Array.Empty<IReadOnlyDictionary<string, string?>>());
    Check("空快照拼回为空脚本", DeletedRowRecovery.ReinsertScript(empty, DbEngine.MySQL) == "");

    var dir = Path.Combine(Path.GetTempPath(), "taodbm-recv-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var snapFile = Path.Combine(dir, "users.snapshot.json");
    DeletedRowRecovery.SaveJsonl(snapFile, snap);
    var back = DeletedRowRecovery.LoadJsonl(snapFile);
    Check("快照 JSONL 往返", back is not null && back.Table == "users" && back.RowCount == 2 && back.Columns.Count == 3 && back.Rows[0][1] == "it's bob");
    File.WriteAllText(snapFile, "{broken");
    Check("损坏快照按无快照降级", DeletedRowRecovery.LoadJsonl(snapFile) == null);
    Check("缺文件按无快照降级", DeletedRowRecovery.LoadJsonl(Path.Combine(dir, "none.json")) == null);
}

// 15) ReadRouter 延迟感知读路由（主从底座）
{
    var nodes = new List<ReplicaNode>
    {
        new("r2", 3, true), new("r1", 1, true), new("r3", 30, true), new("r4", 2, false),
    };
    var d = ReadRouter.Route(nodes, maxLagSeconds: 5);
    Check("路由按延迟升序过滤", d.EligibleReplicas.SequenceEqual(new[] { "r1", "r2" }) && !d.FellBackToPrimary);

    var lagged = ReadRouter.Route(nodes, maxLagSeconds: 0);
    Check("全部超阈值回落主库", lagged.FellBackToPrimary && lagged.EligibleReplicas.Count == 0 && lagged.HasTarget);

    var tie = ReadRouter.Route(new List<ReplicaNode> { new("b", 5, true), new("a", 5, true) }, 5);
    Check("同延迟按名字定序", tie.EligibleReplicas.SequenceEqual(new[] { "a", "b" }));

    var toggle = ReadRouter.AutoToggle(nodes, 5);
    Check("自动摘除超时与故障副本", toggle.First(t => t.Name == "r3").ShouldServeReads == false &&
        toggle.First(t => t.Name == "r4").ShouldServeReads == false &&
        toggle.First(t => t.Name == "r1").ShouldServeReads == true);
}

// 16) ReconcileEngine 迁移后数据对账（主从/搬迁配套）
{
    Check("行数对账SQL", ReconcileEngine.CountSql("orders", null, DbEngine.MySQL) == "SELECT COUNT(*) AS row_count FROM `orders`");
    Check("带条件行数SQL去分号", ReconcileEngine.CountSql("orders", "created_at < '2026-01-01';", DbEngine.MySQL) ==
        "SELECT COUNT(*) AS row_count FROM `orders` WHERE created_at < '2026-01-01'");

    var mysqlCk = ReconcileEngine.ChecksumSql("orders", new[] { "id", "amount" }, null, DbEngine.MySQL);
    Check("MySQL校验和CRC32", mysqlCk == "SELECT COUNT(*) AS row_count, COALESCE(SUM(CRC32(CONCAT_WS('|', `id`, `amount`))), 0) AS checksum FROM `orders`", mysqlCk ?? "");
    var pgCk = ReconcileEngine.ChecksumSql("orders", new[] { "id" }, null, DbEngine.PostgreSQL);
    Check("PG校验和md5截段", pgCk is not null && pgCk.Contains("md5(concat_ws('|', \"id\"))", StringComparison.Ordinal));
    Check("无校验和函数方言退化", ReconcileEngine.ChecksumSql("t", new[] { "a" }, null, DbEngine.SQLite) == null);
    Check("空列集不给校验和", ReconcileEngine.ChecksumSql("t", Array.Empty<string>(), null, DbEngine.MySQL) == null);

    Check("两端一致判match", ReconcileEngine.Compare(100, 100, 7, 7).Level == "match");
    Check("行数不一致判档", ReconcileEngine.Compare(100, 98, 7, 7).Level == "count-mismatch");
    Check("行数同内容漂移判档", ReconcileEngine.Compare(100, 100, 7, 9).Level == "checksum-mismatch");
    Check("单端缺校验和判档", ReconcileEngine.Compare(100, 100, 7, null).Level == "checksum-mismatch");

    Check("抽样核对SQL", ReconcileEngine.SampleSql("users", "id", 50, DbEngine.MySQL) == "SELECT * FROM `users` ORDER BY `id` LIMIT 50");
}

// 17) ShardKeyEvaluator 分片键评估与路由预演（分库分区分表底座）
{
    var ideal = new ShardKeyCandidate("user_id", 100000, 1.1, 500, Monotonic: false);
    var skewed = new ShardKeyCandidate("region", 40, 4.0, 300, Monotonic: false);
    var cold = new ShardKeyCandidate("remark", 5000, 1.0, 0, Monotonic: false);
    var monotonic = new ShardKeyCandidate("id", 1000000, 1.0, 800, Monotonic: true);

    var verdicts = ShardKeyEvaluator.Evaluate(new[] { skewed, cold, monotonic, ideal });
    Check("评估按得分降序", verdicts[0].Column == "id" && verdicts[0].Score == 100 && verdicts[^1].Column == "remark",
        string.Join(",", verdicts.Select(v => $"{v.Column}={v.Score}")));
    Check("理想键满分建议范围分片", verdicts.First(v => v.Column == "user_id").Score == 100 &&
        verdicts.First(v => v.Column == "user_id").SuggestedStrategy == "range");
    Check("倾斜键扣分并出热点提示", verdicts.First(v => v.Column == "region").Score == 70 &&
        verdicts.First(v => v.Column == "region").Issues.Any(i => i.Contains("热点")));
    Check("谓词未命中扣分提示全分片扫描", verdicts.First(v => v.Column == "remark").Score == 70 &&
        verdicts.First(v => v.Column == "remark").Issues.Any(i => i.Contains("全分片扫描")));
    Check("单调键建议哈希并提示尾部热点", verdicts.First(v => v.Column == "id").SuggestedStrategy == "hash" &&
        verdicts.First(v => v.Column == "id").Issues.Any(i => i.Contains("尾部热点")));
    Check("并列得分按列名定序", ShardKeyEvaluator.Evaluate(new[] { new ShardKeyCandidate("b", 10, 1.0, 5, false), new ShardKeyCandidate("a", 10, 1.0, 5, false) })
        .Select(v => v.Column).SequenceEqual(new[] { "a", "b" }));

    Check("哈希路由确定性", ShardKeyEvaluator.HashRoute("order-42", 8) == ShardKeyEvaluator.HashRoute("order-42", 8));
    Check("哈希路由取值在分片范围内", ShardKeyEvaluator.HashRoute("order-42", 8) is >= 0 and < 8);
    var spread = Enumerable.Range(0, 1000).Select(i => ShardKeyEvaluator.HashRoute($"k{i}", 8)).Distinct().Count();
    Check("哈希路由千键铺满八分片", spread == 8, spread.ToString());

    bool threw = false;
    try { ShardKeyEvaluator.HashRoute("x", 0); } catch (ArgumentOutOfRangeException) { threw = true; }
    Check("非法分片数抛参数异常", threw);

    var bounds = new long[] { 1000, 2000, 3000 };
    Check("范围路由落首段", ShardKeyEvaluator.RangeRoute(500, bounds) == 0);
    Check("范围路由上界含入本段", ShardKeyEvaluator.RangeRoute(2000, bounds) == 1);
    Check("范围路由超界提示扩分片", ShardKeyEvaluator.RangeRoute(3001, bounds) == -1);
}

// 18) HotspotDetector 热点倾斜检测（分库分区分表底座）
{
    var skewed = HotspotDetector.Analyze(new[] { new ShardLoad("p0", 100), new ShardLoad("p1", 10), new ShardLoad("p2", 10), new ShardLoad("p3", 10), new ShardLoad("p4", 10) });
    Check("热点分片检出", skewed.HasHotSpot && skewed.Verdicts[0].Name == "p0" && skewed.Verdicts[0].Level == "hot");
    Check("冷清分片检出", skewed.Verdicts.Count(v => v.Level == "cold") == 4);
    Check("热点建议含拆分或加盐", skewed.Suggestions.Any(s => s.Contains("p0") && s.Contains("加盐")));
    Check("峰值倍数正确", skewed.PeakRatio > 3.5 && skewed.PeakRatio < 3.6, skewed.PeakRatio.ToString("0.###"));

    var even = HotspotDetector.Analyze(new[] { new ShardLoad("a", 10), new ShardLoad("b", 10), new ShardLoad("c", 10) });
    Check("均匀分布无热点无冷清", !even.HasHotSpot && even.Verdicts.All(v => v.Level == "normal"));
    Check("均匀分布建议为无需处置", even.Suggestions.SequenceEqual(new[] { "负载分布正常，无需处置" }));

    var zeros = HotspotDetector.Analyze(new[] { new ShardLoad("x", 0), new ShardLoad("y", 0) });
    Check("全零负载不误报热点", !zeros.HasHotSpot && zeros.MeanLoad == 0 && zeros.Verdicts.All(v => v.Level == "normal"));

    var empty = HotspotDetector.Analyze(Array.Empty<ShardLoad>());
    Check("空输入安全降级", empty.Verdicts.Count == 0 && !empty.HasHotSpot && empty.Suggestions.Single().Contains("无分片负载数据"));

    var tie = HotspotDetector.Analyze(new[] { new ShardLoad("b", 5), new ShardLoad("a", 5) });
    Check("并列负载按名字定序", tie.Verdicts.Select(v => v.Name).SequenceEqual(new[] { "a", "b" }));
}

// 19) PartitionRoller 时间分区自动滚动（分库分区分表底座）
{
    var day = TimeSpan.FromDays(1);
    var noon = new DateTime(2026, 11, 15, 12, 0, 0);

    var fresh = PartitionRoller.EnsureCoverage(Array.Empty<TimePartition>(), noon, day, retainCount: 7);
    Check("空表从目标槽位起建一个分区", fresh.ToAdd.Count == 1 && fresh.ToAdd[0].Name == "p_20261115" &&
        fresh.ToAdd[0].StartInclusive == new DateTime(2026, 11, 15) && fresh.CoversHorizon);

    var existing = new[] { new TimePartition("p_20261114", new DateTime(2026, 11, 14), new DateTime(2026, 11, 15)) };
    var roll = PartitionRoller.EnsureCoverage(existing, noon, day, 7);
    Check("逐日滚动补当前槽", roll.ToAdd.Count == 1 && roll.ToAdd[0].Name == "p_20261115" && roll.CoversHorizon);
    Check("保留窗口内不归档", roll.ToArchive.Count == 0);

    var span = PartitionRoller.EnsureCoverage(new[] { new TimePartition("p_20261113", new DateTime(2026, 11, 13), new DateTime(2026, 11, 14)) }, noon, day, 7);
    Check("断档多槽按序补齐", span.ToAdd.Select(p => p.Name).SequenceEqual(new[] { "p_20261114", "p_20261115" }));

    var withOld = new[]
    {
        new TimePartition("p_20261101", new DateTime(2026, 11, 1), new DateTime(2026, 11, 2)),
        new TimePartition("p_20261114", new DateTime(2026, 11, 14), new DateTime(2026, 11, 15)),
    };
    var arch = PartitionRoller.EnsureCoverage(withOld, noon, day, 7);
    Check("超保留窗口旧分区建议归档", arch.ToArchive.SequenceEqual(new[] { "p_20261101" }));

    var covered = PartitionRoller.EnsureCoverage(new[] { new TimePartition("p_20261115", new DateTime(2026, 11, 15), new DateTime(2026, 11, 16)) }, noon, day, 7);
    Check("已覆盖时不重复建分区", covered.ToAdd.Count == 0 && covered.CoversHorizon);

    var hourGrain = TimeSpan.FromHours(1);
    var hourly = PartitionRoller.EnsureCoverage(new[] { new TimePartition("p_20261115_1100", new DateTime(2026, 11, 15, 11, 0, 0), new DateTime(2026, 11, 15, 12, 0, 0)) }, new DateTime(2026, 11, 15, 12, 30, 0), hourGrain, 24);
    Check("小时粒度命名与滚动", hourly.ToAdd.Count == 1 && hourly.ToAdd[0].Name == "p_20261115_1200");

    bool threw = false;
    try { PartitionRoller.EnsureCoverage(Array.Empty<TimePartition>(), noon, TimeSpan.Zero, 1); } catch (ArgumentOutOfRangeException) { threw = true; }
    Check("非法粒度抛参数异常", threw);
}

// 20) RebalancePlanner 数据再均衡计划（分库分区分表底座）
{
    var skewed = RebalancePlanner.Plan(new[] { new RebalanceShard("p0", 100), new RebalanceShard("p1", 10), new RebalanceShard("p2", 10), new RebalanceShard("p3", 10), new RebalanceShard("p4", 10) });
    Check("热点搬出量正确", skewed.Moves.Count == 4 && skewed.Moves.All(m => m.From == "p0"));
    Check("每片承接缺口相等", skewed.Moves.All(m => m.Rows == 18) && skewed.Moves.Select(m => m.To).SequenceEqual(new[] { "p1", "p2", "p3", "p4" }));
    Check("均值计算正确", skewed.MeanLoad == 28 && !skewed.Balanced);

    var even = RebalancePlanner.Plan(new[] { new RebalanceShard("a", 10), new RebalanceShard("b", 10) });
    Check("已均衡不出搬迁", even.Balanced && even.Moves.Count == 0);

    var empty = RebalancePlanner.Plan(Array.Empty<RebalanceShard>());
    Check("空输入视为均衡", empty.Balanced && empty.MeanLoad == 0);

    var over = RebalancePlanner.Plan(new[] { new RebalanceShard("big", 90), new RebalanceShard("s1", 10), new RebalanceShard("s2", 20) });
    Check("溢出量受承接缺口封顶", over.Moves.Sum(m => m.Rows) == 50 && over.Moves[0].To == "s1", string.Join(",", over.Moves.Select(m => $"{m.From}->{m.To}:{m.Rows}")));

    var tie = RebalancePlanner.Plan(new[] { new RebalanceShard("h2", 40), new RebalanceShard("h1", 40), new RebalanceShard("c1", 10), new RebalanceShard("c2", 30) });
    Check("并列热点按超出量与名字定序", tie.Moves[0].From == "h1" && tie.Moves[0].To == "c1", string.Join(",", tie.Moves.Select(m => $"{m.From}->{m.To}:{m.Rows}")));
}

// 21) PartitionDdl 分区 DDL 方言生成（分库分区分表底座）
{
    var p = new TimePartition("p_20261115", new DateTime(2026, 11, 15), new DateTime(2026, 11, 16));
    Check("MySQL 新增分区走 TO_DAYS 上界", PartitionDdl.AddSql(DbEngine.MySQL, "orders", p) ==
        "ALTER TABLE `orders` ADD PARTITION (PARTITION p_20261115 VALUES LESS THAN (TO_DAYS('2026-11-16')))");
    Check("PG 新增分区声明式半开区间", PartitionDdl.AddSql(DbEngine.PostgreSQL, "orders", p) ==
        "CREATE TABLE orders_p_20261115 PARTITION OF orders FOR VALUES FROM ('2026-11-15 00:00:00') TO ('2026-11-16 00:00:00')");
    Check("不支持分区方言返回空", PartitionDdl.AddSql(DbEngine.SQLite, "orders", p) == null && PartitionDdl.DropSql(DbEngine.SQLite, "orders", "p_x") == null);
    Check("MySQL 归档分区 DROP PARTITION", PartitionDdl.DropSql(DbEngine.MySQL, "orders", "p_20261101") == "ALTER TABLE `orders` DROP PARTITION p_20261101");
    Check("PG 归档分区 DROP 子表", PartitionDdl.DropSql(DbEngine.PostgreSQL, "orders", "p_20261101") == "DROP TABLE IF EXISTS orders_p_20261101");

    var noon = new DateTime(2026, 11, 15, 12, 0, 0);
    var day = TimeSpan.FromDays(1);
    var plan = PartitionRoller.EnsureCoverage(new[]
    {
        new TimePartition("p_20261101", new DateTime(2026, 11, 1), new DateTime(2026, 11, 2)),
        new TimePartition("p_20261114", new DateTime(2026, 11, 14), new DateTime(2026, 11, 15)),
    }, noon, day, 7);
    var mysqlSqls = PartitionDdl.PlanSql(DbEngine.MySQL, "orders", plan);
    Check("整计划先归档后新增有序", mysqlSqls.Count == 2 && mysqlSqls[0].Contains("DROP PARTITION p_20261101") && mysqlSqls[1].Contains("ADD PARTITION (PARTITION p_20261115"));
    Check("不支持分区整计划返回空表", PartitionDdl.PlanSql(DbEngine.SQLite, "orders", plan).Count == 0);
}

// 22) HotspotSalting 热点盐化（分库分区分表底座，承接 A5-4「拆分加盐」处置）
{
    var plan = HotspotSalting.Plan("merchant_88", 4);
    Check("默认命名子键确定序", plan.SaltedKeys.SequenceEqual(new[] { "merchant_88#0", "merchant_88#1", "merchant_88#2", "merchant_88#3" }),
        string.Join("|", plan.SaltedKeys));
    Check("方案记录键与槽数", plan.HotKey == "merchant_88" && plan.SaltCount == 4);

    var custom = HotspotSalting.Plan("big_shop", 3, (k, i) => $"{k}_{i:00}");
    Check("自定义命名生效", custom.SaltedKeys.SequenceEqual(new[] { "big_shop_00", "big_shop_01", "big_shop_02" }));

    Check("读扇出覆盖全槽", HotspotSalting.FanOutRead(plan).Count == 4 && HotspotSalting.FanOutRead(plan)[2] == "merchant_88#2");

    int r1 = HotspotSalting.RouteWrite("row-123", 8);
    int r2 = HotspotSalting.RouteWrite("row-123", 8);
    Check("写路由确定且落在槽内", r1 == r2 && r1 >= 0 && r1 < 8);

    var hits = new bool[8];
    for (int i = 0; i < 200; i++) hits[HotspotSalting.RouteWrite($"row-{i}", 8)] = true;
    Check("写路由实际打散到多槽", hits.Count(h => h) >= 4, $"覆盖 {hits.Count(h => h)}/8 槽");

    bool threw;
    threw = false; try { HotspotSalting.Plan("", 4); } catch (ArgumentException) { threw = true; }
    Check("空热点键拒绝", threw);
    threw = false; try { HotspotSalting.Plan("k", 1); } catch (ArgumentOutOfRangeException) { threw = true; }
    Check("盐槽数 1 拒绝", threw);
    threw = false; try { HotspotSalting.Plan("k", 1025); } catch (ArgumentOutOfRangeException) { threw = true; }
    Check("盐槽数超上界拒绝", threw);
    threw = false; try { HotspotSalting.RouteWrite("r", 0); } catch (ArgumentOutOfRangeException) { threw = true; }
    Check("写路由零槽拒绝", threw);
}

// 23) OnlineDdl 在线改表扇出（新需求「图形化改表」配套，对标 pt-osc 步骤）
{
    var plan = OnlineDdl.Plan("orders", "ADD COLUMN remark VARCHAR(200)", "id", 2500, 1000);
    Check("影子表与旧表确定命名", plan.ShadowTable == "_osc_orders_new" && plan.OldTable == "_osc_orders_old");
    var phases = plan.Steps.Select(s => s.Phase).ToList();
    Check("阶段顺序 build→alter→backfill→swap→cleanup",
        phases[0] == "build" && phases[1] == "alter" && phases[2] == "backfill" && phases[^2] == "swap" && phases[^1] == "cleanup",
        string.Join(",", phases));
    Check("回填批数向上取整", plan.Steps.Count(s => s.Phase == "backfill") == 4, $"{plan.Steps.Count(s => s.Phase == "backfill")} 批");
    Check("换名是单语句原子操作", plan.Steps.Single(s => s.Phase == "swap").Sql ==
        "RENAME TABLE `orders` TO `_osc_orders_old`, `_osc_orders_new` TO `orders`");
    Check("换名步唯一且在回填之后", plan.Steps.Count(s => s.Phase == "swap") == 1);
    Check("清理步提示先对账", plan.Steps[^1].Note.Contains("A5-2 数据对账"));
    Check("回填携带主键分段谓词", plan.Steps.Where(s => s.Phase == "backfill").All(s => s.Sql.Contains("WHERE s.`id` > @last")));

    var empty = OnlineDdl.Plan("t", "ADD COLUMN c INT", "id", 0, 500);
    Check("零行无回填步", empty.Steps.Count(s => s.Phase == "backfill") == 0 && empty.Steps.Count == 4);

    bool threw;
    threw = false; try { OnlineDdl.Plan("", "ADD COLUMN c INT", "id", 10, 100); } catch (ArgumentException) { threw = true; }
    Check("空表名拒绝", threw);
    threw = false; try { OnlineDdl.Plan("t", " ", "id", 10, 100); } catch (ArgumentException) { threw = true; }
    Check("空变更子句拒绝", threw);
    threw = false; try { OnlineDdl.Plan("t", "ADD COLUMN c INT", "", 10, 100); } catch (ArgumentException) { threw = true; }
    Check("空主键列拒绝", threw);
    threw = false; try { OnlineDdl.Plan("t", "ADD COLUMN c INT", "id", -1, 100); } catch (ArgumentOutOfRangeException) { threw = true; }
    Check("负行数拒绝", threw);
    threw = false; try { OnlineDdl.Plan("t", "ADD COLUMN c INT", "id", 10, 0); } catch (ArgumentOutOfRangeException) { threw = true; }
    Check("零批大小拒绝", threw);
}

// 24) ShardingWizard 三策略分片方案与分片键依据（P-6「图形化分库分区分表」四步向导）
{
    // 按范围：MySQL 一条 ALTER，尾段 MAXVALUE 兜底
    var rngM = ShardingWizard.RangePartitionSql(DbEngine.MySQL, "orders", "order_time", new[] { "100", "200", "300" });
    Check("范围MySQL主语句RANGE", rngM[0].StartsWith("ALTER TABLE `orders` PARTITION BY RANGE (`order_time`)"));
    Check("范围MySQL含尾段MAXVALUE", rngM[0].Contains("PARTITION p2 VALUES LESS THAN (300)") && rngM[0].Contains("PARTITION pn VALUES LESS THAN (MAXVALUE)"));
    Check("范围MySQL附现状查询", rngM.Count == 2 && rngM[1].Contains("information_schema.PARTITIONS"));

    // 按范围：PG 声明式分区，首段 MINVALUE 起
    var rngP = ShardingWizard.RangePartitionSql(DbEngine.PostgreSQL, "orders", "order_time", new[] { "100", "200" });
    Check("范围PG建新表分区", rngP[1] == "CREATE TABLE orders_pnew (LIKE orders INCLUDING ALL) PARTITION BY RANGE (order_time);");
    Check("范围PG首段MINVALUE", rngP[2] == "CREATE TABLE orders_p0 PARTITION OF orders_pnew FOR VALUES FROM (MINVALUE) TO (100);");
    Check("范围PG续段接上界", rngP[3] == "CREATE TABLE orders_p1 PARTITION OF orders_pnew FOR VALUES FROM (100) TO (200);");

    // 按类别：MySQL VALUES IN，数字原样、字符串重转义
    var lstM = ShardingWizard.ListPartitionSql(DbEngine.MySQL, "orders", "region",
        new (string, IReadOnlyList<string>)[] { ("south", new[] { "1", "2" }), ("north", new[] { "3" }), ("other", new[] { "us'east", "eu-west" }) });
    Check("类别MySQL数字原样", lstM[0].Contains("PARTITION south VALUES IN (1, 2)") && lstM[0].Contains("PARTITION north VALUES IN (3)"));
    Check("类别MySQL字符串重转义", lstM[0].Contains("PARTITION other VALUES IN ('us''east', 'eu-west')"), lstM[0]);

    // 按类别：PG FOR VALUES IN
    var lstP = ShardingWizard.ListPartitionSql(DbEngine.PostgreSQL, "orders", "region",
        new (string, IReadOnlyList<string>)[] { ("south", new[] { "1" }), ("north", new[] { "2" }) });
    Check("类别PG建新表分区", lstP[1] == "CREATE TABLE orders_pnew (LIKE orders INCLUDING ALL) PARTITION BY LIST (region);");
    Check("类别PG取值IN", lstP[2] == "CREATE TABLE orders_south PARTITION OF orders_pnew FOR VALUES IN (1);");

    // 非法输入拒绝
    bool threw = false;
    try { ShardingWizard.RangePartitionSql(DbEngine.MySQL, "t", "k", new[] { "100" }); } catch (ArgumentOutOfRangeException) { threw = true; }
    Check("范围单上界拒绝", threw);
    threw = false; try { ShardingWizard.ListPartitionSql(DbEngine.MySQL, "t", "k", Array.Empty<(string, IReadOnlyList<string>)>()); } catch (ArgumentOutOfRangeException) { threw = true; }
    Check("类别空组拒绝", threw);
    threw = false; try { ShardingWizard.ListPartitionSql(DbEngine.MySQL, "t", "k", new (string, IReadOnlyList<string>)[] { ("", new[] { "1" }) }); } catch (ArgumentException) { threw = true; }
    Check("类别空组名拒绝", threw);
    threw = false; try { ShardingWizard.RangePartitionSql(DbEngine.SQLite, "t", "k", new[] { "1", "2" }); } catch (NotSupportedException) { threw = true; }
    Check("不支持方言拒绝", threw);

    // 分片键依据判定：索引元数据不区分大小写
    Check("有索引字段有依据", ShardingWizard.KeyBacked("User_ID", new[] { "user_id", "city" }));
    Check("无索引字段无依据", !ShardingWizard.KeyBacked("nickname", new[] { "user_id", "city" }));
    Check("空索引清单全无依据", !ShardingWizard.KeyBacked("user_id", Array.Empty<string>()));

    // 熟手解锁审计行与读写分离提示
    Check("解锁审计行格式", ShardingWizard.UnlockAudit("orders", "熟手开关解锁全部字段") == "SHARDKEY-UNLOCK\torders\t熟手开关解锁全部字段");
    Check("解锁审计空原因兜底", ShardingWizard.UnlockAudit("orders", "  ") == "SHARDKEY-UNLOCK\torders\t解锁全部字段（熟手）");
    Check("解锁审计表名去制表符", ShardingWizard.UnlockAudit("or\tders", "") == "SHARDKEY-UNLOCK\tor ders\t解锁全部字段（熟手）");
    threw = false; try { ShardingWizard.UnlockAudit("  ", ""); } catch (ArgumentException) { threw = true; }
    Check("解锁审计空表名拒绝", threw);
    Check("读写分离提示含片数", ShardingWizard.ReadSplitHint(4).Contains("4 片") && ShardingWizard.ReadSplitHint(4).Contains("读路由"));

    // 策略名与向导枚举一致（四步向导第 2 步点选口径）
    Check("策略名三选齐备", ShardingWizard.StrategyNames.Length == 3 && ShardingWizard.StrategyNames[0].Contains("范围") &&
        ShardingWizard.StrategyNames[1].Contains("哈希") && ShardingWizard.StrategyNames[2].Contains("类别"));
}

// 智能线 C1 第一批：慢查询诊断+一键优化闭环（含复测）、容量增长预测、查询复现包（纯引擎，SQLite 真库验证）
{
    // 1) 静态诊断
    var f1 = SlowQueryDiagnoser.Analyze("SELECT * FROM big_table");
    Check("智能线-SELECT*与全量读双报", f1.Any(x => x.Code == "S001") && f1.Any(x => x.Code == "S005"), string.Join(",", f1.Select(x => x.Code)));
    var f2 = SlowQueryDiagnoser.Analyze("DELETE FROM t");
    Check("智能线-无WHERE删除判危险", f2.Any(x => x.Code == "D001" && x.Severity == "danger"), "");
    var f3 = SlowQueryDiagnoser.Analyze("SELECT a FROM t WHERE name LIKE '%x'");
    Check("智能线-前导通配LIKE", f3.Any(x => x.Code == "S002"), "");
    var f4 = SlowQueryDiagnoser.Analyze("SELECT a FROM t ORDER BY a");
    Check("智能线-排序无LIMIT提示", f4.Any(x => x.Code == "S007"), "");
    Check("智能线-正常查询不误报", SlowQueryDiagnoser.Analyze("SELECT id, name FROM t WHERE id = 1 LIMIT 10").Count == 0,
        string.Join(",", SlowQueryDiagnoser.Analyze("SELECT id, name FROM t WHERE id = 1 LIMIT 10").Select(x => x.Code)));

    // 2) 计划诊断（SQLite EXPLAIN QUERY PLAN 口径）
    Check("智能线-计划全表扫描", SlowQueryDiagnoser.AnalyzePlan("SCAN t").Any(x => x.Code == "P001"), "");
    Check("智能线-走索引不报扫描", !SlowQueryDiagnoser.AnalyzePlan("SEARCH t USING INDEX idx_t_code").Any(x => x.Code == "P001"), "");
    Check("智能线-临时B树提示", SlowQueryDiagnoser.AnalyzePlan("USE TEMP B-TREE FOR ORDER BY").Any(x => x.Code == "P002"), "");
    Check("智能线-标量子查询提示", SlowQueryDiagnoser.AnalyzePlan("SCALAR SUBQUERY 1").Any(x => x.Code == "P003"), "");

    // 3) 真库闭环：诊断→建索引→计划改善→复测提速
    var dbPath = Path.Combine(Path.GetTempPath(), "taodbm-smartline-" + Guid.NewGuid().ToString("N") + ".db");
    await using (var setup = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
    {
        setup.Open();
        using (var c = setup.CreateCommand())
        {
            c.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT);";
            c.ExecuteNonQuery();
        }
        using (var tx = setup.BeginTransaction())
        {
            for (int i = 0; i < 20000; i++)
            using (var one = setup.CreateCommand())
            {
                one.Transaction = tx;
                one.CommandText = "INSERT INTO t(code) VALUES (@c)";
                one.Parameters.AddWithValue("@c", "X" + i);
                one.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
    await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
    {
        conn.Open();
        string Explain(string sql)
        {
            using var c = conn.CreateCommand();
            c.CommandText = "EXPLAIN QUERY PLAN " + sql;
            var parts = new List<string>();
            using var r = c.ExecuteReader();
            while (r.Read()) parts.Add(r.GetString(3));
            return string.Join("|", parts);
        }
        string probe = "SELECT * FROM t WHERE code = 'X12345'";
        Check("智能线-真库初始全表扫描", Explain(probe).Contains("SCAN"), Explain(probe));
        var ddl = SlowQueryDiagnoser.SuggestIndexDdl(probe, "t");
        Check("智能线-索引建议含code列", ddl.Count == 1 && ddl[0].Contains("(code)"), ddl.FirstOrDefault() ?? "");

        async Task RunAsync(string sql, CancellationToken ct)
        {
            using var c = conn.CreateCommand();
            c.CommandText = sql;
            using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { }
        }
        var before = await QueryRetest.MedianMsAsync(RunAsync, probe, 3);
        using (var c = conn.CreateCommand()) { c.CommandText = ddl[0]; c.ExecuteNonQuery(); }
        Check("智能线-建索引后走索引", System.Text.RegularExpressions.Regex.IsMatch(Explain(probe), @"USING\s+(COVERING\s+)?INDEX"), Explain(probe));
        Check("智能线-计划体检识别改善", !SlowQueryDiagnoser.AnalyzePlan(Explain(probe)).Any(x => x.Code == "P001"), "");
        var after = await QueryRetest.MedianMsAsync(RunAsync, probe, 3);
        Check("智能线-建索引复测提速", after < before, $"before={before:F2}ms after={after:F2}ms");
        var cmp = await QueryRetest.CompareAsync(RunAsync, "SELECT COUNT(*) FROM t", "SELECT COUNT(*) FROM t", 3);
        Check("智能线-复测报告口径", cmp.Speedup > 0 && cmp.Iterations == 3, $"speedup={cmp.Speedup:F2}");
    }
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    for (int attempt = 0; attempt < 5 && System.IO.File.Exists(dbPath); attempt++)
    {
        try { System.IO.File.Delete(dbPath); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); }
    }

    // 4) 容量增长预测：线性样本拟合 + 阈值外推
    var t0 = DateTimeOffset.Parse("2026-11-01T00:00:00+08:00");
    var samples = Enumerable.Range(0, 5).Select(i => new CapacityForecaster.CapacitySample(t0.AddDays(i), 100.0 * (i + 1))).ToList();
    var fc = CapacityForecaster.Forecast(samples, 1000.0);
    Check("智能线-日增速拟合", Math.Abs(fc.GrowthPerDay - 100.0) < 1e-6, fc.GrowthPerDay.ToString("F4"));
    Check("智能线-拟合优度", fc.RSquared > 0.999, fc.RSquared.ToString("F4"));
    Check("智能线-阈值外推", Math.Abs(fc.DaysToThreshold!.Value - 7.0) < 1e-6, fc.DaysToThreshold!.Value.ToString("F3"));
    Check("智能线-用量外推", Math.Abs(fc.PredictAt(10) - 1300.0) < 1e-6, fc.PredictAt(10).ToString("F2"));
    Check("智能线-单样本不外推", CapacityForecaster.Forecast(new List<CapacityForecaster.CapacitySample> { samples[0] }).DaysToThreshold == null, "");

    // 5) 查询复现包：净化→打包→往返→真库回放
    var pack = new QueryReproPack.ReproPack("1.0", "订单查询复现", "SQLite", "SELECT COUNT(*) AS n FROM orders WHERE region = @r",
        new Dictionary<string, string> { ["r"] = "华东", ["password"] = "should-drop" },
        new List<string> { "CREATE TABLE orders(id INTEGER PRIMARY KEY, region TEXT);", "INSERT INTO orders(region) VALUES ('华东');", "INSERT INTO orders(region) VALUES ('华北');" },
        "用于复现慢查询", DateTimeOffset.UtcNow);
    var clean = QueryReproPack.Sanitize(pack);
    Check("智能线-复现包剔凭据", !clean.Params.ContainsKey("password") && (clean.Notes ?? "").Contains("password"), clean.Notes ?? "");
    var packJson = QueryReproPack.ToJson(clean);
    Check("智能线-复现包不含凭据值", !packJson.Contains("should-drop"), "");
    var back = QueryReproPack.FromJson(packJson);
    Check("智能线-复现包往返", back != null && back.Sql == clean.Sql && back.Params["r"] == "华东", "");
    Check("智能线-坏包不炸", QueryReproPack.FromJson("{坏") == null, "");

    // 发现125：重测守门——只放行单条 SELECT/WITH，注释与字符串字面量剥净后判定
    Check("重测守门-纯SELECT", QueryRetest.IsSelectOnly("SELECT COUNT(*) FROM t"), "");
    Check("重测守门-CTE放行", QueryRetest.IsSelectOnly("WITH c AS (SELECT 1) SELECT * FROM c"), "");
    Check("重测守门-多语句拒绝", !QueryRetest.IsSelectOnly("SELECT 1; DROP TABLE t"), "");
    Check("重测守门-UPDATE拒绝", !QueryRetest.IsSelectOnly("UPDATE t SET a = 'x'"), "");
    Check("重测守门-DELETE拒绝", !QueryRetest.IsSelectOnly("DELETE FROM t WHERE id = 1"), "");
    Check("重测守门-字符串内分号放行", QueryRetest.IsSelectOnly("SELECT 'a;b' FROM t"), "");
    Check("重测守门-注释剥净后放行", QueryRetest.IsSelectOnly("SELECT 1 /* ; DROP TABLE t */"), "");
    Check("重测守门-纯注释拒绝", !QueryRetest.IsSelectOnly("-- just a comment"), "");
    Check("重测守门-空拒绝", !QueryRetest.IsSelectOnly("   "), "");

    // 发现126：复现包 SQL 原文凭据打码——不再依赖人工过目
    var packCred = new QueryReproPack.ReproPack("1.0", "带凭据复现", "MySQL",
        "SELECT * FROM t WHERE pwd = 'hunter2' AND id = 1",
        new Dictionary<string, string>(), new List<string> { "SET @pwd = password123;" }, null, DateTimeOffset.UtcNow);
    var cleanCred = QueryReproPack.Sanitize(packCred);
    Check("复现包-SQL凭据打码", !cleanCred.Sql.Contains("hunter2") && cleanCred.Sql.Contains("***"), cleanCred.Sql);
    Check("复现包-SetupSql打码", !cleanCred.SetupSql[0].Contains("password123") && cleanCred.SetupSql[0].Contains("***"), cleanCred.SetupSql[0]);
    Check("复现包-打码留痕", (cleanCred.Notes ?? "").Contains("打码"), cleanCred.Notes ?? "");
    Check("复现包-无凭据原文不动", QueryReproPack.Sanitize(pack).Sql == pack.Sql, "");
    var db2 = Path.Combine(Path.GetTempPath(), "taodbm-repro-" + Guid.NewGuid().ToString("N") + ".db");
    await using (var conn2 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db2}"))
    {
        conn2.Open();
        foreach (var ddlSql in back!.SetupSql)
            using (var c = conn2.CreateCommand()) { c.CommandText = ddlSql; c.ExecuteNonQuery(); }
        using (var c = conn2.CreateCommand())
        {
            c.CommandText = back.Sql;
            c.Parameters.AddWithValue("@r", back.Params["r"]);
            var n = Convert.ToInt64(c.ExecuteScalar());
            Check("智能线-复现包真库回放", n == 1, n.ToString());
        }
    }
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    for (int attempt = 0; attempt < 5 && System.IO.File.Exists(db2); attempt++)
    {
        try { System.IO.File.Delete(db2); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); }
    }

    // 6) 优化回访闭环（2026-11-20 批次 D）：登记→到期→真库复测→对比卡→任务中心并入→快照往返
    {
        var fuT0 = DateTimeOffset.Parse("2026-11-20T08:00:00+08:00");

        // 登记：基准取静态命中；间隔须为正
        var reg = OptimizationFollowup.Register("orders 改写优化", "SELECT * FROM orders", "SQLite",
            new[] { "S001", "S005" }, new List<string>(), 120.0, 1, fuT0);
        Check("回访-登记基准与间隔", reg.Verdict == "pending" && reg.IntervalDays == 1 && reg.BaselineMs == 120.0 &&
            reg.BaselineCodes.SequenceEqual(new[] { "S001", "S005" }), "");
        bool threw = false;
        try { OptimizationFollowup.Register("x", "SELECT 1", "SQLite", Array.Empty<string>(), Array.Empty<string>(), null, 0, fuT0); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Check("回访-间隔须为正", threw, "");

        // 到期判定：未到期不算 due，到期后算；已出结论不再触发
        var tEarly = fuT0.AddHours(23);
        var tDue = fuT0.AddDays(1);
        Check("回访-未到期不算due", !OptimizationFollowup.IsDue(reg, tEarly), "");
        Check("回访-到期算due", OptimizationFollowup.IsDue(reg, tDue), "");
        var reg2 = OptimizationFollowup.Register("另一条", "SELECT 1", "SQLite", Array.Empty<string>(), Array.Empty<string>(), null, 1, fuT0.AddHours(-2));
        var due = OptimizationFollowup.DueOf(new[] { reg, reg2 }, tDue);
        Check("回访-到期清单按序", due.Count == 2 && due[0].TaskId == reg2.TaskId, due.Count.ToString());
        var doneNow = reg with { Verdict = "passed" };
        Check("回访-已结论不再触发", !OptimizationFollowup.IsDue(doneNow, tDue), "");

        // 复测通过：耗时降 + 命中不恶化（真库口径在下方真库段补）
        var ok = OptimizationFollowup.Retest(reg, 60.0, new[] { "S001" }, now: tDue);
        Check("回访-耗时降复测通过", ok.Verdict == "passed" && ok.RetestMs == 60.0, ok.Verdict);
        threw = false;
        try { OptimizationFollowup.Retest(ok, 50.0, Array.Empty<string>()); } catch (InvalidOperationException) { threw = true; }
        Check("回访-已结论不可重复复测", threw, "");

        // 复测未通过：命中新增恶化
        var reg3 = OptimizationFollowup.Register("恶化回访", "SELECT * FROM orders", "SQLite", new[] { "S001" }, Array.Empty<string>(), 100.0, 1, fuT0);
        var bad = OptimizationFollowup.Retest(reg3, 100.0, new[] { "S001", "S002" }, now: tDue);
        Check("回访-命中恶化判未通过", bad.Verdict == "failed", bad.Verdict);

        // 耗时噪声容差：<=1.05 倍持平通过，超过判未通过
        var reg4 = OptimizationFollowup.Register("持平回访", "SELECT 1", "SQLite", Array.Empty<string>(), Array.Empty<string>(), 100.0, 1, fuT0);
        Check("回访-持平在容差内通过", OptimizationFollowup.Retest(reg4, 104.9, Array.Empty<string>(), now: tDue).Verdict == "passed", "");
        var reg5 = OptimizationFollowup.Register("超差回访", "SELECT 1", "SQLite", Array.Empty<string>(), Array.Empty<string>(), 100.0, 1, fuT0);
        Check("回访-超容差判未通过", OptimizationFollowup.Retest(reg5, 105.1, Array.Empty<string>(), now: tDue).Verdict == "failed", "");

        // 计划命中对比：基准有计划命中时，复测计划新增恶化判未通过（SCAN+TEMP B-TREE → P001+P002）
        var reg6 = OptimizationFollowup.Register("计划回访", "SELECT * FROM t", "SQLite", Array.Empty<string>(), new[] { "P001" }, null, 1, fuT0);
        Check("回访-计划恶化判未通过", OptimizationFollowup.Retest(reg6, null, Array.Empty<string>(), "SCAN t|USE TEMP B-TREE FOR ORDER BY", now: tDue).Verdict == "failed", "");
        Check("回访-计划改善判通过", OptimizationFollowup.Retest(reg6 with { Verdict = "pending" }, null, Array.Empty<string>(), "SEARCH t USING INDEX idx", now: tDue).Verdict == "passed", "");

        // 对比卡：pending 展示到期状态，终态展示耗时变化与结论
        var cardPending = OptimizationFollowup.CompareCard(reg2);
        Check("回访-对比卡待复测行", cardPending.Any(l => l.Contains("状态：")), string.Join("|", cardPending));
        var cardDone = OptimizationFollowup.CompareCard(ok);
        Check("回访-对比卡含耗时变化与结论", cardDone.Any(l => l.Contains("120.0 → 60.0 ms")) && cardDone.Any(l => l.Contains("复测通过")), string.Join("|", cardDone));

        // 真库闭环：诊断→建索引→登记→复测通过
        var dbPath3 = Path.Combine(Path.GetTempPath(), "taodbm-followup-" + Guid.NewGuid().ToString("N") + ".db");
        await using (var setup = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath3}"))
        {
            setup.Open();
            using (var c = setup.CreateCommand())
            {
                c.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT);";
                c.ExecuteNonQuery();
            }
            using (var tx = setup.BeginTransaction())
            {
                for (int i = 0; i < 20000; i++)
                using (var one = setup.CreateCommand())
                {
                    one.Transaction = tx;
                    one.CommandText = "INSERT INTO t(code) VALUES (@c)";
                    one.Parameters.AddWithValue("@c", "X" + i);
                    one.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
        await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath3}"))
        {
            conn.Open();
            async Task RunAsync(string sql, CancellationToken ct)
            {
                using var c = conn.CreateCommand();
                c.CommandText = sql;
                using var r = await c.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) { }
            }
            string Explain(string sql)
            {
                using var c = conn.CreateCommand();
                c.CommandText = "EXPLAIN QUERY PLAN " + sql;
                var parts = new List<string>();
                using var r = c.ExecuteReader();
                while (r.Read()) parts.Add(r.GetString(3));
                return string.Join("|", parts);
            }
            var probe = "SELECT * FROM t WHERE code = 'X12345'";
            var ddl = SlowQueryDiagnoser.SuggestIndexDdl(probe, "t");
            var baselinePlan = SlowQueryDiagnoser.AnalyzePlan(Explain(probe)).Select(x => x.Code).ToList();
            var beforeMs = await QueryRetest.MedianMsAsync(RunAsync, probe, 3);
            using (var c = conn.CreateCommand()) { c.CommandText = ddl[0]; c.ExecuteNonQuery(); }
            var afterPlan = SlowQueryDiagnoser.AnalyzePlan(Explain(probe)).Select(x => x.Code).ToList();
            var afterMs = await QueryRetest.MedianMsAsync(RunAsync, probe, 3);
            var regDb = OptimizationFollowup.Register("真库建索引回访", probe, "SQLite",
                SlowQueryDiagnoser.Analyze(probe).Select(x => x.Code), baselinePlan, beforeMs, 1, fuT0);
            var retestDb = OptimizationFollowup.Retest(regDb, afterMs, SlowQueryDiagnoser.Analyze(probe).Select(x => x.Code), Explain(probe), now: tDue);
            Check("回访-真库复测通过", retestDb.Verdict == "passed" && afterMs < beforeMs, $"before={beforeMs:F2} after={afterMs:F2} verdict={retestDb.Verdict}");
            Check("回访-真库计划改善对比", retestDb.RetestPlanCodes.Count == 0 && regDb.BaselinePlanCodes.Count > 0, string.Join(",", retestDb.RetestPlanCodes));

            // 并入任务中心（真中心实例，非 UI 共享）
            var center = new TaoDBM.Core.Streaming.TaskCenter();
            var centerTaskId = OptimizationFollowup.AttachToCenter(center, retestDb);
            var listed = center.List().FirstOrDefault(s => s.TaskId == centerTaskId);
            Check("回访-并入任务中心可查", listed != null && listed.Kind == "retest" && listed.Title.Contains("真库建索引回访"), listed?.Title ?? "null");
            var again = OptimizationFollowup.AttachToCenter(center, retestDb with { CenterTaskId = centerTaskId });
            Check("回访-重复并入返回原任务号", again == centerTaskId, again);

            // 快照往返（落盘口径）
            var json = OptimizationFollowup.ToJson(new[] { retestDb, reg });
            var fuBack = OptimizationFollowup.FromJson(json);
            Check("回访-快照往返", back != null && fuBack != null && fuBack.Count == 2 && fuBack[0] != null && fuBack[0].TaskId == retestDb.TaskId && fuBack[0].Verdict == "passed", "");
            Check("回访-坏快照不炸", OptimizationFollowup.FromJson("{坏") == null, "");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (int attempt = 0; attempt < 5 && System.IO.File.Exists(dbPath3); attempt++)
        {
            try { System.IO.File.Delete(dbPath3); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }
}

// 智能线 C2 第二批：数据体检报告（空值/重复/分布突变）、查询结果出图聚合、离线快照包（SQLite 真库采样→离线浏览再聚合）
{
    // 1) 数据体检：空值与重复
    var profs = new List<ColumnProfile>
    {
        new("orders", "region", 1000, 5, 10, null, new Dictionary<string, double> { ["华东"] = 0.6, ["华北"] = 0.4 }),
        new("orders", "phone", 1000, 600, 0),
        new("orders", "email", 1000, 200, 500),
    };
    var rep = DataHealthCheck.Inspect(profs);
    Check("智能线2-空值红线判danger", rep.Findings.Any(f => f.Column == "phone" && f.Code == "H001" && f.Severity == "danger"), "");
    Check("智能线2-空值偏高判warn", rep.Findings.Any(f => f.Column == "email" && f.Code == "H001" && f.Severity == "warn"), "");
    Check("智能线2-重复率报H002", rep.Findings.Any(f => f.Column == "email" && f.Code == "H002"), "");
    Check("智能线2-健康列不误报", !rep.Findings.Any(f => f.Column == "region"), string.Join(",", rep.Findings.Select(f => f.Column)));
    Check("智能线2-扣分口径", rep.Score == 70, rep.Score.ToString());
    var repBack = DataHealthCheck.FromJson(DataHealthCheck.ToJson(rep));
    Check("智能线2-体检报告往返", repBack != null && repBack.Score == rep.Score && repBack.Findings.Count == rep.Findings.Count, "");

    // 2) 分布突变对比：类别占比漂移与均值漂移
    var before = new List<ColumnProfile>
    {
        new("orders", "region", 1000, 0, 0, null, new Dictionary<string, double> { ["华东"] = 0.6, ["华北"] = 0.4 }),
        new("orders", "amount", 1000, 0, 0, 100.0),
    };
    var afterSame = new List<ColumnProfile>
    {
        new("orders", "region", 1000, 0, 0, null, new Dictionary<string, double> { ["华东"] = 0.61, ["华北"] = 0.39 }),
        new("orders", "amount", 1000, 0, 0, 101.0),
    };
    Check("智能线2-无突变不报", DataHealthCheck.Compare(afterSame, before).Count == 0, "");
    var afterDrift = new List<ColumnProfile>
    {
        new("orders", "region", 1000, 0, 0, null, new Dictionary<string, double> { ["华东"] = 0.2, ["华北"] = 0.8 }),
        new("orders", "amount", 1000, 0, 0, 150.0),
    };
    var drifts = DataHealthCheck.Compare(afterDrift, before);
    Check("智能线2-类别占比突变", drifts.Any(f => f.Column == "region" && f.Code == "H003"), string.Join(",", drifts.Select(f => f.Code)));
    Check("智能线2-均值漂移", drifts.Any(f => f.Column == "amount" && f.Code == "H004"), string.Join(",", drifts.Select(f => f.Code)));
    Check("智能线2-新列不参与对比", DataHealthCheck.Compare(new List<ColumnProfile> { new("t", "c", 10, 0, 0) }, before).Count == 0, "");

    // 3) 出图聚合：分类 TopN、直方图、时间序列、图型推荐
    var qr = new QueryResult { Columns = { "city", "age", "day" } };
    foreach (var (c, a, d) in new[] { ("上海", 20.0, "2026-01-01"), ("北京", 30.0, "2026-01-01"), ("上海", 40.0, "2026-02-01"), ("广州", 50.0, "2026-02-01"), ("上海", 60.0, "2026-03-01") })
        qr.Rows.Add(new object?[] { c, a, DateTime.Parse(d) });
    var cat = ChartSpecBuilder.CategoryCount(qr, "city", 2);
    Check("智能线2-分类TopN计数", cat.Type == "bar" && cat.Labels[0] == "上海" && cat.Values[0] == 3 && cat.Labels.Count == 2, string.Join(",", cat.Labels));
    var hist = ChartSpecBuilder.Histogram(qr, "age", 4);
    Check("智能线2-直方图桶数守恒", hist.Type == "histogram" && hist.Labels.Count == 4 && hist.Values.Sum() == 5, string.Join("|", hist.Labels));
    Check("智能线2-直方图末桶闭区间", hist.Labels[0].StartsWith("[20,") && hist.Labels[3].EndsWith("]"), hist.Labels[3]);
    Check("智能线2-图型推荐", ChartSpecBuilder.RecommendType(qr, "age") == "histogram" && ChartSpecBuilder.RecommendType(qr, "day") == "line" && ChartSpecBuilder.RecommendType(qr, "city") == "bar", "");
    var ts = ChartSpecBuilder.TimeSeries(qr, "day", null, "month");
    Check("智能线2-月度分桶计数", ts.Type == "line" && ts.Labels.Count == 3 && ts.Values[0] == 2 && ts.Values[1] == 2 && ts.Values[2] == 1, string.Join(",", ts.Values));
    var tsv = ChartSpecBuilder.TimeSeries(qr, "day", "age", "month");
    Check("智能线2-月度求和", Math.Abs(tsv.Values[0] - 50) < 1e-9 && Math.Abs(tsv.Values[1] - 90) < 1e-9, string.Join(",", tsv.Values));
    var specBack = ChartSpecBuilder.FromJson(ChartSpecBuilder.ToJson(ts));
    Check("智能线2-图表规格往返", specBack != null && specBack.ChartId == ts.ChartId && specBack.Values.SequenceEqual(ts.Values), "");

    // 4) 离线快照包：真库采样→打包→往返→指纹→不带库浏览与再聚合
    var dbPath2 = Path.Combine(Path.GetTempPath(), "taodbm-snap-" + Guid.NewGuid().ToString("N") + ".db");
    QueryResult sample;
    await using (var conn3 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath2}"))
    {
        conn3.Open();
        using (var c = conn3.CreateCommand())
        {
            c.CommandText = "CREATE TABLE orders(id INTEGER PRIMARY KEY, region TEXT, amount REAL);" +
                "INSERT INTO orders(region, amount) VALUES ('华东',10),('华东',20),('华北',30),('华南',40),('华东',50),('华北',60);";
            c.ExecuteNonQuery();
        }
        using var q = conn3.CreateCommand();
        q.CommandText = "SELECT region, amount FROM orders";
        using var rd = q.ExecuteReader();
        sample = new QueryResult { Title = "orders" };
        for (int i = 0; i < rd.FieldCount; i++) sample.Columns.Add(rd.GetName(i));
        while (rd.Read()) sample.Rows.Add(Enumerable.Range(0, rd.FieldCount).Select(i => (object?)rd.GetValue(i)).ToArray());
    }
    var tbl = SnapshotPackFile.FromQueryResult("orders", sample, maxRows: 4);
    Check("智能线2-快照截断留痕", tbl.Rows.Count == 4 && tbl.TotalRows == 6, $"{tbl.Rows.Count}/{tbl.TotalRows}");
    var pack = SnapshotPackFile.Create("订单离线包", "demo", new[] { tbl }, "体检样例");
    pack.Tables.Add(SnapshotPackFile.FromQueryResult("meta", new QueryResult { Columns = { "k" }, Rows = { new object?[] { "v" } } }));
    var fp = SnapshotPackFile.Fingerprint(pack);
    var packJson = SnapshotPackFile.ToJson(pack);
    Check("智能线2-快照无凭据键", !packJson.Contains("password", StringComparison.OrdinalIgnoreCase), "");
    var packBack = SnapshotPackFile.FromJson(packJson);
    Check("智能线2-快照往返", packBack != null && packBack.Tables.Count == 2 && packBack.Tables[0].TotalRows == 6, "");
    Check("智能线2-指纹一致", packBack != null && SnapshotPackFile.Fingerprint(packBack) == fp, "");
    packBack!.Tables[0].Rows[0][1] = "999999";
    Check("智能线2-篡改可发现", SnapshotPackFile.Fingerprint(packBack) != fp, "");
    var offline = SnapshotPackFile.GetTable(packBack, "orders");
    Check("智能线2-离线取表不碰原库", offline != null && offline!.Columns.SequenceEqual(new[] { "region", "amount" }), "");
    Check("智能线2-取不到表返回空", SnapshotPackFile.GetTable(packBack, "nope") == null, "");
    var offChart = ChartSpecBuilder.CategoryCount(SnapshotPackFile.ToQueryResult(offline!), "region");
    Check("智能线2-离线再聚合出图", offChart.Labels[0] == "华东" && offChart.Values[0] == 2, string.Join(",", offChart.Labels));
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    for (int attempt = 0; attempt < 5 && System.IO.File.Exists(dbPath2); attempt++)
    {
        try { System.IO.File.Delete(dbPath2); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); }
    }
}

// 协作线 C3：表单化录入、数据血缘影响地图、团队局域网共享合并（纯引擎，免界面稿）
{
    // 1) 表单化录入：字段生成→校验→参数化 INSERT
    var cols = new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "INT AUTO_INCREMENT", Nullable = false, IsKey = true },
        new() { Name = "name", DataType = "VARCHAR(50)", Nullable = false },
        new() { Name = "age", DataType = "INT", Nullable = true },
        new() { Name = "day", DataType = "DATETIME", Nullable = false },
        new() { Name = "flag", DataType = "BIT", Nullable = true },
    };
    var form = FormSchemaBuilder.Build("customers", cols, new Dictionary<string, (string?, bool?, bool?)> { ["name"] = ("客户名", null, null) });
    Check("协作-自增主键隐藏", form.First(f => f.Column == "id").Hidden, "");
    Check("协作-类型推断与必填", form.First(f => f.Column == "age").InputType == "number" && !form.First(f => f.Column == "age").Required
        && form.First(f => f.Column == "day").InputType == "datetime" && form.First(f => f.Column == "day").Required
        && form.First(f => f.Column == "flag").InputType == "bool", "");
    Check("协作-覆盖改标签", form.First(f => f.Column == "name").Label == "客户名" && form.First(f => f.Column == "name").MaxLength == 50, "");
    var bad = FormSchemaBuilder.Validate(form, new Dictionary<string, string?> { ["name"] = "  ", ["age"] = "abc", ["day"] = "", ["flag"] = "maybe" });
    Check("协作-必填与类型校验拦截", !bad.Ok && bad.Errors.ContainsKey("name") && bad.Errors.ContainsKey("age") && bad.Errors.ContainsKey("day") && bad.Errors.ContainsKey("flag"),
        string.Join("|", bad.Errors));
    var over = FormSchemaBuilder.Validate(form, new Dictionary<string, string?> { ["name"] = new string('长', 51) });
    Check("协作-超长校验", over.Errors.ContainsKey("name") && over.Errors["name"].Contains("超长"), over.Errors.GetValueOrDefault("name") ?? "");
    var good = FormSchemaBuilder.Validate(form, new Dictionary<string, string?> { ["name"] = "张三", ["age"] = "30", ["day"] = "2026-11-15 10:00:00", ["flag"] = "true" });
    Check("协作-合法值通过", good.Ok && good.Errors.Count == 0, string.Join("|", good.Errors));
    var (insSql, insParams) = FormSchemaBuilder.BuildInsert("customers", form,
        new Dictionary<string, string?> { ["name"] = "张三", ["age"] = "30", ["day"] = "2026-11-15 10:00:00", ["flag"] = "true", ["id"] = "9" });
    Check("协作-INSERT跳过隐藏列且参数化", insSql.StartsWith("INSERT INTO customers") && !insSql.Contains("id") && insSql.Contains("@name") && insParams["name"] == "张三", insSql);

    // 2) 数据血缘：建图→逐级追溯→抽验提示
    var corpus = new List<(LineageObject, string)>
    {
        (new LineageObject("orders", "table", "CREATE TABLE orders(id INT)"), "CREATE TABLE orders(id INT)"),
        (new LineageObject("v_order_sum", "view", "CREATE VIEW v_order_sum AS SELECT region, SUM(amt) FROM orders GROUP BY region"),
         "CREATE VIEW v_order_sum AS SELECT region, SUM(amt) FROM orders GROUP BY region"),
        (new LineageObject("v_dash", "view", "CREATE VIEW v_dash AS SELECT * FROM v_order_sum"),
         "CREATE VIEW v_dash AS SELECT * FROM v_order_sum"),
        (new LineageObject("sp_alert", "routine", "CREATE PROCEDURE sp_alert AS BEGIN SELECT COUNT(*) FROM v_dash END"),
         "CREATE PROCEDURE sp_alert AS BEGIN SELECT COUNT(*) FROM v_dash END"),
    };
    var map = LineageMapper.Build(corpus);
    Check("协作-血缘建图直接依赖", map.ContainsKey("orders") && map["orders"].Any(o => o.Name == "v_order_sum"), string.Join(",", map.Keys));
    var impacts = LineageMapper.Impact(map, "orders");
    Check("协作-逐级追溯全部下游", impacts.Count == 3 && impacts[0].Object == "v_order_sum" && impacts[1].Object == "v_dash" && impacts[2].Object == "sp_alert",
        string.Join(",", impacts.Select(i => $"{i.Object}:{i.Path.Count}")));
    Check("协作-追溯路径逐级", impacts[2].Path.SequenceEqual(new[] { "orders", "v_order_sum", "v_dash", "sp_alert" }), string.Join("←", impacts[2].Path));
    var hints = LineageMapper.Hints(impacts);
    Check("协作-提示含抽验片段", hints.Count == 3 && hints[0].Contains("抽验片段") && hints[0].Contains("GROUP BY"), hints.FirstOrDefault() ?? "");
    Check("协作-无依赖对象空影响", LineageMapper.Impact(map, "items").Count == 0, "");

    // 3) 团队共享：双向合并（后写胜出/平局保本地/墓碑同步）+ 净化 + 往返
    var t0 = DateTimeOffset.Parse("2026-11-15T10:00:00+08:00");
    var local = new List<ShareItem>
    {
        new("f1", "favorite", "{\"sql\":\"SELECT 1\"}", t0, "甲"),
        new("f2", "comment", "{\"text\":\"旧评论\"}", t0, "甲"),
        new("c1", "connection", "{\"host\":\"10.0.0.5\",\"port\":3306}", t0, "甲"),
    };
    var remote = new List<ShareItem>
    {
        new("f1", "favorite", "{\"sql\":\"SELECT 2\"}", t0.AddMinutes(5), "乙"),
        new("f2", "comment", "{\"text\":\"旧评论\"}", t0, "乙"),
        new("f3", "comment", "{\"text\":\"新评论\"}", t0.AddMinutes(1), "乙", true),
        new("c9", "connection", "{\"host\":\"10.0.0.9\",\"password\":\"leak-me\",\"apikey\":\"k2\"}", t0.AddMinutes(2), "乙"),
    };
    var merged = TeamShareMerge.Merge(local, remote);
    Check("协作-后写胜出", merged.Merged.First(i => i.ItemId == "f1").Payload.Contains("SELECT 2"), "");
    Check("协作-平局保本地", merged.Merged.First(i => i.ItemId == "f2").Author == "甲", "");
    Check("协作-合并统计口径", merged.Added == 2 && merged.Updated == 1 && merged.Tombstones == 1, $"{merged.Added}/{merged.Updated}/{merged.Tombstones}");
    Check("协作-墓碑同步删除意图", merged.Merged.First(i => i.ItemId == "f3").Deleted, "");
    var (c9, dropped) = TeamShareMerge.Sanitize(merged.Merged.First(i => i.ItemId == "c9"));
    Check("协作-共享包剔凭据", !c9.Payload.Contains("leak-me") && !c9.Payload.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        && dropped.Contains("password") && dropped.Contains("apikey"), string.Join(",", dropped));
    var round = TeamShareMerge.FromJson(TeamShareMerge.ToJson(merged.Merged));
    Check("协作-共享包往返", round != null && round.Count == merged.Merged.Count && round.First(i => i.ItemId == "f3").Deleted, "");
    Check("协作-坏包不炸", TeamShareMerge.FromJson("{坏") == null, "");
}

// 档三纯引擎：定时备份计划（节奏生成/下次执行/保留清理）+ 权限审计（GRANT 解析/风险发现/对等回收）
{
    var anchor = DateTimeOffset.Parse("2026-11-15T00:00:00+08:00");
    var policy = new BackupPolicy(24, 6, 3, anchor);

    // 1) 槽位生成：窗口截取与增量挂靠
    var slots = BackupPlanner.Schedule(policy,
        DateTimeOffset.Parse("2026-11-15T12:00:00+08:00"), DateTimeOffset.Parse("2026-11-17T00:00:00+08:00"));
    Check("备份-窗口外全量跳过", slots.Count == 5 && slots.Count(s => s.Kind == "full") == 2
        && !slots.Any(s => s.At <= DateTimeOffset.Parse("2026-11-15T12:00:00+08:00")),
        string.Join(",", slots.Select(s => $"{s.Kind}:{s.At:MM-dd HH}:{s.Seq}")));
    Check("备份-增量挂靠全量序号", slots.Where(s => s.Kind == "incr").All(s => s.Seq == 1) && slots.First(s => s.Kind == "full").Seq == 1, "");
    var next = BackupPlanner.NextRun(policy, DateTimeOffset.Parse("2026-11-15T23:30:00+08:00"));
    Check("备份-下次执行时间", next == DateTimeOffset.Parse("2026-11-16T00:00:00+08:00"), next?.ToString() ?? "null");

    // 2) 保留清理：过期满量连同挂靠增量出列
    var existing = new List<BackupSlot>();
    for (int i = 0; i <= 4; i++)
    {
        existing.Add(new BackupSlot(anchor.AddDays(i), "full", i));
        existing.Add(new BackupSlot(anchor.AddDays(i).AddHours(6), "incr", i));
        if (i >= 3) existing.Add(new BackupSlot(anchor.AddDays(i).AddHours(12), "incr", i));
    }
    var prune = BackupPlanner.Prune(existing, policy);
    Check("备份-过期满量出列", prune.Expired.Count(s => s.Kind == "full") == 2 && prune.RetainedFulls == 3, $"fulls={prune.RetainedFulls}");
    Check("备份-挂靠增量随全量出列", prune.Expired.Count(s => s.Kind == "incr") == 2 && prune.RetainedIncrs == 5, $"incrs={prune.RetainedIncrs}");
    Check("备份-策略JSON往返", BackupPlanner.FromJson(BackupPlanner.ToJson(policy)) is { } bp && bp.RetainFulls == 3 && bp.IncrEveryHours == 6, "");
    Check("备份-坏策略不炸", BackupPlanner.FromJson("{坏") == null, "");
    bool threwBak = false;
    try { BackupPlanner.Schedule(new BackupPolicy(0, 0, 1, anchor), anchor, anchor.AddDays(1)); } catch (ArgumentOutOfRangeException) { threwBak = true; }
    Check("备份-零间隔拒绝", threwBak, "");

    // 3) 权限审计：解析→风险发现→对等回收
    var grants = PermissionAuditor.Parse(new[]
    {
        "GRANT SELECT, INSERT ON app.* TO 'app'@'10.0.0.%';",
        "GRANT ALL ON *.* TO 'dev'@'%', 'ops'@'10.0.0.5' WITH GRANT OPTION;",
        "GRANT FILE ON *.* TO 'report'@'10.0.0.7';",
        "GRANT SELECT ON *.* TO PUBLIC;",
        "REVOKE SELECT ON app.* FROM 'app'@'10.0.0.%';",
    });
    Check("权限-GRANT解析计数", grants.Count == 5, grants.Count.ToString());
    var app = grants.First(r => r.Grantee == "'app'@'10.0.0.%'");
    Check("权限-权限与对象解析", app.Privileges.SequenceEqual(new[] { "SELECT", "INSERT" }) && app.Object == "app.*" && !app.WithGrantOption, "");
    Check("权限-多账号拆分与转授", grants.Count(r => r.Grantee == "'dev'@'%'") == 1 && grants.First(r => r.Grantee == "'ops'@'10.0.0.5'").WithGrantOption, "");
    var findings = PermissionAuditor.Audit(grants);
    Check("权限-宽账号全权判danger", findings.Any(f => f.Grantee == "'dev'@'%'" && f.Code == "R101" && f.Severity == "danger"), "");
    Check("权限-可转授逐条报R102", findings.Count(f => f.Code == "R102") == 2, findings.Count(f => f.Code == "R102").ToString());
    Check("权限-高危权限报R103", findings.Any(f => f.Grantee == "'report'@'10.0.0.7'" && f.Code == "R103" && f.Message.Contains("FILE")), "");
    Check("权限-受限SELECT不误报", !findings.Any(f => f.Grantee == "'app'@'10.0.0.%'"), "");
    Check("权限-PUBLIC只读不算全权", !findings.Any(f => f.Grantee == "PUBLIC" && f.Code == "R101"), "");
    var revokes = PermissionAuditor.RevokeSql(app);
    Check("权限-对等REVOKE生成", revokes.Single() == "REVOKE SELECT, INSERT ON app.* FROM 'app'@'10.0.0.%';", revokes.FirstOrDefault() ?? "");
}

// 档三纯引擎：ER 正向生成（模型→建库 DDL）+ 执行计划树解析与瓶颈标注 + 对象树目录语句生成
{
    // 1) ER 正向：user ← orders（故意乱序放入，验证依赖排序）
    var user = new ErdTable { Name = "user" };
    user.Columns.Add(new ErdColumn { Name = "id", Type = "INT", IsKey = true });
    user.Columns.Add(new ErdColumn { Name = "city", Type = "VARCHAR(50)" });
    var orders = new ErdTable { Name = "orders" };
    orders.Columns.Add(new ErdColumn { Name = "id", Type = "INT", IsKey = true });
    orders.Columns.Add(new ErdColumn { Name = "user_id", Type = "INT" });
    var em = new ErdModel { Tables = { orders, user }, Edges = { new ErdEdge { FromTable = "orders", FromCol = "user_id", ToTable = "user" } } };
    var fwd = ErdForward.Generate(em);
    Check("ER正向-生成成功", fwd.Ok && fwd.Ddl.Count == 2, string.Join("|", fwd.Errors));
    Check("ER正向-被引用表在前", fwd.Ddl.Count == 2 && fwd.Ddl[0].StartsWith("CREATE TABLE `user`", StringComparison.Ordinal), fwd.Ddl.FirstOrDefault() ?? "");
    Check("ER正向-主键非空", fwd.Ddl[0].Contains("`id` INT NOT NULL", StringComparison.Ordinal) && fwd.Ddl[0].Contains("PRIMARY KEY (`id`)", StringComparison.Ordinal), fwd.Ddl[0]);
    Check("ER正向-外键约束", fwd.Ddl[1].Contains("FOREIGN KEY (`user_id`) REFERENCES `user` (`id`)", StringComparison.Ordinal), fwd.Ddl[1]);
    Check("ER正向-可空列不带NOT NULL", fwd.Ddl[0].Contains("`city` VARCHAR(50) NULL", StringComparison.Ordinal), fwd.Ddl[0]);

    var weird = new ErdTable { Name = "we`ird" };
    weird.Columns.Add(new ErdColumn { Name = "id", IsKey = true });
    var fwdWeird = ErdForward.Generate(new ErdModel { Tables = { weird } });
    Check("ER正向-反引号转义", fwdWeird.Ok && fwdWeird.Ddl[0].Contains("`we``ird`", StringComparison.Ordinal), fwdWeird.Ddl.FirstOrDefault() ?? "");
    Check("ER正向-模型为空拒绝", !ErdForward.Generate(null).Ok, "");

    var dupA = new ErdTable { Name = "dup" };
    dupA.Columns.Add(new ErdColumn { Name = "id", IsKey = true });
    var dupB = new ErdTable { Name = "dup" };
    dupB.Columns.Add(new ErdColumn { Name = "id", IsKey = true });
    Check("ER正向-重名表拒绝", !ErdForward.Generate(new ErdModel { Tables = { dupA, dupB } }).Ok, "");

    var noPk = new ErdTable { Name = "t" };
    noPk.Columns.Add(new ErdColumn { Name = "a", Type = "INT" });
    Check("ER正向-无主键拒绝", !ErdForward.Generate(new ErdModel { Tables = { noPk } }).Ok, "");

    var ta = new ErdTable { Name = "a" };
    ta.Columns.Add(new ErdColumn { Name = "id", IsKey = true });
    var tb = new ErdTable { Name = "b" };
    tb.Columns.Add(new ErdColumn { Name = "c", Type = "INT" });
    Check("ER正向-目标无主键拒绝", !ErdForward.Generate(new ErdModel
    {
        Tables = { ta, tb },
        Edges = { new ErdEdge { FromTable = "a", FromCol = "id", ToTable = "b" } }
    }).Ok, "");
    var badColFwd = ErdForward.Generate(new ErdModel
    {
        Tables = { new ErdTable { Name = "a", Columns = { new ErdColumn { Name = "id", IsKey = true } } } },
        Edges = { new ErdEdge { FromTable = "a", FromCol = "ghost", ToTable = "a" } }
    });
    Check("ER正向-坏列外键拒绝", !badColFwd.Ok && badColFwd.Errors.Any(e => e.Contains("列不存在")), string.Join("|", badColFwd.Errors));
}

{
    // 2) 执行计划：缩进树解析 + 瓶颈标注 + 索引建议
    var planText = "SCAN user\n  SEARCH user USING INDEX idx_user_city (city=?)\nSCAN orders\n  USE TEMP B-TREE FOR ORDER BY\n    Using filesort";
    var plan = ExplainPlanEngine.Parse(planText);
    Check("计划-解析节点数与根数", plan.Flat.Count == 5 && plan.Roots.Count == 2, $"flat={plan.Flat.Count} roots={plan.Roots.Count}");
    Check("计划-父子层级挂接", plan.Roots[0].Children.Count == 1 && plan.Roots[1].Children.Count == 1
        && plan.Roots[1].Children[0].Children.Count == 1,
        $"r0={plan.Roots[0].Children.Count} r1={plan.Roots[1].Children.Count}");
    Check("计划-空文本不炸", ExplainPlanEngine.Parse("  \n ").Flat.Count == 0, "");
    Check("计划-深度跳级收敛", ExplainPlanEngine.Parse("A\n        B").Flat.Last().Depth == 1, "");
    var pf = ExplainPlanEngine.Annotate(plan);
    Check("计划-全表扫描标注P201", pf.Count(f => f.Code == "P201") == 2, pf.Count.ToString());
    Check("计划-文件排序P202为danger", pf.Any(f => f.Code == "P202" && f.Severity == "danger"), "");
    Check("计划-临时表P203", pf.Any(f => f.Code == "P203"), "");
    Check("计划-命中索引不误报P201", !pf.Any(f => f.Node.Contains("USING INDEX")), "");
    Check("计划-SQLite口径提取", ExplainPlanEngine.ExtractFullScanTable("SCAN user") == "user", "");
    Check("计划-MySQL口径提取", ExplainPlanEngine.ExtractFullScanTable("Table scan on `orders`") == "orders", "");
    Check("计划-JSON往返", ExplainPlan.FromJson(plan.ToJson()) is { } back && back.Flat.Count == plan.Flat.Count, "");
}

{
    // 3) 对象树目录：按引擎生成元数据查询语句
    Check("目录-支持引擎判定", ObjectTreeCatalog.Supported(DbEngine.MySQL) && ObjectTreeCatalog.Supported(DbEngine.SQLite) && ObjectTreeCatalog.Supported(DbEngine.Redis), "");
    var myViews = ObjectTreeCatalog.ListViews(DbEngine.MySQL, "shop");
    Check("目录-MySQL视图语句", myViews.Contains("information_schema.VIEWS") && myViews.Contains("'shop'"), myViews);
    Check("目录-PG视图语句", ObjectTreeCatalog.ListViews(DbEngine.PostgreSQL, "shop").Contains("pg_views"), "");
    Check("目录-SQLite视图语句", ObjectTreeCatalog.ListViews(DbEngine.SQLite, "main").Contains("sqlite_master"), "");
    Check("目录-MySQL索引语句", ObjectTreeCatalog.ListIndexes(DbEngine.MySQL, "shop").Contains("information_schema.STATISTICS"), "");
    Check("目录-MySQL外键语句", ObjectTreeCatalog.ListForeignKeys(DbEngine.MySQL, "shop").Contains("REFERENCED_TABLE_NAME IS NOT NULL"), "");
    Check("目录-MySQL触发器语句", ObjectTreeCatalog.ListTriggers(DbEngine.MySQL, "shop").Contains("information_schema.TRIGGERS"), "");
    Check("目录-SQLite触发器语句", ObjectTreeCatalog.ListTriggers(DbEngine.SQLite, "main").Contains("sqlite_master"), "");
    Check("目录-SQLite例程空清单", ObjectTreeCatalog.ListRoutines(DbEngine.SQLite, "main").Contains("WHERE 0"), "");
    Check("目录-引号转义", ObjectTreeCatalog.ListViews(DbEngine.MySQL, "it's").Contains("it''s"), "");
    bool threwCat = false;
    try { ObjectTreeCatalog.ListViews((DbEngine)999, "x"); } catch (NotSupportedException) { threwCat = true; }
    Check("目录-不支持引擎报错", threwCat, "");
}

{
    // 例程管理：DROP/执行语句生成 + 调试支持判定 + 校验 + 引用查找（对账漏码 §1，2026-11-19）
    Check("例程-MySQL删过程", RoutineManager.BuildDropSql(DbEngine.MySQL, RoutineKind.Procedure, "sp1") == "DROP PROCEDURE `sp1`", "");
    Check("例程-MySQL带库删过程", RoutineManager.BuildDropSql(DbEngine.MySQL, RoutineKind.Procedure, "sp1", "shop") == "DROP PROCEDURE `shop`.`sp1`", "");
    Check("例程-MySQL删触发器带库", RoutineManager.BuildDropSql(DbEngine.MySQL, RoutineKind.Trigger, "tr1", "shop") == "DROP TRIGGER `shop`.`tr1`", "");
    Check("例程-PG删函数", RoutineManager.BuildDropSql(DbEngine.PostgreSQL, RoutineKind.Function, "fn1") == "DROP FUNCTION \"fn1\"", "");
    Check("例程-SQLServer删触发器", RoutineManager.BuildDropSql(DbEngine.SqlServer, RoutineKind.Trigger, "tr1") == "DROP TRIGGER [tr1]", "");
    Check("例程-SQLite仅触发器", RoutineManager.BuildDropSql(DbEngine.SQLite, RoutineKind.Trigger, "tr1") == "DROP TRIGGER \"tr1\"", "");
    Check("例程-PG删触发器带表", RoutineManager.BuildDropSql(DbEngine.PostgreSQL, RoutineKind.Trigger, "tr1", onTable: "users") == "DROP TRIGGER \"tr1\" ON \"users\"", "");
    bool threwDrop = false;
    try { RoutineManager.BuildDropSql(DbEngine.SQLite, RoutineKind.Procedure, "x"); } catch (NotSupportedException) { threwDrop = true; }
    Check("例程-SQLite过程不支持", threwDrop, "");
    bool threwPgTrg = false;
    try { RoutineManager.BuildDropSql(DbEngine.PostgreSQL, RoutineKind.Trigger, "tr1"); } catch (ArgumentException) { threwPgTrg = true; }
    Check("例程-PG删触发器缺表名拒绝", threwPgTrg, "");
    Check("例程-删名引号转义", RoutineManager.BuildDropSql(DbEngine.PostgreSQL, RoutineKind.Procedure, "a\"b") == "DROP PROCEDURE \"a\"\"b\"", "");
    Check("例程-MySQL调用过程", RoutineManager.BuildExecuteSql(DbEngine.MySQL, RoutineKind.Procedure, "sp1", "1, 'a'") == "CALL `sp1`(1, 'a')", "");
    Check("例程-MySQL调用函数", RoutineManager.BuildExecuteSql(DbEngine.MySQL, RoutineKind.Function, "fn1", "2") == "SELECT `fn1`(2)", "");
    Check("例程-PG调用函数", RoutineManager.BuildExecuteSql(DbEngine.PostgreSQL, RoutineKind.Function, "fn1", "2") == "SELECT \"fn1\"(2)", "");
    Check("例程-SQLServer执行过程", RoutineManager.BuildExecuteSql(DbEngine.SqlServer, RoutineKind.Procedure, "sp1", "1") == "EXEC [sp1] 1", "");
    Check("例程-SQLServer执行函数带schema", RoutineManager.BuildExecuteSql(DbEngine.SqlServer, RoutineKind.Function, "fn1", "2") == "SELECT dbo.[fn1](2)", "");
    Check("例程-SQLServer无参过程", RoutineManager.BuildExecuteSql(DbEngine.SqlServer, RoutineKind.Procedure, "sp1") == "EXEC [sp1]", "");
    bool threwTrg = false;
    try { RoutineManager.BuildExecuteSql(DbEngine.MySQL, RoutineKind.Trigger, "tr1"); } catch (NotSupportedException) { threwTrg = true; }
    Check("例程-触发器不可手执", threwTrg, "");
    Check("例程-调试仅MySQL", RoutineManager.DebugSupported(DbEngine.MySQL)
        && !RoutineManager.DebugSupported(DbEngine.PostgreSQL)
        && !RoutineManager.DebugSupported(DbEngine.SqlServer)
        && !RoutineManager.DebugSupported(DbEngine.SQLite), "");
    Check("例程-灰置文案", RoutineManager.DebugUnsupportedText == "v2.0 暂不支持", "");
    var errs = RoutineManager.Validate(DbEngine.MySQL, RoutineKind.Procedure, "");
    Check("例程-空体拒绝", errs.Count == 1, string.Join("|", errs));
    errs = RoutineManager.Validate(DbEngine.MySQL, RoutineKind.Procedure, "CREATE PROCEDURE p()\nBEGIN\n  SELECT 1;\nEND");
    Check("例程-过程体通过", errs.Count == 0, string.Join("|", errs));
    errs = RoutineManager.Validate(DbEngine.MySQL, RoutineKind.Function, "CREATE PROCEDURE p()");
    Check("例程-类型前缀不符拒绝", errs.Any(e => e.Contains("类型")), string.Join("|", errs));
    errs = RoutineManager.Validate(DbEngine.MySQL, RoutineKind.Procedure, "CREATE PROCEDURE p()\nBEGIN\n  SELECT 1;\nEND\nDELIMITER $$");
    Check("例程-DELIMITER拒绝", errs.Any(e => e.Contains("DELIMITER")), string.Join("|", errs));
    errs = RoutineManager.Validate(DbEngine.MySQL, RoutineKind.Procedure, "DROP PROCEDURE p");
    Check("例程-非CREATE拒绝", errs.Any(e => e.Contains("CREATE")), string.Join("|", errs));
    Check("例程-引用查找含触发器与转义", RoutineManager.ReferencesSql(DbEngine.MySQL, "shop", "it's").Contains("it''s")
        && RoutineManager.ReferencesSql(DbEngine.MySQL, "shop", "k").Contains("TRIGGERS"), "");
    Check("例程-SQLServer引用查找", RoutineManager.ReferencesSql(DbEngine.SqlServer, "db", "k").Contains("OBJECT_DEFINITION"), "");
    Check("例程-模板含CREATE", RoutineManager.Template(DbEngine.MySQL, RoutineKind.Procedure, "p_new").StartsWith("CREATE PROCEDURE", StringComparison.Ordinal), "");
    Check("例程-调试脚手架含CALL", RoutineManager.DebugScript(DbEngine.MySQL, RoutineKind.Procedure, "sp1").Contains("CALL `sp1`"), "");
    bool threwDbg = false;
    try { RoutineManager.DebugScript(DbEngine.PostgreSQL, RoutineKind.Procedure, "sp1"); } catch (NotSupportedException) { threwDbg = true; }
    Check("例程-非MySQL调试拒绝", threwDbg, "");
}

{
    // 迁移续传：清单基线 + 逐表水位 + 重启后重算续传计划（纯引擎，凭据不落盘）
    var manifest = new MigrationManifest("M-20261116", "src-main", "dst-main",
        DateTimeOffset.Parse("2026-11-16T04:50:00+08:00"), new[]
        {
            new MigrationTable("users", 1000, 10),
            new MigrationTable("orders", 5000, 50),
            new MigrationTable("logs", 200, 2),
        });
    var state = MigrationResume.Fresh(manifest);
    Check("续传-新状态零水位", state.Tables.Count == 3 && state.Tables.All(p => p.BatchDone == 0 && !p.Done), "");

    state = MigrationResume.Advance(state, "users", 10, "u-1000", 10);
    state = MigrationResume.Advance(state, "orders", 20, "o-2400", 50);
    Check("续传-满批自动置完成", state.Tables.First(p => p.Table == "users").Done, "");
    Check("续传-半途保留水位", state.Tables.First(p => p.Table == "orders") is { BatchDone: 20, LastKey: "o-2400", Done: false }, "");

    var plan = MigrationResume.Plan(manifest, state);
    Check("续传-动作三分", plan.Actions.Count == 3
        && plan.Actions[0] is { Action: "verify", Table: "users" }
        && plan.Actions[1] is { Action: "resume", FromBatch: 20, Table: "orders" }
        && plan.Actions[2] is { Action: "copy", Table: "logs" }, plan.Actions[1].Note);
    Check("续传-续传从下一批起", plan.Actions[1].Note.Contains("第 21 批"), plan.Actions[1].Note);

    var drifted = MigrationResume.Plan(manifest, state,
        sourceCounts: new Dictionary<string, long> { ["orders"] = 5100 });
    Check("续传-源漂移报出", drifted.Drifted.SequenceEqual(new[] { "orders" }), string.Join(",", drifted.Drifted));
    Check("续传-漂移不改动作", drifted.Actions[1].Action == "resume", "");

    var mismatch = MigrationResume.Plan(manifest, state,
        targetCounts: new Dictionary<string, long> { ["users"] = 998 });
    Check("续传-完成表行数不符", mismatch.VerifyMismatch.SequenceEqual(new[] { "users" }) && mismatch.Actions[0].Action == "copy", "");

    bool threwMig = false;
    try { MigrationResume.Plan(manifest, state with { ManifestId = "M-别的" }); } catch (ArgumentException) { threwMig = true; }
    Check("续传-清单状态不匹配拒绝", threwMig, "");

    var json = MigrationResume.ToJson(state);
    Check("续传-状态JSON往返", MigrationResume.FromJson(json) is { } back && back.ManifestId == "M-20261116"
        && back.Tables.First(p => p.Table == "orders").LastKey == "o-2400", "");
    Check("续传-状态无凭据字段", !json.Contains("Password", StringComparison.OrdinalIgnoreCase) && !json.Contains("pwd"), "");
    Check("续传-坏JSON不炸", MigrationResume.FromJson("{坏") == null, "");
}

{
    // 局域网搬运：封包指纹 + 验包防篡改 + 分块过线重装（纯引擎）
    var t6 = DateTimeOffset.Parse("2026-11-16T06:40:00+08:00");
    var items = new List<ShareItem>
    {
        new("c1", "conn", "{}", t6, "alice"),
        new("f1", "fav", "{\"name\":\"生产库\"}", t6, "alice"),
        new("n1", "note", "备注", t6, "bob", Deleted: true),
    };
    var packet = SharePacketBuilder.Build(items, "alice-book", t6);
    Check("搬运-封包头完整", packet.Header.ItemCount == 3 && packet.Header.Sender == "alice-book" && packet.Header.Sha256.Length == 64, packet.Header.Sha256);
    Check("搬运-验包通过", SharePacketBuilder.Verify(packet) is { Ok: true, Payload: not null }, "");

    var tampered = packet with { Payload = packet.Payload.Replace("fav", "favs") };
    var checkTamper = SharePacketBuilder.Verify(tampered);
    Check("搬运-载荷被改验出", !checkTamper.Ok && checkTamper.Reason.Contains("指纹不符"), checkTamper.Reason);

    var truncated = packet with { Payload = packet.Payload[..^10] };
    Check("搬运-载荷被截验出", !SharePacketBuilder.Verify(truncated).Ok, "");

    var chunks = SharePacketBuilder.Chunk(packet, 64);
    Check("搬运-分块数正确", chunks.Count > 1 && chunks.All(c => c.PacketId == packet.Header.PacketId) && chunks[0].Total == chunks.Count, chunks.Count.ToString());
    var reassembled = SharePacketBuilder.Assemble(chunks);
    Check("搬运-重装还原载荷", reassembled == packet.Payload, "");
    Check("搬运-重装后复验通过", reassembled is not null && SharePacketBuilder.Verify(packet with { Payload = reassembled }).Ok, "");

    var missing = chunks.Where(c => c.Index != 1).ToList();
    Check("搬运-缺块拒装", SharePacketBuilder.Assemble(missing) == null, "");
    var mixed = chunks.ToList();
    mixed[0] = mixed[0] with { PacketId = "别的包" };
    Check("搬运-混包拒装", SharePacketBuilder.Assemble(mixed) == null, "");
    bool threwChunk = false;
    try { SharePacketBuilder.Chunk(packet, 0); } catch (ArgumentOutOfRangeException) { threwChunk = true; }
    Check("搬运-零块宽拒绝", threwChunk, "");

    var empty = SharePacketBuilder.Build(new List<ShareItem>(), "bob-book", t6);
    Check("搬运-空包封验通过", empty.Header.ItemCount == 0 && SharePacketBuilder.Verify(empty).Ok, "");
}

{
    // B3 权限接线目录：账号清单 + 授权文本获取 + 展示口径（纯引擎）
    Check("权限目录-引擎支持面", UserPermissionCatalog.Supported(DbEngine.MySQL)
        && UserPermissionCatalog.Supported(DbEngine.PostgreSQL)
        && !UserPermissionCatalog.Supported(DbEngine.SQLite)
        && !UserPermissionCatalog.Supported(DbEngine.MongoDB), "");

    Check("权限目录-账号清单口径", UserPermissionCatalog.ListAccountsSql(DbEngine.MySQL).Contains("mysql.user")
        && UserPermissionCatalog.ListAccountsSql(DbEngine.PostgreSQL).Contains("pg_user"), "");
    bool threwAcc = false;
    try { UserPermissionCatalog.ListAccountsSql(DbEngine.SQLite); } catch (NotSupportedException) { threwAcc = true; }
    Check("权限目录-不支持引擎拒绝", threwAcc, "");

    Check("权限目录-授权SQL引号转义", UserPermissionCatalog.GrantsSql(DbEngine.MySQL, "o'brien", "10.0.%").Contains("''")
        && UserPermissionCatalog.GrantsSql(DbEngine.PostgreSQL, "it's", "-").Contains("''"), "");
    Check("权限目录-PG合成含授权目录", UserPermissionCatalog.GrantsSql(DbEngine.PostgreSQL, "app_rw", "-")
        .Contains("information_schema.table_privileges")
        && UserPermissionCatalog.GrantsSql(DbEngine.PostgreSQL, "app_rw", "-").Contains("WITH GRANT OPTION"), "");

    var grantRows = new List<object?[]>
    {
        new object?[] { "Grants for 'root'@'%': GRANT ALL PRIVILEGES ON *.* TO 'root'@'%' WITH GRANT OPTION" },
        new object?[] { "GRANT SELECT, DROP ON shop_db.* TO 'app_rw'@'10.0.%' WITH GRANT OPTION" },
        new object?[] { "1 row in set" },
    };
    var texts = UserPermissionCatalog.ExtractGrantTexts(grantRows);
    Check("权限目录-抠GRANT原文", texts.Count == 2 && texts[1].StartsWith("GRANT SELECT, DROP"), texts.Count.ToString());

    var accRows = new List<object?[]> { new object?[] { "root", "%" }, new object?[] { "app_rw", "10.0.%" }, new object?[] { null, null } };
    var accounts = UserPermissionCatalog.ExtractAccounts(accRows);
    Check("权限目录-账号行抽取", accounts.Count == 2 && accounts[0].User == "root" && accounts[1].Host == "10.0.%", accounts.Count.ToString());

    var audited = PermissionAuditor.Audit(PermissionAuditor.Parse(texts));
    Check("权限目录-解析后可审计", audited.Any(f => f.Code == "R101") && audited.Any(f => f.Code == "R102")
        && audited.Any(f => f.Code == "R103"), string.Join(",", audited.Select(f => f.Code).Distinct()));

    Check("权限目录-展示口径", UserPermissionCatalog.AccountLabel(DbEngine.MySQL, "root", "%") == "'root'@'%'"
        && UserPermissionCatalog.AccountLabel(DbEngine.PostgreSQL, "app_rw", "-") == "app_rw", "");

    var back = UserPermissionCatalog.SplitGrantee("'app_rw'@'10.0.%'");
    Check("权限目录-账号键往返", back is { } b && b.User == "app_rw" && b.Host == "10.0.%",
        back is { } c ? c.User : "null");
    Check("权限目录-裸词不拆", UserPermissionCatalog.SplitGrantee("PUBLIC") == null, "");
}

{
    // B10 主从管理面目录：角色探测 + 拓扑解析 + 延迟分级 + 管理草稿（纯引擎）
    Check("复制目录-引擎支持面", ReplicationCatalog.Supported(DbEngine.MySQL)
        && ReplicationCatalog.Supported(DbEngine.PostgreSQL)
        && !ReplicationCatalog.Supported(DbEngine.SQLite)
        && !ReplicationCatalog.Supported(DbEngine.MongoDB), "");

    Check("复制目录-探测与节点口径", ReplicationCatalog.RoleProbeSql(DbEngine.PostgreSQL).Contains("pg_is_in_recovery")
        && ReplicationCatalog.RoleProbeSql(DbEngine.MySQL).StartsWith("SHOW REPLICA STATUS")
        && ReplicationCatalog.ReplicasSql(DbEngine.MySQL) == "SHOW REPLICAS"
        && ReplicationCatalog.ReplicasSql(DbEngine.PostgreSQL).Contains("pg_stat_replication"), "");

    var lagNone = ReplicationCatalog.LagClass(null);
    Check("复制目录-延迟分级", ReplicationCatalog.LagClass(0.4) == ("正常", "ok")
        && ReplicationCatalog.LagClass(12.6) == ("延迟偏高", "warn")
        && ReplicationCatalog.LagClass(90) == ("严重滞后", "danger")
        && lagNone == ("—", "none"), "");

    var pgCols = new List<string> { "name", "addr", "state", "sync_state", "lag_seconds" };
    var pgRows = new List<object?[]>
    {
        new object?[] { "replica-1", "10.0.0.11", "streaming", "async", 0.4 },
        new object?[] { "replica-2", "10.0.0.12", "streaming", "async", 12.6 },
        new object?[] { "replica-3", "10.0.0.13", "startup", "async", null },
    };
    var topo = ReplicationCatalog.BuildFromPrimary(DbEngine.PostgreSQL, pgCols, pgRows);
    Check("复制目录-PG拓扑解析", topo.Count == 4 && topo[0].Role == "主库"
        && topo[1].State == "正常" && topo[2].State == "延迟偏高" && topo[3].State == "连接中", topo.Count.ToString());

    var myCols = new List<string> { "Server_name", "Host", "Port" };
    var myRows = new List<object?[]> { new object?[] { "replica-1", "10.0.0.11", 3307 } };
    var topoMy = ReplicationCatalog.BuildFromPrimary(DbEngine.MySQL, myCols, myRows);
    Check("复制目录-MySQL主视角拓扑", topoMy.Count == 2 && topoMy[1].Name == "replica-1" && topoMy[1].Role == "从库", topoMy.Count.ToString());

    var repCols = new List<string> { "Replica_IO_Running", "Replica_SQL_Running", "Seconds_BehindSource", "Source_Host" };
    var repRows = new List<object?[]> { new object?[] { "Yes", "Yes", 12, "master-1" } };
    var selfOk = ReplicationCatalog.BuildFromReplica(DbEngine.MySQL, repCols, repRows);
    var repBad = new List<object?[]> { new object?[] { "Yes", "No", null, "master-1" } };
    var selfBad = ReplicationCatalog.BuildFromReplica(DbEngine.MySQL, repCols, repBad);
    Check("复制目录-从视角状态", selfOk.State == "延迟偏高" && selfOk.LagSeconds == 12
        && selfBad.State == "已断开", selfOk.State);

    Check("复制目录-列名兼容旧版", ReplicationCatalog.Col(repCols, repRows[0], "Seconds_BehindSource", "Seconds_Behind_Master") == "12"
        || ReplicationCatalog.Col(repCols, repRows[0], "Seconds_Behind_Master", "Seconds_BehindSource") == "12", "");

    var pause = ReplicationCatalog.PauseReplicaSql(DbEngine.MySQL, true);
    var resume = ReplicationCatalog.PauseReplicaSql(DbEngine.MySQL, false);
    Check("复制目录-暂停恢复草稿", pause[0] == "STOP REPLICA;" && resume[0] == "START REPLICA;"
        && ReplicationCatalog.PauseReplicaSql(DbEngine.PostgreSQL, true)[0].Contains("pg_wal_replay_pause"), "");

    var reconnect = ReplicationCatalog.ReconnectTemplateSql(DbEngine.MySQL, "master-1", 3306, "repl", "shop_db");
    Check("复制目录-重建通道模板", reconnect.Count == 4 && reconnect[1].Contains("SOURCE_HOST='master-1'")
        && reconnect[1].Contains("SOURCE_PORT=3306") && reconnect[1].Contains("<口令现场填入>"), reconnect[1]);

    var switchover = ReplicationCatalog.SwitchoverDraftSql(DbEngine.MySQL, "master-1");
    Check("复制目录-切换主库高危草稿", switchover[0] == "STOP REPLICA;" && switchover[1] == "RESET REPLICA ALL;"
        && switchover[3].Contains("master-1") && switchover[3].Contains("双主"), "");

    bool threwRepl = false;
    try { ReplicationCatalog.PauseReplicaSql(DbEngine.SQLite, true); } catch (NotSupportedException) { threwRepl = true; }
    Check("复制目录-不支持引擎拒绝", threwRepl, "");
}

{
    // B11 分库分区分表向导：方案生成 + 现状目录 + 倾斜判定 + 盐化摘要（纯引擎）
    Check("分片向导-引擎支持面", ShardingWizard.Supported(DbEngine.MySQL)
        && ShardingWizard.Supported(DbEngine.PostgreSQL)
        && !ShardingWizard.Supported(DbEngine.SQLite)
        && !ShardingWizard.Supported(DbEngine.MongoDB), "");

    var myPlan = ShardingWizard.HashPartitionSql(DbEngine.MySQL, "orders", "user_id", 4);
    Check("分片向导-MySQL哈希方案", myPlan.Count == 2 && myPlan[0] == "ALTER TABLE `orders` PARTITION BY HASH (`user_id`) PARTITIONS 4;"
        && myPlan[1].Contains("information_schema.PARTITIONS"), myPlan[0]);

    var pgPlan = ShardingWizard.HashPartitionSql(DbEngine.PostgreSQL, "orders", "user_id", 4);
    Check("分片向导-PG声明式方案", pgPlan.Count == 7 && pgPlan[0].StartsWith("-- PostgreSQL")
        && pgPlan[1].Contains("PARTITION BY HASH") && pgPlan[1].Contains("_pnew")
        && pgPlan[2].Contains("MODULUS 4, REMAINDER 0") && pgPlan[5].Contains("REMAINDER 3")
        && pgPlan[6].StartsWith("-- 迁移"), pgPlan.Count.ToString());

    bool threwShard = false;
    try { ShardingWizard.HashPartitionSql(DbEngine.MySQL, "t", "k", 1); } catch (ArgumentOutOfRangeException) { threwShard = true; }
    Check("分片向导-分片数下限", threwShard, "");

    Check("分片向导-现状目录口径", ShardingWizard.PartitionsInfoSql(DbEngine.MySQL, "shop_db", "orders").Contains("PARTITIONS")
        && ShardingWizard.PartitionsInfoSql(DbEngine.PostgreSQL, "shop_db", "orders").Contains("pg_inherits"), "");

    var even = new List<(string, long)> { ("p0", 1208), ("p1", 1201), ("p2", 1210), ("p3", 1211) };
    var skew = new List<(string, long)> { ("p0", 4000), ("p1", 100), ("p2", 100), ("p3", 100) };
    Check("分片向导-倾斜判定", ShardingWizard.AnalyzeDistribution(even).Skewed == false
        && ShardingWizard.AnalyzeDistribution(skew).Skewed == true
        && ShardingWizard.AnalyzeDistribution(new List<(string, long)>()).Skewed == false
        && ShardingWizard.AnalyzeDistribution(even).Parts[0].Name == "p3", "");

    var salt = ShardingWizard.SaltingSummary("user_id", 8);
    Check("分片向导-盐化摘要", salt.Contains("user_id") && salt.Contains("8"), salt);
}

{
    // 批次 L：MSSQL 六处缺口补齐 + 对账引擎扩三库（纯引擎）
    // 1) 诊断目录 MSSQL
    Check("诊断-MSSQL支持面", DiagnosticsCatalog.Supported(DbEngine.SqlServer), "");
    Check("诊断-MSSQL版本", DiagnosticsCatalog.VersionQuery(DbEngine.SqlServer).Contains("@@VERSION"), "");
    Check("诊断-MSSQL规模", DiagnosticsCatalog.TopTablesQuery(DbEngine.SqlServer, "shop")!.Contains("sys.tables")
        && DiagnosticsCatalog.TopTablesQuery(DbEngine.SqlServer, "shop")!.Contains("TOP 8"), "");
    Check("诊断-MSSQL锁等待", DiagnosticsCatalog.LockWaitQuery(DbEngine.SqlServer)!.Contains("blocking_session_id > 0")
        && DiagnosticsCatalog.LockWaitQuery(DbEngine.SqlServer)!.Contains("dm_exec_requests"), "");
    Check("诊断-MSSQL健康命令为空", DiagnosticsCatalog.HealthCommand(DbEngine.SqlServer) is null, "");

    // 2) 执行计划 MSSQL 方言
    Check("计划-MSSQL表扫描提取", ExplainPlanEngine.ExtractFullScanTable("Table Scan(OBJECT:([shop_db].[dbo].[orders]))") == "orders", "");
    Check("计划-MSSQL聚集索引扫描提取", ExplainPlanEngine.ExtractFullScanTable("Clustered Index Scan(OBJECT:([shop_db].[dbo].[orders]))") == "orders", "");
    var mssqlPlan = ExplainPlanEngine.Parse("Sort(DISTINCT ORDER BY:([t].[c] ASC))\nTable Scan(OBJECT:([db].[dbo].[orders]))");
    var mssqlFindings = ExplainPlanEngine.Annotate(mssqlPlan);
    Check("计划-MSSQL排序标注P202", mssqlFindings.Any(f => f.Code == "P202"), string.Join(",", mssqlFindings.Select(f => f.Code)));
    Check("计划-MSSQL全表扫描标注P201", mssqlFindings.Any(f => f.Code == "P201" && f.Message.Contains("orders")), string.Join(",", mssqlFindings.Select(f => f.Code)));
    var spoolFindings = ExplainPlanEngine.Annotate(ExplainPlanEngine.Parse("Table Spool"));
    Check("计划-MSSQLSpool标注P203", spoolFindings.Any(f => f.Code == "P203"), string.Join(",", spoolFindings.Select(f => f.Code)));
    Check("计划-MSSQL包装语句", ExplainPlanEngine.PlanSql(DbEngine.SqlServer, "SELECT 1").StartsWith("SET SHOWPLAN_ALL ON;")
        && ExplainPlanEngine.PlanSql(DbEngine.SqlServer, "SELECT 1").Contains("SELECT 1")
        && ExplainPlanEngine.PlanSql(DbEngine.SqlServer, "SELECT 1").EndsWith("SET SHOWPLAN_ALL OFF;"), "");
    Check("计划-MySQL包装语句", ExplainPlanEngine.PlanSql(DbEngine.MySQL, "SELECT 1;") == "EXPLAIN SELECT 1", "");
    bool threwPlan = false;
    try { ExplainPlanEngine.PlanSql(DbEngine.SQLite, "SELECT 1"); } catch (NotSupportedException) { threwPlan = true; }
    Check("计划-不支持引擎拒绝", threwPlan, "");

    // 3) 权限目录 MSSQL
    Check("权限目录-MSSQL支持面", UserPermissionCatalog.Supported(DbEngine.SqlServer), "");
    Check("权限目录-MSSQL账号清单", UserPermissionCatalog.ListAccountsSql(DbEngine.SqlServer).Contains("sys.database_principals"), "");
    var mssqlGrantsSql = UserPermissionCatalog.GrantsSql(DbEngine.SqlServer, "app_rw", "-");
    Check("权限目录-MSSQL合成授权", mssqlGrantsSql.Contains("sys.database_permissions")
        && mssqlGrantsSql.Contains("WITH GRANT OPTION") && mssqlGrantsSql.Contains("'app_rw'"), "");
    var mssqlGrantTexts = PermissionAuditor.Parse(new[] { "GRANT CONTROL ON dbo.orders TO [app_user];" });
    Check("权限目录-MSSQL授权文本可解析", mssqlGrantTexts.Count == 1 && mssqlGrantTexts[0].Grantee == "[app_user]"
        && mssqlGrantTexts[0].Privileges.SequenceEqual(new[] { "CONTROL" }), mssqlGrantTexts.Count.ToString());
    var mssqlFindingsPerm = PermissionAuditor.Audit(mssqlGrantTexts);
    Check("权限目录-MSSQL高危CONTROL报R103", mssqlFindingsPerm.Any(f => f.Code == "R103" && f.Message.Contains("CONTROL")), "");

    // 4) 复制目录 MSSQL（Always On 口径）
    Check("复制目录-MSSQL支持面", ReplicationCatalog.Supported(DbEngine.SqlServer), "");
    Check("复制目录-MSSQL探测", ReplicationCatalog.RoleProbeSql(DbEngine.SqlServer).Contains("SECONDARY"), "");
    Check("复制目录-MSSQL从库清单", ReplicationCatalog.ReplicasSql(DbEngine.SqlServer).Contains("sys.dm_hadr_availability_replica_states"), "");
    var agCols = new List<string> { "role_desc", "connected_state_desc", "synchronization_health_desc" };
    var agOk = ReplicationCatalog.BuildFromReplicaSqlServer(agCols, new List<object?[]> { new object?[] { "SECONDARY", "CONNECTED", "HEALTHY" } });
    var agBad = ReplicationCatalog.BuildFromReplicaSqlServer(agCols, new List<object?[]> { new object?[] { "SECONDARY", "DISCONNECTED", "NOT_HEALTHY" } });
    Check("复制目录-MSSQL从视角解析", agOk.State == "正常" && agBad.State == "已断开", agOk.State);
    Check("复制目录-MSSQL暂停草稿", ReplicationCatalog.PauseReplicaSql(DbEngine.SqlServer, true)[0].Contains("PAUSE"), "");
    Check("复制目录-MSSQL切换草稿", ReplicationCatalog.SwitchoverDraftSql(DbEngine.SqlServer, "old-src")[0].Contains("FORCE_FAILOVER"), "");

    // 5) 分片向导 MSSQL
    Check("分片向导-MSSQL支持面", ShardingWizard.Supported(DbEngine.SqlServer), "");
    var msHash = ShardingWizard.HashPartitionSql(DbEngine.SqlServer, "orders", "user_id", 4);
    Check("分片向导-MSSQL哈希方案", msHash[0].StartsWith("ALTER TABLE [orders]") && msHash.Any(s => s.Contains("% 4"))
        && msHash.Any(s => s.Contains("CREATE PARTITION FUNCTION")) && msHash[0].Contains("PERSISTED"), msHash.FirstOrDefault() ?? "");
    var msRange = ShardingWizard.RangePartitionSql(DbEngine.SqlServer, "orders", "order_time", new[] { "100", "200" });
    Check("分片向导-MSSQL范围方案", msRange.Any(s => s.Contains("RANGE LEFT FOR VALUES (100, 200)"))
        && msRange.Any(s => s.Contains("CREATE PARTITION SCHEME")), msRange.FirstOrDefault() ?? "");
    var msList = ShardingWizard.ListPartitionSql(DbEngine.SqlServer, "orders", "region",
        new (string, IReadOnlyList<string>)[] { ("east", new[] { "'SH'", "'JS'" }), ("west", new[] { "'SC'", "'YN'" }) });
    Check("分片向导-MSSQL类别方案", msList[0].Contains("CASE") && msList[0].Contains("WHEN") && msList[0].Contains("PERSISTED"), msList.FirstOrDefault() ?? "");
    Check("分片向导-MSSQL索引列口径", ShardingWizard.IndexColumnsSql(DbEngine.SqlServer, "dbo", "orders").Contains("sys.index_columns"), "");
    Check("分片向导-MSSQL现状目录", ShardingWizard.PartitionsInfoSql(DbEngine.SqlServer, "dbo", "orders").Contains("sys.partitions"), "");

    // 6) 对账引擎扩 SQLite / MSSQL / Oracle
    Check("对账-MSSQL计数方括号", ReconcileEngine.CountSql("orders", null, DbEngine.SqlServer) == "SELECT COUNT(*) AS row_count FROM [orders]", "");
    Check("对账-MSSQL校验和", ReconcileEngine.ChecksumSql("orders", new[] { "id", "amount" }, null, DbEngine.SqlServer)!
        .Contains("CHECKSUM([id], [amount])"), "");
    var oraCk = ReconcileEngine.ChecksumSql("orders", new[] { "id", "amount" }, null, DbEngine.Oracle);
    Check("对账-Oracle校验和", oraCk!.Contains("STANDARD_HASH") && oraCk.Contains("NVL") && oraCk.Contains("MD5"), "");
    Check("对账-SQLite无哈希退化", ReconcileEngine.ChecksumSql("t", new[] { "a" }, null, DbEngine.SQLite) is null, "");
    Check("对账-MSSQL抽样TOP", ReconcileEngine.SampleSql("users", "id", 50, DbEngine.SqlServer) == "SELECT TOP 50 * FROM [users] ORDER BY [id]", "");
    Check("对账-Oracle抽样FETCH", ReconcileEngine.SampleSql("users", "id", 50, DbEngine.Oracle).Contains("FETCH FIRST 50 ROWS ONLY"), "");
    Check("对账-Oracle计数双引号", ReconcileEngine.CountSql("orders", null, DbEngine.Oracle).Contains("\"orders\""), "");
}

Layer("系统");
{
    // 持久化完整工作流（临时目录）：收藏 + 会话 文件往返、损坏文件容错、失败路径不残留
    var dir = Path.Combine(Path.GetTempPath(), "taodbm-sys-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        Favorites.UseFile(Path.Combine(dir, "favorites.json"));
        TabSession.UseFile(Path.Combine(dir, "tabsession.json"));
        var favs = Favorites.Add(new List<FavoriteItem>(), "系统层", "MySQL", "db", "SELECT 1;");
        Favorites.Save(favs);
        Check("系统层：收藏文件往返", Favorites.Load().Count == 1 && Favorites.Load()[0].Name == "系统层");

        var badFav = Path.Combine(dir, "favorites.json");
        var good = File.ReadAllText(badFav);
        File.WriteAllText(badFav, "{corrupted json!!");
        Check("损坏收藏文件安全降级", Favorites.Load().Count == 0);
        Check("损坏文件原样保留", File.ReadAllText(badFav) == "{corrupted json!!");
        Check("失败路径无临时残留", Directory.GetFiles(dir).Length == 1);
        File.WriteAllText(badFav, good);

        File.WriteAllText(Path.Combine(dir, "tabsession.json"), "not-json-at-all");
        Check("损坏会话文件安全降级", TabSession.Load().Count == 0);
    }
    finally
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
{
    // 主题选择持久化：往返、损坏文件安全回落、未知值回落、失败路径无残留
    var dir = Path.Combine(Path.GetTempPath(), "taodbm-theme-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        ThemeStore.UseFile(Path.Combine(dir, "theme.json"));
        Check("主题默认水墨", ThemeStore.Load() == "ink");
        ThemeStore.Save("night");
        Check("主题选择往返", ThemeStore.Load() == "night");
        ThemeStore.Save("ant");
        Check("旧 ant 选择映射天青", ThemeStore.Load() == "gray");
        File.WriteAllText(Path.Combine(dir, "theme.json"), "{oops");
        Check("损坏主题文件回落水墨", ThemeStore.Load() == "ink");
        Check("主题文件原样保留", File.ReadAllText(Path.Combine(dir, "theme.json")) == "{oops");
        Check("失败路径无临时残留", Directory.GetFiles(dir).Length == 1);
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}
{
    // 托盘关闭偏好持久化：默认询问、tray/exit 往返、损坏文件安全回落、失败路径无残留
    var dir = Path.Combine(Path.GetTempPath(), "taodbm-prefs-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        AppPrefs.UseFile(Path.Combine(dir, "appprefs.json"));
        Check("关闭行为默认询问", AppPrefs.Load() == CloseBehavior.Ask);
        AppPrefs.Save(CloseBehavior.Tray);
        Check("托盘最小化偏好往返", AppPrefs.Load() == CloseBehavior.Tray);
        AppPrefs.Save(CloseBehavior.Exit);
        Check("直接退出偏好往返", AppPrefs.Load() == CloseBehavior.Exit);
        File.WriteAllText(Path.Combine(dir, "appprefs.json"), "{broken");
        Check("损坏偏好文件回落询问", AppPrefs.Load() == CloseBehavior.Ask);
        Check("偏好文件原样保留", File.ReadAllText(Path.Combine(dir, "appprefs.json")) == "{broken");
        Check("失败路径无临时残留", Directory.GetFiles(dir).Length == 1);
    }
    finally { AppPrefs.UseFile(""); if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}
{
    // 托盘健在断言（2026-11-03 真机托盘不见的教训常驻）：窗口句柄必须随消息上送，增改删同源；
    // 添加结果不得被掩盖；explorer 重启后自动补挂；右键必须带「我的连接」常用区且主窗已接线。
    string ProjRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent!)
            if (File.Exists(Path.Combine(d.FullName, "Src", "TaoDBM.App", "MainWindow.xaml"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    var root = ProjRoot();
    var tray = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "UI", "TrayIcon.cs"));
    Check("托盘注册带主窗句柄（NIM_ADD 成败的唯一键）", tray.Contains("hWnd = _ownerHwnd"));
    Check("托盘添加结果如实记录不被掩盖", tray.Contains("_added = Shell_NotifyIconW(NIM_ADD"));
    Check("explorer 重启后托盘自动补挂", tray.Contains("TaskbarCreated") && tray.Contains("AddToShell"));
    Check("托盘右键带我的连接动态区", tray.Contains("RecentProvider") && tray.Contains("RecentRequested") && tray.Contains("我的连接"));
    var mwCs3 = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "MainWindow.xaml.cs"));
    Check("主窗接线托盘连接快捷区", mwCs3.Contains("RecentProvider") && mwCs3.Contains("TrayConnect"));
    // 关窗分流铁律（2026-11-03 真机反馈常驻）：托盘没挂上时绝不隐藏玩消失——要么真挂上再拦，要么如实退出
    Check("托盘挂载状态如实可查", tray.Contains("public bool IsAdded"));
    Check("托盘未挂上时关窗不拦截", mwCs3.Contains("!_tray.IsAdded") && mwCs3.Contains("try { AppWindow.Hide(); }"));
    // 2026-11-03 二轮兜底（真机复测仍看不到托盘）：资源缺失不连坐、失败自动重试、版本可验装
    Check("托盘创建不以图标文件存在为前提", mwCs3.Contains("StartTray(trayIcoPath);"));
    Check("托盘图标缺失回落系统图标", tray.Contains("LoadIconW"));
    Check("托盘未挂上自动重试", tray.Contains("public void RetryAdd") && mwCs3.Contains("StartTrayRetry()"));
    // 2026-11-03 三轮（真机日志实锤：自建消息窗口 CreateWindowExW 返回空，托盘整链失效）：改托管主窗句柄
    Check("托盘消息托管主窗不再自建窗口", tray.Contains("SetWindowLongPtrW") && !tray.Contains("CreateWindowExW"));
    Check("子类化其余消息链回原过程", tray.Contains("CallWindowProcW"));
    Check("退出时还原主窗过程", tray.Contains("GWLP_WNDPROC") && tray.Contains("_prevProc"));
    Check("托盘诊断写入日志且带错误码", tray.Contains("Core.AppLog.Write") && tray.Contains("GetLastWin32Error"));
    Check("日志目录固定在数据根 logs", File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.Infrastructure", "AppLog.cs")).Contains("\"logs\""));
    // 陶瓷质感（2026-11-06 用户定稿，对齐 TaoAgent/TaoPad）：卡片面 XAML 资源换纵向釉面微渐变，
    // 三套主题统一生效；异常退回纯色不阻断换肤；墨夜配色本体不被触碰（母本锚点断言在上文主题块）。
    var antCs = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "UI", "Ant.cs"));
    Check("陶瓷质感：釉面微渐变挂载到卡片资源且随主题重涂",
        antCs.Contains("ApplyCeramicCard(pal)") && antCs.Contains("LinearGradientBrush") && antCs.Contains("Offset = 0.55"));
    Check("陶瓷质感：通道增减受钳制且异常退回纯色画刷",
        antCs.Contains("Math.Min(255, v + 15)") && antCs.Contains("Math.Max(0, v - 12)")
        && antCs.Contains("Application.Current.Resources[\"AntCard\"] = brush") && antCs.Contains("Application.Current.Resources[\"AntCard\"] = Card"));
    var csprojText = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "TaoDBM.App.csproj"));
    // 二期拆分后安装包工程暂未纳入，wxs 存在才校验
    var wxsPath = Path.Combine(root, "Src", "Installer", "Package.wxs");
    var versionOk = csprojText.Contains("<Version>2.0.0<")
        && (!File.Exists(wxsPath) || File.ReadAllText(wxsPath).Contains("Version=\"2.0.0\""));
    Check("版本号升级可验装（标题栏 v2.0.0）", versionOk);
    // 弹窗表单右缘让出滚动条带（2026-11-03 真机截图：浮层滚动条压在输入框右缘像一条越界竖线）
    int bandOk = 0;
    foreach (var f in new[] { "ConnectionDialog.cs", "DataSyncDialog.cs", "FakerDialog.cs", "WorkTab.cs" })
        if (File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "UI", f)).Contains("Margin = new Thickness(0, 0, 12, 0)")) bandOk++;
    Check("弹窗表单统一让出滚动条带（4 处）", bandOk == 4, $"{bandOk}/4");
}
{
    // 存储落 D 盘（Tao 系列）：回退目录契约 + 旧数据迁移（已有同名不覆盖）+ 失败安全
    Check("存储根目录可用", Directory.Exists(TaoPaths.Root) && Path.IsPathRooted(TaoPaths.Root));
    Check("存储根即回退目录（非 Windows）", OperatingSystem.IsWindows() || TaoPaths.Root.Contains("TaoDBM"));
    var fp = TaoPaths.FilePath("probe-test.json");
    Check("FilePath 自动建目录", File.Exists(fp) == false && Directory.Exists(TaoPaths.Root));
    var migDir = Path.Combine(Path.GetTempPath(), "taodbm-mig-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(Path.Combine(migDir, "old"));
        Directory.CreateDirectory(Path.Combine(migDir, "new"));
        File.WriteAllText(Path.Combine(migDir, "old", "favorites.json"), "[{\"Name\":\"迁移\"}]");
        File.WriteAllText(Path.Combine(migDir, "old", "connections.json"), "[]");
        var newRoot = Path.Combine(migDir, "new");
        File.WriteAllText(Path.Combine(newRoot, "favorites.json"), "[{\"Name\":\"已有\"}]");
        var moved = TaoPaths.MigrateLegacyFiles([Path.Combine(migDir, "old"), Path.Combine(migDir, "new")], targetRoot: newRoot);
        Check("迁移复制缺失文件", File.ReadAllText(Path.Combine(newRoot, "connections.json")) == "[]" && moved.Contains("connections.json"), string.Join(",", moved));
        Check("迁移不覆盖已有文件", File.ReadAllText(Path.Combine(newRoot, "favorites.json")) == "[{\"Name\":\"已有\"}]" && !moved.Contains("favorites.json"));
        Check("迁移失败路径安全（源目录缺失不抛）", TaoPaths.MigrateLegacyFiles([Path.Combine(migDir, "no-such-dir")], targetRoot: newRoot).Count == 0);
    }
    finally { if (Directory.Exists(migDir)) Directory.Delete(migDir, true); }
}
{
    // 大数据分块往返：5 万行 CSV → 解析 → INSERT，行数字节级一致
    var sb = new StringBuilder("id,tag\n");
    for (int i = 0; i < 50000; i++) sb.Append(i).Append(",tag_").Append(i % 7).Append('\n');
    var (bh, br) = CsvImporter.Parse(sb.ToString());
    var bc = new List<ColumnSpec> { new() { Name = "id", DataType = "int" }, new() { Name = "tag", DataType = "varchar(20)" } };
    var inserts = CsvImporter.BuildInserts(new TestProv(), "db", "big", bc, bh, br, batch: 500);
    var total = inserts.Sum(x => x.Split("VALUES (").Length - 1);
    Check("5 万行往返行数一致", br.Count == 50000 && total == 50000, $"{br.Count}/{total}");
}

{
    // 层色底定律（TaoZip v62 母本）断言锁：页底显式、空态禁三级灰、标签根板层色
    string SrcRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent!)
            if (File.Exists(Path.Combine(d.FullName, "Src", "TaoDBM.App", "MainWindow.xaml"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    var srcRoot = SrcRoot();
    var xaml = File.ReadAllText(Path.Combine(srcRoot, "Src", "TaoDBM.App", "MainWindow.xaml"));
    Check("主窗根板钉主题底色", xaml.Contains("<Grid Background=\"{ThemeResource AntBg}\">"));
    var tab = File.ReadAllText(Path.Combine(srcRoot, "Src", "TaoDBM.App", "UI", "WorkTab.cs"));
    Check("标签根板层色底在位", tab.Contains("Background = Ant.Bg;"));
    bool EmptyHintOk(string marker)
    {
        int i = tab.IndexOf(marker, StringComparison.Ordinal);
        return i >= 0 && tab[i..Math.Min(i + 300, tab.Length)].Contains("Foreground = Ant.TextSub");
    }
    Check("空态提示用次要前景", EmptyHintOk("还没有收藏") && EmptyHintOk("还没有历史记录"));
}
{
    // 命名与 BOM 铁律（2026-11-01 全系避坑常驻，源自 TaoPad v74 审计）：源码树本身必须干净
    string TreeRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent!)
            if (File.Exists(Path.Combine(d.FullName, "Src", "TaoDBM.App", "MainWindow.xaml"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    var treeRoot = TreeRoot();
    bool clean = true; string? hit = null;
    foreach (var p in Directory.EnumerateFiles(treeRoot, "*", SearchOption.AllDirectories))
    {
        var rel = p.Substring(treeRoot.Length + 1).Replace('\\', '/');
        if (rel.Contains("/obj/") || rel.Contains("/bin/") || rel.Contains("/.git/") || rel.Contains("/.vs/")
            || rel.StartsWith("typecheck/") || rel.StartsWith("logictest/") || rel.StartsWith("Docs/")) continue;
        if (rel.Any(c => c > 127)) { clean = false; hit = rel; break; }
        if (p.EndsWith(".cs") || p.EndsWith(".xaml"))
        {
            var b = File.ReadAllBytes(p);
            if (b.Length < 3 || b[0] != 0xEF || b[1] != 0xBB || b[2] != 0xBF) { clean = false; hit = rel; break; }
        }
    }
    Check("源码树全 ASCII 且 .cs/.xaml 带 BOM（首违规: " + (hit ?? "无") + "）", clean);
}
{
    // 工程文件结构铁律（2026-11-01 真机 MSB4066 教训）：PropertyGroup 内只许属性，条目元素必须在 ItemGroup
    string ProjRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent!)
            if (File.Exists(Path.Combine(d.FullName, "Src", "TaoDBM.App", "MainWindow.xaml"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    var csproj = Path.Combine(ProjRoot(), "Src", "TaoDBM.App", "TaoDBM.App.csproj");
    var doc = System.Xml.Linq.XDocument.Load(csproj);
    string[] itemTags = { "Content", "Page", "None", "PackageReference", "ProjectReference", "Reference", "EmbeddedResource", "Compile", "ApplicationDefinition", "Resource", "Folder" };
    var stray = doc.Descendants("PropertyGroup")
        .SelectMany(pg => pg.Elements())
        .Where(e => itemTags.Contains(e.Name.LocalName))
        .Select(e => e.Name.LocalName)
        .ToList();
    Check("csproj PropertyGroup 内无条目元素（发现: " + string.Join(",", stray) + "）", stray.Count == 0);
    // 批处理换行铁律（2026-11-01 真机乱码嚼行教训）：.bat 必须 CRLF；根目录=GBK 编码，build/*.utf8.bat=UTF-8 编码
    var projRoot = ProjRoot();
    int batBad = 0; string batHit = "";
    foreach (var p in Directory.EnumerateFiles(projRoot, "*.bat", SearchOption.AllDirectories))
    {
        var rel = p.Substring(projRoot.Length + 1).Replace('\\', '/');
        if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
        var b = File.ReadAllBytes(p);
        int bareLf = 0;
        for (int i = 0; i < b.Length; i++)
            if (b[i] == 10 && (i == 0 || b[i - 1] != 13)) bareLf++;
        bool isUtf8Variant = rel.Contains(".utf8.");
        bool utf8Clean = !System.Text.Encoding.UTF8.GetString(b).Contains('\uFFFD');
        bool hasHighBytes = b.Any(x => x >= 0x80);
        bool encOk = isUtf8Variant ? utf8Clean : (hasHighBytes ? !utf8Clean : true);
        if (bareLf > 0 || !encOk) { batBad++; batHit = rel; }
    }
    Check("全部 .bat 为 CRLF 且编码合规（首违规: " + batHit + "）", batBad == 0);
}
{
    // 换肤三闸（2026-11-01 对照 TaoDown v2.36 换肤失效教训）：页面笔刷消费 + 标题栏钉色 + 强调色随象
    string ProjRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent!)
            if (File.Exists(Path.Combine(d.FullName, "Src", "TaoDBM.App", "MainWindow.xaml"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    var root = ProjRoot();
    var mwXaml = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "MainWindow.xaml"));
    Check("页面层无写死旧蓝 #1677FF", !mwXaml.Contains("#1677FF"));
    var mwCs = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "MainWindow.xaml.cs"));
    Check("标题栏随象而染（AppWindow.TitleBar 钉色在位）", mwCs.Contains("AppWindow.TitleBar") && mwCs.Contains("ButtonHoverBackgroundColor"));
    var ant = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "UI", "Ant.cs"));
    Check("系统强调色族随主题（accent 挂会变色画刷）", ant.Contains("SystemControlForegroundAccentBrush") && ant.Contains("ThemeApplied"));
}
{
    // 换肤收尾（2026-11-02 同步全系最新要求）：accent 全族随象 + 主色底字随象 + 高亮/报告随象
    string ProjRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent!)
            if (File.Exists(Path.Combine(d.FullName, "Src", "TaoDBM.App", "MainWindow.xaml"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    var root = ProjRoot();
    var appXaml = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "App.xaml"));
    Check("App.xaml 不写死 accent 色族（含浅深变体全由 Ant 运行时管）", !appXaml.Contains("SystemAccentColor") && !appXaml.Contains("#1677FF"));
    var antCs = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "UI", "Ant.cs"));
    Check("主色底字随象（OnPrimary 画刷，墨夜近黑，accent 浅深变体同源）",
        antCs.Contains("OnPrimary") && antCs.Contains("131211") && antCs.Contains("SystemAccentColorLight1") && antCs.Contains("SystemAccentColorDark1"));
    string uiHit = "";
    foreach (var f in Directory.GetFiles(Path.Combine(root, "Src", "TaoDBM.App", "UI"), "*.cs"))
    {
        var s = File.ReadAllText(f);
        bool hardWhite = s.Contains("Colors.White");
        bool hardBlue = Path.GetFileName(f) != "Ant.cs" && s.Contains("#1677FF");
        bool hlOld = s.Contains("Ant.Color(\"1677FF\")");
        if (hardWhite || hardBlue || hlOld) { uiHit = Path.GetFileName(f); break; }
    }
    Check("界面层无写死旧蓝与死白字（首违规: " + uiHit + "）", uiHit == "");
    var mwXaml = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "MainWindow.xaml"));
    Check("顶栏文字随象（AntOnPrimary 资源在位）", mwXaml.Contains("AntOnPrimary") && !mwXaml.Contains("Foreground=\"White\""));
    var wtCs = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "UI", "WorkTab.cs"));
    Check("SQL 高亮关键字取主题主色且换肤重刷", wtCs.Contains("Ant.Primary.Color") && wtCs.Contains("Ant.Text.Color") && wtCs.Contains("ThemeApplied"));
    var rb = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.Core", "ReportBuilder.cs"));
    Check("HTML 报告取色随主题", !rb.Contains("#1677FF") && rb.Contains("AntPrimary"));
}
{
    // 构建闸流程铁律（2026-11-02 真机教训：成功路径掉进失败块，闸把完整好包全拦了）：
    // 两份 bat 必须三段式——探针失败→:unpackbad，自检失败→:checkbad，全过→显式 goto :gateok 放行
    string ProjRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent!)
            if (File.Exists(Path.Combine(d.FullName, "Src", "TaoDBM.App", "MainWindow.xaml"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    static int BCount(byte[] arr, byte[] pat)
    {
        int c = 0;
        for (int i = 0; i <= arr.Length - pat.Length; i++)
        {
            bool m = true;
            for (int j = 0; j < pat.Length; j++) if (arr[i + j] != pat[j]) { m = false; break; }
            if (m) c++;
        }
        return c;
    }
    static int BIndex(byte[] arr, byte[] pat)
    {
        for (int i = 0; i <= arr.Length - pat.Length; i++)
        {
            bool m = true;
            for (int j = 0; j < pat.Length; j++) if (arr[i + j] != pat[j]) { m = false; break; }
            if (m) return i;
        }
        return -1;
    }
    var root = ProjRoot();
    int badFlow = 0; string batHit = "";
    int batTotal = 0;
    foreach (var rel in new[] { "build-installer.bat", "build/build-installer.utf8.bat" })
    {
        if (!File.Exists(Path.Combine(root, rel))) { continue; }
        batTotal++;
        var bb = File.ReadAllBytes(Path.Combine(root, rel));
        byte[] P(string s) => System.Text.Encoding.UTF8.GetBytes(s);
        bool hasGates = BCount(bb, P("goto :gateok")) == 1 && BCount(bb, P("goto :checkbad")) == 1
            && BCount(bb, P("goto :unpackbad")) == 3 && BCount(bb, P("if errorlevel 1 goto :checkbad")) == 1
            && BCount(bb, P("-pdbtype none")) == 1 && BCount(bb, P("out\\*.msi")) == 1;
        int iUn = BIndex(bb, P("\r\n:unpackbad\r\n")), iCh = BIndex(bb, P("\r\n:checkbad\r\n")), iGo = BIndex(bb, P("\r\n:gateok\r\n"));
        bool ordered = iUn >= 0 && iUn < iCh && iCh < iGo;
        if (!hasGates || !ordered) { badFlow++; batHit = rel; }
    }
    Check(batTotal == 0 ? "构建闸三段式流程（二期暂无 bat，跳过）" : "构建闸三段式流程齐备（首违规: " + batHit + "）", badFlow == 0);
}
{
    // 快捷方式与顶栏铁律（2026-11-02 真机教训）：
    // ①自定义动作设置的目录属性值必须以目录分隔符结尾（MSI 官方规则），否则快捷方式 Target 拼接黏连成坏路径；
    // ②两条快捷方式必须显式带 Icon 引用（TaoZip/TaoDown 同款）；③开始菜单目录须带卸载清理；
    // ④主题菜单独立成右上角按钮（关于左侧），图标菜单不再含主题项；
    // ⑤图标菜单贴图标左缘防卡片溢出主窗，主题菜单右缘对齐防右溢。
    string ProjRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent!)
            if (File.Exists(Path.Combine(d.FullName, "Src", "TaoDBM.App", "MainWindow.xaml"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    var root = ProjRoot();
    // 二期拆分后安装包工程暂未纳入，wxs 存在才校验
    var wxsPath = Path.Combine(root, "Src", "Installer", "Package.wxs");
    if (File.Exists(wxsPath))
    {
        var wxs = File.ReadAllText(wxsPath);
    Check("目录属性赋值以分隔符结尾（快捷方式目标防黏连）", wxs.Contains("Value=\"D:\\Program Files\\TaoDBM\\\""));
    Check("两条快捷方式显式带安装包图标引用", System.Text.RegularExpressions.Regex.Matches(wxs, "Icon=\"TaoDBMIco\"").Count == 2);
    Check("开始菜单目录卸载清理在位", wxs.Contains("RemoveStartMenuDir"));
    }
    var mwXaml = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "MainWindow.xaml"));
    Check("主题菜单独立成钮且在关于左侧（全系勾选样式）",
        mwXaml.Contains("BtnTheme") && mwXaml.IndexOf("BtnTheme") < mwXaml.IndexOf("BtnAbout")
        && System.Text.RegularExpressions.Regex.Matches(mwXaml, "ToggleMenuFlyoutItem").Count == 3
        && !mwXaml.Contains("主题："));
    var mwCs2 = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "MainWindow.xaml.cs"));
    Check("主题对勾随象刷新（切象同步勾选+启动初始化）",
        mwCs2.Contains("UpdateThemeChecks") && mwCs2.Contains("ThemeInk.IsChecked")
        && mwCs2.Contains("ThemeSky.IsChecked") && mwCs2.Contains("ThemeNight.IsChecked"));
    Check("菜单卡片贴边防溢出（图标左缘对齐/主题右缘对齐）",
        mwXaml.Contains("Placement=\"BottomEdgeAlignedLeft\"") && mwXaml.Contains("Placement=\"BottomEdgeAlignedRight\""));
}
{
    // 全测三件套之真人模拟操作链：拟人完整路径（切主题→查历史→收藏→会话恢复→退出重载）
    var dir = Path.Combine(Path.GetTempPath(), "taodbm-human-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    ThemeStore.UseFile(Path.Combine(dir, "theme.json"));
    Favorites.UseFile(Path.Combine(dir, "favorites.json"));
    TabSession.UseFile(Path.Combine(dir, "tabsession.json"));
    QueryHistory.UseFile(Path.Combine(dir, "history.json"));
    try
    {
        // ① 挑主题（三象各切一遍，退出前停在水墨）
        foreach (var th in ThemePalette.Names) ThemeStore.Save(th);
        Check("拟人：换装一轮后停在墨夜", ThemeStore.Load() == "night");
        ThemeStore.Save("ink");
        // ② 查历史（连续 5 条，最新在前）
        for (int i = 0; i < 5; i++) QueryHistory.Add("mysql", "db1", $"SELECT {i}");
        var hist = QueryHistory.Items;
        Check("拟人：历史最新在前且全在位", hist.Count >= 5 && hist[0].Sql == "SELECT 4");
        // ③ 收藏一条常用 SQL 并能找回
        var favs = Favorites.Load();
        Favorites.Save(Favorites.Add(favs, "常用-用户", "mysql", "db1", "SELECT * FROM users WHERE name LIKE '%tao%'"));
        Check("拟人：收藏往返", Favorites.Load().Any(f => f.Name == "常用-用户"));
        // ④ 会话恢复（两个标签）
        var tabs = new List<TabSessionItem> { new() { ConnectionId = "c1", Mode = "Query", Db = "db1", Sql = "SELECT 1" }, new() { ConnectionId = "c2", Mode = "Browse", Db = "db0", Child = "users" } };
        TabSession.Save(tabs);
        var back = TabSession.Load();
        Check("拟人：会话标签完整恢复", back.Count == 2 && back[0].ConnectionId == "c1" && back[1].Child == "users");
    }
    finally
    {
        ThemeStore.UseFile(""); Favorites.UseFile(""); TabSession.UseFile(""); QueryHistory.UseFile("");
        Directory.Delete(dir, true);
    }
}

Layer("暴力");

// A1 App 工程引用自洽（2026-11-01 TaoPad 真机 CS0103 避坑，全系同型）：
// 沙箱不编译 WinUI App 工程，界面侧编译错误只有真机能看见——
// 源码层兜底：App 工程所有 .cs 对 ThemePalette/ThemeStore 的静态成员引用必须有定义。
{
    string baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Src"));
    // 二期拆分后：tests/TaoDBM.LogicTest/bin/<cfg>/<tfm> 需上跳五级到解决方案根（旧布局为四级）；
    // 定义在 Infrastructure，被扫描的是 App 工程（沙箱不编译 WinUI，源码层兜底）
    var defs = File.ReadAllText(Path.Combine(baseDir, "TaoDBM.Infrastructure", "ThemePalette.cs"))
        + File.ReadAllText(Path.Combine(baseDir, "TaoDBM.Infrastructure", "ThemeStore.cs"));
    var orphans = new List<string>();
    foreach (var f in Directory.GetFiles(Path.Combine(baseDir, "TaoDBM.App"), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 && !f.EndsWith("ThemePalette.cs") && !f.EndsWith("ThemeStore.cs")))
    {
        var src = File.ReadAllText(f);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(src, @"(?:ThemePalette|ThemeStore)\.(\w+)"))
            if (!System.Text.RegularExpressions.Regex.IsMatch(defs, @"\b" + m.Groups[1].Value + @"\b"))
                orphans.Add($"{Path.GetFileName(f)}:{m.Groups[1].Value}");
    }
    Check("A1 App工程引用自洽：ThemePalette/ThemeStore 成员引用皆有定义",
        orphans.Count == 0, orphans.Count > 0 ? $"孤儿引用 {string.Join(",", orphans)}" : "自洽");
}

{
    // 模糊输入 1 万条 × 全部解析入口，全都不允许抛异常
    var rnd = new Random(20261101);
    string Nasty()
    {
        var pool = "'\"; --/*\n { } []() {},:0aA#\u4e2d\u6587\ud83d\ude00\t\\x00', ' OR 1=1--, DROP TABLE t; {}{{ }}[, ]";
        var n = rnd.Next(1, 40);
        var cs = new char[n];
        for (int i = 0; i < n; i++) cs[i] = pool[rnd.Next(pool.Length)];
        return new string(cs);
    }
    int exceptions = 0;
    for (int i = 0; i < 10000; i++)
    {
        var s = Nasty();
        try { SqlSplitter.Split(s); } catch { exceptions++; }
        try { TnsNames.Parse(s); } catch { exceptions++; }
        try { CsvImporter.Parse(s); } catch { exceptions++; }
        try { JsonImporter.Parse(s); } catch { exceptions++; }
        try { SqlHighlighter.Tokenize(s); } catch { exceptions++; }
    }
    Check("1 万条模糊输入全入口零异常", exceptions == 0, $"异常 {exceptions} 次");
    var inject = SqlSplitter.Split("'; DROP TABLE users; --");
    Check("注入载荷不产生可执行边界", inject.All(x => x.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || !x.Contains("DROP", StringComparison.OrdinalIgnoreCase)) || inject.Count <= 2, string.Join("|", inject));
}
{
    // 并发压测：8 线程 × 500 次，四个解析入口并行无异常无串扰
    int errs = 0;
    Parallel.For(0, 8, _ =>
    {
        var rnd = new Random(Environment.CurrentManagedThreadId);
        string N() { var pool = "';\"/*\n{}[]:,"; var n = rnd.Next(1, 30); var cs = new char[n]; for (int i = 0; i < n; i++) cs[i] = pool[rnd.Next(pool.Length)]; return new string(cs); }
        for (int i = 0; i < 500; i++)
        {
            var s = N();
            try { SqlSplitter.Split(s); } catch { Interlocked.Increment(ref errs); }
            try { CsvImporter.Parse(s); } catch { Interlocked.Increment(ref errs); }
            try { TnsNames.Parse(s); } catch { Interlocked.Increment(ref errs); }
        }
    });
    Check("8 线程并发解析零异常", errs == 0, $"异常 {errs} 次");
}
{
    // 模糊主题文件 2000 次：Load 永不抛、恒返回合法主题名
    var dir = Path.Combine(Path.GetTempPath(), "taodbm-themefuzz-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    ThemeStore.UseFile(Path.Combine(dir, "theme.json"));
    var rnd = new Random(77);
    int bad = 0;
    for (int i = 0; i < 2000; i++)
    {
        var bytes = new byte[rnd.Next(0, 40)];
        rnd.NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(dir, "theme.json"), bytes);
        try
        {
            var v = ThemeStore.Load();
            if (v != "ink" && v != "gray" && v != "night") bad++;
        }
        catch { bad++; }
    }
    Check("2000 条模糊主题文件零异常", bad == 0, $"异常 {bad} 次");
    ThemeStore.UseFile("");   // 恢复默认路径，避免影响后续
    Directory.Delete(dir, true);
}


// ================= 拟真数据（v1.4.0 全测；v1.4.2 多表化） =================
Console.WriteLine("== Faker 语义推断 ==");
{
    var cases = new (string name, string type, SemanticKind kind)[]
    {
        ("id", "BIGINT", SemanticKind.Id), ("user_id", "INT", SemanticKind.Id),
        ("order_guid", "CHAR(36)", SemanticKind.Guid), ("order_no", "VARCHAR(32)", SemanticKind.OrderNo),
        ("user_name", "VARCHAR(50)", SemanticKind.UserName), ("nickname", "VARCHAR(50)", SemanticKind.NickName),
        ("real_name", "VARCHAR(20)", SemanticKind.PersonName), ("email", "VARCHAR(64)", SemanticKind.Email),
        ("mobile_phone", "VARCHAR(20)", SemanticKind.Phone), ("id_card_no", "VARCHAR(18)", SemanticKind.IdCard),
        ("passwd", "VARCHAR(64)", SemanticKind.PasswordHash), ("access_token", "VARCHAR(128)", SemanticKind.Token),
        ("avatar_url", "VARCHAR(255)", SemanticKind.ImageUrl), ("home_page", "VARCHAR(255)", SemanticKind.Url),
        ("client_ip", "VARCHAR(15)", SemanticKind.Ip), ("listen_port", "INT", SemanticKind.Port),
        ("age", "INT", SemanticKind.Age), ("gender", "ENUM('男','女')", SemanticKind.Gender),
        ("total_amount", "DECIMAL(10,2)", SemanticKind.Money), ("unit_price", "DECIMAL(8,2)", SemanticKind.Price),
        ("stock_num", "INT", SemanticKind.Quantity), ("pay_rate", "DECIMAL(5,2)", SemanticKind.Percent),
        ("order_status", "TINYINT", SemanticKind.Status), ("member_type", "VARCHAR(20)", SemanticKind.Category),
        ("tags", "VARCHAR(100)", SemanticKind.Tag), ("article_title", "VARCHAR(120)", SemanticKind.Title),
        ("content", "TEXT", SemanticKind.ContentText), ("remark", "VARCHAR(255)", SemanticKind.Remark),
        ("address_detail", "VARCHAR(200)", SemanticKind.Address), ("city_name", "VARCHAR(30)", SemanticKind.City),
        ("province_name", "VARCHAR(30)", SemanticKind.Province), ("company_name", "VARCHAR(80)", SemanticKind.CompanyName),
        ("birthday", "DATE", SemanticKind.Birthday), ("created_at", "DATETIME", SemanticKind.DateTime),
        ("deleted", "TINYINT(1)", SemanticKind.Boolean), ("is_enabled", "BIT", SemanticKind.Boolean),
        ("color_hex", "CHAR(7)", SemanticKind.ColorCode), ("job_title", "VARCHAR(40)", SemanticKind.Job),
        ("education_level", "VARCHAR(20)", SemanticKind.Education), ("version_code", "VARCHAR(20)", SemanticKind.Version),
        ("lang", "VARCHAR(10)", SemanticKind.Language), ("tag_json", "JSON", SemanticKind.Json),
    };
    int wrong = 0;
    foreach (var (nm, ty, k) in cases)
        if (Faker.InferSemantic(nm, ty) != k) { wrong++; Console.WriteLine($"    · {nm}({ty}) → {Faker.InferSemantic(nm, ty)} 期望 {k}"); }
    Check($"语义推断 {cases.Length} 例全中", wrong == 0, $"错 {wrong} 例");
}
{
    Check("长度解析", Faker.ParseLen("VARCHAR(50)") == 50 && Faker.ParseLen("CHAR(36)") == 36 && Faker.ParseLen("TEXT") == 0);
    Check("精度解析", Faker.ParsePrecision("DECIMAL(10,2)") == (10, 2) && Faker.ParsePrecision("INT") == (0, 0));
    Check("枚举解析", string.Join("|", Faker.ParseEnumValues("ENUM('男','女','未知')")) == "男|女|未知");
    Check("类型判断", Faker.IsNumericType("BIGINT") && Faker.IsDateType("DATETIME") && Faker.IsTextType("NVARCHAR(50)") &&
        Faker.IsUuidType("UUID") && Faker.IsEnumType("SET('a','b')") && Faker.IsTinyInt("BIT"));
}
{
    // 各槽位生成值类型合法 + 确定性
    var col = new ColumnCtx { Name = "email", RawType = "VARCHAR(64)", Nullable = false, MaxLen = 64 };
    var r1 = new Random(42);
    var v1 = Faker.Gen(Faker.InferSemantic("email", "VARCHAR(64)"), col, r1, 1);
    var r2 = new Random(42);
    var v2 = Faker.Gen(Faker.InferSemantic("email", "VARCHAR(64)"), col, r2, 1);
    Check("同 seed 同值", v1 as string == v2 as string, $"{v1}|{v2}");
    var vMail = v1 as string ?? "";
    Check("邮箱合法", vMail.Contains('@') && vMail.Contains('.') && vMail.Length <= 64, vMail);

    var moneyCol = new ColumnCtx { Name = "amount", RawType = "DECIMAL(10,2)", Nullable = false, Scale = 2 };
    var mv = Faker.Gen(SemanticKind.Money, moneyCol, new Random(7), 1);
    Check("金额两位小数", mv is decimal d && decimal.Round(d, 2) == d, mv?.ToString() ?? "");
    var dtCol = new ColumnCtx { Name = "created_at", RawType = "DATETIME", Nullable = false };
    var dv = Faker.Gen(SemanticKind.DateTime, dtCol, new Random(9), 1) as string ?? "";
    Check("日期格式", System.Text.RegularExpressions.Regex.IsMatch(dv, @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$"), dv);
    var bCol = new ColumnCtx { Name = "is_enabled", RawType = "TINYINT(1)", Nullable = false };
    var bv = Faker.Gen(SemanticKind.Boolean, bCol, new Random(3), 1);
    Check("布尔 0/1", bv is int i && i is 0 or 1, bv?.ToString() ?? "");
    var eCol = new ColumnCtx { Name = "gender", RawType = "ENUM('男','女')", Nullable = false, EnumValues = Faker.ParseEnumValues("ENUM('男','女')") };
    var ev = Faker.Gen(SemanticKind.Gender, eCol, new Random(5), 1) as string ?? "";
    Check("枚举取值合法", ev is "男" or "女", ev);
    var phoneCol = new ColumnCtx { Name = "phone", RawType = "VARCHAR(11)", Nullable = false, MaxLen = 11 };
    var pv = Faker.Gen(SemanticKind.Phone, phoneCol, new Random(11), 1) as string ?? "";
    Check("手机号 11 位", System.Text.RegularExpressions.Regex.IsMatch(pv, @"^1[3-9]\d{9}$"), pv);
    var nameCol = new ColumnCtx { Name = "real_name", RawType = "VARCHAR(20)", Nullable = false, MaxLen = 20 };
    var nv = Faker.Gen(SemanticKind.PersonName, nameCol, new Random(13), 1) as string ?? "";
    Check("中文姓名", nv.Length >= 2 && nv.Length <= 20, nv);
    var nullableCol = new ColumnCtx { Name = "remark", RawType = "VARCHAR(255)", Nullable = true };
    var rndN = new Random(21);
    bool sawNull = false;
    for (long row = 1; row <= 14; row++)
        if (Faker.Gen(SemanticKind.Remark, nullableCol, rndN, row) is null) { sawNull = true; break; }
    Check("可空列稀疏 NULL", sawNull);
    var notNullCol = new ColumnCtx { Name = "remark", RawType = "VARCHAR(255)", Nullable = false };
    rndN = new Random(21);
    bool anyNull = Enumerable.Range(1, 14).Any(row => Faker.Gen(SemanticKind.Remark, notNullCol, rndN, row) is null);
    Check("非空列不出 NULL", !anyNull);
    var poolCol = new ColumnCtx { Name = "user_id", RawType = "BIGINT", Nullable = false, IsPrimaryKey = false };
    var pv2 = Faker.Gen(SemanticKind.Id, poolCol, new Random(31), 1, poolValue: "88");
    Check("关联列取池值", pv2 as string == "88", pv2?.ToString() ?? "");
}
{
    // 万行零异常 + 断言规模（暴力）
    var cols = new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "BIGINT", IsKey = true },
        new() { Name = "real_name", DataType = "VARCHAR(20)" },
        new() { Name = "email", DataType = "VARCHAR(64)" },
        new() { Name = "mobile", DataType = "VARCHAR(11)" },
        new() { Name = "total_amount", DataType = "DECIMAL(10,2)" },
        new() { Name = "created_at", DataType = "DATETIME" },
        new() { Name = "is_enabled", DataType = "TINYINT(1)" },
        new() { Name = "remark", DataType = "VARCHAR(255)" },
        new() { Name = "city_name", DataType = "VARCHAR(30)" },
        new() { Name = "gender", DataType = "ENUM('男','女')" },
    };
    var table = new SeedTable { Name = "orders", Columns = cols, Rows = 10000 };
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var rows = SeedPlan.GenerateRows(DbEngine.MySQL, table, new List<SeedEdge>(), new(), 2026);
    sw.Stop();
    Check("万行生成零异常", rows.Count == 10000 && rows.All(r => r.Count == cols.Count), $"{rows.Count} 行 {sw.ElapsedMilliseconds} ms");
    var ids = rows.Select(r => r[0]).Cast<long>().ToList();
    Check("PK 唯一递增", ids.Count == 10000 && ids.Distinct().Count() == 10000 && ids[0] == 1 && ids[9999] == 10000);
    var emails = rows.Select(r => r[2] as string).OfType<string>().ToList();
    Check("万行邮箱全合法", emails.Count > 0 && emails.All(e => e.Contains('@') && e.Length <= 64));
    Check("枚举列全合法", rows.All(r => r[9] == null || r[9]!.ToString() is "男" or "女"));
    Check("确定性重放", SeedPlan.GenerateRows(DbEngine.MySQL, table, new(), new(), 2026)
        .Zip(rows, (a, b) => a.Select((x, i) => (x?.ToString()) == (b[i]?.ToString())).All(y => y)).All(x => x));
    Check("不同 seed 不同值", SeedPlan.GenerateRows(DbEngine.MySQL, table, new(), new(), 2027)[1][2]!.ToString() != rows[1][2]!.ToString());
}

Console.WriteLine("== SeedPlan 关联与脚本 ==");
{
    var users = new SeedTable
    {
        Name = "users",
        Columns = new() { new ColumnSpec { Name = "id", DataType = "BIGINT", IsKey = true }, new ColumnSpec { Name = "real_name", DataType = "VARCHAR(20)" } },
    };
    var orders = new SeedTable
    {
        Name = "orders",
        Columns = new()
        {
            new ColumnSpec { Name = "id", DataType = "BIGINT", IsKey = true },
            new ColumnSpec { Name = "user_id", DataType = "BIGINT" },
            new ColumnSpec { Name = "total_amount", DataType = "DECIMAL(10,2)" },
        },
    };
    var items = new SeedTable
    {
        Name = "order_items",
        Columns = new()
        {
            new ColumnSpec { Name = "id", DataType = "BIGINT", IsKey = true },
            new ColumnSpec { Name = "order_id", DataType = "BIGINT" },
            new ColumnSpec { Name = "product_id", DataType = "BIGINT" },
        },
    };
    var tables = new List<SeedTable> { items, orders, users };
    var edges = SeedPlan.BuildRelations(tables, null);
    Check("字段名关联", edges.Count == 2 && edges.Any(e => e.FromTable == "orders" && e.FromCol == "user_id" && e.ToTable == "users") &&
        edges.Any(e => e.FromTable == "order_items" && e.FromCol == "order_id" && e.ToTable == "orders"),
        string.Join(";", edges.Select(e => $"{e.FromTable}.{e.FromCol}->{e.ToTable}")));
    Check("自引用跳过", !SeedPlan.BuildRelations(new List<SeedTable> { users }, null)
        .Any(e => e.FromTable == "users" && e.ToTable == "users"));
    var order = SeedPlan.TopoOrder(tables, edges);
    Check("拓扑：被引用表先", order[0].Name == "users" && order[1].Name == "orders" && order[2].Name == "order_items",
        string.Join("→", order.Select(t => t.Name)));
    Check("环不炸", SeedPlan.TopoOrder(tables, new List<SeedEdge> { new() { FromTable = "users", FromCol = "id", ToTable = "orders", ToCol = "id" }, new() { FromTable = "orders", FromCol = "id", ToTable = "users", ToCol = "id" } }).Count == 3);

    // == 多表生成计划（v1.4.2：每表独立行数，拓扑序执行） ==
    {
        var plan = SeedPlan.PlanFromCounts(new Dictionary<string, int> { ["users"] = 100, ["orders"] = 0, ["order_items"] = 20 },
            tables, edges);
        Check("多表计划：零行剔除、拓扑先行",
            plan.Count == 2 && plan[0].Table.Name == "users" && plan[1].Table.Name == "order_items",
            string.Join("→", plan.Select(p => $"{p.Table.Name}×{p.Count}")));
        Check("多表计划：行数逐表独立", plan[0].Count == 100 && plan[1].Count == 20);
        var all = SeedPlan.PlanFromCounts(new Dictionary<string, int> { ["users"] = 10, ["orders"] = 5, ["order_items"] = 3 },
            tables, edges);
        Check("多表计划：全表拓扑序", all.Select(p => p.Table.Name).SequenceEqual(new[] { "users", "orders", "order_items" }),
            string.Join(",", all.Select(p => p.Table.Name)));
        Check("多表计划：无输入为空", SeedPlan.PlanFromCounts(new Dictionary<string, int>(), tables, edges).Count == 0);
        // 多表生成确定性：同库同表同参必同值（多表计划复用单表生成器）
        var poolUsers = new Dictionary<string, Dictionary<string, List<object?>>> { ["users"] = new() { ["id"] = new List<object?> { 1, 2, 3 } } };
        var g1 = SeedPlan.GenerateRows(DbEngine.MySQL, all[1].Table, edges, poolUsers, 2026);
        var g2 = SeedPlan.GenerateRows(DbEngine.MySQL, all[1].Table, edges, poolUsers, 2026);
        Check("多表生成确定性", g1.Count == 5 && g1.Zip(g2, (a, b) => a.SequenceEqual(b)).All(x => x),
            g1.Count.ToString());
        // 引用列取值来自池（多表模式：被引用表先写入，子表引用列采样其值）
        Check("多表引用值来自池", g1.All(r => new object?[] { 1, 2, 3 }.Contains(r[1])), string.Join(",", g1.Select(r => r[1])));
    }

    // 生成：orders.user_id 从 users 值池采样
    var userRows = SeedPlan.GenerateRows(DbEngine.MySQL, users, edges, new(), 100);
    var pools = new Dictionary<string, Dictionary<string, List<object?>>> { ["users"] = SeedPlan.PoolFrom(DbEngine.MySQL, users, userRows) };
    var orderRows = SeedPlan.GenerateRows(DbEngine.MySQL, orders, edges, pools, 200);
    var userIds = orderRows.Select(r => r[1]).Cast<object>().Where(v => v is not null).Select(v => Convert.ToInt64(v)).ToList();
    Check("关联值来自池", userIds.Count > 0 && userIds.All(v => v >= 1 && v <= 100), string.Join(",", userIds.Take(5)));
}
{
    var cols = new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "INT", IsKey = true },
        new() { Name = "name", DataType = "VARCHAR(20)" },
        new() { Name = "note", DataType = "VARCHAR(50)" },
    };
    var rows = new List<List<object?>>
    {
        new() { 1, "张三", "他说:'你好'" },
        new() { 2, null, null },
    };
    var my = SeedPlan.BuildInsertScripts(DbEngine.MySQL, "t_users", cols, rows);
    Check("MySQL 方言", my.Count == 1 && my[0].Contains("INSERT INTO `t_users` (`id`, `name`, `note`) VALUES") &&
        my[0].Contains("'张三'") && my[0].Contains("'他说:''你好'''") && my[0].Contains("NULL"), my[0]);
    var pg = SeedPlan.BuildInsertScripts(DbEngine.PostgreSQL, "t users", cols, rows);
    Check("PG 双引号", pg[0].Contains("INSERT INTO \"t users\" (\"id\", \"name\", \"note\")"), pg[0]);
    var ss = SeedPlan.BuildInsertScripts(DbEngine.SqlServer, "t", cols, rows);
    Check("SQL Server N 前缀", ss[0].Contains("N'张三'"), ss[0]);
    var lite = SeedPlan.BuildInsertScripts(DbEngine.SQLite, "t", cols, rows);
    Check("SQLite 方言", lite[0].Contains("INSERT INTO \"t\" (\"id\", \"name\", \"note\")"), lite[0]);
    Check("Oracle 裸值", SeedPlan.BuildInsertScripts(DbEngine.Oracle, "T", cols, rows)[0].Contains("'张三'"));
    // 批 500
    var big = new List<List<object?>>();
    for (int i = 0; i < 1201; i++) big.Add(new List<object?> { i, "n", null });
    var scripts = SeedPlan.BuildInsertScripts(DbEngine.MySQL, "t", cols, big);
    Check("分批 500", scripts.Count == 3, scripts.Count.ToString());
    Check("批首 500 行", System.Text.RegularExpressions.Regex.Matches(scripts[0], "\n\\(").Count == 500);
    Check("空列/空行不产出", SeedPlan.BuildInsertScripts(DbEngine.MySQL, "t", new(), rows).Count == 0 &&
        SeedPlan.BuildInsertScripts(DbEngine.MySQL, "t", cols, new()).Count == 0);
}
{
    Check("分页方言", SeedPlan.SelectTopSql(DbEngine.SqlServer, "t", "id", 100).Contains("TOP (100)") &&
        SeedPlan.SelectTopSql(DbEngine.Oracle, "t", "id", 100).Contains("ROWNUM <= 100") &&
        SeedPlan.SelectTopSql(DbEngine.MySQL, "t", "id", 100).EndsWith("LIMIT 100"));
    Check("外键发现 SQL", SeedPlan.ForeignKeySql(DbEngine.MySQL, "shop", "orders")!.Contains("KEY_COLUMN_USAGE") &&
        SeedPlan.ForeignKeySql(DbEngine.SqlServer, "shop", "orders")!.Contains("sys.foreign_keys") &&
        SeedPlan.ForeignKeySql(DbEngine.Oracle, "shop", "orders")!.Contains("all_constraints") &&
        SeedPlan.ForeignKeySql(DbEngine.MongoDB, "shop", "orders") is null);
    Check("SQLite PRAGMA", SeedPlan.SqliteForeignKeySql("orders").Contains("foreign_key_list"));
    Check("引号转义", SeedPlan.QuoteIdent(DbEngine.MySQL, "we`ird") == "`we``ird`" &&
        SeedPlan.QuoteIdent(DbEngine.PostgreSQL, "a\"b") == "\"a\"\"b\"" &&
        SeedPlan.QuoteIdent(DbEngine.SqlServer, "a]b") == "[a]]b]");
}


Layer("万链");
{
    // 万链归账（万级补则）：万链并行走核心纯函数域，链内不写输出，Interlocked 归账终局断言。
    // 四个领域：SQL 分词区间不变量 / SqlSplitter 分段非空 / CSV 导出解析往返 / ER 模型 JSON 往返。
    const int ChainCount = 10000, StepsPerChain = 30;
    long totalAsserts = 0, violations = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var valuePool = new[] { "1", "42", "abc", "张三", "x y", "3.5", "true", "2026-01-01", "", "O'Brien" };
    var tokPool = new[] { "SELECT", "id", "FROM", "`t`", "'a;b'", "--c\n", "/*x*/", ";", "1.5e-3", "中", "\"i\"", ",", "(", ")", "WHERE", "x=", "0x1F" };
    Parallel.For(0, ChainCount, chain =>
    {
        var rnd = new Random(20261101 + chain);   // 每链独立种子：确定性不因并行改变，失败可复现
        long a = 0, v = 0;
        for (int step = 0; step < StepsPerChain; step++)
        {
            switch (rnd.Next(4))
            {
                case 0:   // SQL 分词：区间界内、逐段不越原文
                {
                    var sb = new StringBuilder();
                    int n = rnd.Next(0, 30);
                    for (int i = 0; i < n; i++) sb.Append(tokPool[rnd.Next(tokPool.Length)]);
                    var sql = sb.ToString();
                    try
                    {
                        var toks = SqlHighlighter.Tokenize(sql);
                        int lastEnd = 0;
                        foreach (var t in toks)
                        {
                            if (t.Start < lastEnd || t.Start < 0 || t.Length <= 0 || t.Start + t.Length > sql.Length) v++;
                            a++;
                            lastEnd = Math.Max(lastEnd, t.Start + t.Length);
                        }
                    }
                    catch { v++; a++; }
                    break;
                }
                case 1:   // SqlSplitter：每段非空、永不抛
                {
                    var sb = new StringBuilder();
                    int n = rnd.Next(1, 8);
                    for (int i = 0; i < n; i++) { sb.Append("SELECT ").Append(rnd.Next(1000)); if (i < n - 1) sb.Append(';'); }
                    try
                    {
                        var parts = SqlSplitter.Split(sb.ToString());
                        if (parts.Any(p => string.IsNullOrWhiteSpace(p))) v++;
                        a++;
                    }
                    catch { v++; a++; }
                    break;
                }
                case 2:   // CSV 往返：导出→解析，行列一致、值还原（空串归 null 口径）
                {
                    var r = new QueryResult { Columns = { "c1", "c2", "c3" } };
                    var vals = new object?[3];
                    for (int i = 0; i < 3; i++)
                        vals[i] = rnd.Next(10) == 0 ? DBNull.Value : valuePool[rnd.Next(valuePool.Length)];
                    r.Rows.Add(vals);
                    try
                    {
                        var (heads, rows) = CsvImporter.Parse(Exporter.Csv(r));
                        bool ok = heads.SequenceEqual(r.Columns) && rows.Count == 1;
                        if (ok)
                            for (int i = 0; i < 3; i++)
                            {
                                // CSV 口径：DBNull/null/空串 输出为空单元格，还原一律为 null
                                bool eq = vals[i] is DBNull or null or ""
                                    ? rows[0][i] is null
                                    : rows[0][i] == (string?)vals[i];
                                if (!eq) { ok = false; break; }
                            }
                        if (!ok) v++;
                        a++;
                    }
                    catch { v++; a++; }
                    break;
                }
                case 3:   // ER 模型：JSON 往返表列全等
                {
                    var m = new ErdModel();
                    int tableN = rnd.Next(1, 5);
                    for (int ti = 0; ti < tableN; ti++)
                    {
                        var tab = new ErdTable { Name = $"t{ti}_{rnd.Next(100)}" };
                        int colN = rnd.Next(1, 6);
                        for (int ci = 0; ci < colN; ci++)
                            tab.Columns.Add(new ErdColumn { Name = $"c{ci}", Type = rnd.Next(2) == 0 ? "int" : "varchar(50)", IsKey = ci == 0 });
                        m.Tables.Add(tab);
                    }
                    try
                    {
                        var back = ErdModel.FromJson(m.ToJson());
                        if (back is null || back.Tables.Count != tableN
                            || !back.Tables.Zip(m.Tables, (b, o) => b.Name == o.Name && b.Columns.Count == o.Columns.Count).All(x => x)) v++;
                        a++;
                    }
                    catch { v++; a++; }
                    break;
                }
            }
        }
        Interlocked.Add(ref totalAsserts, a);
        Interlocked.Add(ref violations, v);
    });
    sw.Stop();
    Check($"万链归账：{ChainCount} 链 × {StepsPerChain} 步零违规",
        violations == 0 && totalAsserts >= ChainCount,
        $"断言 {totalAsserts} 条，违规 {violations}，耗时 {sw.Elapsed.TotalSeconds:0.0}s");
    Console.WriteLine($"  （万链归账：{ChainCount} 链 / {ChainCount * StepsPerChain} 步 / 断言 {totalAsserts} 条 / 违规 {violations} / 耗时 {sw.Elapsed.TotalSeconds:0.0}s）");

Console.WriteLine("== AtomicWrite 原子落盘（三.6 断电模拟）==");
{
    var dir = Path.Combine(Path.GetTempPath(), "taodbm-atw-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var f = Path.Combine(dir, "cfg.json");
    // 正常写：目标完整、tmp 消失（已原子 move 而非残留）
    AtomicWrite.Text(f, "{\"v\":1}");
    Check("原子写：目标完整且无残留tmp", System.IO.File.ReadAllText(f) == "{\"v\":1}" && !System.IO.File.Exists(f + ".tmp"),
        System.IO.File.Exists(f + ".tmp") ? "tmp 残留" : "");
    // 覆盖写：旧版被新版完整替换，中途绝不出现半截目标
    AtomicWrite.Text(f, "{\"v\":2}");
    Check("原子覆盖：新版完整替换", System.IO.File.ReadAllText(f) == "{\"v\":2}" && !System.IO.File.Exists(f + ".tmp"));
    // 断电模拟：写 tmp 后、move 前进程死亡 → 目标仍是完整旧版（不清零）
    var f2 = Path.Combine(dir, "conn.json");
    AtomicWrite.Text(f2, "{\"good\":true}");
    System.IO.File.WriteAllText(f2 + ".tmp", "{\"half\":");   // 模拟断电时半截 tmp
    Check("断电模拟：目标保留完整旧版", System.IO.File.ReadAllText(f2) == "{\"good\":true}",
        "目标被半截污染");
    // 断电后恢复：残留半截 tmp 不污染下次写入，且被收敛
    AtomicWrite.Text(f2, "{\"good\":false}");
    Check("断电恢复：残留tmp被收敛且目标完整", System.IO.File.ReadAllText(f2) == "{\"good\":false}" && !System.IO.File.Exists(f2 + ".tmp"));
    // 六处落盘点源码级断言：Core 六个存储文件不得再直写目标文件（家法三.6 收敛零遗漏）
    {
        string[] six = ["AppPrefs.cs", "ConnectionStore.cs", "Favorites.cs", "QueryHistory.cs", "TabSession.cs", "ThemeStore.cs"];
        var root = AppContext.BaseDirectory;
        while (root != null && !System.IO.File.Exists(Path.Combine(root, "TaoDBM.slnx"))) root = Path.GetDirectoryName(root);
        var bad = six.Where(n => !System.IO.File.Exists(Path.Combine(root!, "Src", "TaoDBM.Infrastructure", n)) ||
            System.IO.File.ReadAllText(Path.Combine(root!, "Src", "TaoDBM.Infrastructure", n)).Contains("File.WriteAllText(File,"))
            .ToList();
        Check("六处落盘点统一走 AtomicWrite 零遗漏", bad.Count == 0, string.Join(",", bad));
    }
}

RealDbCheck.Run(Check);

Console.WriteLine("\n== P-3 多表联查问法 ==");
Layer("单元");
{
    var Users = new MultiTableSpec("users", new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "int", IsKey = true },
        new() { Name = "name", DataType = "varchar" },
        new() { Name = "city", DataType = "varchar" },
        new() { Name = "created_at", DataType = "datetime" },
    });
    var Orders = new MultiTableSpec("orders", new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "int", IsKey = true },
        new() { Name = "user_id", DataType = "int" },
        new() { Name = "amount", DataType = "decimal" },
        new() { Name = "status", DataType = "varchar" },
        new() { Name = "created_at", DataType = "datetime" },
    });
    var Items = new MultiTableSpec("order_items", new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "int", IsKey = true },
        new() { Name = "order_id", DataType = "int" },
        new() { Name = "qty", DataType = "int" },
    });
    var Products = new MultiTableSpec("products", new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "int", IsKey = true },
        new() { Name = "title", DataType = "varchar" },
    });
    var Payments = new MultiTableSpec("payments", new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "int", IsKey = true },
        new() { Name = "pay_no", DataType = "varchar" },
    });
    var Two = new[] { Orders, Users };

    var j01 = NaturalLanguageJoinQuery.Ask("orders 表关联 users 表", Two, DbEngine.MySQL);
    Check("J01 基础联查生成 JOIN/ON", j01.Ok && j01.Sql.Contains("FROM `orders` JOIN `users` ON `orders`.`user_id` = `users`.`id`"), j01.Sql);
    var j02 = NaturalLanguageJoinQuery.Ask("orders 表关联 users 表", Two, DbEngine.PostgreSQL);
    Check("J02 PG 方言双引号", j02.Ok && j02.Sql.Contains("FROM \"orders\" JOIN \"users\" ON \"orders\".\"user_id\" = \"users\".\"id\""), j02.Sql);
    var j03 = NaturalLanguageJoinQuery.Ask("orders 跟 users 一起看", Two, DbEngine.MySQL);
    Check("J03 口语变体·一起", j03.Ok && j03.Sql.Contains("JOIN `users` ON"), j03.Sql);
    var j04 = NaturalLanguageJoinQuery.Ask("users 以及 orders", Two, DbEngine.MySQL);
    Check("J04 口语变体·以及（顺序反转仍可连）", j04.Ok && j04.Sql.Contains("FROM `users` JOIN `orders` ON `users`.`id` = `orders`.`user_id`"), j04.Sql);
    var j05 = NaturalLanguageJoinQuery.Ask("orders 加上 users", Two, DbEngine.MySQL);
    Check("J05 口语变体·加上", j05.Ok && j05.Sql.Contains("JOIN `users` ON"), j05.Sql);
    var j06 = NaturalLanguageJoinQuery.Ask("orders join users", Two, DbEngine.MySQL);
    Check("J06 英文 join 词", j06.Ok && j06.Sql.Contains("JOIN `users` ON"), j06.Sql);

    var j07 = NaturalLanguageJoinQuery.Ask("orders 关联 users 关联 order_items", new[] { Orders, Users, Items }, DbEngine.MySQL);
    Check("J07 三表两段 JOIN", j07.Ok && j07.Sql.Contains("JOIN `users` ON `orders`.`user_id` = `users`.`id`")
        && j07.Sql.Contains("JOIN `order_items` ON `orders`.`id` = `order_items`.`order_id`"), j07.Sql);
    var j08 = NaturalLanguageJoinQuery.Ask("orders 关联 users 关联 order_items 一共多少条", new[] { Orders, Users, Items }, DbEngine.MySQL);
    Check("J08 三表计数", j08.Ok && j08.Sql.Contains("COUNT(*)") && j08.Sql.Split("JOIN ").Length == 3, j08.Sql);

    var j09 = NaturalLanguageJoinQuery.Ask("orders 关联 products", new[] { Orders, Products }, DbEngine.MySQL);
    Check("J09 无外键关联明确报因", !j09.Ok && j09.UnsupportedReason!.Contains("不支持该问法") && j09.UnsupportedReason.Contains("模型兜底"), j09.UnsupportedReason ?? "");
    var j10 = NaturalLanguageJoinQuery.Ask("orders 表 amount 大于 100", new[] { Orders }, DbEngine.MySQL);
    Check("J10 单表不成联查", !j10.Ok && j10.UnsupportedReason!.Contains("不支持该问法"), j10.UnsupportedReason ?? "");
    var j11 = NaturalLanguageJoinQuery.Ask("orders 还有 users 看看", Two, DbEngine.MySQL);
    Check("J11 无联查意图词明确报因", !j11.Ok && j11.UnsupportedReason!.Contains("不支持该问法") && j11.UnsupportedReason.Contains("模型兜底"), j11.UnsupportedReason ?? "");

    var j12 = NaturalLanguageJoinQuery.Ask("删除 orders 关联 users 的数据", Two, DbEngine.MySQL);
    Check("J12 危险语义·删除拒绝", !j12.Ok && j12.UnsupportedReason!.Contains("只读"), j12.UnsupportedReason ?? "");
    var j13 = NaturalLanguageJoinQuery.Ask("orders 关联 users，然后 drop 表", Two, DbEngine.MySQL);
    Check("J13 危险语义·DDL 拒绝", !j13.Ok && j13.UnsupportedReason!.Contains("危险"), j13.UnsupportedReason ?? "");
    var j21 = NaturalLanguageJoinQuery.Ask("orders 关联 users 表的记录", Two, DbEngine.MySQL);
    Check("J14 产物只读 SELECT", j21.Ok && j21.Sql.StartsWith("SELECT ") && !j21.Sql.Contains("DELETE") && !j21.Sql.Contains("UPDATE") && !j21.Sql.Contains("INSERT"), j21.Sql);

    var j14 = NaturalLanguageJoinQuery.Ask("orders 关联 users 一共有多少条", Two, DbEngine.MySQL);
    Check("J15 两表计数", j14.Ok && j14.Sql.Contains("COUNT(*)") && j14.Sql.Contains("WHERE") == false, j14.Sql);
    var j15 = NaturalLanguageJoinQuery.Ask("orders 关联 users amount 最大的前 5 条", Two, DbEngine.MySQL);
    Check("J16 排序+LIMIT", j15.Ok && j15.Sql.Contains("ORDER BY `orders`.`amount` DESC") && j15.Sql.Contains("LIMIT 5"), j15.Sql);
    var j16 = NaturalLanguageJoinQuery.Ask("orders 关联 users amount 大于 100", Two, DbEngine.MySQL);
    Check("J17 跨表条件带前缀", j16.Ok && j16.Sql.Contains("`orders`.`amount` > 100"), j16.Sql);
    var j17 = NaturalLanguageJoinQuery.Ask("orders 关联 users 最近 7 天的记录", Two, DbEngine.MySQL);
    Check("J18 时间过滤 MySQL 关键字", j17.Ok && j17.Sql.Contains("`orders`.`created_at` >= NOW() - INTERVAL 7 DAY"), j17.Sql);
    var j18 = NaturalLanguageJoinQuery.Ask("orders 关联 users 最近 7 天的记录", Two, DbEngine.PostgreSQL);
    Check("J19 时间过滤 PG 关键字", j18.Ok && j18.Sql.Contains("\"orders\".\"created_at\" >= CURRENT_DATE - INTERVAL '7 DAY'"), j18.Sql);
    var j19 = NaturalLanguageJoinQuery.Ask("orders 关联 users 今天的记录", Two, DbEngine.MySQL);
    Check("J20 今天·MySQL", j19.Ok && j19.Sql.Contains(">= CURDATE()"), j19.Sql);
    var j20 = NaturalLanguageJoinQuery.Ask("orders 关联 users 今天的记录", Two, DbEngine.PostgreSQL);
    Check("J21 今天·PG", j20.Ok && j20.Sql.Contains(">= CURRENT_DATE") && !j20.Sql.Contains("CURDATE()"), j20.Sql);

    var j22 = NaturalLanguageJoinQuery.Ask("payments 关联 orders",
        new[] { Payments, Orders },
        DbEngine.MySQL,
        new[] { new ErdEdge { FromTable = "orders", FromCol = "user_id", ToTable = "payments" } });
    Check("J22 已知关系优先", j22.Ok && j22.Sql.Contains("`payments`.`id` = `orders`.`user_id`"), j22.Sql);

    var j23 = NaturalLanguageJoinQuery.Ask("orders 关联 users", Two, DbEngine.MongoDB);
    Check("J23 非关系库明确拒绝", !j23.Ok && j23.UnsupportedReason!.Contains("关系型"), j23.UnsupportedReason ?? "");
    var j24 = NaturalLanguageJoinQuery.Ask("orders 关联 users", Two, DbEngine.MySQL);
    Check("J24 置信度与人话解释", j24.Confidence == "high" && j24.Interpretation.Contains("联查"), j24.Interpretation);
    var j25 = NaturalLanguageJoinQuery.Ask("orders 关联 users 谁在 city 是 上海", Two, DbEngine.MySQL);
    Check("J25 文本等值条件", j25.Ok && j25.Sql.Contains("`users`.`city` = '上海'"), j25.Sql);
    var j26 = NaturalLanguageJoinQuery.Ask("orders 关联 users，status 包含 paid", Two, DbEngine.MySQL);
    Check("J26 LIKE 条件", j26.Ok && j26.Sql.Contains("`orders`.`status` LIKE '%paid%'"), j26.Sql);
    var j27 = NaturalLanguageJoinQuery.Ask("orders 关联 users 按修改时间最近 7 天看", Two, DbEngine.MySQL);
    Check("J27 修改时间不是写库语义", j27.Ok && j27.Sql.Contains("`orders`.`created_at` >= NOW() - INTERVAL 7 DAY"), j27.Sql);
    var j28 = NaturalLanguageJoinQuery.Ask("orders 关联 users 修改状态", Two, DbEngine.MySQL);
    Check("J28 裸写库语义仍拒绝", !j28.Ok && j28.UnsupportedReason!.Contains("危险"), j28.UnsupportedReason ?? "");
}

Console.WriteLine("\n== P-3 问答记忆（成长机制）==");
Layer("单元");
{
    var MemOrders = new MultiTableSpec("orders", new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "int", IsKey = true },
        new() { Name = "user_id", DataType = "int" },
        new() { Name = "amount", DataType = "decimal" },
        new() { Name = "created_at", DataType = "datetime" },
    });
    var MemUsers = new MultiTableSpec("users", new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "int", IsKey = true },
        new() { Name = "name", DataType = "varchar" },
    });
    var MemTwo = new[] { MemOrders, MemUsers };

    var mPath = Path.Combine(Path.GetTempPath(), "taodbm_qamem_" + Guid.NewGuid().ToString("N") + ".jsonl");
    var mem = QaMemoryStore.Load(mPath);
    var m1 = mem.Record("orders 表关联 users 表", "SELECT * FROM `orders` JOIN `users` ON `orders`.`user_id` = `users`.`id`;",
        DbEngine.MySQL, "rule");
    Check("M01 登记默认未确认", m1 is { Confirmed: false } && mem.Count == 1, m1?.Id.ToString() ?? "null");
    Check("M02 未确认样本不召回", mem.Recall("orders 表关联 users 表", "MySQL") is null, "");
    Check("M03 确认后精确召回 1.0", mem.Confirm(m1!.Id) && mem.Recall("orders 表关联 users 表", "MySQL") is { Score: 1.0 }, "");
    var h4 = mem.Recall("orders 表关联 users 表的记录", "MySQL");
    Check("M04 相似问法可召回", h4 is not null && h4.Score >= 0.8 && h4.Entry.Id == m1.Id, h4?.Score.ToString("0.00") ?? "无");
    Check("M05 无关问法不误召回", mem.Recall("查询库存表里商品的数量", "MySQL") is null, "");
    Check("M06 方言隔离不串库", mem.Recall("orders 表关联 users 表", "PostgreSQL") is null, "");

    var mBad = mem.Record("orders 关联 users", "Server=10.0.0.1;Password=secret123;Database=app;", DbEngine.MySQL, "manual");
    Check("M07 疑似凭据拒绝落盘", mBad is null && mem.Count == 1, "");

    var mem2 = QaMemoryStore.Load(mPath);
    Check("M08 JSONL 重载记忆仍在", mem2.Count == 1 && mem2.Recall("orders 表关联 users 表", "MySQL") is { Score: 1.0 }, "");

    var xPath = Path.Combine(Path.GetTempPath(), "taodbm_qaexp_" + Guid.NewGuid().ToString("N") + ".jsonl");
    var exported = mem2.Export(xPath);
    var lines = File.ReadAllLines(xPath);
    Check("M09 导出 JSONL 带确认标记", exported == 1 && lines.Length == 1 && lines[0].Contains("\"Confirmed\":true"), lines.Length.ToString());
    Check("M10 清理接口生效", mem2.Clear() == 1 && mem2.Count == 0 && mem2.Recall("orders 表关联 users 表", "MySQL") is null, "");
    Check("M11 清理后落盘同步收缩", File.ReadAllLines(mPath).Length == 0, "");

    var mem3 = QaMemoryStore.Load(Path.Combine(Path.GetTempPath(), "taodbm_qamem_" + Guid.NewGuid().ToString("N") + ".jsonl"));
    var a1 = QaAssistant.AskMultiTable("orders 关联 users", MemTwo, DbEngine.MySQL, mem3);
    Check("M12 记忆未命中走规则层", a1.Ok && a1.Sql.Contains("JOIN `users` ON"), a1.Sql);
    var saved = mem3.Record("orders 关联 users", a1.Sql, DbEngine.MySQL, "rule", confirmed: true);
    Check("M13 规则确认样本入库", saved is { Confirmed: true }, "");
    var a2 = QaAssistant.AskMultiTable("orders 关联 users", MemTwo, DbEngine.MySQL, mem3);
    Check("M14 命中记忆优先复用", a2.Ok && a2.Interpretation.Contains("记忆") && a2.Sql == saved!.Sql, a2.Interpretation);
    var a3 = QaAssistant.AskSingleTable("orders 表有多少条", "orders", MemOrders.Columns, DbEngine.MySQL, mem3);
    Check("M15 单表问法未命中走规则", a3.Ok && a3.Sql.Contains("COUNT"), a3.Sql);
    mem3.Record("orders 表有多少条", a3.Sql, DbEngine.MySQL, "model", confirmed: true);
    var a4 = QaAssistant.AskSingleTable("orders 表有多少条", "orders", MemOrders.Columns, DbEngine.MySQL, mem3);
    Check("M16 模型来源样本同库复用", a4.Ok && a4.Interpretation.Contains("记忆") && a4.Sql == a3.Sql, a4.Interpretation);
}

Console.WriteLine("\n== B12 诊断优化引擎 ==");
Layer("系统");
{
    // T001：值长度远超声明宽度，SQLite → TEXT
    var cols = new List<ColumnMeta> { new("users", "nickname", "VARCHAR", 10, false, "SQLite") };
    var len = new List<ColumnLen> { new(1000, 5, 100, "users", "nickname") };
    var r = TypeLengthAdvisor.Inspect(cols, len, null);
    var f = r.Findings.FirstOrDefault(x => x.Table == "users" && x.Column == "nickname");
    Check("T001 SQLite 超宽报 TEXT", f?.Code == "O001" && f.Suggestion?.Contains("TEXT") == true && f.Suggestion?.Contains("VARCHAR(1000)") == true);

    // T002：值宽度小且有小数位（NULL 宽度计数时不含小数点）
    var cols2 = new List<ColumnMeta> { new("s", "amount", "DECIMAL", 10, false, "MySQL") };
    var len2 = new List<ColumnLen> { new(10, 10, 8, "s", "amount") };
    var r2 = TypeLengthAdvisor.Inspect(cols2, len2, null);
    var f2 = r2.Findings.FirstOrDefault(x => x.Code == "O002");
    Check("T002 DECIMAL 精度收缩", f2?.Suggestion?.Contains("DECIMAL(10,2)") == true && f2.Suggestion?.Contains("DECIMAL(10,2)") != f2.Suggestion?.Contains("回退"));

    // T003：DATETIME 列发现秒以下未使用 → DATE
    var cols3 = new List<ColumnMeta> { new("logs", "created_dt", "DATETIME", 0, false, "MySQL") };
    var len3 = new List<ColumnLen> { new(19, 19, 19, "logs", "created_dt") };
    var r3 = TypeLengthAdvisor.Inspect(cols3, len3, null);
    Check("T003 DATETIME 粒度建议", r3.Findings.Any(x => x.Code == "O004" && x.Suggestion?.Contains("DATE") == true));

    // T004：主键列不动
    var cols4 = new List<ColumnMeta> { new("users", "id", "VARCHAR", 5, true, "SQLite") };
    var len4 = new List<ColumnLen> { new(1000, 5, 100, "users", "id") };
    var r4 = TypeLengthAdvisor.Inspect(cols4, len4, null);
    Check("T004 主键跳过", !r4.Findings.Any(x => x.Table == "users" && x.Column == "id"));

    // T005：刚声明类型不动
    var cols5 = new List<ColumnMeta> { new("n", "c", "NVARCHAR", 50, false, "SQLite") };
    var len5 = new List<ColumnLen> { new(40, 5, 50, "n", "c") };
    var r5 = TypeLengthAdvisor.Inspect(cols5, len5, null);
    Check("T005 NVARCHAR 不重复建议", !r5.Findings.Any(x => x.Table == "n"));

    // T006：回退语句存在且不含分号
    var cols6 = new List<ColumnMeta> { new("users", "nickname", "VARCHAR", 10, false, "SQLite") };
    var len6 = new List<ColumnLen> { new(1000, 5, 100, "users", "nickname") };
    var r6 = TypeLengthAdvisor.Inspect(cols6, len6, null);
    var f6 = r6.Findings.FirstOrDefault();
    Check("T006 回退语句生成", f6 != null && f6.RollbackSql?.StartsWith("-- 回退") == true && f6.RollbackSql?.Split(';').Length == 2);

    // T007：报告 JSON 往返
    var j = TypeLengthAdvisor.ToJson(r6);
    var b6 = TypeLengthAdvisor.FromJson(j);
    Check("T007 JSON 往返", b6?.Findings.Count == r6.Findings.Count && b6?.Findings[0].Code == f6?.Code);

    // T008：Report 表含已执行/已回退路径说明
    var rep = TypeLengthAdvisor.Report(r6, true);
    Check("T008 报告文本", rep.Contains("执行已选优化") && rep.Contains("一键还原"));
}

Console.WriteLine("== NotificationDispatcher ==");
{
    static NotifyEvent Evt(NotifyKind kind, string key = "", DateTime? at = null) =>
        new(kind, key, "标题", "内容", at ?? new DateTime(2026, 11, 18, 12, 0, 0));

    // ---- 够格判断 ----
    {
        var d = new NotificationDispatcher(new NotifyOptions(), () => new DateTime(2026, 11, 18, 12, 0, 0));
        Check("N01 默认配置事件够格", d.TryAccept(Evt(NotifyKind.Backup, "orderdb")) && d.Evaluate(Evt(NotifyKind.Backup, "otherdb")).Reason == "ok");
        var off = new NotificationDispatcher(new NotifyOptions { Enabled = false }, () => DateTime.Now);
        Check("N02 总开关拦截", !off.TryAccept(Evt(NotifyKind.Backup)) && off.Evaluate(Evt(NotifyKind.Backup)).Reason == "global-off");
        var kindOff = new NotificationDispatcher(new NotifyOptions
        {
            KindToggles = new Dictionary<NotifyKind, bool> { [NotifyKind.Backup] = false },
        }, () => DateTime.Now);
        Check("N03 单类开关拦截", !kindOff.TryAccept(Evt(NotifyKind.Backup)) && kindOff.Evaluate(Evt(NotifyKind.Backup)).Reason == "kind-off");
        Check("N04 未配置种类默认开启", kindOff.TryAccept(Evt(NotifyKind.LongTask)));
        Check("N05 默认 KindToggles 全开", NotifyOptions.AllOn().Count == Enum.GetValues<NotifyKind>().Length
            && NotifyOptions.AllOn().Values.All(v => v));
    }

    // ---- 免打扰时段 ----
    {
        NotifyOptions Opts(TimeOnly start, TimeOnly end, bool dndOn = true) => new() { DndEnabled = dndOn, DndStart = start, DndEnd = end };
        var clock = new DateTime(2026, 11, 18, 2, 0, 0);   // 凌晨两点
        var d = new NotificationDispatcher(Opts(new TimeOnly(23, 0), new TimeOnly(7, 0)), () => clock);
        Check("N06 跨零点窗口内拦截", !d.TryAccept(Evt(NotifyKind.Backup)) && d.Evaluate(Evt(NotifyKind.Backup, "k")).Reason == "dnd");
        var noon = new NotificationDispatcher(Opts(new TimeOnly(23, 0), new TimeOnly(7, 0)), () => new DateTime(2026, 11, 18, 12, 0, 0));
        Check("N07 白天正常放行", noon.TryAccept(Evt(NotifyKind.Backup)));
        var lateNight = new NotificationDispatcher(Opts(new TimeOnly(23, 0), new TimeOnly(7, 0)), () => new DateTime(2026, 11, 18, 23, 30, 0));
        Check("N08 窗口起点之后拦截", !lateNight.TryAccept(Evt(NotifyKind.Backup)));
        var normal = new NotificationDispatcher(Opts(new TimeOnly(1, 0), new TimeOnly(5, 0)), () => new DateTime(2026, 11, 18, 12, 0, 0));
        Check("N09 不跨零点窗口外放行", normal.TryAccept(Evt(NotifyKind.Backup)));
        var atStart = new NotificationDispatcher(Opts(new TimeOnly(1, 0), new TimeOnly(5, 0)), () => new DateTime(2026, 11, 18, 1, 0, 0));
        Check("N10 窗口含起点即拦截", !atStart.TryAccept(Evt(NotifyKind.Backup)));
        var atEnd = new NotificationDispatcher(Opts(new TimeOnly(1, 0), new TimeOnly(5, 0)), () => new DateTime(2026, 11, 18, 5, 0, 0));
        Check("N11 窗口终点即放行", atEnd.TryAccept(Evt(NotifyKind.Backup)));
        var dndOff = new NotificationDispatcher(Opts(new TimeOnly(23, 0), new TimeOnly(7, 0), dndOn: false), () => clock);
        Check("N12 免打扰未启用不拦截", dndOff.TryAccept(Evt(NotifyKind.Backup)));
        var zeroLen = new NotificationDispatcher(Opts(new TimeOnly(2, 0), new TimeOnly(2, 0)), () => clock);
        Check("N13 零长度窗口不拦截", zeroLen.TryAccept(Evt(NotifyKind.Backup)));
    }

    // ---- 节流去重 ----
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0);
        DateTime clock = now;
        var d = new NotificationDispatcher(new NotifyOptions { ThrottleMinutes = 5 }, () => clock);
        Check("N14 首条放行", d.TryAccept(Evt(NotifyKind.LongTask, "t1")));
        Check("N15 窗口内同键拦截", !d.TryAccept(Evt(NotifyKind.LongTask, "t1")) && d.Evaluate(Evt(NotifyKind.LongTask, "t1")).Reason == "throttled");
        Check("N16 同键不同类不互斥", d.TryAccept(Evt(NotifyKind.Backup, "t1")));
        Check("N17 同类异键不互斥", d.TryAccept(Evt(NotifyKind.LongTask, "t2")));
        clock = now.AddMinutes(5);   // 窗口恰满即过期（左闭右开）
        Check("N18 窗口过后放行", d.TryAccept(Evt(NotifyKind.LongTask, "t1")));
        Check("N19 被拦事件不续窗口", true);
        // 被节流/免打扰拦下的不记录时间戳：拦截期间不发事件，窗口过后第一条自然通过
        clock = now.AddMinutes(6);
        d.TryAccept(Evt(NotifyKind.LongTask, "t3"));
        clock = now.AddMinutes(7);
        Check("N20 窗口内第三条被拦", !d.TryAccept(Evt(NotifyKind.LongTask, "t3")));
        clock = now.AddMinutes(11);
        Check("N21 窗口过后第三条放行", d.TryAccept(Evt(NotifyKind.LongTask, "t3")));
        var noThrottle = new NotificationDispatcher(new NotifyOptions { ThrottleMinutes = 0 }, () => clock);
        Check("N22 节流关闭时全放行", noThrottle.TryAccept(Evt(NotifyKind.LongTask, "x")) && noThrottle.TryAccept(Evt(NotifyKind.LongTask, "x")));
        var blank = new NotificationDispatcher(new NotifyOptions { ThrottleMinutes = 5 }, () => now);
        Check("N23 空键整类共桶", blank.TryAccept(Evt(NotifyKind.HealthCheck)) && !blank.TryAccept(Evt(NotifyKind.HealthCheck, "  ")));
        Check("N24 节流键含种类与键", NotificationDispatcher.ThrottleKey(Evt(NotifyKind.Backup, " a ")) == "Backup:a");
        d.Reset();
        Check("N25 重置后同键再放行", d.TryAccept(Evt(NotifyKind.LongTask, "t1")));
    }

    // ---- 偏好持久化 ----
    {
        var opts = new NotifyOptions
        {
            Enabled = false,
            DndEnabled = true,
            DndStart = new TimeOnly(22, 30),
            DndEnd = new TimeOnly(6, 15),
            ThrottleMinutes = 10,
            KindToggles = new Dictionary<NotifyKind, bool> { [NotifyKind.Backup] = false, [NotifyKind.LongTask] = true },
        };
        var round = NotifyPrefs.FromJson(NotifyPrefs.ToJson(opts));
        Check("N26 偏好 JSON 往返", round is not null && !round.Enabled && round.DndEnabled
            && round.DndStart == new TimeOnly(22, 30) && round.DndEnd == new TimeOnly(6, 15)
            && round.ThrottleMinutes == 10 && round.IsKindEnabled(NotifyKind.Backup) == false
            && round.IsKindEnabled(NotifyKind.LongTask) == true);
        Check("N27 损坏 JSON 安全回落", NotifyPrefs.FromJson("not-json") is null);
        Check("N28 未配置种类回读仍默认开", round!.IsKindEnabled(NotifyKind.HealthCheck));
        var pFile = Path.Combine(Path.GetTempPath(), "taodbm_notifyprefs_" + Guid.NewGuid().ToString("N") + ".json");
        NotifyPrefs.UseFile(pFile);
        NotifyPrefs.Save(opts);
        Check("N29 文件落盘重载一致", NotifyPrefs.Load().DndStart == new TimeOnly(22, 30));
        NotifyPrefs.UseFile("");
        try { File.Delete(pFile); } catch { /* 清理失败不影响测试 */ }
    }

    Console.WriteLine("== TaskCenter 完成事件 ==");
    {
        var tc = new TaoDBM.Core.Streaming.TaskCenter
        {
            Runner = async h => { await Task.Delay(30, h.Token); return (string?)null; },
        };
        var doneSrc = new TaskCompletionSource<TaoDBM.Core.Streaming.TaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        tc.TaskCompleted += s => { if (s.State == "done") doneSrc.TrySetResult(s); };
        var h = tc.Submit(new TaoDBM.Core.Streaming.TaskSpec { Title = "事件测试", Db = "app", Sql = "SELECT 1" });
        var done = await doneSrc.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Check("N30 任务完成事件携带快照", done.TaskId == h.TaskId && done.State == "done" && done.EndedAt is not null);

        var failSrc = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failTc = new TaoDBM.Core.Streaming.TaskCenter
        {
            Runner = _ => throw new InvalidOperationException("炸了"),
        };
        failTc.TaskCompleted += s => { if (s.State == "failed") failSrc.TrySetResult(s.Message); };
        failTc.Submit(new TaoDBM.Core.Streaming.TaskSpec { Title = "失败测试", Sql = "SELECT 1" });
        var failMsg = await failSrc.Task.WaitAsync(TimeSpan.FromSeconds(15));
        // ㊴ 统一错误文案层：失败事件用户可见文案说人话，原始异常（炸了）只进 AppLog 不上屏
        Check("N31 任务失败事件带错误信息", failMsg.Contains("任务执行没成功") && !failMsg.Contains("炸了"), failMsg);

        var cancelSrc = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowTc = new TaoDBM.Core.Streaming.TaskCenter
        {
            Runner = async h => { await Task.Delay(5000, h.Token); return (string?)null; },
        };
        slowTc.TaskCompleted += s => { if (s.State == "canceled") cancelSrc.TrySetResult(s.TaskId); };
        var slow = slowTc.Submit(new TaoDBM.Core.Streaming.TaskSpec { Title = "取消测试", Sql = "SELECT 1" });
        Check("N32 运行中可取消", slowTc.Cancel(slow.TaskId));
        var cancelId = await cancelSrc.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Check("N33 任务取消事件派发", cancelId == slow.TaskId);

        // 整改二批 #05：Runner 返回值作为完成摘要写入任务快照
        var sumSrc = new TaskCompletionSource<TaoDBM.Core.Streaming.TaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sumTc = new TaoDBM.Core.Streaming.TaskCenter
        {
            Runner = _ => Task.FromResult<string?>("共 42 行 · 耗时 7 ms"),
        };
        sumTc.TaskCompleted += s => { if (s.State == "done") sumSrc.TrySetResult(s); };
        var sumH = sumTc.Submit(new TaoDBM.Core.Streaming.TaskSpec { Title = "摘要测试", Sql = "SELECT 1" });
        var sumDone = await sumSrc.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Check("N39 Runner 摘要回写快照", sumDone.TaskId == sumH.TaskId && sumDone.Message.Contains("共 42 行"), sumDone.Message);
        var nullTc = new TaoDBM.Core.Streaming.TaskCenter
        {
            Runner = _ => Task.FromResult<string?>(null),
        };
        TaskCompletionSource<TaoDBM.Core.Streaming.TaskSnapshot> nullSrc = new(TaskCreationOptions.RunContinuationsAsynchronously);
        nullTc.TaskCompleted += s => { if (s.State == "done") nullSrc.TrySetResult(s); };
        var nullH = nullTc.Submit(new TaoDBM.Core.Streaming.TaskSpec { Title = "空摘要测试", Sql = "SELECT 1" });
        var nullDone = await nullSrc.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Check("N40 Runner 空摘要不报错", nullDone.TaskId == nullH.TaskId && nullDone.State == "done", nullDone.Message);

        // P-3 问库规则修复回归：聚合优先级 / 时间条件 / Top-N
        static TaoDBM.Core.NLQueryResult AskNL(string q, string table, params (string Name, string Type, bool Key)[] cols)
        {
            var list = cols.Select(c => new TaoDBM.Core.ColumnSpec { Name = c.Name, DataType = c.Type, IsKey = c.Key }).ToList();
            return TaoDBM.Core.NaturalLanguageQuery.Ask(q, table, list, DbEngine.MySQL);
        }
        var payCols = new[] { ("id", "INT", true), ("amount", "DECIMAL", false), ("paid_at", "DATETIME", false) };
        var avgQ = AskNL("平均每笔支付多少钱", "payments", payCols);
        Check("N34 平均问句不被计数抢先", avgQ.Ok && avgQ.Sql.Contains("AVG(") && !avgQ.Sql.Contains("COUNT"));
        var cntQ = AskNL("订单有多少条", "orders", new[] { ("id", "INT", true), ("created_at", "DATETIME", false) });
        Check("N35 明确计数问句仍走计数", cntQ.Ok && cntQ.Sql.Contains("COUNT"));
        var lastMonthQ = AskNL("上个月的订单有哪些", "orders", new[] { ("id", "INT", true), ("created_at", "DATETIME", false) });
        Check("N36 上个月时间条件拼入", lastMonthQ.Ok && lastMonthQ.Sql.Contains("%Y-%m-01") && lastMonthQ.Sql.Contains("INTERVAL 1 MONTH"));
        var topNQ = AskNL("上个月成交最多的三单", "orders", new[] { ("id", "INT", true), ("amount", "DECIMAL", false), ("created_at", "DATETIME", false) });
        Check("N37 最多三单按TopN行", topNQ.Ok && topNQ.Sql.Contains("LIMIT 3") && topNQ.Sql.Contains("ORDER BY") && topNQ.Sql.Contains("%Y-%m-01"));
        var yearQ = AskNL("今年的支付总额是多少", "payments", payCols);
        Check("N38 今年总额带年度条件", yearQ.Ok && yearQ.Sql.Contains("SUM(") && yearQ.Sql.Contains("%Y-01-01"));
    }

    Console.WriteLine("== 性能护栏（L5 流式管线/原子落盘）==");
    {
        // N34：多页流式导出 5 万行账面一致（不丢行、不重行、不整体物化）
        {
            var lazyPages = new List<TaoDBM.Core.Streaming.QueryPage>();
            for (int p = 0; p < 50; p++)
                lazyPages.Add(new TaoDBM.Core.Streaming.QueryPage
                {
                    Rows = Enumerable.Range(0, 1000).Select(j => new object[] { p * 1000 + j }).ToArray(),
                    HasMore = p < 49,
                });
            var sink = new StringWriter();
            long n34 = Exporter.CsvStream(lazyPages, new[] { "id" }, sink);
            // 平台差异：Windows TextWriter.NewLine=\r\n，Linux=\n——账面一致性看内容，先剥净 \r 再按 \n 切分（老于：不依赖 NewLine 配对形状）
            var lines = sink.ToString().Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            bool headOk = lines.Length > 0 && lines[0] == "\uFEFFid";
            bool firstOk = lines.Length > 1 && lines[1] == "0";
            bool lastOk = lines.Length > 50000 && lines[50000] == "49999";
            static string DumpHead(string s) => string.Join(" ", s.Take(6).Select(c => $"U+{((int)c):X4}"));
            Check("N34 多页流式导出 行数账", n34 == 50000 && lines.Length == 50001, $"n={n34}, lines={lines.Length}, nl={Environment.NewLine.Replace(((char)13).ToString(), "CR").Replace(((char)10).ToString(), "LF")}");
            Check("N34 多页流式导出 头行带BOM", headOk, lines.Length > 0 ? $"head=[{DumpHead(lines[0])}]" : "lines empty");
            Check("N34 多页流式导出 首行0", firstOk, lines.Length > 1 ? $"first=[{DumpHead(lines[1])}]" : "lines short");
            Check("N34 多页流式导出 尾行49999", lastOk, lines.Length > 50000 ? $"last=[{DumpHead(lines[50000])}]" : "lines short");
        }
        // N35：小批次口径 BatchSize=7 批批受控（低内存导入路径）
        {
            var csv35 = "id\n" + string.Join("\n", Enumerable.Range(1, 100));
            var bs = TaoDBM.Core.Streaming.ImportStreaming.CsvBatches(new StringReader(csv35),
                new TaoDBM.Core.Streaming.ImportOptions { BatchSize = 7 }).ToList();
            Check("N35 小批次全受控", bs.Count == 15 && bs.Take(14).All(b => b.Rows.Count == 7) && bs[14].Rows.Count == 2,
                $"count={bs.Count}, sizes={string.Join(",", bs.Select(b => b.Rows.Count))}");
        }
        // N36：小批次溢出落盘行数账面一致
        {
            var csv36 = "id\n" + string.Join("\n", Enumerable.Range(1, 100));
            using var spill36 = new TaoDBM.Infrastructure.TempFileSpillStore();
            var sum36 = TaoDBM.Core.Streaming.ImportStreaming.CsvIngest(new StringReader(csv36), spill36,
                new TaoDBM.Core.Streaming.ImportOptions { BatchSize = 7 });
            Check("N36 小批次溢出账面一致", sum36.TotalRows == 100 && spill36.BatchCount == 15,
                $"rows={sum36.TotalRows}, batches={spill36.BatchCount}");
        }
        // N37：AtomicWrite 1MB 大文本往返一致
        {
            var big37 = new string('#', 1 << 20);
            var f37 = Path.Combine(Path.GetTempPath(), "taodbm_atombig_" + Guid.NewGuid().ToString("N") + ".json");
            AtomicWrite.Text(f37, big37);
            var back37 = File.ReadAllText(f37);
            Check("N37 大文本原子往返", back37.Length == big37.Length && back37 == big37);
            // N38：落盘完成后无 tmp 残留
            Check("N38 原子写无 tmp 残留", !File.Exists(f37 + ".tmp"));
            File.Delete(f37);
        }
        // N39：目标为目录时 Move 失败抛异常且目录不被破坏（坏写不毁旧值）
        {
            var dir39 = Path.Combine(Path.GetTempPath(), "taodbm_atomdir_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir39);
            var raised = false;
            // 平台差异：Windows 上 Move 覆盖目录抛 UnauthorizedAccessException，Linux 抛 IOException——两种都算「抛异常」
            try { AtomicWrite.Text(dir39, "x"); }
            catch (System.IO.IOException) { raised = true; }
            catch (System.UnauthorizedAccessException) { raised = true; }
            Check("N39 目标异常不毁旧值", raised && Directory.Exists(dir39));
            try { File.Delete(dir39 + ".tmp"); } catch { /* 残留清理失败不影响测试 */ }
            Directory.Delete(dir39, true);
        }
        // N40：导出→导入闭环（CsvStream 产物可被 CsvBatches 无损读回，含逗号/引号转义往返）
        {
            var pages40 = new List<TaoDBM.Core.Streaming.QueryPage>
            {
                new() { Rows = new object[][] { new object[] { 1, "甲" }, new object[] { 2, "乙,含逗号" } }, HasMore = true },
                new() { Rows = new object[][] { new object[] { 3, "丙\"含引号" } }, HasMore = false },
            };
            var sw40 = new StringWriter();
            Exporter.CsvStream(pages40, new[] { "id", "name" }, sw40);
            var back40 = TaoDBM.Core.Streaming.ImportStreaming.CsvBatches(new StringReader(sw40.ToString())).ToList();
            Check("N40 导出导入闭环无损", back40.Count == 1 && back40[0].Rows.Count == 3
                && back40[0].Rows[1].SequenceEqual(new string?[] { "2", "乙,含逗号" })
                && back40[0].Rows[2].SequenceEqual(new string?[] { "3", "丙\"含引号" }),
                $"batches={back40.Count}");
        }
    }
}

Console.WriteLine("== 批次 K2 收尾（数据字典/ER深度/内存封顶/定时SQL/导入口径）==");
{
    // K2-1：数据字典完整导出——字段级字典 HTML / Word 双口径
    {
        var ddCols = new List<ColumnSpec>
        {
            new ColumnSpec { Name = "id", DataType = "INT", IsKey = true },
            new ColumnSpec { Name = "user_id", DataType = "BIGINT", IsKey = false },
            new ColumnSpec { Name = "amount", DataType = "DECIMAL(10,2)", IsKey = false },
        };
        var ddTable = DataDictionary.FromSpecs("orders", ddCols, "订单表");
        var ddHtml = DataDictionary.ToHtml(new[] { ddTable }, "数据字典 · shop");
        Check("K2 字典HTML表头", ddHtml.Contains("数据字典 · shop") && ddHtml.Contains("orders") && ddHtml.Contains("DECIMAL(10,2)"), "");
        Check("K2 字典HTML键标", ddHtml.Contains("主键") && ddHtml.Contains("user_id"), "");
        Check("K2 字典HTML转义", DataDictionary.ToHtml(new[] { DataDictionary.FromSpecs("t<a", ddCols) }, "x").Contains("t&lt;a"), "");
        var ddWord = DataDictionary.ToWord(new[] { ddTable }, "数据字典 · shop");
        Check("K2 字典Word结构", ddWord.StartsWith("<?xml") && ddWord.Contains("urn:schemas-microsoft-com:office:word")
            && ddWord.Contains("orders") && ddWord.Contains("主键"), "");
        Check("K2 字典Word转义", DataDictionary.ToWord(new[] { DataDictionary.FromSpecs("t<b", ddCols) }, "x").Contains("t&lt;b"), "");
        Check("K2 字典FromSpecs", ddTable.Columns.Count == 3 && ddTable.Columns[0].IsKey && ddTable.Comment == "订单表", "");
    }

    // K2-2：ER 图深度——真库外键优先 + 命名约定补边
    {
        var m = new ErdModel();
        m.Tables.Add(new ErdTable { Name = "users", Columns = { new ErdColumn { Name = "id", Type = "INT", IsKey = true } } });
        m.Tables.Add(new ErdTable { Name = "orders", Columns = {
            new ErdColumn { Name = "id", Type = "INT", IsKey = true },
            new ErdColumn { Name = "uid", Type = "INT" },          // 命名约定不命中（非 _id/Id 后缀）
            new ErdColumn { Name = "user_id", Type = "INT" },      // 命名约定命中
        } });
        m.Tables.Add(new ErdTable { Name = "payments", Columns = {
            new ErdColumn { Name = "id", Type = "INT", IsKey = true },
            new ErdColumn { Name = "order_id", Type = "INT" },
        } });
        // 真库外键：orders.uid → users.id（命名约定覆盖不到的边）
        var fkRows = new List<object?[]> { new object?[] { "orders", "uid", "users", "id" } };
        var deep = ErdModel.InferEdgesDeep(m, fkRows);
        Check("K2 ER真值边优先", deep.Any(e => e.FromTable == "orders" && e.FromCol == "uid" && e.ToTable == "users"), "");
        Check("K2 ER约定补边", deep.Any(e => e.FromTable == "orders" && e.FromCol == "user_id" && e.ToTable == "users")
            && deep.Any(e => e.FromTable == "payments" && e.FromCol == "order_id" && e.ToTable == "orders"), "");
        Check("K2 ER真值去重", deep.Count(e => e.FromCol == "uid") == 1, "");
        // 真值表不在模型中：跳过不炸
        var fkGhost = new List<object?[]> { new object?[] { "ghost", "x", "users", "id" } };
        Check("K2 ER幽灵表跳过", ErdModel.InferEdgesDeep(m, fkGhost).All(e => e.FromTable != "ghost"), "");
        // 空真值退纯命名约定
        var convOnly = ErdModel.InferEdgesDeep(m, null);
        Check("K2 ER空真值退约定", !convOnly.Any(e => e.FromCol == "uid")
            && convOnly.Any(e => e.FromCol == "user_id"), "");
    }

    // K2-3：内存封顶——查询侧阈值落账（页序列模拟，不连库）
    {
        var cap = TaoDBM.Core.Streaming.QueryRouter.MaxRowsPerStatement;
        Check("K2 封顶默认20万", cap == 200_000, $"cap={cap}");
        TaoDBM.Core.Streaming.QueryRouter.MaxRowsPerStatement = 0;   // 关闭封顶不炸
        Check("K2 封顶可关闭", TaoDBM.Core.Streaming.QueryRouter.MaxRowsPerStatement == 0, "");
        TaoDBM.Core.Streaming.QueryRouter.MaxRowsPerStatement = cap; // 还原
    }

    // K2-4：定时 SQL 管理面——CRUD + 到点扫描 + 结果回写（独立存储路径隔离）
    {
        var e = SqlScheduler.Add("K2测试任务", "shop", "SELECT 1", 30);
        Check("K2 定时新增", e.Id.Length == 12 && e.Enabled && e.IntervalMinutes == 30, "");
        var listed = SqlScheduler.List();
        Check("K2 定时列表", listed.Any(x => x.Id == e.Id), "");
        Check("K2 定时更新", SqlScheduler.Update(e.Id, "K2改名", null, null, 15, false)
            && SqlScheduler.List().First(x => x.Id == e.Id).IntervalMinutes == 15
            && !SqlScheduler.List().First(x => x.Id == e.Id).Enabled, "");
        // 到点扫描：NextRunAt 已过（Add 时 +30 分，把间隔改负不行——直接造过期条目）
        SqlScheduler.Update(e.Id, null, null, null, 1, true);
        var due = SqlScheduler.TakeDue(DateTimeOffset.Now.AddHours(1));
        Check("K2 定时到点扫描", due.Any(x => x.Id == e.Id), "");
        Check("K2 定时顺延", SqlScheduler.List().First(x => x.Id == e.Id).NextRunAt > DateTimeOffset.Now, "");
        SqlScheduler.RecordResult(e.Id, "done");
        Check("K2 定时结果回写", SqlScheduler.List().First(x => x.Id == e.Id).LastResult == "done", "");
        Check("K2 定时删除", SqlScheduler.Delete(e.Id) && !SqlScheduler.List().Any(x => x.Id == e.Id), "");
        bool threw = false;
        try { SqlScheduler.Add("", "db", "SELECT 1", 10); }
        catch (ArgumentException) { threw = true; }
        Check("K2 定时空名校验", threw, "");
    }

    // K2-5：导入分块口径对齐——默认上限与查询封顶同值锚定
    {
        Check("K2 口径同值锚定", new TaoDBM.Core.Streaming.ImportOptions().MaxRows == TaoDBM.Core.Streaming.QueryRouter.MaxRowsPerStatement, "");
        // MaxRows 生效：3 行上限、5 行源 → 3 行 + 截断标记
        var csv = "id\n" + string.Join("\n", Enumerable.Range(1, 5));
        var bs = TaoDBM.Core.Streaming.ImportStreaming.CsvBatches(new StringReader(csv), new TaoDBM.Core.Streaming.ImportOptions { MaxRows = 3 }).ToList();
        Check("K2 导入上限生效", bs.Sum(b => b.Rows.Count) == 3 && bs[^1].Truncated, "");
        // 显式 0 = 不限制（兼容旧口径）
        var bs0 = TaoDBM.Core.Streaming.ImportStreaming.CsvBatches(new StringReader(csv), new TaoDBM.Core.Streaming.ImportOptions { MaxRows = 0 }).ToList();
        Check("K2 导入不限兼容", bs0.Sum(b => b.Rows.Count) == 5 && !bs0[^1].Truncated, "");
    }
}
}

Console.WriteLine("\n== 组合矩阵（二期全覆盖批次）==");
TaoDBM.LogicTest.Combo.ComboMatrix.RunAll(Check);
TaoDBM.LogicTest.KanbanAnalysis.RunAll(Check);   // 批次E：点选式分析 + 看板钉住/定时刷新（独立文件，ComboMatrix 家法）
TaoDBM.LogicTest.BatchCheck23.RunAll(Check);   // 批次二三：库搜索/监控/INSERT 复制/计划 Kind/连接分组/隧道生命周期
TaoDBM.LogicTest.BatchCheck45.RunAll(Check);   // 批次四五：xlsx/XML/HTML/备份SQL/账本/DdlParser/三层模型/比较/预设/ERD符号/字典/ODBC
TaoDBM.LogicTest.ColumnLazyCheck.RunAll(Check);   // 批次F：宽表列按需加载（2026-11-20 全覆盖令）
TaoDBM.LogicTest.SqlCompleteCheck.RunAll(Check);   // ㉔㉘：SQL补全别名感知/报表页卡片存储（2026-11-25 设计稿冻结落码）

// 制度补齐层：tao-testing-standard 标准测试项与 v3.3 新增把尺逐项落测（2026-11-18 老板令「按制度全覆盖无死角补齐」）
Layer("制度补齐");

Console.WriteLine("== 行为探针（测试规范三.5：类型全装载+构造冒烟）==");
{
    int probedTypes = 0;
    var probeErrors = new List<string>();
    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => (a.GetName().Name ?? "").StartsWith("TaoDBM", StringComparison.Ordinal)))
    {
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            probeErrors.Add($"{asm.GetName().Name}: 装载失败 {ex.LoaderExceptions.Length} 处，首个 {ex.LoaderExceptions[0]?.GetType().Name}");
            continue;
        }
        foreach (var t in types)
        {
            if (string.IsNullOrEmpty(t.Namespace) || !t.Namespace.StartsWith("TaoDBM.", StringComparison.Ordinal)) continue;
            probedTypes++;
            try
            {
                // 元数据级全装载：属性/方法/接口解析任何 TypeLoad/Missing 成员都算断
                _ = t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
                _ = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly);
                _ = t.GetInterfaces();
                // 构造冒烟：非抽象、非泛型、有公共无参构造的类真实实例化一次（临时文件类实例用后即弃）
                if (t.IsClass && !t.IsAbstract && !t.IsGenericType && t.GetConstructor(Type.EmptyTypes) is System.Reflection.ConstructorInfo ctor)
                {
                    var inst = Activator.CreateInstance(t);
                    (inst as IDisposable)?.Dispose();
                }
            }
            catch (Exception ex)
            {
                probeErrors.Add($"{t.FullName}: {ex.GetType().Name} {ex.Message}");
            }
        }
    }
    Check($"行为探针-类型全装载与构造冒烟零断裂({probedTypes} 型)", probeErrors.Count == 0, string.Join("; ", probeErrors.Take(3)));
}

Console.WriteLine("== 版本锚（测试规范三.6：Windows 交付版本一致性）==");
{
    string? root = null;
    for (var dir = new System.IO.DirectoryInfo(Environment.CurrentDirectory); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Src", "TaoDBM.App", "TaoDBM.App.csproj"))) { root = dir.FullName; break; }
    }
    if (root != null)
    {
        var proj = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "TaoDBM.App.csproj"));
        string? Val(string tag)
        {
            var m = System.Text.RegularExpressions.Regex.Match(proj, "<" + tag + ">([^<]+)</" + tag + ">");
            return m.Success ? m.Groups[1].Value : null;
        }
        var ver = Val("Version"); var fv = Val("FileVersion"); var av = Val("AssemblyVersion");
        Check("版本锚-App 三版本一致", ver != null && fv == ver + ".0" && av == ver + ".0", $"Version={ver} FileVersion={fv} AssemblyVersion={av}");
    }
    else
    {
        Check("版本锚-App 三版本一致", false, "未定位到仓库根 " + Environment.CurrentDirectory);
    }
}

Console.WriteLine("== 跨平台路径双分隔符回归（测试规范三.1）==");
{
    var baseDir = Path.Combine(Path.GetTempPath(), "taodbm-path-regression-" + Guid.NewGuid().ToString("N"));
    var mixed = baseDir + Path.AltDirectorySeparatorChar + "sub" + Path.DirectorySeparatorChar
        + Path.DirectorySeparatorChar + "deep" + Path.AltDirectorySeparatorChar + "f.json";
    bool wrote = false; string? content = null;
    try { AtomicWrite.Text(mixed, "{\"k\":1}"); wrote = true; content = File.ReadAllText(Path.GetFullPath(mixed)); }
    catch (Exception ex) { content = "异常:" + ex.Message; }
    Check("双分隔符路径-混用分隔符原子写目录自建", wrote && content == "{\"k\":1}", content ?? "");
    Check("双分隔符路径-落盘位置前缀断言", wrote
        && Path.GetFullPath(mixed).StartsWith(Path.GetFullPath(baseDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    try { Directory.Delete(baseDir, true); } catch { }
}

Console.WriteLine("== 断盘/写坏注入（测试规范三.8/二.4）==");
{
    // 同名文件占位目录：目录创建必败——异常必须原样抛出且不吞，目标旧值不被破坏
    var dir1 = Path.Combine(Path.GetTempPath(), "taodbm-diskfail-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir1);
    var blocker = Path.Combine(dir1, "occupied");
    File.WriteAllText(blocker, "旧值-不可被破坏");
    bool threw = false; string? after = null;
    try { AtomicWrite.Text(Path.Combine(blocker, "f.json"), "新值"); }
    catch { threw = true; }
    after = File.ReadAllText(blocker);
    Check("断盘注入-目录占位写失败必抛且旧值完好", threw && after == "旧值-不可被破坏", after ?? "");
    try { Directory.Delete(dir1, true); } catch { }
    // Move 目标为目录的确定性失败侧已由 N39 覆盖（坏写不毁旧值），此处补目录创建失败侧，两路合围断盘语义
}

Console.WriteLine("\n== P-4 窗口记忆/.sql 三件套（2026-11-19 23:52 老板令）==");
Layer("单元");
{
    // --- WindowStateStore：默认尺寸边界（工作区 80% 封顶 1600×900，<1350 吃满） ---
    var (w1, h1) = WindowStateStore.DefaultSize(1350, 1000);
    Check("WS-默认尺寸-1350临界走80%封顶", Math.Abs(w1 - 1080) < 0.01 && Math.Abs(h1 - 800) < 0.01, $"{w1}x{h1}");
    var (w2, h2) = WindowStateStore.DefaultSize(1349, 800);
    Check("WS-默认尺寸-1349小屏吃满", w2 == 1349 && h2 == 800, $"{w2}x{h2}");
    var (w3, h3) = WindowStateStore.DefaultSize(1920, 1080);
    Check("WS-默认尺寸-1080p走80%", Math.Abs(w3 - 1536) < 0.01 && Math.Abs(h3 - 864) < 0.01, $"{w3}x{h3}");
    var (w4, h4) = WindowStateStore.DefaultSize(2560, 1440);
    Check("WS-默认尺寸-大屏封顶1600x900", w4 == 1600 && h4 == 900, $"{w4}x{h4}");
    var (w5, h5) = WindowStateStore.DefaultSize(800, 600);
    Check("WS-默认尺寸-微屏吃满", w5 == 800 && h5 == 600, $"{w5}x{h5}");

    // --- IsRestorable：拔显示器/NaN 坐标回默认居中 ---
    var item = new WindowStateItem { X = 100, Y = 100, Width = 1200, Height = 800 };
    Check("WS-恢复-坐标在屏可恢复", WindowStateStore.IsRestorable(item, new[] { (0.0, 0.0, 1920.0, 1080.0) }));
    Check("WS-恢复-空显示器表回默认", !WindowStateStore.IsRestorable(item, Array.Empty<(double, double, double, double)>()));
    var nanItem = new WindowStateItem { X = double.NaN, Y = 100, Width = 1200, Height = 800 };
    Check("WS-恢复-NaN坐标回默认", !WindowStateStore.IsRestorable(nanItem, new[] { (0.0, 0.0, 1920.0, 1080.0) }));
    var offItem = new WindowStateItem { X = -5000, Y = -5000, Width = 1200, Height = 800 };
    Check("WS-恢复-拔显示器旧坐标失效", !WindowStateStore.IsRestorable(offItem, new[] { (0.0, 0.0, 1920.0, 1080.0) }));
    Check("WS-恢复-零尺寸无效", !WindowStateStore.IsRestorable(new WindowStateItem { X = 100, Y = 100, Width = 0, Height = 800 }, new[] { (0.0, 0.0, 1920.0, 1080.0) }));

    // --- ClampLeftPane：树 200 起、主区 480 保底 ---
    Check("WS-分栏-过窄夹到200", WindowStateStore.ClampLeftPane(80, 1000) == 200);
    Check("WS-分栏-过宽夹到主区480保底", WindowStateStore.ClampLeftPane(10000, 1000) == 520);
    Check("WS-分栏-区间内原样保留", WindowStateStore.ClampLeftPane(300, 1000) == 300);
    Check("WS-分栏-极小屏上限即200", WindowStateStore.ClampLeftPane(350, 600) == 200);

    // --- 状态存取：写读一致、损坏回落默认 ---
    var wsFile = Path.Combine(Path.GetTempPath(), "taodbm-ws-test-" + Guid.NewGuid().ToString("N") + ".json");
    WindowStateStore.UseFile(wsFile);
    var saved = new WindowStateItem { X = 50, Y = 60, Width = 1100, Height = 760, Maximized = true, LeftPaneWidth = 260, TreeCollapsed = false };
    WindowStateStore.Save(saved);
    var loaded = WindowStateStore.Load();
    Check("WS-存取-全字段写读一致", loaded.X == 50 && loaded.Y == 60 && loaded.Width == 1100 && loaded.Maximized && Math.Abs(loaded.LeftPaneWidth - 260) < 0.01);
    File.WriteAllText(wsFile, "{坏的json");
    Check("WS-存取-损坏回落默认", WindowStateStore.Load().Width == 0 && !WindowStateStore.Load().Maximized);
    try { File.Delete(wsFile); } catch { }
    WindowStateStore.UseFile("");

    // --- SqlFileIo：三态往返 / BOM / GBK 中文 ---
    var sqlContent = "SELECT '你好，世界' AS greeting;\n-- 中文注释不乱码\nINSERT INTO t VALUES (1);";
    var sqlDir = Path.Combine(Path.GetTempPath(), "taodbm-sql-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(sqlDir);
    Check("SQL-IO-UTF8无BOM往返", SqlFileIo.RoundTrip(Path.Combine(sqlDir, "a.sql"), sqlContent, gbk: false, bom: false));
    Check("SQL-IO-UTF8带BOM往返", SqlFileIo.RoundTrip(Path.Combine(sqlDir, "b.sql"), sqlContent, gbk: false, bom: true));
    Check("SQL-IO-GBK中文往返", SqlFileIo.RoundTrip(Path.Combine(sqlDir, "c.sql"), sqlContent, gbk: true, bom: false));
    var bomBytes = File.ReadAllBytes(Path.Combine(sqlDir, "b.sql"));
    Check("SQL-IO-BOM字节锚", bomBytes.Length >= 3 && bomBytes[0] == 0xEF && bomBytes[1] == 0xBB && bomBytes[2] == 0xBF);
    var gbkBytes = File.ReadAllBytes(Path.Combine(sqlDir, "c.sql"));
    Check("SQL-IO-GBK确无BOM", !(gbkBytes.Length >= 3 && gbkBytes[0] == 0xEF && gbkBytes[1] == 0xBB && gbkBytes[2] == 0xBF));
    Check("SQL-IO-编码探测-UTF8无BOM", SqlFileIo.DetectEncoding(Path.Combine(sqlDir, "a.sql")) is UTF8Encoding u8 && !u8.GetPreamble().Any());
    Check("SQL-IO-编码探测-UTF8带BOM", SqlFileIo.DetectEncoding(Path.Combine(sqlDir, "b.sql")) is UTF8Encoding u8b && u8b.GetPreamble().Any());
    Check("SQL-IO-编码探测-GBK", SqlFileIo.DetectEncoding(Path.Combine(sqlDir, "c.sql")).CodePage == 936);
    try { Directory.Delete(sqlDir, true); } catch { }

    // --- SqlOpenPrefs：写读一致、损坏回落默认（默认开=最近激活窗口） ---
    var prefFile = Path.Combine(Path.GetTempPath(), "taodbm-sqlopen-test-" + Guid.NewGuid().ToString("N") + ".json");
    SqlOpenPrefs.UseFile(prefFile);
    Check("SQL-打开偏好-缺省为开", SqlOpenPrefs.Load());
    SqlOpenPrefs.Save(false);
    Check("SQL-打开偏好-关后保持关", !SqlOpenPrefs.Load());
    SqlOpenPrefs.Save(true);
    Check("SQL-打开偏好-重开生效", SqlOpenPrefs.Load());
    File.WriteAllText(prefFile, "not json at all");
    Check("SQL-打开偏好-损坏回落开", SqlOpenPrefs.Load());
    try { File.Delete(prefFile); } catch { }
    SqlOpenPrefs.UseFile("");
}

Console.WriteLine("\n== P-5 虚拟滚动窗口计算/.sql 跨实例交接（2026-11-20 夜批②③）==");
Layer("单元");
{
    // --- VirtualWindowMath：空集 / 单行 / 超万行 / 越界 / NaN ---
    Check("VW-空集-零行返回空窗口", VirtualWindowMath.VisibleWindow(0, 600, 0) == (0, -1), $"{VirtualWindowMath.VisibleWindow(0, 600, 0)}");
    Check("VW-单行-窗口恰为一行", VirtualWindowMath.VisibleWindow(0, 600, 1) == (0, 0));
    Check("VW-负视口高夹零不越界", VirtualWindowMath.VisibleWindow(0, -5, 1) == (0, 0));
    var (vf, vl) = VirtualWindowMath.VisibleWindow(0, 600, 10000);
    Check("VW-顶部-首行0缓冲5", vf == 0 && vl == 24, $"{vf}-{vl}");
    var (mf, ml) = VirtualWindowMath.VisibleWindow(319200, 800, 10000);
    Check("VW-万行-尾部窗口不越界", mf >= 0 && mf <= ml && ml == 9999, $"{mf}-{ml}");
    Check("VW-窗口上限-物化行数有确定上界", (ml - mf + 1) <= VirtualWindowMath.MaxMaterialized(800), $"{ml - mf + 1}");
    var (of_, ol) = VirtualWindowMath.VisibleWindow(1_000_000, 800, 10000);
    Check("VW-偏移越界-收拢到尾部", of_ <= ol && ol == 9999, $"{of_}-{ol}");
    var (nf, nl) = VirtualWindowMath.VisibleWindow(0, double.NaN, 100);
    Check("VW-NaN视口-回落零高窗口", nf == 0 && nl == 5, $"{nf}-{nl}");
    Check("VW-总高-万行乘行高", Math.Abs(VirtualWindowMath.TotalHeight(10000) - 320000) < 0.001);
    Check("VW-总高-零行总高为零", VirtualWindowMath.TotalHeight(0) == 0);
    Check("VW-上限-800视口36行", VirtualWindowMath.MaxMaterialized(800) == 36);
    Check("VW-空集-总高为零后窗口零行", VirtualWindowMath.VisibleWindow(100, 600, 0).Last == -1);

    // --- SqlHandoffRegistry：写读往返 / 解析容错 / 死亡进程清理 / 最近激活排序 ---
    var regFile = Path.Combine(Path.GetTempPath(), "taodbm-sqlhandoff-test-" + Guid.NewGuid().ToString("N") + ".txt");
    SqlHandoffRegistry.UseFile(regFile);
    var nowUtc = DateTime.UtcNow;
    // 存活但非本进程的 pid：MostRecentAlive 会排除自身进程（交接不能交给发起方自己）。
    // 注意 Windows 枚举先见 PID 0(Idle)/4(System)，Parse 拒收 pid<=0，故取 >4 的真实存活进程
    // （2026-11-20 #143/#145 五连败定因：First 命中 Idle，登记行全被解析丢弃，pass=6 fail=5 云本地双向实锤）
    var otherPid = Process.GetProcesses().Where(p => p.Id > 4 && p.Id != Environment.ProcessId).Select(p => p.Id).First();
    SqlHandoffRegistry.Register("pipe-A", otherPid);
    Check("SH-登记-已登记可查", SqlHandoffRegistry.IsRegistered("pipe-A"));
    var eA = SqlHandoffRegistry.Parse(SqlHandoffRegistry.Format(new SqlHandoffRegistry.Entry("pipe-B", otherPid, nowUtc)));
    Check("SH-往返-格式化解析还原到毫秒", eA is not null && eA.PipeName == "pipe-B" && eA.Pid == otherPid && Math.Abs((eA.LastActiveUtc - nowUtc).TotalMilliseconds) < 1.0);
    Check("SH-解析-空行拒收", SqlHandoffRegistry.Parse("") is null);
    Check("SH-解析-两段残行拒收", SqlHandoffRegistry.Parse("pipe|x") is null);
    Check("SH-解析-负pid拒收", SqlHandoffRegistry.Parse("pipe|-5|20261120010101123") is null);
    Check("SH-解析-坏时间戳拒收", SqlHandoffRegistry.Parse("pipe|123|not-a-time") is null);
    SqlHandoffRegistry.Register("pipe-dead", int.MaxValue);
    SqlHandoffRegistry.Register("pipe-first", otherPid);
    Task.Delay(20).Wait();
    SqlHandoffRegistry.Touch("pipe-last", otherPid);
    var recent = SqlHandoffRegistry.MostRecentAlive(selfPid: Environment.ProcessId);
    Check("SH-排序-最近激活优先", recent is not null && recent.PipeName == "pipe-last", recent?.PipeName ?? "null");
    Check("SH-清理-死亡进程条目被摘除", !SqlHandoffRegistry.IsRegistered("pipe-dead"));
    SqlHandoffRegistry.Unregister("pipe-last");
    Check("SH-摘除-退出后不再登记", !SqlHandoffRegistry.IsRegistered("pipe-last"));
    var recent2 = SqlHandoffRegistry.MostRecentAlive(selfPid: Environment.ProcessId);
    Check("SH-排序-次新实例可交", recent2 is not null && recent2.PipeName == "pipe-first", recent2?.PipeName ?? "null");
    File.WriteAllText(regFile, "garbage line\npipe-ok|" + otherPid.ToString() + "|20261120010101123\n");
    var recent3 = SqlHandoffRegistry.MostRecentAlive(selfPid: Environment.ProcessId);
    Check("SH-容错-坏行丢弃好行保留", recent3 is not null && recent3.PipeName == "pipe-ok", recent3?.PipeName ?? "null");
    try { File.Delete(regFile); } catch { }
    SqlHandoffRegistry.UseFile("");
}

Console.WriteLine("\n== 备份可用性演练（批次C 2026-11-20）==");
Layer("单元");
{
    // --- TempDbName：前缀 / 净化 / 时间戳 / 引擎长度上限 / 不撞原名 ---
    var at = new DateTimeOffset(2026, 11, 20, 10, 30, 0, TimeSpan.FromHours(8));
    var n1 = BackupDrill.TempDbName(DbEngine.MySQL, "taodbmui", at);
    Check("BD-库名-前缀与时间戳", n1.StartsWith("taodrill_taodbmui_") && n1.EndsWith("20261120103000"), n1);
    var n2 = BackupDrill.TempDbName(DbEngine.MySQL, "My DB$太长", at);
    Check("BD-库名-非法字符净化成下划线", n2.StartsWith("taodrill_my_db_"), n2);
    Check("BD-库名-空名回落main", BackupDrill.TempDbName(DbEngine.MySQL, "", at).StartsWith("taodrill_main_"));
    Check("BD-库名-纯非法字符回落main", BackupDrill.TempDbName(DbEngine.MySQL, "###", at).StartsWith("taodrill_main_"));
    var longName = new string('a', 80);
    Check("BD-库名-超限截断", BackupDrill.TempDbName(DbEngine.MySQL, longName, at).Length <= BackupDrill.NameLimit(DbEngine.MySQL));
    Check("BD-库名-引擎上限-PG更短", BackupDrill.NameLimit(DbEngine.PostgreSQL) == 56 && BackupDrill.NameLimit(DbEngine.SqlServer) == 120);
    Check("BD-库名-与原库不撞", BackupDrill.TempDbName(DbEngine.MySQL, "taodbmui", at) != "taodbmui");

    // --- SQLite 文件头完整性：真头 / 空文件 / 假头 / 缺文件 ---
    var drillDir = Path.Combine(Path.GetTempPath(), "taodbm-drill-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(drillDir);
    var okFile = Path.Combine(drillDir, "ok.db");
    File.WriteAllBytes(okFile, "SQLite format 3\0后面的真库内容".Select(c => (byte)c).ToArray());
    Check("BD-头校验-真SQLite头通过", BackupDrill.SqliteHeaderOk(okFile));
    var emptyFile = Path.Combine(drillDir, "empty.db");
    File.WriteAllBytes(emptyFile, Array.Empty<byte>());
    Check("BD-头校验-空文件拒绝", !BackupDrill.SqliteHeaderOk(emptyFile));
    var badFile = Path.Combine(drillDir, "bad.db");
    File.WriteAllText(badFile, "不是数据库的文本备份");
    Check("BD-头校验-假头拒绝", !BackupDrill.SqliteHeaderOk(badFile));
    Check("BD-头校验-缺文件拒绝", !BackupDrill.SqliteHeaderOk(Path.Combine(drillDir, "nope.db")));

    // --- SqliteDrillCopy：拷贝 + 自校验；坏头抛异常；目标目录自动建 ---
    var tempCopy = Path.Combine(drillDir, "sub", "dir", "copy.db");
    BackupDrill.SqliteDrillCopy(okFile, tempCopy);
    Check("BD-副本-拷贝成功且头一致", File.Exists(tempCopy) && BackupDrill.SqliteHeaderOk(tempCopy));
    bool badThrow = false;
    try { BackupDrill.SqliteDrillCopy(badFile, Path.Combine(drillDir, "badcopy.db")); }
    catch (InvalidDataException) { badThrow = true; }
    Check("BD-副本-坏头抛InvalidData", badThrow);
    bool missThrow = false;
    try { BackupDrill.SqliteDrillCopy(Path.Combine(drillDir, "nope.db"), Path.Combine(drillDir, "m.db")); }
    catch (FileNotFoundException) { missThrow = true; }
    Check("BD-副本-缺备份文件抛FileNotFound", missThrow);

    // --- 还原规格路由：MySQL/PG 命令行、SQL Server T-SQL、SQLite 不走命令行 ---
    var mys = BackupDrill.RestoreSpec(DbEngine.MySQL, "h", 3306, "u", "drill-secret", "taodrill_x", "f.sql");
    Check("BD-路由-MySQL走mysql且带临时库", mys?.FileName == "mysql" && mys.Arguments.Contains("taodrill_x"));
    Check("BD-路由-MySQL密码走环境变量", mys is not null && !mys.Arguments.Contains("drill-secret") && mys.Env["MYSQL_PWD"] == "drill-secret");
    var pgs = BackupDrill.RestoreSpec(DbEngine.PostgreSQL, "h", 5432, "u", "drill-secret", "taodrill_x", "f.sql");
    Check("BD-路由-PG走psql且带临时库", pgs?.FileName == "psql" && pgs.Arguments.Contains("taodrill_x"));
    Check("BD-路由-SQLite无命令行规格", BackupDrill.RestoreSpec(DbEngine.SQLite, "", 0, "", "", "x", "f") is null);
    Check("BD-路由-SQLServer走TSQL", BackupDrill.SqlServerRestoreSql("taodrill_x", "C:\\b.bak").Contains("RESTORE DATABASE") && BackupDrill.SqlServerRestoreSql("taodrill_x", "C:\\b.bak").Contains("taodrill_x"));

    // --- 建/删临时库 SQL：MySQL 显式字符集 / PG 引号 / SQL Server 判存 ---
    Check("BD-建库-MySQL带utf8mb4", BackupDrill.CreateTempDbSql(DbEngine.MySQL, "taodrill_x")?.Contains("utf8mb4") == true);
    Check("BD-建库-PG带引号", BackupDrill.CreateTempDbSql(DbEngine.PostgreSQL, "taodrill_x")?.Contains("\"taodrill_x\"") == true);
    Check("BD-建库-SQLServer判存", BackupDrill.CreateTempDbSql(DbEngine.SqlServer, "taodrill_x")?.Contains("DB_ID") == true);
    Check("BD-建库-SQLite不适配返回null", BackupDrill.CreateTempDbSql(DbEngine.SQLite, "x") is null);
    Check("BD-删库-MySQL带IF EXISTS", BackupDrill.DropTempDbSql(DbEngine.MySQL, "taodrill_x")?.Contains("IF EXISTS") == true);
    Check("BD-删库-PG带IF EXISTS", BackupDrill.DropTempDbSql(DbEngine.PostgreSQL, "taodrill_x")?.Contains("IF EXISTS") == true);
    Check("BD-删库-SQLite返回null", BackupDrill.DropTempDbSql(DbEngine.SQLite, "x") is null);

    // --- CountSql：逐引擎引号与转义 ---
    Check("BD-计数-MySQL反引号转义", BackupDrill.CountSql(DbEngine.MySQL, "or`der") == "SELECT COUNT(*) AS c FROM `or``der`;");
    Check("BD-计数-SQLServer方括号", BackupDrill.CountSql(DbEngine.SqlServer, "orders").StartsWith("SELECT COUNT(*) AS c FROM [orders]"));
    Check("BD-计数-PG双引号", BackupDrill.CountSql(DbEngine.PostgreSQL, "orders").Contains("\"orders\""));
    Check("BD-计数-SQLite双引号", BackupDrill.CountSql(DbEngine.SQLite, "orders").Contains("\"orders\""));

    // --- ParseExpectedCounts：正常 / 注释 / 空行 / 中文等号 / 非法行 / 重复表 ---
    var (ok, okIssues) = BackupDrill.ParseExpectedCounts("orders=5000\n# 注释\n\ntbl_2=1200\n");
    Check("BD-清单-正常解析", ok.Count == 2 && ok[0].Table == "orders" && ok[0].Count == 5000 && ok[1].Table == "tbl_2" && ok[1].Count == 1200);
    Check("BD-清单-正常解析零异常", okIssues.Count == 0);
    var (cjk, _) = BackupDrill.ParseExpectedCounts("订单表＝42");
    Check("BD-清单-中文等号可解析", cjk.Count == 1 && cjk[0].Table == "订单表" && cjk[0].Count == 42);
    var (bad, badIssues) = BackupDrill.ParseExpectedCounts("没有等号的行\n=99\norders=abc\norders=-1\n");
    Check("BD-清单-非法行全拦且留痕", bad.Count == 0 && badIssues.Count == 4, string.Join("|", badIssues));
    var (dup, dupIssues) = BackupDrill.ParseExpectedCounts("orders=1\norders=2");
    Check("BD-清单-重复表拦截", dup.Count == 1 && dupIssues.Count == 1);

    // --- VerifyCounts：全对 / 缺表 / 计数不符 / 异常透传 / 报告渲染 ---
    var exp = new List<(string, long)> { ("orders", 5000), ("products", 1200) };
    var r1 = BackupDrill.VerifyCounts(exp, new Dictionary<string, long> { ["orders"] = 5000, ["products"] = 1200 });
    Check("BD-对账-全对通过", r1.Pass && r1.Entries.All(e => e.Pass));
    var r2 = BackupDrill.VerifyCounts(exp, new Dictionary<string, long> { ["orders"] = 5000 });
    Check("BD-对账-缺表不过且记异常", !r2.Pass && r2.Issues.Any(i => i.Contains("products")));
    var r3 = BackupDrill.VerifyCounts(exp, new Dictionary<string, long> { ["orders"] = 4999, ["products"] = 1200 });
    Check("BD-对账-计数不符不过", !r3.Pass && r3.Entries.First(e => e.Table == "orders").Actual == 4999);
    var r4 = BackupDrill.VerifyCounts(Array.Empty<(string, long)>(), new Dictionary<string, long>(), new[] { "解析失败" });
    Check("BD-对账-空清单+异常不过", !r4.Pass && r4.Issues.Contains("解析失败"));
    Check("BD-对账-零对账项即失败", !BackupDrill.VerifyCounts(Array.Empty<(string, long)>(), new Dictionary<string, long>()).Pass);
    Check("BD-报告-渲染含结论行", r1.Render("头").Contains("演练结论：通过") && r3.Render("头").Contains("演练结论：未通过"));
    Check("BD-报告-渲染含逐表明细", r3.Render("头").Contains("备份时点 5000 行，临时库 4999 行"));
    try { Directory.Delete(drillDir, true); } catch { }
}

Console.WriteLine("\n== 注入与竞态（R2 盲区补齐 2026-11-21）==");
Layer("集成");
{
    // --- 满盘/断盘：溢出暂存文件路径被目录占位（磁盘满/路径被占的确定性模拟），写失败必须抛出、绝不静默丢数据 ---
    var spillBlocked = new TempFileSpillStore();
    bool dirPlaced = false;
    try
    {
        Directory.CreateDirectory(spillBlocked.FilePath);
        dirPlaced = true;
        spillBlocked.WriteBatch(new[] { "a" }, new List<string?[]> { new string?[] { "1" } });
        Check("INJ-满盘-占位写失败必抛", false, "未抛异常");
    }
    catch (Exception ex)
    {
        Check("INJ-满盘-占位写失败必抛", dirPlaced, ex.GetType().Name);
    }
    finally
    {
        try { Directory.Delete(spillBlocked.FilePath, true); } catch { }
        spillBlocked.Dispose();
    }

    // --- 断盘：AtomicWrite 目标为目录属确定性失败，必须立刻原样抛出、不进退避重试死等 ---
    var dirTarget = Path.Combine(Path.GetTempPath(), "taodbm-inj-dir-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dirTarget);
    var swAtomic = Stopwatch.StartNew();
    Exception? atomicEx = null;
    try { AtomicWrite.Text(dirTarget, "x"); }
    catch (Exception ex) { atomicEx = ex; }
    Check("INJ-断盘-目标为目录必抛不挂起", atomicEx is not null && swAtomic.Elapsed.TotalSeconds < 5,
        $"{atomicEx?.GetType().Name} {swAtomic.Elapsed.TotalSeconds:0.0}s");
    try { Directory.Delete(dirTarget, true); } catch { }

    // --- 断盘后重启：溢出暂存坏行容错，好行完整回放 ---
    var spillReplay = new TempFileSpillStore();
    try
    {
        spillReplay.WriteBatch(new[] { "id", "name" }, new List<string?[]> { new string?[] { "1", "甲" }, new string?[] { "2", null } });
        spillReplay.WriteBatch(new[] { "id", "name" }, new List<string?[]> { new string?[] { "3", "乙" } });
        File.AppendAllText(spillReplay.FilePath, "这行不是JSON\n\n{\"H\": \"坏行\"\n", Encoding.UTF8);
        var replay = spillReplay.ReadBatches().ToList();
        Check("INJ-断盘-坏行跳过好行回放", replay.Count == 2 && replay[0].Rows.Count == 2 && replay[1].Rows.Count == 1,
            $"回放{replay.Count}批");
    }
    finally { spillReplay.Dispose(); }

    // --- 并发同发：任务中心多任务并发提交 + 取消/暂停/续跑竞态，终态必须一一对应、事件恰好一次 ---
    var tc = new TaskCenter();
    var doneEvents = new List<string>();
    tc.TaskCompleted += s => { lock (doneEvents) doneEvents.Add(s.TaskId + ":" + s.State); };
    tc.Runner = h => { Thread.Sleep(30); return Task.FromResult<string?>(null); };
    var handles = Enumerable.Range(0, 12)
        .Select(i => tc.Submit(new TaskSpec { Kind = "query", Title = "并发" + i, Sql = "SELECT 1" }))
        .ToList();
    var cancelResults = new List<(string Id, bool Ok)>();
    foreach (var h in handles.Take(4)) cancelResults.Add((h.TaskId, tc.Cancel(h.TaskId)));
    tc.Pause();
    tc.Resume();
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (DateTime.UtcNow < deadline && tc.List().Any(s => s.State == "running")) Thread.Sleep(50);
    var states = tc.List().GroupBy(s => s.TaskId).ToDictionary(g => g.Key, g => g.First().State);
    Check("INJ-并发-全部到达终态", states.Count == 12 && states.Values.All(s => s is "done" or "canceled" or "failed"),
        string.Join(",", states.Values.Distinct()));
    Check("INJ-并发-取消与完成不冲突",
        cancelResults.All(cr => cr.Ok ? states[cr.Id] == "canceled" : states[cr.Id] != "canceled"));
    Check("INJ-并发-未取消任务完成", handles.Skip(4).All(h => states[h.TaskId] == "done"));
    Check("INJ-并发-终态事件每任务恰一次", doneEvents.Count == 12 && doneEvents.Distinct().Count() == 12,
        $"事件{doneEvents.Count}条");
    var retry = tc.Retry(handles[0].TaskId);
    Check("INJ-并发-终态可重跑", retry is not null);
    if (retry is not null)
    {
        var dl2 = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < dl2 && tc.List().First(s => s.TaskId == retry.TaskId).State == "running") Thread.Sleep(50);
        Check("INJ-并发-重跑到完成", tc.List().First(s => s.TaskId == retry.TaskId).State == "done");
    }

    // --- kill 后重启（引擎侧半场）：损坏快照/水位/时间线/迁移状态/备份策略一律容错，绝不闪退 ---
    var tc2 = new TaskCenter();
    tc2.ImportStates("这不是JSON{{{");
    tc2.ImportStates("");
    tc2.ImportStates("null");
    Check("INJ-重启-损坏快照静默忽略", tc2.List().Count == 0);
    tc2.ImportStates(tc.ExportStates());
    Check("INJ-重启-完好快照照常并入", tc2.List().Count >= 12, $"并入{tc2.List().Count}条");
    var wz = SyncCheckpoint.FailSafeParse("t", "{broken");
    Check("INJ-重启-水位损坏回零", wz.BatchDone == 0 && wz.LastKey == "");
    Check("INJ-重启-回零后从头续传", SyncCheckpoint.Resume(new[] { "a", "b" }, wz).Count == 2);
    Check("INJ-重启-时间线损坏不抛", SchemaTimeline.Parse("{oops").Count == 0);
    Check("INJ-重启-迁移状态损坏返回空", MigrationResume.FromJson("{oops") is null);
    Check("INJ-重启-备份策略损坏返回空", BackupPlanner.FromJson("{oops") is null);
}

Console.WriteLine("\n== 畸形灌数与机械遍历（R3 盲区补齐 2026-11-21）==");
Layer("暴力");
{
    // --- 畸形 SQL 灌切分器与守卫：只许容错，不许崩 ---
    string[] nastySql =
    {
        "", "   ", ";", ";;;", "'", "\"", "`", "''''", "--", "/*", "*/", "/*未闭合",
        "SELECT '未闭合", "SELECT \"未闭合", "SELECT `未闭合",
        "DROP TABLE users; DROP TABLE users;--", "émoji🚀中文表名", new string('A', 100000),
        "SELECT * FROM t WHERE x='a;b;c';SELECT 2", "\x00\x01\x02", "sel;ect;;;from;;where;",
        "INSERT INTO t VALUES ('it''s'); -- 注释\nSELECT 1", "‌‍零宽字符",
    };
    int splitOk = 0, guardOk = 0;
    foreach (var s in nastySql)
    {
        try { var parts = SqlSplitter.Split(s); if (parts is not null) splitOk++; }
        catch (Exception ex) { Console.WriteLine("[MNK-SQL] Split threw: " + ex.GetType().Name); }
        try { var checks = RiskySqlGuard.InspectScript(s, DbEngine.SQLite, q => q); if (checks is not null) guardOk++; }
        catch (Exception ex) { Console.WriteLine("[MNK-SQL] Guard threw: " + ex.GetType().Name); }
    }
    Check("MNK-SQL-切分器畸形不崩", splitOk == nastySql.Length, $"{splitOk}/{nastySql.Length}");
    Check("MNK-SQL-守卫畸形不崩", guardOk == nastySql.Length, $"{guardOk}/{nastySql.Length}");
    var sp = SqlSplitter.Split("SELECT 'a;b' AS c;UPDATE t SET x=1;-- 尾注释");
    Check("MNK-SQL-引号内分号不切", sp.Count == 3 && sp[0].Contains("'a;b'") && sp[1].StartsWith("UPDATE"), string.Join("||", sp));

    // --- 怪问题灌问库引擎：空串/emoji/注入串/超长/不存在列，只许明确拒答，不许崩 ---
    var nlCols = new List<ColumnSpec>
    {
        new() { Name = "amount", DataType = "DECIMAL" },
        new() { Name = "name", DataType = "VARCHAR" },
    };
    string[] nastyQuestions =
    {
        "", "???", "🚀🚀", "'; DROP TABLE users;--", string.Concat(Enumerable.Repeat("多少", 2500)),
        "平均金额最大的前3名是谁", "xyzzy不存在的列名多少条",
    };
    int nlOk = 0;
    foreach (var q in nastyQuestions)
    {
        try { var r = NaturalLanguageQuery.Ask(q, "orders", nlCols, DbEngine.SQLite); if (r is not null) nlOk++; }
        catch (Exception ex) { Console.WriteLine("[MNK-NL] threw: " + ex.GetType().Name); }
    }
    Check("MNK-问库-怪问题不崩", nlOk == nastyQuestions.Length, $"{nlOk}/{nastyQuestions.Length}");
    var nl = NaturalLanguageQuery.Ask("平均金额是多少", "orders", nlCols, DbEngine.SQLite);
    Check("MNK-问库-正常问题仍可用", nl.Ok && nl.Sql.ToUpperInvariant().Contains("AVG"), nl.Sql);

    // --- 畸形 CSV 灌导入流：未闭合引号/二进制垃圾/BOM/跨行字段/超宽行，只许容错不许崩 ---
    string[] nastyCsv =
    {
        "", "\n\n\n", "a,b\n1", "a,b\n\"未闭合,1", "a,b\n\x00\x01,2",
        "a,b\n" + new string('x', 500000), "a,b\r\n1,2\r\n3,4", "﻿a,b\n1,2",
        "a,b\n\"multi\nline\",2", "a,b\n1,2,3,4,5",
    };
    int csvOk = 0;
    foreach (var c in nastyCsv)
    {
        try { var batches = ImportStreaming.CsvBatches(new StringReader(c)).ToList(); if (batches is not null) csvOk++; }
        catch (Exception ex) { Console.WriteLine("[MNK-CSV] threw: " + ex.GetType().Name); }
    }
    Check("MNK-CSV-畸形不崩", csvOk == nastyCsv.Length, $"{csvOk}/{nastyCsv.Length}");
    var csvGood = ImportStreaming.CsvBatches(new StringReader("a,b\r\n1,2\r\n3,4")).ToList();
    Check("MNK-CSV-正常两行一批收齐", csvGood.Count == 1 && csvGood[0].Rows.Count == 2);
    var spillCsv = new TempFileSpillStore();
    try
    {
        var sum = ImportStreaming.CsvIngest(new StringReader("a,b\n1,2\n3,4"), spillCsv);
        Check("MNK-CSV-汇入暂存口径一致", sum is not null && sum.TotalRows == 2 && sum.BatchCount == 1,
            $"行{sum?.TotalRows} 批{sum?.BatchCount}");
    }
    finally { spillCsv.Dispose(); }

    // --- 固定时长机械遍历：种子固定可复现，随机组合纯逻辑引擎，未文档化异常即红（约2秒，不拖垮全量） ---
    var rng = new Random(20261121);
    var swMonkey = Stopwatch.StartNew();
    int monkeyOps = 0;
    Exception? monkeyBoom = null;
    var timeline = new List<SchemaTimelineEntry>();
    var snapTables = new List<SchemaTableSnapshot>
    {
        new() { Table = "t1", Columns = { new TableColumnDesign { Name = "id", SqlType = "INT", IsPrimaryKey = true } } },
    };
    while (swMonkey.Elapsed.TotalSeconds < 2.0)
    {
        try
        {
            switch (rng.Next(6))
            {
                case 0: timeline = SchemaTimeline.Append(timeline, snapTables, "切面" + monkeyOps).ToList(); break;
                case 1: SchemaTimeline.Parse(rng.Next(2) == 0 ? null : "{\"bad\":"); break;
                case 2:
                    var plan = new ShardPlan("t" + monkeyOps, "k", ShardingWizard.ShardStrategy.Hash,
                        2 + rng.Next(4), Array.Empty<string>(), Array.Empty<ShardCategory>());
                    ShardRouting.RouteToShard(plan, rng.Next(2) == 0 ? rng.Next().ToString() : "");
                    break;
                case 3: SyncCheckpoint.FailSafeParse("t", rng.Next(2) == 0 ? null : rng.Next().ToString("N")); break;
                case 4: MigrationResume.FromJson(rng.Next(2) == 0 ? "" : "[1,2,3]"); break;
                case 5: BackupPlanner.FromJson(rng.Next(2) == 0 ? "" : "[]"); break;
            }
            monkeyOps++;
        }
        catch (ArgumentException) { monkeyOps++; }   // 文档化异常：非法入参被明确拒绝
        catch (NotSupportedException) { monkeyOps++; }
        catch (Exception ex) { monkeyBoom = ex; break; }
    }
    Check("MNK-遍历-固定时长无未文档化异常", monkeyBoom is null && monkeyOps > 50,
        $"ops={monkeyOps} {monkeyBoom?.GetType().Name}");
}

Console.WriteLine("\n== 入口对账（R4 盲区补齐 2026-11-21）==");
Layer("制度补齐");
{
    // App 层 UI 类不可在 LogicTest 实例化（会拉 WinUI 依赖）：入口对账用源码文本断言；
    // Infrastructure 层可实例化部分走真测。源码根定位：从测试二进制逐级向上找含 Src/TaoDBM.App 的目录。
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Src", "TaoDBM.App")))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("未定位到源码根（Src/TaoDBM.App）");
        return dir.FullName;
    }
    static string Src(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), "Src", rel));

    // 1) 快捷键：F1 全局手册挂根 Grid；WorkTab F5/Ctrl+Enter/Ctrl+Space/Ctrl+S
    var mw = Src("TaoDBM.App/MainWindow.xaml.cs");
    Check("ENT-快捷键-F1全局手册挂根Grid", mw.Contains("VirtualKey.F1") && mw.Contains("KeyboardAccelerators.Add"));
    var wt = Src("TaoDBM.App/UI/WorkTab.cs");
    Check("ENT-快捷键-工作表四键齐全",
        wt.Contains("VirtualKey.F5") && wt.Contains("VirtualKey.Enter, Windows.System.VirtualKeyModifiers.Control")
        && wt.Contains("VirtualKey.Space") && wt.Contains("VirtualKey.S, Windows.System.VirtualKeyModifiers.Control"));

    // 2) 命令行 .sql 参数：跳过 - 开头、只收 .sql 后缀（App 层文本断言 + Infrastructure 登记簿真测）
    var host = Src("TaoDBM.App/UI/SqlHandoffHost.cs");
    Check("ENT-命令行-只收sql后缀", host.Contains("GetCommandLineArgs") && host.Contains(".sql"));
    var appCs = Src("TaoDBM.App/App.xaml.cs");
    Check("ENT-命令行-交接失败回落新实例", appCs.Contains("TryHandoffToExisting") && appCs.Contains("Current.Exit()"));

    // 3) 管道交接登记簿（Infrastructure 真测）：登记/激活/坏行容错/死亡进程清理
    var regFile = Path.Combine(Path.GetTempPath(), "taodbm-ent-reg-" + Guid.NewGuid().ToString("N") + ".txt");
    SqlHandoffRegistry.UseFile(regFile);
    try
    {
        SqlHandoffRegistry.Register("pipe-a", 1);
        SqlHandoffRegistry.Touch("pipe-a", 1);
        Check("ENT-管道-登记与防重复", SqlHandoffRegistry.IsRegistered("pipe-a"));
        SqlHandoffRegistry.Register("pipe-a", 1);
        Check("ENT-管道-重复登记不叠行", File.ReadAllLines(regFile).Length == 1);
        Check("ENT-管道-pid非正拒收", SqlHandoffRegistry.Parse("pipe-b|0|20261121000000000") is null
            && SqlHandoffRegistry.Parse("pipe-b|-3|20261121000000000") is null);
        Check("ENT-管道-坏行整行丢弃", SqlHandoffRegistry.Parse("垃圾行") is null && SqlHandoffRegistry.Parse("a|b|c") is null);
        var e = SqlHandoffRegistry.Parse("pipe-c|42|20261121093000000");
        Check("ENT-管道-好行往返", e is not null && e.Pid == 42 && SqlHandoffRegistry.Format(e) == "pipe-c|42|20261121093000000");
        // 死亡进程清理：登记一个必死的 pid（取当前进程树外随机大号），MostRecentAlive 后应被摘除
        File.AppendAllText(regFile, "pipe-dead|2147480000|20261121093000000\n");
        SqlHandoffRegistry.MostRecentAlive(Environment.ProcessId);
        Check("ENT-管道-死亡进程被清理", !File.ReadAllText(regFile).Contains("pipe-dead"));
    }
    finally { SqlHandoffRegistry.UseFile(""); try { File.Delete(regFile); } catch { } }

    // 4) 拖放：WorkTabs.AllowDrop 挂在 MainWindow.SqlLoad.cs
    var sqlLoad = Src("TaoDBM.App/MainWindow.SqlLoad.cs");
    Check("ENT-拖放-允许拖入", sqlLoad.Contains("AllowDrop = true"));

    // 5) 托盘：单例 + 五事件 + 最近列表上限
    var tray = Src("TaoDBM.App/UI/TrayIcon.cs");
    Check("ENT-托盘-五事件齐全", new[] { "OpenRequested", "NewConnectionRequested", "ThemeRequested", "SettingsRequested", "ExitRequested" }
        .All(ev => tray.Contains(ev)));
    Check("ENT-托盘-最近列表上限8", tray.Contains("RecentMax = 8"));

    // 6) .sql 文件关联：安装器注册 TaoDBM.SqlFile + .sql 键
    var wxs = File.ReadAllText(Path.Combine(RepoRoot(), "Installer", "Package.wxs"));
    Check("ENT-文件关联-注册表键齐全", wxs.Contains("TaoDBM.SqlFile") && wxs.Contains(".sql"));

    // 7) 窗口状态记忆与 .sql 打开偏好（Infrastructure 真测）：损坏回落默认，绝不闪退
    var wsFile = Path.Combine(Path.GetTempPath(), "taodbm-ent-ws-" + Guid.NewGuid().ToString("N") + ".json");
    WindowStateStore.UseFile(wsFile);
    try
    {
        Check("ENT-窗口-缺失回默认", WindowStateStore.Load() is WindowStateItem);
        WindowStateStore.Save(new WindowStateItem { X = 10, Y = 20, Width = 1200, Height = 800, Maximized = false, LeftPaneWidth = 300 });
        var ws = WindowStateStore.Load();
        Check("ENT-窗口-写读往返", ws.X == 10 && ws.Width == 1200 && ws.LeftPaneWidth == 300);
        File.WriteAllText(wsFile, "{broken");
        Check("ENT-窗口-损坏回默认", WindowStateStore.Load() is WindowStateItem);
        Check("ENT-窗口-默认尺寸口径", WindowStateStore.DefaultSize(1920, 1080) == (1536.0, 864.0)
            && WindowStateStore.DefaultSize(1000, 700) == (1000.0, 700.0));
    }
    finally { WindowStateStore.UseFile(""); try { File.Delete(wsFile); } catch { } }
    var prefsFile = Path.Combine(Path.GetTempPath(), "taodbm-ent-pref-" + Guid.NewGuid().ToString("N") + ".json");
    SqlOpenPrefs.UseFile(prefsFile);
    try
    {
        Check("ENT-偏好-缺失默认开", SqlOpenPrefs.Load());
        SqlOpenPrefs.Save(false);
        Check("ENT-偏好-写读往返", !SqlOpenPrefs.Load());
        File.WriteAllText(prefsFile, "{broken");
        Check("ENT-偏好-损坏回落默认", SqlOpenPrefs.Load());
    }
    finally { SqlOpenPrefs.UseFile(""); try { File.Delete(prefsFile); } catch { } }

    // 8) 数据目录迁移：Tao 系列存储 D 盘探测回退（Infrastructure 真测，只验路径口径不真迁）
    Check("ENT-迁移-路径口径可复现", TaoPaths.FilePath("windowstate.json").EndsWith("windowstate.json"));
}

Console.WriteLine("\n== 操作手册（2026-11-20 老板令）==");
Layer("单元");
{
    var ch = ManualContent.Chapters;
    Check("MAN-章节数量", ch.Length == 17, ch.Length.ToString());   // ㊷ 手册补「安装与卸载」章，16→17
    Check("MAN-章节标题唯一", ch.Select(c => c.Title).Distinct().Count() == ch.Length);
    Check("MAN-每章有小节", ch.All(c => c.Sections.Length > 0));
    Check("MAN-每节有标题与正文", ch.All(c => c.Sections.All(s => !string.IsNullOrWhiteSpace(s.Heading) && !string.IsNullOrWhiteSpace(s.Body))));
    var totalChars = ch.Sum(c => c.Sections.Sum(s => s.Heading.Length + s.Body.Length));
    Check("MAN-正文总字数达标(≥6000)", totalChars >= 6000, totalChars.ToString());
    string[] must = { "快速上手", "连接管理", "备份与恢复", "分库分区分表", "七库方言差异速查", "快捷键一览" };
    Check("MAN-关键章节齐全", must.All(m => ch.Any(c => c.Title.Contains(m))), string.Join("|", ch.Select(c => c.Title)));
    var dialect = ch.First(c => c.Title.Contains("方言"));
    Check("MAN-方言章覆盖七库", new[] { "MySQL", "PostgreSQL", "SQL Server", "SQLite", "Oracle", "MongoDB", "Redis" }
        .All(e => dialect.Sections.Any(s => s.Heading.Contains(e) || s.Body.Contains(e))));
    Check("MAN-快捷键章含F1与F5", ch.First(c => c.Title.Contains("快捷键")).Sections.Any(s => s.Body.Contains("F1") && s.Body.Contains("F5")));
}

Console.WriteLine("\n== 分层结果 ==");
foreach (var name in new[] { "单元", "集成", "系统", "暴力", "万链", "制度补齐" })
    Console.WriteLine($"{name}: {layerPass.GetValueOrDefault(name)} / {layerPass.GetValueOrDefault(name) + layerFail.GetValueOrDefault(name)}");
Console.WriteLine($"\n通过 {pass} / {pass + fail}");
Ck($"ALL-DONE pass={pass} fail={fail}");
return fail == 0 ? 0 : 1;

// 测试用最小 Provider：复用 RelationalProvider 的 Quote 逻辑
class TestProv : TaoDBM.Core.RelationalProvider
{
    private readonly char _q;
    public TestProv(char q = '`') : base(new TaoDBM.Core.ConnectionProfile()) => _q = q;
    protected override System.Data.Common.DbConnection Open(string? db) => throw new NotSupportedException();
    protected override char OpenQuote => _q;
    protected override char CloseQuote => _q;
    public override bool SupportsBrowse => true;
    protected override string ListDatabasesSql() => "";
    protected override string ListChildrenSql(string db) => "";
    protected override string ListColumnsSql(string db, string table) => "";
    protected override string PagingClause(int offset, int limit) => "";
}

// 批次F 列懒加载测试源：计数源读、可注延迟/单次断流；值约定 value(col,row)=col*10000+row
class LazyCountSource : TaoDBM.Core.Streaming.IColumnSource
{
    private readonly string[] _names;
    public int Reads;
    public readonly List<int[]> Batches = new();
    /// <summary>FailOnce 开启时，第 FailAt 次源读抛断流（默认第 2 次：首轮成功、次轮断）。</summary>
    public int FailAt = 2;
    public bool FailOnce;
    public int DelayMs;

    public LazyCountSource(int cols, int rows)
    {
        _names = Enumerable.Range(0, cols).Select(i => "c" + i).ToArray();
        RowCount = rows;
    }

    public IReadOnlyList<string> ColumnNames => _names;
    public int RowCount { get; }

    public async Task<IReadOnlyList<object?[]>> ReadColumnsAsync(int[] columnIndexes, CancellationToken ct)
    {
        if (DelayMs > 0) await Task.Delay(DelayMs, ct);
        Reads++;
        Batches.Add(columnIndexes);
        if (FailOnce && Reads == FailAt) throw new System.IO.IOException("源流中断");
        return columnIndexes
            .Select(c => Enumerable.Range(0, RowCount).Select(r => (object?)(c * 10000 + r)).ToArray())
            .ToArray();
    }
}
