using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>一行内单列的改数留痕：旧值/新值（null 表示 SQL NULL）。</summary>
public sealed record CellChange(string Column, string? OldValue, string? NewValue);

/// <summary>
/// 一行数据的改数留痕（安全线：改数留痕审计 + 单条回滚）。
/// 记录表、主键、改动列与前后值；UndoSql 生成把该行恢复到旧值的单条 UPDATE。
/// </summary>
public sealed record AuditEntry
{
    public string Table { get; init; } = "";
    public string PkColumn { get; init; } = "";
    public string PkValue { get; init; } = "";
    /// <summary>触发改动的语句原文（审计展示用，不参与回滚生成）。</summary>
    public string Statement { get; init; } = "";
    /// <summary>留痕时间（UTC ISO-8601，审计展示用）。</summary>
    public string TimestampUtc { get; init; } = "";
    public List<CellChange> Changes { get; init; } = new();

    /// <summary>该行是否真的有改动（无差异不落账）。</summary>
    public bool HasChanges => Changes.Count > 0;
}

/// <summary>
/// 安全线引擎：改数留痕审计 + 单条回滚的纯逻辑部分（第十章）。
/// 只做前后镜像 diff、留痕账目与回滚语句生成，不连库、不执行；
/// 执行入口捕获 before/after 镜像后调用 CaptureRow，回滚走确认弹窗。
/// </summary>
public static class ChangeAudit
{
    /// <summary>捕获一行改动：diff before/after 镜像，无差异返回 HasChanges=false 的空账。</summary>
    public static AuditEntry CaptureRow(
        string table, string pkColumn, string pkValue,
        IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after,
        string statement = "")
    {
        var changes = new List<CellChange>();
        foreach (var key in before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal))
        {
            before.TryGetValue(key, out var oldV);
            after.TryGetValue(key, out var newV);
            if (!string.Equals(oldV, newV, StringComparison.Ordinal))
                changes.Add(new CellChange(key, oldV, newV));
        }
        changes.Sort((a, b) => string.CompareOrdinal(a.Column, b.Column));
        return new AuditEntry
        {
            Table = table,
            PkColumn = pkColumn,
            PkValue = pkValue,
            Statement = statement,
            TimestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Changes = changes,
        };
    }

    /// <summary>单条回滚语句：把该行改回旧值（列按账目顺序合并进一条 UPDATE）。</summary>
    public static string UndoSql(AuditEntry entry)
    {
        if (!entry.HasChanges) return "";
        var sets = entry.Changes.Select(c => $"{Id(c.Column)} = {Lit(c.OldValue)}");
        return $"UPDATE {Id(entry.Table)} SET {string.Join(", ", sets)} WHERE {Id(entry.PkColumn)} = {Lit(entry.PkValue)}";
    }

    /// <summary>多条账目的回滚脚本：按留痕倒序（后改的先回滚），每条一个分号换行。</summary>
    public static string UndoScript(IEnumerable<AuditEntry> entries) =>
        string.Join(";\n", entries.Reverse().Where(e => e.HasChanges).Select(UndoSql)) +
        (entries.Any(e => e.HasChanges) ? ";\n" : "");

    /// <summary>留痕账落 JSONL 文件（一行一账，追加写由调用方控制；此处整体写）。</summary>
    public static void SaveJsonl(string path, IReadOnlyList<AuditEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
            sb.AppendLine(JsonSerializer.Serialize(e, JsonOpts));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>从 JSONL 读回留痕账；空或损坏文件按空账容错。</summary>
    public static List<AuditEntry> LoadJsonl(string path)
    {
        if (!File.Exists(path)) return new List<AuditEntry>();
        var list = new List<AuditEntry>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { list.Add(JsonSerializer.Deserialize<AuditEntry>(line, JsonOpts) ?? throw new JsonException()); }
            catch (JsonException) { /* 损坏行跳过，账目不中断 */ }
        }
        return list;
    }

    private static string Id(string identifier) =>
        "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

    private static string Lit(string? value) =>
        value is null ? "NULL" : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
}
