namespace TaoDBM.Core;

/// <summary>㉔ SQL 补全偏好（2026-11-25 设计稿冻结）：输入即弹开关，默认开；损坏、缺失回落默认，绝不闪退。
/// SqlOpenPrefs 家法（TaoPaths 存储，UseFile 注入，LogicTest 直测）。</summary>
public static class SqlCompletePrefs
{
    private static string _file = "";
    private static string File => _file.Length > 0 ? _file : TaoPaths.FilePath("sqlcomplete.json");

    /// <summary>测试或自定义存储位置。</summary>
    public static void UseFile(string path) => _file = path;

    /// <summary>读取输入即弹开关；任何异常都安全回落 true（默认开）。</summary>
    public static bool Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return true;
            var v = System.Text.Json.JsonSerializer.Deserialize<PrefsFile>(System.IO.File.ReadAllText(File));
            return v?.AutoPopup != false;
        }
        catch (Exception ex)
        {
            AppLog.Write($"补全偏好读取失败，回落默认: {ex.Message}");
            return true;
        }
    }

    public static void Save(bool autoPopup)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File)!);
            AtomicWrite.Text(File, System.Text.Json.JsonSerializer.Serialize(new PrefsFile { AutoPopup = autoPopup }));
        }
        catch (Exception ex) { AppLog.Write($"补全偏好保存失败: {ex.Message}"); }
    }

    private sealed class PrefsFile { public bool AutoPopup { get; set; } = true; }
}
