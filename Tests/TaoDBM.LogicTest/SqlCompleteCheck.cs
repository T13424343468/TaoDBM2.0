using TaoDBM.Core;

namespace TaoDBM.LogicTest;

/// <summary>㉔㉘ 纯逻辑用例（2026-11-25 设计稿冻结落码）：SQL 补全别名感知/候选过滤/引用判定
/// ＋报表页卡片存储往返。独立文件接线（ComboMatrix 家法）。</summary>
public static class SqlCompleteCheck
{
    public delegate void CheckD(string name, bool ok, string detail = "");

    public static void RunAll(Action<string, bool, string> check)
    {
        CheckD d = (name, ok, detail) => check(name, ok, detail ?? "");
        Console.WriteLine("\n== ㉔㉘ SQL补全别名感知/报表页卡片存储（2026-11-25）==");
        RunAlias(d);
        RunFilter(d);
        RunQuote(d);
        RunReportStore(d);
        RunPrefs(d);
    }

    static void RunAlias(CheckD check)
    {
        // AS 别名：FROM orders AS o → o. 弹 orders 字段（光标停在点后）
        check("别名-AS归属", SqlComplete.QualifierTable("SELECT * FROM orders AS o WHERE o.", 34) == "orders",
            SqlComplete.QualifierTable("SELECT * FROM orders AS o WHERE o.", 34) ?? "null");
        // 裸别名：FROM orders o → o.
        check("别名-裸归属", SqlComplete.QualifierTable("SELECT * FROM orders o WHERE o.", 31) == "orders",
            SqlComplete.QualifierTable("SELECT * FROM orders o WHERE o.", 31) ?? "null");
        // 表名本名：FROM orders → orders.
        check("别名-表名本名", SqlComplete.QualifierTable("SELECT * FROM orders WHERE orders.", 34) == "orders",
            SqlComplete.QualifierTable("SELECT * FROM orders WHERE orders.", 34) ?? "null");
        // JOIN 亦识别（裸别名不吞 JOIN 关键字）
        check("别名-JOIN归属", SqlComplete.QualifierTable("SELECT * FROM a JOIN users u ON u.", 34) == "users",
            SqlComplete.QualifierTable("SELECT * FROM a JOIN users u ON u.", 34) ?? "null");
        // 无点不解析
        check("别名-无点返回空", SqlComplete.QualifierTable("SELECT * FROM orders", 20) == null, "");
        // 未知限定词不归属
        check("别名-未知限定词", SqlComplete.QualifierTable("SELECT * FROM orders o WHERE x.", 31) == null, "");
        // 裸别名是关键字（FROM orders WHERE）不误判
        check("别名-关键字不误判", SqlComplete.QualifierTable("SELECT * FROM orders WHERE x.", 29) == null, "");
        // 子查询就近：外层 o 与内层 i，光标在内层取内层
        string sub = "SELECT * FROM orders o WHERE EXISTS (SELECT 1 FROM items i WHERE i.";
        check("别名-子查询就近", SqlComplete.QualifierTable(sub, sub.Length) == "items",
            SqlComplete.QualifierTable(sub, sub.Length) ?? "null");
        // 引号表名（含空格）
        check("别名-引号表名", SqlComplete.QualifierTable("SELECT * FROM `order detail` od WHERE od.", 41) == "order detail",
            SqlComplete.QualifierTable("SELECT * FROM `order detail` od WHERE od.", 41) ?? "null");
    }

    static void RunFilter(CheckD check)
    {
        var pool = new[] { "SELECT", "SET", "SUM", "orders", "order_detail", "Orders" };
        var got = SqlComplete.Filter(pool, "S");
        check("过滤-前缀去重上限", got.Count == 3 && got[0] == "SELECT" && got.Contains("SET") && got.Contains("SUM"),
            string.Join(",", got));
        check("过滤-全等排除", !SqlComplete.Filter(pool, "orders").Any(k => k.Equals("orders", StringComparison.OrdinalIgnoreCase)),
            string.Join(",", SqlComplete.Filter(pool, "orders")));
        check("过滤-大小写不敏感", SqlComplete.Filter(pool, "se").SequenceEqual(new[] { "SELECT", "SET" }),
            string.Join(",", SqlComplete.Filter(pool, "se")));
        var many = Enumerable.Range(0, 20).Select(i => $"col{i}").ToList();
        check("过滤-上限8条", SqlComplete.Filter(many, "col").Count == SqlComplete.CandidateLimit, "");
        check("过滤-空词根空候选", SqlComplete.Filter(pool, "").Count == 0, "");
        // 发现66：限定词路径空词根回全量字段（截上限），有词根走过滤
        var cols = new[] { "id", "city", "amount", "created_at" };
        var all = SqlComplete.FilterQualified(cols, "");
        check("限定词-空词根回全量", all.Count == 4 && all.Contains("city") && all.Contains("amount"),
            string.Join(",", all));
        check("限定词-空词根截上限", SqlComplete.FilterQualified(many, "").Count == SqlComplete.CandidateLimit, "");
        check("限定词-空集回空", SqlComplete.FilterQualified(Array.Empty<string>(), "").Count == 0, "");
        check("限定词-有词根走过滤", SqlComplete.FilterQualified(cols, "ci").SequenceEqual(new[] { "city" }),
            string.Join(",", SqlComplete.FilterQualified(cols, "ci")));
        check("限定词-有词根全等排除", !SqlComplete.FilterQualified(cols, "id").Any(k => k.Equals("id", StringComparison.OrdinalIgnoreCase)), "");
    }

