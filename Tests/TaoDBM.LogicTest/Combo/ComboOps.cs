using System.Security.Cryptography;
using System.Text;
using TaoDBM.Core;
using TaoDBM.Core.Streaming;
using TaoDBM.UI;

namespace TaoDBM.LogicTest.Combo;

/// <summary>
/// COMBO MATRIX OPERATION REGISTRY (Phase 2 full coverage batch).
/// One ComboOp per usable operation: 18 connection-menu items (C01..C18),
/// 12 database-menu items (D01..D12) and extended operations (X01..X11).
/// Every op runs on the shared ComboData workspace for a given State and Dialect,
/// and returns null when all of its postconditions hold, else a problem detail.
/// </summary>
public sealed class ComboOp
{
    public string Id = "";
    public string Label = "";
    /// <summary>CONN = connection right-click menu, DB = database right-click menu, EXT = extended operation.</summary>
    public string MenuPath = "";
    /// <summary>Real engine source path backing this operation.</summary>
    public string EnginePath = "";
    /// <summary>PLAN = pure derivation, no dataset mutation; TRANS = mutates ComboData.Rows; ENTRY = config/entry orchestration.</summary>
    public string Kind = "";
    /// <summary>Returns null when the op is runnable on the dialect, else the not-composable reason with code refs.</summary>
    public Func<DbEngine, string?> Gate = _ => null;
    public Func<ComboData, DbEngine, string?> Run = (_, _) => null;
}

/// <summary>Shared per-cell workspace: one logical table plus derived artifacts.</summary>
public sealed class ComboData
{
    public string Table => "t_user";
    public List<ColumnSpec> Columns { get; } = new();
    public List<object?[]> Rows { get; } = new();
    /// <summary>Caliber row count (S3 HUGE keeps 500 sample rows but declares 10M).</summary>
    public long DeclaredRows { get; set; }
    public string PkColumn => Columns.FirstOrDefault(c => c.IsKey)?.Name ?? "id";
    /// <summary>Pair-specific scratch artifacts (not part of the fingerprint).</summary>
    public Dictionary<string, object> Bag { get; } = new();

    public ComboData Clone()
    {
        var d = new ComboData { DeclaredRows = DeclaredRows };
        foreach (var c in Columns) d.Columns.Add(new ColumnSpec { Name = c.Name, DataType = c.DataType, Nullable = c.Nullable, IsKey = c.IsKey });
        foreach (var r in Rows) d.Rows.Add((object?[])r.Clone());
        return d;
    }

