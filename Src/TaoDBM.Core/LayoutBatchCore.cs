using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>布局批纯逻辑（2026-11-26，发现132/133/134/135）：顶部功能域导航、对象联动操作栏、
/// 详情侧栏字段模型、欢迎页最近/收藏与新手条偏好。只做数据与文案口径，不碰 UI 控件；
/// 界面层（MainWindow 布局批分部）按本口径落码，测试锚 LayoutBatchCheck 逐条对账。</summary>

// 发现132：顶部功能域直达——高频域清单与当前域判定口径（当前域 3px 品牌色条由 UI 层按 ActiveKey 渲染）。
public sealed record NavItem(string Key, string Label, string Tooltip);

public static class DomainNav
{
    public static List<NavItem> Items { get; } = new()
    {
        new NavItem("tables", "表", "对象树 · 表"),
        new NavItem("query", "查询", "查询编辑器"),
        new NavItem("backup", "备份", "备份与还原"),
        new NavItem("taskcenter", "任务中心", "任务与计划"),
        new NavItem("erd", "ER 图", "模型与关系"),
    };

    public static NavItem? Item(string key) => Items.FirstOrDefault(i => i.Key == key);

    /// <summary>当前域判定：按最近打开的工作页签模式/入口归域；无工作页签归「表」（对象树是默认域）。</summary>
    public static string ActiveKey(string? lastTabMode, bool backupOpen, bool taskCenterOpen, bool erdOpen)
    {
        if (taskCenterOpen) return "taskcenter";
        if (erdOpen) return "erd";
        if (backupOpen) return "backup";
        if (lastTabMode == "Query") return "query";
        if (lastTabMode == "TableData") return "tables";
        return "tables";
    }
}

// 发现133：对象联动操作栏——按选中对象类型出操作清单；权限不足置灰并带原因；AI 建表虚线占位随 AI 版批启用。
public sealed record ActionItem(string Key, string Label, bool Enabled = true, string Reason = "", bool Primary = false, bool AiPlaceholder = false);

public static class ObjectActionBarModel
{
    /// <summary>kind：table / database / connection / none（无选中）。canWrite/canDelete 由调用方按权限审计口径传入。</summary>
    public static (string Context, List<ActionItem> Actions) Build(string kind, bool canWrite = true, bool canDelete = true)
    {
        switch (kind)
        {
            case "table":
                return ("表", new List<ActionItem>
                {
                    new ActionItem("open", "打开", Primary: true),
                    new ActionItem("design", "设计", Enabled: canWrite, Reason: canWrite ? "" : "当前账号无结构变更权限"),
                    new ActionItem("newtable", "新建表", Enabled: canWrite, Reason: canWrite ? "" : "当前账号无建表权限"),
                    new ActionItem("ai", "✦ AI 建表（预留）", Enabled: false, Reason: "随 AI 版批启用", AiPlaceholder: true),
                    new ActionItem("import", "导入", Enabled: canWrite, Reason: canWrite ? "" : "当前账号无写入权限"),
                    new ActionItem("export", "导出"),
                    new ActionItem("delete", "删除", Enabled: canDelete, Reason: canDelete ? "" : "当前账号无删除权限"),
                });
            case "database":
                return ("库", new List<ActionItem>
                {
                    new ActionItem("newtable", "新建表", Enabled: canWrite, Reason: canWrite ? "" : "当前账号无建表权限", Primary: true),
                    new ActionItem("query", "新建查询"),
                    new ActionItem("backup", "备份"),
                    new ActionItem("restore", "还原", Enabled: canWrite, Reason: canWrite ? "" : "当前账号无还原权限"),
                });
            case "connection":
                return ("连接", new List<ActionItem>
                {
                    new ActionItem("query", "新建查询", Primary: true),
                    new ActionItem("backup", "备份"),
                    new ActionItem("taskcenter", "任务中心"),
                });
            default:
                return ("", new List<ActionItem>
                {
                    new ActionItem("newconn", "新建连接", Primary: true),
                    new ActionItem("query", "新建查询"),
                });
        }
    }

    /// <summary>无选中时的状态一句话（原型口径：不留空白）。</summary>
    public static string IdleHint(int connCount) =>
        connCount == 0 ? "还没有连接：点「新建连接」开始。" : $"共 {connCount} 个连接：选中连接 / 库 / 表后，这里给出联动操作。";
}

// 发现134：详情侧栏字段模型——引擎徽标文案＋键值行；任一元数据取不到时该行显「暂不可用」，不弹错误窗。
public sealed record DetailField(string Label, string Value, bool Ok = true);

public static class DetailPanelModel
{
    public static string UnavailableText => "暂不可用";