    static void RunQuote(CheckD check)
    {
        check("引用-普通标识符免引", !SqlComplete.NeedsQuote("order_detail") && !SqlComplete.NeedsQuote("Col$1") && !SqlComplete.NeedsQuote("select"), "");
        check("引用-特殊字符加引", SqlComplete.NeedsQuote("order detail") && SqlComplete.NeedsQuote("my-table"), "");
        check("引用-数字开头加引", SqlComplete.NeedsQuote("1st_col"), "");
        check("引用-空串免引", !SqlComplete.NeedsQuote(""), "");
    }

    static void RunReportStore(CheckD check)
    {
        var cardFile = Path.Combine(Path.GetTempPath(), "taodbm-reportpage-test-" + Guid.NewGuid().ToString("N") + ".json");
        ReportPageStore.UseFile(cardFile);
        try
        {
            check("报表页-缺失文件回落空表", ReportPageStore.All().Count == 0, "");
            var t0 = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
            ReportPageStore.Upsert(new ReportPageStore.Card("rp-1", "city · 分布", "SELECT city FROM t", "SQLite", "city", "bar", "shop", t0));
            check("报表页-钉住可查", ReportPageStore.All().Count == 1, "");
            var back = ReportPageStore.All()[0];
            check("报表页-往返还原", back.CardId == "rp-1" && back.Sql == "SELECT city FROM t" && back.Column == "city"
                && back.Db == "shop" && back.CreatedAt == t0, $"{back.CardId}/{back.Db}");
            check("报表页-图型归一", back.NormalizedType == "bar"
                && new ReportPageStore.Card("x", "t", "s", "SQLite", "c", "怪型").NormalizedType == "bar", "");
            ReportPageStore.Upsert(new ReportPageStore.Card("rp-1", "city · 分布v2", "SELECT city FROM t", "SQLite", "city", "line", "shop", t0));
            check("报表页-同id覆盖", ReportPageStore.All().Count == 1 && ReportPageStore.All()[0].Title == "city · 分布v2"
                && ReportPageStore.All()[0].NormalizedType == "line", "");
            ReportPageStore.Upsert(new ReportPageStore.Card("rp-2", "amount · 直方", "SELECT amount FROM t", "SQLite", "amount", "histogram", null, t0));
            check("报表页-两卡并存", ReportPageStore.All().Count == 2, "");
            check("报表页-移除返回真", ReportPageStore.Remove("rp-1") && !ReportPageStore.Remove("rp-1"), "");
            check("报表页-移除后查无", ReportPageStore.All().Count == 1 && ReportPageStore.All()[0].CardId == "rp-2", "");
            File.WriteAllText(cardFile, "{ 损坏的 JSON");
            check("报表页-坏文件回落空表", ReportPageStore.All().Count == 0, "");
        }
        finally
        {
            try { File.Delete(cardFile); } catch { }
            ReportPageStore.UseFile("");
        }
    }

    static void RunPrefs(CheckD check)
    {
        var prefFile = Path.Combine(Path.GetTempPath(), "taodbm-sqlcomplete-test-" + Guid.NewGuid().ToString("N") + ".json");
        SqlCompletePrefs.UseFile(prefFile);
        try
        {
            check("补全开关-缺失默认开", SqlCompletePrefs.Load(), "");
            SqlCompletePrefs.Save(false);
            check("补全开关-写读往返", SqlCompletePrefs.Load() == false, "");
            SqlCompletePrefs.Invalidate();   // 发现70：缓存命中也要能被强制重读打破
            File.WriteAllText(prefFile, "{broken");
            check("补全开关-损坏回落默认", SqlCompletePrefs.Load() == true, "");
        }
        finally
        {
            try { File.Delete(prefFile); } catch { }
            SqlCompletePrefs.UseFile("");
        }
    }
}
