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

        var roleText = new TextBlock { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var nodeList = new StackPanel { Spacing = 4 };
        var status = new TextBlock { FontSize = 12, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        async Task RefreshAsync()
        {
            nodeList.Children.Clear();
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

                foreach (var n in nodes)
                {
                    var lagText = n.LagSeconds is null ? "—" : $"{n.LagSeconds:0.###}s";
                    var mark = n.State switch
                    {
                        "正常" => "●",
                        "延迟偏高" or "严重滞后" => "▲",
                        "已断开" => "✕",
                        _ => "○",
                    };
                    nodeList.Children.Add(new TextBlock
                    {
                        Text = $"{mark} {n.Name}｜{n.Role}｜{n.State}｜延迟 {lagText}｜{n.Detail}",
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                status.Text = $"共 {nodes.Count} 个节点。延迟 ≥{ReplicationCatalog.LagWarnSeconds:0}s 标黄（▲），≥{ReplicationCatalog.LagDangerSeconds:0}s 判严重滞后。";
            }
            catch (Exception ex)
            {
                status.Text = "拓扑采集失败：" + ex.Message + "（无复制管理权限或该库未配置主从时属正常）";
            }
        }

        Button OpButton(string text, bool danger = false)
        {
            var b = new Button { Content = text, CornerRadius = new CornerRadius(4) };
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

        var refreshBtn = new Button { Content = "刷新", CornerRadius = new CornerRadius(4) };
        refreshBtn.Click += async (_, _) => await RefreshAsync();

        var opRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        opRow.Children.Add(pauseBtn);
        opRow.Children.Add(resumeBtn);
        opRow.Children.Add(reconnectBtn);
        opRow.Children.Add(switchBtn);
        opRow.Children.Add(refreshBtn);

        var panel = new StackPanel { Spacing = 12, MinWidth = 520 };
        panel.Children.Add(roleText);
        panel.Children.Add(new ScrollViewer { Content = nodeList, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
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
