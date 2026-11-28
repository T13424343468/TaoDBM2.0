using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>数据对账对话框：基于 ReconcileEngine 对同一连接下的两个库做行数与校验和对账。
/// 全程只读（COUNT 与校验和查询）；结论为不一致时给出抽样 SQL，打开到查询窗口由用户人工比对，不自动修复。</summary>
public static class ReconcileDialog
{
    public static FrameworkElement BuildPanel(MainWindow owner, NodeInfo info)
    {
        var profile = info.Profile;
        var prov = info.Provider ?? ProviderFactory.Create(profile);
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        List<string> dbs = new();
        try { dbs = prov.GetDatabases().GetAwaiter().GetResult(); }
        catch { dbs = new List<string>(); }
        string defDb = CurrentDb(info);

        var srcDbBox = new ComboBox { MinWidth = 150, PlaceholderText = "源库" };
        foreach (var d in dbs) srcDbBox.Items.Add(d);
        if (profile.Engine != DbEngine.SQLite && dbs.Contains(defDb)) srcDbBox.SelectedItem = defDb;
        else if (dbs.Count > 0) srcDbBox.SelectedIndex = 0;

        var srcTableBox = new TextBox { PlaceholderText = "源表名", MinWidth = 130 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(srcTableBox, "Reconcile_SrcTable");   // 评分补步306：走查置文定位
        var dstDbBox = new ComboBox { MinWidth = 150, PlaceholderText = "目标库" };
        foreach (var d in dbs) dstDbBox.Items.Add(d);
        if (profile.Engine != DbEngine.SQLite && dbs.Count > 0) dstDbBox.SelectedIndex = 0;

        var dstTableBox = new TextBox { PlaceholderText = "目标表名", MinWidth = 130 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dstTableBox, "Reconcile_DstTable");   // 评分补步306：走查置文定位
        var whereBox = new TextBox { PlaceholderText = "可选 WHERE 条件（不含 WHERE 关键字）", MinWidth = 560 };

        var output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 240,
            FontFamily = new FontFamily("Consolas"),
            FontSize = AntTokens.FontMinor,
            Text = "填好两端的库和表后点「开始对账」。",
        };
        var sampleBtn = new Button { Content = "查看源端样本", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Visibility = Visibility.Collapsed };
        string sampleSql = "";

        sampleBtn.Click += (_, _) =>
        {
            if (sampleSql.Length > 0) owner.OpenQueryTab(info, sampleSql);
        };

        var runBtn = new Button { Content = "开始对账", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };

        // 发现306原型：汇总徽标行——源/目标行数、校验和、结论四张小卡，对账完即点亮
        var summaryPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        summaryPanel.Children.Add(new TextBlock
        {
            Text = "对账汇总：填好两端库表后点「开始对账」。",
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.TextSub,
            TextWrapping = TextWrapping.Wrap,
        });

        runBtn.Click += async (_, _) =>
        {
            sampleBtn.Visibility = Visibility.Collapsed;
            sampleSql = "";
            var sample = await RunAsync(prov, S(srcDbBox.SelectedItem), srcTableBox.Text, S(dstDbBox.SelectedItem),
                dstTableBox.Text, whereBox.Text, output, status, summaryPanel);
            if (sample is not null)
            {
                sampleSql = sample;
                sampleBtn.Visibility = Visibility.Visible;
            }
        };

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row1.Children.Add(srcDbBox);
        row1.Children.Add(srcTableBox);
        row1.Children.Add(new TextBlock { Text = "⇄", VerticalAlignment = VerticalAlignment.Center, Foreground = Ant.TextSub });
        row1.Children.Add(dstDbBox);
        row1.Children.Add(dstTableBox);

        var panel = new StackPanel { Spacing = 10, MinWidth = 640 };
        panel.Children.Add(new TextBlock
        {
            Text = "对同一连接下的两个库做行数与内容校验和比对（迁移 / 同步后核对用）；只读查询，不修改任何数据。",
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.TextSub,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(row1);
        panel.Children.Add(summaryPanel);   // 发现306原型：汇总徽标行
        panel.Children.Add(whereBox);

        // 发现306原型：差异清单表头——结果区上方加底色表头条
        panel.Children.Add(new Border
        {
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusSm),
            Background = Ant.HeadBg,
            Child = new TextBlock
            {
                Text = "对账结果（行数 · 校验和 · 结论）",
                FontSize = AntTokens.FontMinor,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Ant.TextSub,
            },
        });
        panel.Children.Add(runBtn);
        panel.Children.Add(output);
        panel.Children.Add(sampleBtn);
        panel.Children.Add(status);

        return panel;
    }

    private static string CurrentDb(NodeInfo info)
    {
        if (info.Db.Length > 0) return info.Db;
        if (info.Profile.Database.Length > 0) return info.Profile.Database;
        return "";
    }

    private static string S(object? v) => v is null ? "" : v.ToString() ?? "";

    /// <summary>发现306原型：汇总小卡——数值＋标签，结论卡按通过绿 / 差异黄着色。</summary>
    private static Border StatCard(string value, string label, bool? ok)
    {
        var valColor = ok switch { true => Ant.Success, false => Ant.Warn, null => Ant.Text };
        var col = new StackPanel { Spacing = 0 };
        col.Children.Add(new TextBlock { Text = value, FontSize = AntTokens.FontEmphasis, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = valColor });
        col.Children.Add(new TextBlock { Text = label, FontSize = AntTokens.FontCaption, Foreground = Ant.TextSub });
        return new Border
        {
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg),
            BorderThickness = new Thickness(1),
            BorderBrush = Ant.Border,
            Background = (Brush?)Application.Current.Resources["AntCard"],
            Child = col,
        };
    }

