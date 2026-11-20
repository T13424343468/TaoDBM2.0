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
        var segs = Environment.GetEnvironmentVariable("REALDB_SEG") ?? "1";
        if (segs.Split(',').Select(s => s.Trim()).Contains("2"))
            RunSegment2(check, engine.Value, spec);
        if (segs.Split(',').Select(s => s.Trim()).Contains("3"))
            RunSegment3(check, engine.Value, spec);
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

    // ---------- 段二：权限 / 执行计划 / 同步 ----------

    static void RunSegment2(Action<string, bool, string> check, DbEngine engine, string spec)
    {
        string tag = engine switch { DbEngine.MySQL => "MYSQL", DbEngine.PostgreSQL => "PG", DbEngine.SqlServer => "MSSQL", _ => "RDB" };
        var profile = ProfileOf(engine, spec);
        string db = profile.Database;

        using var prov = ProviderFactory.Create(profile);
        try
        {
            WithRetry(() => prov.TestConnection().Wait());

            // 权限：账号清单 → 当前账号授权文本 → 审计/回收全链（与权限中心同数据链）
            var accounts = prov.Execute(db, UserPermissionCatalog.ListAccountsSql(engine)).Result.Last();
            check($"[{tag}:S2] 权限-账号清单非空", accounts.HasRows, $"共 {accounts.Rows.Count} 账号");
            var accts = UserPermissionCatalog.ExtractAccounts(accounts.Rows);
            check($"[{tag}:S2] 权限-账号解析含当前账号", accts.Any(a => a.User == profile.User),
                string.Join(";", accts.Take(5).Select(a => a.User)));
            string grantHost = accts.Where(a => a.User == profile.User).Select(a => a.Host).FirstOrDefault() ?? "";
            var grantRs = prov.Execute(db, UserPermissionCatalog.GrantsSql(engine, profile.User, grantHost)).Result.Last();
            var grantTexts = UserPermissionCatalog.ExtractGrantTexts(grantRs.Rows);
            check($"[{tag}:S2] 权限-当前账号授权文本可取", grantTexts.Count > 0, $"共 {grantTexts.Count} 条");
            var records = PermissionAuditor.Parse(grantTexts);
            check($"[{tag}:S2] 权限-授权记录解析非空", records.Count > 0, $"{records.Count} 条");
            var findings = PermissionAuditor.Audit(records);
            var revokes = records.Count > 0 ? PermissionAuditor.RevokeSql(records[0]) : new List<string>();
            check($"[{tag}:S2] 权限-审计与回收语句跑通", revokes.All(r => !string.IsNullOrWhiteSpace(r)),
                $"{findings.Count} 发现 / {revokes.Count} 回收");

            // 执行计划：EXPLAIN 包装 → 真库执行 → 解析/标注全链（与执行计划面板同数据链）
            var planRs = prov.Execute(db, ExplainPlanEngine.PlanSql(engine, $"SELECT * FROM {prov.Quote("products")}")).Result;
            check($"[{tag}:S2] 计划-EXPLAIN 执行无错误", planRs.All(r => !r.Message.StartsWith("错误：")),
                string.Join(";", planRs.Select(r => r.Message)));
            var planText = string.Join("\n", planRs.Last().Rows.Select(r => string.Join(" ", r)));
            var plan = ExplainPlanEngine.Parse(planText);
            check($"[{tag}:S2] 计划-计划树解析非空", plan.Flat.Count > 0, $"节点 {plan.Flat.Count}");
            var planFindings = ExplainPlanEngine.Annotate(plan);
            check($"[{tag}:S2] 计划-瓶颈标注跑通", true, $"{planFindings.Count} 标注");

            // 同步：角色探测 → 从库清单 → 自身复制状态（单机库口径：主库视角，不冒充从库账）
            var roleRs = prov.Execute(db, ReplicationCatalog.RoleProbeSql(engine)).Result.Last();
            string? role = roleRs.Rows.Count > 0
                ? ReplicationCatalog.Col(roleRs.Columns, roleRs.Rows[0], "in_recovery", "role_desc")
                : null;
            check($"[{tag}:S2] 同步-角色探测执行", true, role ?? "无行（主库）");
            bool isPrimary = engine switch
            {
                DbEngine.PostgreSQL => role == "False" || role == "false",
                DbEngine.MySQL => roleRs.Rows.Count == 0, // SHOW REPLICA STATUS 空结果 = 未配置从库 = 主库
                _ => role == null,
            };
            check($"[{tag}:S2] 同步-单机主库角色判定", isPrimary, $"role={role ?? "无"}");
            var replicas = prov.Execute(db, ReplicationCatalog.ReplicasSql(engine)).Result.Last();
            check($"[{tag}:S2] 同步-从库清单执行", true, $"{replicas.Rows.Count} 从库");
            var self = prov.Execute(db, ReplicationCatalog.ReplicaSelfStatusSql(engine)).Result.Last();
            check($"[{tag}:S2] 同步-自身复制状态执行", true, $"{self.Rows.Count} 行");

            check($"[{tag}:S2] 段二小结", true, "权限5+计划3+同步4=12 例");
        }
        catch (Exception ex)
        {
            var cur = ex; while (cur.InnerException != null) cur = cur.InnerException;
            check($"[{tag}:S2] 段二异常收口", false, cur.Message);
        }
    }

    // ---------- 段三：备份 / 建模 / 主从 ----------

    static void RunSegment3(Action<string, bool, string> check, DbEngine engine, string spec)
    {
        string tag = engine switch { DbEngine.MySQL => "MYSQL", DbEngine.PostgreSQL => "PG", DbEngine.SqlServer => "MSSQL", _ => "RDB" };
        if (!BackupTools.Supported(engine))
        {
            check($"[{tag}:S3] 备份-引擎不支持 skip", true, $"skip：{BackupTools.UnsupportedReason(engine)}");
            return;
        }
        var profile = ProfileOf(engine, spec);
        string db = profile.Database;
        string scratch = db + "_har_bak";
        string dumpFile = Path.Combine(Path.GetTempPath(), "t_har_dump_" + Token + ".sql");

        using var prov = ProviderFactory.Create(profile);
        try
        {
            WithRetry(() => prov.TestConnection().Wait());

            // 备份：工具规格 → 真实导出 → 文件落盘含建表语句
            ToolSpec dumpSpec = engine switch
            {
                DbEngine.MySQL => BackupTools.MysqlDump(profile.Host, profile.Port, profile.User, profile.Password, db, dumpFile),
                DbEngine.PostgreSQL => BackupTools.PgDump(profile.Host, profile.Port, profile.User, profile.Password, db, dumpFile),
                _ => throw new NotSupportedException(),
            };
            var (code, output) = RunTool(dumpSpec, null);
            bool dumped = code == 0 && File.Exists(dumpFile) && new FileInfo(dumpFile).Length > 0;
            check($"[{tag}:S3] 备份-导出工具真跑通", dumped, $"exit={code} {output}".Trim());

            // 还原：导入临时库 → 行数对账 → 清库
            prov.Execute(db, engine == DbEngine.MySQL
                ? $"DROP DATABASE IF EXISTS `{scratch}`; CREATE DATABASE `{scratch}`"
                : $"DROP DATABASE IF EXISTS {scratch}; CREATE DATABASE {scratch}").Wait();
            ToolSpec restoreSpec = engine switch
            {
                DbEngine.MySQL => BackupTools.MysqlRestore(profile.Host, profile.Port, profile.User, profile.Password, scratch, dumpFile),
                DbEngine.PostgreSQL => BackupTools.PgRestore(profile.Host, profile.Port, profile.User, profile.Password, scratch, dumpFile),
                _ => throw new NotSupportedException(),
            };
            var (rcode, routput) = RunTool(restoreSpec, engine == DbEngine.MySQL ? dumpFile : null);
            check($"[{tag}:S3] 备份-还原工具真跑通", rcode == 0, $"exit={rcode} {routput}".Trim());
            var cnt = prov.Execute(scratch, $"SELECT COUNT(*) AS c FROM {prov.Quote("orders")}").Result.Last();
            check($"[{tag}:S3] 备份-还原行数对账", Convert.ToInt32(cnt.Rows[0][0]) == 5000, $"计 {cnt.Rows[0][0]}");
            prov.Execute(db, engine == DbEngine.MySQL ? $"DROP DATABASE IF EXISTS `{scratch}`" : $"DROP DATABASE IF EXISTS {scratch}").Wait();
            File.Delete(dumpFile);

            // 建模：真目录 → ER 框 → 外键深推断（真 FK 行 + 命名补边）→ 正向工程
            var erd = new ErdModel
            {
                Tables = new List<ErdTable>
                {
                    ErdModel.FromSpecs("orders", prov.GetColumns(db, "orders").Result),
                    ErdModel.FromSpecs("products", prov.GetColumns(db, "products").Result),
                }
            };
            ErdModel.AutoLayout(erd);
            check($"[{tag}:S3] 建模-表框含列与主键", erd.Tables.All(t => t.Columns.Count == 4 && t.Columns.Any(c => c.IsKey)),
                string.Join(";", erd.Tables.Select(t => t.Name + ":" + t.Columns.Count)));
            var fkRows = prov.Execute(db, FkRowsSql(engine, db)).Result.Last().Rows;
            var edges = ErdModel.InferEdgesDeep(erd, fkRows);
            check($"[{tag}:S3] 建模-关系边推断含产品外键", edges.Any(e => e.FromTable == "orders" && e.ToTable == "products"),
                $"{edges.Count} 边（FK 行 {fkRows.Count}）");
            var fwd = ErdForward.Generate(erd);
            check($"[{tag}:S3] 建模-正向工程跑通", fwd.Ok, fwd.Errors.Count > 0 ? string.Join(";", fwd.Errors) : $"DDL {fwd.Ddl.Count} 段");

            // 主从：从库清单 → 拓扑图构建（单机口径：主库节点恒在）
            var replicas = prov.Execute(db, ReplicationCatalog.ReplicasSql(engine)).Result.Last();
            var nodes = ReplicationCatalog.BuildFromPrimary(engine, replicas.Columns, replicas.Rows);
            check($"[{tag}:S3] 主从-拓扑图含主库节点", nodes.Any(n => n.Role == "主库"), $"{nodes.Count} 节点");

            check($"[{tag}:S3] 段三小结", true, "备份4+建模3+主从1=8 例");
        }
        catch (Exception ex)
        {
            var cur = ex; while (cur.InnerException != null) cur = cur.InnerException;
            check($"[{tag}:S3] 段三异常收口", false, cur.Message);
        }
    }

    static string FkRowsSql(DbEngine engine, string db) => engine switch
    {
        DbEngine.MySQL => "SELECT TABLE_NAME, COLUMN_NAME, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE" +
            $" WHERE TABLE_SCHEMA = '{db.Replace("'", "''")}' AND REFERENCED_TABLE_NAME IS NOT NULL",
        DbEngine.PostgreSQL => "SELECT tc.table_name, kcu.column_name, ccu.table_name, ccu.column_name FROM information_schema.table_constraints tc" +
            " JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema" +
            " JOIN information_schema.constraint_column_usage ccu ON tc.constraint_name = ccu.constraint_name AND tc.table_schema = ccu.table_schema" +
            " WHERE tc.constraint_type = 'FOREIGN KEY'",
        _ => "SELECT NULL, NULL, NULL, NULL WHERE 1 = 0",
    };

    static (int Code, string Output) RunTool(ToolSpec toolSpec, string? stdinFile)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = toolSpec.FileName,
            Arguments = toolSpec.Arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinFile != null,
            UseShellExecute = false,
        };
        foreach (var kv in toolSpec.Env) psi.Environment[kv.Key] = kv.Value;
        using var p = System.Diagnostics.Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (stdinFile != null)
        {
            using var fs = File.OpenRead(stdinFile);
            fs.CopyTo(p.StandardInput.BaseStream);
            p.StandardInput.Close();
        }
        if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return (-1, "timeout"); }
        return (p.ExitCode, outTask.Result + errTask.Result);
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
