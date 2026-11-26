using TaoDBM.Core;

namespace TaoDBM.LogicTest;

/// <summary>界面批 A 测试锚（2026-11-27，发现141/142/148/151）：纯逻辑可测项逐条落断言；
/// 界面层条目（导入四步向导步骤守门/任务中心组合按钮/同步配置行/导出配置对话框）本地沙箱编不了
/// App XAML，归云测首验，此处锚定其下沉 Core 的口径。独立文件接线（ComboMatrix 家法）。</summary>
public static class UiBatchACheck
{
    public static void RunAll(Action<string, bool, string> check)
    {
        Console.WriteLine("\n== 界面批 A 纯逻辑用例（2026-11-27）==");
        RunSmartMatch(check);        // 发现141：智慧匹配
        RunSafeName(check);          // 发现141：配置名收敛
        RunImportProfilesRoundtrip(check);   // 发现141：配置存取往返
        RunTaskCombos(check);        // 发现142：组合规整/命令行解析
        RunTaskCombosRoundtrip(check);       // 发现142：组合存取往返
        RunSyncProfiles(check);      // 发现148：配置校验/存取往返
        RunExportProfiles(check);    // 发现151：最近目录记忆/默认格式
        RunUiPrefs(check);           // 发现151：键值偏好
    }

    // 发现141：SmartMatch——精确（忽略大小写/去空格）优先，别名组兜底，每表列先到先得，无匹配留空
    static void RunSmartMatch(Action<string, bool, string> check)
    {
        var tableCols = new List<string> { "ID", "Name", "Price", "Qty", "Status", "Memo" };
        var m1 = ImportProfiles.SmartMatch(
            new List<string> { " id ", "NAME", "单价", "数量", "状态", "备注" }, tableCols);
        check("141 智慧匹配-精确+别名", m1[0] == "ID" && m1[1] == "Name" && m1[2] == "Price"
            && m1[3] == "Qty" && m1[4] == "Status" && m1[5] == "Memo", string.Join(",", m1));
        var m2 = ImportProfiles.SmartMatch(new List<string> { "创建时间", "说明" }, new List<string> { "Time", "Memo" });
        check("141 智慧匹配-时间/说明别名", m2[0] == "Time" && m2[1] == "Memo", string.Join(",", m2));
        var m3 = ImportProfiles.SmartMatch(new List<string> { "乱写的列" }, new List<string> { "ID" });
        check("141 智慧匹配-无匹配留空", m3[0] is null, string.Join(",", m3));
        var m4 = ImportProfiles.SmartMatch(new List<string> { "编号", "序号" }, new List<string> { "ID", "No" });
        check("141 智慧匹配-先到先得", m4[0] == "ID" && m4[1] == "No", string.Join(",", m4));
    }

    // 发现141：SafeName——文件名安全收敛（去路径分隔/控制字符），空名回落
    static void RunSafeName(Action<string, bool, string> check)
    {
        var s1 = ImportProfiles.SafeName("订单表/日导:v2");
        check("141 SafeName-去危险字符", s1.Length > 0 && !s1.Contains('/') && !s1.Contains(':'), s1);
        check("141 SafeName-空名回落", ImportProfiles.SafeName("   ").Length > 0, ImportProfiles.SafeName("   "));
    }

