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
    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        var profile = info.Profile;
        var prov = info.Provider ?? ProviderFactory.Create(profile);
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        List<string> dbs = new();
        try { dbs = await prov.GetDatabases(); }
        catch { dbs = new List<string>(); }
        string defDb = CurrentDb(info);

        var srcDbBox = new ComboBox { MinWidth = 150, PlaceholderText = "源库" };
        foreach (var d in dbs) srcDbBox.Items.Add(d);
        if (profile.Engine != DbEngine.SQLite && dbs.Contains(defDb)) srcDbBox.SelectedItem = defDb;
        else if (dbs.Count > 0) srcDbBox.SelectedIndex = 0;

        var srcTableBox = new TextBox { PlaceholderText = "源表名", MinWidth = 130 };
        var dstDbBox = new ComboBox { MinWidth = 150, PlaceholderText = "目标库" };
        foreach (var d in dbs) dstDbBox.Items.Add(d);
        if (profile.Engine != DbEngine.SQLite && dbs.Count > 0) dstDbBox.SelectedIndex = 0;

        var dstTableBox = new TextBox { PlaceholderText = "目标表名", MinWidth = 130 };
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
        runBtn.Click += async (_, _) =>
        {
            sampleBtn.Visibility = Visibility.Collapsed;
            sampleSql = "";
            var sample = await RunAsync(prov, S(srcDbBox.SelectedItem), srcTableBox.Text, S(dstDbBox.SelectedItem),
                dstTableBox.Text, whereBox.Text, output, status);
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
        panel.Children.Add(whereBox);
        panel.Children.Add(runBtn);
        panel.Children.Add(output);
        panel.Children.Add(sampleBtn);
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "数据对账",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        await dlg.ShowThemedAsync();
    }

    private static string CurrentDb(NodeInfo info)
    {
        if (info.Db.Length > 0) return info.Db;
        if (info.Profile.Database.Length > 0) return info.Profile.Database;
        return "";
    }

    private static string S(object? v) => v is null ? "" : v.ToString() ?? "";

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

    /// <summary>执行对账；结论不一致时返回源端抽样 SQL，否则返回 null。</summary>
    private static async Task<string?> RunAsync(IDbProvider prov, string srcDb, string srcTable, string dstDb, string dstTable,
        string where, TextBox output, TextBlock status)
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
