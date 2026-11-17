using System.Text;
using System.Text.RegularExpressions;

namespace TaoDBM.Core;

/// <summary>多表联查问库的输入表规格：表名 + 列信息（来自 GetColumns）。</summary>
public sealed record MultiTableSpec(string Name, IReadOnlyList<ColumnSpec> Columns);

/// <summary>P-3 多表联查问法扩展：识别“A 表关联 B 表”类中文问法（含一起/以及/加上/join 等口语变体），
/// 按外键命名约定（子表.xxx_id → 主表.id，或显式提供的已知关系）推断表间关联，生成只读 SELECT 预览。
/// 解析不了时明确报因（“不支持该问法，建议改写或等模型兜底”），绝不猜测、不拼凑 SQL；
/// 危险语义（写库/DDL）一律拒绝。产物只读预览，必须经人确认后才执行。</summary>
public static class NaturalLanguageJoinQuery
{
    static readonly string[] JoinWords = { "关联", "一起", "以及", "加上", "联结", "连接", "联查", "连同", "联合", "join" };
    static readonly string[] DangerWords =
    {
        "删除", "删掉", "删了", "清空", "插入", "写入", "更新", "修改", "改成", "建表", "删表",
        "drop", "delete", "update", "insert", "truncate", "alter",
    };

    /// <summary>把多表中文问题解析成 JOIN SELECT。tables 为可用表及列信息；knownEdges 为可选的显式关系。</summary>
    public static NLQueryResult Ask(string question, IReadOnlyList<MultiTableSpec> tables, DbEngine engine,
        IReadOnlyList<ErdEdge>? knownEdges = null)
    {
        var q = (question ?? "").Trim().Trim('？', '?', '。', '.').Replace(" ", "");
        if (q.Length == 0)
            return Fail("请先输入问题。");
        if (tables is null || tables.Count == 0)
            return Fail("没有可用的表信息，请先连接数据库并刷新表结构。");
        if (!EngineInfo.IsRelational(engine))
            return Fail($"该问法仅支持关系型数据库，{EngineInfo.Name(engine)} 没有表关联概念。");

        var danger = DangerHit(q.ToLowerInvariant());
        if (danger is not null)
            return Fail($"问题涉及“{danger}”这类写库或改表操作，已拒绝：本工具只生成只读 SELECT 预览，危险语义一律不执行。");

        // ---- 识别被提及的表（按出现顺序；表名与问题双方都去掉下划线后匹配）----
        var nq = q.Replace("_", "").ToLowerInvariant();
        var mentioned = new List<(MultiTableSpec Spec, int Pos)>();
        for (int i = 0; i < tables.Count; i++)
        {
            var nt = Norm(tables[i].Name);
            if (nt.Length == 0) continue;
            var pos = nq.IndexOf(nt, StringComparison.Ordinal);
            if (pos >= 0) mentioned.Add((tables[i], pos));
        }
        if (mentioned.Count < 2)
            return Fail("不支持该问法：问题里没有认出两个及以上的表，建议写清楚表名（例如“orders 表关联 users 表”），或等待模型兜底。");
        mentioned.Sort((x, y) => x.Pos.CompareTo(y.Pos));

        if (!JoinWords.Any(w => q.ToLowerInvariant().Contains(w)))
            return Fail("不支持该问法：没认出表间的联查意图，建议用“关联/一起/加上”等词把表连起来（例如“orders 关联 users”），或等待模型兜底。");

        // ---- 相邻可连即连：每张后续表尝试与任一先前提及的表建立外键关联 ----
        var joinParts = new List<string>();
        for (int i = 1; i < mentioned.Count; i++)
        {
            string? onSql = null;
            string? linkedTo = null;
            for (int j = 0; j < i && onSql is null; j++)
            {
                var link = LinkTables(mentioned[j].Spec, mentioned[i].Spec, knownEdges ?? Array.Empty<ErdEdge>(), engine);
                if (link is not null) { onSql = link.Value.OnSql; linkedTo = mentioned[j].Spec.Name; }
            }
            if (onSql is null)
            {
                var left = mentioned[i].Spec.Name;
                var tried = string.Join("/", mentioned.Take(i).Select(x => x.Spec.Name));
                return Fail($"不支持该问法：未能推断出 {tried} 与 {left} 之间的关联字段" +
                            "（可按“子表.外键 → 主表.id”命名，例如 orders.user_id → users.id），建议改写问题或等待模型兜底。");
            }
            joinParts.Add($"{Quote(engine, mentioned[i].Spec.Name)} ON {onSql}");
        }

        // ---- 聚合 / 过滤 / 排序 ----
        bool wantsCount = Regex.IsMatch(q, "多少|几条|几行|几笔|总数|行数");
        bool wantsMax = q.Contains("最大") || q.Contains("最高") || q.Contains("最多") || q.Contains("最晚");
        bool wantsMin = q.Contains("最小") || q.Contains("最低") || q.Contains("最少") || q.Contains("最早");

        int? limit = null;
        var mTop = Regex.Match(q, @"前(\d+|[一二三四五六七八九十]+)名?|([一二三四五六七八九十]+)条");
        if (mTop.Success) limit = ParseNum(mTop.Groups[1].Success ? mTop.Groups[1].Value : mTop.Groups[2].Value);

        var conditions = new List<string>();
        var usedCols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (spec, _) in mentioned)
            foreach (var c in spec.Columns.Where(c => !string.IsNullOrEmpty(c.Name)))
                if (usedCols.Add(c.Name))
                {
                    var cond = BuildCondition(q, c, Qn(engine, spec.Name, c.Name), engine);
                    if (!string.IsNullOrEmpty(cond)) conditions.Add(cond);
                }

