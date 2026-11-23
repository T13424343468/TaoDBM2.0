// 三层模型（批次五格1，2026-11-23）：概念 / 逻辑 / 物理三层各自独立 ErdModel，视图状态 JSON 序列化可往返。
// 物理层 DDL 复用 ErdForward（正向生成），模型比较复用 SchemaDiff（ModelCompare）。
using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>模型层级。</summary>
public enum ModelLayer
{
    Conceptual,
    Logical,
    Physical,
}

public static class ModelLayerInfo
{
    public static string Name(ModelLayer l) => l switch
    {
        ModelLayer.Conceptual => "概念",
        ModelLayer.Logical => "逻辑",
        _ => "物理",
    };
}

/// <summary>三层模型文档：每层一张 ER 图（实体 / 关系 / 画布位置），当前层视图状态随文档序列化。</summary>
public class DataModelDoc
{
    public ModelLayer Current { get; set; } = ModelLayer.Physical;
    public ErdModel Conceptual { get; set; } = new();
    public ErdModel Logical { get; set; } = new();
    public ErdModel Physical { get; set; } = new();

    public ErdModel Of(ModelLayer l) => l switch
    {
        ModelLayer.Conceptual => Conceptual,
        ModelLayer.Logical => Logical,
        _ => Physical,
    };

    public ErdModel CurrentModel => Of(Current);

    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Opt);

    public static DataModelDoc? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<DataModelDoc>(json); }
        catch { return null; }
    }
}

/// <summary>模型比较（批次五格2）：模型实体集 vs 真库列结构，差异清单复用 SchemaDiff（默认不开删）。</summary>
public static class ModelCompare
{
    /// <summary>live：真库逐表列结构（GetColumns 结果）。返回差异条目（table-missing / column-missing / type-mismatch 等）。</summary>
    public static List<SchemaDiffItem> Diff(ErdModel model, IReadOnlyList<(string Table, List<ColumnSpec> Cols)> live)
    {
        var src = model.Tables.Select(t => SchemaTableSnapshot.FromSpecs(t.Name, t.Columns.Select(c => new ColumnSpec
        {
            Name = c.Name,
            DataType = c.Type,
            IsKey = c.IsKey,
        }))).ToList();
        var dst = live.Select(x => SchemaTableSnapshot.FromSpecs(x.Table, x.Cols)).ToList();
        return SchemaDiff.Diff(src, dst);
    }
}