    /// <summary>Stable fingerprint of schema+data (sampled rows for large sets; deterministic).</summary>
    public string Fingerprint()
    {
        var sb = new StringBuilder();
        sb.Append(DeclaredRows).Append('|');
        foreach (var c in Columns) sb.Append(c.Name).Append(':').Append(c.DataType).Append(c.IsKey ? '*' : "").Append(',');
        sb.Append('#').Append(Rows.Count).Append('\n');
        int n = Rows.Count;
        IEnumerable<object?[]> sample = n <= 200 ? Rows : Rows.Take(100).Concat(Rows.Skip(n - 100));
        foreach (var r in sample)
            foreach (var v in r) sb.Append(v?.ToString() ?? "\u0002").Append('\u0001');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

public static class ComboOps
{
    public const string StateEmpty = "EMPTY";
    public const string StateWan = "WAN";
    public const string StateHuge = "HUGE";
    public const string StateSpecial = "SPC";
    public const string StateIllegal = "ILL";

    public static readonly string[] States = { StateEmpty, StateWan, StateHuge, StateSpecial, StateIllegal };
    public static readonly DbEngine[] Dialects = { DbEngine.MySQL, DbEngine.PostgreSQL, DbEngine.SqlServer, DbEngine.SQLite };

    public static string DialectName(DbEngine e) => e switch
    {
        DbEngine.MySQL => "MYSQL",
        DbEngine.PostgreSQL => "POSTGRESQL",
        DbEngine.SqlServer => "SQLSERVER",
        DbEngine.SQLite => "SQLITE",
        _ => e.ToString().ToUpperInvariant(),
    };

    public static Func<string, string> Quoter(DbEngine e) => e switch
    {
        DbEngine.MySQL => n => "`" + n.Replace("`", "") + "`",
        DbEngine.SqlServer => n => "[" + n.Replace("]", "]]") + "]",
        _ => n => "\"" + n.Replace("\"", "\"\"") + "\"",
    };

    // ---------- state builders ----------

    public static ComboData BuildState(string state)
    {
        var d = new ComboData
        {
            DeclaredRows = state switch { StateHuge => 10_000_000, _ => 0L },
        };
        d.Columns.Add(new ColumnSpec { Name = "id", DataType = "int", Nullable = false, IsKey = true });
        d.Columns.Add(new ColumnSpec { Name = "name", DataType = "varchar(50)", Nullable = false });
        d.Columns.Add(new ColumnSpec { Name = "phone", DataType = "varchar(20)" });
        d.Columns.Add(new ColumnSpec { Name = "email", DataType = "varchar(100)" });
        d.Columns.Add(new ColumnSpec { Name = "amount", DataType = "decimal(10,2)" });
        d.Columns.Add(new ColumnSpec { Name = "note", DataType = "varchar(200)" });

        void AddRows(int count, Func<int, int, object?[]> f)
        {
            for (int i = 0; i < count; i++) d.Rows.Add(f(i, count));
        }

        if (state == StateEmpty)
        {
            d.DeclaredRows = 0;
        }
        else if (state == StateWan)
        {
            d.DeclaredRows = 10_000;
            AddRows(10_000, (i, _) => Row(i));
        }
        else if (state == StateHuge)
        {
            d.DeclaredRows = 10_000_000;
            AddRows(500, (i, _) => Row(i));
        }
        else if (state == StateSpecial)
        {
            d.DeclaredRows = 60;
            string[] specials =
            {
                "有,逗号 \"引号\" '单引'", "换\n行符", "emoji \U0001F600\u2728", "中文；分号；全角",
                new string('长', 300), "tab\t制表", "100%混合\t\r\n三件套", "null字样但非null",
            };
            AddRows(60, (i, n) => Row(i, name: specials[i % specials.Length], note: i % 4 == 0 ? null : specials[(i + 3) % specials.Length]));
        }
        else if (state == StateIllegal)
        {
            d.DeclaredRows = 40;
            string[] illegal =
            {
                "' OR '1'='1' --", "'; DROP TABLE t_user; --", "\u0001\u0004\u001F控制字符", "\\", "\u0000嵌入零字节",
                new string('x', 2000), "-2147483648", "NaN", "1e999", "\"\"\"",
            };
            AddRows(40, (i, n) => Row(i, name: illegal[i % illegal.Length], note: illegal[(i + 5) % illegal.Length]));
        }
        return d;
    }

    private static object?[] Row(int i, string? name = null, string? note = null) => new object?[]
    {
        i + 1,
        name ?? $"用户{i % 97:00}",
        i % 5 == 0 ? null : $"138{i:8}",
        i % 7 == 0 ? null : $"u{i}@example.com",
        i % 3 == 0 ? null : (object)(i * 1.5),
        note ?? $"备注{i % 13}",
    };

    // ---------- gates (code-path based) ----------

    private static string? GateSharding(DbEngine e) => ShardingWizard.Supported(e)
        ? null
        : $"NOT COMPOSABLE: ShardingWizard.Supported=false on {DialectName(e)} (Src/TaoDBM.Core/ShardingWizard.cs:6)";

    private static string? GateReplication(DbEngine e) => ReplicationCatalog.Supported(e)
        ? null
        : $"NOT COMPOSABLE: ReplicationCatalog.Supported=false on {DialectName(e)} (Src/TaoDBM.Core/ReplicationCatalog.cs:14)";

    private static string? GateTypeLen(DbEngine e) => TypeLengthAdvisor.Supported(e)
        ? null
        : $"NOT COMPOSABLE: TypeLengthAdvisor.Supported=false on {DialectName(e)} (Src/TaoDBM.Core/TypeLengthAdvisor.cs:26)";

    // ---------- registry ----------

    public static List<ComboOp> All()
    {
        var list = new List<ComboOp>
        {
            // ===== connection right-click menu (17) =====
            new()
            {
                Id = "C01", Label = "EDIT-CONNECTION", MenuPath = "CONN", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/ConnectionDialog.cs + Src/TaoDBM.Infrastructure/ConnectionStore.cs",
                Run = (d, eng) =>
                {
                    var p = new ConnectionProfile { Engine = eng, DisplayName = "combo-conn", Host = "127.0.0.1", Port = EngineInfo.DefaultPort(eng), Database = "db", Password = "pw" + d.Rows.Count };
                    var protected1 = ProfileCrypto.Protect(p.Password);
                    if (protected1 == p.Password) return "password stored in plaintext";
                    var restored = ProfileCrypto.Unprotect(protected1);
                    if (restored != p.Password && restored != "") return "protect/unprotect roundtrip mismatch";
                    if (EngineInfo.DefaultPort(eng) <= 0 && !EngineInfo.IsFileBased(eng)) return "default port missing";
                    return null;
                },
            },
            new()
            {
                Id = "C02", Label = "PERMISSION-AUDIT", MenuPath = "CONN", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.Core/PermissionAuditor.cs",
                Run = (d, eng) =>
                {
                    var grants = new[] { $"GRANT ALL PRIVILEGES ON *.* TO 'wide'@'%' WITH GRANT OPTION;", $"GRANT SELECT ON {d.Table} TO 'app'@'10.%';" };
                    var recs = PermissionAuditor.Parse(grants);
                    if (recs.Count != 2) return $"parse records {recs.Count} != 2";
                    var finds = PermissionAuditor.Audit(recs);
                    if (!finds.Any(f => f.Code == "R101")) return "wide-account R101 not reported";
                    if (!finds.Any(f => f.Code == "R102")) return "grant-option R102 not reported";
                    var rev = PermissionAuditor.RevokeSql(recs[0]);
                    if (!rev[0].StartsWith("REVOKE", StringComparison.Ordinal)) return "revoke sql malformed";
                    return null;
                },
            },
            new()
            {
                Id = "C03", Label = "REPLICATION-PANEL", MenuPath = "CONN", Kind = "PLAN", Gate = GateReplication,
                EnginePath = "Src/TaoDBM.Core/ReplicationCatalog.cs",
                Run = (d, eng) =>
                {
                    if (!ReplicationCatalog.Supported(eng)) return "unexpected gate pass";
                    var probe = ReplicationCatalog.RoleProbeSql(eng);
                    if (string.IsNullOrWhiteSpace(probe)) return "role probe empty";
                    var rep = ReplicationCatalog.ReplicasSql(eng);
                    if (string.IsNullOrWhiteSpace(rep)) return "replica list sql empty";
                    if (ReplicationCatalog.LagClass(100).Item2 != "danger") return "lag class danger wrong";
                    return null;
                },
            },
            new()
            {
                Id = "C04", Label = "SHARDING-WIZARD", MenuPath = "CONN", Kind = "PLAN", Gate = GateSharding,
                EnginePath = "Src/TaoDBM.Core/ShardingWizard.cs",
                Run = (d, eng) =>
                {
                    if (!ShardingWizard.Supported(eng)) return "unexpected gate pass";
                    var hash = ShardingWizard.HashPartitionSql(eng, d.Table, d.PkColumn, 8);
                    if (hash.Count < 2) return "hash partition plan too thin";
                    if (!hash.Any(x => x.Contains("HASH"))) return "hash DDL missing HASH strategy";
                    if (!hash.Any(x => x.Contains(d.Table))) return "table name missing in plan";
                    var range = ShardingWizard.RangePartitionSql(eng, d.Table, d.PkColumn, new[] { "0", "1000", "2000" });
                    if (range.Count == 0) return "range partition plan empty";
                    var info = ShardingWizard.PartitionsInfoSql(eng, "dbo", d.Table);
                    if (string.IsNullOrWhiteSpace(info)) return "partition info sql empty";
                    if (!ShardingWizard.KeyBacked(d.PkColumn, new[] { d.PkColumn, "x" })) return "index-backed check wrong";
                    return null;
                },
            },
            new()
            {
                Id = "C05", Label = "ONE-CLICK-DIAGNOSTICS", MenuPath = "CONN", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/DiagnosticsDialog.cs + Src/TaoDBM.Core/LockAnalyzer.cs + Src/TaoDBM.Core/SmartLine.cs",
                Run = (d, eng) =>
                {
                    var waits = new[] { new LockWait("S1", "S2", "r" + (d.Rows.Count > 0 ? d.Rows[0][0] : "x")), new LockWait("S2", "S1", "r1") };
                    var cycles = LockAnalyzer.FindDeadlocks(waits);
                    if (cycles.Count != 1) return $"deadlock cycles {cycles.Count} != 1";
                    var victim = LockAnalyzer.PickVictim(cycles[0], new Dictionary<string, SessionInfo> { ["S1"] = new("S1", "u", "UPD", 1000), ["S2"] = new("S2", "u", "UPD", 5) });
                    if (victim != "S2") return $"victim {victim} != S2";
                    var kill = LockAnalyzer.KillSql(victim, eng);
                    if (eng == DbEngine.SQLite && kill != null) return "SQLite kill must be null (LockAnalyzer.cs:72)";
                    if (eng != DbEngine.SQLite && string.IsNullOrEmpty(kill)) return "kill sql empty";
                    var sql = "SELECT * FROM " + d.Table + " WHERE note LIKE '%x'";
                    var finds = SlowQueryDiagnoser.Analyze(sql);
                    if (!finds.Any(f => f.Code == "S001")) return "S001 select-star missing";
                    return null;
                },
            },
            new()
            {
                Id = "C06", Label = "RISKY-SQL-GUARD", MenuPath = "CONN", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.Core/RiskySqlGuard.cs",
                Run = (d, eng) =>
                {
                    var q = Quoter(eng);
                    var noWhere = $"UPDATE {d.Table} SET note = 'x'";
                    var chk = RiskySqlGuard.Inspect(noWhere, eng, q);
                    if (chk.Level != "danger" || chk.Kind != "no-where") return $"level {chk.Level}/{chk.Kind} != danger/no-where";
                    if (string.IsNullOrEmpty(chk.EstimateSql)) return "estimate sql missing";
                    var chk2 = RiskySqlGuard.Inspect($"DROP TABLE {d.Table}", eng, q);
                    if (chk2.Level != "danger") return "drop not flagged danger";
                    return null;
                },
            },
            new()
            {
                Id = "C07", Label = "SQL-RELEASE-CHECK", MenuPath = "CONN", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.Core/SqlReleaseCheck.cs",
                Run = (d, eng) =>
                {
                    var (score, finds) = SqlReleaseCheck.Review($"DELETE FROM {d.Table};", eng);
                    if (score >= 100) return "no-where delete not penalized";
                    if (!finds.Any(f => f.Level == "block")) return "block finding missing";
                    if (SqlReleaseCheck.Verdict(95) != "可上线") return "verdict mapping wrong";
                    return null;
                },
            },
            new()
            {
                Id = "C08", Label = "TASK-CENTER-NOTIFY", MenuPath = "CONN", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/TaskCenterDialog.cs + Src/TaoDBM.Core/NotificationDispatcher.cs",
                Run = (d, eng) =>
                {
                    var evt = new NotifyEvent(NotifyKind.RiskySqlDone, d.Table, "t", "m", DateTime.Now);
                    if (NotificationDispatcher.ThrottleKey(evt) != "RiskySqlDone:" + d.Table) return "throttle key wrong";
                    var disp = new NotificationDispatcher();
                    if (!disp.TryAccept(evt)) return "first event rejected";
                    if (disp.TryAccept(evt)) return "second event within throttle window accepted";
                    var off = new NotificationDispatcher(new NotifyOptions { Enabled = false });
                    if (off.TryAccept(evt)) return "global-off event accepted";
                    return null;
                },
            },
            new()
            {
                Id = "C09", Label = "STREAMING-SWITCH", MenuPath = "CONN", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/StreamingSwitchDialog.cs + Src/TaoDBM.Core/Streaming/StreamingSwitch.cs",
                Run = (d, eng) => StreamingSwitch.Enabled == (Environment.GetEnvironmentVariable("DBM_STREAMING") != "off") ? null : "switch state out of sync with env",
            },
            new()
            {
                Id = "C10", Label = "READ-ROUTE-REHEARSAL", MenuPath = "CONN", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/ReadRouterDialog.cs + Src/TaoDBM.Core/ReadRouter.cs",
                Run = (d, eng) =>
                {
                    var nodes = new[] { new ReplicaNode("r1", 1, true), new ReplicaNode("r2", 5, true), new ReplicaNode("r3", 99, true) };
                    var dec = ReadRouter.Route(nodes, 10);
                    if (dec.FellBackToPrimary) return "eligible replicas fell back";
                    if (dec.EligibleReplicas.Count != 2 || dec.EligibleReplicas[0] != "r1") return "route order wrong";
                    var dec2 = ReadRouter.Route(nodes, 0);
                    if (!dec2.FellBackToPrimary) return "no-eligible must fall back to primary";
                    return null;
                },
            },
            new()
            {
                Id = "C11", Label = "HOTSPOT-DETECT", MenuPath = "CONN", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/HotspotDetectorDialog.cs + Src/TaoDBM.Core/HotspotDetector.cs",
                Run = (d, eng) =>
                {
                    var rep = HotspotDetector.Analyze(new[] { new ShardLoad("s0", 100), new ShardLoad("s1", 1), new ShardLoad("s2", 1), new ShardLoad("s3", 1) });
                    if (!rep.HasHotSpot) return "3x shard not flagged hot";
                    if (rep.Verdicts.Count != 4) return "verdict count wrong";
                    if (rep.Suggestions.Count == 0) return "no suggestions";
                    return null;
                },
            },
            new()
            {
                Id = "C12", Label = "REBALANCE-PLAN", MenuPath = "CONN", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/RebalancePlannerDialog.cs + Src/TaoDBM.Core/RebalancePlanner.cs",
                Run = (d, eng) =>
                {
                    var plan = RebalancePlanner.Plan(new[] { new RebalanceShard("s0", 3000), new RebalanceShard("s1", 1000) });
                    if (plan.Balanced) return "unbalanced shards marked balanced";
                    if (plan.Moves.Sum(m => m.Rows) != 1000) return $"moved {plan.Moves.Sum(m => m.Rows)} != 1000";
                    var even = RebalancePlanner.Plan(new[] { new RebalanceShard("a", 1), new RebalanceShard("b", 1) });
                    if (!even.Balanced || even.Moves.Count != 0) return "even shards generated moves";
                    return null;
                },
            },
            new()
            {
                Id = "C13", Label = "SHARD-KEY-EVALUATOR", MenuPath = "CONN", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/ShardKeyEvaluatorDialog.cs + Src/TaoDBM.Core/ShardKeyEvaluator.cs",
                Run = (d, eng) =>
                {
                    var vs = ShardKeyEvaluator.Evaluate(new[]
                    {
                        new ShardKeyCandidate("id", d.Rows.Count, 1.0, 10, true),
                        new ShardKeyCandidate("note", 0, 0, 0, false),
                    });
                    if (vs.Count != 2) return "verdict count wrong";
                    var bad = vs.First(v => v.Column == "note");
                    if (bad.Score > 40) return "zero-cardinality scored too high";
                    int r1 = ShardKeyEvaluator.HashRoute("k1", 8), r2 = ShardKeyEvaluator.HashRoute("k1", 8);
                    if (r1 != r2 || r1 < 0 || r1 >= 8) return "hash route unstable or out of range";
                    return null;
                },
            },
            new()
            {
                Id = "C14", Label = "MIGRATION-RESUME", MenuPath = "CONN", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/MigrationResumeDialog.cs + Src/TaoDBM.Core/MigrationResume.cs",
                Run = (d, eng) =>
                {
                    long total = Math.Max(d.DeclaredRows, 1);
                    var manifest = new MigrationManifest("MF1", "src", "dst", DateTimeOffset.UtcNow,
                        new[] { new MigrationTable(d.Table, total, 4) });
                    var st = MigrationResume.Fresh(manifest);
                    st = MigrationResume.Advance(st, d.Table, 2, "k2", 4);
                    var json = MigrationResume.ToJson(st);
                    var back = MigrationResume.FromJson(json);
                    if (back == null || back.Tables[0].BatchDone != 2) return "state json roundtrip failed";
                    var plan = MigrationResume.Plan(manifest, back);
                    if (plan.Actions[0].Action != "resume" || plan.Actions[0].FromBatch != 2) return "resume action wrong";
                    try { MigrationResume.Plan(manifest, new MigrationState("OTHER", back.Tables)); return "manifest mismatch not rejected"; }
                    catch (ArgumentException) { return null; }
                },
            },
            new()
            {
                Id = "C15", Label = "TEAM-MERGE", MenuPath = "CONN", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/TeamShareMergeDialog.cs + Src/TaoDBM.Core/TeamShareMerge.cs",
                Run = (d, eng) =>
                {
                    var item = new ShareItem("it1", "query", "SELECT 1", DateTimeOffset.UtcNow, "a");
                    var local = new List<ShareItem> { item };
                    var remote = new List<ShareItem> { item with { Author = "b", UpdatedAt = item.UpdatedAt.AddMinutes(1) }, new ShareItem("it2", "query", "SELECT 2", item.UpdatedAt, "b") };
                    var merged = TeamShareMerge.Merge(local, remote);
                    if (merged.Merged.Count != 2 || merged.Updated != 1 || merged.Added != 1) return $"merge stats wrong ({merged.Added}+{merged.Updated})";
                    if (merged.Merged.First(x => x.ItemId == "it1").Author != "b") return "newer remote version must win";
                    var (clean, dropped) = TeamShareMerge.Sanitize(item with { Payload = new string('a', 4096) });
                    if (dropped.Count > 0) return "oversized payload should be trimmed not dropped";
                    return null;
                },
            },
            new()
            {
                Id = "C16", Label = "SHARE-PACKET", MenuPath = "CONN", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/SharePacketBuilderDialog.cs + Src/TaoDBM.Core/SharePacketBuilder.cs",
                Run = (d, eng) =>
                {
                    var payload = string.Join("\n", d.Rows.Take(Math.Min(50, d.Rows.Count)).Select(r => string.Join("|", r.Select(v => v?.ToString() ?? "N"))));
                    var pkt = SharePacketBuilder.BuildRaw(string.IsNullOrWhiteSpace(payload) ? "empty" : payload, "combo", DateTimeOffset.UtcNow);
                    var ok = SharePacketBuilder.Verify(pkt);
                    if (!ok.Ok) return "packet verify failed: " + ok.Reason;
                    var chunks = SharePacketBuilder.Chunk(pkt, 256);
                    if (chunks.Count < 1) return "chunking produced nothing";
                    if (SharePacketBuilder.Assemble(chunks) != pkt.Payload) return "chunk assemble roundtrip mismatch";
                    return null;
                },
            },
            new()
            {
                Id = "C17", Label = "SMART-TOOLS", MenuPath = "CONN", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/SmartToolsDialog.cs + Src/TaoDBM.Core/SmartLine.cs",
                Run = (d, eng) =>
                {
                    var sql = $"SELECT * FROM {d.Table} WHERE note LIKE '%x' OR name = 'a'";
                    var finds = SlowQueryDiagnoser.Analyze(sql);
                    if (!finds.Any(f => f.Code == "S002")) return "leading-wildcard not flagged";
                    var ddl = SlowQueryDiagnoser.SuggestIndexDdl(sql, d.Table);
                    if (ddl.Count == 0) return "index suggestion empty";
                    return null;
                },
            },
            new()
            {
                Id = "C18", Label = "SHARD-ROUTE-LINK", MenuPath = "CONN", Kind = "PLAN", Gate = GateSharding,
                EnginePath = "Src/TaoDBM.Core/ShardRouting.cs",
                Run = (d, eng) =>
                {
                    if (!ShardingWizard.Supported(eng)) return "unexpected gate pass";
                    var hashPlan = new ShardPlan(d.Table, d.PkColumn, ShardingWizard.ShardStrategy.Hash, 4,
                        Array.Empty<string>(), Array.Empty<ShardCategory>());
                    if (ShardRouting.RouteToShard(hashPlan, "7") != 3) return "hash numeric route wrong";
                    if (ShardRouting.RouteToShard(hashPlan, "7") != ShardRouting.RouteToShard(hashPlan, "7")) return "hash route unstable";
                    var rangePlan = new ShardPlan(d.Table, d.PkColumn, ShardingWizard.ShardStrategy.Range, 4,
                        new[] { "0", "1000", "2000" }, Array.Empty<ShardCategory>());
                    if (ShardRouting.RouteToShard(rangePlan, "-5") != 0) return "range below-first wrong";
                    if (ShardRouting.RouteToShard(rangePlan, "1500") != 2) return "range mid wrong";
                    if (ShardRouting.RouteToShard(rangePlan, "9999") != 3) return "range maxvalue wrong";
                    var listPlan = new ShardPlan(d.Table, d.PkColumn, ShardingWizard.ShardStrategy.List, 2,
                        Array.Empty<string>(), new ShardCategory[] { new("south", new[] { "1", "2" }), new("north", new[] { "3", "4" }) });
                    if (ShardRouting.RouteToShard(listPlan, "2") != 0) return "list south wrong";
                    if (ShardRouting.RouteToShard(listPlan, "4") != 1) return "list north wrong";
                    bool rejected = false;
                    try { ShardRouting.RouteToShard(listPlan, "99"); } catch (ArgumentException) { rejected = true; }
                    if (!rejected) return "list unmatched must reject";
                    var replicas = new[] { new ReplicaNode("r1", 5, true), new ReplicaNode("r2", 30, true) };
                    var pv = ShardRouting.Preview(hashPlan, new[] { "7", "8", "9" }, replicas, 10);
                    if (pv.Routes.Count != 3) return "preview route count wrong";
                    if (!pv.ReadDecision.HasTarget || pv.ReadDecision.FellBackToPrimary) return "read route should hit r1";
                    if (!pv.ReadDecision.EligibleReplicas[0].Equals("r1")) return "read route order wrong";
                    var topo = ShardRouting.TopologyRows(hashPlan);
                    if (topo.Count != 4 || topo.Any(t => t.Role != "分片")) return "topology rows wrong";
                    if (ShardRouting.PlanSummary(rangePlan).Length == 0) return "summary empty";
                    return null;
                },
            },

            // ===== database right-click menu (12) =====
            new()
            {
                Id = "D01", Label = "NEW-QUERY", MenuPath = "DB", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/MainWindow.xaml.cs(OpenQueryTab) + Src/TaoDBM.Infrastructure/Providers/RelationalProvider.cs(SqlSplitter)",
                Run = (d, eng) =>
                {
                    var lit = d.Rows.Count > 0 ? d.Rows[0][5]?.ToString() ?? "x" : "x";
                    var script = $"SELECT 1; -- {lit}\nSELECT '{lit}'; SELECT 2";
                    var parts = SqlSplitter.Split(script);
                    if (parts.Count != 3) return $"split count {parts.Count} != 3";
                    if (!parts[1].Contains(lit)) return "literal lost";
                    return null;
                },
            },
            new()
            {
                Id = "D02", Label = "BROWSE-EXPORT", MenuPath = "DB", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/GridBuilder.cs + Src/TaoDBM.App/UI/Exporter.cs",
                Run = (d, eng) =>
                {
                    var r = new QueryResult { Columns = { "id", "name", "phone", "email", "amount", "note" } };
                    foreach (var row in d.Rows.Take(500)) r.Rows.Add(row);
                    var csv = Exporter.Csv(r);
                    if (!csv.StartsWith('\uFEFF')) return "csv BOM missing";
                    if (csv.Count(c => c == '\n') < d.Rows.Take(500).Count()) return "csv rows lost";
                    var ins = Exporter.InsertSql(r, d.Table);
                    if (d.Rows.Count > 0 && !ins.Contains("INSERT")) return "insert sql missing";
                    if (d.Rows.Count > 0 && d.Rows[0][1]?.ToString()?.Contains('\'') == true && ins.Contains("'\\''")) return "bad escaping";
                    return null;
                },
            },
            new()
            {
                Id = "D03", Label = "TABLE-DESIGNER", MenuPath = "DB", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/TableDesignerDialog.cs + Src/TaoDBM.Core/TableDdl.cs",
                Run = (d, eng) =>
                {
                    var q = Quoter(eng);
                    var create = TableDdl.BuildCreate(TableDesign.FromSpecs(d.Table, d.Columns), eng, q);
                    if (!create.Contains("CREATE TABLE")) return "create sql missing";
                    var (alt, skip) = TableDdl.BuildAlter(TableDesign.FromSpecs(d.Table, d.Columns), new[] { d.PkColumn }, eng, q);
                    if (alt.Count == 0 && skip.Count == 0) return "alter produced nothing";
                    return null;
                },
            },
            new()
            {
                Id = "D04", Label = "BACKUP-RESTORE", MenuPath = "DB", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/BackupDialog.cs + Src/TaoDBM.Core/BackupTools.cs",
                Run = (d, eng) =>
                {
                    if (!BackupTools.Supported(eng)) return "engine unsupported for backup";
                    if (!string.IsNullOrEmpty(BackupTools.UnsupportedReason(eng))) return "reason should be empty for supported engine";
                    switch (eng)
                    {
                        case DbEngine.MySQL:
                            if (!BackupTools.MysqlDump("h", 3306, "u", "p", "db", "f").FileName.Contains("mysqldump")) return "mysql tool wrong";
                            if (BackupTools.QuoteArg("a b") != "\"a b\"") return "arg quoting wrong";
                            break;
                        case DbEngine.PostgreSQL:
                            if (!BackupTools.PgDump("h", 5432, "u", "p", "db", "f").FileName.Contains("pg_dump")) return "pg tool wrong";
                            break;
                        case DbEngine.SqlServer:
                            if (!BackupTools.SqlServerBackupSql("db", "f.bak").Contains("BACKUP DATABASE")) return "tsql backup wrong";
                            break;
                        case DbEngine.SQLite:
                            var tmp = Path.GetTempPath();
                            var src = Path.Combine(tmp, "combo-bk-" + Guid.NewGuid().ToString("N"));
                            var dst = Path.Combine(tmp, "combo-bk2-" + Guid.NewGuid().ToString("N"));
                            File.WriteAllText(src, "sqlite-stub");
                            BackupTools.SqliteBackup(src, dst);
                            var okCopy = File.Exists(dst);
                            File.Delete(src);
                            if (File.Exists(dst)) File.Delete(dst);
                            if (!okCopy) return "sqlite backup copy failed";
                            break;
                    }
                    return null;
                },
            },
            new()
            {
                Id = "D05", Label = "BACKUP-PLANNER", MenuPath = "DB", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/BackupPlanDialog.cs + Src/TaoDBM.Core/BackupPlanner.cs",
                Run = (d, eng) =>
                {
                    var anchor = DateTimeOffset.UtcNow.AddHours(-48);
                    var policy = new BackupPolicy(24, 6, 2, anchor);
                    var slots = BackupPlanner.Schedule(policy, anchor, DateTimeOffset.UtcNow);
                    if (slots.Count == 0) return "no slots in 48h window";
                    if (!slots.Select(s => s.At).SequenceEqual(slots.Select(s => s.At).OrderBy(t => t))) return "slots unsorted";
                    var next = BackupPlanner.NextRun(policy, DateTimeOffset.UtcNow);
                    if (next is null || next <= DateTimeOffset.UtcNow) return "next run not in future";
                    var prune = BackupPlanner.Prune(slots, policy);
                    if (prune.RetainedFulls != 2) return $"retained fulls {prune.RetainedFulls} != 2";
                    return null;
                },
            },
            new()
            {
                Id = "D06", Label = "DATA-MASKING", MenuPath = "DB", Kind = "TRANS",
                EnginePath = "Src/TaoDBM.App/UI/MaskPreviewDialog.cs + Src/TaoDBM.Core/MaskEngine.cs",
                Run = (d, eng) =>
                {
                    int phoneIdx = d.Columns.FindIndex(c => c.Name == "phone");
                    if (phoneIdx < 0) return "phone column missing";
                    int masked = 0;
                    for (int i = 0; i < d.Rows.Count; i++)
                    {
                        var row = d.Rows[i];
                        var original = row[phoneIdx]?.ToString();
                        if (string.IsNullOrEmpty(original)) continue;
                        var m1 = MaskEngine.MaskCell("phone", original);
                        var m2 = MaskEngine.MaskCell("phone", m1);
                        masked++;
                        if (m1 != m2) return $"mask not idempotent at row {i}";
                        if (m1 == original && original.Length >= 7) return $"mask leaked original phone at row {i}";
                        row[phoneIdx] = m1;
                    }
                    if (d.Rows.Count > 10 && masked == 0) return "nothing masked on populated table";
                    return null;
                },
            },
            new()
            {
                Id = "D07", Label = "DATA-RECONCILE", MenuPath = "DB", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/ReconcileDialog.cs + Src/TaoDBM.Core/ReconcileEngine.cs",
                Run = (d, eng) =>
                {
                    var count = ReconcileEngine.CountSql(d.Table, null, eng);
                    if (!count.Contains("COUNT(*)")) return "count sql malformed";
                    var chk = ReconcileEngine.ChecksumSql(d.Table, new[] { "id" }, null, eng);
                    if (eng is DbEngine.MySQL or DbEngine.PostgreSQL or DbEngine.SqlServer or DbEngine.Oracle && chk == null) return "checksum sql missing for engine with support";
                    if (eng is DbEngine.SQLite && chk != null) return "checksum must fall back to null (ReconcileEngine.cs:34)";
                    long n = d.DeclaredRows;
                    var v1 = ReconcileEngine.Compare(n, n, null, null);
                    if (v1.Level != "match") return $"equal counts verdict {v1.Level}";
                    var v2 = ReconcileEngine.Compare(n, n + 1, null, null);
                    if (v2.Level != "count-mismatch") return "row drift not detected";
                    return null;
                },
            },
            new()
            {
                Id = "D08", Label = "ACCIDENTAL-DELETE-RECOVERY", MenuPath = "DB", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/RecoveryDialog.cs + Src/TaoDBM.Core/DeletedRowRecovery.cs",
                Run = (d, eng) =>
                {
                    var rows = d.Rows.Take(500).Select(r => (IReadOnlyDictionary<string, string?>)d.Columns
                        .Select((c, i) => KeyValuePair.Create(c.Name, r[i]?.ToString()))
                        .ToDictionary(kv => kv.Key, kv => kv.Value)).ToList();
                    var snap = DeletedRowRecovery.Capture(d.Table, d.PkColumn, rows);
                    if (snap.RowCount != rows.Count) return "snapshot row count mismatch";
                    var script = DeletedRowRecovery.ReinsertScript(snap, eng);
                    if (rows.Count > 0 && !script.Contains("INSERT INTO")) return "reinsert script empty";
                    if (rows.Count == 0 && script.Length != 0) return "empty snapshot must give empty script";
                    var f = Path.Combine(Path.GetTempPath(), "combo-rec-" + Guid.NewGuid().ToString("N") + ".jsonl");
                    try
                    {
                        DeletedRowRecovery.SaveJsonl(f, snap);
                        var back = DeletedRowRecovery.LoadJsonl(f);
                        if (back == null || back.RowCount != snap.RowCount) return "jsonl roundtrip mismatch";
                    }
                    finally { if (File.Exists(f)) File.Delete(f); }
                    return null;
                },
            },
            new()
            {
                Id = "D09", Label = "SNAPSHOT-PACK", MenuPath = "DB", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/SnapshotPackDialog.cs + Src/TaoDBM.Core/SnapshotPack.cs",
                Run = (d, eng) =>
                {
                    var qr = new QueryResult { Columns = { "id", "name" } };
                    foreach (var row in d.Rows.Take(1000)) qr.Rows.Add(new object?[] { row[0], row[1] });
                    var t = SnapshotPackFile.FromQueryResult(d.Table, qr);
                    if (t.TotalRows != qr.Rows.Count) return "total rows caliber lost";
                    var pack = SnapshotPackFile.Create("combo", "src-label", new[] { t });
                    var json = SnapshotPackFile.ToJson(pack);
                    var back = SnapshotPackFile.FromJson(json);
                    if (back == null || SnapshotPackFile.Fingerprint(back) != SnapshotPackFile.Fingerprint(pack)) return "fingerprint roundtrip mismatch";
                    if (SnapshotPackFile.GetTable(pack, d.Table) == null) return "table lookup failed";
                    return null;
                },
            },
            new()
            {
                Id = "D10", Label = "BATCH-CLEANUP", MenuPath = "DB", Kind = "TRANS",
                EnginePath = "Src/TaoDBM.App/UI/BatchCleanupDialog.cs + Src/TaoDBM.Core/BatchCleanup.cs",
                Run = (d, eng) =>
                {
                    long est = Math.Max(d.DeclaredRows, d.Rows.Count);
                    int batch = est > 100_000 ? 10_000 : 1000;
                    var plan = BatchCleanup.Plan(d.Table, "id > 0", d.PkColumn, est, batch, eng);
                    long expectBatches = (long)Math.Ceiling(est / (double)batch);
                    if (plan.TotalBatches != expectBatches) return $"plan batches {plan.TotalBatches} != {expectBatches}";
                    var before = d.Rows.Count;
                    var run = BatchCleanup.Run(plan, sql =>
                    {
                        int n = Math.Min(batch, d.Rows.Count);
                        d.Rows.RemoveRange(0, n);
                        return n;
                    });
                    if (run.RowsDeleted != before) return $"deleted {run.RowsDeleted} != {before}";
                    if (d.Rows.Count != 0) return "rows not emptied";
                    if (est == 0 && (run.RowsDeleted != 0 || plan.BatchSqls.Count != 0)) return "empty state must yield empty plan/run";
                    return null;
                },
            },
            new()
            {
                Id = "D11", Label = "DESIGN-ASSIST", MenuPath = "DB", Kind = "PLAN", Gate = GateTypeLen,
                EnginePath = "Src/TaoDBM.App/UI/DesignAssistDialog.cs + Src/TaoDBM.Core/TypeLengthAdvisor.cs",
                Run = (d, eng) =>
                {
                    var metas = new List<ColumnMeta>();
                    var lens = new List<ColumnLen>();
                    foreach (var c in d.Columns)
                    {
                        var (baseType, len) = TypeLengthAdvisor.ParseDeclared(c.DataType);
                        metas.Add(new ColumnMeta(d.Table, c.Name, baseType, len, c.IsKey, DialectName(eng)));
                        var vals = d.Rows.Select(r => r[d.Columns.IndexOf(c)]?.ToString() ?? "").ToList();
                        if (vals.Count > 0)
                            lens.Add(new ColumnLen(vals.Max(v => v.Length), (long)vals.Average(v => v.Length), vals.Count, d.Table, c.Name));
                    }
                    var rep = TypeLengthAdvisor.Inspect(metas, lens);
                    if (rep.Score < 0 || rep.Score > 100) return "score out of range";
                    if (d.Rows.Count == 0 && rep.Findings.Count != 0) return "empty table must be clean";
                    if (d.Rows.Count > 0 && eng == DbEngine.SqlServer) return "gate must exclude SqlServer";
                    return null;
                },
            },
            new()
            {
                Id = "D12", Label = "ER-DIAGRAM", MenuPath = "DB", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/ErdDialog.cs + Src/TaoDBM.Core/ErdModel.cs + Src/TaoDBM.Core/ErdForward.cs",
                Run = (d, eng) =>
                {
                    var model = new ErdModel();
                    model.Tables.Add(new ErdTable { Name = d.Table, Columns = { new ErdColumn { Name = "id", Type = "int", IsKey = true }, new ErdColumn { Name = "order_id", Type = "int" } } });
                    model.Tables.Add(new ErdTable { Name = "t_order", Columns = { new ErdColumn { Name = "id", Type = "int", IsKey = true } } });
                    model.Edges.Add(new ErdEdge { FromTable = d.Table, FromCol = "order_id", ToTable = "t_order" });
                    ErdModel.AutoLayout(model);
                    var json = model.ToJson();
                    var back = ErdModel.FromJson(json);
                    if (back == null || back.Tables.Count != 2) return "model json roundtrip failed";
                    var fwd = ErdForward.Generate(back);
                    if (!fwd.Ok || fwd.Ddl.Count < 2) return "forward ddl incomplete";
                    return null;
                },
            },

            // ===== extended operations =====
            new()
            {
                Id = "X01", Label = "OBJECT-TREE", MenuPath = "EXT", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/MainWindow.xaml.cs(ShowObjectDefinition) + Src/TaoDBM.Core/ObjectTreeCatalog.cs",
                Run = (d, eng) =>
                {
                    if (!ObjectTreeCatalog.Supported(eng)) return "engine unsupported";
                    if (string.IsNullOrWhiteSpace(ObjectTreeCatalog.ListViews(eng, "db"))) return "views sql empty";
                    if (string.IsNullOrWhiteSpace(ObjectTreeCatalog.ListIndexes(eng, "db"))) return "indexes sql empty";
                    if (string.IsNullOrWhiteSpace(ObjectTreeCatalog.ListForeignKeys(eng, "db"))) return "fk sql empty";
                    if (string.IsNullOrWhiteSpace(ObjectTreeCatalog.ListRoutines(eng, "db"))) return "routines sql empty";
                    if (string.IsNullOrWhiteSpace(ObjectTreeCatalog.ListTriggers(eng, "db"))) return "triggers sql empty";
                    if (string.IsNullOrWhiteSpace(ObjectTreeCatalog.ShowDefinition(eng, d.Table))) return "definition sql empty";
                    return null;
                },
            },
            new()
            {
                Id = "X02", Label = "ASK-DATA(NL)", MenuPath = "EXT", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/WorkTab.cs(NL entry) + Src/TaoDBM.Core/NaturalLanguageQuery.cs",
                Run = (d, eng) =>
                {
                    var r = NaturalLanguageQuery.Ask("有多少条记录", d.Table, d.Columns, eng);
                    if (!r.Ok || !r.Sql.Contains("COUNT")) return "count question failed: " + (r.UnsupportedReason ?? "");
                    var r2 = NaturalLanguageQuery.Ask("amount 的平均值是多少", d.Table, d.Columns, eng);
                    if (!r2.Ok || !r2.Sql.Contains("AVG")) return "avg question failed: " + (r2.UnsupportedReason ?? "");
                    var r3 = NaturalLanguageQuery.Ask("", d.Table, d.Columns, eng);
                    if (r3.Ok || string.IsNullOrEmpty(r3.UnsupportedReason)) return "empty question must fail with reason";
                    return null;
                },
            },
            new()
            {
                Id = "X03", Label = "MIGRATION-RUN(ETL)", MenuPath = "EXT", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.App/UI/DataSyncDialog.cs + Src/TaoDBM.Core/DataSync.cs",
                Run = (d, eng) =>
                {
                    var q = Quoter(eng);
                    var create = DataSync.CreateForTarget(d.Table, d.Columns, eng, q);
                    if (!create.Contains("CREATE TABLE")) return "target create missing";
                    var (sync, _) = DataSync.SyncColumns(d.Table, d.Columns, new List<string> { "id", "name" }, eng, q, alignTypes: false);
                    if (sync.Count == 0) return "missing-column sync statements empty";
                    var qr = new QueryResult { Columns = { "id", "name" } };
                    foreach (var r in d.Rows.Take(300)) qr.Rows.Add(new object?[] { r[0], r[1] });
                    var batches = DataSync.InsertBatches(qr, q(d.Table), q);
                    if (batches.Count > 0 && !batches[0].Contains("INSERT INTO")) return "insert batch malformed";
                    if (d.Rows.Count > 0 && d.Rows[0][1] != null && batches.Count == 0) return "rows not batched";
                    var mark = SyncWatermark.Empty(d.Table);
                    var rest = SyncCheckpoint.Resume(new List<string> { "b1", "b2", "b3" }, new SyncWatermark { Table = d.Table, BatchDone = 1 }, null);
                    if (rest.Count != 2) return "checkpoint resume count wrong";
                    return null;
                },
            },
            new()
            {
                Id = "X04", Label = "AUDIT-UNDO", MenuPath = "EXT", Kind = "TRANS",
                EnginePath = "Src/TaoDBM.Core/ChangeAudit.cs",
                Run = (d, eng) =>
                {
                    string? problem = null;
                    if (d.Rows.Count > 0)
                    {
                        var before = new Dictionary<string, string?> { ["note"] = d.Rows[0][5]?.ToString(), ["name"] = d.Rows[0][1]?.ToString() };
                        d.Rows[0][5] = "AUDIT-NEW";
                        var after = new Dictionary<string, string?> { ["note"] = "AUDIT-NEW", ["name"] = before["name"] };
                        var entry = ChangeAudit.CaptureRow(d.Table, d.PkColumn, "1", before, after, "combo");
                        if (!entry.HasChanges) return "no-change audit recorded";
                        var undo = ChangeAudit.UndoSql(entry);
                        var oldLit = before["note"];
                        if (oldLit != null && !undo.Contains(oldLit.Replace("'", "''"))) return "undo missing old value";
                        d.Rows[0][5] = oldLit;
                        if (ChangeAudit.UndoScript(new[] { entry }).Length == 0) return "undo script empty";
                    }
                    var f = Path.Combine(Path.GetTempPath(), "combo-audit-" + Guid.NewGuid().ToString("N") + ".jsonl");
                    try
                    {
                        ChangeAudit.SaveJsonl(f, Array.Empty<AuditEntry>());
                        if (ChangeAudit.LoadJsonl(f).Count != 0) return "empty ledger load failed";
                    }
                    finally { if (File.Exists(f)) File.Delete(f); }
                    return problem;
                },
            },
            new()
            {
                Id = "X05", Label = "FAKER-SEED", MenuPath = "EXT", Kind = "TRANS",
                EnginePath = "Src/TaoDBM.App/UI/FakerDialog.cs + Src/TaoDBM.Core/Faker.cs",
                Run = (d, eng) =>
                {
                    var before = d.Rows.Count;
                    int gen = Math.Min(50, Math.Max(10, d.Columns.Count * 10));
                    var ctxCols = d.Columns.Select(c => new ColumnCtx { Name = c.Name, RawType = c.DataType, Nullable = c.Nullable, IsPrimaryKey = c.IsKey }).ToList();
                    var rand1 = new Random(42);
                    var generated1 = new List<List<object?>>();
                    for (int i = 0; i < gen; i++)
                    {
                        var vals = new List<object?>();
                        foreach (var cc in ctxCols)
                        {
                            var kind = Faker.InferSemantic(cc.Name, cc.RawType);
                            vals.Add(Faker.Gen(kind, cc, rand1, i));
                        }
                        generated1.Add(vals);
                    }
                    foreach (var vals in generated1) d.Rows.Add(vals.ToArray());
                    if (d.Rows.Count != before + gen) return "faker rows not appended";
                    if (d.Rows.Count > 0 && d.Rows[before][1] == null) return "non-nullable name generated null";
                    return null;
                },
            },
            new()
            {
                Id = "X06", Label = "ONLINE-DDL", MenuPath = "EXT", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.Core/OnlineDdl.cs",
                Run = (d, eng) =>
                {
                    var plan = OnlineDdl.Plan(d.Table, "ADD COLUMN c_ext VARCHAR(10)", d.PkColumn, d.DeclaredRows, 10_000);
                    if (plan.Steps[0].Phase != "build" || plan.Steps[^1].Phase != "cleanup") return "step order wrong";
                    if (plan.Steps.Count(s => s.Phase == "swap") != 1) return "swap must be single atomic step";
                    long batches = (long)Math.Ceiling(d.DeclaredRows / 10000.0);
                    if (plan.Steps.Count(s => s.Phase == "backfill") != batches + (batches > 0 ? 1 : 0)) return $"backfill steps wrong (expected {batches})";
                    if (d.DeclaredRows == 0 && plan.Steps.Any(s => s.Phase == "backfill")) return "empty table must not backfill";
                    return null;
                },
            },
            new()
            {
                Id = "X07", Label = "PARTITION-ROLL", MenuPath = "EXT", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.Core/PartitionRoller.cs + Src/TaoDBM.Core/PartitionDdl.cs",
                Run = (d, eng) =>
                {
                    var horizon = DateTime.UtcNow.Date.AddDays(7);
                    var plan = PartitionRoller.EnsureCoverage(Array.Empty<TimePartition>(), horizon, TimeSpan.FromDays(1), 7);
                    if (!plan.CoversHorizon || plan.ToAdd.Count < 1) return $"coverage plan too thin ({plan.ToAdd.Count})";
                    var sqls = PartitionDdl.PlanSql(eng, d.Table, plan);
                    if (eng is DbEngine.MySQL or DbEngine.PostgreSQL && sqls.Count == 0) return "partition sql empty for supported engine";
                    if (eng is DbEngine.SqlServer or DbEngine.SQLite)
                    {
                        if (PartitionDdl.AddSql(eng, d.Table, plan.ToAdd[0]) != null) return "unsupported dialect must return null (PartitionDdl.cs:11)";
                    }
                    return null;
                },
            },
            new()
            {
                Id = "X08", Label = "DATA-HEALTH", MenuPath = "EXT", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.Core/DataHealthReport.cs",
                Run = (d, eng) =>
                {
                    long n = d.DeclaredRows;
                    var profiles = new List<ColumnProfile>
                    {
                        new(d.Table, "name", n, 0, d.Rows.Count > 1 ? Math.Max(0, d.Rows.Count / 2) : 0),
                        new(d.Table, "phone", n, n / 5, 0),
                    };
                    var rep = DataHealthCheck.Inspect(profiles);
                    if (rep.Score < 0 || rep.Score > 100) return "score out of range";
                    if (d.Rows.Count > 100 && d.DeclaredRows == d.Rows.Count && !rep.Findings.Any(f => f.Code == "H002")) return "heavy duplication not flagged";
                    return null;
                },
            },
            new()
            {
                Id = "X09", Label = "HOTSPOT-SALTING", MenuPath = "EXT", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.Core/HotspotSalting.cs",
                Run = (d, eng) =>
                {
                    var plan = HotspotSalting.Plan("hot", 16);
                    if (plan.SaltedKeys.Count != 16) return "salt count wrong";
                    if (HotspotSalting.RouteWrite("hot", 16) != HotspotSalting.RouteWrite("hot", 16)) return "route unstable";
                    if (HotspotSalting.FanOutRead(plan).Count != 16) return "fanout count wrong";
                    return null;
                },
            },
            new()
            {
                Id = "X10", Label = "SEED-PLAN", MenuPath = "EXT", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.Core/SeedPlan.cs",
                Run = (d, eng) =>
                {
                    var users = new SeedTable { Name = "users", Rows = 10, Columns = { new ColumnSpec { Name = "id", DataType = "int", IsKey = true } } };
                    var orders = new SeedTable { Name = "orders", Rows = 10, Columns = { new ColumnSpec { Name = "id", DataType = "int", IsKey = true }, new ColumnSpec { Name = "user_id", DataType = "int" } } };
                    var edges = SeedPlan.BuildRelations(new List<SeedTable> { users, orders }, null);
                    if (edges.Count == 0) return "name-based relation not inferred";
                    var order = SeedPlan.TopoOrder(new List<SeedTable> { orders, users }, edges);
                    if (order[0].Name != "users") return "topo must put referenced table first";
                    if (SeedPlan.QuoteIdent(eng, "x") .Length < 3) return "identifier quoting too short";
                    if (!SeedPlan.ValuesRow(eng, new List<object?> { 1, "a'b" }).Contains("''")) return "literal escaping missing";
                    return null;
                },
            },
            new()
            {
                Id = "X11", Label = "SCHEMA-DIFF", MenuPath = "EXT", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.Core/SchemaDiff.cs",
                Run = (d, eng) =>
                {
                    var src = new List<SchemaTableSnapshot> { SchemaTableSnapshot.FromSpecs(d.Table, d.Columns) };
                    var tgt = new List<SchemaTableSnapshot> { SchemaTableSnapshot.FromSpecs(d.Table, d.Columns.Take(d.Columns.Count - 1)) };
                    var diffs = SchemaDiff.Diff(src, tgt);
                    if (!diffs.Any(x => x.Kind == "column-missing" && x.Column == "note")) return "missing column not detected";
                    var (stmts, skip) = SchemaDiff.PlanSync(src, tgt, diffs, eng, Quoter(eng));
                    if (stmts.Count == 0) return "sync plan empty";
                    if (!stmts.All(s => s.Contains("ADD"))) return "sync plan must add missing column";
                    return null;
                },
            },
            new()
            {
                Id = "X12", Label = "ALERT-RULE", MenuPath = "EXT", Kind = "PLAN",
                EnginePath = "Src/TaoDBM.Core/AlertRule.cs",
                Run = (d, eng) =>
                {
                    var now = DateTime.Parse("2026-11-20T08:00:00Z", null, System.Globalization.DateTimeStyles.RoundtripKind);
                    var conn = AlertEngine.FromTemplate(AlertEngine.Templates[0]);   // 连接数过高 >80
                    var repl = AlertEngine.FromTemplate(AlertEngine.Templates[1]);   // 复制中断 ==1
                    var metrics = new Dictionary<string, double> { ["connections"] = 90, ["replication_break"] = 1 };
                    var fired = AlertEngine.Evaluate(new List<AlertRule> { conn, repl }, metrics, now);
                    if (fired.Count != 2) return $"expected 2 fired, got {fired.Count}";
                    if (fired.Any(f => f.RuleName == "复制中断" && f.Severity != "error")) return "replication break must be red";
                    if (fired.Any(f => f.RuleName == "连接数过高" && f.Severity != "warn")) return "threshold alert must be warn";
                    if (conn.TriggerCount != 1 || repl.TriggerCount != 1) return "trigger count not updated";
                    // 防轰炸：冷却窗内重复触发合并（复制中断冷却 0 分仍立即再报；连接数冷却 5 分被合并）
                    var again = AlertEngine.Evaluate(new List<AlertRule> { conn, repl }, metrics, now.AddMinutes(1));
                    if (again.Count != 1 || again[0].RuleName != "复制中断") return $"cooldown merge wrong ({again.Count})";
                    // 冷却窗过后再报
                    var later = AlertEngine.Evaluate(new List<AlertRule> { conn }, metrics, now.AddMinutes(6));
                    if (later.Count != 1) return "cooldown expiry must re-fire";
                    // 未启用规则不触发
                    conn.Enabled = false;
                    if (AlertEngine.Evaluate(new List<AlertRule> { conn }, metrics, now.AddMinutes(20)).Count != 0) return "disabled rule fired";
                    // 规则落盘回读
                    var f = Path.Combine(Path.GetTempPath(), "combo-alert-" + Guid.NewGuid().ToString("N") + ".json");
                    try
                    {
                        AlertEngine.SaveRules(f, new List<AlertRule> { conn, repl });
                        var loaded = AlertEngine.LoadRules(f);
                        if (loaded.Count != 2 || loaded[0].Metric != "connections") return "rules roundtrip failed";
                    }
                    finally { if (File.Exists(f)) File.Delete(f); }
                    return null;
                },
            },
        };
        return list;
    }
}
