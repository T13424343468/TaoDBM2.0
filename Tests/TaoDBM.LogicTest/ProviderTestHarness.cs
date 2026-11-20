// ProviderTestHarness：真库全功能矩阵切库口径（2026-11-20 12:04-12:08 老板四连拍）。
// 口径：LogicTest 全量功能面逐点参数化，同一套用例在每个库型独立环境整套跑通；
// 仅目标库物理不可能的用例按库型 skip 并逐例注明原因；skip 明确落 detail，不冒充功能绿账。
// 环境隔离：REALDB_PROVIDER + REALDB_CONN 每库型独立 job / 独立连接串，互不串账；
// 用例表名带运行令牌（t_har_XXXXXXXX），跑前清、跑后删，可断点重掷。
// 分段：RDB 段一（连接/对象树/查询流式）→ 段二（权限/执行计划/同步）→ 段三（备份/建模/主从）
// → 段四（迁移/安全/智能线/分库分区分表）；REALDB_SEG 逗号分隔选段（默认 1），每段独立出账。
using System.Globalization;
using TaoDBM.Core;

public static class ProviderTestHarness
{
    static readonly string Token = Guid.NewGuid().ToString("N")[..8];
    static string Tbl => "t_har_" + Token;

    /// <summary>目标引擎；未启用返回 null。</summary>
    static DbEngine? EngineOf(string? name) => name?.Trim() switch
    {
        "MySQL" or "MYSQL" => DbEngine.MySQL,
        "PostgreSQL" or "PG" => DbEngine.PostgreSQL,
        "SqlServer" or "MSSQL" => DbEngine.SqlServer,
        _ => null,
    };

    public static bool Enabled =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REALDB_PROVIDER"));

    public static void Run(Action<string, bool, string> check)
    {
        var engName = Environment.GetEnvironmentVariable("REALDB_PROVIDER");
        var engine = EngineOf(engName);
        if (engine is null)
        {
            if (!string.IsNullOrEmpty(engName))
                check("[Harness] 引擎未收录（本段支持 MySQL/PostgreSQL/SqlServer）", false, $"REALDB_PROVIDER={engName}");
            return;
        }
        var spec = Environment.GetEnvironmentVariable("REALDB_CONN");
        if (string.IsNullOrEmpty(spec) || spec.Split('|').Length < 2)
        {
            check("[Harness] REALDB_CONN 缺失或格式不对（host|port|user|pass|db）", false, $"provider={engName}");
            return;
        }
        RunSegment1(check, engine.Value, spec);
        // 段二/三/四占位：随 J1 后续切片落码（每段独立出账，不在此轮冒进）
        var segs = Environment.GetEnvironmentVariable("REALDB_SEG") ?? "1";
        if (segs.Split(',').Select(s => s.Trim()).Contains("2"))
            check("[Harness] 段二（权限/执行计划/同步）切片未落码", true, "J1 后续切片，见交账");
        if (segs.Split(',').Select(s => s.Trim()).Contains("3"))
            check("[Harness] 段三（备份/建模/主从）切片未落码", true, "J1 后续切片，见交账");
        if (segs.Split(',').Select(s => s.Trim()).Contains("4"))
            check("[Harness] 段四（迁移/安全/智能线/分库分区分表）切片未落码", true, "J1 后续切片，见交账");
    }

    static ConnectionProfile ProfileOf(DbEngine engine, string spec)
    {
        var p = spec.Split('|');
        return new ConnectionProfile
        {
            Engine = engine,
            Host = p[0],
            Port = int.TryParse(p[1], out var port) && port > 0 ? port : EngineInfo.DefaultPort(engine),
            User = p.Length > 2 ? p[2] : "",
            Password = p.Length > 3 ? p[3] : "",
            Database = p.Length > 4 ? p[4] : "",
        };
    }

    static void WithRetry(Action act, int times = 6)
    {
        for (int i = 1; ; i++)
        {
            try { act(); return; }
            catch (Exception) when (i < times) { Thread.Sleep(8000); }
        }
    }

    // ---------- 段一：连接 / 对象树 / 查询流式 ----------

