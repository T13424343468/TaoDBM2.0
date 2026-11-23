using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using TaoDBM.Core;
using Windows.Storage.Pickers;

namespace TaoDBM.UI;

/// <summary>备份 / 恢复四步向导（设计稿 §5）：① 选库/表 → ② 备份点 → ③ 目标 → ④ 执行确认。
/// 恢复属高危不可逆：影响范围预览、默认勾选恢复前自动备份、输入目标库名点亮开始恢复；演练入口接 BackupDrillDialog。</summary>
public static class BackupDialog
{
    private static readonly string[] StepNames = { "选库/表", "备份点", "目标", "执行确认" };

    /// <summary>info 须为连接节点（携带 Profile 与 Provider）。</summary>
    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        var profile = info.Profile;
        var prov = info.Provider ?? ProviderFactory.Create(profile);
        if (!BackupTools.Supported(profile.Engine))
        {
            await new MessageDialogHelper(owner).Show("备份 / 恢复", BackupTools.UnsupportedReason(profile.Engine));
            return;
        }

        List<string> dbs = new();
        try { dbs = await prov.GetDatabases(); }
        catch { dbs = new List<string> { profile.Database }; }

        bool[] done = new bool[4];
        var stepBar = new TextBlock { FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 10, MinWidth = 620 };
        ContentDialog? dlg = null;

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
            catch (Exception ex) { scopeInfo.Text = $"表清单读取失败：{ex.Message}（不影响备份 / 恢复执行）。"; }
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

        void ShowFileInfo()
        {
            string file = fileBox.Text.Trim();
            if (file.Length == 0) { fileInfo.Text = ""; return; }
            try
            {
                var fi = new FileInfo(file);
                fileInfo.Text = fi.Exists
                    ? $"备份点：时间 {fi.LastWriteTime:yyyy-MM-dd HH:mm} · 大小 {fi.Length / 1024.0:N1} KB"
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

        async void LoadImpact()
        {
            string db = targetBox.Text.Trim();
            if (db.Length == 0) { impactBox.Text = "请先填目标数据库。"; return; }
            impactBox.Text = "统计中…";
            try
            {
                var tables = await prov.GetChildren(db);
                if (tables.Count == 0) { impactBox.Text = $"目标库 {db} 当前没有表。"; return; }
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
            catch (Exception ex) { impactBox.Text = $"影响预览失败：{ex.Message}"; }
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
        drillBtn.Click += (_, _) => _ = BackupDrillDialog.Show(owner, info);
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
        var step4 = new StackPanel { Spacing = 8 };
        step4.Children.Add(confirmBox);
        step4.Children.Add(drillHint);
        step4.Children.Add(drillBtn);
        step4.Children.Add(execRow);
        step4.Children.Add(output);

        void RefreshConfirm()
        {
            bool restore = isRestore.IsChecked == true;
            startBtn.Content = restore ? "开始恢复" : "开始备份";
            confirmBox.Visibility = restore ? Visibility.Visible : Visibility.Collapsed;
            drillHint.Visibility = restore ? Visibility.Visible : Visibility.Collapsed;
            drillBtn.Visibility = restore ? Visibility.Visible : Visibility.Collapsed;
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
                        ? $"备份完成：{file}（{new FileInfo(file).Length / 1024.0:N1} KB）"
                        : $"退出码 {code}\n{text}";
                }
                case DbEngine.PostgreSQL:
                {
                    var (h, p) = TunnelResolver.Resolve(profile, profile.Host, profile.Port > 0 ? profile.Port : 5432);
                    var spec = BackupTools.PgDump(h, p, profile.User, profile.Password, db, file);
                    var (code, text) = await RunTool(spec, ct, progress);
                    return code == 0 && text.Trim().Length == 0
                        ? $"备份完成：{file}（{new FileInfo(file).Length / 1024.0:N1} KB）"
                        : $"退出码 {code}\n{text}";
                }
                case DbEngine.SqlServer:
                {
                    var sql = BackupTools.SqlServerBackupSql(db, file);
                    ct.ThrowIfCancellationRequested(); // 格10：T-SQL 执行前检查点
                    var list = await prov.Execute("", sql);
                    var err = list.FirstOrDefault(r => r.Message.StartsWith("错误", StringComparison.Ordinal));
                    return err is null ? $"备份完成：服务器已写入 {file}" : err.Message;
                }
                case DbEngine.SQLite:
                    // 格10：SQLite 备份后台化，取消走检查点
                    await Task.Run(() => { ct.ThrowIfCancellationRequested(); BackupTools.SqliteBackup(profile.Database, file); }, ct);
                    return $"备份完成：{profile.Database} → {file}";
                default:
                    return "当前向导支持 MySQL / PostgreSQL / SQL Server / SQLite；其他引擎请使用恢复演练或备份计划。";
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
                    var err = list.FirstOrDefault(r => r.Message.StartsWith("错误", StringComparison.Ordinal));
                    return err is null ? $"还原完成：{db} 已从 {file} 恢复" : err.Message;
                }
                case DbEngine.SQLite:
                    await Task.Run(() => { ct.ThrowIfCancellationRequested(); BackupTools.SqliteRestore(profile.Database, file); }, ct);
                    return $"还原完成：{file} → {profile.Database}";
                default:
                    return "当前向导支持 MySQL / PostgreSQL / SQL Server / SQLite；其他引擎请使用恢复演练或备份计划。";
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
            var progress = new Progress<int>(n => output.Text = $"执行中…（已读工具输出 {n} 行）");
            startBtn.IsEnabled = false;
            cancelBtn.IsEnabled = true;
            output.Text = "执行中…";
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
                AppLog.Write("[备份] cancel");
            }
            catch (Exception ex)
            {
                output.Text = (ex is System.ComponentModel.Win32Exception
                    ? "未找到命令行工具。请安装 MySQL / PostgreSQL 客户端工具并加入 PATH 后重试。\n"
                    : "") + "失败：" + ex.Message;
            }
            finally
            {
                cancelBtn.IsEnabled = false;
                RefreshConfirm();
            }
        }
        startBtn.Click += (_, _) => OnExecute();
        cancelBtn.Click += (_, _) => { output.Text = "取消中…"; liveCts?.Cancel(); };

        UpdateStepBar();
        RefreshConfirm();
        LoadScope();
        ShowStep(0);

        dlg = new ContentDialog
        {
            Title = "备份 / 恢复向导",
            Content = body,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        await dlg.ShowThemedAsync();
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
