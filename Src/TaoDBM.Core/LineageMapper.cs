
namespace TaoDBM.Core;

/// <summary>协作线：数据血缘影响地图引擎（改表前提示下游视图/例程依赖；SQL 语料建图，逐级追溯）。</summary>
/// <remarks>纯引擎免界面稿：语料由调用方喂 DDL/例程文本；提示准确性抽验由 Impact 报告附对象定义片段供人工核对。</remarks>

/// <summary>血缘对象：Type=view/routine/table/other；Definition 保留原文片段用于抽验。</summary>
public sealed record LineageObject(string Name, string Type, string? Definition = null);

/// <summary>影响项：下游对象及追溯路径（如 orders ← v_order_sum ← v_order_dash）。</summary>
public sealed record LineageImpact(string Object, string Type, List<string> Path, string? Definition = null);

public static class LineageMapper
{
    /// <summary>建图：解析 SQL 语料中 CREATE VIEW / CREATE (OR ALTER) PROCEDURE|FUNCTION 等定义及其引用的表。</summary>
    public static Dictionary<string, List<LineageObject>> Build(IEnumerable<(LineageObject Obj, string Body)> corpus)
    {
        var map = new Dictionary<string, List<LineageObject>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (obj, body) in corpus)
        {
            if (obj.Type.Equals("table", StringComparison.OrdinalIgnoreCase)) continue; // 表自身不挂依赖
            foreach (var refName in References(body))
            {
                if (refName.Equals(obj.Name, StringComparison.OrdinalIgnoreCase)) continue;
                var key = refName.ToLowerInvariant();
                if (!map.TryGetValue(key, out var list)) map[key] = list = new List<LineageObject>();
                if (!list.Any(o => o.Name.Equals(obj.Name, StringComparison.OrdinalIgnoreCase)))
                    list.Add(obj);
            }
        }
        return map;
    }

    /// <summary>影响追溯：被改对象直接与间接（视图套视图）的全部下游，路径从被改对象起逐级记录。</summary>
    public static List<LineageImpact> Impact(Dictionary<string, List<LineageObject>> map, string changedObject)
    {
        var impacts = new List<LineageImpact>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { changedObject };
        var frontier = new List<(LineageObject Obj, List<string> Path)> { (new LineageObject(changedObject, "table"), new List<string> { changedObject }) };
        while (frontier.Count > 0)
        {
            var next = new List<(LineageObject, List<string>)>();
            foreach (var (obj, path) in frontier)
            {
                if (!map.TryGetValue(obj.Name, out var deps)) continue;
                foreach (var d in deps)
                {
                    if (!visited.Add(d.Name)) continue;
                    var p = path.Append(d.Name).ToList();
                    impacts.Add(new LineageImpact(d.Name, d.Type, p, d.Definition));
                    next.Add((d, p));
                }
            }
            frontier = next;
        }
        return impacts.OrderBy(i => i.Path.Count).ThenBy(i => i.Object, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>提示文案：改表前逐条下游影响，含抽验用的定义片段（截 200 字符）。</summary>
    public static List<string> Hints(List<LineageImpact> impacts) => impacts
        .Select(i => $"变更 {i.Path[0]} 将影响 {i.Type} {i.Object}（路径 {string.Join(" ← ", i.Path.AsEnumerable().Reverse())}）"
            + (string.IsNullOrWhiteSpace(i.Definition) ? "" : $"\n  抽验片段：{Cut(i.Definition!)}"))
        .ToList();

    /// <summary>从 SQL 文本提取被引用对象名：FROM/JOIN/INTO/UPDATE/DELETE FROM 后的标识符，剥引号与库名前缀。</summary>
    internal static HashSet<string> References(string body)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     body, @"\b(?:FROM|JOIN|INTO|UPDATE)\s+([`""\[]?[A-Za-z_][\w$]*[`""\]]?)(?:\s*[`""\]]?\.[\w$\[\]`""]+)?",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var name = m.Groups[1].Value.Trim('`', '"', '[', ']');
            if (!Reserved(name)) found.Add(name);
        }
        return found;
    }

    static bool Reserved(string w) =>
        w.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || w.Equals("SET", StringComparison.OrdinalIgnoreCase) ||
        w.Equals("VALUES", StringComparison.OrdinalIgnoreCase) || w.Equals("DUAL", StringComparison.OrdinalIgnoreCase);

    static string Cut(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
