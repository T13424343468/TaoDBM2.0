// 拟真数据快捷预设（批次四格6，2026-11-23）：姓名 / 手机 / 身份证段 / 地址四类一键模板。
// 预设按表名模式给行数建议（命中模式的表给默认行数，其余 0），一键填入多表计划；生成口径不变。
namespace TaoDBM.Core;

/// <summary>一键模板：按表名模式匹配行数（纯逻辑，可测试）。</summary>
public sealed record FakerPreset(string Key, string Title, string Hint, int DefaultRows, string[] TablePatterns)
{
    /// <summary>四类一键模板（姓名 / 手机 / 身份证段 / 地址）。</summary>
    public static readonly FakerPreset[] All =
    [
        new("name", "姓名", "user / person / member 类表 1000 行", 1000,
            new[] { "user", "person", "member", "customer", "employee", "staff" }),
        new("phone", "手机", "contact / phone 类表 500 行", 500,
            new[] { "contact", "phone", "mobile", "tel", "sms" }),
        new("idcard", "身份证段", "id / cert / kyc 类表 200 行", 200,
            new[] { "id", "cert", "kyc", "identity", "idcard" }),
        new("address", "地址", "address / region 类表 300 行", 300,
            new[] { "address", "region", "area", "geo", "city", "district" }),
    ];

    /// <summary>表名是否命中模板模式（前缀 / 包含，忽略大小写）。</summary>
    public bool Matches(string tableName)
    {
        var t = tableName.ToLowerInvariant();
        foreach (var p in TablePatterns)
            if (t.Contains(p, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>按模板给每表行数：命中 → DefaultRows，未命中 → 0（不生成）。</summary>
    public Dictionary<string, int> RowsFor(IEnumerable<string> tables) =>
        tables.ToDictionary(t => t, t => Matches(t) ? DefaultRows : 0, StringComparer.OrdinalIgnoreCase);
}
