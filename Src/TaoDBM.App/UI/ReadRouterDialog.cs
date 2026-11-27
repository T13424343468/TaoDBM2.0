using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>读写路由对话框：延迟感知的读路由决策预演。
/// 输入副本节点画像（名称、延迟秒、是否健康）与最大可接受延迟，
/// 引擎只做纯计算：健康且延迟达标的副本按延迟升序作为读节点，全部不达标自动回落主库；
/// 另给每个副本摘除 / 加回建议，供流量开关对照。全程只读，不改任何状态。</summary>
public static class ReadRouterDialog
{
    public static Task Show(MainWindow owner, NodeInfo info)
    {
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var report = new StackPanel { Spacing = 10 };

        var nodesBox = new TextBox
        {
            AcceptsReturn = true,
            Height = 132,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = AntTokens.FontMinor,
            Text = "副本A,3,1\n副本B,12,1\n副本C,2,0",
        };
        var lagBox = new TextBox { Header = "最大可接受延迟（秒）", Text = "10", Width = 160 };
        var routeBtn = new Button { Content = "路由一次读请求", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var toggleBtn = new Button { Content = "摘除 / 加回建议", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };

        routeBtn.Click += async (_, _) => await RunRouteAsync(nodesBox.Text, lagBox.Text, report, status);
        toggleBtn.Click += async (_, _) => await RunToggleAsync(nodesBox.Text, lagBox.Text, report, status);

        // 整改三批三-11（2026-11-24 老板令「抓紧时间」）：演示式接真实数据——副本画像从当前连接的复制状态读取，
        // 不再只靠手填。只读查询（SHOW REPLICA STATUS / pg_stat_replication），失败如实提示不阻断手填路径。
        var realBtn = new Button { Content = "从当前连接读复制画像", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        realBtn.Click += async (_, _) => await RunRealReplicaAsync(info, nodesBox, report, status);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        btnRow.Children.Add(routeBtn);
        btnRow.Children.Add(toggleBtn);
        btnRow.Children.Add(realBtn);

        var panel = new StackPanel { Spacing = 12, MinWidth = 620 };
        panel.Children.Add(new TextBlock
        {
            Text = "每行一个副本：名称,延迟秒,健康(1/0)。引擎按健康且延迟达标的副本排序选读节点，全部不达标回落主库；纯计算，不触库。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ant.TextSub,
            FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(nodesBox);
        panel.Children.Add(lagBox);
        panel.Children.Add(btnRow);
        panel.Children.Add(new ScrollViewer
        {
            Content = report,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        panel.Children.Add(status);

        // ── 分片拓扑接入（P-6 联动二）：已注册分片计划并入主从/多节点视图（只读展示） ──
        var topoPanel = new StackPanel { Spacing = 4 };
        var topoTitle = new TextBlock
        {
            Text = "分片拓扑接入",
            FontSize = AntTokens.FontBody,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ant.Primary,
            Margin = new Thickness(0, 6, 0, 0),
        };
        topoPanel.Children.Add(topoTitle);
        var topoLoaded = ShardRouting.AllPlans();
        if (topoLoaded.Count == 0)
        {
            topoPanel.Children.Add(new TextBlock
            {
                Text = "暂无已注册的分片计划：在分库分区分表向导勾选「方案接入读写分离路由」后，分片节点会并入此视图。",
                FontSize = AntTokens.FontMinor,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Ant.TextSub,
            });
        }
        else
        {
            foreach (var plan in topoLoaded)
            {
                topoPanel.Children.Add(new TextBlock
                {
                    Text = ShardRouting.PlanSummary(plan),
                    FontSize = AntTokens.FontMinor,
                    Foreground = Ant.TextSub,
                    TextWrapping = TextWrapping.Wrap,
                });
                foreach (var n in ShardRouting.TopologyRows(plan))
                {
                    topoPanel.Children.Add(new TextBlock
                    {
                        Text = $"  [{n.Role}] {n.Name}｜{n.State}｜{n.Detail}",
                        FontSize = AntTokens.FontMinor,
                        FontFamily = new FontFamily("Consolas"),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
            }
            topoPanel.Children.Add(new TextBlock
            {
                Text = "分片节点为注册态只读展示：主从角色探测以 ReplicationCatalog 为准，分片路由决策见向导联动页。",
                FontSize = AntTokens.FontMinor,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Ant.TextSub,
            });
        }
        panel.Children.Add(topoPanel);

        var dlg = new ContentDialog
        {
            Title = "读写路由预演",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
        return Task.CompletedTask;
    }

    /// <summary>三-11：从当前连接读复制画像——MySQL 走 SHOW REPLICA STATUS（旧版退 SHOW SLAVE STATUS），
    /// PostgreSQL 走 pg_stat_replication；其余引擎如实提示暂无只读画像查询。结果回填副本框。</summary>
    private static async Task RunRealReplicaAsync(NodeInfo info, TextBox nodesBox, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        try
        {
            var provider = info.Provider ?? ProviderFactory.Create(info.Profile);
            string db = info.Kind == NodeKind.Table ? info.Db : (info.Db.Length > 0 ? info.Db : info.Name);
            var lines = new List<string>();
            if (provider.EngineName.Contains("MySQL", StringComparison.OrdinalIgnoreCase))
            {
                var rs = await TryQuery(provider, db, "SHOW REPLICA STATUS")
                         ?? await TryQuery(provider, db, "SHOW SLAVE STATUS");
                if (rs is null) { status.Text = "当前连接未返回复制状态（可能不是主库或无权限）。"; return; }
                var cols = rs.Columns;
                int Host() => IndexOfCol(cols, "Source_Host", "Host", "Master_Host");
                int Lag() => IndexOfCol(cols, "Seconds_Behind_Source", "Seconds_Behind_Source", "Seconds_Behind_Master");
                int Io() => IndexOfCol(cols, "Source_IO_Running", "Slave_IO_Running");
                foreach (var row in rs.Rows)
                {
                    string name = Host() >= 0 ? $"{row[Host()]}" : $"副本{lines.Count + 1}";
                    string lagRaw = Lag() >= 0 ? $"{row[Lag()]}" : "NULL";
                    int lag = int.TryParse(lagRaw, out var l) ? l : 0;   // NULL＝未启动，按 0 秒但健康置 0
                    string healthy = Io() >= 0 && $"{row[Io()]}".Equals("Yes", StringComparison.OrdinalIgnoreCase) && lagRaw != "NULL" ? "1" : "0";
                    lines.Add($"{name},{lag},{healthy}");
                }
            }
            else if (provider.EngineName.Contains("Postgre", StringComparison.OrdinalIgnoreCase))
            {
                var rs = await TryQuery(provider, db,
                    "SELECT COALESCE(application_name, client_addr::text, '副本') AS name, " +
                    "COALESCE(EXTRACT(EPOCH FROM (now() - write_lag))::bigint, 0) AS lag_s, 1 FROM pg_stat_replication");
                if (rs is null || rs.Rows.Count == 0) { status.Text = "pg_stat_replication 无副本连接（当前库可能不是主库）。"; return; }
                foreach (var row in rs.Rows)
                    lines.Add($"{row[0]},{Convert.ToInt64(row[1])},1");
            }
            else
            {
                status.Text = $"{provider.EngineName} 暂无只读复制画像查询，请手填画像。";
                return;
            }
            if (lines.Count == 0) { status.Text = "未读到任何副本。"; return; }
            nodesBox.Text = string.Join("\n", lines);
            AddSection(report, "真实复制画像已回填副本框");
            status.Text = $"读到 {lines.Count} 个副本，点「路由一次读请求」出结论。";
            AppLog.Write($"[读路由采样] replicas={lines.Count}");
        }
        catch (Exception ex)
        {
            status.Text = HumanError.Say(ex, "读取复制画像失败");
            AppLog.Write($"[读路由采样] error={ex.Message}");
        }
    }

    static async Task<QueryResult?> TryQuery(IDbProvider provider, string db, string sql)
    {
        try { return (await provider.Execute(db, sql)).FirstOrDefault(x => x.Rows.Count > 0 || x.Columns.Count > 0); }
        catch { return null; }
    }

    static int IndexOfCol(System.Collections.Generic.IReadOnlyList<string> cols, params string[] names)
    {
        foreach (var n in names)
            for (int i = 0; i < cols.Count; i++)
                if (cols[i].Equals(n, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static async Task RunRouteAsync(string nodesText, string lagText, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        var nodes = ParseNodes(nodesText, out var parseError);
        if (parseError.Length > 0)
        {
            status.Text = parseError;
            return;
        }
        if (!TryGetLag(lagText, out var maxLag, out var lagError))
        {
            status.Text = lagError;
            return;
        }

        var decision = ReadRouter.Route(nodes, maxLag);
        AddSection(report, "路由决策");
        if (!decision.HasTarget)
        {
            AddLine(report, "无可读节点：副本全部不可用且主库也不可用，请检查连接。", Ant.Error);
        }
        else if (decision.FellBackToPrimary)
        {
            AddLine(report, "本次读请求回落主库：没有健康且延迟达标的副本。", Ant.Warn);
        }
        else
        {
            AddLine(report, "命中副本（按延迟升序）：", Ant.Success);
            foreach (var name in decision.EligibleReplicas)
                AddLine(report, $"  {name}");
        }

        AddSection(report, "读流量分配建议");
        var serveList = ReadRouter.AutoToggle(nodes, maxLag).ToList();
        int serving = serveList.Count(s => s.shouldServe);
        // 发现309原型：权重卡——健康副本均分读流量，摘除节点置灰
        var cardRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (name, shouldServe) in serveList)
        {
            double share = shouldServe && serving > 0 ? 1.0 / serving : 0;
            cardRow.Children.Add(WeightCard(name, share, shouldServe));
        }
        report.Children.Add(cardRow);
        foreach (var (name, shouldServe) in serveList)
            AddLine(report, $"  {name}：{(shouldServe ? "继续承接读流量" : "建议摘除读流量")}", shouldServe ? null : Ant.Warn);

        status.Text = "路由预演完成（纯计算，未触库）。";
        await Task.CompletedTask;
    }

    private static Task RunToggleAsync(string nodesText, string lagText, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        var nodes = ParseNodes(nodesText, out var parseError);
        if (parseError.Length > 0)
        {
            status.Text = parseError;
            return Task.CompletedTask;
        }
        if (!TryGetLag(lagText, out var maxLag, out var lagError))
        {
            status.Text = lagError;
            return Task.CompletedTask;
        }

        AddSection(report, $"摘除 / 加回建议（阈值 {maxLag} 秒）");
        // 发现309原型：拓扑健康表——节点/健康/建议，表头底色，摘除行标红
        var table = new StackPanel { Spacing = 0 };
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Background = (Brush?)Application.Current.Resources["AntHeadBg"],
            Padding = new Thickness(10, 5, 10, 5),
        };
        foreach (var h in new[] { "节点", "健康", "建议" })
            header.Children.Add(new TextBlock
            {
                Text = h,
                Width = h == "节点" ? 160 : 110,
                FontSize = AntTokens.FontMinor,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Ant.TextSub,
            });
        table.Children.Add(header);
        foreach (var (name, shouldServe) in ReadRouter.AutoToggle(nodes, maxLag))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(10, 4, 10, 4) };
            row.Children.Add(new TextBlock { Text = name, Width = 160, FontSize = AntTokens.FontMinor, FontFamily = new FontFamily("Consolas") });
            row.Children.Add(new TextBlock
            {
                Text = shouldServe ? "健康" : "已摘除",
                Width = 110,
                FontSize = AntTokens.FontMinor,
                Foreground = shouldServe ? Ant.Success : Ant.Error,
            });
            row.Children.Add(new TextBlock
            {
                Text = shouldServe ? "加回读流量" : "摘除读流量（延迟超阈值）",
                FontSize = AntTokens.FontMinor,
                Foreground = shouldServe ? Ant.TextSub : Ant.Warn,
                TextWrapping = TextWrapping.Wrap,
            });
            table.Children.Add(row);
        }
        report.Children.Add(table);
        status.Text = "建议已生成（只读，不改流量开关）。";
        return Task.CompletedTask;
    }

    /// <summary>发现309原型：权重卡——健康副本均分读流量（比例条主色），摘除节点整卡置灰、比例条归零。</summary>
    private static Border WeightCard(string name, double share, bool serving)
    {
        var body = new StackPanel { Spacing = 3, Opacity = serving ? 1.0 : 0.55 };
        body.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = AntTokens.FontMinor,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontFamily = new FontFamily("Consolas"),
        });
        var track = new Border
        {
            Width = 100,
            Height = 8,
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusBadge),
            Background = (Brush?)Application.Current.Resources["AntAltRow"],
        };
        if (serving)
            track.Child = new Border
            {
                Width = Math.Max(2, 100 * share),
                Height = 8,
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusBadge),
                Background = Ant.Primary,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
        body.Children.Add(track);
        body.Children.Add(new TextBlock
        {
            Text = serving ? $"读权重 {share:P0}" : "已摘除",
            FontSize = AntTokens.FontCaption,
            Foreground = serving ? Ant.TextSub : Ant.Error,
        });
        return new Border
        {
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg),
            BorderThickness = new Thickness(1),
            BorderBrush = Ant.Border,
            Background = (Brush?)Application.Current.Resources["AntCard"],
            Child = body,
        };
    }

    private static List<ReplicaNode> ParseNodes(string text, out string error)
    {
        error = "";
        var nodes = new List<ReplicaNode>();
        var lines = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || !long.TryParse(parts[1], out var lag))
            {
                error = "格式有误：" + line;
                return new List<ReplicaNode>();
            }
            bool healthy = parts[2] is "1" or "true" or "是" or "健康";
            nodes.Add(new ReplicaNode(parts[0], lag, healthy));
        }
        if (nodes.Count == 0) error = "请先填写至少一个副本节点。";
        return nodes;
    }

    private static bool TryGetLag(string text, out long maxLag, out string error)
    {
        maxLag = 0;
        error = "";
        if (!long.TryParse((text ?? "").Trim(), out maxLag) || maxLag < 0)
        {
            error = "最大延迟需为非负整数（秒）。";
            return false;
        }
        return true;
    }

    private static void AddLine(StackPanel p, string text, Brush? brush = null)
    {
        p.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = AntTokens.FontMinor,
            TextWrapping = TextWrapping.Wrap,
            Foreground = brush ?? Ant.TextSub,
            FontFamily = new FontFamily("Consolas"),
        });
    }

    private static void AddSection(StackPanel p, string title)
    {
        p.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = AntTokens.FontBody,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ant.Primary,
            Margin = new Thickness(0, 6, 0, 0),
        });
    }
}
