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
    public static FrameworkElement BuildPanel(MainWindow owner, NodeInfo info)
    {
        var profile = info.Profile;
        var prov = info.Provider ?? ProviderFactory.Create(profile);
        if (!ReplicationCatalog.Supported(profile.Engine))
        {
            return new TextBlock
            {
                Text = $"主从管理面：{ReplicationCatalog.UnsupportedReason(profile.Engine)}",
                FontSize = AntTokens.FontMinor,
                Foreground = Ant.TextSub,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16),
            };
        }

        var roleText = new TextBlock { FontSize = AntTokens.FontBody, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var topoPanel = new StackPanel { Spacing = 10 };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        // 图形化拓扑节点框（设计稿 §14）：主库框 + 「══ 状态 · 延迟 ══▶」连线 + 从库框；右键向导化操作。
        // 发现304原型：节点框加角色徽标行（PRIMARY · 主库 / REPLICA · 从库），一眼分清角色。
        Border NodeBox(string role, string title, string sub, SolidColorBrush border, SolidColorBrush bg, SolidColorBrush roleColor)
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
            sp.Children.Add(new TextBlock { Text = role, FontSize = AntTokens.FontCaption, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = roleColor });
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
                deploy.Click += (_, _) => owner.OpenPanelTab($"复制向导 · {info.Name}", () => DeploymentWizardDialog.BuildPanel(owner, info));
                menu.Items.Add(deploy);
                var seed = new MenuFlyoutItem { Text = "备份播种" };
                seed.Click += (_, _) => owner.OpenPanelTab($"备份 / 恢复 · {info.Name}", () => BackupDialog.BuildPanel(owner, info));   // 页签化改造：播种走备份面板页签
                menu.Items.Add(seed);
            }
            return menu;
        }

        // 发现304原型：连线加状态圆点——正常绿 / 滞后黄 / 断开红，与文字同色
        StackPanel LinkArrow(TopologyNode n)
        {
            var lagText = n.LagSeconds is null ? "—" : $"{n.LagSeconds:0.###}s";
            var color = n.State switch
            {
                "正常" => Ant.Success,
                "延迟偏高" or "严重滞后" => Ant.Warn,
                "已断开" => Ant.Error,
                _ => Ant.TextSub,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Fill = color, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock
            {
                Text = $"══ {n.State} · 延迟 {lagText} ══▶",
                FontFamily = new FontFamily("Consolas"),
                FontSize = AntTokens.FontMinor,
                Foreground = color,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return row;
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
                    isReplica ? "PRIMARY · 主库" : "PRIMARY · 主库（本机）",
                    isReplica ? "上游主库" : "master（本机）",
                    $"{EngineInfo.Name(profile.Engine)} · {profile.Host}:{(profile.Port > 0 ? profile.Port : EngineInfo.DefaultPort(profile.Engine))}",
                    Ant.Primary, Ant.PrimaryBg, Ant.Primary);
                masterBox.RightTapped += (s, _) => { if (s is Border b) NodeMenu("master").ShowAt(b, new Windows.Foundation.Point(40, 20)); };
                topoPanel.Children.Add(masterBox);

                foreach (var n in nodes)
                {
                    var broken = n.State == "已断开";
                    var replicaBox = NodeBox(
                        "REPLICA · 从库",
                        n.Name,
                        $"{n.Role} · {n.Detail}" + (broken ? "\n排查：网络/端口、复制账号权限、位点是否过期" : ""),
                        broken ? Ant.Error : Ant.Border,
                        broken ? Ant.ErrorBg : Ant.Card,
                        broken ? Ant.Error : Ant.TextSub);
                    replicaBox.RightTapped += (s, _) => { if (s is Border b) NodeMenu(n.Name).ShowAt(b, new Windows.Foundation.Point(40, 20)); };
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                    row.Children.Add(LinkArrow(n));
                    row.Children.Add(replicaBox);
                    topoPanel.Children.Add(row);
                }
                RefreshDetail(nodes);
                status.Text = $"共 {nodes.Count} 个下游节点。延迟 ≥{ReplicationCatalog.LagWarnSeconds:0}s 标黄，≥{ReplicationCatalog.LagDangerSeconds:0}s 判严重滞后，断开标红；右键节点可探活 / 向导化操作。";
            }
            catch (Exception ex)
            {
                status.Text = HumanError.Say(ex, "拓扑采集失败") + "（无复制管理权限或该库未配置主从时属正常）";
            }
        }

        Button OpButton(string text, bool danger = false)
        {
            var b = new Button { Content = text, CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
            if (danger) b.Foreground = Ant.Error;
            return b;
        }

        // 发现304原型：从库明细表——实例 / 复制状态徽标 / 延迟，表头底色一眼分栏
        var detailPanel = new StackPanel { Spacing = 4 };
        static Border StateBadge(string text, bool ok)
        {
            return new Border
            {
                Padding = new Thickness(8, 1, 8, 1),
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusBadge),
                Background = ok ? (Brush?)Ant.SuccessBg : (Brush?)Ant.ErrorBg,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = AntTokens.FontCaption,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ok ? (Brush?)Ant.Success : (Brush?)Ant.Error,
                },
            };
        }
        void RefreshDetail(List<TopologyNode> nodes)
        {
            detailPanel.Children.Clear();
            detailPanel.Children.Add(new TextBlock
            {
                Text = "📊 从库明细",
                FontSize = AntTokens.FontMinor,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            if (nodes.Count == 0)
            {
                detailPanel.Children.Add(new TextBlock
                {
                    Text = "没有下游从库节点。",
                    FontSize = AntTokens.FontMinor,
                    Foreground = Ant.TextSub,
                });
                return;
            }
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string[] headers = { "实例", "复制状态", "延迟" };
            for (int c = 0; c < 3; c++)
            {
                var headCell = new Border
                {
                    Padding = new Thickness(0, 4, 0, 4),
                    Background = Ant.HeadBg,
                    Child = new TextBlock
                    {
                        Text = headers[c],
                        FontSize = AntTokens.FontMinor,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = Ant.TextSub,
                    },
                };
                Grid.SetColumn(headCell, c);
                grid.Children.Add(headCell);
            }
            for (int r = 0; r < nodes.Count; r++)
            {
                var n = nodes[r];
                grid.RowDefinitions.Add(new RowDefinition());
                var name = new TextBlock { Text = n.Name, FontSize = AntTokens.FontMinor, FontFamily = new FontFamily("Consolas") };
                var badge = StateBadge(n.State, n.State == "正常");
                var lag = new TextBlock
                {
                    Text = n.LagSeconds is null ? "—" : $"{n.LagSeconds:0.###}s",
                    FontSize = AntTokens.FontMinor,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = n.State switch
                    {
                        "正常" => Ant.Success,
                        "延迟偏高" or "严重滞后" => Ant.Warn,
                        "已断开" => Ant.Error,
                        _ => Ant.TextSub,
                    },
                };
                var cells = new UIElement[] { name, badge, lag };
                for (int c = 0; c < 3; c++)
                {
                    var cell = new Border { Padding = new Thickness(0, 4, 0, 4), Child = cells[c] };
                    Grid.SetColumn(cell, c);
                    Grid.SetRow(cell, r + 1);
                    grid.Children.Add(cell);
                }
            }
            detailPanel.Children.Add(grid);
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
        panel.Children.Add(detailPanel);   // 发现304原型：从库明细表
        panel.Children.Add(opRow);
        panel.Children.Add(status);

        _ = Safe.RunAsync(RefreshAsync, "主从管理面加载拓扑");
        return panel;
    }
}
