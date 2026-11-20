using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>B10 主从 / 分布式管理面对话框：拓扑节点列表 + 复制延迟 + 管理操作入口。
/// 数据安全口径：暂停/恢复、重建通道、切换主库全部只生成语句草稿，弹窗预览逐条勾选确认后才执行；
/// 拓扑变更与恢复操作统一写本地操作历史；管理操作使用当前连接账号自身权限，无权限时报错提示而非隐藏。</summary>
public static class ReplicationDialog
{
    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        var profile = info.Profile;
        var prov = info.Provider ?? ProviderFactory.Create(profile);
        if (!ReplicationCatalog.Supported(profile.Engine))
        {
            await new MessageDialogHelper(owner).Show("主从管理面", ReplicationCatalog.UnsupportedReason(profile.Engine));
            return;
        }

        var roleText = new TextBlock { FontSize = AntTokens.FontBody, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var topoPanel = new StackPanel { Spacing = 10 };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        // 图形化拓扑节点框（设计稿 §14）：主库框 + 「══ 状态 · 延迟 ══▶」连线 + 从库框；右键向导化操作。
        Border NodeBox(string title, string sub, SolidColorBrush border, SolidColorBrush bg)
        {
            var b = new Border
            {
                BorderBrush = border,
                BorderThickness = new Thickness(1.5),
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
                Padding = new Thickness(10, 8, 10, 8),
                Background = bg,
            };
            var sp = new StackPanel { Spacing = 2 };
            sp.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = AntTokens.FontBody });
            sp.Children.Add(new TextBlock { Text = sub, FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, FontFamily = new FontFamily("Consolas") });
            b.Child = sp;
            return b;
        }

        MenuFlyout NodeMenu(string nodeName)
        {
            var menu = new MenuFlyout();
            var probe = new MenuFlyoutItem { Text = "探活（刷新）" };
            probe.Click += async (_, _) => await RefreshAsync();
            menu.Items.Add(probe);
            if (profile.Engine == DbEngine.MySQL)
            {
                var deploy = new MenuFlyoutItem { Text = "建复制账号 / 配置从库（向导）" };
                deploy.Click += (_, _) => _ = DeploymentWizardDialog.Show(owner, info);
                menu.Items.Add(deploy);
                var seed = new MenuFlyoutItem { Text = "备份播种" };
                seed.Click += (_, _) => _ = BackupDialog.Show(owner, info);
                menu.Items.Add(seed);
            }
            return menu;
        }

        TextBlock LinkArrow(TopologyNode n)
        {
            var lagText = n.LagSeconds is null ? "—" : $"{n.LagSeconds:0.###}s";
            var color = n.State switch
            {
                "正常" => Ant.Success,
                "延迟偏高" or "严重滞后" => Ant.Warn,
                "已断开" => Ant.Error,
                _ => Ant.TextSub,
            };
            return new TextBlock
            {
                Text = $"══ {n.State} · 延迟 {lagText} ══▶",
                FontFamily = new FontFamily("Consolas"),
                FontSize = AntTokens.FontMinor,
                Foreground = color,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        async Task RefreshAsync()
        {
            status.Text = "正在采集复制拓扑…";
            try
            {
                // 角色探测：MySQL 以能读到复制线程状态为准；PG 以 pg_is_in_recovery 为准
                bool isReplica;
                List<QueryResult> probe;
                try
                {
                    probe = await prov.Execute("", ReplicationCatalog.RoleProbeSql(profile.Engine));
                }
                catch when (profile.Engine == DbEngine.MySQL)
                {
                    // 8.0.22 之前无 SHOW REPLICA STATUS，退回旧名再试
                    probe = await prov.Execute("", "SHOW SLAVE STATUS");
                }
                if (profile.Engine == DbEngine.PostgreSQL)
                {
                    var v = probe.Count > 0 && probe[0].Rows.Count > 0
                        ? probe[0].Rows[0].FirstOrDefault()?.ToString() : "f";
                    isReplica = string.Equals(v, "True", StringComparison.OrdinalIgnoreCase) || v == "t";
                }
                else
                {
                    isReplica = probe.Count > 0 && probe[0].Rows.Count > 0;
                }

                List<TopologyNode> nodes;
                if (isReplica)
                {
                    var rs = await prov.Execute("", ReplicationCatalog.ReplicaSelfStatusSql(profile.Engine));
                    var cols = rs.Count > 0 ? rs[0].Columns : new List<string>();
                    var rows = rs.Count > 0 ? rs[0].Rows : new List<object?[]>();
                    nodes = new List<TopologyNode> { ReplicationCatalog.BuildFromReplica(profile.Engine, cols, rows) };
                    roleText.Text = "本节点角色：从库";
                }
                else
                {
                    var rs = await prov.Execute("", ReplicationCatalog.ReplicasSql(profile.Engine));
                    var cols = rs.Count > 0 ? rs[0].Columns : new List<string>();
                    var rows = rs.Count > 0 ? rs[0].Rows : new List<object?[]>();
                    nodes = ReplicationCatalog.BuildFromPrimary(profile.Engine, cols, rows);
                    roleText.Text = "本节点角色：主库";
                }

                topoPanel.Children.Clear();
                var masterBox = NodeBox(
                    isReplica ? "上游主库" : "master（本机）",
                    $"{EngineInfo.Name(profile.Engine)} · {profile.Host}:{(profile.Port > 0 ? profile.Port : EngineInfo.DefaultPort(profile.Engine))}",
                    Ant.Primary, Ant.PrimaryBg);
                masterBox.RightTapped += (s, _) => { if (s is Border b) NodeMenu("master").ShowAt(b, new Windows.Foundation.Point(40, 20)); };
                topoPanel.Children.Add(masterBox);

                foreach (var n in nodes)
                {
                    var broken = n.State == "已断开";
                    var replicaBox = NodeBox(
                        n.Name,
                        $"{n.Role} · {n.Detail}" + (broken ? "\n排查：网络/端口、复制账号权限、位点是否过期" : ""),
                        broken ? Ant.Error : Ant.Border,
                        broken ? Ant.ErrorBg : Ant.Card);
                    replicaBox.RightTapped += (s, _) => { if (s is Border b) NodeMenu(n.Name).ShowAt(b, new Windows.Foundation.Point(40, 20)); };
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                    row.Children.Add(LinkArrow(n));
                    row.Children.Add(replicaBox);
                    topoPanel.Children.Add(row);
                }
                status.Text = $"共 {nodes.Count} 个下游节点。延迟 ≥{ReplicationCatalog.LagWarnSeconds:0}s 标黄，≥{ReplicationCatalog.LagDangerSeconds:0}s 判严重滞后，断开标红；右键节点可探活 / 向导化操作。";
            }
            catch (Exception ex)
            {
                status.Text = "拓扑采集失败：" + ex.Message + "（无复制管理权限或该库未配置主从时属正常）";
            }
        }

        Button OpButton(string text, bool danger = false)
        {
            var b = new Button { Content = text, CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
            if (danger) b.Foreground = Ant.Error;
            return b;
        }

        var pauseBtn = OpButton("暂停复制");
        pauseBtn.Click += async (_, _) =>
        {
            await DraftOps.ConfirmExecuteAsync(owner, prov, "暂停复制（草稿）",
                ReplicationCatalog.PauseReplicaSql(profile.Engine, pause: true), status);
        };
        var resumeBtn = OpButton("恢复复制");
        resumeBtn.Click += async (_, _) =>
        {
            await DraftOps.ConfirmExecuteAsync(owner, prov, "恢复复制（草稿）",
                ReplicationCatalog.PauseReplicaSql(profile.Engine, pause: false), status);
        };
        var reconnectBtn = OpButton("重建复制通道（模板）");
        reconnectBtn.Click += async (_, _) =>
        {
            var drafts = ReplicationCatalog.ReconnectTemplateSql(profile.Engine,
                profile.Host.Length > 0 ? profile.Host : "<来源主机>", profile.Port > 0 ? profile.Port : EngineInfo.DefaultPort(profile.Engine),
                profile.User.Length > 0 ? profile.User : "<复制账号>", profile.Database.Length > 0 ? profile.Database : "db");
            await DraftOps.ConfirmExecuteAsync(owner, prov, "重建复制通道（模板，口令与位点需现场补齐）", drafts, status);
        };
        var switchBtn = OpButton("切换主库（高危）", danger: true);
        switchBtn.Click += async (_, _) =>
        {
            var confirm = new ContentDialog
            {
                Title = "切换主库（高危）",
                Content = new TextBlock
                {
                    Text = "切换主库会重置本节点复制通道并改变数据流向，影响所有下游节点；执行前请确认已停止应用写入旧主库。\n将继续生成切换草稿供逐条勾选。",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "生成切换草稿",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = owner.Content.XamlRoot,
            };
            if (await confirm.ShowThemedAsync() != ContentDialogResult.Primary) return;
            var drafts = ReplicationCatalog.SwitchoverDraftSql(profile.Engine, profile.Host.Length > 0 ? profile.Host : "旧主库");
            await DraftOps.ConfirmExecuteAsync(owner, prov, "切换主库（高危草稿）", drafts, status);
        };

        var refreshBtn = new Button { Content = "刷新", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        refreshBtn.Click += async (_, _) => await RefreshAsync();

        var opRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        opRow.Children.Add(pauseBtn);
        opRow.Children.Add(resumeBtn);
        opRow.Children.Add(reconnectBtn);
        opRow.Children.Add(switchBtn);
        opRow.Children.Add(refreshBtn);

        var panel = new StackPanel { Spacing = 12, MinWidth = 520 };
        panel.Children.Add(roleText);
        panel.Children.Add(new ScrollViewer { Content = topoPanel, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(opRow);
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "主从 / 分布式管理面",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        var loadTask = RefreshAsync();
        _ = dlg.ShowThemedAsync();
        await loadTask;
    }
}
