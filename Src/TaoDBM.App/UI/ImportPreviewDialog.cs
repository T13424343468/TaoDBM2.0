using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>格3：CSV 导入预览与映射——前 50 行预览、列映射（默认同名匹配）、
/// 首行为表头 / 跳过错误行选项。映射结果交回 ImportCsv 按批执行（取消贯穿 CsvBatches）。</summary>
public static class ImportPreviewDialog
{
    /// <summary>导入计划：MappedHeaders 与 MappedCsvIndexes 一一对应（选中导入的列），SkipErrors=出错继续。</summary>
    public sealed record ImportPlan(IReadOnlyList<string> MappedHeaders, int[] MappedCsvIndexes, bool SkipErrors, bool FirstRowHeader);

    public static async Task<ImportPlan?> ShowAsync(MainWindow owner, List<ColumnSpec> tableCols, IReadOnlyList<string> csvHeaders, IReadOnlyList<string?[]> previewRows)
    {
        // 列映射：行=CSV 列，列=表列 ComboBox（默认同名匹配，忽略大小写；未匹配=不导入）
        var mapGrid = new Grid { ColumnSpacing = 10, RowSpacing = 4, AutomationId = "MapGrid" };
        mapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mapGrid.Children.Add(new TextBlock { Text = "CSV 列", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = AntTokens.FontMinor });
        mapGrid.Children.Add(new TextBlock { Text = "导入到表列", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = AntTokens.FontMinor, Grid.Column = 1 });
        var combos = new List<ComboBox>();
        for (int i = 0; i < csvHeaders.Count; i++)
        {
            int row = mapGrid.RowDefinitions.Count;
            mapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mapGrid.Children.Add(new TextBlock { Text = csvHeaders[i], FontSize = AntTokens.FontMinor, VerticalAlignment = VerticalAlignment.Center, Grid.Row = row, TextTrimming = TextTrimming.CharacterEllipsis });
            var cb = new ComboBox { MinWidth = 170, AutomationId = $"Map_{i}", PlaceholderText = "（不导入）", HorizontalAlignment = HorizontalAlignment.Stretch, Grid.Row = row, Grid.Column = 1 };
            cb.Items.Add("（不导入）");
            int def = 0;
            for (int c = 0; c < tableCols.Count; c++)
            {
                cb.Items.Add(tableCols[c].Name);
                if (tableCols[c].Name.Equals(csvHeaders[i], StringComparison.OrdinalIgnoreCase)) def = c + 1;
            }
            cb.SelectedIndex = def;
            mapGrid.Children.Add(cb);
            combos.Add(cb);
        }

        var headerCheck = new CheckBox { Content = "首行为表头", IsChecked = true };
        var skipCheck = new CheckBox { Content = "跳过错误行（出错继续）", IsChecked = true };
        // 前 50 行预览
        var sb = new System.Text.StringBuilder();
        int shown = Math.Min(50, previewRows.Count);
        for (int r = 0; r < shown; r++)
            sb.AppendLine(string.Join(" | ", previewRows[r].Select(v => v ?? "NULL")));
        if (previewRows.Count > shown) sb.AppendLine($"…（预览仅前 {shown} 行，共更多行）");
        var preview = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = (FontFamily)Application.Current.Resources["AntMono"],
            FontSize = AntTokens.FontMinor,
            MaxHeight = 180,
            Text = sb.ToString(),
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 520 };
        panel.Children.Add(new TextBlock { Text = $"读出 {csvHeaders.Count} 列、预览前 {shown} 行：", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(preview);
        panel.Children.Add(new TextBlock { Text = "列映射（默认按列名同名匹配，可改）：", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        panel.Children.Add(new ScrollViewer { Content = mapGrid, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(headerCheck);
        panel.Children.Add(skipCheck);

        var dlg = new ContentDialog
        {
            Title = "CSV 导入预览与映射",
            Content = panel,
            PrimaryButtonText = "开始导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = owner.Content.XamlRoot,
            AutomationId = "ImportPreviewDlg",
        };
        var result = await dlg.ShowThemedAsync();
        if (result != ContentDialogResult.Primary) return null;

        // 收集映射：选中的列按表列名输出，行值按 CSV 索引重排
        var mappedHeaders = new List<string>();
        var mappedIdx = new List<int>();
        for (int i = 0; i < combos.Count; i++)
        {
            if (combos[i].SelectedIndex > 0)
            {
                mappedHeaders.Add(combos[i].SelectedItem?.ToString() ?? "");
                mappedIdx.Add(i);
            }
        }
        if (mappedHeaders.Count == 0) return null;
        return new ImportPlan(mappedHeaders, mappedIdx.ToArray(), skipCheck.IsChecked == true, headerCheck.IsChecked == true);
    }
}
