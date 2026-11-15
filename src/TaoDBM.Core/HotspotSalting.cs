using System.Text;

namespace TaoDBM.Core;

/// <summary>盐化方案：热点键被拆成的盐槽数量与盐化后子键清单（确定序 0..N-1）。</summary>
public sealed record SaltingPlan(string HotKey, int SaltCount, IReadOnlyList<string> SaltedKeys);

/// <summary>
/// 分库分区分表底座引擎：热点盐化（新需求配套，承接 A5-4 倾斜检测的「拆分加盐」处置建议）。
/// 把单一热点键（大商户号、单调递增段等）按盐槽数拆成 N 个盐化子键打散写入；
/// 写路由按 FNV-1a 对行键取模选槽（与 A5-3 哈希路由同算法），读取按扇出全盐槽后归并——纯计算不触库。
/// </summary>
public static class HotspotSalting
{
    /// <summary>盐槽数下界：1 槽等于不盐化，无意义。</summary>
    public const int MinSalt = 2;

    /// <summary>盐槽数上界：防止槽位表与扇出查询被无节制放大。</summary>
    public const int MaxSalt = 1024;

    /// <summary>
    /// 生成盐化方案：子键默认命名 {热点键}#{槽号}，可用 namer 自定义；
    /// 子键按槽号 0..N-1 确定序返回，供建表/路由/扇出查询共用同一命名。
    /// </summary>
    public static SaltingPlan Plan(string hotKey, int saltCount, Func<string, int, string>? namer = null)
    {
        if (string.IsNullOrWhiteSpace(hotKey)) throw new ArgumentException("热点键不能为空", nameof(hotKey));
        if (saltCount < MinSalt || saltCount > MaxSalt)
            throw new ArgumentOutOfRangeException(nameof(saltCount), $"盐槽数须在 {MinSalt}–{MaxSalt} 之间");
        var name = namer ?? ((k, i) => $"{k}#{i}");
        var keys = new List<string>(saltCount);
        for (int i = 0; i < saltCount; i++) keys.Add(name(hotKey, i));
        return new SaltingPlan(hotKey, saltCount, keys);
    }

    /// <summary>写路由：该行落在哪个盐槽（FNV-1a 对行键取模，确定序）；盐槽数超出盐化边界同样拒绝。</summary>
    public static int RouteWrite(string rowKey, int saltCount)
    {
        if (saltCount < MinSalt || saltCount > MaxSalt)
            throw new ArgumentOutOfRangeException(nameof(saltCount), $"盐槽数须在 {MinSalt}–{MaxSalt} 之间");
        uint h = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(rowKey)) { h ^= b; h *= 16777619; }
        return (int)(h % (uint)saltCount);
    }

    /// <summary>读扇出：按盐化方案返回全部子键（查询需覆盖全槽再归并，纯列表展开）。</summary>
    public static IReadOnlyList<string> FanOutRead(SaltingPlan plan) => plan.SaltedKeys;
}
