using System.Text;
using System.Text.RegularExpressions;

namespace TaoDBM.Core;

/// <summary>功能批纯逻辑（2026-11-26，发现100/136/137/139）：任务中心取消/重跑口径、网格聚合提交缓冲、
/// 编辑器悬停元数据与使用处高亮、查询构建器三步出 SQL。只做数据与文案口径，不碰 UI 控件；
/// 测试锚 LayoutBatchCheck 逐条对账。</summary>

// 发现100：任务中心取消/重跑——终态可重跑、运行中只能取消；取消需二次确认；落账行格式统一。
public static class TaskOps
{
    public static string StateLabel(string state) => state switch
    {
        "done" => "完成",
        "failed" => "失败",
        "canceled" => "已取消",
        "running" => "运行中",
        _ => state,
    };

    /// <summary>终态（done/failed/canceled）可重跑；running 不可重跑（TaskCenter.Retry 同口径）。</summary>
    public static bool CanRetry(string state) => state is "done" or "failed" or "canceled";

    /// <summary>取消确认文案（二次确认条口径）。</summary>
    public static string CancelConfirmText(string title) =>
        $"确定取消任务「{title}」？取消后任务标记「已取消」，已完成的步骤保留。";

    public static string CancelLog(string taskId) => $"[任务中心] cancel id={taskId}";
    public static string RetryLog(string newId, string fromId) => $"[任务中心] retry id={newId} from={fromId}";
    public static string CancelDenyLog(string taskId) => $"[任务中心] cancel-denied id={taskId}";
}

// 发现136：网格聚合提交——修改不即时入库，攒进缓冲；应用时按行聚合成 UPDATE 并逐行落留痕账；放弃整批清空。
public sealed record PendingEdit(int RowIndex, int ColIndex, string Column, string? OldValue, string NewValue, string KeyDisplay);

public sealed class GridBatchEditBuffer
{
    private readonly List<PendingEdit> _edits = new();

    /// <summary>同一单元格重复修改只保留最新一条。</summary>
    public void Add(int rowIndex, int colIndex, string column, string? oldValue, string newValue, string keyDisplay)
    {
        _edits.RemoveAll(e => e.RowIndex == rowIndex && e.ColIndex == colIndex);
        _edits.Add(new PendingEdit(rowIndex, colIndex, column, oldValue, newValue, keyDisplay));
    }

    public IReadOnlyList<PendingEdit> Edits => _edits;
    public bool HasEdits => _edits.Count > 0;
    public int Count => _edits.Count;

    /// <summary>差异摘要行：「#主键 列: 旧值 → 新值」（UI 层逐行渲染，旧值删除线/新值强调）。</summary>
    public List<(string KeyDisplay, string Column, string? OldValue, string NewValue)> DiffLines() =>
        _edits.Select(e => (e.KeyDisplay, e.Column, e.OldValue, e.NewValue)).ToList();

    /// <summary>按行聚合：同主键多列改动合成一条 UPDATE；返回 (SQL, before镜像, after镜像, 主键值) 计划，执行与审计由调用方走 ExecWrite 全口径。</summary>
    public List<(string Sql, IReadOnlyDictionary<string, string?> Before, IReadOnlyDictionary<string, string?> After, string KeyValue)> Plan(
        string table, string keyColumn, Func<string, string> quote, Func<ColumnSpec, string, string> literal, IReadOnlyList<ColumnSpec> cols)
    {
        var plan = new List<(string, IReadOnlyDictionary<string, string?>, IReadOnlyDictionary<string, string?>, string)>();
        foreach (var g in _edits.GroupBy(e => e.RowIndex))
        {
            var keyDisplay = g.First().KeyDisplay;
            var sets = new List<string>();
            var before = new Dictionary<string, string?>();
            var after = new Dictionary<string, string?>();
            foreach (var e in g)
            {
                var spec = cols.FirstOrDefault(c => c.Name == e.Column);
                string val = e.NewValue.Length == 0
                    ? (spec is not null && CsvImporter.IsNumeric(spec.DataType) ? "NULL" : "''")
                    : (spec is null ? "'" + e.NewValue.Replace("'", "''", StringComparison.Ordinal) + "'" : literal(spec, e.NewValue));
                sets.Add($"{quote(e.Column)} = {val}");
                before[e.Column] = e.OldValue;
                after[e.Column] = e.NewValue.Length == 0 && spec is not null && CsvImporter.IsNumeric(spec.DataType) ? null : e.NewValue;
            }
            var sql = $"UPDATE {quote(table)} SET {string.Join(", ", sets)} WHERE {quote(keyColumn)} = '{keyDisplay.Replace("'", "''", StringComparison.Ordinal)}'";
            plan.Add((sql, before, after, keyDisplay));
        }
        return plan;
    }

    /// <summary>移除已提交（或单条重试成功）的行改动；保留其余。</summary>
    public void RemoveRow(int rowIndex) => _edits.RemoveAll(e => e.RowIndex == rowIndex);

    public void Discard() => _edits.Clear();
}

// 发现137：编辑器悬停元数据——词提取、使用处高亮区间、元数据卡文案；断连/取不到显「元数据暂不可用」。
public static class EditorHoverOps
{
    /// <summary>悬停 300ms 口径常量（UI 层计时器用）。</summary>
    public const int HoverDelayMs = 300;

