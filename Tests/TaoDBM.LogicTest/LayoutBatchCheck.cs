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
        RunTaskOps(d);          // 发现100
        RunGridBatchEdit(d);    // 发现136
        RunEditorHover(d);      // 发现137
        RunQueryBuilder(d);     // 发现139
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

    // 发现100：任务中心取消/重跑口径
    static void RunTaskOps(CheckD d)
    {
        d("100 状态文案", TaskOps.StateLabel("done") == "完成" && TaskOps.StateLabel("failed") == "失败"
            && TaskOps.StateLabel("canceled") == "已取消" && TaskOps.StateLabel("running") == "运行中", "");
        d("100 终态可重跑运行中不可", TaskOps.CanRetry("done") && TaskOps.CanRetry("failed") && TaskOps.CanRetry("canceled")
            && !TaskOps.CanRetry("running"), "");
        d("100 取消确认文案", TaskOps.CancelConfirmText("导出订单").Contains("导出订单") && TaskOps.CancelConfirmText("导出订单").Contains("已取消"), "");
        d("100 落账行格式", TaskOps.CancelLog("t1") == "[任务中心] cancel id=t1"
            && TaskOps.RetryLog("t2", "t1") == "[任务中心] retry id=t2 from=t1"
            && TaskOps.CancelDenyLog("t3") == "[任务中心] cancel-denied id=t3", "");
    }

    // 发现136：聚合缓冲——同格去重、按行聚合 UPDATE、before/after 镜像、移除与放弃
    static void RunGridBatchEdit(CheckD d)
    {
        var buf = new GridBatchEditBuffer();
        buf.Add(0, 1, "memo", "a", "b", "1");
        buf.Add(0, 1, "memo", "a", "c", "1");   // 同格重复只留最新
        buf.Add(0, 2, "note", "x", "y", "1");   // 同行第二列
        buf.Add(1, 1, "memo", "p", "q", "2");
        d("136 同格去重", buf.Count == 3, $"count={buf.Count}");
        var diffs = buf.DiffLines();
        d("136 差异摘要行", diffs.Count == 3 && diffs[0].OldValue == "a" && diffs[0].NewValue == "c", "");
        var cols = new List<ColumnSpec>
        {
            new() { Name = "id", DataType = "INT", IsKey = true },
            new() { Name = "memo", DataType = "VARCHAR(50)" },
            new() { Name = "note", DataType = "VARCHAR(50)" },
        };
        var plan = buf.Plan("orders", "id", ident => $"[{ident}]", (spec, v) => $"'{v}'", cols);
        d("136 按行聚合成两条UPDATE", plan.Count == 2, $"plan={plan.Count}");
        d("136 同行多列合并一条", plan[0].Sql.Contains("[memo] = 'c'") && plan[0].Sql.Contains("[note] = 'y'")
            && plan[0].Sql.StartsWith("UPDATE [orders] SET") && plan[0].Sql.EndsWith("WHERE [id] = '1'"), plan[0].Sql);
        d("136 before/after镜像", plan[0].Before["memo"] == "a" && plan[0].After["memo"] == "c"
            && plan[0].Before["note"] == "x" && plan[0].After["note"] == "y", "");
        d("136 主键行键值", plan[0].KeyValue == "1" && plan[1].KeyValue == "2", "");
        buf.RemoveRow(0);
        d("136 移除行保留其余", buf.Count == 1 && buf.Edits[0].KeyDisplay == "2", "");
        buf.Discard();
        d("136 放弃整批清空", !buf.HasEdits && buf.Count == 0, "");
    }

    // 发现137：悬停词提取、整词高亮、元数据卡行
    static void RunEditorHover(CheckD d)
    {
        d("137 悬停300ms口径", EditorHoverOps.HoverDelayMs == 300, "");
        const string sql = "SELECT order_id, order_amt FROM orders WHERE order_id > 10";
        var (s, len) = EditorHoverOps.WordAt(sql, 10);   // 光标在 order_id 内
        d("137 光标处词提取", s == 7 && len == 8 && sql.Substring(s, len) == "order_id", $"s={s} len={len}");
        var (s2, l2) = EditorHoverOps.WordAt(sql, 7);    // 词首也取整词
        d("137 词首取整词", s2 == 7 && l2 == 8, "");
        var (s3, l3) = EditorHoverOps.WordAt("", 0);
        d("137 空文本不取词", s3 == 0 && l3 == 0, "");
        var hits = EditorHoverOps.HighlightUses(sql, "order_id");
        d("137 整词高亮两处", hits.Count == 2, $"hits={hits.Count}");
        var hits2 = EditorHoverOps.HighlightUses("SELECT order_id FROM t WHERE note = 'order_id2'", "order_id");
        d("137 不命中子串", hits2.Count == 1, $"hits={hits2.Count}");
        var card = EditorHoverOps.CardLines("表", "orders", null, "id", "订单");
        d("137 元数据卡缺落暂不可用", card.First(l => l.Label == "行数估算").Value == "暂不可用"
            && card.First(l => l.Label == "主键").Value == "id"
            && card.First(l => l.Label == "注释").Value == "订单", "");
    }

    // 发现139：外键/同名候选；三步出 SQL（别名 a,b,c…；无关联退 CROSS JOIN）
    static void RunQueryBuilder(CheckD d)
    {
        var c1 = new List<ColumnSpec> { new() { Name = "id", DataType = "INT", IsKey = true }, new() { Name = "shop_id", DataType = "INT" } };
        var c2 = new List<ColumnSpec> { new() { Name = "id", DataType = "INT", IsKey = true }, new() { Name = "name", DataType = "VARCHAR(50)" } };
        var fks = new List<(string, string, string, string)> { ("orders", "shop_id", "shops", "id") };
        var cands = QueryBuilderCore.Candidates("orders", c1, "shops", c2, fks);
        d("139 外键候选标推荐", cands.Count == 1 && cands[0] is { Recommended: true, Source: "外键" }
            && cands[0].LeftColumn == "shop_id" && cands[0].RightColumn == "id", "");
        var cands2 = QueryBuilderCore.Candidates("orders", c1, "shops", c2, Array.Empty<(string, string, string, string)>());
        d("139 无外键退同名候选", cands2.Count == 0, $"count={cands2.Count}");   // 同名 id 是主键，排除
        var c3 = new List<ColumnSpec> { new() { Name = "id", DataType = "INT", IsKey = true }, new() { Name = "shop_id", DataType = "INT" } };
        var cands3 = QueryBuilderCore.Candidates("orders", c1, "logs", c3, Array.Empty<(string, string, string, string)>());
        d("139 同名非主键作候选", cands3.Count == 1 && cands3[0] is { Recommended: false, Source: "同名" }, "");
        string sql = QueryBuilderCore.BuildSql(new List<string> { "orders", "shops" }, cands, ident => $"[{ident}]");
        d("139 生成JOIN SQL别名ab", sql.StartsWith("SELECT a.*, b.*") && sql.Contains("FROM [orders] a") && sql.Contains("JOIN [shops] b ON a.shop_id = b.id"), sql);
        string sql2 = QueryBuilderCore.BuildSql(new List<string> { "orders", "shops" }, new List<JoinCandidate>(), ident => $"[{ident}]");
        d("139 无关联退CROSS JOIN", sql2.Contains("CROSS JOIN [shops] b"), sql2);
        d("139 空表清单不出SQL", QueryBuilderCore.BuildSql(new List<string>(), new List<JoinCandidate>(), ident => ident) == "", "");
    }
}
