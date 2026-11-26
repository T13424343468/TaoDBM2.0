namespace TaoDBM.Core;

/// <summary>
/// 界面批 B 纯逻辑锚（2026-11-27，发现144/147/149）：文案与口径下沉 Core，测试锚 UiBatchBCheck 逐条对账；
/// 界面层（ErdDialog / DiagnosticsDialog / MainWindow 备份树）按本口径落码。
/// </summary>
public static class UiBatchB
{
    // ---------- 发现149：诊断器进程页自动刷新 ----------

    /// <summary>进程页自动刷新周期（秒）：设计稿口径 10 秒一轮，对话框关闭即停。</summary>
    public const int AutoRefreshSeconds = 10;

    /// <summary>变量 / 状态页不支持引擎的人话（SQLite / MongoDB / Redis）：不报错不空白。</summary>
    public static string KeyValueUnsupported(DbEngine engine) => engine switch
    {
        DbEngine.SQLite => "SQLite 是单文件库，没有服务器变量 / 状态可查；文件级信息可在「一键诊断」体检页查看。",
        DbEngine.MongoDB => "MongoDB 没有可查询的服务器变量 / 状态，请用对应管理工具（mongosh）查看。",
        DbEngine.Redis => "Redis 没有可查询的服务器变量 / 状态，请用对应管理工具（redis-cli CONFIG GET *）查看。",
        _ => "",
    };

    // ---------- 发现147：ER 图正向同步逐条勾选 ----------

    /// <summary>逐条勾选行文案：语句类型徽标（CREATE / ALTER）＋ 表名 ＋ 摘要（超 90 字截断，全文在展开区）。</summary>
    public static string SyncItemLabel(string stmt)
    {
        string s = stmt.Trim().TrimEnd(';');
        string head = s.Length > 0 ? s : stmt;
        string upper = head.ToUpperInvariant();
        string type = upper.StartsWith("CREATE", StringComparison.Ordinal) ? "CREATE"
            : upper.StartsWith("ALTER", StringComparison.Ordinal) ? "ALTER"
            : upper.StartsWith("DROP", StringComparison.Ordinal) ? "DROP" : "SQL";
        string table = ExtractTable(head);
        string summary = head.Length > 90 ? head[..90] + "…" : head;
        return table.Length > 0 ? $"【{table}】{type} · {summary}" : $"{type} · {summary}";
    }

    /// <summary>从 DDL 里抠表名：CREATE TABLE xxx / ALTER TABLE xxx（带引号括号都剥掉）。</summary>
    public static string ExtractTable(string ddl)
    {
        var tokens = ddl.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            if (!tokens[i].Equals("TABLE", StringComparison.OrdinalIgnoreCase)) continue;
            string t = tokens[i + 1].Trim().TrimEnd('(');
            return t.Trim('[', ']', '`', '"', ' ');
        }
        return "";
    }

    // ---------- 发现144：备份树条目 ----------

    /// <summary>备份树条目文案：时间 · 档位 · 大小 · 成败，文件已不在磁盘的置灰标注（账本条目保留）。</summary>
    public static string BackupEntryLabel(BackupHistory.Entry e, bool fileExists)
    {
        string size = e.SizeBytes > 0 ? HumanSize.Of(e.SizeBytes) : "—";
        string mark = e.Ok ? "✓" : "✗";
        string gone = fileExists ? "" : "（文件已不在磁盘）";
        return $"{e.Time} · {e.Kind} · {size} · {mark}{gone}";
    }

    /// <summary>备份树空态文案（设计稿口径）。</summary>
    public const string BackupTreeEmptyText = "还没有备份记录。右键连接 → 备份 / 还原，或用备份计划定时备份。";
}
