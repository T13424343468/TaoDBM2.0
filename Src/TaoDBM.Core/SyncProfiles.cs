using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>
/// 发现148：数据同步配置存取——源/目标连接与库、表清单、结构同步/对齐类型/传数据/模式全量记忆，
/// 存数据根 sync-profiles/ 子目录；挂任务中心时 Kind=sync，Sql 字段存配置文件名（执行方按名回读）。
/// 纯逻辑下沉 Core：配置往返与校验独立可测。
/// </summary>
public static class SyncProfiles
{
    public sealed record SyncProfile(
        string Name,
        string SourceConnection,
        string SourceDb,
        string TargetConnection,
        string TargetDb,
        IReadOnlyList<string> Tables,
        bool SyncStruct,
        bool AlignTypes,
        bool SendData,
        bool TruncateMode);

    private static string DirName => "sync-profiles";

    private static string PathOf(string name) =>
        TaoPaths.FilePathIn(DirName, ImportProfiles.SafeName(name) + ".json");

    public static void Save(SyncProfile profile)
    {
        var dto = new
        {
            profile.Name,
            profile.SourceConnection,
            profile.SourceDb,
            profile.TargetConnection,
            profile.TargetDb,
            profile.Tables,
            profile.SyncStruct,
            profile.AlignTypes,
            profile.SendData,
            profile.TruncateMode,
            SavedAt = DateTimeOffset.Now,
        };
        System.IO.File.WriteAllText(PathOf(profile.Name),
            JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }),
            new System.Text.UTF8Encoding(false));
        AppLog.Write($"[同步配置] 已保存 {profile.Name}（{profile.SourceConnection}/{profile.SourceDb} → {profile.TargetConnection}/{profile.TargetDb}，{profile.Tables.Count} 表）");
    }

    public static SyncProfile? Load(string name)
    {
        try
        {
            var json = System.IO.File.ReadAllText(PathOf(name));
            var root = JsonDocument.Parse(json).RootElement;
            return new SyncProfile(
                root.GetProperty("Name").GetString() ?? name,
                Get(root, "SourceConnection"), Get(root, "SourceDb"),
                Get(root, "TargetConnection"), Get(root, "TargetDb"),
                root.GetProperty("Tables").EnumerateArray().Select(v => v.GetString() ?? "").Where(s => s.Length > 0).ToList(),
                Bool(root, "SyncStruct"),
                Bool(root, "AlignTypes"),
                Bool(root, "SendData"),
                Bool(root, "TruncateMode"));
        }
        catch (Exception ex)
        {
            AppLog.Write($"[同步配置] 读取失败 {name}: {ex.Message}");
            return null;
        }
    }

    private static string Get(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

    /// <summary>布尔字段读取：JSON 布尔取 GetBoolean，缺失回落 false（GetString 对布尔元素会抛）。</summary>
    private static bool Bool(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

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
        try { System.IO.File.Delete(PathOf(name)); AppLog.Write($"[同步配置] 已删除 {name}"); return true; }
        catch { return false; }
    }

    /// <summary>发现148：挂任务中心校验——配置名非空、表清单非空、源目标不同名同库（自传自无意义）；
    /// 返回错误消息（空串=通过），人话口径。</summary>
    public static string Validate(SyncProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name)) return "请先给配置起个名字。";
        if (profile.Tables.Count == 0) return "配置里没有要传的表，先勾选表再保存。";
        if (profile.SourceConnection.Equals(profile.TargetConnection, StringComparison.OrdinalIgnoreCase)
            && profile.SourceDb.Equals(profile.TargetDb, StringComparison.OrdinalIgnoreCase))
            return "源和目标是同一个库，传输没有意义，请改一处。";
        return "";
    }
}