        var sb = new StringBuilder();
        sb.Append($"SELECT {(wantsCount ? "COUNT(*)" : "*")} FROM {Quote(engine, mentioned[0].Spec.Name)}");
        foreach (var jp in joinParts)
            sb.Append(" JOIN ").Append(jp);

        if (conditions.Count > 0)
            sb.Append(" WHERE ").Append(string.Join(" AND ", conditions));

        if (!wantsCount && (wantsMax || wantsMin))
        {
            var oc = FindAggCol(q, mentioned.Select(x => x.Spec));
            if (oc is not null)
                sb.Append($" ORDER BY {Qn(engine, oc.Value.Spec.Name, oc.Value.Col.Name)} {(wantsMin ? "ASC" : "DESC")}");
        }
        if (limit is > 0 && !wantsCount)
            sb.Append(engine switch
            {
                DbEngine.SqlServer => $" OFFSET 0 ROWS FETCH NEXT {limit} ROWS ONLY",
                DbEngine.Oracle => $" FETCH FIRST {limit} ROWS ONLY",
                _ => $" LIMIT {limit}",
            });

        var names = mentioned.Select(x => x.Spec.Name).ToList();
        string human = $"联查 {string.Join(" 与 ", names)}（按外键关联）" +
            (wantsCount ? "，统计关联后的总行数。" : "，列出满足条件的记录。");
        return new NLQueryResult(true, sb + ";", human, "high");
    }

    /// <summary>危险词扫描；“修改”后跟“时间/日期”属列名指代（如“修改时间最近 7 天”），不算写库语义。</summary>
    static string? DangerHit(string low)
    {
        foreach (var w in DangerWords)
        {
            int pos = 0;
            while ((pos = low.IndexOf(w, pos, StringComparison.Ordinal)) >= 0)
            {
                bool colRef = w == "修改" && pos + w.Length < low.Length &&
                              (low[pos + w.Length] == '时' || low[pos + w.Length] == '日');
                if (!colRef) return w;
                pos += w.Length;
            }
        }
        return null;
    }

    // ---- 关联推断 ----

    /// <summary>已知关系优先，其次命名约定；返回 ON 等式或 null。</summary>
    static (string OnSql, ErdEdge Edge)? LinkTables(MultiTableSpec a, MultiTableSpec b, IReadOnlyList<ErdEdge> known, DbEngine engine)
    {
        foreach (var e in known)
        {
            if (Eq(e.FromTable, a.Name) && Eq(e.ToTable, b.Name))
                return (BuildOn(a, b, e.FromCol, true, engine), e);
            if (Eq(e.FromTable, b.Name) && Eq(e.ToTable, a.Name))
                return (BuildOn(b, a, e.FromCol, false, engine), e);
        }
        if (FkCol(a, b) is { } ca)
            return (BuildOn(a, b, ca, true, engine), new ErdEdge { FromTable = a.Name, FromCol = ca, ToTable = b.Name });
        if (FkCol(b, a) is { } cb)
            return (BuildOn(b, a, cb, false, engine), new ErdEdge { FromTable = b.Name, FromCol = cb, ToTable = a.Name });
        return null;
    }

    static string BuildOn(MultiTableSpec fkTable, MultiTableSpec pkTable, string fkCol, bool fkOnLeft, DbEngine engine)
    {
        var pk = PkCol(pkTable);
        var l = fkOnLeft ? Qn(engine, fkTable.Name, fkCol) : Qn(engine, pkTable.Name, pk);
        var r = fkOnLeft ? Qn(engine, pkTable.Name, pk) : Qn(engine, fkTable.Name, fkCol);
        return $"{l} = {r}";
    }

    static string PkCol(MultiTableSpec t) =>
        t.Columns.FirstOrDefault(c => c.IsKey && c.Name.Equals("id", StringComparison.OrdinalIgnoreCase))?.Name
        ?? t.Columns.FirstOrDefault(c => c.IsKey)?.Name ?? "id";

    /// <summary>命名约定：source 表存在 xxx_id 列且 xxx 匹配 target 表名（含复数），target 有主键。</summary>
    static string? FkCol(MultiTableSpec source, MultiTableSpec target)
    {
        if (!target.Columns.Any(c => c.IsKey)) return null;
        var tn = Norm(target.Name);
        var stemTarget = tn.EndsWith("s") && tn.Length > 1 ? tn[..^1] : tn;
        foreach (var c in source.Columns.Where(c => !c.IsKey))
        {
            var name = c.Name;
            string? stem = name.EndsWith("_id", StringComparison.OrdinalIgnoreCase) ? name[..^3]
                : name.EndsWith("Id", StringComparison.OrdinalIgnoreCase) ? name[..^2] : null;
            if (stem is null) continue;
            var ns = Norm(stem);
            if (ns == stemTarget || ns + "s" == tn || ns == tn) return c.Name;
        }
        return null;
    }

    // ---- 条件与排序 ----

    static string? BuildCondition(string q, ColumnSpec c, string qc, DbEngine engine)
    {
        bool isTime = IsTimeCol(c);
        bool isNum = IsNumCol(c);

        foreach (var op in new[] { "大于", "超过", "高于", "不少于", "不低于", "小于", "低于", "不多于" })
        {
            var m = Regex.Match(q, Regex.Escape(c.Name) + op + @"(\d+(?:\.\d+)?)");
            if (m.Success)
                return $"{qc} {(op is "大于" or "超过" or "高于" ? ">" : op is "小于" or "低于" ? "<" : ">=")} {m.Groups[1].Value}";
        }
        if (isNum)
        {
            var m = Regex.Match(q, @"(\d+(?:\.\d+)?)(以上|以下)");
            if (m.Success) return $"{qc} {(m.Groups[2].Value == "以上" ? ">=" : "<=")} {m.Groups[1].Value}";
        }
        if (isTime)
        {
            var days = Regex.Match(q, @"(?:最近|近|过去)(\d+)天");
            if (days.Success)
            {
                int n = int.Parse(days.Groups[1].Value);
                var expr = engine == DbEngine.MySQL
                    ? $"NOW() - INTERVAL {n} DAY"
                    : $"CURRENT_DATE - INTERVAL '{n} DAY'";
                return $"{qc} >= {expr}";
            }
            if (q.Contains("今天") || q.Contains("今日"))
                return engine == DbEngine.MySQL ? $"{qc} >= CURDATE()" : $"{qc} >= CURRENT_DATE";
            if (q.Contains("昨天") || q.Contains("昨日"))
                return engine == DbEngine.MySQL
                    ? $"{qc} >= CURDATE() - INTERVAL 1 DAY"
                    : $"{qc} >= CURRENT_DATE - INTERVAL '1 DAY'";
        }
        var me = Regex.Match(q, Regex.Escape(c.Name) + @"(?:是|等于|为)([\w\u4e00-\u9fa5]+)");
        if (me.Success && !isNum && !isTime)
            return $"{qc} = '{me.Groups[1].Value.Replace("'", "''")}'";
        var mc = Regex.Match(q, Regex.Escape(c.Name) + @"(?:包含|含有|含)([\w\u4e00-\u9fa5]+)");
        if (mc.Success && !isNum && !isTime)
            return $"{qc} LIKE '%{mc.Groups[1].Value.Replace("'", "''")}%'";
        return null;
    }

    static (MultiTableSpec Spec, ColumnSpec Col)? FindAggCol(string q, IEnumerable<MultiTableSpec> specs)
    {
        var m = Regex.Match(q, @"([\w\u4e00-\u9fa5]+?)的?(?:平均值|平均|最大值|最大|最高|最多|最晚|最小值|最小|最低|最少|最早)");
        if (!m.Success) return null;
        var target = Norm(m.Groups[1].Value);
        var all = specs.SelectMany(s => s.Columns.Where(c => !string.IsNullOrEmpty(c.Name)).Select(c => (s, c)))
            .ToList();
        foreach (var (s, c) in all)
            if (Norm(c.Name) == target)
                return (s, c);
        // 去空格后列名前面紧挨着其他词（如“users amount”连成“usersamount”），按尾段匹配
        foreach (var (s, c) in all)
        {
            var cn = Norm(c.Name);
            if (cn.Length > 0 && target.EndsWith(cn, StringComparison.Ordinal)) return (s, c);
        }
        return null;
    }

    static NLQueryResult Fail(string reason) => new(false, "", "", "none", reason);

    static string Norm(string s) => (s ?? "").Replace("_", "").ToLowerInvariant();

    static bool Eq(string x, string y) => Norm(x) == Norm(y);

    static string Qn(DbEngine engine, string table, string col) => $"{Quote(engine, table)}.{Quote(engine, col)}";

    static bool IsTimeCol(ColumnSpec c) =>
        c.DataType.ToLowerInvariant().Contains("date") || c.DataType.ToLowerInvariant().Contains("time") ||
        c.Name.ToLowerInvariant() is "created_at" or "updated_at" || c.Name.Contains("时间") || c.Name.Contains("日期");

    static bool IsNumCol(ColumnSpec c) =>
        new[] { "int", "decimal", "numeric", "float", "double", "real", "bigint", "smallint", "number" }
            .Any(k => c.DataType.ToLowerInvariant().Contains(k));

    static int? ParseNum(string s)
    {
        if (int.TryParse(s, out int n)) return n;
        return s switch
        {
            "一" => 1, "二" => 2, "两" => 2, "三" => 3, "四" => 4, "五" => 5,
            "六" => 6, "七" => 7, "八" => 8, "九" => 9, "十" => 10,
            _ => null,
        };
    }

    static string Quote(DbEngine engine, string ident) => engine switch
    {
        DbEngine.MySQL => "`" + ident.Replace("`", "") + "`",
        _ => "\"" + ident.Replace("\"", "") + "\"",
    };
}
