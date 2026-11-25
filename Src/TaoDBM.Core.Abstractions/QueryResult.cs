namespace TaoDBM.Core;

public class QueryResult
{
    public string Title { get; set; } = "结果";
    public List<string> Columns { get; set; } = new();
    public List<object?[]> Rows { get; set; } = new();
    /// <summary>-1 表示非写语句。</summary>
    public int Affected { get; set; } = -1;
    public TimeSpan Elapsed { get; set; }
    /// <summary>浏览模式下的总行数（可空）。</summary>
    public long? TotalRows { get; set; }
    public string Message { get; set; } = "";
    /// <summary>执行失败结构化标记（发现76：错误判定走标记，不再靠文案子串匹配）。</summary>
    public bool IsError { get; set; }
    public bool HasRows => Rows.Count > 0;
}

public class ColumnSpec
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public bool Nullable { get; set; } = true;
    public bool IsKey { get; set; }
    /// <summary>自增标记（发现74：改表前读回原样，MODIFY 带回不再静默丢）。</summary>
    public bool IsAutoIncrement { get; set; }
    /// <summary>列默认值原样表达式（发现75：库内读回，空表示无；改表时带回）。</summary>
    public string DefaultValue { get; set; } = "";
    /// <summary>列注释（发现75：MySQL 内联在列定义里，MODIFY 需原样带回）。</summary>
    public string Comment { get; set; } = "";

    public override string ToString() =>
        Name + (string.IsNullOrEmpty(DataType) ? "" : " · " + DataType) + (IsKey ? " · 🔑" : "");
}
