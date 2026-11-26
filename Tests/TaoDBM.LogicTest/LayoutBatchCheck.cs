using TaoDBM.Core;

namespace TaoDBM.LogicTest;

/// <summary>布局批测试锚（2026-11-26，发现132/133/134/135 + 功能批 100/136/137/139）：
/// 纯逻辑逐条断言，口径出自 LayoutBatchCore / FuncBatchCore；UI 层（MainWindow.LayoutBatch.cs 等）
/// 按同一口径落码，语义级验证归云测首验。</summary>
public static class LayoutBatchCheck
{
    public delegate void CheckD(string name, bool ok, string detail = "");

    public static void RunAll(Action<string, bool, string> check)
    {
        CheckD d = (name, ok, detail) => check(name, ok, detail ?? "");
        Console.WriteLine("\n== 布局批+功能批 纯逻辑用例（2026-11-26）==");
        RunDomainNav(d);        // 发现132
        RunObjectActionBar(d);  // 发现133
        RunDetailPanel(d);      // 发现134
        RunWelcomeBoard(d);     // 发现135
        // 批二（100/136/137/139）断言随 FuncBatchCore 批次提交接入
    }

    // 发现132：域清单 5 项固定；当前域判定——任务中心 > ER 图 > 备份 > 查询页签 > 默认表域
    static void RunDomainNav(CheckD d)
    {
        var keys = DomainNav.Items.Select(i => i.Key).ToList();
        d("132 域清单五项", keys.Count == 5 && string.Join(",", keys) == "tables,query,backup,taskcenter,erd",
            string.Join(",", keys));
        d("132 当前域优先级", DomainNav.ActiveKey("Query", false, true, false) == "taskcenter"
            && DomainNav.ActiveKey("Query", false, false, true) == "erd"
            && DomainNav.ActiveKey("Query", true, false, false) == "backup"
            && DomainNav.ActiveKey("Query", false, false, false) == "query"
            && DomainNav.ActiveKey("TableData", false, false, false) == "tables"
            && DomainNav.ActiveKey(null, false, false, false) == "tables",
            "taskcenter>erd>backup>query>tables");
        d("132 域条目文案非空", DomainNav.Items.All(i => i.Label.Length > 0 && i.Tooltip.Length > 0), "");
    }

    // 发现133：四态操作清单；权限不足置灰带原因；AI 占位；无选中给状态一句话
    static void RunObjectActionBar(CheckD d)
    {
        var (ctxT, actsT) = ObjectActionBarModel.Build("table");
        d("133 表态操作含打开/设计/删除", ctxT == "表"
            && actsT.Any(a => a.Key == "open" && a.Primary)
            && actsT.Any(a => a.Key == "design")
            && actsT.Any(a => a.Key == "delete"), ctxT);
        var (_, actsNoWrite) = ObjectActionBarModel.Build("table", canWrite: false);
        d("133 无写权限设计/新建置灰带原因", actsNoWrite.First(a => a.Key == "design") is { Enabled: false, Reason.Length: > 0 }
            && actsNoWrite.First(a => a.Key == "newtable").Enabled == false, "");
        var (_, actsDb) = ObjectActionBarModel.Build("database");
        d("133 库态含新建表/查询/备份/还原", actsDb.Select(a => a.Key).SequenceEqual(new[] { "newtable", "query", "backup", "restore" }), "");
        var (_, actsConn) = ObjectActionBarModel.Build("connection");
        d("133 连接态含查询/备份/任务中心", actsConn.Select(a => a.Key).SequenceEqual(new[] { "query", "backup", "taskcenter" }), "");
        var (_, actsNone) = ObjectActionBarModel.Build("none");
        d("133 无选中态新建连接为主操作", actsNone.First(a => a.Key == "newconn").Primary, "");
        d("133 AI 建表占位", actsT.First(a => a.Key == "ai") is { Enabled: false, AiPlaceholder: true, Reason: "随 AI 版批启用" }, "");
        d("133 空闲提示分口径", ObjectActionBarModel.IdleHint(0).Contains("还没有连接")
            && ObjectActionBarModel.IdleHint(3).Contains("共 3 个连接"), "");
    }

