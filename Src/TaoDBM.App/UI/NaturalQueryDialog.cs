using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>自然语言问库（设计稿 §9）：中文问题 → 确定性解析成 SELECT → 人确认后才动库。
/// SQL 只读预览；「在编辑器打开」带去查询编辑器继续改；「直接执行」走 §7 危险拦截闸再执行。
/// 解析不了的问题如实报原因，不猜测、不拼凑（P-3：纯本地，不调外部模型）。</summary>
public static class NaturalQueryDialog
{
    public static FrameworkElement BuildPanel(MainWindow owner, NodeInfo info)
    {
        var prov = info.Provider ?? ProviderFactory.Create(info.Profile);
        string db = info.Db.Length > 0 ? info.Db : info.Profile.Database;

        // 表选择：表节点直接带入；库节点列全表
        var tableBox = new ComboBox { MinWidth = 260, PlaceholderText = "选择要问的表", Header = "目标表" };
        List<string> tables = new();
        try { tables = prov.GetChildren(db).GetAwaiter().GetResult(); }
        catch { tables = new List<string>(); }
        foreach (var t in tables) tableBox.Items.Add(t);
        if (info.Kind == NodeKind.Table && tables.Contains(info.Name)) tableBox.SelectedItem = info.Name;
        else if (tables.Count > 0) tableBox.SelectedIndex = 0;

        var questionBox = new TextBox
        {
            Header = "你的问题（中文）",
            PlaceholderText = "例如：上个月金额最大的三笔订单是多少？",
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
        };

        var interpretText = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var sqlBox = new TextBox
        {
            Header = "SQL 预览（只读，确认后再执行）",
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = AntTokens.FontMinor,
            MinHeight = 64,
            AcceptsReturn = true,
        };
        var resultHost = new StackPanel { Spacing = 6 };

        var openBtn = new Button { Content = "在编辑器打开", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), FontSize = AntTokens.FontMinor, IsEnabled = false };
        var runBtn = new Button { Content = "直接执行", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), FontSize = AntTokens.FontMinor, IsEnabled = false };
        var askBtn = new Button { Content = "问一下", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusPill), FontSize = AntTokens.FontMinor };

        NLQueryResult? last = null;

        askBtn.Click += async (_, _) =>
        {
            resultHost.Children.Clear();
            interpretText.Text = "";
            sqlBox.Text = "";
            openBtn.IsEnabled = false;
            runBtn.IsEnabled = false;
            if (tableBox.SelectedItem is not string table || table.Length == 0)
            {
                interpretText.Text = "请先选择要问的表。";
                interpretText.Foreground = Ant.Warn;
                return;
            }
            List<ColumnSpec> cols;
            try { cols = await prov.GetColumns(db, table); }
            catch (Exception ex)
            {
                interpretText.Text = HumanError.Say(ex, "读取表结构失败");
                interpretText.Foreground = Ant.Warn;
                return;
            }
            var r = NaturalLanguageQuery.Ask(questionBox.Text, table, cols, prov.Profile.Engine);
            if (!r.Ok)
            {
                interpretText.Text = "解析不了这个问题：" + r.UnsupportedReason;
                interpretText.Foreground = Ant.Warn;
                return;
            }
            last = r;
            interpretText.Text = $"{r.Interpretation}（把握：{(r.Confidence == "high" ? "较高" : "较低，请核对 SQL")}）";
            interpretText.Foreground = r.Confidence == "high" ? Ant.TextSub : Ant.Warn;
            sqlBox.Text = r.Sql;
            openBtn.IsEnabled = true;
            runBtn.IsEnabled = true;
        };

        openBtn.Click += (_, _) =>
        {
            if (last is not { Ok: true } r) return;
            owner.OpenQueryTab(info, r.Sql);
        };

        runBtn.Click += (_, _) => _ = RunAsync();

        async Task RunAsync()
        {
            if (last is not { Ok: true } r || tableBox.SelectedItem is not string tbl) return;
            var gate = await RiskySqlInterceptDialog.GateAsync(owner, prov, db, r.Sql);
            if (!gate.Proceed) return;
            try
            {
                var list = await prov.Execute(db, gate.SqlToRun);
                var qr = list.LastOrDefault();
                resultHost.Children.Clear();
                if (qr is null) return;
                if (qr.HasRows)
                {
                    resultHost.Children.Add(new TextBlock { Text = $"查询结果（{qr.Rows.Count} 行，{qr.Elapsed.TotalMilliseconds:F0} ms）：", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
                    resultHost.Children.Add(new ScrollViewer
                    {
                        MaxHeight = 260,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = GridBuilder.Build(qr, null, r.Sql, prov.EngineName),
                    });
                }
                else
                {
                    resultHost.Children.Add(new TextBlock { Text = qr.Message.Length > 0 ? qr.Message : "执行完成，没有返回行。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });
                }
            }
            catch (Exception ex)
            {
                resultHost.Children.Clear();
                resultHost.Children.Add(new TextBlock { Text = HumanError.Say(ex, "执行失败"), FontSize = AntTokens.FontMinor, Foreground = Ant.Error, TextWrapping = TextWrapping.Wrap });
            }
        }

        var panel = new StackPanel { Spacing = 10, MinWidth = 560 };
        panel.Children.Add(tableBox);
        var qRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        questionBox.MinWidth = 420;
        qRow.Children.Add(questionBox);
        qRow.Children.Add(askBtn);
        qRow.VerticalAlignment = VerticalAlignment.Bottom;
        panel.Children.Add(qRow);
        panel.Children.Add(interpretText);
        panel.Children.Add(sqlBox);
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        btnRow.Children.Add(openBtn);
        btnRow.Children.Add(runBtn);
        panel.Children.Add(btnRow);
        panel.Children.Add(resultHost);

        return new ScrollViewer { Content = panel, MaxHeight = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
