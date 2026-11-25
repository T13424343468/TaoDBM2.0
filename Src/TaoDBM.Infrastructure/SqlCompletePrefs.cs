namespace TaoDBM.Core;

/// <summary>㉔ SQL 补全偏好（2026-11-25 设计稿冻结）：输入即弹开关，默认开；损坏、缺失回落默认，绝不闪退。
/// SqlOpenPrefs 家法（TaoPaths 存储，UseFile 注入，LogicTest 直测）。</summary>
public static class SqlCompletePrefs
{
    private static string _file = "";
    private static string File => _file.Length > 0 ? _file : TaoPaths.FilePath("sqlcomplete.json");
    private static bool? _cache;   // 发现70：输入即弹每次按键都 Load，读盘改内存缓存；Save 同步写缓存，Invalidate 强制重读

    /// <summary>测试或自定义存储位置。</summary>
    public static void UseFile(string path) { _file = path; _cache = null; }

    /// <summary>强制下一次 Load 重读盘（设置页打开、外部改文件后调用）。</summary>
    public static void Invalidate() => _cache = null;

    /// <summary>读取输入即弹开关；任何异常都安全回落 true（默认开）。命中缓存不走盘。</summary>
    public static bool Load()
    {
        if (_cache is bool cached) return cached;
        try
        {
            if (!System.IO.File.Exists(File)) { _cache = true; return true; }
            var v = System.Text.Json.JsonSerializer.Deserialize<PrefsFile>(System.IO.File.ReadAllText(File));
            var result = v?.AutoPopup != false;
            _cache = result;
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Write($"补全偏好读取失败，回落默认: {ex.Message}");
            return true;   // 异常路径不落缓存：文件恢复后下次重读
        }
    }

    public static void Save(bool autoPopup)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File)!);
            AtomicWrite.Text(File, System.Text.Json.JsonSerializer.Serialize(new PrefsFile { AutoPopup = autoPopup }));
            _cache = autoPopup;   // 保存即生效，按键路径零读盘
        }
        catch (Exception ex) { AppLog.Write($"补全偏好保存失败: {ex.Message}"); }
    }

    private sealed class PrefsFile { public bool AutoPopup { get; set; } = true; }
}