    // 发现134：字段模型——取不到落「暂不可用」；列摘要带 PK 徽标口径
    static void RunDetailPanel(CheckD d)
    {
        var tf = DetailPanelModel.TableFields("orders", "MySQL", null, "12 MB", "", "订单表", null);
        d("134 表详情缺字段落暂不可用", tf.First(f => f.Label == "行数估算").Value == "暂不可用"
            && tf.First(f => f.Label == "行数估算").Ok == false
            && tf.First(f => f.Label == "数据大小").Value == "12 MB"
            && tf.First(f => f.Label == "字符集").Value == "暂不可用"
            && tf.First(f => f.Label == "注释").Value == "订单表"
            && !tf.Any(f => f.Label == "设置文件"), "");
        var tfSqlite = DetailPanelModel.TableFields("main", "SQLite", "3", null, null, null, "D:/data/a.db");
        d("134 SQLite 带设置文件行", tfSqlite.Any(f => f.Label == "设置文件" && f.Value == "D:/data/a.db"), "");
        var df = DetailPanelModel.DatabaseFields("shop", "MySQL", null, "utf8mb4", null);
        d("134 库详情子对象缺落暂不可用", df.First(f => f.Label == "子对象").Value == "暂不可用"
            && df.First(f => f.Label == "字符集").Value == "utf8mb4", "");
        var cf = DetailPanelModel.ConnectionFields("本地库", "MySQL", "127.0.0.1", 3306, "");
        d("134 连接详情状态缺落暂不可用", cf.First(f => f.Label == "状态").Value == "暂不可用"
            && cf.First(f => f.Label == "端口").Value == "3306", "");
        var cols = DetailPanelModel.ColumnSummary(new List<ColumnSpec>
        {
            new() { Name = "id", DataType = "INT", IsKey = true },
            new() { Name = "memo", DataType = "VARCHAR(50)", IsKey = false },
        });
        d("134 列摘要带PK口径", cols.Count == 2 && cols[0].Pk && cols[0].Name == "id" && !cols[1].Pk, "");
    }

    // 发现135：相对时间口径；星标优先；最近打开文案；新手条偏好落盘
    static void RunWelcomeBoard(CheckD d)
    {
        var now = new DateTime(2026, 11, 26, 15, 0, 0);
        d("135 相对时间口径", WelcomeBoard.RelativeTime(now.AddSeconds(-30), now) == "刚刚"
            && WelcomeBoard.RelativeTime(now.AddMinutes(-5), now) == "5 分钟前"
            && WelcomeBoard.RelativeTime(now.AddHours(-2), now) == "2 小时前"
            && WelcomeBoard.RelativeTime(new DateTime(2026, 11, 25, 9, 5, 0), now) == "昨天 09:05"
            && WelcomeBoard.RelativeTime(new DateTime(2026, 11, 20, 9, 0, 0), now) == "11-20", "");
        var p1 = new ConnectionProfile { Id = "a", DisplayName = "甲", Starred = true };
        var p2 = new ConnectionProfile { Id = "b", DisplayName = "乙", Starred = false };
        var p3 = new ConnectionProfile { Id = "c", DisplayName = "丙", Starred = true };
        var sorted = WelcomeBoard.StarredFirst(new[] { p2, p1, p3 });
        d("135 星标优先保持原序", sorted[0].Id == "a" && sorted[1].Id == "c" && sorted[2].Id == "b", "");
        d("135 最近打开文案", WelcomeBoard.RecentLabel("Query", "shop", "") == "查询页签 · shop"
            && WelcomeBoard.RecentLabel("TableData", "shop", "orders") == "数据浏览 · shop / orders"
            && WelcomeBoard.RecentLabel("Query", "", "") == "查询页签 · 连接", "");
        var dir = Path.Combine(Path.GetTempPath(), "taodbm-test-welcome-" + Guid.NewGuid().ToString("N"));
        WelcomePrefs.UseFile(Path.Combine(dir, "welcome.json"));
        try
        {
            d("135 新手条默认显示", WelcomePrefs.ShowGuide, "");
            WelcomePrefs.DismissGuide();
            d("135 新手条关闭后不再显示", WelcomePrefs.ShowGuide == false, "");
        }
        finally { WelcomePrefs.UseFile(""); Directory.Delete(dir, true); }
    }

}
