using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>P-6 图形化分库分区分表向导：四步式（选表 → 选拆分方式 → 选分片键 → 确认方案），
/// 三种分片策略（按范围 / 按哈希 / 按类别）全量点选。分片键默认只允许主键 / 有索引等有依据的字段
/// （索引元数据判断），提供熟手开关解锁全部字段（解锁动作写 permops.log 审计）；
/// 选了无依据字段黄牌提醒 + 二次确认。执行复用 DraftOps 草稿执行器（预览 + 勾选确认 + permops.log 留痕）；
/// 在线改表复用 OnlineDdl.Plan 计划；分区现状、热点盐化与读写分离提示随方案顺路展示。</summary>
public static class ShardingWizardDialog
{
    private const int StepTable = 0, StepStrategy = 1, StepKey = 2, StepConfirm = 3;

    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        var profile = info.Profile;
        var prov = info.Provider ?? ProviderFactory.Create(profile);
        if (!ShardingWizard.Supported(profile.Engine))
        {
            await new MessageDialogHelper(owner).Show("分片向导", ShardingWizard.UnsupportedReason(profile.Engine));
            return;
        }

        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        string db = profile.Database;

        // ── 向导状态 ──
        int step = StepTable;
        int maxStep = StepTable;   // 格13：已到达过的最远步骤（决定步骤条可点范围）
        var drafts = new List<string>();
        var indexedCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool expertUnlocked = false;

        // ── 第 1 步：选表 ──
        var tableBox = new ComboBox { Width = 260, PlaceholderText = "选择目标表" };
        var stepPanels = new Grid();
        stepPanels.Children.Add(Wrap(new StackPanel { Spacing = 10 }, StepTable));

