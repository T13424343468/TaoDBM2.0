using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>
/// 通用键值偏好（发现151：导出最近目录等轻量 UI 状态）——独立于 AppPrefs（关闭行为专用），
/// 存数据根 uiprefs.json 单文件；读失败/键缺失回落空串，写失败只落日志，绝不拖垮业务。
/// </summary>
public static class UiPrefs
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _cache;

    private static string File => TaoPaths.FilePath("uiprefs.json");

    private static Dictionary<string, string> Load()
    {
        if (_cache is not null) return _cache;
        try
        {
            if (System.IO.File.Exists(File))
                _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(File))
                         ?? new Dictionary<string, string>();
            else _cache = new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            AppLog.Write($"[UI偏好] 读取失败，回落空表: {ex.Message}");
            _cache = new Dictionary<string, string>();
        }
        return _cache;
    }

    private static void Save(Dictionary<string, string> dict)
    {
        try
        {
            AtomicWrite.Text(File, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Write($"[UI偏好] 保存失败: {ex.Message}"); }
    }

    public static string Get(string key)
    {
        lock (Gate) return Load().TryGetValue(key ?? "", out var v) ? v : "";
    }

    public static void Set(string key, string value)
    {
        lock (Gate)
        {
            var dict = Load();
            dict[key ?? ""] = value ?? "";
            Save(dict);
        }
    }

    /// <summary>测试/自定义隔离：清内存缓存（下次读回磁盘真值）。</summary>
    public static void Invalidate() => _cache = null;
}
