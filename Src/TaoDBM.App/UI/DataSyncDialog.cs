using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>数据传输与结构同步：源连接/库/表 → 目标连接/库，可选补结构、传数据。</summary>
public static class DataSyncDialog
{
    public static FrameworkElement BuildPanel(MainWindow owner, List<ConnectionProfile> profiles, ConnectionProfile? preselect)
    {
        var relational = profiles.Where(p => p.Engine is not (DbEngine.MongoDB or DbEngine.Redis)).ToList();
        if (relational.Count < 2)
        {
            // 页签化改造：连接不足不再弹窗，面板内直接给指引
            return new TextBlock
            {
                Text = "需要至少两个关系型连接（MySQL / PostgreSQL / SQL Server / SQLite / Oracle）。请先在左侧新建连接。",
                FontSize = AntTokens.FontBody, Foreground = Ant.Warn, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
            };
        }

        var srcBox = new ComboBox { MinWidth = 260, PlaceholderText = "源连接" };
        var dstBox = new ComboBox { MinWidth = 260, PlaceholderText = "目标连接" };
        foreach (var p in relational)
        {
            srcBox.Items.Add(p);
            dstBox.Items.Add(p);
        }
        if (preselect is not null && relational.Contains(preselect)) srcBox.SelectedItem = preselect;

        var srcDbBox = new ComboBox { MinWidth = 200, PlaceholderText = "源库" };
        var dstDbBox = new ComboBox { MinWidth = 200, PlaceholderText = "目标库" };
        async Task LoadDbs(ComboBox box, ComboBox dbBox, TextBox outBox)
        {
            dbBox.Items.Clear();
            var p = box.SelectedItem as ConnectionProfile;
            if (p is null) return;
            try
            {
                using var prov = ProviderFactory.Create(p);
                foreach (var d in await prov.GetDatabases()) dbBox.Items.Add(d);
                if (dbBox.Items.Count > 0) dbBox.SelectedIndex = 0;
            }
            catch (Exception ex) { outBox.Text = HumanError.Say(ex, "读取库列表失败"); }
        }

        var tableList = new ListView { SelectionMode = ListViewSelectionMode.None, Height = 200 };
        var chkAll = new CheckBox { Content = "全选", IsChecked = true };
        var syncStruct = new CheckBox { Content = "结构同步（补缺失列，可选对齐类型）", IsChecked = true };
        var alignTypes = new CheckBox { Content = "对齐共有列的类型（会修改目标表已有列，谨慎勾选）", IsChecked = false, Margin = new Thickness(20, 0, 0, 0) };
        // 格11：两种数据模式——追加（默认，记同步账表 tao_sync_log）/ 清空重灌（TRUNCATE 后全量）
        var modeAppend = new RadioButton { Content = "追加写入（记同步账表 tao_sync_log）", IsChecked = true, Margin = new Thickness(0, 0, 0, 0) };
        var modeTruncate = new RadioButton { Content = "清空重灌（先 TRUNCATE 目标表再全量写入，不可回退）", Margin = new Thickness(20, 0, 0, 0) };
        var sendData = new CheckBox { Content = "传输数据（按所选模式）", IsChecked = true };
        alignTypes.IsEnabled = false;
        syncStruct.Checked += (_, _) => alignTypes.IsEnabled = true;
        syncStruct.Unchecked += (_, _) => alignTypes.IsEnabled = false;

        var loadBtn = new Button { Content = "加载表列表", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var runBtn = new Button
        {
            Content = "开始传输",
            Background = Ant.Primary,
            Foreground = Ant.OnPrimary,
            BorderThickness = new Thickness(0),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
        };
        // 格11：取消（贯穿分页枚举与逐页写入）
        var cancelBtn = new Button { Content = "取消", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), IsEnabled = false };
        var output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 170,
            FontFamily = (FontFamily)Application.Current.Resources["AntMono"],
            FontSize = AntTokens.FontMinor,
            Text = "选好源 / 目标连接与库，加载表列表后点「开始传输」。",
        };

