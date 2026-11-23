namespace TaoDBM.Core;

/// <summary>代码段（批次一）：常用 SQL 模板，占位符用 {表}/{列} 等中文占位，插入后由用户替换。</summary>
public static class SqlSnippets
{
    /// <summary>单条代码段：名称、分类、模板正文。</summary>
    public sealed record Snippet(string Name, string Category, string Sql);

    private static readonly List<Snippet> All = new()
    {
        new("查询全部列", "查询", "SELECT *\nFROM {表}\nLIMIT 100;"),
        new("条件查询", "查询", "SELECT {列}\nFROM {表}\nWHERE {条件}\nORDER BY {列} DESC;"),
        new("分组统计", "查询", "SELECT {分组列}, COUNT(*) AS 条数\nFROM {表}\nGROUP BY {分组列}\nHAVING COUNT(*) > 1\nORDER BY 条数 DESC;"),
        new("多表连接", "查询", "SELECT a.*, b.{列}\nFROM {表A} a\nLEFT JOIN {表B} b ON b.{外键} = a.{主键};"),
        new("子查询", "查询", "SELECT *\nFROM {表}\nWHERE {列} IN (\n    SELECT {列} FROM {表B} WHERE {条件}\n);"),
        new("新增行", "写入", "INSERT INTO {表} ({列1}, {列2})\nVALUES ({值1}, {值2});"),
        new("更新行", "写入", "UPDATE {表}\nSET {列} = {值}\nWHERE {主键} = {值};"),
        new("删除行", "写入", "DELETE FROM {表}\nWHERE {主键} = {值};"),
        new("建表", "结构", "CREATE TABLE {表} (\n    id BIGINT PRIMARY KEY,\n    name VARCHAR(200) NOT NULL,\n    created_at DATETIME DEFAULT CURRENT_TIMESTAMP\n);"),
        new("加索引", "结构", "CREATE INDEX idx_{表}_{列} ON {表} ({列});"),
        new("事务块", "其他", "BEGIN;\n\n-- 多条写入语句\n\nCOMMIT;"),
        new("分页查询", "查询", "SELECT *\nFROM {表}\nORDER BY {主键}\nLIMIT {每页} OFFSET {偏移};"),
    };

    /// <summary>全部代码段（按分类分组前的平铺清单）。</summary>
    public static IReadOnlyList<Snippet> Items => All;

    /// <summary>按关键字过滤（名称或正文包含，忽略大小写）。</summary>
    public static List<Snippet> Search(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return All;
        return All.Where(s => s.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                              s.Sql.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
