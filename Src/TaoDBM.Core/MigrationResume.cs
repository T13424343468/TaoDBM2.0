using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>档三：迁移续传引擎（整库迁移的任务级断点续传：清单基线 + 逐表水位 + 重启后重算续传计划）。</summary>
/// <remarks>
/// 纯引擎免界面稿：单表批内断点已由 DataSync/SyncCheckpoint 承担，本引擎管"一次迁移整个任务"的状态——
/// 清单（manifest）记源表基线行数与批次数，状态（state）记每表已落库水位，进程重启后凭两者重算：
/// 完成的表只做行数校验、半途的表从水位续、未动的表整表拷贝。连接凭据不进清单与状态（只存连接名，凭据不落盘）。
/// </remarks>

/// <summary>迁移清单表目：manifest 时刻的源表基线（行数与预计批次数）。</summary>
public sealed record MigrationTable(string Name, long SourceRows, int TotalBatches);

/// <summary>迁移清单：一次全库迁移的静态计划。Source/Target 为连接名，不含任何凭据。</summary>
public sealed record MigrationManifest(string ManifestId, string Source, string Target, DateTimeOffset CreatedAt, IReadOnlyList<MigrationTable> Tables);

/// <summary>单表续传水位：已完整落库的批次数与水位键。</summary>
public sealed record TableProgress(string Table, int BatchDone, string LastKey, bool Done)
{
    public static TableProgress Fresh(string table) => new(table, 0, "", false);
}

/// <summary>迁移运行状态：可持久化 JSON，进程重启后凭它续传。</summary>
public sealed record MigrationState(string ManifestId, IReadOnlyList<TableProgress> Tables);

/// <summary>续传动作：Action=copy（整表）/ resume（从 FromBatch 续）/ verify（仅行数校验）。</summary>
public sealed record ResumeAction(string Table, string Action, int FromBatch, string Note);

/// <summary>续传计划：按清单顺序的动作列表 + 源漂移表 + 目标行数不符表。</summary>
public sealed record ResumePlan(IReadOnlyList<ResumeAction> Actions, IReadOnlyList<string> Drifted, IReadOnlyList<string> VerifyMismatch);

public static class MigrationResume
{
    /// <summary>按清单生成全新状态（全部表零水位）。</summary>
    public static MigrationState Fresh(MigrationManifest manifest) =>
        new(manifest.ManifestId, manifest.Tables.Select(t => TableProgress.Fresh(t.Name)).ToList());

    /// <summary>推进某表水位并返回新状态（原状态不变）；batchDone 达到清单批次数时自动置 Done。</summary>
    public static MigrationState Advance(MigrationState state, string table, int batchDone, string lastKey, long manifestTotalBatches)
    {
        var updated = state.Tables
            .Select(p => p.Table == table
                ? new TableProgress(table, batchDone, lastKey, batchDone >= manifestTotalBatches)
                : p)
            .ToList();
        return new MigrationState(state.ManifestId, updated);
    }

    /// <summary>重算续传计划：完成表→verify，半途表→resume，未动表→copy；源行数漂移与目标行数不符单列提醒，不改动作顺序。</summary>
    /// <param name="sourceCounts">可选：当前源表实际行数（键=表名），与清单基线比对发现漂移。</param>
    /// <param name="targetCounts">可选：目标表当前行数（键=表名），对完成表做落库完整性校验。</param>
    public static ResumePlan Plan(MigrationManifest manifest, MigrationState state,
        IReadOnlyDictionary<string, long>? sourceCounts = null, IReadOnlyDictionary<string, long>? targetCounts = null)
    {
        if (!string.Equals(manifest.ManifestId, state.ManifestId, StringComparison.Ordinal))
            throw new ArgumentException("清单与状态不匹配（ManifestId 不一致），拒绝续传");
        var byTable = state.Tables.ToDictionary(p => p.Table, p => p, StringComparer.Ordinal);
        var actions = new List<ResumeAction>();
        var drifted = new List<string>();
        var mismatch = new List<string>();
        foreach (var t in manifest.Tables)
        {
            var p = byTable.TryGetValue(t.Name, out var got) ? got : TableProgress.Fresh(t.Name);
            if (sourceCounts is not null && sourceCounts.TryGetValue(t.Name, out var now) && now != t.SourceRows)
                drifted.Add(t.Name);
            if (p.Done)
            {
                if (targetCounts is not null && targetCounts.TryGetValue(t.Name, out var gotRows) && gotRows != t.SourceRows)
                {
                    mismatch.Add(t.Name);
                    actions.Add(new ResumeAction(t.Name, "copy", 0, $"目标 {gotRows} 行 ≠ 清单基线 {t.SourceRows} 行，整表重灌"));
                }
                else
                {
                    actions.Add(new ResumeAction(t.Name, "verify", t.TotalBatches, "已完成，仅校验目标行数"));
                }
            }
            else if (p.BatchDone > 0)
            {
                actions.Add(new ResumeAction(t.Name, "resume", p.BatchDone, $"从第 {p.BatchDone + 1} 批续传，水位键 {p.LastKey}"));
            }
            else
            {
                actions.Add(new ResumeAction(t.Name, "copy", 0, "未开始，整表拷贝"));
            }
        }
        return new ResumePlan(actions, drifted, mismatch);
    }

    /// <summary>状态持久化：仅连接名与水位，无凭据字段。</summary>
    public static string ToJson(MigrationState state) =>
        JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });

    public static MigrationState? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<MigrationState>(json); }
        catch (JsonException) { return null; }
        catch (ArgumentNullException) { return null; }
    }
}