        IDbProvider? SrcProv()
        {
            var p = srcBox.SelectedItem as ConnectionProfile;
            return p is null ? null : ProviderFactory.Create(p);
        }
        IDbProvider? DstProv()
        {
            var p = dstBox.SelectedItem as ConnectionProfile;
            return p is null ? null : ProviderFactory.Create(p);
        }

        srcBox.SelectionChanged += async (_, _) => await LoadDbs(srcBox, srcDbBox, output);
        dstBox.SelectionChanged += async (_, _) => await LoadDbs(dstBox, dstDbBox, output);
        if (srcBox.SelectedItem is not null) _ = LoadDbs(srcBox, srcDbBox, output);   // 页签化改造：BuildPanel 同步返回，预加载改后台
        if (dstBox.SelectedItem is not null) _ = LoadDbs(dstBox, dstDbBox, output);
        // RefreshChannel 初始点亮移至通道头部卡声明与事件接线之后（run405 定谳：局部函数捕获变量未赋值前调用即 CS0165）

        loadBtn.Click += async (_, _) =>
        {
            var prov = SrcProv();
            var db = srcDbBox.SelectedItem as string;
            if (prov is null || string.IsNullOrEmpty(db)) { output.Text = "请先选好源连接与源库。"; return; }
            tableList.Items.Clear();
            try
            {
                foreach (var t in await prov.GetChildren(db))
                {
                    var cb = new CheckBox { Content = t, IsChecked = true };
                    tableList.Items.Add(cb);
                }
                output.Text = $"已加载 {(tableList.Items.Count)} 个表 / 对象。";
            }
            catch (Exception ex) { output.Text = HumanError.Say(ex, "读取表列表失败"); }
        };
        chkAll.Checked += (_, _) => { foreach (var i in tableList.Items) if (i is CheckBox cb) cb.IsChecked = true; };
        chkAll.Unchecked += (_, _) => { foreach (var i in tableList.Items) if (i is CheckBox cb) cb.IsChecked = false; };

