using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>优化回访账目落盘（整改二批 #07，2026-11-23）：登记、复测、结论即写即存，重启不丢。
/// 纯文件逻辑，Tao 系列存储家法（默认 TaoPaths，测试 UseFile 注入），AtomicWrite 原子落盘；
/// 文件缺失或损坏回落空账，绝不闪退（坏账降级为空账＋日志）。SQL 原文落盘前由调用方过脱敏器。</summary>
public static class FollowupLedger
{
    private static readonly object Gate = new();

    private static string _file = "";
    private static string File => _file.Length > 0 ? _file : TaoPaths.FilePath("followups.json");

    /// <summary>测试或自定义存储位置。</summary>
    public static void UseFile(string path) => _file = path;

    /// <summary>全量账目（文件缺失/损坏回落空表）。</summary>
    public static List<OptimizationFollowup.FollowupTask> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!System.IO.File.Exists(File)) return new List<OptimizationFollowup.FollowupTask>();
                var tasks = JsonSerializer.Deserialize<List<OptimizationFollowup.FollowupTask>>(System.IO.File.ReadAllText(File));
                return tasks ?? new List<OptimizationFollowup.FollowupTask>();
            }
            catch (Exception ex)
            {
                AppLog.Write($"回访账读取失败，回落空账: {ex.Message}");
                return new List<OptimizationFollowup.FollowupTask>();
            }
        }
    }

    /// <summary>全量覆盖保存（先写临时文件再原子替换，防半截账）。</summary>
    public static void Save(IEnumerable<OptimizationFollowup.FollowupTask> tasks)
    {
        lock (Gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(File);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                AtomicWrite.Text(File, JsonSerializer.Serialize(tasks, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLog.Write($"回访账保存失败: {ex.Message}"); }
        }
    }
}
