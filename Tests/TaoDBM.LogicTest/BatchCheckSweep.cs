using TaoDBM.Core;
using TaoDBM.Infrastructure;
using TaoDBM.UI;

namespace TaoDBM.LogicTest;

/// <summary>落码清扫批测试锚（2026-11-25，发现80/81/95/97/101/102/103/105/106/108/109/110/112/115/117/118/
/// 119/120/121/122/123/124/127/147/149）：纯逻辑可测项逐条落断言；界面层条目（端口校验/设置节流/看板重入闸/
/// 诊断页签等）本地沙箱编不了 App XAML，归云测首验，此处锚定其下沉 Core 的口径。
/// 独立文件接线（ComboMatrix 家法），不碰 Program.cs 主体。</summary>
public static class BatchCheckSweep
{
    public delegate void CheckD(string name, bool ok, string detail = "");

    public static void RunAll(Action<string, bool, string> check)
    {
        CheckD d = (name, ok, detail) => check(name, ok, detail ?? "");
        Console.WriteLine("\n== 落码清扫批 纯逻辑用例（2026-11-25）==");
        RunDefaultConstraint(d);   // 发现81
        RunOracleIdentity(d);      // 发现80
        RunErdForwardSync(d);      // 发现147
        RunBinaryLiteral(d);       // 发现109
        RunAlertSustain(d);        // 发现115
        RunFakerSeed(d);           // 发现117/118
        RunReplicationCatalog(d);  // 发现120/121
        RunRebalance(d);           // 发现122/123
        RunTaoPaths(d);            // 发现95/108/112
        RunOdbcEscape(d);          // 发现101
        RunManualContent(d);       // 发现89/92/98/99/128
    }