    // 发现141：ImportProfiles 存取往返（唯一名＋finally 清理）
    static void RunImportProfilesRoundtrip(Action<string, bool, string> check)
    {
        var name = "测试导入配置-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            ImportProfiles.Save(new ImportProfiles.ImportProfile(name,
                new List<string> { "id", "名称" },
                new List<string?> { "ID", "Name" }, true, false));
            var p = ImportProfiles.Load(name);
            check("141 导入配置-往返", p is not null && p.Name == name && p.FirstRowHeader && !p.SkipErrors
                && p.CsvHeaders.Count == 2 && p.TargetColumns[0] == "ID" && p.TargetColumns[1] == "Name",
                p?.ToString() ?? "null");
            check("141 导入配置-列表可见", ImportProfiles.List().Contains(name), string.Join(",", ImportProfiles.List()));
        }
        finally { ImportProfiles.Delete(name); }
        check("141 导入配置-删除生效", ImportProfiles.Load(name) is null, name);
    }

    // 发现142：Normalize 剔空/重名后缀；ParseRunTaskArgs 多 id/缺值警告/去重
    static void RunTaskCombos(Action<string, bool, string> check)
    {
        var norm = TaskCombos.Normalize(new List<TaskCombos.ComboStep>
        {
            new("步骤A", "db1", "SELECT 1"),
            new("", "db1", "SELECT 2"),          // 空标题剔除
            new("步骤B", "db1", ""),             // 空 SQL 剔除
            new("步骤A", "db1", "SELECT 3"),     // 重名加后缀
        });
        check("142 组合规整-剔空+重名后缀", norm.Count == 2 && norm[1].Title == "步骤A·2",
            string.Join("|", norm.Select(s => s.Title)));
        var (ids, warns) = TaskCombos.ParseRunTaskArgs(new[] { "-run-task", "abc", "--other", "x", "-run-task", "abc", "-RUN-TASK", "def", "-run-task" });
        check("142 参数解析-多id去重保序", ids.Count == 2 && ids[0] == "abc" && ids[1] == "def", string.Join(",", ids));
        check("142 参数解析-缺值警告", warns.Count == 1, string.Join(",", warns));
        var (ids2, warns2) = TaskCombos.ParseRunTaskArgs(new[] { "没有参数" });
        check("142 参数解析-无参数空表", ids2.Count == 0 && warns2.Count == 0, "");
    }

    // 发现142：TaskCombos 存取往返
    static void RunTaskCombosRoundtrip(Action<string, bool, string> check)
    {
        var name = "测试组合-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            TaskCombos.Save(new TaskCombos.TaskCombo(name, new List<TaskCombos.ComboStep>
            {
                new("第一步", "db1", "SELECT 1"),
                new("第二步", "db2", "SELECT 2"),
            }));
            var c = TaskCombos.Load(name);
            check("142 组合-往返", c is not null && c.Name == name && c.Steps.Count == 2
                && c.Steps[0].Db == "db1" && c.Steps[1].Sql == "SELECT 2", c?.ToString() ?? "null");
            check("142 组合-列表可见", TaskCombos.List().Contains(name), string.Join(",", TaskCombos.List()));
        }
        finally { TaskCombos.Delete(name); }
        check("142 组合-删除生效", TaskCombos.Load(name) is null, name);
    }

    // 发现148：SyncProfiles.Validate 三分支＋存取往返
    static void RunSyncProfiles(Action<string, bool, string> check)
    {
        var ok = new SyncProfiles.SyncProfile("合法", "A", "db1", "B", "db2",
            new List<string> { "t1" }, true, false, true, false);
        check("148 校验-合法通过", SyncProfiles.Validate(ok) == "", SyncProfiles.Validate(ok));
        check("148 校验-空名拒绝", SyncProfiles.Validate(ok with { Name = " " }).Contains("名字"), "");
        check("148 校验-空表拒绝", SyncProfiles.Validate(ok with { Tables = new List<string>() }).Contains("表"), "");
        check("148 校验-自传自拒绝", SyncProfiles.Validate(ok with { TargetConnection = "A", TargetDb = "db1" }).Contains("同一个库"), "");
        var name = "测试同步配置-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            SyncProfiles.Save(new SyncProfiles.SyncProfile(name, "源连接", "sdb", "目标连接", "tdb",
                new List<string> { "t1", "t2" }, true, true, false, true));
            var p = SyncProfiles.Load(name);
            check("148 同步配置-往返", p is not null && p.SourceConnection == "源连接" && p.TargetDb == "tdb"
                && p.Tables.Count == 2 && p.SyncStruct && p.AlignTypes && !p.SendData && p.TruncateMode,
                p?.ToString() ?? "null");
            check("148 同步配置-列表可见", SyncProfiles.List().Contains(name), string.Join(",", SyncProfiles.List()));
        }
        finally { SyncProfiles.Delete(name); }
        check("148 同步配置-删除生效", SyncProfiles.Load(name) is null, name);
    }

    // 发现151：RememberLastDir/LastDirOf 收敛（目录在→记住；目录消失→回落空串）；DefaultFormat 唯一
    static void RunExportProfiles(Action<string, bool, string> check)
    {
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "taodbm-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            System.IO.Directory.CreateDirectory(tmp);
            ExportProfiles.RememberLastDir("CSV", System.IO.Path.Combine(tmp, "out.csv"));
            check("151 最近目录-记住", ExportProfiles.LastDirOf("csv") == tmp, ExportProfiles.LastDirOf("csv"));
            ExportProfiles.RememberLastDir("csv", System.IO.Path.Combine(tmp, "gone", "out.csv"));
            check("151 最近目录-死路径回落空", ExportProfiles.LastDirOf("csv") == "", ExportProfiles.LastDirOf("csv"));
            var n1 = "测试导出配置-" + Guid.NewGuid().ToString("N")[..8];
            var n2 = "测试导出配置-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                ExportProfiles.Save(new ExportProfiles.ExportProfile(n1, "json", "", "all", true));
                ExportProfiles.Save(new ExportProfiles.ExportProfile(n2, "html", "", "", true));
                check("151 默认格式-唯一", ExportProfiles.DefaultFormat() == "html", ExportProfiles.DefaultFormat());
                var saved = ExportProfiles.LoadAll().FirstOrDefault(p => p.Name == n2);
                check("151 导出配置-往返", saved is not null && saved.Format == "html" && saved.IsDefault, saved?.ToString() ?? "null");
            }
            finally { ExportProfiles.Delete(n1); ExportProfiles.Delete(n2); }
        }
        finally { if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true); }
    }

    // 发现151：UiPrefs Set/Get/Invalidate
    static void RunUiPrefs(Action<string, bool, string> check)
    {
        var key = "test-uipref-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            UiPrefs.Set(key, "值1");
            check("151 偏好-写读", UiPrefs.Get(key) == "值1", UiPrefs.Get(key));
            UiPrefs.Set(key, "值2");
            check("151 偏好-覆盖", UiPrefs.Get(key) == "值2", UiPrefs.Get(key));
            UiPrefs.Invalidate();
            check("151 偏好-失效后读回磁盘", UiPrefs.Get(key) == "值2", UiPrefs.Get(key));
        }
        finally { UiPrefs.Set(key, ""); }
        check("151 偏好-缺键空串", UiPrefs.Get("不存在的键-" + key) == "", "");
    }
}
