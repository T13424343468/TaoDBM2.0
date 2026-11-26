using System.Text.Json;
using TaoDBM.Core;

namespace TaoDBM.Infrastructure;

/// <summary>虚拟分组成员：组名 → 连接 Id 清单（组只是视图，连接数据不动）。</summary>
public sealed class ConnGroup
{
    public string Name { get; set; } = "";
    public List<string> MemberIds { get; set; } = new();
}

/// <summary>
/// 批次二格6：连接虚拟分组——本地 JSON 持久化（TaoPaths 数据根 conn-groups.json，D 盘优先），
/// 不落库、不影响连接本身；删组不删连接。纯逻辑可测试；线程安全（树操作与设置页并发读写）。
/// </summary>
public static class ConnectionGroups
{
    private static readonly object Gate = new();
    private static readonly string StorePath = TaoPaths.FilePath("conn-groups.json");   // 发现108/112：改走数据根

    private static List<ConnGroup> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return new List<ConnGroup>();
            return JsonSerializer.Deserialize<List<ConnGroup>>(File.ReadAllText(StorePath)) ?? new List<ConnGroup>();
        }
        catch { return new List<ConnGroup>(); }
    }

    private static void Save(List<ConnGroup> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static List<ConnGroup> List() { lock (Gate) return Load(); }

    /// <summary>新建组；重名返回 false。</summary>
    public static bool Create(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return false;
        lock (Gate)
        {
            var list = Load();
            if (list.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return false;
            list.Add(new ConnGroup { Name = name });
            Save(list);
            return true;
        }
    }

    public static bool Rename(string oldName, string newName)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) return false;
        lock (Gate)
        {
            var list = Load();
            var g = list.FirstOrDefault(x => x.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            if (g is null || list.Any(x => x != g && x.Name.Equals(newName, StringComparison.OrdinalIgnoreCase))) return false;
            g.Name = newName;
            Save(list);
            return true;
        }
    }

    /// <summary>删组不删连接：成员连接原地保留，只是不再归组。</summary>
    public static bool Delete(string name)
    {
        lock (Gate)
        {
            var list = Load();
            int n = list.RemoveAll(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (n > 0) Save(list);
            return n > 0;
        }
    }

    /// <summary>把连接移入组（先清出旧组再入新组，一连接只属一组）；组不存在自动建。</summary>
    public static void MoveTo(string profileId, string groupName)
    {
        groupName = (groupName ?? "").Trim();
        if (profileId.Length == 0 || groupName.Length == 0) return;
        lock (Gate)
        {
            var list = Load();
            foreach (var g in list) g.MemberIds.Remove(profileId);
            var target = list.FirstOrDefault(g => g.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
            if (target is null) { target = new ConnGroup { Name = groupName }; list.Add(target); }
            target.MemberIds.Add(profileId);
            Save(list);
        }
    }

    /// <summary>连接所属组名（不在任何组返回空串）。</summary>
    public static string GroupOf(string profileId)
    {
        lock (Gate)
        {
            foreach (var g in Load())
                if (g.MemberIds.Contains(profileId)) return g.Name;
            return "";
        }
    }

    /// <summary>组内成员 Id 清单（组不存在返回空表）。</summary>
    public static List<string> MembersOf(string groupName)
    {
        lock (Gate)
        {
            var g = Load().FirstOrDefault(x => x.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
            return g?.MemberIds ?? new List<string>();
        }
    }
}
