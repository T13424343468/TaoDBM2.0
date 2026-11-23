using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>数据传输与结构同步：源连接/库/表 → 目标连接/库，可选补结构、传数据。</summary>
public static class DataSyncDialog
{
    public static async Task Show(MainWindow owner, List<ConnectionProfile> profiles, ConnectionProfile? preselect)
    {
        var relational = profiles.Where(p => p.Engine is not (DbEngine.MongoDB or DbEngine.Redis)).ToList();
        if (relational.Count < 2)
        {
            await new MessageDialogHelper(owner).Show("数据传输",
                "需要至少两个关系型连接（MySQL / PostgreSQL / SQL Server / SQLite / Oracle）。请先在左侧新建连接。");
            return;
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
            catch (Exception ex) { outBox.Text = "读取库列表失败：" + ex.Message; }
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
        if (srcBox.SelectedItem is not null) await LoadDbs(srcBox, srcDbBox, output);
        if (dstBox.SelectedItem is not null) await LoadDbs(dstBox, dstDbBox, output);

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
            catch (Exception ex) { output.Text = "读取表列表失败：" + ex.Message; }
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
                        output.Text += $"▶ {t}  失败：{ex.Message}\n";
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

        var content = new StackPanel { Spacing = 10, MinWidth = 620, Margin = new Thickness(0, 0, 12, 0) };
        content.Children.Add(new TextBlock
        {
            Text = "源库只读；目标库会被直接建表、改列、写入数据。点「开始传输」后会先给出确认，确认才会动目标库。",
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.TextSub,
            TextWrapping = TextWrapping.Wrap,
        });
        var rowA = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rowA.Children.Add(new TextBlock { Text = "源", VerticalAlignment = VerticalAlignment.Center });
        rowA.Children.Add(srcBox);
        rowA.Children.Add(srcDbBox);
        content.Children.Add(rowA);
        var rowB = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rowB.Children.Add(new TextBlock { Text = "目标", VerticalAlignment = VerticalAlignment.Center });
        rowB.Children.Add(dstBox);
        rowB.Children.Add(dstDbBox);
        content.Children.Add(rowB);
        var rowC = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rowC.Children.Add(loadBtn);
        rowC.Children.Add(chkAll);
        content.Children.Add(rowC);
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

        var dlg = new ContentDialog
        {
            Title = "数据传输与结构同步",
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
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
