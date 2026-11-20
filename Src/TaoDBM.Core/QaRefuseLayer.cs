using System.Text.RegularExpressions;

namespace TaoDBM.Core;

/// <summary>
/// 问库安全拒答层（B0 词表 v2，2026-11-19 晚定稿）：模型/规则层之前的硬闸。
/// 命中即确定性拒答或转确认流程，问题文本永不透传给模型层；
/// 词表为拒绝清单，保持最小面防误伤，只读聚合类问题不在此拦截（走语义层/记忆层）。
/// 离线验证（题库 930 题）：17 条陷阱题全命中、896 条非陷阱题零误伤。
/// </summary>
public static class QaRefuseLayer
{
    /// <summary>拒答判定：Refused=是否拦截；Classes=命中的类别名（可多个）；Reply=给用户的确定性安全回复。</summary>
    public sealed record Verdict(bool Refused, IReadOnlyList<string> Classes, string Reply);

    /// <summary>词表 v2：类别名 → 正则（对用户问题原文匹配，忽略大小写）。顺序即账单呈现顺序。</summary>
    internal static readonly (string Class, Regex Pattern)[] Rules =
    {
        ("高危DDL", new Regex(@"\bdrop\s+(table|database|schema|index)|truncate\s+(table\s+)?\w+|\btruncate\b", RegexOptions.IgnoreCase | RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("高危授权", new Regex(@"\b(grant|revoke)\b", RegexOptions.IgnoreCase | RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("删除意图", new Regex(@"(删掉|删除|清空|干掉).{0,12}(表|库|数据|所有)|(把|将).{0,10}(表|库|数据).{0,10}(删掉|删除|清空)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("批量改数", new Regex(@"(改|更新|替换|重置|设置).{0,12}(所有|全部|全|每个人).{0,10}(成|为)|(把|将).{0,20}(改成|更新成|设置成|清成)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("敏感列探测", new Regex(@"(查|看|获取|拿|多少).{0,15}密码|密码.{0,8}(查|看|获取|拿)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("数据外发", new Regex(@"(外网|发出去|发到|上传).{0,15}(库|数据)|(把|将).{0,10}数据.{0,10}(发|传|外)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("越权访问", new Regex(@"(别的库|其他库|另一个库|跨库|连到外网)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("绕过确认", new Regex(@"(别问我|不要问|别问|不要确认|直接执行|一起执行|顺便把)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("无守卫删除", new Regex(@"\bdelete\s+from\b", RegexOptions.IgnoreCase | RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("越权请求", new Regex(@"(生成|给我).{0,10}(管理员|后台|登录).{0,6}(sql|权限)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    private static readonly Dictionary<string, string> ClassReplies = new()
    {
        ["高危DDL"] = "这类操作会直接删库删表，问库不代做。请到对应功能里走带确认的危险操作流程。",
        ["高危授权"] = "账号授权类操作不在问库范围，请由管理员在数据库侧办理。",
        ["删除意图"] = "删除类请求问库不代做。如需清理数据，请走带预览和确认的删除流程。",
        ["批量改数"] = "批量改数影响面大，问库不代做。请走改数留痕流程，逐条预览确认。",
        ["敏感列探测"] = "密码等敏感信息不在问库可查范围。",
        ["数据外发"] = "数据不出内网，外发类请求问库不执行。",
        ["越权访问"] = "问库只在当前已连接的库内作答，不跨库、不外连。",
        ["绕过确认"] = "确认环节不能跳过。请按正常流程预览确认后执行。",
        ["无守卫删除"] = "不带条件的删除太危险，问库不代做。请走带预览的删除流程。",
        ["越权请求"] = "提权类请求问库不执行，请由管理员按正规流程办理。",
    };

    private static readonly string FallbackReply = "这个问题涉及危险操作，问库不代做。请走对应功能的确认流程。";

    /// <summary>对用户问题原文做安全检查；未命中返回 Refused=false，问题可继续走记忆/规则/模型层。</summary>
    public static Verdict Inspect(string? question)
    {
        var q = (question ?? "").Trim();
        if (q.Length == 0)
            return new Verdict(false, Array.Empty<string>(), "");
        var classes = new List<string>();
        foreach (var (name, pattern) in Rules)
        {
            if (pattern.IsMatch(q))
                classes.Add(name);
        }
        if (classes.Count == 0)
            return new Verdict(false, Array.Empty<string>(), "");
        var reply = ClassReplies.TryGetValue(classes[0], out var r) ? r : FallbackReply;
        return new Verdict(true, classes, reply);
    }
}
