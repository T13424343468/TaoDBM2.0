using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;
using TaoDBM.UI;

namespace TaoDBM;

/// <summary>布局批分部（2026-11-26，发现132/133/134/135）：顶部功能域直达、对象联动操作栏、
/// 选中即详情侧栏、欢迎页最近使用＋收藏。口径全部出自 LayoutBatchCore，测试锚 LayoutBatchCheck 逐条对账；
/// 原型（已过双闸）见 projects/1875692/workspace/布局批原型-20261126。</summary>
public partial class MainWindow
{
    // ---------- 发现132：顶部功能域直达 ----------

    private readonly Dictionary<string, Border> _navBars = new();
    private string _navLastMode = "";      // 最近打开的工作页签模式（Query / TableData）
    private bool _navBackupOpen, _navTaskOpen, _navErdOpen;

    /// <summary>按 DomainNav.Items 清单生成 5 个域按钮；当前域 3px 品牌条。
    /// 原型条色为品牌蓝，实装头部底色即品牌蓝，条色按对比度适配为 OnPrimary（白），提示语不变。</summary>
    private void BuildDomainNav()
    {
        HeadNav.Children.Clear();
        _navBars.Clear();
        foreach (var item in DomainNav.Items)
        {
            var bar = new Border
            {
                Height = 3,
                Width = 28,
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusBadge),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var label = new TextBlock
            {
                Text = item.Label,
                FontSize = AntTokens.FontMinor,
                Foreground = Ant.OnPrimary,
                Margin = new Thickness(2, 0, 2, 2),
            };
            var btn = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 4, 8, 0),
                Content = new StackPanel { Children = { label, bar } },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(btn, $"nav-{item.Key}");
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(btn, item.Tooltip);
            var key = item.Key;
            btn.Click += (_, _) => OnDomainNavClick(key);
            HeadNav.Children.Add(btn);
            _navBars[key] = bar;
        }
        UpdateDomainNav();
    }

    private void OnDomainNavClick(string key)
    {
        AppLog.Write($"[域导航] click key={key}");
        switch (key)
        {
            case "tables":
                _navBackupOpen = _navTaskOpen = _navErdOpen = false;
                ConnTree.Focus(FocusState.Programmatic);
                break;
            case "query":
                _navBackupOpen = _navTaskOpen = _navErdOpen = false;
                OnNewQuerySel(this, new RoutedEventArgs());
                break;
            case "backup":
                _navBackupOpen = true; _navTaskOpen = _navErdOpen = false;
                OnBackupSel(this, new RoutedEventArgs());
                break;
            case "taskcenter":
                _navTaskOpen = true; _navBackupOpen = _navErdOpen = false;
                OpenTaskCenterFromNav();
                break;
            case "erd":
                _navErdOpen = true; _navBackupOpen = _navTaskOpen = false;
                OnErdSel(this, new RoutedEventArgs());
                break;
        }
        UpdateDomainNav();
    }

    private async void OpenTaskCenterFromNav()
    {
        var info = SelectedInfo();
        if (info is null) { await InfoAsync("请先在左侧树选中一个连接，再打开任务中心。"); return; }
        await UI.TaskCenterDialog.Show(this, info);
    }

    /// <summary>当前域判定走 DomainNav.ActiveKey 口径；打开新域 / 切换工作页签后刷新。</summary>
    private void UpdateDomainNav()
    {
        var active = DomainNav.ActiveKey(_navLastMode, _navBackupOpen, _navTaskOpen, _navErdOpen);
        foreach (var (key, bar) in _navBars)
            bar.Background = key == active ? Ant.OnPrimary : Brushes.Transparent;
    }

    // ---------- 发现133：对象联动操作栏 ----------

    /// <summary>按选中对象类型刷新操作栏；无选中给状态一句话（IdleHint），不留空白。</summary>
    private void RefreshActionBar()
    {
        var info = SelectedInfo();
        string kind = info?.Kind switch
        {
            NodeKind.Table => "table",
            NodeKind.Database => "database",
            NodeKind.Connection => "connection",
            _ => "none",
        };
        var (_, actions) = ObjectActionBarModel.Build(kind);
        ActionBarHost.Children.Clear();
        if (kind == "none")
        {
            ActionBarHint.Text = ObjectActionBarModel.IdleHint(_profiles.Count);
            ActionBarHint.Visibility = Visibility.Visible;
            return;
        }
        ActionBarHint.Visibility = Visibility.Collapsed;
        foreach (var a in actions)
        {
            var btn = new Button
            {
                Content = a.Label,
                IsEnabled = a.Enabled,
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
                Padding = new Thickness(10, 4, 10, 4),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(btn, $"actionbar-{a.Key}");
            if (a.Primary) { btn.Background = Ant.Primary; btn.Foreground = Ant.OnPrimary; }
            if (!a.Enabled && a.Reason.Length > 0)
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(btn, a.Reason);
            var actKey = a.Key;
            btn.Click += (_, _) => OnActionBarAction(actKey, info!);
            ActionBarHost.Children.Add(btn);
        }
    }

    private async void OnActionBarAction(string key, NodeInfo info)
    {
        AppLog.Write($"[操作栏] click key={key} kind={info.Kind} name={info.Name}");
        switch (key)
        {
            case "open":
            case "import":   // 导入入口在数据浏览页签工具栏（WorkTab._importBtn），先开页签
            case "export":   // 导出同源：数据浏览页签内操作
                OpenTableTab(info);
                break;
            case "design":
            case "newtable":
                OnDesignSel(this, new RoutedEventArgs());
                break;
            case "query":
                OnNewQuerySel(this, new RoutedEventArgs());
                break;
            case "backup":
            case "restore":
                OnBackupSel(this, new RoutedEventArgs());
                break;
            case "taskcenter":
                OpenTaskCenterFromNav();
                break;
            case "newconn":
                OnNewConn(this, new RoutedEventArgs());
                break;
            case "ai":   // 发现138：AI 建表入口提级——占位改真实入口，未并入时人话引导，不报错不空白
                if (ObjectActionBarModel.AiTableAvailable)
                {
                    AppLog.Write("[AI建表] 入口点击：模型已并入，AI 建表流程随 AI 版批开放");
                    await InfoAsync("AI 建表已就绪：模型已并入，建表流程入口随 AI 版批开放。");
                }
                else
                {
                    AppLog.Write("[AI建表] 入口点击：模型未并入，人话引导");
                    await InfoAsync(ObjectActionBarModel.AiTableGuide);
                }
                break;
            case "delete" when info.Kind == NodeKind.Connection:
                OnDelConn(this, new RoutedEventArgs());
                break;
            case "delete":
                // 现版本无删表入口（全库检索无 DROP TABLE UI）；给人话指引，不做假删除
                await InfoAsync("当前版本未提供删表入口；如需清理请使用库工具中的「批量清理」。");
                break;
        }
    }

    // ---------- 发现134：选中即详情侧栏 ----------

    /// <summary>选中即刷新详情；元数据取不到的字段落「暂不可用」，失败不弹错误窗只落账。</summary>
    private async Task RefreshDetailPanel(NodeInfo info)
    {
        DetailPanel.Visibility = Visibility.Visible;
        DetailHost.Children.Clear();
        DetailHost.Children.Add(new TextBlock
        {
            Text = "详情",
            FontSize = AntTokens.FontEmphasis,
            Foreground = Ant.Text,
            Margin = new Thickness(0, 0, 0, 4),
        });
        List<DetailField> fields;
        List<(string Name, string Type, bool Pk)>? cols = null;
        try
        {
            string engine = info.Provider?.EngineName ?? info.Profile.Engine.ToString();
            switch (info.Kind)
            {
                case NodeKind.Table:
                    fields = DetailPanelModel.TableFields(info.Name, engine, null, null, null, null, null);
                    if (info.Provider is not null)
                        cols = DetailPanelModel.ColumnSummary(await info.Provider.GetColumns(info.Db, info.Name));
                    break;
                case NodeKind.Database:
                    string? objects = null;
                    if (info.Provider is not null)
                        objects = $"{(await info.Provider.GetChildren(info.Name)).Count} 个";
                    fields = DetailPanelModel.DatabaseFields(info.Name, engine, objects, null, null);
                    break;
                default:
                    fields = DetailPanelModel.ConnectionFields(info.Profile.DisplayName, engine,
                        info.Profile.Host, info.Profile.Port, info.ConnState.Length > 0 ? info.ConnState : "未连接");
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[详情侧栏] 取元数据失败 kind={info.Kind} name={info.Name}: {ex.Message}");
            fields = new List<DetailField>
            {
                new DetailField("对象", info.Name),
                new DetailField("元数据", DetailPanelModel.UnavailableText, false),
            };
        }
        // 异步取数期间选中可能已切换：过期结果直接丢弃
        var cur = SelectedInfo();
        if (cur is null || cur.Kind != info.Kind || cur.Name != info.Name || cur.Db != info.Db) return;
        foreach (var f in fields) DetailHost.Children.Add(DetailRow(f.Label, f.Value, f.Ok));
        if (cols is not null && cols.Count > 0)
        {
            DetailHost.Children.Add(new TextBlock
            {
                Text = $"列（{cols.Count}）",
                FontSize = AntTokens.FontMinor,
                Foreground = Ant.TextSub,
                Margin = new Thickness(0, 8, 0, 0),
            });
            foreach (var c in cols.Take(30))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                if (c.Pk)
                    row.Children.Add(new Border
                    {
                        Background = Ant.PrimaryBg,
                        CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusBadge),
                        Padding = new Thickness(4, 0, 4, 0),
                        Child = new TextBlock { Text = "PK", FontSize = AntTokens.FontTiny, Foreground = Ant.Primary },
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                row.Children.Add(new TextBlock { Text = c.Name, FontSize = AntTokens.FontMinor, Foreground = Ant.Text, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = c.Type, FontSize = AntTokens.FontTiny, Foreground = Ant.TextSub, VerticalAlignment = VerticalAlignment.Center });
                DetailHost.Children.Add(row);
            }
        }
    }

    private static Grid DetailRow(string label, string value, bool ok)
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = label, FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, VerticalAlignment = VerticalAlignment.Top });
        var v = new TextBlock
        {
            Text = value,
            FontSize = AntTokens.FontMinor,
            Foreground = ok ? Ant.Text : Ant.TextSub,
            TextWrapping = TextWrapping.Wrap,
            Grid.Column = 1,
        };
        g.Children.Add(v);
        return g;
    }

    // ---------- 发现135：欢迎页最近使用＋收藏 ----------

    /// <summary>欢迎页改版：新手条（可关闭，偏好落盘）＋收藏连接（星标）＋最近打开（会话恢复口径）。</summary>
    private void AddWelcomeTab()
    {
        var root = new StackPanel
        {
            Margin = new Thickness(AntTokens.SpaceXl),
            Spacing = AntTokens.SpaceMd,
            MaxWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        root.Children.Add(new TextBlock { Text = "欢迎使用 TaoDBM", FontSize = AntTokens.FontPage, Foreground = Ant.Text });

        if (WelcomePrefs.ShowGuide)
        {
            var guideText = new TextBlock
            {
                Text = "新手引导：右上角「＋ 新建连接」建好连接后，左侧树展开库与表，双击表即可浏览数据；选中后点「新建查询」，写 SQL 按 F5 执行。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = AntTokens.FontMinor,
                Foreground = Ant.Text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, AntTokens.SpaceSm, 0),
            };
            var dismiss = new Button
            {
                Content = "知道了",
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dismiss, "welcome-guide-dismiss");
            var guide = new Border
            {
                Background = Ant.PrimaryBg,
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
                Padding = new Thickness(AntTokens.SpaceMd),
                Child = new Grid { },
            };
            var gGrid = new Grid { ColumnSpacing = AntTokens.SpaceSm };
            gGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            guideText.SetValue(Grid.ColumnProperty, 0);
            dismiss.SetValue(Grid.ColumnProperty, 1);
            gGrid.Children.Add(guideText);
            gGrid.Children.Add(dismiss);
            guide.Child = gGrid;
            dismiss.Click += (_, _) =>
            {
                WelcomePrefs.DismissGuide();
                AppLog.Write("[欢迎页] guide dismissed");
                root.Children.Remove(guide);
            };
            root.Children.Add(guide);
        }

        // 收藏连接：星标优先（WelcomeBoard.StarredFirst 口径），只取星标项
        root.Children.Add(new TextBlock { Text = "收藏连接", FontSize = AntTokens.FontEmphasis, Foreground = Ant.Text, Margin = new Thickness(0, AntTokens.SpaceSm, 0, 0) });
        var starred = WelcomeBoard.StarredFirst(_profiles.Where(p => !p.Hidden)).Where(p => p.Starred).Take(5).ToList();
        if (starred.Count == 0)
            root.Children.Add(new TextBlock { Text = "暂无收藏：在连接右键菜单里「星标」即可收藏到这里。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        foreach (var p in starred)
        {
            var pRef = p;
            var btn = new Button
            {
                Content = $"{pRef.DisplayName} · {pRef.Engine}",
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
                Padding = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(btn, "welcome-starred");
            btn.Click += (_, _) =>
            {
                AppLog.Write($"[欢迎页] open starred id={pRef.Id}");
                SelectConnectionInTree(pRef.Id);
            };
            root.Children.Add(btn);
        }

        // 最近打开：上次会话页签（TabSession.Load 口径），相对时间取页签打开时刻
        root.Children.Add(new TextBlock { Text = "最近打开", FontSize = AntTokens.FontEmphasis, Foreground = Ant.Text, Margin = new Thickness(0, AntTokens.SpaceSm, 0, 0) });
        var recents = TabSession.FilterValid(TabSession.Load(), _profiles).Take(6).ToList();
        if (recents.Count == 0)
            root.Children.Add(new TextBlock { Text = "暂无最近打开的页签。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        foreach (var it in recents)
        {
            var item = it;
            string time = item.LastOpened is { } t ? WelcomeBoard.RelativeTime(t, DateTime.Now) : "";
            var btn = new Button
            {
                Content = WelcomeBoard.RecentLabel(item.Mode, item.Db, item.Child) + (time.Length > 0 ? $"（{time}）" : ""),
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
                Padding = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(btn, "welcome-recent");
            btn.Click += (_, _) =>
            {
                AppLog.Write($"[欢迎页] reopen mode={item.Mode} db={item.Db} child={item.Child}");
                ReopenSessionItem(item);
            };
            root.Children.Add(btn);
        }

        root.Children.Add(new TextBlock
        {
            Text = "支持：MySQL · PostgreSQL · SQL Server · SQLite · Oracle · MongoDB · Redis · OceanBase · GaussDB · 达梦 · 金仓 · PolarDB · TDSQL",
            TextWrapping = TextWrapping.Wrap,
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.TextSub,
            Margin = new Thickness(0, AntTokens.SpaceSm, 0, 0),
        });

        var tab = new TabViewItem
        {
            Header = "开始",
            IsClosable = false,
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        WorkTabs.TabItems.Add(tab);
        WorkTabs.SelectedItem = tab;
    }

    /// <summary>收藏点击：在对象树里选中对应连接根节点（找不到则提示先刷新）。</summary>
    private void SelectConnectionInTree(string profileId)
    {
        foreach (var root in ConnTree.Roots)
        {
            if (_nodeMap.TryGetValue(root, out var i) && i.Profile.Id == profileId)
            {
                ConnTree.SelectedItem = root;
                return;
            }
        }
        _ = InfoAsync("该连接不在当前树中，请先刷新。");
    }

    /// <summary>最近打开点击：按会话口径重开查询 / 数据浏览页签。</summary>
    private void ReopenSessionItem(TabSessionItem item)
    {
        var p = _profiles.FirstOrDefault(x => x.Id == item.ConnectionId);
        if (p is null) { _ = InfoAsync("该页签的连接已删除，无法重开。"); return; }
        if (item.Mode == "TableData" && item.Child.Length > 0)
        {
            var tInfo = new NodeInfo { Kind = NodeKind.Table, Profile = p, Provider = ProviderFactory.Create(p), Db = item.Db, Name = item.Child };
            OpenTableTab(tInfo);
        }
        else
        {
            var qInfo = new NodeInfo
            {
                Kind = NodeKind.Database,
                Profile = p,
                Provider = ProviderFactory.Create(p),
                Db = item.Db,
                Name = item.Db.Length > 0 ? item.Db : p.DisplayName,
            };
            OpenQueryTab(qInfo);
        }
    }
}
