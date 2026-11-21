using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>
/// 误删找回的表快照：危险操作前捕获的行镜像（第十章「误删找回：快照+拼回」）。
/// 列集取全部行的并集并按字典序定序，缺列按 NULL 拼回。
/// </summary>
public sealed record TableSnapshot
{
    public string Table { get; init; } = "";
    /// <summary>主键列（拼回语句按主键排序，降低外键顺序问题）。</summary>
    public string PkColumn { get; init; } = "";
    /// <summary>快照捕获时间（UTC ISO-8601）。</summary>
    public string CapturedUtc { get; init; } = "";
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();
    /// <summary>行数据与 Columns 对齐；null 表示 NULL。</summary>
    public IReadOnlyList<IReadOnlyList<string?>> Rows { get; init; } = Array.Empty<IReadOnlyList<string?>>();
    public int RowCount => Rows.Count;
}

/// <summary>
/// 安全线引擎：误删找回（快照+拼回）的纯逻辑部分。
/// 捕获镜像、JSONL 落账、生成拼回 INSERT 脚本；不连库、不执行，
/// 快照入口接危险操作执行前的确认弹窗，拼回走预览+确认。
/// </summary>
public static class DeletedRowRecovery
{
    /// <summary>捕获行镜像：列集取并集排序，行内按列集对齐（缺列补 null）。</summary>
    public static TableSnapshot Capture(string table, string pkColumn,
        IEnumerable<IReadOnlyDictionary<string, string?>> rows)
    {
        var list = rows.Select(r => (IReadOnlyDictionary<string, string?>)r).ToList();
        var columns = list.SelectMany(r => r.Keys).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var data = list.Select(r => columns.Select(c => r.TryGetValue(c, out var v) ? v : null).ToList()).ToList();
        return new TableSnapshot
        {
            Table = table,
            PkColumn = pkColumn,
            CapturedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Columns = columns,
            Rows = data,
        };
    }

    /// <summary>拼回脚本：按主键排序后分块 INSERT（每块 rowsPerStatement 行，默认 100）。</summary>
    public static string ReinsertScript(TableSnapshot snapshot, DbEngine engine, int rowsPerStatement = 100)
    {
        if (snapshot.RowCount == 0) return "";
        var pkIdx = IndexOf(snapshot.Columns, snapshot.PkColumn);
        var ordered = pkIdx >= 0
            ? snapshot.Rows.OrderBy(r => r[pkIdx], StringComparer.Ordinal).ToList()
            : snapshot.Rows.ToList();

        var sb = new StringBuilder();
        foreach (var chunk in ordered.Chunk(Math.Max(rowsPerStatement, 1)))
        {
            sb.Append($"INSERT INTO {Id(snapshot.Table, engine)} ({string.Join(", ", snapshot.Columns.Select(c => Id(c, engine)))}) VALUES ");
            sb.Append(string.Join(", ", chunk.Select(r =>
                "(" + string.Join(", ", r.Select(Lit)) + ")")));
            sb.Append(";\n");
        }
        return sb.ToString();
    }

    /// <summary>快照落 JSONL（整表一文件，一行一账式转义由 JSON 承担）。</summary>
    public static void SaveJsonl(string path, TableSnapshot snapshot) =>
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOpts) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>从 JSONL 读回快照；文件缺失或损坏返回 null（调用方按无快照提示重建）。</summary>
    public static TableSnapshot? LoadJsonl(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<TableSnapshot>(File.ReadAllText(path), JsonOpts); }
        catch (JsonException) { return null; }
    }

    private static int IndexOf(IReadOnlyList<string> list, string item)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], item, StringComparison.Ordinal)) return i;
        return -1;
    }

    private static string Id(string identifier, DbEngine engine)
    {
        var q = engine == DbEngine.MySQL ? '`' : '"';
        return q + identifier.Replace(q.ToString(), q.ToString() + q.ToString(), StringComparison.Ordinal) + q;
    }

    private static string Lit(string? value) =>
        value is null ? "NULL" : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
}