    /// <summary>光标处标识符（字母/数字/下划线；空文本或非词字符返回空）。</summary>
    public static (int Start, int Length) WordAt(string text, int caret)
    {
        if (string.IsNullOrEmpty(text) || caret <= 0 || caret > text.Length) return (0, 0);
        int s = caret;
        while (s > 0 && (char.IsLetterOrDigit(text[s - 1]) || text[s - 1] == '_')) s--;
        int e = caret;
        while (e < text.Length && (char.IsLetterOrDigit(text[e]) || text[e] == '_')) e++;
        return s < e ? (s, e - s) : (0, 0);
    }

    /// <summary>标识符在全文的使用处区间（整词匹配，避免 order_id 命中 order）。</summary>
    public static List<(int Start, int Length)> HighlightUses(string text, string word)
    {
        var hits = new List<(int, int)>();
        if (word.Length == 0) return hits;
        foreach (Match m in Regex.Matches(text, Regex.Escape(word)))
        {
            int s = m.Index;
            bool leftOk = s == 0 || !(char.IsLetterOrDigit(text[s - 1]) || text[s - 1] == '_');
            int e = s + word.Length;
            bool rightOk = e >= text.Length || !(char.IsLetterOrDigit(text[e]) || text[e] == '_');
            if (leftOk && rightOk) hits.Add((s, word.Length));
        }
        return hits;
    }

    /// <summary>元数据卡行：类型徽标＋名称＋键值；行数/主键/注释取不到传 null 落「暂不可用」。</summary>
    public static List<(string Label, string Value)> CardLines(string kind, string name, string? rows, string? pk, string? comment)
    {
        string na = "暂不可用";
        return new List<(string, string)>
        {
            ("类型", kind),
            ("名称", name),
            ("行数估算", string.IsNullOrWhiteSpace(rows) ? na : rows),
            ("主键", string.IsNullOrWhiteSpace(pk) ? na : pk),
            ("注释", string.IsNullOrWhiteSpace(comment) ? na : comment),
        };
    }
}

// 发现139：查询构建器——三步（勾表→关联字段→生成 SQL）；外键候选标「推荐」，同名字段作同名候选。
public sealed record JoinCandidate(string LeftTable, string LeftColumn, string RightTable, string RightColumn, bool Recommended, string Source);

public static class QueryBuilderCore
{
    /// <summary>两表关联候选：外键目录命中标「推荐」（Source=外键）；同名列（非主键）作同名候选（Source=同名）。</summary>
    public static List<JoinCandidate> Candidates(
        string t1, IReadOnlyList<ColumnSpec> c1, string t2, IReadOnlyList<ColumnSpec> c2,
        IEnumerable<(string Table, string Column, string RefTable, string RefColumn)> fks)
    {
        var list = new List<JoinCandidate>();
        foreach (var fk in fks)
        {
            if (fk.Table == t1 && fk.RefTable == t2)
                list.Add(new JoinCandidate(t1, fk.Column, t2, fk.RefColumn, true, "外键"));
            else if (fk.Table == t2 && fk.RefTable == t1)
                list.Add(new JoinCandidate(t2, fk.Column, t1, fk.RefColumn, true, "外键"));
        }
        if (list.Count == 0)
        {
            var names1 = c1.Where(c => !c.IsKey).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var c in c2.Where(c => !c.IsKey && names1.Contains(c.Name)))
                list.Add(new JoinCandidate(t1, c.Name, t2, c.Name, false, "同名"));
        }
        return list;
    }

    /// <summary>生成 SQL：首表别名 a、次表 b…；有勾选关联走 JOIN，无关联退 CROSS JOIN（调用方提示）。</summary>
    public static string BuildSql(IReadOnlyList<string> tables, IReadOnlyList<JoinCandidate> picks, Func<string, string> quote)
    {
        if (tables.Count == 0) return "";
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string letters = "abcdefghijklmnop";
        for (int i = 0; i < tables.Count && i < letters.Length; i++) aliases[tables[i]] = letters[i].ToString();
        var sb = new StringBuilder("SELECT ");
        sb.Append(string.Join(", ", tables.Select(t => $"{aliases[t]}.*")));
        sb.Append($"\nFROM {quote(tables[0])} {aliases[tables[0]]}");
        for (int i = 1; i < tables.Count; i++)
        {
            var t = tables[i];
            var pick = picks.FirstOrDefault(p =>
                (p.LeftTable == tables[i - 1] && p.RightTable == t) ||
                (p.LeftTable == t && p.RightTable == tables[i - 1]));
            if (pick is null)
                sb.Append($"\nCROSS JOIN {quote(t)} {aliases[t]}");
            else
            {
                // ON 列方向：按表定位列——左表取 LeftColumn、右表取 RightColumn（2026-11-26 测试锚抓出原实现方向写反）
                string leftCol = pick.LeftTable == tables[i - 1] ? pick.LeftColumn : pick.RightColumn;
                string rightCol = pick.RightTable == t ? pick.RightColumn : pick.LeftColumn;
                sb.Append($"\nJOIN {quote(t)} {aliases[t]} ON {aliases[pick.LeftTable]}.{leftCol} = {aliases[pick.RightTable]}.{rightCol}");
            }
        }
        return sb.ToString();
    }
}
