using System.Text;
using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>程序侧投喂：把语料喂忆材料批量落进 QaMemoryStore（AI 版阶段 0 喂养通路）。</summary>
/// <remarks>种子行格式 {qid,batch,cat,engine,question,sql,source,confirmed,refuse}；
/// 方言名归一（DIALECT_MAP 口径：SQL Server→SqlServer）在种子生成侧完成，加载时再校验；
/// 拒答类条目（-- REFUSE 前缀）不进记忆召回池，走 QaRefuseLayer 确定性拒答，此处仅登记留痕。</remarks>
public static class QaFeedingLoader
{
    static readonly JsonSerializerOptions SeedJsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>方言名归一映射（与语料审计脚本 DIALECT_MAP 同口径）。</summary>
    public static readonly Dictionary<string, string> DialectMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MySQL"] = DbEngine.MySQL.ToString(),
        ["PostgreSQL"] = DbEngine.PostgreSQL.ToString(),
        ["SQL Server"] = DbEngine.SqlServer.ToString(),
        ["SqlServer"] = DbEngine.SqlServer.ToString(),
        ["MsSQL"] = DbEngine.SqlServer.ToString(),
        ["MSSQL"] = DbEngine.SqlServer.ToString(),
        ["SQLite"] = DbEngine.SQLite.ToString(),
        ["Oracle"] = DbEngine.Oracle.ToString(),
        ["MongoDB"] = DbEngine.MongoDB.ToString(),
        ["Redis"] = DbEngine.Redis.ToString(),
    };

    /// <summary>单条投喂结果。</summary>
    public sealed record FeedOutcome(string Qid, bool Ok, string Reason, long? MemoryId);

    /// <summary>批量投喂报告。</summary>
    public sealed record FeedReport(int Total, int Fed, int Skipped, IReadOnlyList<FeedOutcome> Outcomes);

    /// <summary>从种子 JSONL 流式读取并逐条投喂；坏行跳过并记入报告，不中断整批。</summary>
    public static FeedReport LoadSeed(QaMemoryStore store, string seedPath, string batchId)
    {
        int total = 0, fed = 0, skipped = 0;
        var outcomes = new List<FeedOutcome>();
        foreach (var raw in File.ReadAllLines(seedPath, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            total++;
            try
            {
                var row = JsonSerializer.Deserialize<SeedRow>(line, SeedJsonOpt);
                if (row is null || string.IsNullOrWhiteSpace(row.Question) || string.IsNullOrWhiteSpace(row.Sql))
                {
                    skipped++;
                    outcomes.Add(new FeedOutcome(row?.Qid ?? "?", false, "empty-fields", null));
                    continue;
                }
                if (!DialectMap.TryGetValue(row.Engine, out var engineName))
                {
                    skipped++;
                    outcomes.Add(new FeedOutcome(row.Qid, false, "unknown-dialect:" + row.Engine, null));
                    continue;
                }
                if (row.Refuse || row.Sql.StartsWith("-- REFUSE", StringComparison.Ordinal))
                {
                    // 拒答类：不进召回池（安全拒答层是硬前置，词表已覆盖），登记留痕供对账。
                    skipped++;
                    outcomes.Add(new FeedOutcome(row.Qid, false, "refuse-type-via-refuse-layer", null));
                    continue;
                }
                var engine = Enum.Parse<DbEngine>(engineName);
                var e = store.Record(row.Question, row.Sql, engine, "corpus", confirmed: true,
                    note: $"feed:{batchId}:{row.Qid}:{row.Cat}");
                if (e is null)
                {
                    skipped++;
                    outcomes.Add(new FeedOutcome(row.Qid, false, "record-rejected", null));
                    continue;
                }
                fed++;
                outcomes.Add(new FeedOutcome(row.Qid, true, "", e.Id));
            }
            catch (JsonException)
            {
                skipped++;
                outcomes.Add(new FeedOutcome("?", false, "bad-json-line", null));
            }
            catch (ArgumentException)
            {
                skipped++;
                outcomes.Add(new FeedOutcome("?", false, "bad-engine-name", null));
            }
        }
        return new FeedReport(total, fed, skipped, outcomes);
    }

    /// <summary>喂忆回放：对已喂 qid 清单逐题召回，期望 vs 召回零容忍，产出四量具之③。</summary>
    public sealed record ReplayRow(string Qid, bool Hit, bool SqlMatch, double Score, double LookupMs);

    public sealed record ReplayReport(int Total, int Hits, int SqlMatches, IReadOnlyList<ReplayRow> Rows)
    {
        public double HitRate => Total == 0 ? 0 : (double)Hits / Total;
        public double SqlMatchRate => Total == 0 ? 0 : (double)SqlMatches / Total;
    }

    /// <summary>按种子逐题回放召回并计时（量具③正确率 100% 零容忍＋量具④毫秒级）。</summary>
    public static ReplayReport Replay(QaMemoryStore store, string seedPath)
    {
        int hits = 0, sqlMatches = 0;
        var rows = new List<ReplayRow>();
        foreach (var raw in File.ReadAllLines(seedPath, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                var row = JsonSerializer.Deserialize<SeedRow>(line, SeedJsonOpt);
                if (row is null || row.Refuse || (row.Sql ?? "").StartsWith("-- REFUSE", StringComparison.Ordinal))
                    continue; // 拒答类由 QaRefuseLayer 量具覆盖，不在记忆回放口径内
                if (!DialectMap.TryGetValue(row.Engine, out var replayEngine))
                    continue; // 未知方言与投喂口径一致：不入账不计时
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var m = store.Recall(row.Question, replayEngine);
                sw.Stop();
                var ms = Math.Round(sw.Elapsed.TotalMilliseconds, 3);
                var hit = m is not null;
                if (hit) hits++;
                var sqlOk = hit && string.Equals(
                    QaMemoryStoreNormalize(m!.Entry.Sql), QaMemoryStoreNormalize(row.Sql ?? ""), StringComparison.Ordinal);
                if (sqlOk) sqlMatches++;
                rows.Add(new ReplayRow(row.Qid, hit, sqlOk, hit ? m!.Score : 0, ms));
            }
            catch (JsonException)
            {
                // 坏行与投喂口径一致：跳过不计入回放账
            }
        }
        return new ReplayReport(rows.Count, hits, sqlMatches, rows);
    }

    static string QaMemoryStoreNormalize(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();
}

/// <summary>种子行反序列化载体。</summary>
public sealed class SeedRow
{
    public string Qid { get; set; } = "";
    public string Batch { get; set; } = "";
    public string Cat { get; set; } = "";
    public string Engine { get; set; } = "";
    public string Question { get; set; } = "";
    public string Sql { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Confirmed { get; set; }
    public bool Refuse { get; set; }
}