        // ── 第 2 步：选拆分方式 ──
        var strategyBox = new ComboBox { Width = 260, SelectedIndex = 1, PlaceholderText = "选拆分方式" };
        foreach (var n in ShardingWizard.StrategyNames) strategyBox.Items.Add(new ComboBoxItem { Content = n });
        strategyBox.SelectedIndex = 1; // 默认按哈希
        var hashRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var shardBox = new ComboBox { Width = 88, SelectedIndex = 2 };
        for (int i = 2; i <= 16; i++) shardBox.Items.Add(new ComboBoxItem { Content = i.ToString() });
        shardBox.SelectedIndex = 2; // 4 片
        hashRow.Children.Add(new TextBlock { Text = "分片数", VerticalAlignment = VerticalAlignment.Center, FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        hashRow.Children.Add(shardBox);
        var rangeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var boundsBox = new TextBox { PlaceholderText = "升序上界，如 100,200,300", Width = 260 };
        rangeRow.Children.Add(boundsBox);
        var listRow = new StackPanel { Spacing = 6 };
        var catsBox = new TextBox
        {
            AcceptsReturn = true,
            Height = 90,
            FontFamily = new FontFamily("Consolas"),
            FontSize = AntTokens.FontMinor,
            PlaceholderText = "每行一组：组名=取值，如 south=1,2",
        };
        listRow.Children.Add(new TextBlock { Text = "类别组（每行 组名=取值，逗号分隔）：", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        listRow.Children.Add(catsBox);
        var strategyPanel = new StackPanel { Spacing = 10 };
        strategyPanel.Children.Add(strategyBox);
        strategyPanel.Children.Add(hashRow);
        strategyPanel.Children.Add(rangeRow);
        strategyPanel.Children.Add(listRow);
        stepPanels.Children.Add(Wrap(strategyPanel, StepStrategy));

        // ── 第 3 步：选分片键 ──
        var keyBox = new ComboBox { Width = 260, PlaceholderText = "选择分片键" };
        var columnCache = new List<string>();
        var expertCheck = new CheckBox
        {
            Content = "解锁全部字段（熟手；无索引依据字段需黄牌二次确认）",
            FontSize = AntTokens.FontMinor,
        };
        var keyHint = new TextBlock { FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub };
        // 发现307原型：候选分片键卡片——有索引依据字段逐个成卡，首个标 ★ 推荐
        var keyCardRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var keyPanel = new StackPanel { Spacing = 10 };
        keyPanel.Children.Add(keyBox);
        keyPanel.Children.Add(keyHint);
        keyPanel.Children.Add(keyCardRow);
        keyPanel.Children.Add(expertCheck);
        stepPanels.Children.Add(Wrap(keyPanel, StepKey));

        // ── 第 4 步：确认方案 ──
        var summaryText = new TextBlock { FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var planPanel = new StackPanel { Spacing = 4 };
        var execBtn = new Button { Content = "预览并执行方案", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), IsEnabled = false };
        var confirmPanel = new StackPanel { Spacing = 10 };
        confirmPanel.Children.Add(summaryText);
        // 发现307原型：SQL 改写预览表头——草稿区上方加底色表头条
        confirmPanel.Children.Add(new Border
        {
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusSm),
            Background = Ant.HeadBg,
            Child = new TextBlock
            {
                Text = "SQL 改写预览（草稿逐条，执行前逐条勾选）",
                FontSize = AntTokens.FontMinor,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Ant.TextSub,
            },
        });
        confirmPanel.Children.Add(new ScrollViewer { Content = planPanel, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        confirmPanel.Children.Add(execBtn);
        stepPanels.Children.Add(Wrap(confirmPanel, StepConfirm));

        // ── 导航与步骤指示 ──
        var prevBtn = new Button { Content = "上一步", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var nextBtn = new Button { Content = "下一步", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var navRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        navRow.Children.Add(prevBtn);
        navRow.Children.Add(nextBtn);
        var stepChips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var chipNames = new[] { "① 选表", "② 选拆分方式", "③ 选分片键", "④ 确认方案" };
        var chips = chipNames.Select(n => new Border
        {
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusPill),
            Background = Ant.Bg,
            Child = new TextBlock { Text = n, FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub },
        }).ToList();
        // 格13：状态行前置——对话框顶部固定一行当前步骤说明，校验错误仍在步骤内 status 展示。
        // 声明须先于步骤条 Tapped 挂接：局部函数 ShowStep 读取 topStatus，调用点前须已明确赋值。
        var topStatus = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.Primary, TextWrapping = TextWrapping.Wrap };
        var stepDescs = new[] { "第 1 步：选择目标表", "第 2 步：选拆分方式", "第 3 步：选分片键", "第 4 步：确认方案" };
        // 格13：步骤条可点跳转——已过步骤可点回跳（后续步骤态保留，确认页重过校验），未到步骤禁用（半透明）。
        for (int i = 0; i < chips.Count; i++)
        {
            int idx = i;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(chips[i], $"StepChip_{i + 1}");
            chips[i].Tapped += (_, _) => { if (idx <= maxStep) ShowStep(idx); };
            stepChips.Children.Add(chips[i]);
        }

        void ShowStep(int s)
        {
            step = s;
            maxStep = Math.Max(maxStep, s);
            foreach (var child in stepPanels.Children.OfType<Border>())
                child.Visibility = (int)child.Tag == s ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < chips.Count; i++)
            {
                chips[i].Background = i == s ? Ant.Primary : Ant.Bg;
                ((TextBlock)chips[i].Child).Foreground = i == s ? Ant.OnPrimary : Ant.TextSub;
                chips[i].Opacity = i <= maxStep ? 1.0 : 0.5;   // 格13：未到步骤半透明禁点
            }
            prevBtn.IsEnabled = s > StepTable;
            nextBtn.Content = s == StepConfirm ? "生成分片方案" : "下一步";
            topStatus.Text = stepDescs[s];
            status.Text = "";
        }

        // ── 第 1 步交互：拉表清单 ──
        _ = LoadTablesAsync();
        async Task LoadTablesAsync()
        {
            try
            {
                var tables = await prov.GetChildren(db);
                tableBox.Items.Clear();
                foreach (var t in tables) tableBox.Items.Add(new ComboBoxItem { Content = t, Tag = t });
                status.Text = tables.Count == 0 ? "当前库没有可分片的表。" : $"共 {tables.Count} 张表可选。";
            }
            catch (Exception ex) { status.Text = HumanError.Say(ex, "表清单获取失败"); }
        }
        tableBox.SelectionChanged += async (_, _) =>
        {
            if (Selected(tableBox) is not { Length: > 0 } table) return;
            await LoadKeyMetadataAsync(table);
        };

        async Task LoadKeyMetadataAsync(string table)
        {
            keyBox.Items.Clear();
            indexedCols.Clear();
            columnCache.Clear();
            keyHint.Text = "";
            try
            {
                var cols = await prov.GetColumns(db, table);
                columnCache.AddRange(cols.Select(c => c.Name).Where(n => n.Length > 0));
                var rs = await prov.Execute("", ShardingWizard.IndexColumnsSql(profile.Engine, db, table));
                if (rs.Count > 0)
                    foreach (var row in rs[0].Rows)
                    {
                        var col = row[0]?.ToString();
                        if (!string.IsNullOrEmpty(col)) indexedCols.Add(col);
                    }
                FillKeyBox(columnCache);
                keyHint.Text = indexedCols.Count == 0
                    ? "未查到该表的索引列，主键 / 有索引字段不可用；可用熟手开关解锁全部字段。"
                    : $"主键 / 有索引字段 {indexedCols.Count} 个（默认仅这些可选）。";
                if (expertUnlocked)
                    Safe.Run(() => File.AppendAllLines(TaoPaths.FilePath("permops.log"),
                        new[] { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{ShardingWizard.UnlockAudit(table, "熟手开关解锁全部字段（选表后补记）")}" }), "分片键解锁审计");
            }
            catch (Exception ex) { status.Text = HumanError.Say(ex, "列与索引元数据获取失败"); }
        }

        void FillKeyBox(IEnumerable<string> allCols)
        {
            keyBox.Items.Clear();
            foreach (var c in allCols)
            {
                bool backed = indexedCols.Contains(c);
                if (!backed && !expertUnlocked) continue;
                var item = new ComboBoxItem { Content = backed ? c : c + "（无索引依据）", Tag = c };
                keyBox.Items.Add(item);
            }
            keyBox.SelectedIndex = keyBox.Items.Count > 0 ? 0 : -1;
            // 发现307原型：候选分片键卡片——有索引依据字段逐个成卡，首个标 ★ 推荐
            keyCardRow.Children.Clear();
            int carded = 0;
            foreach (var c in allCols)
            {
                if (!indexedCols.Contains(c)) continue;
                if (carded >= 6) break;
                keyCardRow.Children.Add(KeyCard(c, carded == 0));
                carded++;
            }
            if (carded == 0)
                keyCardRow.Children.Add(new TextBlock
                {
                    Text = "暂无有索引依据的候选键。",
                    FontSize = AntTokens.FontCaption,
                    Foreground = Ant.TextSub,
                });
        }

        // 熟手开关：解锁动作本身写操作记录（permops.log 审计留痕）。
        expertCheck.Click += (_, _) =>
        {
            bool unlocking = expertCheck.IsChecked == true && !expertUnlocked;
            expertUnlocked = expertCheck.IsChecked == true;
            if (unlocking && Selected(tableBox) is { Length: > 0 } table)
            {
                Safe.Run(() => File.AppendAllLines(TaoPaths.FilePath("permops.log"),
                    new[] { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{ShardingWizard.UnlockAudit(table, "熟手开关解锁全部字段")}" }), "分片键解锁审计");
                status.Text = "已解锁全部字段，解锁动作已写入操作记录。";
            }
            FillKeyBox(columnCache);
        };

        // ── 策略联动 ──
        void SyncStrategyRows()
        {
            hashRow.Visibility = strategyBox.SelectedIndex == (int)ShardingWizard.ShardStrategy.Hash ? Visibility.Visible : Visibility.Collapsed;
            rangeRow.Visibility = strategyBox.SelectedIndex == (int)ShardingWizard.ShardStrategy.Range ? Visibility.Visible : Visibility.Collapsed;
            listRow.Visibility = strategyBox.SelectedIndex == (int)ShardingWizard.ShardStrategy.List ? Visibility.Visible : Visibility.Collapsed;
        }
        strategyBox.SelectionChanged += (_, _) => SyncStrategyRows();
        SyncStrategyRows();

        // ── 导航逻辑 ──
        nextBtn.Click += async (_, _) =>
        {
            switch (step)
            {
                case StepTable when Selected(tableBox) is { Length: > 0 }:
                    ShowStep(StepStrategy);
                    break;
                case StepTable:
                    status.Text = "请先选择目标表。";
                    break;
                case StepStrategy when ParseStrategyInput(status):
                    ShowStep(StepKey);
                    break;
                case StepStrategy:
                    break; // 解析失败，status 已带原因
                case StepKey:
                    await TryGoConfirmAsync();
                    break;
                case StepConfirm:
                    GenerateDrafts();
                    break;
            }
        };
        prevBtn.Click += (_, _) => { if (step > StepTable) ShowStep(step - 1); };

        async Task TryGoConfirmAsync()
        {
            if (Selected(keyBox) is not { Length: > 0 } key || Selected(tableBox) is not { Length: > 0 } table)
            {
                status.Text = "请先选择分片键。";
                return;
            }
            if (!ShardingWizard.KeyBacked(key, indexedCols))
            {
                // 黄牌提醒 + 二次确认：无索引依据字段不给静默通过。
                status.Text = $"黄牌提醒：{key} 无主键 / 索引依据，查询可能退化为全分片扫描。";
                var warn = new ContentDialog
                {
                    Title = "黄牌确认：无索引依据的分片键",
                    Content = new TextBlock
                    {
                        Text = $"字段 {key} 不是主键 / 索引列，作分片键将使未携带该键的查询退化为全分片扫描。\n确认仍选它作分片键？",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    PrimaryButtonText = "仍用该字段",
                    CloseButtonText = "重新选择",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = owner.Content.XamlRoot,
                };
                if (await warn.ShowThemedAsync() != ContentDialogResult.Primary)
                {
                    status.Text = "请重新选择有依据的分片键。";
                    return;
                }
            }
            ShowStep(StepConfirm);
        }

        // 解析第 2 步输入（写回策略上下文），失败在 status 给原因。
        bool ParseStrategyInput(TextBlock st)
        {
            var strategy = (ShardingWizard.ShardStrategy)Math.Max(0, strategyBox.SelectedIndex);
            try
            {
                drafts.Clear();
                var table = Selected(tableBox) ?? "";
                var key = Selected(keyBox) ?? "";
                drafts.AddRange(strategy switch
                {
                    ShardingWizard.ShardStrategy.Hash => ShardingWizard.HashPartitionSql(profile.Engine, table, key, shardBox.SelectedIndex + 2),
                    ShardingWizard.ShardStrategy.Range => ShardingWizard.RangePartitionSql(profile.Engine, table, key, Bounds()),
                    ShardingWizard.ShardStrategy.List => ShardingWizard.ListPartitionSql(profile.Engine, table, key, Categories()),
                    _ => throw new NotSupportedException("未知的拆分方式"),
                });
                return true;
            }
            catch (Exception ex)
            {
                st.Text = HumanError.Say(ex, "输入有误");
                return false;
            }

            List<string> Bounds()
            {
                var list = boundsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (list.Count < 2) throw new ArgumentException("范围分片至少需要 2 个上界，逗号分隔且升序");
                var nums = list.Select(s => long.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToList();
                for (int i = 1; i < nums.Count; i++)
                    if (nums[i] <= nums[i - 1]) throw new ArgumentException("范围上界必须严格升序");
                return list;
            }

            List<(string Name, IReadOnlyList<string> Values)> Categories()
            {
                var groups = new List<(string Name, IReadOnlyList<string> Values)>();
                foreach (var line in catsBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var seg = line.Split('=', 2);
                    if (seg.Length != 2 || seg[0].Trim().Length == 0)
                        throw new ArgumentException($"类别组格式有误：{line}（应为 组名=取值）");
                    var vals = seg[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (vals.Count == 0) throw new ArgumentException($"类别组 {seg[0]} 至少需要一个取值");
                    groups.Add((seg[0].Trim(), vals));
                }
                return groups;
            }
        }

        void GenerateDrafts()
        {
            planPanel.Children.Clear();
            foreach (var d in drafts)
                planPanel.Children.Add(new TextBlock
                {
                    Text = d,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = AntTokens.FontMinor,
                    TextWrapping = TextWrapping.Wrap,
                });
            var table = Selected(tableBox) ?? "";
            var key = Selected(keyBox) ?? "";
            var strategy = (ShardingWizard.ShardStrategy)Math.Max(0, strategyBox.SelectedIndex);
            int pieces = strategy == ShardingWizard.ShardStrategy.Hash
                ? shardBox.SelectedIndex + 2
                : strategy == ShardingWizard.ShardStrategy.Range
                    ? boundsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries).Length + 1
                    : catsBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            summaryText.Text = $"目标表 {table}，分片键 {key}，算法 {ShardingWizard.StrategyNames[(int)strategy].Trim('（', '）')}。\n{ShardingWizard.ReadSplitHint(pieces)}";
            execBtn.IsEnabled = drafts.Count > 0;
            status.Text = profile.Engine == DbEngine.PostgreSQL
                ? "PG 为声明式分区：方案含建新表 + 分区 + 迁移注释，执行前请逐条核对。"
                : "方案已生成，确认后执行（草稿执行器会再次预览 + 勾选确认）。";
        }

        execBtn.Click += async (_, _) =>
        {
            var snapshot = drafts.ToList();
            if (snapshot.Count == 0) return;
            await DraftOps.ConfirmExecuteAsync(owner, prov, "执行分片方案（草稿）", snapshot, status);
        };

        ShowStep(StepTable);

        // ── 向导之外的既有能力块：分区现状 / 热点盐化 / 在线改表 ──
        TextBlock Head(string s) => new()
        {
            Text = s,
            FontSize = AntTokens.FontBody,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };

        // 分区现状分布（行数降序 + 倾斜标红）
        var distBtn = new Button { Content = "查询分区现状", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var distPanel = new StackPanel { Spacing = 4 };
        distBtn.Click += async (_, _) =>
        {
            if (Selected(tableBox) is not { Length: > 0 } t) { status.Text = "请先在第 1 步选择目标表。"; return; }
            try
            {
                var rs = await prov.Execute("", ShardingWizard.PartitionsInfoSql(profile.Engine, db, t));
                var parts = new List<(string Name, long Rows)>();
                if (rs.Count > 0)
                    foreach (var row in rs[0].Rows)
                        parts.Add((row[0]?.ToString() ?? "", row[1] is null ? 0 : Convert.ToInt64(row[1])));
                var (sorted, skewed) = ShardingWizard.AnalyzeDistribution(parts);
                distPanel.Children.Clear();
                if (sorted.Count == 0) { status.Text = "未查到分区数据（该表可能尚未分区）。"; return; }
                long maxRows = sorted[0].Rows;
                foreach (var p in sorted)
                {
                    // 发现307原型：分布条形——按行数占比画横条，倾斜分片标红
                    double frac = maxRows > 0 ? (double)p.Rows / maxRows : 0;
                    bool hot = skewed && p.Rows == maxRows;
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(new TextBlock
                    {
                        Text = p.Name,
                        Width = 130,
                        FontSize = AntTokens.FontMinor,
                        FontFamily = new FontFamily("Consolas"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    row.Children.Add(new Border
                    {
                        Width = 140 * frac,
                        Height = 8,
                        CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusBadge),
                        Background = hot ? (Brush?)Ant.Error : (Brush?)Ant.Primary,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    row.Children.Add(new TextBlock
                    {
                        Text = $"{p.Rows} 行",
                        FontSize = AntTokens.FontMinor,
                        Foreground = hot ? Ant.Error : Ant.TextSub,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    distPanel.Children.Add(row);
                }
                status.Text = skewed
                    ? "分布倾斜：最大分片超过最小分片 1.5 倍，建议重新分片或对热点键盐化。"
                    : $"共 {sorted.Count} 个分区，分布均匀。";
            }
            catch (Exception ex)
            {
                status.Text = HumanError.Say(ex, "分区现状查询失败");
            }
        };

        // 热点盐化摘要（展示用）
        var saltKeyBox = new TextBox { PlaceholderText = "热点键，如 user_id", Width = 180 };
        var saltCountBox = new TextBox { Text = "8", Width = 88 };
        var saltBtn = new Button { Content = "生成盐化摘要", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var saltText = new TextBlock { FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        saltBtn.Click += (_, _) =>
        {
            var k = saltKeyBox.Text.Trim();
            if (k.Length == 0) { status.Text = "请先填写热点键。"; return; }
            if (!int.TryParse(saltCountBox.Text.Trim(), out int sc) || sc < 2 || sc > 64) { status.Text = "盐桶数需为 2–64 的整数。"; return; }
            saltText.Text = ShardingWizard.SaltingSummary(k, sc);
        };
        var saltRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        saltRow.Children.Add(saltKeyBox);
        saltRow.Children.Add(saltCountBox);
        saltRow.Children.Add(saltBtn);

        // ── 分片路由联动（P-6 联动一）：方案接入读写分离路由 —— 落片预览 + 读节点建议 + 计划落盘 ──
        var linkCheck = new CheckBox
        {
            Content = "方案接入读写分离路由（保存分片计划，写入按分片键落片，读取交延迟感知读路由）",
            FontSize = AntTokens.FontMinor,
        };
        var routeKeyBox = new TextBox { PlaceholderText = "样例分片键值，逗号分隔，如 7,1500,9999", Width = 260 };
        var routeLagBox = new TextBox { Text = "10", Width = 88 };
        var routeBtn = new Button { Content = "路由预览", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var routeText = new TextBlock { FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var routeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        routeRow.Children.Add(routeKeyBox);
        routeRow.Children.Add(new TextBlock { Text = "延迟阈值(秒)", VerticalAlignment = VerticalAlignment.Center, FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        routeRow.Children.Add(routeLagBox);
        routeRow.Children.Add(routeBtn);

        // 由向导当前选择构建 ShardPlan（与 ParseStrategyInput 同一解析口径）。
        ShardPlan BuildPlan()
        {
            var table = Selected(tableBox) ?? "";
            var key = Selected(keyBox) ?? "";
            var strategy = (ShardingWizard.ShardStrategy)Math.Max(0, strategyBox.SelectedIndex);
            return strategy switch
            {
                ShardingWizard.ShardStrategy.Hash => new ShardPlan(table, key, strategy,
                    shardBox.SelectedIndex + 2, Array.Empty<string>(), Array.Empty<ShardCategory>()),
                ShardingWizard.ShardStrategy.Range => new ShardPlan(table, key, strategy,
                    boundsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length + 1,
                    boundsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    Array.Empty<ShardCategory>()),
                _ => new ShardPlan(table, key, strategy,
                    catsBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length,
                    Array.Empty<string>(),
                    catsBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(l => l.Split('=', 2))
                        .Where(seg => seg.Length == 2)
                        .Select(seg => new ShardCategory(seg[0].Trim(),
                            seg[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                        .ToList()),
            };
        }

        linkCheck.Click += (_, _) =>
        {
            if (linkCheck.IsChecked != true) { status.Text = "已取消联动：该表路由回归常规连接配置。"; return; }
            if (Selected(tableBox) is not { Length: > 0 } || Selected(keyBox) is not { Length: > 0 })
            {
                linkCheck.IsChecked = false;
                status.Text = "请先完成向导的选表与选分片键，再接入读写分离路由。";
                return;
            }
            try
            {
                ShardRouting.SavePlan(BuildPlan());
                status.Text = "分片计划已保存：写入按分片键落片，读取走延迟感知读路由；可在读路由页查看联动预览。";
            }
            catch (Exception ex)
            {
                linkCheck.IsChecked = false;
                status.Text = HumanError.Say(ex, "分片计划保存失败");
            }
        };

        routeBtn.Click += (_, _) =>
        {
            if (Selected(tableBox) is not { Length: > 0 } || Selected(keyBox) is not { Length: > 0 })
            { status.Text = "请先完成向导的选表与选分片键。"; return; }
            var keys = routeKeyBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (keys.Length == 0) { status.Text = "请先填入样例分片键值。"; return; }
            if (!long.TryParse(routeLagBox.Text.Trim(), out long lag) || lag < 0) { status.Text = "延迟阈值需为非负整数。"; return; }
            try
            {
                var plan = BuildPlan();
                // 副本画像演示口径：p0..pN 命名，p0 延迟 5s、其余 30s（真机连接后由探活填充）
                var replicas = Enumerable.Range(0, plan.ShardCount)
                    .Select(i => new ReplicaNode($"{plan.Table}.p{i}", i == 0 ? 5 : 30, true)).ToList();
                var pv = ShardRouting.Preview(plan, keys, replicas, lag);
                routeText.Text = string.Join("\n", pv.Routes.Select(r => $"{r.Key} → 分片 {r.Shard}")) + "\n" + pv.Note;
                status.Text = ShardRouting.PlanSummary(plan);
            }
            catch (Exception ex)
            {
                routeText.Text = "";
                status.Text = HumanError.Say(ex, "路由预览失败");
            }
        };

        // 在线改表计划（MySQL 方言，复用 OnlineDdl.Plan；@last 占位符不可直接执行）
        var estBox = new TextBox { PlaceholderText = "预估行数", Text = "1000000", Width = 130 };
        var batchBox = new TextBox { Text = "2000", Width = 90 };
        var pkBox = new TextBox { PlaceholderText = "回填主键列", Width = 130 };
        var onlineBtn = new Button { Content = "生成在线改表计划", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var onlineText = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var openQryBtn = new Button { Content = "作为查询窗口打开", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), IsEnabled = false };
        string onlinePlanText = "";
        if (profile.Engine == DbEngine.MySQL)
        {
            onlineBtn.Click += (_, _) =>
            {
                if (Selected(tableBox) is not { Length: > 0 } t || Selected(keyBox) is not { Length: > 0 } k)
                {
                    status.Text = "请先完成向导的选表与选分片键。";
                    return;
                }
                if (!long.TryParse(estBox.Text.Trim(), out long rows) || rows < 0) { status.Text = "预估行数需为非负整数。"; return; }
                if (!int.TryParse(batchBox.Text.Trim(), out int batch) || batch < 1) { status.Text = "批大小需为正整数。"; return; }
                try
                {
                    var alterClause = $"PARTITION BY HASH (`{k.Replace("`", "")}`) PARTITIONS {shardBox.SelectedIndex + 2}";
                    var plan = OnlineDdl.Plan(t, alterClause, pkBox.Text.Trim(), rows, batch);
                    int backfills = plan.Steps.Count(s => s.Phase == "backfill");
                    var lines = new List<string>
                    {
                        $"-- 在线改表计划：影子表 {plan.ShadowTable} / 旧表 {plan.OldTable}，预估 {plan.EstimatedRows} 行、回填 {backfills} 步",
                    };
                    foreach (var s in plan.Steps)
                        lines.Add($"[{s.Phase}] {s.Sql}    -- {s.Note}");
                    lines.Add("-- 注意：含 @last 占位符的语句不可直接执行，请到查询窗口按批替换主键值后逐段执行。");
                    onlinePlanText = string.Join("\n", lines);
                    onlineText.Text = onlinePlanText;
                    openQryBtn.IsEnabled = true;
                    status.Text = "在线改表计划已生成（只读展示）。换名步为单语句原子操作；清理旧表前必须先跑数据对账。";
                }
                catch (Exception ex)
                {
                    status.Text = HumanError.Say(ex, "在线改表计划生成失败");
                }
            };
            openQryBtn.Click += (_, _) =>
            {
                if (onlinePlanText.Length > 0) owner.OpenQueryTab(info, onlinePlanText);
            };
        }
        else
        {
            onlineText.Text = "在线改表计划当前为 MySQL 方言，PG 连接下一批补齐；PG 分区迁移请用方案中的注释流程。";
        }

        var estRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        estRow.Children.Add(estBox);
        estRow.Children.Add(batchBox);
        estRow.Children.Add(pkBox);

        var panel = new StackPanel { Spacing = 12, MinWidth = 580 };
        panel.Children.Add(Head("四步向导：选表 → 选拆分方式 → 选分片键 → 确认方案"));
        panel.Children.Add(topStatus);   // 格13：状态行前置
        panel.Children.Add(stepChips);
        panel.Children.Add(stepPanels);
        panel.Children.Add(navRow);
        panel.Children.Add(Head("分区现状分布"));
        panel.Children.Add(distBtn);
        panel.Children.Add(new ScrollViewer { Content = distPanel, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(Head("热点盐化摘要"));
        panel.Children.Add(saltRow);
        panel.Children.Add(saltText);
        panel.Children.Add(Head("分片路由联动（接入读写分离）"));
        panel.Children.Add(linkCheck);
        panel.Children.Add(routeRow);
        panel.Children.Add(new ScrollViewer { Content = routeText, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(Head("在线改表计划（只读，@last 不可直接执行）"));
        panel.Children.Add(onlineBtn);
        if (profile.Engine == DbEngine.MySQL) panel.Children.Add(estRow);
        panel.Children.Add(new ScrollViewer { Content = onlineText, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (profile.Engine == DbEngine.MySQL) panel.Children.Add(openQryBtn);
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "分库分区分表向导",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
        await Task.CompletedTask;
    }

    /// <summary>把步骤内容包进 Border，用 Tag 记步骤号，ShowStep 按可见性切换。</summary>
    private static Border Wrap(UIElement content, int step) => new()
    {
        Tag = step,
        Visibility = step == StepTable ? Visibility.Visible : Visibility.Collapsed,
        Child = content,
    };

    /// <summary>发现307原型：候选分片键卡片——推荐键主色高亮，其余常规卡。</summary>
    private static Border KeyCard(string col, bool recommended)
    {
        var col2 = new StackPanel { Spacing = 2 };
        col2.Children.Add(new TextBlock { Text = col, FontSize = AntTokens.FontMinor, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        col2.Children.Add(new TextBlock
        {
            Text = recommended ? "★ 推荐（有索引依据）" : "有索引依据 ✓",
            FontSize = AntTokens.FontCaption,
            Foreground = recommended ? Ant.Primary : Ant.TextSub,
        });
        return new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
            BorderThickness = new Thickness(1),
            BorderBrush = recommended ? Ant.Primary : Ant.Border,
            Background = recommended ? (Brush?)Ant.PrimaryBg : (Brush?)Application.Current.Resources["AntCard"],
            Child = col2,
        };
    }

    /// <summary>取 ComboBox 选中项的 Tag（表名 / 列名都放 Tag）。</summary>
    private static string? Selected(ComboBox box) =>
        box.SelectedItem is ComboBoxItem item ? item.Tag?.ToString() : null;
}
