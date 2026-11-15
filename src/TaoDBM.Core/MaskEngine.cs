using System.Globalization;

namespace TaoDBM.Core;

/// <summary>脱敏档位：None=不脱敏；Secret=密码类整值打码。</summary>
public enum MaskRule
{
    /// <summary>非敏感列，原样导出。</summary>
    None,
    /// <summary>手机/电话：留前3后4。</summary>
    Phone,
    /// <summary>邮箱：留首字母与域名。</summary>
    Email,
    /// <summary>证件号：留前3后4。</summary>
    IdCard,
    /// <summary>姓名：留首位。</summary>
    Name,
    /// <summary>银行卡：留后4。</summary>
    BankCard,
    /// <summary>密码/密钥类：整值打码。</summary>
    Secret,
}

/// <summary>
/// 安全线引擎：一键脱敏导出的纯逻辑部分（第十章「一键脱敏导出」）。
/// 按列名关键词推断敏感档位，对单元格值打码；不读文件、不连库，
/// CSV/JSON 导出管线与查询结果网格均可直接复用 MaskCell/MaskRow。
/// </summary>
public static class MaskEngine
{
    /// <summary>结构性的 *_name 列不按姓名脱敏，避免导出元数据被误打码。</summary>
    private static readonly HashSet<string> StructuralNames = new(StringComparer.Ordinal)
    {
        "table_name", "column_name", "db_name", "database_name", "schema_name",
        "index_name", "file_name", "field_name", "constraint_name", "view_name",
        "trigger_name", "proc_name", "routine_name", "engine_name", "job_name",
    };

    /// <summary>关键词表：顺序即优先级，先命中先定档。</summary>
    private static readonly (string[] Keys, MaskRule Rule)[] KeywordTable =
    {
        (new[] { "密码", "口令", "密钥", "password", "passwd", "secret", "token" }, MaskRule.Secret),
        (new[] { "身份证", "证件号", "idcard", "id_card", "ssn" }, MaskRule.IdCard),
        (new[] { "银行卡", "卡号", "bank_card", "bankcard", "card_no", "cardno" }, MaskRule.BankCard),
        (new[] { "手机", "电话", "phone", "mobile", "tel", "msisdn" }, MaskRule.Phone),
        (new[] { "邮箱", "email", "mail" }, MaskRule.Email),
        (new[] { "姓名", "联系人", "name", "contact" }, MaskRule.Name),
    };

    /// <summary>按列名推断脱敏档位；overrides 允许调用方按列名精确覆盖（大小写不敏感）。</summary>
    public static MaskRule InferRule(string column, IReadOnlyDictionary<string, MaskRule>? overrides = null)
    {
        if (string.IsNullOrWhiteSpace(column)) return MaskRule.None;
        var key = column.Trim();
        if (overrides is not null)
        {
            foreach (var kv in overrides)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
        }
        var norm = Normalize(key);
        if (StructuralNames.Contains(norm)) return MaskRule.None;
        foreach (var (keys, rule) in KeywordTable)
            foreach (var k in keys)
                if (norm.Contains(k, StringComparison.Ordinal))
                    return rule;
        return MaskRule.None;
    }

    /// <summary>单值打码；空值原样返回空串，短值整段打码不越界。</summary>
    public static string Mask(string? value, MaskRule rule)
    {
        var v = value ?? "";
        if (rule == MaskRule.None || v.Length == 0) return v;
        return rule switch
        {
            MaskRule.Secret => new string('*', Math.Max(v.Length, 1)),
            MaskRule.Email => MaskEmail(v),
            MaskRule.Name => MaskName(v),
            MaskRule.BankCard => v.Length < 8 ? new string('*', v.Length) : new string('*', v.Length - 4) + v[^4..],
            MaskRule.Phone => MaskFixed(v, keepHead: 3, keepTail: 4, minLen: 7),
            MaskRule.IdCard => MaskFixed(v, keepHead: 3, keepTail: 4, minLen: 8),
            _ => v,
        };
    }

    /// <summary>单元格脱敏：先按列名定档再打码（None 档原样返回）。</summary>
    public static string MaskCell(string column, string? value, IReadOnlyDictionary<string, MaskRule>? overrides = null)
    {
        var rule = InferRule(column, overrides);
        return rule == MaskRule.None ? value ?? "" : Mask(value, rule);
    }

    /// <summary>行级脱敏：逐列按列名定档（供导出流式管线按行调用）。</summary>
    public static List<KeyValuePair<string, string>> MaskRow(
        IEnumerable<KeyValuePair<string, string>> row, IReadOnlyDictionary<string, MaskRule>? overrides = null) =>
        row.Select(kv => new KeyValuePair<string, string>(kv.Key, MaskCell(kv.Key, kv.Value, overrides))).ToList();

    private static string MaskFixed(string v, int keepHead, int keepTail, int minLen)
    {
        if (v.Length < minLen) return new string('*', v.Length);
        var head = v[..Math.Min(keepHead, v.Length)];
        var tail = v[^Math.Min(keepTail, v.Length)..];
        var middle = new string('*', Math.Max(v.Length - head.Length - tail.Length, 1));
        return head + middle + tail;
    }

    private static string MaskEmail(string v)
    {
        var at = v.IndexOf('@');
        if (at <= 0) return MaskFixed(v, keepHead: 1, keepTail: 1, minLen: 6);
        var local = v[..at];
        var domain = v[(at + 1)..];
        return (local.Length == 1 ? local[0].ToString(CultureInfo.InvariantCulture) : local[..1]) + "***@" + domain;
    }

    private static string MaskName(string v) =>
        v.Length <= 1 ? v : v[..1] + new string('*', v.Length - 1);

    private static string Normalize(string column) =>
        new string(column.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
