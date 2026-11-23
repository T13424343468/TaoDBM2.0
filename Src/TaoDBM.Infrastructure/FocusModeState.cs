using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>批次六格3：专注模式状态存取（%TaoDir%\focusmode.json；损坏回落关闭，重启恢复上次状态）。</summary>
public static class FocusModeState
{
    private static string _file = "";
    private static string File => _file.Length > 0 ? _file : TaoPaths.FilePath("focusmode.json");

    /// <summary>测试或自定义存储位置。</summary>
    public static void UseFile(string path) => _file = path;

    public static bool Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return false;
            return JsonSerializer.Deserialize<bool>(System.IO.File.ReadAllText(File));
        }
        catch { return false; }   // 损坏回落：默认非专注
    }

    public static void Save(bool on)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            AtomicWrite.Text(File, JsonSerializer.Serialize(on));
        }
        catch { }   // 状态落盘失败不阻断切换
    }
}
