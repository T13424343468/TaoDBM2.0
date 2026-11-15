using System.Text;
namespace TaoDBM.Core;

/// <summary>时间线上的一个结构切面：某时刻的全库表结构快照。</summary>
public sealed class SchemaTimelineEntry
{
    public string Id { get; init; } = "";
    public DateTimeOffset At { get; init; }
    /// <summary>切面说明（如「上线前基线」「加列后」），预览展示用。</summary>
    public string Label { get; init; } = "";
    public List<SchemaTableSnapshot> Tables { get; init; } = new();
}

/// <summary>
/// A3-2 表结构变更时间线（纯逻辑，不碰连接，不碰文件）：追加结构切面 →
/// 任意两切面间可对比（复用 SchemaDiff）→ 可回放（生成中间的同步语句计划）；
/// 序列化交调用方走 AtomicWrite 落盘，Parse 容错不抛。
/// </summary>
public static class SchemaTimeline
{
    /// <summary>追加一个切面，返回含新条目的新列表（不改动入参列表）。</summary>
    public static IReadOnlyList<SchemaTimelineEntry> Append(
        IReadOnlyList<SchemaTimelineEntry> entries, IReadOnlyList<SchemaTableSnapshot> tables,
        string label, DateTimeOffset? at = null)
    {
        var list = entries.ToList();
        list.Add(new SchemaTimelineEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            At = at ?? DateTimeOffset.Now,
            Label = label,
            Tables = tables.Select(t => new SchemaTableSnapshot
            {
                Table = t.Table,
                Columns = t.Columns.Select(c => new TableColumnDesign
                {
                    Name = c.Name, SqlType = c.SqlType, Nullable = c.Nullable,
                    IsPrimaryKey = c.IsPrimaryKey, AutoIncrement = c.AutoIncrement, DefaultValue = c.DefaultValue,
                }).ToList(),
            }).ToList(),
        });
        return list;
    }

    /// <summary>对比两个切面，返回「从 from 升级到 to」的差异：
    /// from 视为目标库现状（被改侧），to 视为期望结构（源侧）；多余项仅提示不删。</summary>
    public static List<SchemaDiffItem> DiffBetween(SchemaTimelineEntry from, SchemaTimelineEntry to,
        SchemaDiffOptions? options = null)
        => SchemaDiff.Diff(to.Tables, from.Tables, options);

    /// <summary>回放：把 from 现状升级到 to 的同步语句计划（删侧默认关，与 SchemaDiff.PlanSync 同口径）。</summary>
    public static (List<string> Statements, List<string> Skipped) PlanBetween(
        SchemaTimelineEntry from, SchemaTimelineEntry to, DbEngine engine, Func<string, string> quote,
        bool dropMissing = false, SchemaDiffOptions? options = null)
        => SchemaDiff.PlanSync(to.Tables, from.Tables, DiffBetween(from, to, options), engine, quote, dropMissing);

    /// <summary>时间线序列化（调用方拿去走 AtomicWrite 落盘）。</summary>
    public static string ToJson(IReadOnlyList<SchemaTimelineEntry> entries) =>
        System.Text.Json.JsonSerializer.Serialize(entries);

    /// <summary>时间线回读容错：JSON 合法按 At 升序还原；损坏/为空返回空时间线（不抛，可重新开始记录）。</summary>
    public static IReadOnlyList<SchemaTimelineEntry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<SchemaTimelineEntry>>(json);
            if (entries is null) return [];
            entries.RemoveAll(e => e is null || e.Id.Length == 0);
            return entries.OrderBy(e => e.At).ToList();
        }
        catch (System.Text.Json.JsonException) { return []; }
    }
}
