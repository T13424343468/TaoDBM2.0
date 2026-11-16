using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>数据脱敏对话框：按列名推断敏感档位，对查询结果打码预览。
/// 只读预览，不修改库中数据；正式导出走同一套 MaskCell/MaskRow 规则。</summary>
public static class MaskPreviewDialog
{
    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        var profile = info.Profile;
        var prov = info.Provider ?? ProviderFactory.Create(profile);
        var status = new TextBlock { FontSize = 12, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        var tableBox = new TextBox { PlaceholderText = "表名", MinWidth = 220, Text = info.Kind == NodeKind.Table ? info.Name : "" };
        var limitBox = new TextBox { PlaceholderText = "行数", Text = "20", MinWidth = 80 };

        var runBtn = new Button { Content = "预览脱敏效果", CornerRadius = new CornerRadius(4) };
        var output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 300,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Text = "填好表名后点「预览脱敏效果」。",
        };

        runBtn.Click += async (_, _) => await RunAsync(prov, CurrentDb(info), tableBox.Text, limitBox.Text, output, status);

        panel.Children.Add(new TextBlock
        {
            Text = "按列名推断敏感档位（手机 / 邮箱 / 证件号 / 姓名 / 银行卡 / 密码类）并打码预览，只读少量行，不改库中数据；正式导出走同一套规则。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ant.TextSub,
            FontSize = 12,
        });
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row1.Children.Add(tableBox);
        row1.Children.Add(limitBox);
        row1.Children.Add(runBtn);
        panel.Children.Add(row1);
        panel.Children.Add(output);
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "数据脱敏",
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

    private static string RuleName(MaskRule rule) => rule switch
    {
        MaskRule.Phone => "手机/电话",
        MaskRule.Email => "邮箱",
        MaskRule.IdCard => "证件号",
        MaskRule.Name => "姓名",
        MaskRule.BankCard => "银行卡",
        MaskRule.Secret => "密码/密钥类",
        _ => "不脱敏",
    };

    private static async Task RunAsync(IDbProvider prov, string db, string table, string limitText, TextBox output, TextBlock status)
    {
        table = (table ?? "").Trim();
        if (table.Length == 0) { status.Text = "请先填写表名。"; return; }
        int limit = int.TryParse(limitText?.Trim(), out var n) ? Math.Min(Math.Max(n, 1), 100) : 20;

        status.Text = "读取数据并脱敏中…";
        List<ColumnSpec> cols;
        try
        {
            cols = await prov.GetColumns(db, table);
        }
        catch (Exception ex)
        {
            status.Text = "读取列信息失败：" + ex.Message;
            return;
        }

        QueryResult rs;
        try
        {
            if (prov.SupportsBrowse)
            {
                rs = await prov.Browse(db, table, 0, limit);
            }
            else
            {
                var list = await prov.Execute(db, $"SELECT * FROM {prov.Quote(table)} LIMIT {limit}");
                rs = list.Count > 0 ? list[0] : new QueryResult();
            }
        }
        catch (Exception ex)
        {
            status.Text = "读取数据失败：" + ex.Message;
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("识别到的脱敏规则：");
        bool hasRule = false;
        foreach (var c in cols)
        {
            var rule = MaskEngine.InferRule(c.Name);
            if (rule != MaskRule.None)
            {
                hasRule = true;
                sb.AppendLine($"  {c.Name} → {RuleName(rule)}");
            }
        }
        if (!hasRule) sb.AppendLine("  （未识别到敏感列）");

        sb.AppendLine();
        sb.AppendLine("脱敏后预览（前 " + Math.Min(limit, rs.Rows.Count) + " 行）：");
        int shown = 0;
        foreach (var row in rs.Rows)
        {
            var pairs = new List<KeyValuePair<string, string?>>();
            for (int i = 0; i < rs.Columns.Count && i < row.Length; i++)
            {
                var v = row[i];
                pairs.Add(new KeyValuePair<string, string?>(rs.Columns[i], v is null ? null : v.ToString()));
            }
            var masked = MaskEngine.MaskRow(pairs);
            sb.AppendLine("  " + string.Join(" | ", masked.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}")));
            shown++;
            if (shown >= 50)
            {
                sb.AppendLine("  …（仅展示前 50 行预览）");
                break;
            }
        }

        output.Text = sb.ToString();
        status.Text = $"已展示 {shown} 行脱敏预览（只读，未改库中数据）。";
    }
}
