using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>
/// 发现151：导出配置——命名配置（格式/最近目录/浏览范围默认值/是否默认格式）＋自动记忆开关，
/// 存数据根 export-profiles/ 子目录；最近目录记忆按格式分键存 UiPrefs（AppPrefs 同源）。
/// 纯逻辑下沉 Core：配置往返与最近目录收敛独立可测。
/// </summary>
public static class ExportProfiles
{
    public sealed record ExportProfile(
        string Name,
        string Format,          // csv / insert / excel / xml / html / json
        string LastDir,         // 最近目录（可空）
        string BrowseScopeDefault,   // page / filtered / all（浏览模式范围默认值，可空=不指定）
        bool IsDefault);

    private static string DirName => "export-profiles";

    private static string PathOf(string name) =>
        TaoPaths.FilePathIn(DirName, ImportProfiles.SafeName(name) + ".json");

    public static void Save(ExportProfile profile)
    {
        var list = LoadAll();
        list.RemoveAll(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        list.Add(profile);
        // 默认格式唯一：新配置设默认时取消其他默认
        if (profile.IsDefault)
            for (int i = 0; i < list.Count; i++)
                if (list[i].IsDefault && !list[i].Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))
                    list[i] = list[i] with { IsDefault = false };
        var dto = list.Select(p => new
        {
            p.Name, p.Format, p.LastDir, p.BrowseScopeDefault, p.IsDefault,
            SavedAt = DateTimeOffset.Now,
        });
        System.IO.File.WriteAllText(PathOf(profile.Name),
            JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }),
            new System.Text.UTF8Encoding(false));
        AppLog.Write($"[导出配置] 已保存 {profile.Name}（{profile.Format}{(profile.IsDefault ? "，设为默认" : "")}）");
    }

    public static List<ExportProfile> LoadAll()
    {
        try
        {
            var dir = System.IO.Path.Combine(TaoPaths.Root, DirName);
            if (!System.IO.Directory.Exists(dir)) return new List<ExportProfile>();
            var result = new List<ExportProfile>();
            foreach (var f in System.IO.Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var root = JsonDocument.Parse(System.IO.File.ReadAllText(f)).RootElement;
                    foreach (var p in root.EnumerateArray())
                    {
                        result.Add(new ExportProfile(
                            p.GetProperty("Name").GetString() ?? "",
                            p.GetProperty("Format").GetString() ?? "csv",
                            p.TryGetProperty("LastDir", out var d) ? d.GetString() ?? "" : "",
                            p.TryGetProperty("BrowseScopeDefault", out var b) ? b.GetString() ?? "" : "",
                            p.TryGetProperty("IsDefault", out var i) && i.GetBoolean()));
                    }
                }
                catch { /* 单个坏文件跳过不拖垮整表 */ }
            }
            return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return new List<ExportProfile>(); }
    }

    public static bool Delete(string name)
    {
        try { System.IO.File.Delete(PathOf(name)); AppLog.Write($"[导出配置] 已删除 {name}"); return true; }
        catch { return false; }
    }

    /// <summary>发现151：最近目录记忆——成功导出后按格式记目录（UiPrefs 键 export-lastdir-&lt;format&gt;）；
    /// 原样存储，读取时过滤死路径（U 盘拔掉等），不把死路径回给文件对话框。</summary>
    public static void RememberLastDir(string format, string savePath)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(savePath) ?? "";
            if (dir.Length == 0) return;
            // 发现151：原样记忆，死路径由 LastDirOf 读取时过滤（不把死路径回给文件对话框）
            UiPrefs.Set($"export-lastdir-{(format ?? "csv").Trim().ToLowerInvariant()}", dir);
        }
        catch (Exception ex) { AppLog.Write($"[导出配置] 目录记忆失败: {ex.Message}"); }
    }

    /// <summary>发现151：取该格式最近目录（无记忆或目录已不存在返回空串，调用方回落系统默认）。</summary>
    public static string LastDirOf(string format)
    {
        try
        {
            var dir = UiPrefs.Get($"export-lastdir-{(format ?? "csv").Trim().ToLowerInvariant()}");
            return dir.Length > 0 && System.IO.Directory.Exists(dir) ? dir : "";
        }
        catch { return ""; }
    }

    /// <summary>发现151：默认格式（无默认配置返回空串）。</summary>
    public static string DefaultFormat()
    {
        var all = LoadAll();
        return all.FirstOrDefault(p => p.IsDefault)?.Format ?? "";
    }

    /// <summary>发现151：浏览模式范围默认值（page/filtered/all，无配置返回空串）。</summary>
    public static string BrowseScopeDefault()
    {
        var all = LoadAll();
        return all.FirstOrDefault(p => p.BrowseScopeDefault.Length > 0)?.BrowseScopeDefault ?? "";
    }
}
