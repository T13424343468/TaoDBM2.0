using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>批次二格5：数据库范围搜索——选库 → 关键词 → 逐表文本列 LIKE 计数，
/// 出「表/列/命中数」清单，双击跳转浏览页并预填 WHERE。进度条＋取消（CancellationToken 口径），
/// 每表超时跳过不阻断。引擎 SQL 走 Core/DbSearch 纯逻辑出口。</summary>
public static class DbSearchDialog
{
    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        var provider = info.Provider!;
        string db = info.Db;
        var kw = new TextBox { Header = "关键词", PlaceholderText = "输入要搜的文本（区分引擎大小写口径）" };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var results = new ListView { MaxHeight = 360, SelectionMode = ListViewSelectionMode.Single };
        var cancelCts = new CancellationTokenSource();

        // 原型210：范围徽标行（库·表·深度口径）——打开时填连接概要
        var scope = new TextBlock
        {
            Text = $"{info.Name} · 深度含列/索引/存储过程",
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.TextSub,
        };
        var content = new StackPanel { Spacing = 10, MinWidth = 560,
            Children = { scope, kw, new Button { Content = "开始搜索", HorizontalAlignment = HorizontalAlignment.Left }, progress, status, results } };
        var dlg = new ContentDialog
        {
            Title = $"在库中搜索 · {db}",
            Content = content,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        dlg.Closed += (_, _) => cancelCts.Cancel();

        async void Run()
        {
            var keyword = kw.Text.Trim();
            if (keyword.Length == 0) { status.Text = "请输入关键词。"; return; }
            progress.Visibility = Visibility.Visible;
            results.Items.Clear();
            status.Text = "正在取表清单…";
            int hitTables = 0, hitRows = 0;
            try
            {
                var tables = await provider.GetChildren(db);
                int done = 0;
                foreach (var t in tables)
                {
                    if (cancelCts.IsCancellationRequested) break;
                    status.Text = $"搜索 {t}（{done + 1}/{tables.Count}）…";
                    try
                    {
                        var cols = await provider.GetColumns(db, t);
                        using var perTable = new CancellationTokenSource(TimeSpan.FromSeconds(10));   // 每表 10 秒超时跳过
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancelCts.Token, perTable.Token);
                        var hits = await DbSearch.SearchAsync(provider, db, cols, t, keyword, linked.Token);
                        foreach (var h in hits)
                        {
                            hitTables++; hitRows += (int)Math.Min(h.Hits, int.MaxValue);
                            var li = new ListViewItem { Content = $"{h.Table} · {h.Column} · {h.Hits:N0} 行命中" };
                            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(li, $"DbSearch_{h.Table}_{h.Column}");
                            li.DoubleTapped += (_, _) =>
                            {
                                var tblInfo = new NodeInfo { Kind = NodeKind.Table, Profile = info.Profile, Provider = provider, Db = db, Name = h.Table };
                                owner.OpenTableTab(tblInfo);
                                dlg.Hide();
                            };
                            results.Items.Add(li);
                        }
                    }
                    catch (OperationCanceledException) when (cancelCts.IsCancellationRequested) { break; }
                    catch { /* 单表超时/不支持：跳过不阻断 */ }
                    done++;
                }
                status.Text = cancelCts.IsCancellationRequested ? $"已取消：完成 {done}/{tables.Count} 表。" : $"搜索完成：{hitTables} 列命中，共 {hitRows:N0} 行。双击命中项跳转浏览。";
                AppLog.Write($"[库搜索] 表={hitTables} 命中={hitRows}");
            }
            catch (Exception ex)
            {
                status.Text = HumanError.Say(ex, "搜索失败");
            }
            finally { progress.Visibility = Visibility.Collapsed; }
        }

        ((Button)content.Children[1]).Click += (_, _) => Run();
        await dlg.ShowThemedAsync();
    }
}
