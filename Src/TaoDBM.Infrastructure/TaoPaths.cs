using System.Runtime.InteropServices;

namespace TaoDBM.Core;

/// <summary>
/// Tao 系列存储规范：应用数据默认放 D 盘（D:\TaoDBMData\TaoDBM），不占 C 盘系统空间；
/// 非 Windows 或 D 盘不可用（创建/写入失败）时回退 %LOCALAPPDATA%\TaoDBM，绝不闪退。
/// 首次运行可把旧 C 盘目录的数据文件迁移过来（已存在同名文件不覆盖）。
/// </summary>
public static class TaoPaths
{
    /// <summary>数据根目录（构造时确定，全程不变）。</summary>
    public static string Root { get; }

    /// <summary>非 Windows 平台直接用回退目录，保证桌面逻辑跨平台可测。</summary>
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    static TaoPaths()
    {
        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaoDBM");
        Root = fallback;
        if (!IsWindows) return;
        try
        {
            var d = Path.Combine(@"D:\", "TaoDBMData", "TaoDBM");
            Directory.CreateDirectory(d);
            var probe = Path.Combine(d, ".write-probe");
            System.IO.File.WriteAllText(probe, "ok");
            System.IO.File.Delete(probe);
            Root = d;
        }
        catch
        {
            // D 盘不可用：保持回退目录，不闪退
        }
    }

    /// <summary>数据根下的文件路径（自动建目录）。</summary>
    public static string FilePath(string name)
    {
        Directory.CreateDirectory(Root);
        return Path.Combine(Root, name);
    }

    /// <summary>数据根下子目录中的文件路径（自动建目录）——发现108/112：旁路统一收口到数据根。</summary>
    public static string FilePathIn(string subDir, string name)
    {
        var dir = Path.Combine(Root, subDir);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    /// <summary>旧 C 盘目录（升级迁移来源）：Roaming 与 Local 两处 %APPDATA%。</summary>
    public static IEnumerable<string> LegacyDirs() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaoDBM"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaoDBM"),
    ];

    /// <summary>把旧目录的数据文件迁到当前 Root；目标已存在则不覆盖。返回实际迁移的文件名。
    /// 发现95：迁移清单补齐全部用户面文件（数据根实际持久化 20+ 个，原清单只有 5 个，升级丢配置）。</summary>
    public static List<string> MigrateLegacyFiles(IEnumerable<string>? legacyDirs = null, string? targetRoot = null)
    {
        var moved = new List<string>();
        try
        {
            // 发现95：清单扩容——根目录用户面文件全量＋子目录产物（快照/共享包/合并/任务状态/迁移状态）
            string[] dataFiles =
            [
                "connections.json", "favorites.json", "tabsession.json",
                "theme.json", "history.json",
                "appprefs.json", "notify.json", "focusmode.json", "sqlcomplete.json",
                "kanban-pins.json", "alert-rules.json", "alert-events.jsonl",
                "followups.json", "dashboard-layout.json", "report-page.json",
                "sqlopen.json", "windowstate.json", "conn-groups.json",
                "scheduled-sql.json", "tao_backup_history.json",
            ];
            // 子目录产物按整目录保守复制（同名不覆盖）
            string[] subDirs = ["snapshots", "share", "teamshare", "taskstates", "migrations"];
            var root = targetRoot ?? Root;
            Directory.CreateDirectory(root);
            foreach (var oldDir in legacyDirs ?? LegacyDirs())
            {
                if (!Directory.Exists(oldDir) || Path.GetFullPath(oldDir).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var name in dataFiles)
                {
                    var src = Path.Combine(oldDir, name);
                    var dst = Path.Combine(root, name);
                    if (!System.IO.File.Exists(src) || System.IO.File.Exists(dst)) continue;
                    System.IO.File.Copy(src, dst, overwrite: false);
                    moved.Add(name);
                }
                foreach (var sub in subDirs)
                {
                    var srcDir = Path.Combine(oldDir, sub);
                    if (!Directory.Exists(srcDir)) continue;
                    foreach (var file in Directory.EnumerateFiles(srcDir))
                    {
                        var dst = Path.Combine(root, sub, System.IO.Path.GetFileName(file));
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        if (System.IO.File.Exists(dst)) continue;
                        System.IO.File.Copy(file, dst, overwrite: false);
                        moved.Add($"{sub}/{System.IO.Path.GetFileName(file)}");
                    }
                }
            }
        }
        catch
        {
            // 迁移失败不阻断启动，用户数据仍留在原处可用
        }
        return moved;
    }
}