    // 发现81：SQL Server 改默认值——带约束名映射时先 DROP 旧约束再 ADD DEFAULT；无映射保持原口径不 DROP
    static void RunDefaultConstraint(CheckD d)
    {
        var t = new TableDesign
        {
            TableName = "t",
            Columns =
            [
                new TableColumnDesign { Name = "id", SqlType = "INT" },
                new TableColumnDesign { Name = "memo", SqlType = "VARCHAR(50)", DefaultValue = "'xyz'" },
            ],
        };
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["memo"] = "DF_t_memo_1234" };
        var (withDrop, _) = TableDdl.BuildAlter(t, new[] { "id", "memo" }, DbEngine.SqlServer, s => $"[{s}]",
            existingDefaultConstraints: map);
        int dropIdx = withDrop.FindIndex(s => s.Contains("DROP CONSTRAINT [DF_t_memo_1234]"));
        int addIdx = withDrop.FindIndex(s => s.Contains("ADD DEFAULT"));
        d("发现81 SqlServer 先删旧默认约束", dropIdx >= 0 && addIdx > dropIdx, string.Join(" | ", withDrop));
        var (noDrop, _) = TableDdl.BuildAlter(t, new[] { "id", "memo" }, DbEngine.SqlServer, s => $"[{s}]");
        d("发现81 无映射不生成 DROP", noDrop.All(s => !s.Contains("DROP CONSTRAINT")), string.Join(" | ", noDrop));
        d("发现81 新默认值仍落 ADD DEFAULT", withDrop.Any(s => s == "ALTER TABLE [t] ADD DEFAULT 'xyz' FOR [memo]"), string.Join(" | ", withDrop));
    }

    // 发现80：Oracle 列清单 12c+ 口径读 identity_column；11g 回退口径恒 '0'（回退分支由 GetColumns 异常链触发，云测首验）
    static void RunOracleIdentity(CheckD d)
    {
        var prov = new OracleSqlProbe();
        string sql = prov.ProbeColumns("t");
        d("发现80 Oracle 12c+ 读 identity_column", sql.Contains("identity_column", StringComparison.OrdinalIgnoreCase) && sql.Contains("user_tab_columns"), sql);
        d("发现80 口径仍带默认值原样列", sql.Contains("DATA_DEFAULT", StringComparison.OrdinalIgnoreCase), sql);
    }

    private sealed class OracleSqlProbe : OracleProvider
    {
        public OracleSqlProbe() : base(new ConnectionProfile { Engine = DbEngine.Oracle, Host = "h", User = "u", Password = "p" }) { }
        public string ProbeColumns(string table) => ListColumnsSql(null!, table);
    }

    // 发现147：正向同步——模型有库无 → CREATE；模型缺列 → ADD；库多余对象不开删
    static void RunErdForwardSync(CheckD d)
    {
        var model = new ErdModel();
        var t1 = ErdModel.FromSpecs("orders", new List<ColumnSpec>
        {
            new() { Name = "id", DataType = "INT", IsKey = true },
            new() { Name = "memo", DataType = "VARCHAR(50)" },
        });
        model.Tables.Add(t1);
        var live = new List<(string, List<ColumnSpec>)>();   // 库空：orders 整表缺失
        var diffs = ModelCompare.Diff(model, live);
        d("发现147 模型有库无出 table-missing", diffs.Any(x => x.Table == "orders" && x.Kind == "table-missing" && x.Actionable),
            string.Join("|", diffs.Select(x => x.Kind)));
        var src = model.Tables.Select(t => SchemaTableSnapshot.FromSpecs(t.Name, t.Columns.Select(c => new ColumnSpec
        {
            Name = c.Name, DataType = c.Type, IsKey = c.IsKey,
        }))).ToList();
        var (stmts, skipped) = SchemaDiff.PlanSync(src, new List<SchemaTableSnapshot>(), diffs, DbEngine.MySQL, s => $"`{s}`", dropMissing: false);
        d("发现147 整表缺失落 CREATE TABLE", stmts.Any(s => s.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase) && s.Contains("`orders`")),
            string.Join(" | ", stmts));
        d("发现147 不开删无 skipped 噪音", skipped.Count == 0, string.Join("|", skipped));

        // 模型缺列：库有 orders(id) 模型有 orders(id,memo) → ADD COLUMN
        var live2 = new List<(string, List<ColumnSpec>)> { ("orders", new List<ColumnSpec> { new() { Name = "id", DataType = "INT", IsKey = true } }) };
        var diffs2 = ModelCompare.Diff(model, live2);
        var src2 = model.Tables.Select(t => SchemaTableSnapshot.FromSpecs(t.Name, t.Columns.Select(c => new ColumnSpec
        {
            Name = c.Name, DataType = c.Type, IsKey = c.IsKey,
        }))).ToList();
        var dst2 = live2.Select(x => SchemaTableSnapshot.FromSpecs(x.Item1, x.Item2)).ToList();
        var (stmts2, _) = SchemaDiff.PlanSync(src2, dst2, diffs2.Where(x => x.Actionable).ToList(), DbEngine.MySQL, s => $"`{s}`", dropMissing: false);
        d("发现147 缺列落 ADD COLUMN", stmts2.Any(s => s.Contains("ADD") && s.Contains("`memo`")), string.Join(" | ", stmts2));
    }

    // 发现109：二进制列判定与 X'hex' 还原；非法 hex 抛 ArgumentException；Exporter 委托同口径
    static void RunBinaryLiteral(CheckD d)
    {
        d("发现109 二进制列判定", CsvImporter.IsBinaryColumn("VARBINARY") && CsvImporter.IsBinaryColumn("BLOB") && CsvImporter.IsBinaryColumn("RAW(16)")
            && !CsvImporter.IsBinaryColumn("VARCHAR(50)") && !CsvImporter.IsBinaryColumn(null), "");
        d("发现109 合法 hex 还原 X 字面量", CsvImporter.BinaryLiteral("deadBEef") == "X'DEADBEEF'", CsvImporter.BinaryLiteral("deadBEef"));
        bool thrown = false;
        try { CsvImporter.BinaryLiteral("abc"); }   // 奇数位
        catch (ArgumentException) { thrown = true; }
        d("发现109 奇数位 hex 拒绝", thrown, "");
        thrown = false;
        try { CsvImporter.BinaryLiteral("zz12"); }  // 非十六进制字符
        catch (ArgumentException) { thrown = true; }
        d("发现109 非法字符 hex 拒绝", thrown, "");
        d("发现109 Exporter 委托同口径", Exporter.BinaryLiteral("0a0b") == CsvImporter.BinaryLiteral("0a0b")
            && Exporter.IsBinaryColumn("bytea") == CsvImporter.IsBinaryColumn("bytea"), "");
        // 编辑值入口：二进制列走 X 字面量，普通列原样转义
        d("发现109 编辑值二进制列还原", Exporter.Literal(new ColumnSpec { Name = "c", DataType = "BLOB" }, "AB") == "X'AB'", "");
        d("发现109 编辑值普通列仍转义", Exporter.Literal(new ColumnSpec { Name = "c", DataType = "VARCHAR(10)" }, "it's") == "'it''s'", "");
    }

    // 发现115：持续时长承诺兑现——首次满足只记 MetSinceUtc 不触发，满持续分钟数才触发，中断清零
    static void RunAlertSustain(CheckD d)
    {
        var r = new AlertRule { Name = "conn", Metric = "connections", Op = ">", Threshold = 10, DurationMinutes = 5, CooldownMinutes = 0, Enabled = true };
        var t0 = new DateTime(2026, 11, 25, 8, 0, 0, DateTimeKind.Utc);
        var metrics = new Dictionary<string, double> { ["connections"] = 20 };
        var e1 = AlertEngine.Evaluate(new[] { r }, metrics, t0);
        d("发现115 首次满足不触发只记起点", e1.Count == 0 && r.MetSinceUtc is not null, r.MetSinceUtc ?? "null");
        var e2 = AlertEngine.Evaluate(new[] { r }, metrics, t0.AddMinutes(4));
        d("发现115 未满持续分钟不触发", e2.Count == 0, "");
        var e3 = AlertEngine.Evaluate(new[] { r }, metrics, t0.AddMinutes(5));
        d("发现115 满持续分钟触发", e3.Count == 1, e3.Count.ToString());
        r.MetSinceUtc = null;
        r.LastFiredUtc = null;
        AlertEngine.Evaluate(new[] { r }, metrics, t0);
        var metricsOk = new Dictionary<string, double> { ["connections"] = 1 };
        AlertEngine.Evaluate(new[] { r }, metricsOk, t0.AddMinutes(1));
        d("发现115 中断清零重新计时", r.MetSinceUtc is null, r.MetSinceUtc ?? "null");
    }

    // 发现117：种子派生可复现（同种子同值）；发现118：ClampInt 收敛到列长上限
    static void RunFakerSeed(CheckD d)
    {
        var col = new ColumnCtx { Name = "code", RawType = "CHAR(36)", MaxLen = 36 };
        var g1 = Faker.GuidLike(col, new Random(7), 1, 42);
        var g2 = Faker.GuidLike(col, new Random(99), 1, 42);
        d("发现117 同种子派生同值", Convert.ToString(g1) == Convert.ToString(g2), $"{g1}|{g2}");
        var g3 = Faker.GuidLike(col, new Random(7), 1, 43);
        d("发现117 异种子派生异值", Convert.ToString(g1) != Convert.ToString(g3), $"{g1}|{g3}");
        var clamped = (int)Faker.ClampInt(99999, new ColumnCtx { Name = "age", RawType = "TINYINT" });
        d("发现118 TINYINT 收敛到 127", clamped == 127, clamped.ToString());
        var clamped2 = (int)Faker.ClampInt(999999, new ColumnCtx { Name = "n", RawType = "SMALLINT" });
        d("发现118 SMALLINT 收敛到 32767", clamped2 == 32767, clamped2.ToString());
        var neg = (int)Faker.ClampInt(-5, new ColumnCtx { Name = "qty", RawType = "INT" });
        d("发现118 无界列原样保留", neg == -5, neg.ToString());
    }

    // 发现120/121：复制目录——支持引擎给 SQL，不支持引擎给归真人话
    static void RunReplicationCatalog(CheckD d)
    {
        d("发现120 支持引擎三库", ReplicationCatalog.Supported(DbEngine.MySQL) && ReplicationCatalog.Supported(DbEngine.PostgreSQL) && ReplicationCatalog.Supported(DbEngine.SqlServer), "");
        d("发现120 不支持引擎明示", !ReplicationCatalog.Supported(DbEngine.SQLite) && ReplicationCatalog.UnsupportedReason(DbEngine.SQLite).Length > 0, ReplicationCatalog.UnsupportedReason(DbEngine.SQLite));
        d("发现120 从库清单 SQL 非空", ReplicationCatalog.ReplicasSql(DbEngine.MySQL).Length > 0 && ReplicationCatalog.ReplicasSql(DbEngine.PostgreSQL).Length > 0, "");
        d("发现121 延迟分级", ReplicationCatalog.LagClass(0.5).State != ReplicationCatalog.LagClass(600).State, "");
    }

    // 发现122/123：再均衡——完全一致才 Balanced；{1,2} 无可行整数搬迁归真 Balanced=false
    static void RunRebalance(CheckD d)
    {
        var even = RebalancePlanner.Plan(new List<RebalanceShard> { new("s0", 2), new("s1", 2) });
        d("发现123 负载一致即均衡", even.Balanced && even.Moves.Count == 0, $"{even.Balanced}|{even.Moves.Count}");
        var uneven = RebalancePlanner.Plan(new List<RebalanceShard> { new("s0", 1), new("s1", 2) });
        d("发现123 无可行整数搬迁不虚报均衡", !uneven.Balanced, $"{uneven.Balanced}|{uneven.Moves.Count}");
        var fixable = RebalancePlanner.Plan(new List<RebalanceShard> { new("s0", 0), new("s1", 4) });
        d("发现122 有超差给搬迁计划", !fixable.Balanced && fixable.Moves.Count > 0, $"{fixable.Balanced}|{fixable.Moves.Count}");
    }

    // 发现95：迁移清单补齐（含 tao_backup_history.json）；发现108/112：Tao 系列存储统一走数据根
    static void RunTaoPaths(CheckD d)
    {
        string oldDir = Path.Combine(Path.GetTempPath(), $"tao-mig-{Guid.NewGuid():N}");
        string target = Path.Combine(Path.GetTempPath(), $"tao-mig-dst-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(oldDir);
            File.WriteAllText(Path.Combine(oldDir, "tao_backup_history.json"), "[]");
            File.WriteAllText(Path.Combine(oldDir, "conn-groups.json"), "[]");
            var moved = TaoPaths.MigrateLegacyFiles(new[] { oldDir }, target);
            d("发现95 备份账本入迁移清单", moved.Contains("tao_backup_history.json") && File.Exists(Path.Combine(target, "tao_backup_history.json")), string.Join("|", moved));
            d("发现95 连接分组入迁移清单", moved.Contains("conn-groups.json") && File.Exists(Path.Combine(target, "conn-groups.json")), string.Join("|", moved));
            // 同名不覆盖
            File.WriteAllText(Path.Combine(target, "conn-groups.json"), "[]");
            File.WriteAllText(Path.Combine(oldDir, "conn-groups.json"), "[1]");
            var moved2 = TaoPaths.MigrateLegacyFiles(new[] { oldDir }, target);
            d("发现95 同名不覆盖", !moved2.Contains("conn-groups.json"), string.Join("|", moved2));
        }
        finally
        {
            try { Directory.Delete(oldDir, true); } catch { }
            try { Directory.Delete(target, true); } catch { }
        }
        d("发现108/112 分组存储走数据根", TaoPaths.FilePath("conn-groups.json").EndsWith("conn-groups.json", StringComparison.Ordinal)
            && TaoPaths.FilePath("conn-groups.json").StartsWith(TaoPaths.Root, StringComparison.Ordinal), TaoPaths.FilePath("conn-groups.json"));
    }

    // 发现101：ODBC 连接串特殊字符包 {} 且内层 } 翻倍
    static void RunOdbcEscape(CheckD d)
    {
        var p = new ConnectionProfile { Engine = DbEngine.Odbc, Database = "D", User = "u}1", Password = "p;1" };
        var s = OdbcCore.BuildConnectionString(p);
        d("发现101 分号值包花括号", s.Contains("PWD={p;1}", StringComparison.Ordinal), s);
        d("发现101 花括号翻倍", s.Contains("UID={u}}1}", StringComparison.Ordinal), s);
        var plain = OdbcCore.BuildConnectionString(new ConnectionProfile { Engine = DbEngine.Odbc, Database = "D", User = "u", Password = "p" });
        d("发现101 普通值原样", plain == "DSN=D;UID=u;PWD=p", plain);
    }

    // 发现89/92/98/99/128：手册新增章节与口径归真（章节数量/字数等结构锚在 MAN-* 保持）
    static void RunManualContent(CheckD d)
    {
        var ch = ManualContent.Chapters;
        string all = string.Join("\n", ch.SelectMany(c => c.Sections.Select(s => s.Heading + "\n" + s.Body)));
        d("发现92 手册含数据导入节", ch.SelectMany(c => c.Sections).Any(s => s.Heading.Contains("数据导入") || s.Body.Contains("数据导入")), "");
        d("发现98 导出章覆盖六格式", new[] { "CSV", "INSERT", "Excel", "XML", "HTML", "JSON" }.All(k => all.Contains(k)), "");
        d("发现98 导出章含范围选择", all.Contains("当前页") || all.Contains("筛选结果"), "");
        d("发现99 连接管理章含分组与星标", all.Contains("分组") && all.Contains("星标"), "");
        d("发现89 连接管理章含哨兵与集群", all.Contains("哨兵") && all.Contains("集群"), "");
        d("发现128 卸载口径归真数据根", all.Contains("数据根"), "");
        d("发现128 快捷键章含 F11 专注模式", ch.First(c => c.Title.Contains("快捷键")).Sections.Any(s => s.Body.Contains("F11")), "");
    }
}
