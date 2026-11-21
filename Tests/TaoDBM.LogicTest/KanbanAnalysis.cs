using TaoDBM.Core;

namespace TaoDBM.LogicTest;

/// <summary>批次E纯逻辑用例（2026-11-20）：点选式分析 CellAnalysis + 看板钉住/定时刷新 KanbanPinStore。
/// 独立文件接线（ComboMatrix.RunAll 家法），避免与其他批次的 Program.cs 改动冲突。</summary>
public static class KanbanAnalysis
{
    /// <summary>带默认 detail 的检查委托：Call 端口可两参省略 detail（与 Program.Check 同形）。</summary>
    public delegate void CheckD(string name, bool ok, string detail = "");

    public static void RunAll(Action<string, bool, string> check)
    {
        CheckD d = (name, ok, detail) => check(name, ok, detail ?? "");
        Console.WriteLine("\n== 批次E 点选式分析/看板钉住定时刷新（2026-11-20）==");
        RunCellAnalysis(d);
        RunKanbanPins(d);
    }

    static void RunCellAnalysis(CheckD check)
    {
        var r = new QueryResult
        {
            Columns = new() { "city", "amount", "note" },
            Rows = new()
            {
                new object?[] { "上海", 10.5, "a" },
                new object?[] { "北京", 20, "b" },
                new object?[] { "上海", null, null },
                new object?[] { null, 7.5, "c" },
                new object?[] { "深圳", 12, "b" },
            },
        };

        var s = CellAnalysis.Analyze(r, "city");
        check("CA-列概览-总数与非空", s.Total == 5 && s.NonNull == 4, $"{s.Total}/{s.NonNull}");
        check("CA-列概览-去重数", s.Distinct == 3, s.Distinct.ToString());
        check("CA-列概览-非数值列", !s.IsNumeric, s.IsNumeric.ToString());
        check("CA-TopN-计数降序稳定", s.Top.Count == 3 && s.Top[0].Value == "上海" && s.Top[0].Count == 2 &&
            s.Top[1].Value == "北京" && s.Top[2].Value == "深圳", string.Join(",", s.Top.Select(t => $"{t.Value}:{t.Count}")));

        var a = CellAnalysis.Analyze(r, "amount");
        check("CA-数值列-判定", a.IsNumeric, a.IsNumeric.ToString());
        check("CA-数值列-min-max-avg", a.Min == 7.5 && a.Max == 20 && Math.Abs(a.Avg!.Value - 12.5) < 1e-9, $"{a.Min}/{a.Max}/{a.Avg}");

        var n = CellAnalysis.Analyze(r, "note");
        check("CA-空值列-非数值无聚合", !n.IsNumeric && n.Min == null && n.Avg == null, n.IsNumeric.ToString());
        check("CA-TopN-取前N上限", CellAnalysis.Analyze(r, "city", topN: 2).Top.Count == 2, "");

        bool threw = false;
        try { CellAnalysis.Analyze(r, "不存在的列"); }
        catch (ArgumentException) { threw = true; }
        check("CA-容错-列不存在抛参数异常", threw, "");

        var empty = new QueryResult { Columns = new() { "x" }, Rows = new() };
        var es = CellAnalysis.Analyze(empty, "x");
        check("CA-空集-零行零聚合", es.Total == 0 && es.NonNull == 0 && es.Distinct == 0 && es.Min == null, "");
        check("CA-接图-TopN直出ChartSpec", CellAnalysis.ToChart(r, "city").Type == "bar", "");
        check("CA-口径-一句话概览非空", s.Headline.Contains("共 5 行"), s.Headline);
    }

    static void RunKanbanPins(CheckD check)
    {
        var pinFile = Path.Combine(Path.GetTempPath(), "taodbm-kanban-test-" + Guid.NewGuid().ToString("N") + ".json");
        KanbanPinStore.UseFile(pinFile);
        var t0 = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

        KanbanPinStore.Upsert(new KanbanPinStore.Pin("pin-1", "城市分布", "SELECT city FROM t", "SQLite", "bar-city", 30, t0, "shop"));
        check("KB-钉住-已钉可查", KanbanPinStore.IsPinned("pin-1"));
        check("KB-钉住-往返还原", KanbanPinStore.All().Count == 1 && KanbanPinStore.All()[0].Title == "城市分布" && KanbanPinStore.All()[0].Sql == "SELECT city FROM t");
        check("KB-钉住-库归属往返", KanbanPinStore.All()[0].Db == "shop", KanbanPinStore.All()[0].Db ?? "null");

        KanbanPinStore.Upsert(new KanbanPinStore.Pin("pin-1", "城市分布v2", "SELECT city FROM t", "SQLite", null, 0, t0));
        check("KB-钉住-同id覆盖", KanbanPinStore.All().Count == 1 && KanbanPinStore.All()[0].Title == "城市分布v2" && !KanbanPinStore.All()[0].AutoRefresh);

        KanbanPinStore.Upsert(new KanbanPinStore.Pin("pin-2", "3秒太急", "SELECT 1", "SQLite", null, 3, t0));
        check("KB-间隔-低于下限夹5s", KanbanPinStore.All().First(p => p.PinId == "pin-2").IntervalSeconds == KanbanPinStore.MinIntervalSeconds);

        // pin-1 不自动刷新（0），pin-2 每5s刷新：t0+10s 时 pin-2 到期
        var due = KanbanPinStore.Due(t0.AddSeconds(10));
        check("KB-到期-仅自动刷新入队", due.Count == 1 && due[0].PinId == "pin-2", string.Join(",", due.Select(d => d.PinId)));
        check("KB-水位-未到期不重跑", KanbanPinStore.Due(t0.AddSeconds(4)).Count == 0, "");

        KanbanPinStore.Touch("pin-2", t0.AddSeconds(10));
        check("KB-水位-刷新后重新计时", KanbanPinStore.Due(t0.AddSeconds(11)).Count == 0 && KanbanPinStore.Due(t0.AddSeconds(15)).Count == 1, "");

        check("KB-摘除-可摘且返回真", KanbanPinStore.Unpin("pin-1"));
        check("KB-摘除-重复摘返回假", !KanbanPinStore.Unpin("pin-1"));
        check("KB-摘除-查无此钉", !KanbanPinStore.IsPinned("pin-1"));

        // 容错：坏文件回落空表，绝不闪退
        File.WriteAllText(pinFile, "{ not json at all");
        check("KB-容错-坏文件回落空表", KanbanPinStore.All().Count == 0, "");
        try { File.Delete(pinFile); } catch { }
        KanbanPinStore.UseFile("");
    }
}
