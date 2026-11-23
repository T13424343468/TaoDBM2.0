using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>批次三格3：命令监控——两栏：「本机历史」（QueryHistory 过滤：库/关键字，可打开到查询窗口）
/// 与「库端实时」（轮询方言视图只读展示：PROCESSLIST / pg_stat_activity / sys.dm_exec_requests）。
/// 打开到查询窗口复用 OpenQueryWithSql（格14 既有出口）。</summary>
public static class CommandMonitorDialog
{
    /// <summary>库端实时会话 SQL（与 ServerMonitor 会话清单同源，只读展示不提供终止）。</summary>
    public static string RealtimeSql(DbEngine e) => e switch
    {
        DbEngine.MySQL => "SELECT Id, User, Host, db, Command, Time, LEFT(COALESCE(Info,''),120) AS query FROM information_schema.PROCESSLIST WHERE Id <> CONNECTION_ID()",
        DbEngine.PostgreSQL => "SELECT pid, usename, state, now()-query_start AS duration, left(query,120) AS query FROM pg_stat_activity WHERE pid <> pg_backend_pid()",
        DbEngine.SqlServer => "SELECT session_id, login_name, status, LEFT(COALESCE(text,''),120) AS query FROM sys.dm_exec_sessions s LEFT JOIN sys.dm_exec_requests r ON s.session_id=r.session_id OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) WHERE s.is_user_process=1",
        DbEngine.Oracle => "SELECT sid || ',' || serial# AS session, username, status, seconds_in_wait FROM V$SESSION WHERE username IS NOT NULL",
        _ => "SELECT '该引擎无库端会话视图' AS note WHERE 0",
    };

    public static void Show(MainWindow owner, NodeInfo info)
    {
        var provider = info.Provider!;
        var kw = new TextBox { Header = "关键字过滤（本机历史）", PlaceholderText = "语句包含…" };
        var hist = new ListView { MaxHeight = 220 };
        var realtime = new ListView { MaxHeight = 220 };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub };

        void FillHistory()
        {
            hist.Items.Clear();
            foreach (var h in QueryHistory.Items)
            {
                if (kw.Text.Trim().Length > 0 && !h.Sql.Contains(kw.Text.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                var item = new ListViewItem { Content = $"[{h.Engine}/{h.Database}] {h.Sql.Replace("\n", " ")[..Math.Min(100, h.Sql.Length)]}" };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "CmdMonitor_Hist");
                item.DoubleTapped += (_, _) => owner.OpenQueryWithSql(h.Sql);   // 打开到查询窗口（格14 出口）
                hist.Items.Add(item);
            }
            status.Text = $"本机历史 {hist.Items.Count} 条 · 双击打开到查询窗口。";
        }
        kw.TextChanged += (_, _) => FillHistory();

        async Task FillRealtime()
        {
            realtime.Items.Clear();
            try
            {
                var rs = await provider.Execute(info.Db, RealtimeSql(provider.Profile.Engine));
                var r = rs.FirstOrDefault();
                if (r is null || r.Rows.Count == 0) { status.Text += " · 库端当前无活动会话。"; return; }
                foreach (var row in r.Rows.Take(50))
                    realtime.Items.Add(new ListViewItem { Content = string.Join(" | ", r.Columns.Zip(row, (c, v) => $"{c}={ResultGridOps.CellText(v)}").Take(4)) });
                status.Text += $" · 库端实时 {r.Rows.Count} 会话。";
                AppLog.Write($"[命令监控] 历史={hist.Items.Count} 实时={r.Rows.Count}");
            }
            catch (Exception ex) { status.Text += " · 库端实时查询失败：" + ex.Message; }
        }

        var dlg = new ContentDialog
        {
            Title = $"命令监控 · {info.Name}",
            Content = new StackPanel { Spacing = 10, MinWidth = 620, Children = {
                kw,
                new TextBlock { Text = "本机历史（双击打开到查询窗口）", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub }, hist,
                new TextBlock { Text = "库端实时（打开时快照）", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub }, realtime,
                status } },
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        dlg.Opened += (_, _) => { FillHistory(); _ = FillRealtime(); };
        _ = dlg.ShowThemedAsync();
    }
}
