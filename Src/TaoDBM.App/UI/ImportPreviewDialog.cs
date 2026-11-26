using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>发现141：CSV 导入改四步分步向导——①类型与文件（含配置加载）②字段映射（智慧匹配一键）
/// ③导入模式（首行为表头＋遇错策略二选一＋保存配置）④开始导入（汇总确认）。
/// 映射结果交回 ImportCsv 按批执行（取消贯穿 CsvBatches）；进度条与取消沿用现有执行链。</summary>
public static class ImportPreviewDialog
{
    /// <summary>导入计划：MappedHeaders 与 MappedCsvIndexes 一一对应（选中导入的列），SkipErrors=出错继续。</summary>
    public sealed record ImportPlan(IReadOnlyList<string> MappedHeaders, int[] MappedCsvIndexes, bool SkipErrors, bool FirstRowHeader);

    public static async Task<ImportPlan?> ShowAsync(MainWindow owner, List<ColumnSpec> tableCols, IReadOnlyList<string> csvHeaders, IReadOnlyList<string?[]> previewRows)
    {
        // 步骤状态：0=类型与文件 1=字段映射 2=导入模式 3=开始导入
        int step = 0;
        var savedConfigName = "";   // ①加载的配置名（保存时沿用，避免重名堆积）

        // ② 映射控件（跨步骤复用）
        var mapGrid = new Grid { ColumnSpacing = 10, RowSpacing = 4 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(mapGrid, "MapGrid");
        mapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mapGrid.Children.Add(new TextBlock { Text = "CSV 列", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = AntTokens.FontMinor });
        var hdrTo = new TextBlock { Text = "导入到表列", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = AntTokens.FontMinor };
        mapGrid.Children.Add(hdrTo);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(hdrTo, 1);
        var combos = new List<ComboBox>();
        for (int i = 0; i < csvHeaders.Count; i++)
        {
            int row = mapGrid.RowDefinitions.Count;
            mapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lbl = new TextBlock { Text = csvHeaders[i], FontSize = AntTokens.FontMinor, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            mapGrid.Children.Add(lbl);
            Microsoft.UI.Xaml.Controls.Grid.SetRow(lbl, row);
            var cb = new ComboBox { MinWidth = 170, PlaceholderText = "（不导入）", HorizontalAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Controls.Grid.SetRow(cb, row);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(cb, 1);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(cb, $"Map_{i}");
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

        // ① 配置下拉与操作
        var configBox = new ComboBox { MinWidth = 200, PlaceholderText = "（新建，不套配置）" };
        foreach (var n in ImportProfiles.List()) configBox.Items.Add(n);
        var configStatus = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var loadConfigBtn = new Button { Content = "加载所选配置", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var delConfigBtn = new Button { Content = "删除所选配置", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        // ③ 选项（遇错策略二选一，发现141）——先声明，供加载配置回调引用
        var headerCheck = new CheckBox { Content = "首行为表头", IsChecked = true };
        var skipRadio = new RadioButton { Content = "跳过错误行（出错继续）", GroupName = "ErrPolicy", IsChecked = true };
        var stopRadio = new RadioButton { Content = "遇错即停（报已导入行数）", GroupName = "ErrPolicy" };
        loadConfigBtn.Click += (_, _) =>
        {
            if (configBox.SelectedIndex <= 0) { configStatus.Text = "先在下拉里选一个配置再加载。"; return; }
            var name = configBox.SelectedItem?.ToString() ?? "";
            var p = ImportProfiles.Load(name);
            if (p is null) { configStatus.Text = $"配置「{name}」读不出来（可能已损坏），请删掉重建。"; return; }
            int hit = 0;
            for (int i = 0; i < combos.Count && i < p.TargetColumns.Count; i++)
            {
                var target = p.TargetColumns[i];
                int idx = 0;
                if (target is not null)
                    for (int c = 0; c < tableCols.Count; c++)
                        if (tableCols[c].Name.Equals(target, StringComparison.OrdinalIgnoreCase)) { idx = c + 1; break; }
                combos[i].SelectedIndex = idx;
                if (idx > 0) hit++;
            }
            headerCheck.IsChecked = p.FirstRowHeader;
            skipRadio.IsChecked = p.SkipErrors;
            savedConfigName = name;
            configStatus.Text = $"已套用配置「{name}」：映射 {hit} 列、首行为表头={p.FirstRowHeader}、遇错策略={(p.SkipErrors ? "跳过错误行" : "遇错即停")}。";
        };
        delConfigBtn.Click += (_, _) =>
        {
            if (configBox.SelectedIndex <= 0) { configStatus.Text = "先选一个配置再删。"; return; }
            var name = configBox.SelectedItem?.ToString() ?? "";
            ImportProfiles.Delete(name);
            configBox.Items.RemoveAt(configBox.SelectedIndex);
            configBox.SelectedIndex = -1;
            configStatus.Text = $"已删除配置「{name}」。";
        };

        // ② 智慧匹配（发现141：一键全字段匹配）
        var smartBtn = new Button { Content = "智慧匹配全部字段", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(smartBtn, "ImportSmartMatch");
        smartBtn.Click += (_, _) =>
        {
            var match = ImportProfiles.SmartMatch(csvHeaders, tableCols.Select(c => c.Name).ToList());
            for (int i = 0; i < combos.Count && i < match.Count; i++)
            {
                int idx = 0;
                if (match[i] is not null)
                    for (int c = 0; c < tableCols.Count; c++)
                        if (tableCols[c].Name.Equals(match[i], StringComparison.OrdinalIgnoreCase)) { idx = c + 1; break; }
                combos[i].SelectedIndex = idx;
            }
            AppLog.Write($"[导入配置] 智慧匹配命中 {match.Count(m => m is not null)}/{csvHeaders.Count} 列");
        };

        // ③ 保存配置
        var saveNameBox = new TextBox { PlaceholderText = "配置名（如：订单表日导）", MinWidth = 200 };
        var saveBtn = new Button { Content = "保存为配置", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        saveBtn.Click += (_, _) =>
        {
            var name = (saveNameBox.Text ?? "").Trim();
            if (name.Length == 0) { configStatus.Text = "先填配置名再保存。"; return; }
            var (headers, targets) = CollectMapping();
            ImportProfiles.Save(new ImportProfiles.ImportProfile(name, headers, targets, headerCheck.IsChecked == true, skipRadio.IsChecked == true));
            if (!configBox.Items.Contains(name)) configBox.Items.Add(name);
            configBox.SelectedItem = name;
            savedConfigName = name;
            configStatus.Text = $"已保存配置「{name}」，下次导入①可直接套用。";
        };

        // 前 50 行预览（②内嵌，滚动区）
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
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["AntMono"],
            FontSize = AntTokens.FontMinor,
            MaxHeight = 180,
            Text = sb.ToString(),
        };

        // 步骤条与内容容器
        var stepLabel = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub };
        var contentHost = new ContentControl { MinWidth = 520 };
        var hint = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.Error, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var prevBtn = new Button { Content = "上一步", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), IsEnabled = false };
        var nextBtn = new Button { Content = "下一步", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(nextBtn, "ImportNext");

        (UIElement Content, string Label) BuildStep(int s)
        {
            switch (s)
            {
                case 0:
                {
                    var p = new StackPanel { Spacing = 10 };
                    p.Children.Add(new TextBlock { Text = "① 类型与文件：文件已选好。有保存过的导入配置可直接套用（映射＋选项一起回填），没有就点下一步新建映射。", TextWrapping = TextWrapping.Wrap, FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(configBox);
                    row.Children.Add(loadConfigBtn);
                    row.Children.Add(delConfigBtn);
                    p.Children.Add(row);
                    p.Children.Add(configStatus);
                    return (p, "① 类型与文件");
                }
                case 1:
                {
                    var p = new StackPanel { Spacing = 10 };
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(smartBtn);
                    p.Children.Add(row);
                    p.Children.Add(new TextBlock { Text = $"读出 {csvHeaders.Count} 列、预览前 {shown} 行：", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });
                    p.Children.Add(preview);
                    p.Children.Add(new TextBlock { Text = "列映射（默认按列名同名匹配，可逐列手改）：", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
                    p.Children.Add(new ScrollViewer { Content = mapGrid, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                    return (p, "② 字段映射");
                }
                case 2:
                {
                    var p = new StackPanel { Spacing = 10 };
                    p.Children.Add(new TextBlock { Text = "③ 导入模式：「首行为表头」决定首行当列名还是数据；遇错策略二选一——跳过错误行继续导入，或遇错即停（会报已导入到第几行）。", TextWrapping = TextWrapping.Wrap, FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
                    p.Children.Add(headerCheck);
                    p.Children.Add(skipRadio);
                    p.Children.Add(stopRadio);
                    p.Children.Add(new TextBlock { Text = "想下次直接套用这套映射和选项？起个名字存下来：", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(saveNameBox);
                    row.Children.Add(saveBtn);
                    p.Children.Add(row);
                    p.Children.Add(configStatus);
                    return (p, "③ 导入模式");
                }
                default:
                {
                    var (headers, targets) = CollectMapping();
                    int mapped = targets.Count(t => t is not null);
                    var p = new StackPanel { Spacing = 10 };
                    p.Children.Add(new TextBlock { Text = "④ 开始导入：确认下面的汇总，点「开始导入」执行。", TextWrapping = TextWrapping.Wrap, FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
                    p.Children.Add(new TextBlock
                    {
                        Text = $"映射 {mapped} 列（共 {csvHeaders.Count} 列）\n首行为表头：{(headerCheck.IsChecked == true ? "是" : "否")}\n遇错策略：{(skipRadio.IsChecked == true ? "跳过错误行继续" : "遇错即停")}" +
                               (savedConfigName.Length > 0 ? $"\n套用配置：{savedConfigName}" : ""),
                        FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap,
                    });
                    return (p, "④ 开始导入");
                }
            }
        }

        (List<string> Headers, List<string?> Targets) CollectMapping()
        {
            var headers = new List<string>();
            var targets = new List<string?>();
            for (int i = 0; i < combos.Count; i++)
            {
                headers.Add(csvHeaders[i]);
                targets.Add(combos[i].SelectedIndex > 0 ? combos[i].SelectedItem?.ToString() : null);
            }
            return (headers, targets);
        }

        void ShowStep(int s)
        {
            step = s;
            var (content, label) = BuildStep(s);
            contentHost.Content = content;
            stepLabel.Text = label;
            prevBtn.IsEnabled = s > 0;
            nextBtn.Content = s == 3 ? "开始导入" : "下一步";
            hint.Visibility = Visibility.Collapsed;
        }

        var panel = new StackPanel { Spacing = 10, MinWidth = 540 };
        panel.Children.Add(stepLabel);
        panel.Children.Add(contentHost);
        panel.Children.Add(hint);
        var navRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        navRow.Children.Add(prevBtn);
        navRow.Children.Add(nextBtn);
        panel.Children.Add(navRow);

        var dlg = new ContentDialog
        {
            Title = "CSV 导入向导",
            Content = panel,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = owner.Content.XamlRoot,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dlg, "ImportPreviewDlg");

        var done = false;
        nextBtn.Click += (_, _) =>
        {
            // 步骤守门：②至少一列映射且无冲突；③选项天然合法
            if (step == 1)
            {
                var (_, targets) = CollectMapping();
                var dup = targets.Where(t => t is not null).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                if (dup is not null)
                {
                    hint.Visibility = Visibility.Visible;
                    hint.Text = $"映射冲突：表列「{dup.Key}」被多个 CSV 列同时选中，请只保留一个。";
                    return;
                }
                if (targets.All(t => t is null))
                {
                    hint.Visibility = Visibility.Visible;
                    hint.Text = "还没有任何映射：把至少一个 CSV 列指到表列，或点取消放弃导入。";
                    return;
                }
            }
            if (step == 3) { done = true; dlg.Hide(); return; }
            ShowStep(step + 1);
        };
        prevBtn.Click += (_, _) => { if (step > 0) ShowStep(step - 1); };

        ShowStep(0);
        await dlg.ShowThemedAsync();
        if (!done) return null;

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
        return new ImportPlan(mappedHeaders, mappedIdx.ToArray(), skipRadio.IsChecked == true, headerCheck.IsChecked == true);
    }
}
