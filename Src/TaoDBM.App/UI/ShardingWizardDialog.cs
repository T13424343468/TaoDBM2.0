using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>B11 图形化分库分区分表向导对话框：三步生成分片方案（预览+确认执行）、分区现状分布（倾斜标红）、
/// 热点盐化摘要与在线改表计划（只读展示，@last 占位符不可直接执行，引导到查询窗口逐段执行）。</summary>
public static class ShardingWizardDialog
{
    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        var profile = info.Profile;
        var prov = info.Provider ?? ProviderFactory.Create(profile);
        if (!ShardingWizard.Supported(profile.Engine))
        {
            await new MessageDialogHelper(owner).Show("分片向导", ShardingWizard.UnsupportedReason(profile.Engine));
            return;
        }

        var status = new TextBlock { FontSize = 12, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        // ── 块一：向导三步（表 / 分片键 / 分片数 2–16 → 生成方案 → 预览执行） ──
        var tableBox = new TextBox { PlaceholderText = "表名，如 orders", Width = 180 };
        var keyBox = new TextBox { PlaceholderText = "分片键列名，如 user_id", Width = 180 };
        var shardBox = new ComboBox { Width = 88, SelectedIndex = 2 };
        for (int i = 2; i <= 16; i++) shardBox.Items.Add(new ComboBoxItem { Content = i.ToString() });
        shardBox.SelectedIndex = 2; // 4 片
        var genBtn = new Button { Content = "生成分片方案", CornerRadius = new CornerRadius(4) };
        var planPanel = new StackPanel { Spacing = 4 };
        var execBtn = new Button { Content = "预览并执行方案", CornerRadius = new CornerRadius(4), IsEnabled = false };
        List<string> drafts = new();
        genBtn.Click += (_, _) =>
        {
            var t = tableBox.Text.Trim();
            var k = keyBox.Text.Trim();
            if (t.Length == 0 || k.Length == 0) { status.Text = "请先填写表名与分片键。"; return; }
            int n = shardBox.SelectedIndex + 2;
            try
            {
                drafts = ShardingWizard.HashPartitionSql(profile.Engine, t, k, n).ToList();
            }
            catch (Exception ex)
            {
                status.Text = "方案生成失败：" + ex.Message;
                return;
            }
            planPanel.Children.Clear();
            foreach (var d in drafts)
                planPanel.Children.Add(new TextBlock { Text = d, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
            execBtn.IsEnabled = true;
            status.Text = profile.Engine == DbEngine.PostgreSQL
                ? "PG 为声明式分区：方案含建新表 + 分区 + 迁移注释，执行前请逐条核对。"
                : "MySQL 为一条 ALTER HASH 分区 + 现状目录查询。";
        };
        execBtn.Click += async (_, _) =>
        {
            var snapshot = drafts.ToList();
            if (snapshot.Count == 0) return;
            await DraftOps.ConfirmExecuteAsync(owner, prov, "执行分片方案（草稿）", snapshot, status);
        };

        // ── 块二：分区现状分布（行数降序 + 倾斜标红） ──
        var dbBox = new TextBox { PlaceholderText = "库名", Text = profile.Database, Width = 180 };
        var distBtn = new Button { Content = "查询分区现状", CornerRadius = new CornerRadius(4) };
        var distPanel = new StackPanel { Spacing = 4 };
        distBtn.Click += async (_, _) =>
        {
            var t = tableBox.Text.Trim();
            var db = dbBox.Text.Trim();
            if (t.Length == 0) { status.Text = "请先填写表名。"; return; }
            if (db.Length == 0 && profile.Engine == DbEngine.MySQL) { status.Text = "MySQL 需要库名限定 information_schema 查询。"; return; }
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
                    var line = new TextBlock
                    {
                        Text = $"{p.Name}｜{p.Rows} 行",
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                    };
                    if (skewed && p.Rows == maxRows) line.Foreground = Ant.Error; // 跟主题走的警示红
                    distPanel.Children.Add(line);
                }
                status.Text = skewed
                    ? "分布倾斜：最大分片超过最小分片 1.5 倍，建议重新分片或对热点键盐化。"
                    : $"共 {sorted.Count} 个分区，分布均匀。";
            }
            catch (Exception ex)
            {
                status.Text = "分区现状查询失败：" + ex.Message;
            }
        };

        // ── 块三：热点盐化摘要（展示用） ──
        var saltKeyBox = new TextBox { PlaceholderText = "热点键，如 user_id", Width = 180 };
        var saltCountBox = new TextBox { Text = "8", Width = 88 };
        var saltBtn = new Button { Content = "生成盐化摘要", CornerRadius = new CornerRadius(4) };
        var saltText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        saltBtn.Click += (_, _) =>
        {
            var k = saltKeyBox.Text.Trim();
            if (k.Length == 0) { status.Text = "请先填写热点键。"; return; }
            if (!int.TryParse(saltCountBox.Text.Trim(), out int sc) || sc < 2 || sc > 64) { status.Text = "盐桶数需为 2–64 的整数。"; return; }
            saltText.Text = ShardingWizard.SaltingSummary(k, sc);
        };

        // ── 块四：在线改表计划（MySQL 方言，只读展示；@last 占位符不可直接执行） ──
        var estBox = new TextBox { PlaceholderText = "预估行数", Text = "1000000", Width = 130 };
        var batchBox = new TextBox { Text = "2000", Width = 90 };
        var pkBox = new TextBox { PlaceholderText = "回填主键列", Width = 130 };
        var onlineBtn = new Button { Content = "生成在线改表计划", CornerRadius = new CornerRadius(4) };
        var onlineText = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var openQryBtn = new Button { Content = "作为查询窗口打开", CornerRadius = new CornerRadius(4), IsEnabled = false };
        string onlinePlanText = "";
        if (profile.Engine == DbEngine.MySQL)
        {
            onlineBtn.Click += (_, _) =>
            {
                var t = tableBox.Text.Trim();
                var k = keyBox.Text.Trim();
                if (t.Length == 0 || k.Length == 0) { status.Text = "请先填写表名与分片键。"; return; }
                if (!long.TryParse(estBox.Text.Trim(), out long rows) || rows < 0) { status.Text = "预估行数需为非负整数。"; return; }
                if (!int.TryParse(batchBox.Text.Trim(), out int batch) || batch < 1) { status.Text = "批大小需为正整数。"; return; }
                int n = shardBox.SelectedIndex + 2;
                try
                {
                    var alterClause = $"PARTITION BY HASH (`{k.Replace("`", "")}`) PARTITIONS {n}";
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
                    status.Text = "在线改表计划生成失败：" + ex.Message;
                }
            };
            openQryBtn.Click += (_, _) =>
            {
                if (onlinePlanText.Length > 0) owner.OpenQueryTab(info, onlinePlanText);
            };
        }
        else
        {
            onlineText.Text = "在线改表计划当前为 MySQL 方言，PG 连接下一批补齐；PG 分区迁移请使用块一方案的注释流程。";
        }

        var estRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        estRow.Children.Add(estBox);
        estRow.Children.Add(batchBox);
        estRow.Children.Add(pkBox);

        var step1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        step1.Children.Add(tableBox);
        step1.Children.Add(keyBox);
        step1.Children.Add(shardBox);
        step1.Children.Add(genBtn);

        var saltRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        saltRow.Children.Add(saltKeyBox);
        saltRow.Children.Add(saltCountBox);
        saltRow.Children.Add(saltBtn);

        TextBlock Head(string s) => new()
        {
            Text = s,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 560 };
        panel.Children.Add(Head("① 分片方案（表 → 分片键 → 分片数）"));
        panel.Children.Add(step1);
        panel.Children.Add(execBtn);
        panel.Children.Add(new ScrollViewer { Content = planPanel, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(Head("② 分区现状分布"));
        panel.Children.Add(dbBox);
        panel.Children.Add(distBtn);
        panel.Children.Add(new ScrollViewer { Content = distPanel, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(Head("③ 热点盐化摘要"));
        panel.Children.Add(saltRow);
        panel.Children.Add(saltText);
        panel.Children.Add(Head("④ 在线改表计划（只读，@last 不可直接执行）"));
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
}