        runBtn.Click += async (_, _) =>
        {
            var src = SrcProv();
            var dst = DstProv();
            var sdb = srcDbBox.SelectedItem as string ?? "";
            var ddb = dstDbBox.SelectedItem as string ?? "";
            var tables = tableList.Items.OfType<CheckBox>().Where(c => c.IsChecked == true)
                .Select(c => (string)c.Content).ToList();
            if (src is null || dst is null || sdb.Length == 0 || ddb.Length == 0 || tables.Count == 0)
            {
                output.Text = "请把源 / 目标连接、库和要传的表都选好。";
                return;
            }
            bool truncateMode = modeTruncate.IsChecked == true;
            // 目标库会被直接建表 / 改表 / 写入，属危险操作：先给预览 + 确认，不静默执行。
            var steps = new[] { syncStruct.IsChecked == true ? "结构同步（补缺失列" + (alignTypes.IsChecked == true ? "，并对齐共有列类型" : "") + "）" : null,
                                sendData.IsChecked == true ? (truncateMode ? "清空重灌（先 TRUNCATE 目标表再全量写入）" : "追加写入（记同步账表 tao_sync_log）") : null }
                .Where(s => s is not null).ToList();
            var confirm = new ContentDialog
            {
                Title = "确认传输",
                Content = new TextBlock
                {
                    Text = $"将把源库 {sdb} 中选中的 {tables.Count} 张表传到目标库 {ddb}：\n" +
                           (steps.Count > 0 ? string.Join("；\n", steps) + "。\n\n" : "") +
                           "目标库会按上面勾选直接变更，源库只读不动，传输不可自动回退。确认执行？",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "开始传输",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = owner.Content.XamlRoot,
            };
            if (await confirm.ShowThemedAsync() != ContentDialogResult.Primary) return;
            runBtn.IsEnabled = false;
            cancelBtn.IsEnabled = true;
            using var cts = new CancellationTokenSource();
            cancelBtn.Click += (_, _) => cts.Cancel();
            var ct = cts.Token;
            int done = 0, failed = 0;
            output.Text = "开始传输…\n";
            try
            {
                foreach (var t in tables)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var line = new List<string> { $"▶ {t}" };
                        // 1) 结构同步
                        List<ColumnSpec> srcCols;
                        try { srcCols = await src.GetColumns(sdb, t); }
                        catch { srcCols = new(); }
                        if (syncStruct.IsChecked == true && srcCols.Count > 0)
                        {
                            List<string> dstNames = new();
                            bool targetExists = true;
                            try { dstNames = (await dst.GetColumns(ddb, t)).Select(c => c.Name).ToList(); }
                            catch { targetExists = false; }
                            if (!targetExists)
                            {
                                var create = DataSync.CreateForTarget(t, srcCols, ((RelationalProvider)dst).Profile.Engine, dst.Quote);
                                await dst.Execute(ddb, create);
                                line.Add("  目标表不存在，已按源结构创建");
                            }
                            else
                            {
                                var (stmts, skipped) = DataSync.SyncColumns(t, srcCols, dstNames, ((RelationalProvider)dst).Profile.Engine, dst.Quote, alignTypes.IsChecked == true);
                                foreach (var s in stmts) await dst.Execute(ddb, s);
                                line.Add($"  结构同步：执行 {stmts.Count} 条语句" + (skipped.Count > 0 ? $"，跳过 {skipped.Count} 条（{skipped[0]}）" : ""));
                            }
                        }
                        // 2) 数据传输（格11：src 侧分页枚举逐页写入；追加/清空重灌两模式）
                        if (sendData.IsChecked == true)
                        {
                            if (truncateMode)
                            {
                                // 清空重灌：SQLite 无 TRUNCATE 用 DELETE FROM，其余 TRUNCATE
                                var clearSql = dst.Profile.Engine == DbEngine.SQLite
                                    ? $"DELETE FROM {dst.Quote(t)}"
                                    : $"TRUNCATE TABLE {dst.Quote(t)}";
                                await dst.Execute(ddb, clearSql);
                                line.Add("  已清空目标表（清空重灌）");
                            }
                            else if (await TargetHasRowsAsync(dst, ddb, t))
                            {
                                line.Add("  目标表已有数据，按追加模式继续写入（账表 tao_sync_log 记录本次传输）");
                            }
                            int inserted = 0, pages = 0;
                            bool streamed = false;
                            if (src is RelationalProvider relSrc)
                            {
                                // 格11：流式分页——DataReader 直驱逐页，不整包攒内存
                                try
                                {
                                    await using var conn = relSrc.OpenStream(sdb);
                                    var sq = await Core.Streaming.StreamingQuery.StartAsync(conn, $"SELECT * FROM {src.Quote(t)}", ct);
                                    try
                                    {
                                        while (true)
                                        {
                                            ct.ThrowIfCancellationRequested();
                                            var page = await sq.NextPageAsync(200, ct);
                                            if (page.Rows.Length == 0 && !page.HasMore) break;
                                            var qr = new QueryResult();
                                            qr.Columns.AddRange(sq.Columns);
                                            foreach (var r in page.Rows) qr.Rows.Add(r);
                                            foreach (var b in DataSync.InsertBatches(qr, dst.Quote(t), dst.Quote))
                                                await dst.Execute(ddb, b);
                                            inserted += page.Rows.Length;
                                            pages++;
                                            AppLog.Write($"[同步] 表={t} 页={pages}");
                                            output.Text = $"传输中… {t}：已写 {inserted} 行（{pages} 页）\n";
                                            if (!page.HasMore) break;
                                        }
                                        streamed = true;
                                    }
                                    finally { await sq.DisposeAsync(); }
                                }
                                catch (OperationCanceledException) { throw; }
                                catch { streamed = false; } // 流式不可用退整包路径
                            }
                            if (!streamed)
                            {
                                var qr = (await src.Execute(sdb, $"SELECT * FROM {src.Quote(t)}")).FirstOrDefault();
                                if (qr is null || qr.Rows.Count == 0) { line.Add("  数据：源无行，跳过"); }
                                else
                                {
                                    foreach (var b in DataSync.InsertBatches(qr, dst.Quote(t), dst.Quote))
                                    {
                                        ct.ThrowIfCancellationRequested();
                                        await dst.Execute(ddb, b);
                                    }
                                    inserted = qr.Rows.Count;
                                }
                            }
                            if (inserted > 0) line.Add($"  数据：写入 {inserted} 行（{pages} 页）");
                            // 追加模式：同步账表 tao_sync_log 记录本次传输
                            if (!truncateMode && inserted > 0)
                                await WriteSyncLogAsync(dst, ddb, t, inserted, ct);
                        }
                        output.Text += string.Join("\n", line) + "\n";
                        done++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        failed++;
                        output.Text += $"▶ {t}  " + HumanError.Say(ex, "执行") + "\n";
                    }
                }
                output.Text += $"完成：成功 {done}，失败 {failed}。";
                AppLog.Write($"[同步] 完成 成功={done} 失败={failed}");
            }
            catch (OperationCanceledException)
            {
                output.Text += "已取消。";
                AppLog.Write("[同步] cancel");
            }
            finally
            {
                runBtn.IsEnabled = true;
                cancelBtn.IsEnabled = false;
            }
        };

        // 发现148：同步配置——整套源/目标/表/选项存取，可挂任务中心到点自动跑
        var cfgNameBox = new TextBox { PlaceholderText = "配置名（另存/挂任务用）", MinWidth = 170 };
        var cfgBox = new ComboBox { MinWidth = 180, PlaceholderText = "选择已存配置" };
        foreach (var n in SyncProfiles.List()) cfgBox.Items.Add(n);
        var cfgLoadBtn = new Button { Content = "加载", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var cfgSaveBtn = new Button { Content = "另存", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var cfgManageBtn = new Button { Content = "管理", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var hookIvBox = new TextBox { PlaceholderText = "间隔(分)", Text = "60", MinWidth = 70 };
        var hookBtn = new Button { Content = "挂到任务中心", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };

        SyncProfiles.SyncProfile? BuildProfileFromUi(string name)
        {
            var src = srcBox.SelectedItem as ConnectionProfile;
            var dst = dstBox.SelectedItem as ConnectionProfile;
            var sdb = srcDbBox.SelectedItem as string ?? "";
            var ddb = dstDbBox.SelectedItem as string ?? "";
            var tables = tableList.Items.OfType<CheckBox>().Where(c => c.IsChecked == true)
                .Select(c => (string)c.Content).ToList();
            if (src is null || dst is null || sdb.Length == 0 || ddb.Length == 0 || tables.Count == 0) return null;
            return new SyncProfiles.SyncProfile(name, src.DisplayName, sdb, dst.DisplayName, ddb, tables,
                syncStruct.IsChecked == true, alignTypes.IsChecked == true, sendData.IsChecked == true, modeTruncate.IsChecked == true);
        }

        cfgLoadBtn.Click += async (_, _) =>
        {
            var name = cfgBox.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(name)) { output.Text = "先在下拉里选一个配置再加载。"; return; }
            var p = SyncProfiles.Load(name);
            if (p is null) { output.Text = $"配置「{name}」读不出来（可能已损坏），请删掉重建。"; return; }
            var srcItem = srcBox.Items.OfType<ConnectionProfile>().FirstOrDefault(x => x.DisplayName.Equals(p.SourceConnection, StringComparison.OrdinalIgnoreCase));
            var dstItem = dstBox.Items.OfType<ConnectionProfile>().FirstOrDefault(x => x.DisplayName.Equals(p.TargetConnection, StringComparison.OrdinalIgnoreCase));
            if (srcItem is null || dstItem is null)
            {
                output.Text = $"配置里的连接「{(srcItem is null ? p.SourceConnection : p.TargetConnection)}」当前不存在，请先在左侧建好同名连接再加载。";
                return;
            }
            srcBox.SelectedItem = srcItem;
            dstBox.SelectedItem = dstItem;
            await LoadDbs(srcBox, srcDbBox, output);
            await LoadDbs(dstBox, dstDbBox, output);
            if (srcDbBox.Items.Contains(p.SourceDb)) srcDbBox.SelectedItem = p.SourceDb;
            if (dstDbBox.Items.Contains(p.TargetDb)) dstDbBox.SelectedItem = p.TargetDb;
            tableList.Items.Clear();
            foreach (var t in p.Tables) tableList.Items.Add(new CheckBox { Content = t, IsChecked = true });
            syncStruct.IsChecked = p.SyncStruct;
            alignTypes.IsChecked = p.AlignTypes;
            sendData.IsChecked = p.SendData;
            if (p.TruncateMode) modeTruncate.IsChecked = true; else modeAppend.IsChecked = true;
            cfgNameBox.Text = p.Name;
            output.Text = $"已套用配置「{p.Name}」：{p.SourceConnection}/{p.SourceDb} → {p.TargetConnection}/{p.TargetDb}，{p.Tables.Count} 表。";
        };
        cfgSaveBtn.Click += (_, _) =>
        {
            var name = (cfgNameBox.Text ?? "").Trim();
            if (name.Length == 0) { output.Text = "先填配置名再另存。"; return; }
            var p = BuildProfileFromUi(name);
            if (p is null) { output.Text = "请把源 / 目标连接、库和要传的表都选好再另存。"; return; }
            var verr = SyncProfiles.Validate(p);
            if (verr.Length > 0) { output.Text = verr; return; }
            try
            {
                SyncProfiles.Save(p);
                if (!cfgBox.Items.Contains(name)) cfgBox.Items.Add(name);
                cfgBox.SelectedItem = name;
                output.Text = $"已保存配置「{name}」，下次打开可直接加载或挂到任务中心。";
            }
            catch (Exception ex) { output.Text = HumanError.Say(ex, "保存配置失败"); }
        };
        cfgManageBtn.Click += async (_, _) =>
        {
            var panel = new StackPanel { Spacing = 8, MinWidth = 380 };
            var names = SyncProfiles.List();
            if (names.Count == 0) panel.Children.Add(new TextBlock { Text = "还没有保存过同步配置。", TextWrapping = TextWrapping.Wrap });
            foreach (var n in names)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new TextBlock { Text = n, VerticalAlignment = VerticalAlignment.Center });
                var delBtn = new Button { Content = "删除", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusBadge) };
                delBtn.Click += (_, _) =>
                {
                    SyncProfiles.Delete(n);
                    panel.Children.Remove(row);
                    if (cfgBox.Items.Contains(n)) cfgBox.Items.Remove(n);
                };
                row.Children.Add(delBtn);
                panel.Children.Add(row);
            }
            var mg = new ContentDialog
            {
                Title = "管理同步配置",
                Content = new ScrollViewer { Content = panel, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                CloseButtonText = "关闭",
                XamlRoot = owner.Content.XamlRoot,
            };
            await mg.ShowThemedAsync();
        };
        hookBtn.Click += async (_, _) =>
        {
            var name = (cfgNameBox.Text ?? "").Trim();
            if (name.Length == 0) { output.Text = "先填配置名，再挂到任务中心。"; return; }
            var p = BuildProfileFromUi(name);
            if (p is null) { output.Text = "请把源 / 目标连接、库和要传的表都选好再挂任务。"; return; }
            var verr = SyncProfiles.Validate(p);
            if (verr.Length > 0) { output.Text = verr; return; }
            if (!int.TryParse(hookIvBox.Text?.Trim(), out int iv)) { output.Text = "间隔请填数字（分钟）。"; return; }
            if (p.TruncateMode)
            {
                // 清空重灌到点会先 TRUNCATE：危险操作先确认，不静默挂账
                var confirm = new ContentDialog
                {
                    Title = "确认挂任务",
                    Content = new TextBlock
                    {
                        Text = $"配置「{name}」是清空重灌模式：到点会先 TRUNCATE 目标表再全量写入，不可回退。\n确认挂到任务中心（每 {iv} 分钟执行一次）？",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    PrimaryButtonText = "确认挂任务",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = owner.Content.XamlRoot,
                };
                if (await confirm.ShowThemedAsync() != ContentDialogResult.Primary) return;
            }
            try
            {
                SyncProfiles.Save(p);
                var e = SqlScheduler.Add(p.Name, p.TargetDb, "--tao-sync:" + p.Name, iv, "sync");
                output.Text = $"已挂到任务中心：{e.Name}（{e.Id}），每 {iv} 分钟执行一次，下次 {e.NextRunAt:MM-dd HH:mm}。到点自动按配置执行，可在任务中心→定时 SQL 管理里启停。";
                AppLog.Write($"[同步配置] 已挂任务 {p.Name}（{e.Id}）每 {iv} 分");
            }
            catch (Exception ex) { output.Text = HumanError.Say(ex, "挂任务失败"); }
        };

        // 发现305原型：通道头部卡——源端 A ⇄ 目标端 B，选好连接与库即实时点亮
        var srcRole = new TextBlock { Text = "源端 A", FontSize = AntTokens.FontCaption, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Ant.Primary };
        var srcName = new TextBlock { Text = "（未选）", FontSize = AntTokens.FontMinor, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var srcMeta = new TextBlock { FontSize = AntTokens.FontCaption, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var dstRole = new TextBlock { Text = "目标端 B", FontSize = AntTokens.FontCaption, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Ant.Primary };
        var dstName = new TextBlock { Text = "（未选）", FontSize = AntTokens.FontMinor, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var dstMeta = new TextBlock { FontSize = AntTokens.FontCaption, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var srcCard = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg),
            BorderThickness = new Thickness(1),
            BorderBrush = Ant.Border,
            Background = (Brush?)Application.Current.Resources["AntCard"],
        };
        srcCard.Child = new StackPanel { Spacing = 2 };
        ((StackPanel)srcCard.Child).Children.Add(srcRole);
        ((StackPanel)srcCard.Child).Children.Add(srcName);
        ((StackPanel)srcCard.Child).Children.Add(srcMeta);
        var dstCard = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg),
            BorderThickness = new Thickness(1),
            BorderBrush = Ant.Border,
            Background = (Brush?)Application.Current.Resources["AntCard"],
        };
        dstCard.Child = new StackPanel { Spacing = 2 };
        ((StackPanel)dstCard.Child).Children.Add(dstRole);
        ((StackPanel)dstCard.Child).Children.Add(dstName);
        ((StackPanel)dstCard.Child).Children.Add(dstMeta);
        var channelGrid = new Grid { ColumnSpacing = 10 };
        channelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        channelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        channelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var arrow = new TextBlock
        {
            Text = "⇄",
            FontSize = AntTokens.FontTitle,
            Foreground = Ant.Primary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(srcCard, 0);
        Grid.SetColumn(arrow, 1);
        Grid.SetColumn(dstCard, 2);
        channelGrid.Children.Add(srcCard);
        channelGrid.Children.Add(arrow);
        channelGrid.Children.Add(dstCard);

        void RefreshChannel()
        {
            var s = srcBox.SelectedItem as ConnectionProfile;
            var d = dstBox.SelectedItem as ConnectionProfile;
            srcName.Text = s?.DisplayName ?? "（未选）";
            srcMeta.Text = s is null ? "选好源连接后这里显示主机与库" : $"{s.Host}:{(s.Port > 0 ? s.Port : EngineInfo.DefaultPort(s.Engine))} · {srcDbBox.SelectedItem as string ?? "—"}";
            dstName.Text = d?.DisplayName ?? "（未选）";
            dstMeta.Text = d is null ? "选好目标连接后这里显示主机与库" : $"{d.Host}:{(d.Port > 0 ? d.Port : EngineInfo.DefaultPort(d.Engine))} · {dstDbBox.SelectedItem as string ?? "—"}";
        }
        srcBox.SelectionChanged += (_, _) => RefreshChannel();
        dstBox.SelectionChanged += (_, _) => RefreshChannel();
        srcDbBox.SelectionChanged += (_, _) => RefreshChannel();
        dstDbBox.SelectionChanged += (_, _) => RefreshChannel();
        RefreshChannel();   // 发现305原型：通道头部卡初始点亮（声明后调用）

        // 发现305原型：表规则表头——表清单上方加底色表头条
        var tableHead = new Border
        {
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusSm),
            Background = Ant.HeadBg,
            Child = new TextBlock
            {
                Text = "表清单（勾选要传的表）",
                FontSize = AntTokens.FontMinor,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Ant.TextSub,
            },
        };

        var content = new StackPanel { Spacing = 10, MinWidth = 620, Margin = new Thickness(0, 0, 12, 0) };
        // 305 原型：面板标题行
        content.Children.Add(new TextBlock { Text = "数据同步", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = AntTokens.FontBody });
        content.Children.Add(new TextBlock
        {
            Text = "源库只读；目标库会被直接建表、改列、写入数据。点「开始传输」后会先给出确认，确认才会动目标库。",
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.TextSub,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(channelGrid);   // 发现305原型：通道头部卡
        var rowA = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rowA.Children.Add(new TextBlock { Text = "源", VerticalAlignment = VerticalAlignment.Center });
        rowA.Children.Add(srcBox);
        rowA.Children.Add(srcDbBox);
        content.Children.Add(rowA);
        // 发现148：同步配置行——另存/加载/管理整套配置，可挂任务中心到点自动执行
        var rowCfg = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rowCfg.Children.Add(cfgNameBox);
        rowCfg.Children.Add(cfgBox);
        rowCfg.Children.Add(cfgLoadBtn);
        rowCfg.Children.Add(cfgSaveBtn);
        rowCfg.Children.Add(cfgManageBtn);
        content.Children.Add(rowCfg);
        var rowHook = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rowHook.Children.Add(new TextBlock { Text = "到点自动执行：", VerticalAlignment = VerticalAlignment.Center, FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        rowHook.Children.Add(hookIvBox);
        rowHook.Children.Add(hookBtn);
        content.Children.Add(rowHook);
        var rowB = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rowB.Children.Add(new TextBlock { Text = "目标", VerticalAlignment = VerticalAlignment.Center });
        rowB.Children.Add(dstBox);
        rowB.Children.Add(dstDbBox);
        content.Children.Add(rowB);
        var rowC = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rowC.Children.Add(loadBtn);
        rowC.Children.Add(chkAll);
        content.Children.Add(rowC);
        content.Children.Add(tableHead);   // 发现305原型：表规则表头
        content.Children.Add(tableList);
        content.Children.Add(syncStruct);
        content.Children.Add(alignTypes);
        content.Children.Add(sendData);
        content.Children.Add(modeAppend);
        content.Children.Add(modeTruncate);
        var runRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        runRow.Children.Add(runBtn);
        runRow.Children.Add(cancelBtn);
        content.Children.Add(runRow);
        content.Children.Add(output);

        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>发现148：按配置执行一次同步（任务中心 sync 条目与挂账任务共用）——
    /// 按连接显示名解析两连接，结构同步＋数据传输；返回错误消息（空串=成功）。</summary>
    public static async Task<string> ExecuteProfileAsync(SyncProfiles.SyncProfile profile)
    {
        var all = ConnectionStore.Load();
        var srcProfile = all.FirstOrDefault(p => p.DisplayName.Equals(profile.SourceConnection, StringComparison.OrdinalIgnoreCase));
        var dstProfile = all.FirstOrDefault(p => p.DisplayName.Equals(profile.TargetConnection, StringComparison.OrdinalIgnoreCase));
        if (srcProfile is null) return $"源连接「{profile.SourceConnection}」不存在，请先建好同名连接。";
        if (dstProfile is null) return $"目标连接「{profile.TargetConnection}」不存在，请先建好同名连接。";
        try
        {
            using var src = ProviderFactory.Create(srcProfile);
            using var dst = ProviderFactory.Create(dstProfile);
            foreach (var t in profile.Tables)
            {
                // 1) 结构同步
                if (profile.SyncStruct)
                {
                    List<ColumnSpec> srcCols;
                    try { srcCols = await src.GetColumns(profile.SourceDb, t); }
                    catch { srcCols = new(); }
                    if (srcCols.Count > 0)
                    {
                        List<string> dstNames = new();
                        bool targetExists = true;
                        try { dstNames = (await dst.GetColumns(profile.TargetDb, t)).Select(c => c.Name).ToList(); }
                        catch { targetExists = false; }
                        if (!targetExists)
                        {
                            var create = DataSync.CreateForTarget(t, srcCols, dst.Profile.Engine, dst.Quote);
                            await dst.Execute(profile.TargetDb, create);
                        }
                        else
                        {
                            var (stmts, _) = DataSync.SyncColumns(t, srcCols, dstNames, dst.Profile.Engine, dst.Quote, profile.AlignTypes);
                            foreach (var s in stmts) await dst.Execute(profile.TargetDb, s);
                        }
                    }
                }
                // 2) 数据传输
                if (profile.SendData)
                {
                    if (profile.TruncateMode)
                    {
                        var clearSql = dst.Profile.Engine == DbEngine.SQLite
                            ? $"DELETE FROM {dst.Quote(t)}"
                            : $"TRUNCATE TABLE {dst.Quote(t)}";
                        await dst.Execute(profile.TargetDb, clearSql);
                    }
                    var qr = (await src.Execute(profile.SourceDb, $"SELECT * FROM {src.Quote(t)}")).FirstOrDefault();
                    if (qr is not null && qr.Rows.Count > 0)
                        foreach (var b in DataSync.InsertBatches(qr, dst.Quote(t), dst.Quote))
                            await dst.Execute(profile.TargetDb, b);
                }
            }
            AppLog.Write($"[同步配置] 已执行 {profile.Name}（{profile.Tables.Count} 表）");
            return "";
        }
        catch (Exception ex)
        {
            AppLog.Write($"[同步配置] 执行失败 {profile.Name}: {ex.Message}");
            return HumanError.Say(ex, "同步配置执行");
        }
    }

    /// <summary>格11：目标表是否已有数据（重复同名表提示用；读不到按无数据处理）。</summary>
    private static async Task<bool> TargetHasRowsAsync(IDbProvider dst, string ddb, string table)
    {
        try
        {
            var rs = await dst.Execute(ddb, $"SELECT COUNT(*) AS n FROM {dst.Quote(table)}");
            var qr = rs.FirstOrDefault();
            if (qr is null || qr.Rows.Count == 0) return false;
            return Convert.ToInt64(qr.Rows[0][0]) > 0;
        }
        catch { return false; }
    }

    /// <summary>格11：同步账表 tao_sync_log——建表（缺则建）＋记录本次传输（追加模式）。</summary>
    private static async Task WriteSyncLogAsync(IDbProvider dst, string ddb, string table, long rows, CancellationToken ct)
    {
        try
        {
            string create = dst.Profile.Engine == DbEngine.SqlServer
                ? "IF OBJECT_ID(N'dbo.tao_sync_log', N'U') IS NULL CREATE TABLE dbo.tao_sync_log (table_name NVARCHAR(256), row_count BIGINT, mode NVARCHAR(20), synced_at DATETIME DEFAULT GETDATE());"
                : "CREATE TABLE IF NOT EXISTS tao_sync_log (table_name VARCHAR(256), row_count BIGINT, mode VARCHAR(20), synced_at TIMESTAMP)";
            await dst.Execute(ddb, create);
            await dst.Execute(ddb, $"INSERT INTO {dst.Quote("tao_sync_log")} (table_name, row_count, mode, synced_at) " +
                                   $"VALUES ('{table.Replace("'", "''")}', {rows}, 'append', CURRENT_TIMESTAMP)");
        }
        catch { /* 账表失败不阻断传输主流程 */ }
    }
}
