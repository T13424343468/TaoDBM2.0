using System.Globalization;
using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>协作线：表单化录入引擎（按表结构自动生成录入表单定义，业务同事免 SQL；类型校验+必填校验生效）。</summary>
/// <remarks>纯引擎免界面稿：产出字段定义/校验结果/参数化 INSERT，界面渲染由 App 层接。</remarks>

/// <summary>表单字段定义：InputType=text/number/date/time/datetime/bool；Required=非空列；自增主键自动隐藏。</summary>
public sealed record FormField(string Column, string Label, string InputType, bool Required, bool Hidden, int? MaxLength);

/// <summary>录入校验结果：Errors 按「列名→原因」累计，空字典即通过。</summary>
public sealed record FormValidation(bool Ok, Dictionary<string, string> Errors);

public static class FormSchemaBuilder
{
    /// <summary>从列结构生成表单定义：类型推断、必填判定、自增主键隐藏；overrides 可改 Label/Required/Hidden。</summary>
    public static List<FormField> Build(string table, IReadOnlyList<ColumnSpec> columns,
        Dictionary<string, (string? Label, bool? Required, bool? Hidden)>? overrides = null)
    {
        if (string.IsNullOrWhiteSpace(table)) throw new ArgumentException("表名不能为空", nameof(table));
        var fields = new List<FormField>();
        foreach (var c in columns)
        {
            bool identityKey = c.IsKey && c.DataType.Contains("INT", StringComparison.OrdinalIgnoreCase);
            bool required = !c.Nullable;
            bool hidden = identityKey;
            string? label = null;
            if (overrides != null && overrides.TryGetValue(c.Name, out var ov))
            {
                label = ov.Label; required = ov.Required ?? required; hidden = ov.Hidden ?? hidden;
            }
            fields.Add(new FormField(c.Name, label ?? c.Name, InputTypeOf(c.DataType), required, hidden,
                ParseMaxLength(c.DataType)));
        }
        return fields;
    }

    /// <summary>录入校验：必填缺失与类型不符逐列报出；隐藏字段（自增主键等）不参与校验。</summary>
    public static FormValidation Validate(IReadOnlyList<FormField> fields, Dictionary<string, string?> values)
    {
        var errors = new Dictionary<string, string>();
        foreach (var f in fields)
        {
            if (f.Hidden) continue;
            values.TryGetValue(f.Column, out var v);
            bool empty = string.IsNullOrWhiteSpace(v);
            if (f.Required && empty) { errors[f.Column] = "必填项未填写"; continue; }
            if (empty) continue;
            string? reason = f.InputType switch
            {
                "number" => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? null : "需为数字",
                "date" => DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : "需为日期",
                "time" => TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out _) ? null : "需为时间",
                "datetime" => DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : "需为日期时间",
                "bool" => IsBool(v!) ? null : "需为 true/false/1/0",
                _ => f.MaxLength is int max && v!.Length > max ? $"超长（≤{max}）" : null,
            };
            if (reason != null) errors[f.Column] = reason;
        }
        return new FormValidation(errors.Count == 0, errors);
    }

    /// <summary>校验通过后生成参数化 INSERT（@列名 占位，值由执行层绑定）；隐藏列跳过。</summary>
    public static (string Sql, Dictionary<string, string> Params) BuildInsert(string table,
        IReadOnlyList<FormField> fields, Dictionary<string, string?> values)
    {
        var taken = fields.Where(f => !f.Hidden && values.TryGetValue(f.Column, out var v) && !string.IsNullOrWhiteSpace(v)).ToList();
        if (taken.Count == 0) throw new ArgumentException("没有可写入的字段值");
        var cols = string.Join(", ", taken.Select(f => f.Column));
        var paras = string.Join(", ", taken.Select(f => "@" + f.Column));
        return ($"INSERT INTO {table} ({cols}) VALUES ({paras});",
            taken.ToDictionary(f => f.Column, f => values[f.Column]!.Trim()));
    }

    static string InputTypeOf(string dataType)
    {
        var t = dataType.Trim().ToUpperInvariant();
        if (t.Contains("BIT") || t.Contains("BOOL")) return "bool";
        if (t.Contains("DATE") && t.Contains("TIME")) return "datetime";
        if (t.Contains("DATETIME") || t.Contains("TIMESTAMP")) return "datetime";
        if (t.Contains("DATE")) return "date";
        if (t.Contains("TIME")) return "time";
        if (t.Contains("INT") || t.Contains("DEC") || t.Contains("NUM") || t.Contains("REAL") ||
            t.Contains("FLOAT") || t.Contains("DOUB")) return "number";
        return "text";
    }

    static int? ParseMaxLength(string dataType)
    {
        var m = System.Text.RegularExpressions.Regex.Match(dataType, @"\(\s*(\d+)\s*\)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    static bool IsBool(string v) => v.Trim().ToLowerInvariant() is "true" or "false" or "1" or "0";
}
