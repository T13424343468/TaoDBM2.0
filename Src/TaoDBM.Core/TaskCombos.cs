using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>
/// 发现142：任务中心批处理组合——勾选多条任务保存为命名组合（执行顺序即勾选顺序），
/// 存数据根 task-combos/ 子目录；加载组合后由调用方按序提交进任务队列。
/// 纯逻辑下沉 Core：组合校验（去空/去重/顺序）独立可测。
/// </summary>
public static class TaskCombos
{
    /// <summary>组合定义：Name=组合名；Steps=按序任务描述（标题+SQL+库，提交时展开）。</summary>
    public sealed record ComboStep(string Title, string Db, string Sql);
    public sealed record TaskCombo(string Name, IReadOnlyList<ComboStep> Steps);

    private static string DirName => "task-combos";

    private static string PathOf(string name) =>
        TaoPaths.FilePathIn(DirName, ImportProfiles.SafeName(name) + ".json");

    public static void Save(TaskCombo combo)
    {
        var dto = new
        {
            combo.Name,
            Steps = combo.Steps.Select(s => new { s.Title, s.Db, s.Sql }).ToList(),
            SavedAt = DateTimeOffset.Now,
        };
        System.IO.File.WriteAllText(PathOf(combo.Name),
            JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }),
            new System.Text.UTF8Encoding(false));
        AppLog.Write($"[任务组合] 已保存 {combo.Name}（{combo.Steps.Count} 步）");
    }

    public static TaskCombo? Load(string name)
    {
        try
        {
            var json = System.IO.File.ReadAllText(PathOf(name));
            var root = JsonDocument.Parse(json).RootElement;
            var steps = root.GetProperty("Steps").EnumerateArray()
                .Select(s => new ComboStep(
                    s.GetProperty("Title").GetString() ?? "",
                    s.TryGetProperty("Db", out var d) ? d.GetString() ?? "" : "",
                    s.TryGetProperty("Sql", out var q) ? q.GetString() ?? "" : ""))
                .ToList();
            return new TaskCombo(root.GetProperty("Name").GetString() ?? name, steps);
        }
        catch (Exception ex)
        {
            AppLog.Write($"[任务组合] 读取失败 {name}: {ex.Message}");
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
        try { System.IO.File.Delete(PathOf(name)); AppLog.Write($"[任务组合] 已删除 {name}"); return true; }
        catch { return false; }
    }

    /// <summary>发现142：组合校验——剔除空标题/空 SQL 步骤，重复标题按顺序加序号后缀；
    /// 返回规整后的步骤清单（空组合返回空表，调用方给人话提示）。</summary>
    public static List<ComboStep> Normalize(IEnumerable<ComboStep> steps)
    {
        var seen = new Dictionary<string, int>();
        var result = new List<ComboStep>();
        foreach (var s in steps)
        {
            var title = (s.Title ?? "").Trim();
            var sql = (s.Sql ?? "").Trim();
            if (title.Length == 0 || sql.Length == 0) continue;
            if (seen.TryGetValue(title, out var n))
            {
                seen[title] = n + 1;
                title = $"{title}·{n + 1}";
            }
            else seen[title] = 1;
            result.Add(new ComboStep(title, s.Db ?? "", sql));
        }
        return result;
    }

    /// <summary>发现142：命令行参数解析——从启动参数提取 -run-task &lt;id&gt;（可多个，大小写不敏感）；
    /// 返回任务 Id 清单（去重、保序）；无参数返回空表。参数格式错（缺值）跳过并返回警告消息。</summary>
    public static (List<string> TaskIds, List<string> Warnings) ParseRunTaskArgs(IEnumerable<string> args)
    {
        var ids = new List<string>();
        var warns = new List<string>();
        var argv = (args ?? Enumerable.Empty<string>()).ToArray();
        for (int i = 0; i < argv.Length; i++)
        {
            if (!argv[i].Equals("-run-task", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= argv.Length || argv[i + 1].StartsWith('-'))
            {
                warns.Add("-run-task 后面缺任务 Id，已忽略。");
                continue;
            }
            var id = argv[++i].Trim();
            if (id.Length > 0 && !ids.Contains(id)) ids.Add(id);
        }
        return (ids, warns);
    }
}
