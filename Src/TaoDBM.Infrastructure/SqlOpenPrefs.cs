namespace TaoDBM.Core;

/// <summary>.sql 打开偏好（需求合并稿第十六章，2026-11-19 23:52 老板令）：
/// 双击 / 拖入 .sql 文件时装载到最近激活窗口（默认开）；损坏、缺失回落默认，绝不闪退。</summary>
public static class SqlOpenPrefs
{
    private static string _file = "";
    private static string File => _file.Length > 0 ? _file : TaoPaths.FilePath("sqlopen.json");   // Tao 系列存储默认 D 盘

    /// <summary>测试或自定义存储位置。</summary>
    public static void UseFile(string path) => _file = path;

    /// <summary>读取是否在最近激活窗口打开 .sql；任何异常都安全回落 true。</summary>
    public static bool Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return true;
            var v = System.Text.Json.JsonSerializer.Deserialize<PrefsFile>(System.IO.File.ReadAllText(File));
            return v?.OpenInLatestWindow != false;
        }
        catch (Exception ex)
        {
            Core.AppLog.Write($".sql 打开偏好读取失败，回落默认: {ex.Message}");
            return true;
        }
    }

    public static void Save(bool openInLatestWindow)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File)!);
            AtomicWrite.Text(File, System.Text.Json.JsonSerializer.Serialize(new PrefsFile
            {
                OpenInLatestWindow = openInLatestWindow,
            }));
        }
        catch (Exception ex) { Core.AppLog.Write($".sql 打开偏好保存失败: {ex.Message}"); }
    }

    private sealed class PrefsFile { public bool OpenInLatestWindow { get; set; } = true; }
}
