using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>
/// 发现141：导入配置存取——映射（CSV 列 → 表列）＋选项（首行为表头/遇错策略）整体记忆，
/// 存数据根 import-profiles/ 子目录（TaoPaths.FilePathIn），同名覆盖需显式确认（调用方把关）。
/// 纯逻辑下沉 Core：智慧匹配算法（SmartMatch）独立可测。
/// </summary>
public static class ImportProfiles
{
    /// <summary>导入配置：Name=配置名；Mappings=CSV列名→表列名（值 null=不导入）；FirstRowHeader/SkipErrors=选项。</summary>
    public sealed record ImportProfile(
        string Name,
        IReadOnlyList<string> CsvHeaders,
        IReadOnlyList<string?> TargetColumns,
        bool FirstRowHeader,
        bool SkipErrors);

    private static string DirName => "import-profiles";

    private static string PathOf(string name) =>
        TaoPaths.FilePathIn(DirName, SafeName(name) + ".json");

    /// <summary>配置名转安全文件名：只留中英文数字下划线短横线，其余转下划线。</summary>
    public static string SafeName(string name)
    {
        var chars = (name ?? "").Trim().Select(c =>
            char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray();
        var s = new string(chars).Trim('_');
        return s.Length == 0 ? "未命名配置" : s;
    }

    public static void Save(ImportProfile profile)
    {
        var dto = new
        {
            profile.Name,
            profile.CsvHeaders,
            profile.TargetColumns,
            profile.FirstRowHeader,
            profile.SkipErrors,
            SavedAt = DateTimeOffset.Now,
        };
        System.IO.File.WriteAllText(PathOf(profile.Name),
            JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }),
            new System.Text.UTF8Encoding(false));
        AppLog.Write($"[导入配置] 已保存 {profile.Name}（映射 {profile.TargetColumns.Count(t => t is not null)} 列）");
    }

    public static ImportProfile? Load(string name)
    {
        try
        {
            var json = System.IO.File.ReadAllText(PathOf(name));
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var headers = root.GetProperty("CsvHeaders").EnumerateArray().Select(v => v.GetString() ?? "").ToList();
            var targets = root.GetProperty("TargetColumns").EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.Null ? null : v.GetString()).ToList();
            return new ImportProfile(
                root.GetProperty("Name").GetString() ?? name,
                headers, targets,
                root.TryGetProperty("FirstRowHeader", out var f) && f.GetBoolean(),
                root.TryGetProperty("SkipErrors", out var s) ? s.GetBoolean() : true);
        }
        catch (Exception ex)
        {
            AppLog.Write($"[导入配置] 读取失败 {name}: {ex.Message}");
            return null;
        }
    }

    public static List<string> List()
    {
        try
        {
            var dir = System.IO.Path.Combine(TaoPaths.Root, DirName);
            if (!System.IO.Directory.Exists(dir)) return new List<string>();
            return System.IO.Directory.GetFiles(dir, "*.json")
                .Select(p => System.IO.Path.GetFileNameWithoutExtension(p) ?? "")
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return new List<string>(); }
    }

    public static bool Delete(string name)
    {
        try { System.IO.File.Delete(PathOf(name)); AppLog.Write($"[导入配置] 已删除 {name}"); return true; }
        catch { return false; }
    }

    /// <summary>发现141：智慧匹配——CSV 列名与表列名按忽略大小写/去空格精确匹配，
    /// 再按常见别名表（id↔编号、name↔名称、price↔价格、time/date↔时间/日期、memo/note↔备注/说明）匹配。
    /// 返回与 csvHeaders 等长的目标列名数组（null=不导入）；每个表列最多被一列选中（先到先得）。</summary>
    public static List<string?> SmartMatch(IReadOnlyList<string> csvHeaders, IReadOnlyList<string> tableColumns)
    {
        var result = new List<string?>(new string?[csvHeaders.Count]);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 第一轮：精确（忽略大小写与首尾空格）
        for (int i = 0; i < csvHeaders.Count; i++)
        {
            var h = (csvHeaders[i] ?? "").Trim();
            var hit = tableColumns.FirstOrDefault(t => !used.Contains(t) && t.Trim().Equals(h, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) { result[i] = hit; used.Add(hit); }
        }
        // 第二轮：别名表
        foreach (var (csv, aliases) in AliasPairs)
        {
            for (int i = 0; i < csvHeaders.Count; i++)
            {
                if (result[i] is not null) continue;
                var h = (csvHeaders[i] ?? "").Trim();
                if (!aliases.Contains(h, StringComparer.OrdinalIgnoreCase)) continue;
                var hit = tableColumns.FirstOrDefault(t => !used.Contains(t) &&
                    (t.Trim().Equals(csv, StringComparison.OrdinalIgnoreCase) || aliases.Contains(t.Trim(), StringComparer.OrdinalIgnoreCase)));
                if (hit is not null) { result[i] = hit; used.Add(hit); }
            }
        }
        return result;
    }

    /// <summary>别名组：组内任一名字互为同义（组名取最常见形态）。</summary>
    private static readonly (string Name, string[] Aliases)[] AliasPairs =
    {
        ("id", new[] { "id", "编号", "序号", "no" }),
        ("name", new[] { "name", "名称", "姓名" }),
        ("price", new[] { "price", "价格", "单价" }),
        ("time", new[] { "time", "时间", "日期", "date", "created", "创建时间" }),
        ("memo", new[] { "memo", "备注", "说明", "note", "comment" }),
        ("qty", new[] { "qty", "quantity", "数量" }),
        ("status", new[] { "status", "状态" }),
    };
}