    /// <summary>表/集合详情：行数估算、数据大小、字符集、注释、设置文件（SQLite）；取不到传 null 落「暂不可用」。</summary>
    public static List<DetailField> TableFields(string name, string engine, string? rows, string? size, string? charset, string? comment, string? setting)
    {
        var list = new List<DetailField>
        {
            new DetailField("对象", name),
            new DetailField("引擎", engine),
            new DetailField("行数估算", string.IsNullOrWhiteSpace(rows) ? UnavailableText : rows, !string.IsNullOrWhiteSpace(rows)),
            new DetailField("数据大小", string.IsNullOrWhiteSpace(size) ? UnavailableText : size, !string.IsNullOrWhiteSpace(size)),
            new DetailField("字符集", string.IsNullOrWhiteSpace(charset) ? UnavailableText : charset, !string.IsNullOrWhiteSpace(charset)),
            new DetailField("注释", string.IsNullOrWhiteSpace(comment) ? UnavailableText : comment, !string.IsNullOrWhiteSpace(comment)),
        };
        if (!string.IsNullOrWhiteSpace(setting)) list.Add(new DetailField("设置文件", setting));
        return list;
    }

    /// <summary>库详情：子对象计数、字符集、注释。</summary>
    public static List<DetailField> DatabaseFields(string name, string engine, string? objects, string? charset, string? comment)
    {
        var list = new List<DetailField>
        {
            new DetailField("对象", name),
            new DetailField("引擎", engine),
            new DetailField("子对象", string.IsNullOrWhiteSpace(objects) ? UnavailableText : objects, !string.IsNullOrWhiteSpace(objects)),
            new DetailField("字符集", string.IsNullOrWhiteSpace(charset) ? UnavailableText : charset, !string.IsNullOrWhiteSpace(charset)),
            new DetailField("注释", string.IsNullOrWhiteSpace(comment) ? UnavailableText : comment, !string.IsNullOrWhiteSpace(comment)),
        };
        return list;
    }

    /// <summary>连接详情：引擎、主机、端口、状态。</summary>
    public static List<DetailField> ConnectionFields(string name, string engine, string host, int port, string state)
    {
        return new List<DetailField>
        {
            new DetailField("对象", name),
            new DetailField("引擎", engine),
            new DetailField("主机", host.Length > 0 ? host : UnavailableText, host.Length > 0),
            new DetailField("端口", port > 0 ? port.ToString() : UnavailableText, port > 0),
            new DetailField("状态", state.Length > 0 ? state : UnavailableText, state.Length > 0),
        };
    }

    /// <summary>列摘要行：PK 徽标＋列名＋类型（UI 层渲染）。</summary>
    public static List<(string Name, string Type, bool Pk)> ColumnSummary(IEnumerable<ColumnSpec> cols) =>
        cols.Select(c => (c.Name, c.DataType, c.IsKey)).ToList();
}

// 发现135：欢迎页——最近打开（相对时间）＋收藏连接（星标优先）＋新手条可关闭（偏好持久化）。
public static class WelcomeBoard
{
    /// <summary>相对时间口径：1 分钟内「刚刚」；1 小时内「N 分钟前」；今天内「N 小时前」；昨天带时刻；更早 MM-dd。</summary>
    public static string RelativeTime(DateTime t, DateTime now)
    {
        var span = now - t;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
        if (t.Date == now.Date) return $"{(int)span.TotalHours} 小时前";
        if (t.Date == now.Date.AddDays(-1)) return $"昨天 {t:HH:mm}";
        return t.ToString("MM-dd");
    }

    /// <summary>收藏优先（保持原相对顺序）；星标连接排前面。</summary>
    public static List<ConnectionProfile> StarredFirst(IEnumerable<ConnectionProfile> profiles) =>
        profiles.OrderByDescending(p => p.Starred ? 1 : 0).ToList();

    /// <summary>最近打开条目文案：连接 / 库 / 表与模式一句话。</summary>
    public static string RecentLabel(string mode, string db, string child)
    {
        string where = child.Length > 0 ? $"{db} / {child}" : (db.Length > 0 ? db : "连接");
        return mode switch
        {
            "Query" => $"查询页签 · {where}",
            "TableData" => $"数据浏览 · {where}",
            _ => where,
        };
    }
}

/// <summary>新手条偏好：关闭后不再显示（JSON 落盘，损坏回落显示）。</summary>
public static class WelcomePrefs
{
    private static string _file = "";
    private static string File => _file.Length > 0 ? _file : TaoPaths.FilePath("welcome.json");

    /// <summary>测试或自定义存储位置。</summary>
    public static void UseFile(string path) => _file = path;

    public static bool ShowGuide
    {
        get
        {
            try
            {
                if (!System.IO.File.Exists(File)) return true;
                var v = JsonSerializer.Deserialize<PrefsFile>(System.IO.File.ReadAllText(File));
                return v?.GuideDismissed != true;
            }
            catch { return true; }
        }
    }

    public static void DismissGuide()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File)!);
            AtomicWrite.Text(File, JsonSerializer.Serialize(new PrefsFile { GuideDismissed = true }));
        }
        catch { /* 偏好写失败不影响欢迎页 */ }
    }

    private sealed class PrefsFile { public bool GuideDismissed { get; set; } }
}
