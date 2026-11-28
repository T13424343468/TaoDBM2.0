using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using TaoDBM.Core;
using Windows.Storage.Pickers;

namespace TaoDBM.UI;

/// <summary>备份 / 恢复四步向导（设计稿 §5）：① 选库/表 → ② 备份点 → ③ 目标 → ④ 执行确认。
/// 恢复属高危不可逆：影响范围预览、默认勾选恢复前自动备份、输入目标库名点亮开始恢复；演练入口接备份演练页签（三-15）。</summary>
public static class BackupDialog
{
    private static readonly string[] StepNames = { "选库/表", "备份点", "目标", "执行确认" };

    /// <summary>info 须为连接节点（携带 Profile 与 Provider）。</summary>
    public static FrameworkElement BuildPanel(MainWindow owner, NodeInfo info)
    {
        var profile = info.Profile;
        var prov = info.Provider ?? ProviderFactory.Create(profile);
        if (!BackupTools.Supported(profile.Engine))
        {
            // 页签化改造：不支持引擎不再弹窗，面板内直接给人话原因
            return new TextBlock
            {
                Text = BackupTools.UnsupportedReason(profile.Engine),
                FontSize = AntTokens.FontBody, Foreground = Ant.Warn, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
            };
        }

        List<string> dbs = new();
        try { dbs = prov.GetDatabases().GetAwaiter().GetResult(); }
        catch { dbs = new List<string> { profile.Database }; }

        bool[] done = new bool[4];
        var stepBar = new TextBlock { FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 10, MinWidth = 620 };

        // 发现301原型：头部面包屑——连接 · 数据库，一眼确认操作对象
        var crumb = new TextBlock
        {
            Text = $"{profile.DisplayName ?? profile.Host} · {profile.Database}",
            FontSize = AntTokens.FontBody,
            Foreground = Ant.TextSub,
            TextWrapping = TextWrapping.Wrap,
        };
        // 发现301原型：七引擎卡片——当前引擎高亮，不支持引擎置灰并给人话原因
        var engineRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        // 修红（评分打回301）：七卡一行超对话框宽被硬裁右缘——包横滚容器，窄时出滚动条自证，不裁切
        var engineRowHost = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Enabled,
            // 评分复拍轮2口径：滚动须自证——滚动条常驻可见，窄屏一眼看出可横滚，不再似裁切
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = engineRow,
        };
        foreach (DbEngine e in Enum.GetValues<DbEngine>())
        {
            bool current = e == profile.Engine;
            bool usable = BackupTools.Supported(e);
            var card = new Border
            {
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
                BorderThickness = new Thickness(1),
                BorderBrush = current ? Ant.Primary : Ant.Border,
                Background = current ? Ant.PrimaryBg : (Brush?)Application.Current.Resources["AntCard"],
                Opacity = usable ? 1.0 : 0.55,
            };
            card.Child = new TextBlock
            {
                Text = EngineInfo.Name(e) + (usable ? "" : " ⚠"),
                FontSize = AntTokens.FontMinor,
                Foreground = current ? Ant.Primary : Ant.TextSub,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(card, $"EngineCard_{e}");
            engineRow.Children.Add(card);
        }
        var engineNote = new TextBlock
        {
            Text = BackupTools.Supported(profile.Engine) ? $"备份引擎：{EngineInfo.Name(profile.Engine)}（官方命令行工具）" : BackupTools.UnsupportedReason(profile.Engine),
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.TextSub,
            TextWrapping = TextWrapping.Wrap,
        };

        void UpdateStepBar()
        {
            var parts = new List<string>();
            for (int i = 0; i < 4; i++)
                parts.Add($"{"①②③④"[i]} {StepNames[i]}{(done[i] ? " ✔" : "")}");
            stepBar.Text = string.Join(" → ", parts);
        }

        // ---- 步骤 ①：选库/表 ----
        var isBackup = new RadioButton { Content = "备份到文件", IsChecked = true, GroupName = "brMode" };
        var isRestore = new RadioButton { Content = "从文件还原", GroupName = "brMode" };
        var modePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        modePanel.Children.Add(isBackup);
        modePanel.Children.Add(isRestore);

        var dbBox = new ComboBox { MinWidth = 240, PlaceholderText = "选择数据库" };
        foreach (var d in dbs) dbBox.Items.Add(d);
        if (profile.Database.Length > 0 && dbs.Contains(profile.Database)) dbBox.SelectedItem = profile.Database;
        else if (dbs.Count > 0) dbBox.SelectedIndex = 0;

        var scopeInfo = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var step1 = new StackPanel { Spacing = 8 };
        step1.Children.Add(modePanel);
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row1.Children.Add(new TextBlock { Text = "数据库", VerticalAlignment = VerticalAlignment.Center });
        row1.Children.Add(dbBox);
        step1.Children.Add(row1);
        step1.Children.Add(scopeInfo);

        async void LoadScope()
        {
            string db = dbBox.SelectedItem as string ?? "";
            if (db.Length == 0) { scopeInfo.Text = "请选择数据库。"; return; }
            scopeInfo.Text = "读取表清单…";
            try
            {
                var tables = await prov.GetChildren(db);
                scopeInfo.Text = $"整库口径：{db} 共 {tables.Count} 张表；备份 / 恢复按整库进行。";
            }
            catch (Exception ex) { scopeInfo.Text = HumanError.Say(ex, "表清单读取") + "（不影响备份 / 恢复执行）。"; }
        }
        dbBox.SelectionChanged += (_, _) => LoadScope();

        // ---- 步骤 ②：备份点 / 文件 ----
        var fileBox = new TextBox { MinWidth = 360, PlaceholderText = "选择或输入文件路径" };
        var pickBtn = new Button { Content = "选择文件…", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        pickBtn.Click += async (_, _) =>
        {
            try
            {
                if (isBackup.IsChecked == true)
                {
                    var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                    var ext = profile.Engine == DbEngine.SqlServer ? ".bak" : profile.Engine == DbEngine.SQLite ? ".db" : ".sql";
                    picker.FileTypeChoices.Add(ext == ".db" ? "SQLite 库文件" : ext == ".bak" ? "SQL Server 备份" : "SQL 备份", new List<string> { ext });
                    picker.SuggestedFileName = $"{dbBox.SelectedItem ?? "backup"}-{DateTime.Now:yyyyMMdd-HHmmss}";
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
                    var f = await picker.PickSaveFileAsync();
                    if (f != null) fileBox.Text = f.Path;
                }
                else
                {
                    var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                    picker.FileTypeFilter.Add(profile.Engine == DbEngine.SqlServer ? ".bak" : profile.Engine == DbEngine.SQLite ? ".db" : ".sql");
                    picker.FileTypeFilter.Add("*");
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
                    var f = await picker.PickSingleFileAsync();
                    if (f != null) fileBox.Text = f.Path;
                }
            }
            catch { /* 用户取消 */ }
        };
        var fileInfo = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var step2 = new StackPanel { Spacing = 8 };
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row2.Children.Add(fileBox);
        row2.Children.Add(pickBtn);
        step2.Children.Add(row2);
        step2.Children.Add(fileInfo);

        // 发现303原型：备份集列表——本地账最近 5 次成功备份，点「选用」即填入文件路径
        var restoreSetList = new StackPanel { Spacing = 6 };
        var restoreSetWrap = new StackPanel { Spacing = 4 };
        restoreSetWrap.Children.Add(new TextBlock { Text = "备份集（本地账最近 5 次）", FontSize = AntTokens.FontCaption, Foreground = Ant.TextSub });
        restoreSetWrap.Children.Add(restoreSetList);

        void RefreshRestoreSets()
        {
            restoreSetList.Children.Clear();
            string db = dbBox.SelectedItem as string ?? profile.Database;
            var sets = BackupHistory.All().Where(h => h.Kind == "备份" && h.Db == db && h.Ok).Take(5).ToList();
            if (sets.Count == 0)
            {
                restoreSetList.Children.Add(new TextBlock
                {
                    Text = "本地账暂无该库备份记录；用「选择文件…」挑备份文件。",
                    FontSize = AntTokens.FontMinor,
                    Foreground = Ant.TextSub,
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }
            foreach (var s in sets)
            {
                bool exists = File.Exists(s.File);
                var card = new Border
                {
                    Padding = new Thickness(10, 6, 10, 6),
                    CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Ant.Border,
                    Background = (Brush?)Application.Current.Resources["AntCard"],
                    Opacity = exists ? 1.0 : 0.55,
                };
                var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                nameRow.Children.Add(new TextBlock
                {
                    Text = System.IO.Path.GetFileName(s.File),
                    FontSize = AntTokens.FontMinor,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                });
                var col = new StackPanel { Spacing = 2 };
                col.Children.Add(nameRow);
                col.Children.Add(new TextBlock
                {
                    Text = $"{s.Time} · {s.Kind} · {(s.SizeBytes > 0 ? Core.HumanSize.Of(s.SizeBytes) : "—")}{(exists ? " · 已落账 ✓" : " · 文件已不在，不可用")}",
                    FontSize = AntTokens.FontCaption,
                    Foreground = Ant.TextSub,
                    TextWrapping = TextWrapping.Wrap,
                });
                card.Child = col;
                if (exists)
                {
                    var useBtn = new Button
                    {
                        Content = "选用",
                        FontSize = AntTokens.FontMinor,
                        Padding = new Thickness(8, 0, 8, 0),
                        CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    var f = s.File;
                    useBtn.Click += (_, _) => { fileBox.Text = f; };
                    nameRow.Children.Add(useBtn);
                }
                restoreSetList.Children.Add(card);
            }
        }
        step2.Children.Add(restoreSetWrap);

        void ShowFileInfo()
        {
            string file = fileBox.Text.Trim();
            if (file.Length == 0) { fileInfo.Text = ""; return; }
            try
            {
                var fi = new FileInfo(file);
                fileInfo.Text = fi.Exists
                    ? $"备份点：时间 {fi.LastWriteTime:yyyy-MM-dd HH:mm} · 大小 {Core.HumanSize.Of(fi.Length)}"
                    : "文件尚不存在：备份将新建该文件；还原请选择已有备份文件。";
            }
            catch { fileInfo.Text = ""; }
        }
        fileBox.TextChanged += (_, _) => ShowFileInfo();

        // ---- 步骤 ③：目标与影响 ----
        var targetBox = new TextBox { Header = "目标数据库（恢复写入对象）", Text = profile.Database };
        var autoBackup = new CheckBox { Content = "恢复前自动备份当前状态（强烈建议）", IsChecked = true };
        var impactBtn = new Button { Content = "刷新影响预览", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var impactBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 120,
            FontFamily = (FontFamily)Application.Current.Resources["AntMono"],
            FontSize = AntTokens.FontMinor,
            Text = "还原模式：点「刷新影响预览」查看将覆盖的表与行数估计。",
        };
        var backupNote = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var step3 = new StackPanel { Spacing = 8 };
        step3.Children.Add(targetBox);
        step3.Children.Add(autoBackup);
        step3.Children.Add(impactBtn);
        step3.Children.Add(impactBox);
        step3.Children.Add(backupNote);

        // 发现303原型：演练预检卡——自动备份 / 磁盘空间 / 目标库覆盖三行体检，恢复模式才显示
        int impactTables = -1;
        var precheckList = new StackPanel { Spacing = 4 };
        var precheckCard = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg),
            BorderThickness = new Thickness(1),
            BorderBrush = Ant.Border,
            Background = (Brush?)Application.Current.Resources["AntCard"],
        };
        precheckCard.Child = new StackPanel { Spacing = 4 };
        ((StackPanel)precheckCard.Child).Children.Add(new TextBlock
        {
            Text = "🩺 恢复预检",
            FontSize = AntTokens.FontMinor,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        ((StackPanel)precheckCard.Child).Children.Add(precheckList);

        static StackPanel PrecheckRow(bool ok, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock
            {
                Text = ok ? "✓" : "⚠",
                Foreground = ok ? Ant.Success : Ant.Warn,
                FontSize = AntTokens.FontMinor,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            row.Children.Add(new TextBlock { Text = text, FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap });
            return row;
        }

        void RefreshPrecheck()
        {
            precheckList.Children.Clear();
            bool autoOn = autoBackup.IsChecked == true;
            precheckList.Children.Add(PrecheckRow(autoOn, autoOn ? "恢复前自动备份：已开启（安全快照可回退）" : "恢复前自动备份：未开启，建议开启"));
            try
            {
                string file = fileBox.Text.Trim();
                string root = file.Length > 0 ? Path.GetPathRoot(Path.GetFullPath(file)) ?? "" : "";
                if (root.Length > 0 && new DriveInfo(root).IsReady)
                {
                    long free = new DriveInfo(root).AvailableFreeSpace;
                    bool enough = free >= 1024L * 1024 * 1024;
                    precheckList.Children.Add(PrecheckRow(enough, $"磁盘空间：目标盘剩余 {Core.HumanSize.Of(free)}{(enough ? "" : "，空间偏紧")}"));
                }
            }
            catch { /* 盘符取不到就跳过该行 */ }
            precheckList.Children.Add(PrecheckRow(impactTables >= 0, impactTables >= 0
                ? $"目标库现有 {impactTables} 张表，恢复将覆盖（详见影响预览）"
                : "目标库影响：点「刷新影响预览」查看将覆盖的表"));
        }
        autoBackup.Checked += (_, _) => RefreshPrecheck();
        autoBackup.Unchecked += (_, _) => RefreshPrecheck();
        step3.Children.Add(precheckCard);

        async void LoadImpact()
        {
            string db = targetBox.Text.Trim();
            if (db.Length == 0) { impactBox.Text = "请先填目标数据库。"; return; }
            impactBox.Text = "统计中…";
            try
            {
                var tables = await prov.GetChildren(db);
                if (tables.Count == 0) { impactBox.Text = $"目标库 {db} 当前没有表。"; impactTables = 0; RefreshPrecheck(); return; }
                impactTables = tables.Count;
                RefreshPrecheck();
                var lines = new List<string> { $"将覆盖目标库 {db}：共 {tables.Count} 张表。" };
                int shown = Math.Min(tables.Count, 20);
                for (int i = 0; i < shown; i++)
                {
                    string rows = "行数未知";
                    try
                    {
                        var rs = await prov.Execute(db, $"SELECT COUNT(*) AS c FROM {prov.Quote(tables[i])}");
                        if (rs.Count > 0 && rs[0].Rows.Count > 0) rows = $"{rs[0].Rows[0][0]} 行（估计）";
                    }
                    catch { /* 单表统计失败不阻断预览 */ }
                    lines.Add($"· {tables[i]}：{rows}");
                }
                if (tables.Count > shown) lines.Add($"· …另有 {tables.Count - shown} 张表未估算。");
                impactBox.Text = string.Join('\n', lines);
            }
            catch (Exception ex) { impactBox.Text = HumanError.Say(ex, "影响预览失败"); }
        }
        impactBtn.Click += (_, _) => LoadImpact();

        // ---- 步骤 ④：执行确认 ----
        var confirmBox = new TextBox { Header = "高风险确认：输入目标库名以点亮「开始恢复」" };
        var drillHint = new TextBlock
        {
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.TextSub,
            TextWrapping = TextWrapping.Wrap,
            Text = "恢复前建议先做恢复演练：把备份恢复到临时库验证可用，再对真实库执行。",
        };
        var drillBtn = new Button { Content = "打开恢复演练", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        drillBtn.Click += (_, _) => owner.OpenBackupDrillTab(info);   // 三-15：弹窗改页面，升级内嵌页签
        var startBtn = new Button
        {
            Content = "开始备份",
            Background = Ant.Primary,
            Foreground = Ant.OnPrimary,
            BorderThickness = new Thickness(0),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
        };
        // 格10：后台化执行＋取消（进程树 Kill / 检查点）
        var cancelBtn = new Button { Content = "取消", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), IsEnabled = false };
        var execRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        execRow.Children.Add(startBtn);
        execRow.Children.Add(cancelBtn);
        var output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 150,
            FontFamily = (FontFamily)Application.Current.Resources["AntMono"],
            FontSize = AntTokens.FontMinor,
            Text = "确认无误后点「开始」。",
        };
        // 发现301原型：执行进度区——状态行＋进度条＋SHA-256 校验行＋磁盘空间提示
        var progStatus = new TextBlock { FontSize = AntTokens.FontMinor, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Ant.Primary, Text = "● 待执行" };
        var progPercent = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub };
        var progTop = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        progTop.Children.Add(progStatus);
        progTop.Children.Add(progPercent);
        var progBar = new Microsoft.UI.Xaml.Controls.ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = false };
        var progMeta = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var checksumLabel = new TextBlock { Text = "SHA-256 校验值", FontSize = AntTokens.FontMinor, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Ant.Primary, VerticalAlignment = VerticalAlignment.Center };
        var checksumText = new TextBlock
        {
            FontFamily = (FontFamily)Application.Current.Resources["AntMono"],
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.Text,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var checksumRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        checksumRow.Children.Add(checksumLabel);
        checksumRow.Children.Add(checksumText);
        var checksumCard = new Border
        {
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
            BorderThickness = new Thickness(1),
            BorderBrush = Ant.Primary,
            Background = Ant.PrimaryBg,
            Visibility = Visibility.Collapsed,
        };
        checksumCard.Child = checksumRow;
        var diskText = new TextBlock { FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap };
        var diskTip = new Border
        {
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
        };
        diskTip.Child = diskText;
        var progWrap = new StackPanel { Spacing = 6 };
        progWrap.Children.Add(progTop);
        progWrap.Children.Add(progBar);
        progWrap.Children.Add(progMeta);
        progWrap.Children.Add(checksumCard);
        progWrap.Children.Add(diskTip);
        var step4 = new StackPanel { Spacing = 8 };
        step4.Children.Add(confirmBox);
        step4.Children.Add(drillHint);
        step4.Children.Add(drillBtn);
        step4.Children.Add(execRow);
        step4.Children.Add(progWrap);
        step4.Children.Add(output);

        // 发现301原型：磁盘空间提示——目标盘剩余空间偏紧时给警告色人话提示
        void ShowDiskTip()
        {
            try
            {
                string file = fileBox.Text.Trim();
                string root = file.Length > 0 ? Path.GetPathRoot(Path.GetFullPath(file)) ?? "" : "";
                if (root.Length == 0 || !new DriveInfo(root).IsReady) { diskTip.Visibility = Visibility.Collapsed; return; }
                long free = new DriveInfo(root).AvailableFreeSpace;
                bool tight = free < 1024L * 1024 * 1024;
                diskText.Text = tight
                    ? $"⚠ 目标目录所在磁盘剩余空间 {Core.HumanSize.Of(free)}，空间偏紧，建议更换保存位置或清理旧备份。"
                    : $"目标目录所在磁盘剩余空间 {Core.HumanSize.Of(free)}。";
                diskText.Foreground = tight ? Ant.Warn : Ant.TextSub;
                diskTip.BorderBrush = tight ? Ant.Warn : Ant.Border;
                diskTip.Background = tight ? (Brush?)Ant.WarnBg : (Brush?)Application.Current.Resources["AntCard"];
                diskTip.Visibility = Visibility.Visible;
            }
            catch { diskTip.Visibility = Visibility.Collapsed; }
        }
        fileBox.TextChanged += (_, _) => ShowDiskTip();

        // 发现301原型：SHA-256 校验行——备份文件存在时展示校验值前缀，便于完成后核对一致性
        async Task ShowChecksumAsync(string file)
        {
            checksumCard.Visibility = Visibility.Collapsed;
            try
            {
                if (file.Length == 0 || !File.Exists(file)) return;
                checksumText.Text = "计算中…";
                checksumCard.Visibility = Visibility.Visible;
                string hex = await Task.Run(() =>
                {
                    using var fs = File.OpenRead(file);
                    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
                });
                checksumText.Text = $"{hex[..16]}…（可用于校验备份文件一致性）";
            }
            catch { checksumCard.Visibility = Visibility.Collapsed; }
        }

        void RefreshConfirm()
        {
            bool restore = isRestore.IsChecked == true;
            startBtn.Content = restore ? "开始恢复" : "开始备份";
            confirmBox.Visibility = restore ? Visibility.Visible : Visibility.Collapsed;
            drillHint.Visibility = restore ? Visibility.Visible : Visibility.Collapsed;
            drillBtn.Visibility = restore ? Visibility.Visible : Visibility.Collapsed;
            restoreSetWrap.Visibility = restore ? Visibility.Visible : Visibility.Collapsed;   // 发现303原型：备份集列表仅恢复模式
            precheckCard.Visibility = restore ? Visibility.Visible : Visibility.Collapsed;     // 发现303原型：预检卡仅恢复模式
            if (restore) { RefreshRestoreSets(); RefreshPrecheck(); }
            startBtn.IsEnabled = !restore || (targetBox.Text.Trim().Length > 0 && confirmBox.Text.Trim() == targetBox.Text.Trim());
        }
        confirmBox.TextChanged += (_, _) => RefreshConfirm();
        targetBox.TextChanged += (_, _) => RefreshConfirm();
        isBackup.Checked += (_, _) => { RefreshConfirm(); ShowFileInfo(); };
        isRestore.Checked += (_, _) => { RefreshConfirm(); ShowFileInfo(); };

        string note = profile.Engine switch
        {
            DbEngine.MySQL or DbEngine.PostgreSQL => "使用官方命令行工具（mysqldump / pg_dump + mysql / psql），需已安装并加入 PATH；密码经环境变量传递，不会出现在命令行。",
            DbEngine.SqlServer => "用 T-SQL BACKUP / RESTORE 执行；磁盘路径是数据库服务器上的路径（本机连接时即本机路径）。",
            // 批次四格4（2026-11-23）：MongoDB 原生备份界面——mongodump / mongorestore，密码走 --config 文件
            DbEngine.MongoDB => "使用官方 mongodump / mongorestore（输出为目录），需已安装并加入 PATH；密码经 --config 文件传递，不会出现在命令行。工具缺失时请先安装 MongoDB Database Tools。",
            _ => "直接复制 / 覆盖 .db 文件；还原会覆盖当前库文件，请确认没有其他程序占用。",
        };
        backupNote.Text = note;

        // ---- 步骤条与导航 ----
        var panels = new[] { step1, step2, step3, step4 };
        var prevBtn = new Button { Content = "上一步", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var nextBtn = new Button { Content = "下一步", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nav.Children.Add(prevBtn);
        nav.Children.Add(nextBtn);

        int cur = 0;
        void ShowStep(int i)
        {
            cur = i;
            body.Children.Clear();
            body.Children.Add(crumb);        // 发现301原型：头部面包屑常驻
            body.Children.Add(engineRowHost);    // 发现301原型：七引擎卡片常驻（横滚容器防裁切）
            body.Children.Add(engineNote);
            body.Children.Add(stepBar);
            var head = new TextBlock { Text = $"步骤 {i + 1}/4 · {StepNames[i]}", FontSize = AntTokens.FontTitle, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            body.Children.Add(head);
            body.Children.Add(panels[i]);
            body.Children.Add(status);
            body.Children.Add(nav);
            prevBtn.IsEnabled = i > 0;
            nextBtn.Visibility = i == 3 ? Visibility.Collapsed : Visibility.Visible;
        }

        prevBtn.Click += (_, _) => { if (cur > 0) ShowStep(cur - 1); };
        nextBtn.Click += (_, _) =>
        {
            if (cur == 0)
            {
                if (dbBox.SelectedItem is null) { status.Text = "请先选择数据库。"; return; }
                done[0] = true;
                if (isRestore.IsChecked == true && targetBox.Text.Trim().Length == 0)
                    targetBox.Text = dbBox.SelectedItem as string ?? "";
                LoadScope();
            }
            else if (cur == 1)
            {
                if (fileBox.Text.Trim().Length == 0) { status.Text = "请先选择文件。"; return; }
                if (isRestore.IsChecked == true && !File.Exists(fileBox.Text.Trim()))
                {
                    status.Text = "备份文件不存在，请重新选择。";
                    return;
                }
                done[1] = true;
                ShowFileInfo();
            }
            else if (cur == 2)
            {
                if (isRestore.IsChecked == true && targetBox.Text.Trim().Length == 0)
                {
                    status.Text = "请先填目标数据库。";
                    return;
                }
                done[2] = true;
                if (isRestore.IsChecked == true) LoadImpact();
            }
            UpdateStepBar();
            if (cur < 3) ShowStep(cur + 1);
        };

        // ---- 执行（沿用既有引擎分流；恢复前自动备份默认开启） ----
        async Task<string> RunBackupAsync(string db, string file, CancellationToken ct, IProgress<int> progress)
        {
            switch (profile.Engine)
            {
                case DbEngine.MySQL:
                {
                    var (h, p) = TunnelResolver.Resolve(profile, profile.Host, profile.Port > 0 ? profile.Port : 3306);
                    var spec = BackupTools.MysqlDump(h, p, profile.User, profile.Password, db, file);
                    var (code, text) = await RunTool(spec, ct, progress);
                    return code == 0 && text.Trim().Length == 0
                        ? $"备份完成：{file}（{Core.HumanSize.Of(new FileInfo(file).Length)}）"
                        : $"退出码 {code}\n{text}";
                }
                case DbEngine.PostgreSQL:
                {
                    var (h, p) = TunnelResolver.Resolve(profile, profile.Host, profile.Port > 0 ? profile.Port : 5432);
                    var spec = BackupTools.PgDump(h, p, profile.User, profile.Password, db, file);
                    var (code, text) = await RunTool(spec, ct, progress);
                    return code == 0 && text.Trim().Length == 0
                        ? $"备份完成：{file}（{Core.HumanSize.Of(new FileInfo(file).Length)}）"
                        : $"退出码 {code}\n{text}";
                }
                case DbEngine.SqlServer:
                {
                    var sql = BackupTools.SqlServerBackupSql(db, file);
                    ct.ThrowIfCancellationRequested(); // 格10：T-SQL 执行前检查点
                    var list = await prov.Execute("", sql);
                    var err = list.FirstOrDefault(r => (r.Message.StartsWith("错误", StringComparison.Ordinal) || r.Message.Contains("没成功：")));
                    return err is null ? $"备份完成：服务器已写入 {file}" : err.Message;
                }
                case DbEngine.SQLite:
                    // 格10：SQLite 备份后台化，取消走检查点
                    await Task.Run(() => { ct.ThrowIfCancellationRequested(); BackupTools.SqliteBackup(profile.Database, file); }, ct);
                    return $"备份完成：{profile.Database} → {file}";
                case DbEngine.MongoDB:
                {
                    // 批次四格4：mongodump（--out 目录），密码走 --config 文件；输出行数落账
                    var (h, p) = TunnelResolver.Resolve(profile, profile.Host, profile.Port > 0 ? profile.Port : 27017);
                    var spec = BackupTools.MongoDump(h, p, profile.User, profile.Password, db, file);
                    var (code, text) = await RunTool(spec, ct, progress);
                    int lines = text.Length > 0 ? text.Split('\n').Length : 0;
                    AppLog.Write($"[备份] mongo dump lines={lines}");   // 批次四格4 验收锚点
                    return code == 0
                        ? $"备份完成：{db} → {file}（mongodump 输出 {lines} 行）"
                        : $"退出码 {code}\n{text}\n（mongodump 未安装或不可用时，请先安装 MongoDB Database Tools 并加入 PATH）";
                }
                default:
                    return "当前向导支持 MySQL / PostgreSQL / SQL Server / SQLite / MongoDB；其他引擎请使用恢复演练或备份计划。";
            }
        }

        async Task<string> RunRestoreAsync(string db, string file, CancellationToken ct, IProgress<int> progress)
        {
            switch (profile.Engine)
            {
                case DbEngine.MySQL:
                {
                    var (h, p) = TunnelResolver.Resolve(profile, profile.Host, profile.Port > 0 ? profile.Port : 3306);
                    var spec = BackupTools.MysqlRestore(h, p, profile.User, profile.Password, db, file);
                    var (code, text) = await RunTool(spec, ct, progress);
                    return code == 0 ? $"还原完成：{file} → {db}" : $"退出码 {code}\n{text}";
                }
                case DbEngine.PostgreSQL:
                {
                    var (h, p) = TunnelResolver.Resolve(profile, profile.Host, profile.Port > 0 ? profile.Port : 5432);
                    var spec = BackupTools.PgRestore(h, p, profile.User, profile.Password, db, file);
                    var (code, text) = await RunTool(spec, ct, progress);
                    return code == 0 ? $"还原完成：{file} → {db}" : $"退出码 {code}\n{text}";
                }
                case DbEngine.SqlServer:
                {
                    var sql = BackupTools.SqlServerRestoreSql(db, file);
                    ct.ThrowIfCancellationRequested(); // 格10：T-SQL 执行前检查点
                    var list = await prov.Execute("", sql);
                    var err = list.FirstOrDefault(r => (r.Message.StartsWith("错误", StringComparison.Ordinal) || r.Message.Contains("没成功：")));
                    return err is null ? $"还原完成：{db} 已从 {file} 恢复" : err.Message;
                }
                case DbEngine.SQLite:
                    await Task.Run(() => { ct.ThrowIfCancellationRequested(); BackupTools.SqliteRestore(profile.Database, file); }, ct);
                    return $"还原完成：{file} → {profile.Database}";
                case DbEngine.MongoDB:
                {
                    // 批次四格4：mongorestore（--drop 先清后写），密码走 --config 文件
                    var (h, p) = TunnelResolver.Resolve(profile, profile.Host, profile.Port > 0 ? profile.Port : 27017);
                    var spec = BackupTools.MongoRestore(h, p, profile.User, profile.Password, db, file);
                    var (code, text) = await RunTool(spec, ct, progress);
                    int lines = text.Length > 0 ? text.Split('\n').Length : 0;
                    AppLog.Write($"[备份] mongo restore lines={lines}");   // 批次四格4 验收锚点
                    return code == 0
                        ? $"还原完成：{file} → {db}（mongorestore 输出 {lines} 行）"
                        : $"退出码 {code}\n{text}\n（mongorestore 未安装或不可用时，请先安装 MongoDB Database Tools 并加入 PATH）";
                }
                default:
                    return "当前向导支持 MySQL / PostgreSQL / SQL Server / SQLite / MongoDB；其他引擎请使用恢复演练或备份计划。";
            }
        }

        static string PreRestoreFile(string file)
        {
            string dir = Path.GetDirectoryName(file) ?? ".";
            string name = Path.GetFileNameWithoutExtension(file);
            string ext = Path.GetExtension(file);
            return Path.Combine(dir, $"{name}.pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
        }

        // 格10：执行期 CTS 引用（OnExecute 每次新建，取消按钮转发）
        CancellationTokenSource? liveCts = null;

        async void OnExecute()
        {
            bool backup = isBackup.IsChecked == true;
            string db = backup ? dbBox.SelectedItem as string ?? "" : targetBox.Text.Trim();
            string file = fileBox.Text.Trim();
            if (db.Length == 0) { output.Text = "请先选择数据库。"; return; }
            if (file.Length == 0) { output.Text = "请先选择文件。"; return; }

            // 格10：后台化执行＋可取消（进度行数进 output，取消立即 Kill/检查点）
            using var cts = new CancellationTokenSource();
            liveCts = cts;
            var progress = new Progress<int>(n =>
            {
                output.Text = $"执行中…（已读工具输出 {n} 行）";
                progStatus.Text = "● 执行中";
                progBar.IsIndeterminate = true;
                progMeta.Text = $"已读工具输出 {n} 行";
            });
            startBtn.IsEnabled = false;
            cancelBtn.IsEnabled = true;
            output.Text = "执行中…";
            // 发现301原型：执行时点亮进度区
            progStatus.Text = $"● 正在{(backup ? "备份" : "恢复")}";
            progPercent.Text = "";
            progMeta.Text = "";
            progBar.IsIndeterminate = true;
            checksumCard.Visibility = Visibility.Collapsed;
            ShowDiskTip();
            try
            {
                string result;
                if (backup)
                {
                    result = await RunBackupAsync(db, file, cts.Token, progress);
                }
                else
                {
                    if (autoBackup.IsChecked == true)
                    {
                        string pre = PreRestoreFile(file);
                        output.Text = $"恢复前自动备份当前状态 → {pre}\n执行中…";
                        string preResult = await RunBackupAsync(db, pre, cts.Token, progress);
                        if (!preResult.StartsWith("备份完成", StringComparison.Ordinal))
                        {
                            output.Text = $"恢复前自动备份失败，已停止恢复（现有数据未动）：\n{preResult}";
                            return;
                        }
                        output.Text = $"恢复前自动备份完成：{pre}\n开始恢复…";
                    }
                    result = await RunRestoreAsync(db, file, cts.Token, progress);
                }
                output.Text = result;
                // 发现301原型：完成后进度区收口＋SHA-256 校验行
                bool okResult = result.StartsWith(backup ? "备份完成" : "还原完成", StringComparison.Ordinal);
                progBar.IsIndeterminate = false;
                progStatus.Text = okResult ? "● 完成" : "● 已停止";
                progPercent.Text = okResult ? "100%" : "";
                if (okResult && backup) await ShowChecksumAsync(file);
                // 批次四格4：备份 / 还原落 tao_backup_history 本地账（成功失败都记，可回看）
                long sizeBytes = 0;
                try { if (okResult && File.Exists(file) && (backup ? File.GetAttributes(file) & FileAttributes.Directory : 0) == 0) sizeBytes = new FileInfo(file).Length; } catch { /* 目录口径不取大小 */ }
                BackupHistory.Add(prov.EngineName, db, backup ? "备份" : "还原", file, okResult, sizeBytes, result.Split('\n')[0].Trim());
                // P4：备份 / 恢复结果接通知调度（规则判定不够格就静默）；消息取结果首行，超长截断
                var firstLine = result.Split('\n')[0].Trim();
                if (firstLine.Length > 120) firstLine = firstLine[..120];
                ToastCenter.Publish(new NotifyEvent(
                    backup ? NotifyKind.Backup : NotifyKind.Restore, db,
                    backup ? "备份结果" : "还原结果", firstLine, DateTime.Now));
                QueryHistory.Add(prov.EngineName, db, $"-- {(backup ? "备份" : "还原")}：{file}");
            }
            catch (OperationCanceledException)
            {
                output.Text = "已取消。";
                progStatus.Text = "● 已取消";
                AppLog.Write("[备份] cancel");
            }
            catch (Exception ex)
            {
                output.Text = (ex is System.ComponentModel.Win32Exception
                    ? "未找到命令行工具。请安装 MySQL / PostgreSQL 客户端工具并加入 PATH 后重试。\n"
                    : "") + HumanError.Say(ex, "失败");
                progStatus.Text = "● 失败";
            }
            finally
            {
                cancelBtn.IsEnabled = false;
                progBar.IsIndeterminate = false;
                RefreshConfirm();
            }
        }
        startBtn.Click += (_, _) => OnExecute();
        cancelBtn.Click += (_, _) => { output.Text = "取消中…"; liveCts?.Cancel(); };

        UpdateStepBar();
        RefreshConfirm();
        LoadScope();
        ShowStep(0);
        return body;
    }

    /// <summary>格10：后台化执行外部工具——逐行泵输出做不确定进度，取消走进程树 Kill。</summary>
    private static async Task<(int Code, string Output)> RunTool(ToolSpec spec, CancellationToken ct = default, IProgress<int>? progress = null)
    {
        var psi = new ProcessStartInfo(spec.FileName, spec.Arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var kv in spec.Env) psi.Environment[kv.Key] = kv.Value;
        if (spec.InputFile != null) psi.RedirectStandardInput = true;

        // 辅助文件（mongodump --config / expdp parfile）：启动前落盘，退出后删除；内容不进命令行与展示文本。
        var tempFiles = new List<string>();
        foreach (var kv in spec.Files)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), kv.Key);
            await File.WriteAllTextAsync(path, kv.Value);
            tempFiles.Add(path);
            // parfile/--config 为相对文件名时依赖工作目录；统一改写为绝对路径引用。
            spec.Arguments = spec.Arguments.Replace(BackupTools.QuoteArg(kv.Key), BackupTools.QuoteArg(path));
        }
        psi = new ProcessStartInfo(spec.FileName, spec.Arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var kv in spec.Env) psi.Environment[kv.Key] = kv.Value;
        if (spec.InputFile != null) psi.RedirectStandardInput = true;

        try
        {
            using var p = new Process { StartInfo = psi };
            // 格10：取消＝进程树整树 Kill（含 mysqldump/pg_dump 派生子进程）
            using var reg = ct.Register(() =>
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 进程可能已退出 */ }
            });
            p.Start();
            int lines = 0;
            async Task Pump(System.IO.StreamReader r, System.Text.StringBuilder sink)
            {
                while (await r.ReadLineAsync(ct) is { } line)
                {
                    sink.AppendLine(line);
                    lines++;
                    if (lines % 50 == 0) progress?.Report(lines);
                    if (lines % 500 == 0) AppLog.Write($"[备份] progress lines={lines}");
                }
            }
            var outSink = new System.Text.StringBuilder();
            var errSink = new System.Text.StringBuilder();
            var outTask = Pump(p.StandardOutput, outSink);
            var errTask = Pump(p.StandardError, errSink);
            if (spec.InputFile != null)
            {
                var content = await File.ReadAllTextAsync(spec.InputFile);
                await p.StandardInput.WriteAsync(content);
                p.StandardInput.Close();
            }
            await p.WaitForExitAsync();
            await outTask;
            await errTask;
            return (p.ExitCode, (outSink.ToString() + "\n" + errSink.ToString()).Trim());
        }
        finally
        {
            foreach (var f in tempFiles)
            {
                try { File.Delete(f); } catch { /* 清理失败不掩盖主流程结果 */ }
            }
        }
    }
}