    private static async Task<long> ScalarLong(IDbProvider prov, string db, string sql, string col, string caller)
    {
        var rs = await prov.Execute(db, sql);
        if (rs.Count == 0 || rs[0].Rows.Count == 0)
            throw new InvalidOperationException(caller + "未返回结果");
        int ci = 0;
        for (int i = 0; i < rs[0].Columns.Count; i++)
            if (string.Equals(rs[0].Columns[i], col, StringComparison.OrdinalIgnoreCase)) { ci = i; break; }
        var v = rs[0].Rows[0][ci];
        return v is null ? 0 : long.TryParse(v.ToString(), out var n) ? n : 0;
    }

    /// <summary>执行对账；结论不一致时返回源端抽样 SQL，否则返回 null。
    /// 发现306原型：对账完点亮汇总徽标行（源/目标行数、校验和、结论）。</summary>
    private static async Task<string?> RunAsync(IDbProvider prov, string srcDb, string srcTable, string dstDb, string dstTable,
        string where, TextBox output, TextBlock status, StackPanel summaryPanel)
    {
        srcTable = (srcTable ?? "").Trim();
        dstTable = (dstTable ?? "").Trim();
        if (srcTable.Length == 0 || dstTable.Length == 0) { status.Text = "请填写源表名和目标表名。"; return null; }
        if (srcDb.Length == 0 || dstDb.Length == 0) { status.Text = "请选择源库和目标库。"; return null; }

        status.Text = "对账中（只读查询）…";
        var engine = prov.Profile.Engine;
        List<ColumnSpec> cols;
        try
        {
            cols = await prov.GetColumns(srcDb, srcTable);
        }
        catch (Exception ex)
        {
            status.Text = HumanError.Say(ex, "读取源表列信息失败");
            return null;
        }
        if (cols.Count == 0) { status.Text = "源表没有可用列，无法对账。"; return null; }
        var colNames = cols.Select(c => c.Name).ToList();

        try
        {
            long srcCount = await ScalarLong(prov, srcDb, ReconcileEngine.CountSql(srcTable, where, engine), "row_count", "行数对账");
            long dstCount = await ScalarLong(prov, dstDb, ReconcileEngine.CountSql(dstTable, where, engine), "row_count", "行数对账");

            long? srcSum = null;
            long? dstSum = null;
            var srcCk = ReconcileEngine.ChecksumSql(srcTable, colNames, where, engine);
            var dstCk = ReconcileEngine.ChecksumSql(dstTable, colNames, where, engine);
            if (srcCk is not null && dstCk is not null)
            {
                srcSum = await ScalarLong(prov, srcDb, srcCk, "checksum", "校验和对账");
                dstSum = await ScalarLong(prov, dstDb, dstCk, "checksum", "校验和对账");
            }

            var verdict = ReconcileEngine.Compare(srcCount, dstCount, srcSum, dstSum);

            // 发现306原型：汇总徽标行——行数、校验和、结论三张小卡
            summaryPanel.Children.Clear();
            summaryPanel.Children.Add(StatCard(srcCount.ToString("N0"), "源端行数", null));
            summaryPanel.Children.Add(StatCard(dstCount.ToString("N0"), "目标端行数", null));
            if (srcSum.HasValue || dstSum.HasValue)
                summaryPanel.Children.Add(StatCard(srcSum == dstSum ? "一致" : "不一致", "校验和比对", srcSum == dstSum));
            summaryPanel.Children.Add(StatCard(verdict.Level == "match" ? "✓ 通过" : "⚠ 有差异", "对账结论", verdict.Level == "match"));

            var sb = new StringBuilder();
            sb.AppendLine($"源 {srcDb}.{srcTable}：{srcCount} 行" + (srcSum.HasValue ? $"，校验和 {srcSum}" : ""));
            sb.AppendLine($"目标 {dstDb}.{dstTable}：{dstCount} 行" + (dstSum.HasValue ? $"，校验和 {dstSum}" : ""));
            sb.AppendLine();
            sb.AppendLine((verdict.Level == "match" ? "✓ " : "⚠ ") + verdict.Message);
            output.Text = sb.ToString();

            if (verdict.Level != "match")
            {
                var pk = cols.FirstOrDefault(c => c.IsKey)?.Name ?? colNames[0];
                status.Text = "对账发现差异，可打开源端样本人工比对。";
                return ReconcileEngine.SampleSql(srcTable, pk, 50, engine);
            }
            status.Text = "对账通过。";
            return null;
        }
        catch (Exception ex)
        {
            status.Text = HumanError.Say(ex, "对账失败");
            return null;
        }
    }
}