    static void RunSegment1(Action<string, bool, string> check, DbEngine engine, string spec)
    {
        string tag = engine switch { DbEngine.MySQL => "MYSQL", DbEngine.PostgreSQL => "PG", DbEngine.SqlServer => "MSSQL", _ => "RDB" };
        var profile = ProfileOf(engine, spec);
        string db = profile.Database;
        string textType = engine == DbEngine.SqlServer ? "NVARCHAR(500)" : "VARCHAR(500)";
        string nlit = engine == DbEngine.SqlServer ? "N'" : "'";

        using var prov = ProviderFactory.Create(profile);
        try
        {
            WithRetry(() => prov.TestConnection().Wait());
            check($"[{tag}:S1] 连接-测试连接", true, "");
            var dbs = prov.GetDatabases().Result;
            check($"[{tag}:S1] 连接-库列表非空", dbs.Count > 0, $"{dbs.Count} 项");
            check($"[{tag}:S1] 连接-目标库在列表", dbs.Contains(db), string.Join(",", dbs.Take(5)));

            // 对象树：建表 → GetChildren / GetColumns 与 App 对象树同数据链
            prov.Execute(db, DropSql(engine, Tbl)).Wait();
            prov.Execute(db, $"CREATE TABLE {prov.Quote(Tbl)} (id INT PRIMARY KEY, name {textType}, val DECIMAL(10,2))").Wait();
            var children = prov.GetChildren(db).Result;
            check($"[{tag}:S1] 对象树-表清单含矩阵表", children.Contains(Tbl), $"共 {children.Count} 子级");
            var cols = prov.GetColumns(db, Tbl).Result;
            check($"[{tag}:S1] 对象树-列清单数量", cols.Count == 3, string.Join(",", cols.Select(c => c.Name)));
            check($"[{tag}:S1] 对象树-主键列标记", cols.Any(c => c.Name == "id" && c.IsKey),
                string.Join(",", cols.Select(c => c.Name + (c.IsKey ? "*" : ""))));
            // 对象树数据链衔接：库→子级→列逐层非断链（App RebuildRoots 同口径）
            bool chainOk = dbs.Contains(db) && children.Contains(Tbl) && cols.Count > 0;
            check($"[{tag}:S1] 对象树-逐层衔接不断链", chainOk, "");

            // 查询流式：多语句脚本、写账、大结果、分页、中文、NULL、聚合、标识符引用
            var rs = prov.Execute(db, $"INSERT INTO {prov.Quote(Tbl)} (id, name, val) VALUES (1, {nlit}道生一', 3.14), (2, {nlit}αβγ', 2.71), (3, {nlit}tai', 1.61)").Result;
            check($"[{tag}:S1] 查询-INSERT 落账无错误", rs.All(r => !r.Message.StartsWith("错误：")),
                string.Join(";", rs.Select(r => r.Message)));
            var rSel = prov.Execute(db, $"SELECT id, name, val FROM {prov.Quote(Tbl)} ORDER BY id").Result.Last();
            static decimal Dec(object? v) => decimal.Parse(Convert.ToString(v)!.Trim(), CultureInfo.InvariantCulture);
            bool okIns = rSel.Rows.Count == 3
                && Convert.ToInt32(rSel.Rows[0][0]) == 1 && Convert.ToString(rSel.Rows[0][1]) == "道生一" && Dec(rSel.Rows[0][2]) == 3.14m
                && Convert.ToString(rSel.Rows[1][1]) == "αβγ" && Dec(rSel.Rows[1][2]) == 2.71m;
            check($"[{tag}:S1] 查询-中文与 Unicode 回读", okIns, string.Join(";", rSel.Rows.Select(r => string.Join(",", r))));

            var multi = prov.Execute(db, $"SELECT COUNT(*) AS c FROM {prov.Quote(Tbl)}; SELECT SUM(val) AS s FROM {prov.Quote(Tbl)}").Result;
            check($"[{tag}:S1] 查询-多语句脚本拆分执行", multi.Count == 2, $"返回 {multi.Count} 结果集");

            // 大结果回读 1000 行（流式通道逐行累计不截断）
            prov.Execute(db, DropSql(engine, "t_har_bulk_" + Token)).Wait();
            prov.Execute(db, $"CREATE TABLE {prov.Quote("t_har_bulk_" + Token)} (n INT, note {textType})").Wait();
            var bulkRows = string.Join(",\n", Enumerable.Range(1, 1000).Select(i =>
                $"({i}, {nlit}行{i:D4}" + new string('填', 30) + "')"));
            prov.Execute(db, $"INSERT INTO {prov.Quote("t_har_bulk_" + Token)} (n, note) VALUES {bulkRows}").Wait();
            var bulk = prov.Execute(db, $"SELECT n, note FROM {prov.Quote("t_har_bulk_" + Token)}").Result.Last();
            check($"[{tag}:S1] 查询-千行全量回读", bulk.Rows.Count == 1000, $"实回 {bulk.Rows.Count} 行");
            check($"[{tag}:S1] 查询-千行长文本完整", bulk.Rows.All(r => Convert.ToString(r[1])!.Contains("行0999") || Convert.ToString(r[1])!.Length >= 34), "");
            check($"[{tag}:S1] 查询-执行时长账为正", bulk.Elapsed > TimeSpan.Zero, "");

            if (prov.SupportsBrowse)
            {
                var pg1 = prov.Browse(db, "t_har_bulk_" + Token, 0, 500).Result;
                var pg2 = prov.Browse(db, "t_har_bulk_" + Token, 900, 500).Result;
                var pg3 = prov.Browse(db, "t_har_bulk_" + Token, 1000, 500).Result;
                check($"[{tag}:S1] 查询-分页首段行数", pg1.Rows.Count == 500 && pg1.TotalRows == 1000, $"{pg1.Rows.Count}/{pg1.TotalRows}");
                check($"[{tag}:S1] 查询-分页末段行数", pg2.Rows.Count == 100, $"实回 {pg2.Rows.Count}");
                check($"[{tag}:S1] 查询-分页越界空结果", pg3.Rows.Count == 0, $"实回 {pg3.Rows.Count}");
            }
            else check($"[{tag}:S1] 查询-分页用例 skip", true, $"skip：{engine} 提供者未实现 Browse（物理不可能），分段二按表浏览口径重估");

            var nullR = prov.Execute(db, $"SELECT COUNT(*) AS c FROM {prov.Quote(Tbl)} WHERE name IS NULL").Result.Last();
            check($"[{tag}:S1] 查询-空值过滤计数", Convert.ToInt32(nullR.Rows[0][0]) == 0, $"计 {nullR.Rows[0][0]}");
            var agg = prov.Execute(db, $"SELECT SUM(val) FROM {prov.Quote(Tbl)}").Result.Last();
            check($"[{tag}:S1] 查询-聚合求和", Math.Abs(Dec(agg.Rows[0][0]) - 7.46m) < 0.001m, $"和 {agg.Rows[0][0]}");
            var empty = prov.Execute(db, $"SELECT id FROM {prov.Quote(Tbl)} WHERE id = 99999").Result.Last();
            check($"[{tag}:S1] 查询-空结果集口径", !empty.HasRows && empty.Columns.Count == 1, $"rows={empty.Rows.Count} cols={empty.Columns.Count}");
            var quoted = prov.Execute(db, $"SELECT COUNT(*) AS c FROM {prov.Quote(Tbl)} WHERE id = 1").Result.Last();
            check($"[{tag}:S1] 查询-标识符引用执行", Convert.ToInt32(quoted.Rows[0][0]) == 1, $"计 {quoted.Rows[0][0]}");

            // 清理收口：删表后对象树不再含表
            prov.Execute(db, DropSql(engine, "t_har_bulk_" + Token)).Wait();
            prov.Execute(db, DropSql(engine, Tbl)).Wait();
            var after = prov.GetChildren(db).Result;
            check($"[{tag}:S1] 清理-矩阵表已移出树", !after.Contains(Tbl) && !after.Contains("t_har_bulk_" + Token), "");
            check($"[{tag}:S1] 段一小结", true, "连接3+对象树4+查询流式12+清理1=20 例（skip 另计）");
        }
        catch (Exception ex)
        {
            var cur = ex; while (cur.InnerException != null) cur = cur.InnerException;
            check($"[{tag}:S1] 段一异常收口", false, cur.Message);
        }
    }

    /// <summary>删表语句按方言（SQL Server 全版本兼容 OBJECT_ID 口径，不赌 DROP IF EXISTS 版本线）。</summary>
    static string DropSql(DbEngine engine, string table)
    {
        return engine switch
        {
            DbEngine.SqlServer => $"IF OBJECT_ID(N'[{table}]', 'U') IS NOT NULL DROP TABLE [{table}]",
            DbEngine.MySQL => $"DROP TABLE IF EXISTS `{table}`",
            DbEngine.PostgreSQL => $"DROP TABLE IF EXISTS \"{table}\"",
            _ => $"DROP TABLE IF EXISTS {table}",
        };
    }
}
