using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using TaoDBM.Core;
using TaoDBM.Infrastructure;
using TaoDBM.UI;

namespace TaoDBM;

public enum NodeKind { Connection, Database, Table, Column, Key, Category, ObjectDef, BackupHistory, BackupEntry }

/// <summary>树节点附加信息。</summary>
public class NodeInfo
{
    public NodeKind Kind;
    public ConnectionProfile Profile = new();
    public IDbProvider? Provider;
    public string Db = "";
    public string Name = "";
    public bool Loaded;
    public string Category = "";  // B2 对象树：views/indexes/fks/routines/triggers
    public string RowText = "";   // B2 对象树：目录行原文（索引/外键无 DDL，直接展示）
    public string ConnState = ""; // 格18：连接运行态 ""=未知 / "Connected"=绿点 / "Failed"=红点（随 RebuildRoots 按 Profile.Id 保留）
    public BackupHistory.Entry? BackupEntry;   // 发现144：备份树条目原文（还原 / 删除要用）
}

public partial class MainWindow : Window
{
    private readonly List<ConnectionProfile> _profiles = new();
    private readonly Dictionary<TreeViewNode, NodeInfo> _nodeMap = new();
    /// <summary>打开着的工作标签（会话保存用；欢迎页不在内）。</summary>
    private readonly Dictionary<TabViewItem, UI.WorkTab> _workTabs = new();

    /// <summary>版本号来自程序集版本，让「装的是哪版」一眼可见（窗口标题与顶栏同源）。</summary>
    private static string VersionLabel()
    {
        var v = typeof(MainWindow).Assembly.GetName().Version;
        return v == null ? "" : $" v{v.Major}.{v.Minor}.{v.Build}";
    }

    public MainWindow()
    {
        InitializeComponent();
        Title = "TaoDBM" + VersionLabel();
        AppTitleText.Text = Title;
        Core.AppLog.Write($"TaoDBM 启动 {Title}");
        // 发现114：老库兼容降级重连成功后一次性人话提示（状态栏一句话，不弹窗打断）
        Core.AdaptiveDbConnection.FallbackConnected += msg => _ = DispatcherQueue.TryEnqueue(() => _ = InfoAsync(msg));
        UI.Ant.ThemeApplied += PinTitleBar;
        UI.Ant.ThemeApplied += PinRootTheme;
        PinTitleBar(Core.ThemePalette.Get(Core.ThemeStore.Load()));
        PinRootTheme(Core.ThemePalette.Get(Core.ThemeStore.Load()));
        // 顶栏两处下拉菜单是弹层，挂窗口内容之外，须逐个钉明暗象（换肤后再开也随象）
        BtnLogo.Flyout.PinTheme();
        BtnTheme.Flyout.PinTheme();
        UpdateThemeChecks();
        // F1 全局唤起操作手册（2026-11-20 老板令）：挂在根 Grid 上，任何焦点下都响应
        var f1 = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.F1 };
        f1.Invoked += (_, args) => { args.Handled = true; UI.ManualDialog.Show(this); };
        var rootGridF1 = (Microsoft.UI.Xaml.Controls.Grid)Content;
        rootGridF1.KeyboardAccelerators.Add(f1);
        // 预修批修红（云测 201/210 实锤）：WinUI 默认（PlacementMode=Auto）把加速键组合渲染成 tooltip，
        // 树区悬停残留「F1」游离悬浮层；置 Hidden 只关提示展示，F1/F11/Ctrl+N 等唤起行为不变
        rootGridF1.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        var trayIcoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "TaoDBM.ico");
        Safe.Run(() =>
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "TaoDBM.png");
            if (System.IO.File.Exists(iconPath))
            {
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
                LogoImg.Source = bmp;
            }
            if (System.IO.File.Exists(trayIcoPath))
                AppWindow.SetIcon(trayIcoPath);
        }, "窗口图标");
        // 托盘创建不再以图标文件存在为前提（2026-11-03 真机二轮反馈）：图标缺失时 TrayIcon 内部回落系统图标，
        // 绝不因资源缺失整段静默跳过导致托盘整个没有。
        try { StartTray(trayIcoPath); }
        catch (Exception ex) { Core.AppLog.Write($"托盘: 创建异常 {ex.Message}"); }
        // 标题栏保持系统默认（Standard）；在此显式设置会触发 WindowsAppSDK 0x8007139F 崩溃，勿再加回。
        AppWindow.Closing += OnAppWinClosing;

        // P4：通知点击路由——NotificationInvoked 在后台线程派发，这里切回 UI 线程再跳入口
        UI.ToastCenter.RouteRequested += OnToastRoute;

        LoadConnections();
        BuildDomainNav();      // 发现132：顶部功能域直达
        RefreshActionBar();    // 发现133：操作栏初始态（无选中给状态一句话）
        AddWelcomeTab();
        RestoreSession();
        ApplyWindowState();
        // 手册承诺兑现（2026-11-28）：命令行 -run-task 启动立即执行——主窗就绪即排水，
        // 不再等任务中心打开才执行（计划任务/无人值守场景依赖此路径）
        UI.TaskCenterDialog.DrainPendingOnStartup(this);
        // 批次六格3：F11 专注模式——根网格挂加速键；上次退出为专注态则原样恢复
        if (Content is Grid rootGrid)
        {
            var f11 = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.F11 };
            f11.Invoked += (_, args) => { args.Handled = true; ToggleFocusMode(); };
            rootGrid.KeyboardAccelerators.Add(f11);
            // 整改三批三-2：快捷键三件套——Ctrl+N 新建查询 / Ctrl+W 关当前页签 / Ctrl+Tab(+Shift) 循环切页签
            var ctrlN = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.N };
            ctrlN.Invoked += (_, args) => { args.Handled = true; OnNewQuerySel(this, new RoutedEventArgs()); Core.AppLog.Write("[快捷键] n 新建查询"); };
            rootGrid.KeyboardAccelerators.Add(ctrlN);
            var ctrlW = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.W };
            ctrlW.Invoked += (_, args) => { args.Handled = true; CloseCurrentTab(); };
            rootGrid.KeyboardAccelerators.Add(ctrlW);
            var ctrlTab = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.Tab };
            ctrlTab.Invoked += (_, args) => { args.Handled = true; CycleWorkTab(forward: true); };
            rootGrid.KeyboardAccelerators.Add(ctrlTab);
            var ctrlShiftTab = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Modifiers = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, Key = Windows.System.VirtualKey.Tab };
            ctrlShiftTab.Invoked += (_, args) => { args.Handled = true; CycleWorkTab(forward: false); };
            rootGrid.KeyboardAccelerators.Add(ctrlShiftTab);
        }
        RestoreFocusMode();
        SizeChanged += EnforceMinimumSize;
        SizeChanged += OnSizeTierChanged;   // 批次H：弹性布局三档（第十五章）
        ApplyLayoutTier();
        EnableSqlDrop();
        DispatcherQueue.TryEnqueue(LoadSqlFromCommandLine);
        Closed += (_, _) => { SaveWindowState(); SaveSession(); TunnelResolver.StopAll(); };
    }

    /// <summary>系统通知点击跳转：回主窗并打开对应功能入口（P4）。路由键见 ToastCenter.Route。</summary>
    private void OnToastRoute(string target)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ShowFromTray();
            switch (target)
            {
                case "taskcenter":
                    if (SelectedInfo() is { } ti) OpenPanelTab("任务中心", () => UI.TaskCenterDialog.BuildPanel(this, ti));
                    else _ = InfoAsync("点通知跳转：请先在左侧树中选中一个连接。");
                    break;
                case "backup":
                    OnBackupSel(this, new RoutedEventArgs());
                    break;
                case "risky":
                    if (SelectedInfo() is { } ri) OpenPanelTab("高危 SQL 拦截", () => UI.RiskySqlGuardDialog.BuildPanel(this, ri));
                    else _ = InfoAsync("点通知跳转：请先在左侧树中选中一个连接。");
                    break;
                case "migration":
                    if (SelectedInfo() is { } mi) OpenPanelTab("迁移续传", () => UI.MigrationResumeDialog.BuildPanel(this, mi));
                    else _ = InfoAsync("点通知跳转：请先在左侧树中选中一个连接。");
                    break;
                case "health":
                    SetStatus("健康检查异常：详情见最近一次数据体检报告。");
                    break;
                default:
                    SetStatus("来自系统通知。");
                    break;
            }
        });
    }

    // ---------- 托盘与关闭行为 ----------

    private UI.TrayIcon? _tray;
    private bool _allowClose;
    private bool _balloonShown;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _trayRetry;

    /// <summary>创建托盘：消息托管在主窗句柄上（子类化转发），回调在本 UI 线程派发，动作无需再切线程。</summary>
    private void StartTray(string icoPath)
    {
        _tray = new UI.TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this), icoPath, () => Core.ThemeStore.Load());
        _tray.RecentProvider = () => _profiles.Take(UI.TrayIcon.RecentMax)
            .Select(p => (p.Id, p.DisplayName)).ToList();
        _tray.OpenRequested += ShowFromTray;
        _tray.NewConnectionRequested += () => OnNewConn(this, new RoutedEventArgs());
        _tray.ThemeRequested += name => SwitchTheme(name);
        _tray.SettingsRequested += () => OpenPanelTab("设置", () => UI.SettingsDialog.BuildPanel(this));   // 页签化改造：设置入页签
        _tray.ExitRequested += ExitApp;
        _tray.RecentRequested += TrayConnect;
        if (!_tray.IsAdded) StartTrayRetry();   // 没一次挂上就短周期重试，不静默放弃
    }

    /// <summary>托盘未挂上时的自动重试（启动早期桌面可能未就绪）：最多 3 次、每次 2 秒，结果如实反映到 IsAdded。</summary>
    private void StartTrayRetry()
    {
        _trayRetry?.Stop();
        var timer = DispatcherQueue.CreateTimer();
        _trayRetry = timer;
        int tries = 0;
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += (_, _) =>
        {
            if (_tray is null || _tray.IsAdded || tries >= 3) { timer.Stop(); return; }
            tries++;
            Core.AppLog.Write($"托盘: 自动重试 {tries}/3");
            _tray.RetryAdd();
        };
        timer.Start();
    }

    /// <summary>托盘「我的连接」：回主窗并选中、展开该连接（展开即连库拉库列表）。</summary>
    private void TrayConnect(string id)
    {
        ShowFromTray();
        foreach (var kv in _nodeMap)
        {
            if (kv.Value.Kind != NodeKind.Connection || kv.Value.Profile.Id != id) continue;
            ConnTree.SelectedItem = kv.Key;
            kv.Key.IsExpanded = true;
            return;
        }
    }

    /// <summary>.sql 跨实例交接入口：前置窗口并装载（收口与托盘唤醒一致）。</summary>
    public void ActivateFromHandoff() => ShowFromTray();

    private void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    private void HideToTray()
    {
        try { AppWindow.Hide(); }
        catch { return; }   // 隐藏失败就留在原地，绝不玩消失
        if (!_balloonShown)
        {
            _balloonShown = true;
            _tray?.ShowInfo("TaoDBM 已最小化到托盘", "点托盘图标可随时回来。");
        }
    }

    private void ExitApp()
    {
        _allowClose = true;
        _tray?.Dispose();
        _tray = null;
        Close();
    }

    /// <summary>关闭拦截：按设置走「最小化到托盘 / 直接退出 / 每次询问」；托盘没挂上时不拦——关窗即退出，绝不玩消失。</summary>
    private void OnAppWinClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || _tray is null || !_tray.IsAdded)
        {
            // 诊断信息一律走日志不弹窗（2026-11-03 用户定调）：托盘没挂上时如实记录，关窗按退出处理
            Core.AppLog.Write(_tray is null
                ? "关窗: 托盘未创建，按退出处理"
                : !_tray.IsAdded ? "关窗: 托盘未挂上，按退出处理（原因见前序托盘日志）" : "关窗: 退出");
            return;
        }
        args.Cancel = true;
        switch (Core.AppPrefs.Load())
        {
            case Core.CloseBehavior.Tray: HideToTray(); break;
            case Core.CloseBehavior.Exit: ExitApp(); break;
            default: _ = AskCloseAsync(); break;
        }
    }

    private async Task AskCloseAsync()
    {
        var remember = new CheckBox { Content = "记住我的选择，下次不再问" };
        var dlg = new ContentDialog
        {
            Title = "要最小化到托盘，还是退出？",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "最小化到托盘后 TaoDBM 在后台继续运行，点托盘图标随时回来。", TextWrapping = TextWrapping.Wrap },
                    remember,
                }
            },
            PrimaryButtonText = "最小化到托盘",
            SecondaryButtonText = "退出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dlg.ShowThemedAsync();
        if (result == ContentDialogResult.None) return;   // 取消：留在原状
        if (remember.IsChecked == true)
            Core.AppPrefs.Save(result == ContentDialogResult.Primary ? Core.CloseBehavior.Tray : Core.CloseBehavior.Exit);
        if (result == ContentDialogResult.Primary) HideToTray();
        else ExitApp();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        OpenPanelTab("设置", () => UI.SettingsDialog.BuildPanel(this));   // 页签化改造：设置入页签
    }

    public void SetStatus(string s) => StatusText.Text = s;

    // ---------- 连接管理 ----------

    private void LoadConnections()
    {
        _profiles.Clear();
        _profiles.AddRange(ConnectionStore.Load());
        RebuildRoots();
    }

    private void RebuildRoots()
    {
        // 格18：状态随重建保留（按 Profile.Id 映射），筛选只隐藏不销毁（_profiles 不动）
        var states = _nodeMap.Values.Where(i => i.ConnState.Length > 0)
            .GroupBy(i => i.Profile.Id).ToDictionary(g => g.Key, g => g.First().ConnState);
        ConnTree.RootNodes.Clear();
        _nodeMap.Clear();
        var filter = (ConnFilter.Text ?? "").Trim();
        int shown = 0;
        // 批次二格6：虚拟分组——组节点作顶层文件夹，成员连接嵌组内；未分组连接保持顶层。组只是视图，连接数据不动。
        var groupNames = ConnectionGroups.List().Select(g => g.Name).ToList();
        var groupNodes = new Dictionary<string, TreeViewNode>();
        foreach (var name in groupNames)
        {
            var gInfo = new NodeInfo { Kind = NodeKind.Category, Name = $"group:{name}" };
            var gNode = new TreeViewNode { IsExpanded = false, Content = NodeIconText("group", name) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(gNode, $"ConnGroup_{name}");
            _nodeMap[gNode] = gInfo;
            ConnTree.RootNodes.Add(gNode);
            groupNodes[name] = gNode;
        }
        // E②：星标置顶（保持原相对顺序）；隐藏连接不进常规树，收进「已隐藏」折叠组
        var hiddenList = new List<(TreeViewNode Node, NodeInfo Info)>();
        foreach (var p in _profiles.OrderByDescending(p => p.Starred ? 1 : 0))
        {
            // 格18：筛选——DisplayName/Host 不区分大小写包含匹配；清空恢复全部
            if (filter.Length > 0 &&
                !(p.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                  p.Host.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                continue;
            shown++;
            var info = new NodeInfo { Kind = NodeKind.Connection, Profile = p, Name = p.DisplayName, ConnState = states.GetValueOrDefault(p.Id, "") };
            var node = new TreeViewNode { IsExpanded = false };
            SetNodeContent(node, info);   // 格18：状态点着色（绿=Connected / 红=Failed），无状态保持纯文本
            node.Children.Add(new TreeViewNode { Content = "…" }); // 占位，展开时填充
            _nodeMap[node] = info;
            if (p.Hidden) { hiddenList.Add((node, info)); continue; }   // E②：隐藏连接进「已隐藏」组
            var groupName = p.Group.Length > 0 ? p.Group : ConnectionGroups.GroupOf(p.Id);   // E②：档案分组字段优先，虚拟分组兜底
            if (groupName.Length > 0 && groupNodes.TryGetValue(groupName, out var gNode)) gNode.Children.Add(node);
            else ConnTree.RootNodes.Add(node);
        }
        // E②：「已隐藏（N）」折叠组——隐藏连接不删配置，可右键找回
        if (hiddenList.Count > 0)
        {
            var hInfo = new NodeInfo { Kind = NodeKind.Category, Name = "hidden" };
            var hNode = new TreeViewNode { IsExpanded = false, Content = NodeIconText("group", $"已隐藏（{hiddenList.Count}）") };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(hNode, "ConnHidden");
            _nodeMap[hNode] = hInfo;
            foreach (var (hn, _) in hiddenList) hNode.Children.Add(hn);
            ConnTree.RootNodes.Add(hNode);
        }
        // 组内连接被筛选隐藏时组节点仍显示（组是视图），空组如实标注
        foreach (var g in groupNodes) if (g.Value.Children.Count == 0) g.Value.Children.Add(new TreeViewNode { Content = "（空分组）" });
        SetStatus(filter.Length > 0 ? $"筛选「{filter}」：{shown}/{_profiles.Count} 个连接" : $"共 {_profiles.Count} 个连接 · {groupNames.Count} 个分组" + (hiddenList.Count > 0 ? $" · 已隐藏 {hiddenList.Count}" : ""));
        UpdateConnCount();   // Navicat 口径：状态栏右侧常驻连接计数
        AppLog.Write($"[连接树] filter={filter} shown={shown} groups={groupNames.Count} hidden={hiddenList.Count}");   // 格18：走查 S85 断言锚点
    }

    /// <summary>状态栏右侧连接计数（Navicat「N 连接」口径）：N=配置数，M=已连上（绿点）数。</summary>
    private void UpdateConnCount()
    {
        int live = _nodeMap.Values
            .Where(i => i.Kind == NodeKind.Connection && i.ConnState == "Connected")
            .Select(i => i.Profile.Id).Distinct().Count();
        ConnCountText.Text = $"{_profiles.Count} 连接 · {live} 已连接";
    }

    /// <summary>格18：筛选框输入即过滤（清空恢复全部）；XAML 解析期 TextChanged 可能早于树就绪，判空防崩。</summary>
    private void OnConnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (ConnTree is null) return;
        RebuildRoots();
    }

    /// <summary>格18：根节点内容——无状态纯文本；Connected 绿点 / Failed 红点前缀（颜色随主题语义色）。</summary>
    /// <summary>整改三批三-4：对象树图标 Fluent 化——emoji 改 Segoe MDL2/Fluent 字形，跨 Windows 版本渲染一致。</summary>
    private static readonly System.Collections.Generic.Dictionary<string, string> NodeGlyphs = new()
    {
        ["conn"] = "\uE717",    // World——连接
        ["group"] = "\uE8B7",   // Folder——分组/库
        ["table"] = "\uE8A5",   // Document——表/集合
        ["backup"] = "\uE8E5",  // 发现144：备份（SaveLocal 储存字形）
    };

    private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");

    /// <summary>#288 B 路线定谳：TreeViewItem 对等名取 Content.ToString()，Content 元素上的 AutomationProperties</summary>
    /// <summary>传不上来（#285 证伪），容器 SetName 随虚拟化重建丢失（#286 证伪）。改用重写 ToString 的面板作</summary>
    /// <summary>Content：名字随 Content 对象持久、无竞态，Fluent 图标保留，兼修无障碍读屏。</summary>
    private sealed class NodePanel : StackPanel
    {
        public string NodeName { get; set; } = "";
        public override string ToString() => NodeName;
    }

    private static StackPanel NodeIconText(string glyphKey, string text)
    {
        var host = new NodePanel { NodeName = text, Orientation = Orientation.Horizontal, Spacing = 5 };
        host.Children.Add(new FontIcon { Glyph = NodeGlyphs[glyphKey], FontFamily = IconFont, FontSize = AntTokens.FontMinor, VerticalAlignment = VerticalAlignment.Center, Foreground = Ant.TextSub });
        host.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return host;
    }

    private static void SetNodeContent(TreeViewNode node, NodeInfo info)
    {
        string text = (info.Profile.Starred ? "⭐ " : "") + $"{info.Profile.DisplayName}（{EngineInfo.Name(info.Profile.Engine)}）";   // E②：星标前缀置顶标识
        if (info.ConnState.Length == 0) { node.Content = NodeIconText("conn", text); return; }
        var host = NodeIconText("conn", text);
        var dot = new TextBlock
        {
            Text = "●",
            FontSize = AntTokens.FontTiny,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = info.ConnState == "Connected" ? Ant.Success : Ant.Error,
        };
        host.Children.Insert(1, dot);   // 状态点插在图标与文本之间
        node.Content = host;
    }

    /// <summary>格18：展开成败后就地刷新该根节点状态点（不重建整树）。</summary>
    private void SetNodeStateDot(TreeViewNode node, NodeInfo info) => SetNodeContent(node, info);

    private async void OnNewConn(object sender, RoutedEventArgs e)
    {
        // 保存链路（_profiles.Add→ConnectionStore.Save→RebuildRoots）任何一环抛错都曾是 async void 停存异常崩溃点
        //（云端 #73/#75 实锤 0xC000027B）：业务主体走 Safe.RunAsync 兜底，保存失败改为界面报错不崩（#76 定谳修复）。
        await Safe.RunAsync(async () =>
        {
            var dlg = new UI.ConnectionDialog(new ConnectionProfile());
            bool saved = await dlg.ShowAsync(this, isNew: true);
            if (saved)
            {
                // 树随内存立即重建，与存盘成败解耦：存盘抛错只弹提示，树不得缺节点（#77 S06 红场，2026-11-18）
                _profiles.Add(dlg.Profile);
                string? saveErr = null;
                try
                {
                    ConnectionStore.Save(_profiles);
                }
                catch (Exception ex)
                {
                    saveErr = ex.Message;
                    AppLog.Write($"[新建连接] 存盘失败: {ex.Message}");
                }
                // 树先随内存重建再弹错：模态报错框会挂起其后代码，RebuildRoots 排在弹框后时框不关树不刷（#78 定谳）
                RebuildRoots();
                if (saveErr != null) await InfoAsync($"连接存盘失败：{saveErr}");
                SetStatus($"已添加连接 {dlg.Profile.DisplayName}");
            }
        }, "新建连接");
    }

    private async void OnEditConn(object sender, RoutedEventArgs e)
    {
        await Safe.RunAsync(async () =>
        {
            var info = SelectedInfo();
            if (info?.Kind != NodeKind.Connection) { await InfoAsync("请先在左侧树中选中一个连接。"); return; }
            var dlg = new UI.ConnectionDialog(info.Profile);
            bool saved = await dlg.ShowAsync(this, isNew: false);
            if (saved)
            {
                // 树随内存立即重建，与存盘成败解耦：存盘抛错只弹提示，树不得缺节点（#77 S06 红场，2026-11-18）
                string? saveErr = null;
                try
                {
                    ConnectionStore.Save(_profiles);
                }
                catch (Exception ex)
                {
                    saveErr = ex.Message;
                    AppLog.Write($"[编辑连接] 存盘失败: {ex.Message}");
                }
                // 树先随内存重建再弹错：模态报错框会挂起其后代码（#78 定谳）
                RebuildRoots();
                if (saveErr != null) await InfoAsync($"连接存盘失败：{saveErr}");
            }
        }, "编辑连接");
    }

    private void OnThemeInk(object sender, RoutedEventArgs e) => SwitchTheme("ink");

    /// <summary>非客户区随象而染：系统标题栏与三颗门钮钉成当前主题色（TaoZip v60 教训）。</summary>
    private void PinTitleBar(IReadOnlyDictionary<string, string> pal)
    {
        Safe.Run(() =>
        {
            var bar = AppWindow.TitleBar;
            bar.BackgroundColor = UI.Ant.Brush(pal["AntHeadBg"]).Color;
            bar.ForegroundColor = UI.Ant.Brush(pal["AntText"]).Color;
            bar.ButtonBackgroundColor = UI.Ant.Brush(pal["AntHeadBg"]).Color;
            bar.ButtonForegroundColor = UI.Ant.Brush(pal["AntText"]).Color;
            bar.ButtonHoverBackgroundColor = UI.Ant.Brush(pal["AntBorder"]).Color;
            bar.ButtonHoverForegroundColor = UI.Ant.Brush(pal["AntText"]).Color;
        }, "标题栏配色");
    }

    /// <summary>客户区根元素重钉系统明暗象：内容对话框、菜单、下拉、输入框等系统控件随象而染
    /// （弹层从所在元素取象，根钉住即全树继承，切肤即时生效无需重启）。</summary>
    private void PinRootTheme(IReadOnlyDictionary<string, string> pal)
    {
        if (Content is FrameworkElement root)
            root.RequestedTheme = UI.Ant.ElementTheme;
    }

    private void OnThemeGray(object sender, RoutedEventArgs e) => SwitchTheme("gray");
    private void OnThemeNight(object sender, RoutedEventArgs e) => SwitchTheme("night");

    /// <summary>切主题：改画刷颜色即时全窗口生效，选择持久化（Tao 系列水墨默认）。</summary>
    private void SwitchTheme(string name)
    {
        Safe.Run(() =>
        {
            UI.Ant.ApplyTheme(name);
            Core.ThemeStore.Save(name);
            SetStatus(name switch { "gray" => "主题：天青", "night" => "主题：墨夜", _ => "主题：水墨" });
            UpdateThemeChecks();
        }, "切主题");
    }

    /// <summary>主题菜单对勾随象刷新（TaoPad 同款：当前主题项左侧带勾选）。</summary>
    private void UpdateThemeChecks()
    {
        var cur = Core.ThemeStore.Load();
        ThemeInk.IsChecked = cur == "ink";
        ThemeSky.IsChecked = cur == "gray";
        ThemeNight.IsChecked = cur == "night";
    }

    private async void OnDelConn(object sender, RoutedEventArgs e)
    {
        await Safe.RunAsync(async () =>
        {
            var info = SelectedInfo();
            if (info?.Kind != NodeKind.Connection) { await InfoAsync("请先选中一个连接。"); return; }
            await DeleteConnection(info);
        }, "删除连接");
    }

    /// <summary>删除连接：确认 → 断开隧道 → 关闭该连接已打开的页签 → 移除并重建树。</summary>
    private async Task DeleteConnection(NodeInfo info)
    {
        bool ok = await ConfirmAsync($"确定删除连接“{info.Profile.DisplayName}”吗？");
        if (!ok) return;
        TunnelResolver.StopProfile(info.Profile.Id);
        info.Provider?.Dispose();
        var dead = _workTabs.Where(kv => kv.Value.SessionConnectionId == info.Profile.Id).Select(kv => kv.Key).ToList();
        foreach (var tab in dead) { _workTabs.Remove(tab); WorkTabs.TabItems.Remove(tab); }
        _profiles.Remove(info.Profile);
        string? saveErr = null;
        try
        {
            ConnectionStore.Save(_profiles);
        }
        catch (Exception ex)
        {
            saveErr = ex.Message;
            AppLog.Write($"[删除连接] 存盘失败: {ex.Message}");
        }
        // 树先随内存重建再弹错：模态报错框会挂起其后代码（#78 定谳）
        RebuildRoots();
        SetStatus($"已删除连接 {info.Profile.DisplayName}");
        AppLog.Write($"[删除连接] 成功: {info.Profile.DisplayName}");   // 发现130：成功路径补落账
        if (saveErr != null) await InfoAsync($"连接存盘失败：{saveErr}");
        // P4：危险操作执行完成 → 通知调度（规则判定不够格就静默）
        ToastCenter.Publish(new NotifyEvent(NotifyKind.RiskySqlDone, info.Profile.DisplayName,
            "危险操作完成", $"连接 {info.Profile.DisplayName} 已删除", DateTime.Now));
    }

    // ---------- 树交互 ----------

    /// <summary>批次二格6：把连接移到虚拟分组（输入组名，不存在自动建；留空取消）。</summary>
    private async Task MoveToGroup(NodeInfo info)
    {
        var box = new TextBox { Header = "分组名（不存在自动创建；留空取消）", PlaceholderText = "如：生产环境 / 测试环境" };
        var dlg = new ContentDialog
        {
            Title = $"把「{info.Profile.DisplayName}」移到分组",
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowThemedAsync() != ContentDialogResult.Primary) return;
        var name = box.Text.Trim();
        if (name.Length == 0) return;
        ConnectionGroups.MoveTo(info.Profile.Id, name);
        RebuildRoots();
        SetStatus($"已把 {info.Profile.DisplayName} 移到分组「{name}」");
        AppLog.Write($"[连接分组] 组={name} 成员=1");
    }

    internal NodeInfo? SelectedInfo()
    {
        var node = ConnTree.SelectedNode;
        return node is null ? null : _nodeMap.GetValueOrDefault(node);
    }

    private TreeViewNode? SelectedNode => ConnTree.SelectedNode;

    /// <summary>树节点右键菜单：按节点类型给最常用操作（v1.4.2 新增，连接可在此关闭 / 删除）。</summary>
    private void OnTreeRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var node = (e.OriginalSource as FrameworkElement)?.DataContext as TreeViewNode;
        if (node is null || !_nodeMap.TryGetValue(node, out var info)) return;
        ConnTree.SelectedItem = node;   // 右键即选中，菜单动作与选中态一致
        var fly = new MenuFlyout();
        fly.PinTheme();   // 右键菜单是弹层，挂窗口内容之外，钉当前明暗象
        // 格17：二级分组——语义子菜单收敛平铺项，高频项保留一级；AutomationId=Menu_{组}_{项}。
        var subMenus = new Dictionary<string, MenuFlyoutSubItem>();
        MenuFlyoutSubItem Sub(string name)
        {
            if (!subMenus.TryGetValue(name, out var sub))
            {
                sub = new MenuFlyoutSubItem { Text = name };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(sub, $"Menu_{name}");
                fly.Items.Add(sub);
                subMenus[name] = sub;
            }
            return sub;
        }
        void Item(string text, RoutedEventHandler h, bool danger = false, string? group = null)
        {
            var mi = new MenuFlyoutItem { Text = text };
            if (danger) mi.Foreground = Ant.Error;   // 跟主题走的警示红，不写死颜色
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(mi, group is null ? $"Menu_{text}" : $"Menu_{group}_{text}");
            mi.Click += h;
            if (group is null) fly.Items.Add(mi); else Sub(group).Items.Add(mi);
        }
        void Sep() => fly.Items.Add(new MenuFlyoutSeparator());
        // U2 树-4：长尾功能收进二级子菜单，对齐 Navicat 层级（常用项平铺，工具类归组）
        // SubItem 挂外层作用域（#240 定谳：内嵌在 Sub 里时 lambda 词法域看不到，CS0103）；
        // 目标子菜单经 _curSub 传递，Sub 进入时置位、退出时清空，调用点零改动。
        MenuFlyoutSubItem? _curSub = null;
        void SubItem(string t, RoutedEventHandler h)
        {
            if (_curSub is null) return;
            var mi = new MenuFlyoutItem { Text = t };
            mi.Click += h;
            _curSub.Items.Add(mi);
        }
        void SubGroup(string text, Action<MenuFlyoutSubItem> fill)
        {
            var sub = new MenuFlyoutSubItem { Text = text };
            _curSub = sub;
            fill(sub);
            _curSub = null;
            if (sub.Items.Count > 0) fly.Items.Add(sub);
        }
        switch (info.Kind)
        {
            case NodeKind.Connection:
                Item("刷新", (_, _) => RefreshNode(node));   // D-10 修红：连接节点补刷新入口（此前仅左上角图标菜单）
                Item("展开连接", (_, _) => { if (!node.IsExpanded) node.IsExpanded = true; });
                Item("编辑连接", async (_, _) => await EditConnection(info));
                Item("移到分组", async (_, _) => await MoveToGroup(info));   // 批次二格6
                Item("移出分组", (_, _) => { ConnectionGroups.MoveTo(info.Profile.Id, ""); RebuildRoots(); SetStatus($"已把 {info.Profile.DisplayName} 移出分组"); });   // 批次二格6
                Item(info.Profile.Starred ? "取消星标" : "星标", (_, _) => { info.Profile.Starred = !info.Profile.Starred; ConnectionStore.Save(_profiles); RebuildRoots(); });   // E②：星标置顶
                Item(info.Profile.Hidden ? "取消隐藏" : "隐藏", (_, _) => { info.Profile.Hidden = !info.Profile.Hidden; ConnectionStore.Save(_profiles); RebuildRoots(); });   // E②：隐藏不删配置，进「已隐藏」组可找回
                Sep();
                SubGroup("工具与向导", sub =>
                {
                    SubItem("权限审计", (_, _) => OpenPanelTab("权限审计", () => UI.UserPermissionDialog.BuildPanel(this, info)));
                    SubItem("主从管理面", (_, _) => OpenPanelTab("主从管理面", () => UI.ReplicationDialog.BuildPanel(this, info)));
                    SubItem("部署向导", (_, _) => OpenPanelTab("部署向导", () => UI.DeploymentWizardDialog.BuildPanel(this, info)));
                    SubItem("分片向导", (_, _) => OpenPanelTab("分片向导", () => UI.ShardingWizardDialog.BuildPanel(this, info)));
                    SubItem("一键诊断", (_, _) => OpenPanelTab("一键诊断", () => UI.DiagnosticsDialog.BuildPanel(this, info)));
                    SubItem("高危 SQL 拦截", (_, _) => OpenPanelTab("高危 SQL 拦截", () => UI.RiskySqlGuardDialog.BuildPanel(this, info)));
                    SubItem("上线审查", (_, _) => OpenPanelTab("上线审查", () => UI.SqlReleaseCheckDialog.BuildPanel(this, info)));
                    SubItem("任务中心", (_, _) => OpenPanelTab("任务中心", () => UI.TaskCenterDialog.BuildPanel(this, info)));
                    SubItem("流式开关", (_, _) => OpenPanelTab("流式开关", () => UI.StreamingSwitchDialog.BuildPanel(this, info)));
                    SubItem("读路由预演", (_, _) => OpenPanelTab("读路由预演", () => UI.ReadRouterDialog.BuildPanel(this, info)));
                    SubItem("热点检测", (_, _) => OpenPanelTab("热点检测", () => UI.HotspotDetectorDialog.BuildPanel(this, info)));
                    SubItem("再均衡规划", (_, _) => OpenPanelTab("再均衡规划", () => UI.RebalancePlannerDialog.BuildPanel(this, info)));
                    SubItem("分片键评估", (_, _) => OpenPanelTab("分片键评估", () => UI.ShardKeyEvaluatorDialog.BuildPanel(this, info)));
                    SubItem("迁移续传", (_, _) => OpenPanelTab("迁移续传", () => UI.MigrationResumeDialog.BuildPanel(this, info)));
                    SubItem("团队合并", (_, _) => OpenPanelTab("团队合并", () => UI.TeamShareMergeDialog.BuildPanel(this, info)));
                    SubItem("共享包", (_, _) => OpenPanelTab("共享包", () => UI.SharePacketBuilderDialog.BuildPanel(this, info)));
                    SubItem("智能工具", (_, _) => OpenPanelTab("智能工具", () => UI.SmartToolsDialog.BuildPanel(this, info)));
                    SubItem("看板管理", (_, _) => OpenPanelTab("看板管理", () => UI.KanbanDialog.BuildPanel(this, info.Provider)));
                    SubItem("服务器监控", (_, _) => OpenPanelTab("服务器监控", () => UI.ServerMonitorDialog.BuildPanel(this, info)));   // 批次三格2
                    SubItem("命令监控", (_, _) => OpenPanelTab("命令监控", () => UI.CommandMonitorDialog.BuildPanel(this, info)));   // 批次三格3
                    SubItem("仪表板", (_, _) => OpenPanelTab("仪表板", () => UI.DashboardDialog.BuildPanel(this, info)));   // 批次五格6
                    SubItem("报表页", (_, _) => OpenPanelTab("报表页", () => UI.ReportPageDialog.BuildPanel(this, info)));   // ㉘：点选出图即钉住（2026-11-25 设计稿冻结）
                });
                Sep();
                Item("关闭连接", (_, _) => CloseConnection(info, node));
                Item("删除连接", async (_, _) => await DeleteConnection(info), danger: true);
                break;
            case NodeKind.Database:
                Item("刷新", (_, _) => RefreshNode(node));
                Item("新建查询", (_, _) => OpenQueryTab(info));   // 高频项保留一级
                Item("查看数据", (_, _) => OpenTableTab(info));
                Item("设计表", (_, _) => OnDesignSel(this, new RoutedEventArgs()));
                Item("在库中搜索", (_, _) => DispatcherQueue.TryEnqueue(() => _ = OpenDbSearchSafe(info)));   // 批次二格5；U078 红场：菜单点击内直开 ContentDialog 与 Flyout 收场竞态成 stowed exception（0xC000027B），改调度队列延迟开＋守门不外溢
                Item("备份 / 还原", (_, _) => OnBackupSel(this, new RoutedEventArgs()));
                Item("ER 图", (_, _) => OnErdSel(this, new RoutedEventArgs()));
                Sep();
                SubGroup("库工具", sub =>
                {
                    SubItem("备份计划", (_, _) => OpenPanelTab("备份计划", () => UI.BackupPlanDialog.BuildPanel(this, info)));
                    SubItem("备份演练", (_, _) => OpenBackupDrillTab(info));   // 三-15：弹窗改页面，升级内嵌页签
                    SubItem("数据脱敏", (_, _) => OpenPanelTab("数据脱敏", () => UI.MaskPreviewDialog.BuildPanel(this, info)));
                    SubItem("数据对账", (_, _) => OpenPanelTab("数据对账", () => UI.ReconcileDialog.BuildPanel(this, info)));
                    SubItem("误删恢复", (_, _) => OpenPanelTab("误删恢复", () => UI.RecoveryDialog.BuildPanel(this, info)));
                    SubItem("留痕审计", (_, _) => OpenPanelTab("留痕审计", () => UI.AuditTrailDialog.BuildPanel(this, info)));
                    SubItem("问库", (_, _) => OpenPanelTab("问库", () => UI.NaturalQueryDialog.BuildPanel(this, info)));
                    SubItem("快照包", (_, _) => OpenPanelTab("快照包", () => UI.SnapshotPackDialog.BuildPanel(this, info)));
                    SubItem("批量清理", (_, _) => OpenPanelTab("批量清理", () => UI.BatchCleanupDialog.BuildPanel(this, info)));
                    SubItem("设计辅助", (_, _) => OpenPanelTab("设计辅助", () => UI.DesignAssistDialog.BuildPanel(this, info)));
                    SubItem("例程管理", (_, _) => OpenPanelTab("例程管理", () => UI.RoutinesDialog.BuildPanel(this, info)));
                    SubItem("一键诊断", (_, _) => OpenPanelTab("一键诊断", () => UI.DiagnosticsDialog.BuildPanel(this, info)));
                    SubItem("告警中心", (_, _) => OpenPanelTab("告警中心", () => UI.AlertCenterDialog.BuildPanel(this, info)));
                    SubItem("三层模型", (_, _) => OpenPanelTab("三层模型", () => UI.ModelDialog.BuildPanel(this, info)));   // 批次五格1/2/4/5
                });
                break;
            case NodeKind.Table:
                // 树-4 验收口径：菜单次序对齐 Navicat——查看数据 / 设计表 / 新建查询 / 刷新
                Item("查看数据", (_, _) => OpenTableTab(info));
                Item("设计表", (_, _) => OnDesignSel(this, new RoutedEventArgs()));
                Item("新建查询", (_, _) => OpenQueryTab(info));
                Item("刷新", (_, _) => RefreshNode(node));
                Sep();
                Item("问库", (_, _) => OpenPanelTab("问库", () => UI.NaturalQueryDialog.BuildPanel(this, info)));
                Item("拟真数据", (_, _) => OpenFaker(info));
                Item("设计辅助", (_, _) => OpenPanelTab($"设计辅助 · {info.Name}", () => UI.DesignAssistDialog.BuildPanel(this, info, info.Name)));   // 格15：表节点带表名上下文
                break;
            case NodeKind.Category:
                Item("刷新", (_, _) => RefreshNode(node));
                break;
            case NodeKind.ObjectDef:
                Item("查看定义", (_, _) => _ = ShowObjectDefinition(info));
                if (info.Category is "views" or "routines" or "triggers")
                    Item("打开到查询窗口", (_, _) => _ = OpenObjectToQuery(info));
                else
                    Item("详情", (_, _) => _ = InfoAsync(info.RowText.Length > 0 ? info.RowText : "（无详情）"));
                if (info.Category == "routines")
                    Item(RoutineManager.DebugSupported(info.Profile.Engine) ? "断点调试" : "断点调试（v2.0 暂不支持）",
                        async (_, _) =>
                        {
                            if (!RoutineManager.DebugSupported(info.Profile.Engine)) { await InfoAsync(RoutineManager.DebugUnsupportedText); return; }
                            await OpenRoutineDebug(info);
                        });
                break;
            case NodeKind.BackupHistory:   // 发现144：备份节点——刷新重读账本
                Item("刷新", (_, _) => RefreshNode(node));
                break;
            case NodeKind.BackupEntry:   // 发现144：备份条目——还原 / 打开所在文件夹 / 删除记录
            {
                var entry = info.BackupEntry;
                if (entry is null) break;
                bool exists = System.IO.File.Exists(entry.File);
                if (exists) Item("还原", async (_, _) => await RestoreBackupEntry(info, entry));
                Item("打开所在文件夹", (_, _) => RevealInExplorer(entry.File), danger: false);
                Item(exists ? "删除" : "删除记录", async (_, _) => await DeleteBackupEntry(node, info, entry), danger: true);
                break;
            }
        }
        fly.ShowAt(e.OriginalSource as FrameworkElement ?? ConnTree);
    }

    /// <summary>树节点双击：表节点直接打开数据浏览（欢迎页与手册承诺的行为，P0 整改 02）。</summary>
    private void OnTreeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var node = (e.OriginalSource as FrameworkElement)?.DataContext as TreeViewNode;
        if (node is null || !_nodeMap.TryGetValue(node, out var info)) return;
        ConnTree.SelectedItem = node;   // 双击即选中，动作与选中态一致
        if (info.Kind == NodeKind.Table)
        {
            AppLog.Write($"[对象树] 双击表 table={info.Name}");   // 走查 S21 实效断言锚点
            OpenTableTab(info);
        }
    }

    private void RefreshNode(TreeViewNode node)
    {
        node.Children.Clear();
        if (_nodeMap.TryGetValue(node, out var i)) i.Loaded = false;
        node.Children.Add(new TreeViewNode { Content = "…" });
    }

    /// <summary>在库中搜索守门入口（U078 红场修红）：异常只落账不外溢——async void 直抛会成为
    /// stowed exception（0xC000027B）带崩整个进程，与 #88 S11 同族；Provider 缺失也在此拦成可读提示。</summary>
    private async Task OpenDbSearchSafe(NodeInfo info)
    {
        try
        {
            if (info.Provider is null) { AppLog.Write("[库搜索] 打开失败: Provider 为空"); return; }
            OpenPanelTab($"全库搜索 · {info.Db}", () => UI.DbSearchDialog.BuildPanel(this, info));
        }
        catch (Exception ex) { AppLog.Write($"[库搜索] 打开失败: {ex.Message}"); }
    }

    /// <summary>编辑连接（树右键与 Logo 菜单共用）。</summary>
    private async Task EditConnection(NodeInfo info)
    {
        var dlg = new UI.ConnectionDialog(info.Profile);
        bool saved = await dlg.ShowAsync(this, isNew: false);
        if (saved)
        {
            // 树随内存立即重建，与存盘成败解耦：存盘抛错只弹提示，树不得缺节点（#77 S06 红场，2026-11-18）
            string? saveErr = null;
            try
            {
                ConnectionStore.Save(_profiles);
            }
            catch (Exception ex)
            {
                saveErr = ex.Message;
                AppLog.Write($"[编辑连接-树右键] 存盘失败: {ex.Message}");
            }
            // 树先随内存重建再弹错：模态报错框会挂起其后代码（#78 定谳）
            RebuildRoots();
            if (saveErr != null) await InfoAsync($"连接存盘失败：{saveErr}");
        }
    }

    /// <summary>关闭连接：断开 provider、收起子树、关闭该连接的 SSH 隧道，连接配置保留可再展开。</summary>
    private void CloseConnection(NodeInfo info, TreeViewNode node)
    {
        TunnelResolver.StopProfile(info.Profile.Id);
        info.Provider?.Dispose();
        info.Provider = null;
        info.Loaded = false;
        info.ConnState = "";   // 格18：断开置回未知，状态点消失
        SetNodeContent(node, info);
        UpdateConnCount();
        node.Children.Clear();
        node.Children.Add(new TreeViewNode { Content = "…" });
        node.IsExpanded = false;
        SetStatus($"已关闭连接 {info.Profile.DisplayName}（配置保留，展开即重新连接）");
        AppLog.Write($"[关闭连接] 成功: {info.Profile.DisplayName}");   // 发现130：断开成功路径补落账
    }

    /// <summary>树右键打开拟真数据（多表模式）。</summary>
    private void OpenFaker(NodeInfo info) => OpenPanelTab($"拟真数据 · {info.Name}", () => UI.FakerDialog.BuildPanel(this, info.Provider!, info.Db, info.Name));

    /// <summary>发现144：备份条目还原——二次确认后走与向导同源的还原链路，成功失败都落账。</summary>
    private async Task RestoreBackupEntry(NodeInfo info, BackupHistory.Entry entry)
    {
        bool ok = await ConfirmAsync($"将用该备份覆盖当前库 {entry.Db}，确认？");
        if (!ok) return;
        try
        {
            info.Provider ??= ProviderFactory.Create(info.Profile);
            string result = await BackupPlanRunner.RestoreAsync(info.Profile, info.Provider, entry.Db, entry.File);
            SetStatus(result);
            AppLog.Write($"[备份树] 还原 db={entry.Db} file={entry.File} ok=true");
            ToastCenter.Publish(new NotifyEvent(NotifyKind.Restore, info.Profile.DisplayName, "还原完成", result, DateTime.Now));
        }
        catch (Exception ex)
        {
            string msg = HumanError.Say(ex, "还原失败");
            SetStatus(msg);
            AppLog.Write($"[备份树] 还原 db={entry.Db} file={entry.File} ok=false {msg}");
            await InfoAsync(msg);
            ToastCenter.Publish(new NotifyEvent(NotifyKind.Restore, info.Profile.DisplayName, "还原失败", msg, DateTime.Now));
        }
    }

    /// <summary>发现144：删除——文件在盘上只删文件（账本条目保留，条目置灰标注）；文件已不在盘上删账本记录。</summary>
    private async Task DeleteBackupEntry(TreeViewNode node, NodeInfo info, BackupHistory.Entry entry)
    {
        bool ok = await ConfirmAsync(System.IO.File.Exists(entry.File)
            ? $"确定删除备份文件 {entry.File} 吗？账本记录保留（条目会置灰标注）。"
            : "确定删除该备份记录吗？");
        if (!ok) return;
        try
        {
            if (System.IO.File.Exists(entry.File))
            {
                System.IO.File.Delete(entry.File);
                AppLog.Write($"[备份树] 删除文件 file={entry.File}（账本条目保留）");
                SetStatus("已删除备份文件；账本条目保留并置灰标注。");
            }
            else
            {
                BackupHistory.RemoveFile(entry.File);
                AppLog.Write($"[备份树] 删除记录 file={entry.File}");
                SetStatus("已删除备份记录。");
            }
            RefreshNode(node);
        }
        catch (Exception ex) { await InfoAsync(HumanError.Say(ex, "删除失败")); }
    }

    /// <summary>发现144：资源管理器定位备份文件。</summary>
    private static void RevealInExplorer(string file)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
            AppLog.Write($"[备份树] 打开所在文件夹 file={file}");
        }
        catch (Exception ex) { AppLog.Write($"[备份树] 打开文件夹失败: {ex.Message}"); }
    }

    private async void OnNodeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        var node = args.Node;
        var info = _nodeMap.GetValueOrDefault(node);
        if (info is null || info.Loaded) return;

        // 占位子节点 → 清空并懒加载
        bool wasPlaceholder = node.Children.Count == 1 && !_nodeMap.ContainsKey(node.Children[0]);
        if (!wasPlaceholder) return;
        node.Children.Clear();
        info.Loaded = true;
        try
        {
            switch (info.Kind)
            {
                case NodeKind.Connection:
                    SetStatus($"正在连接 {info.Profile.DisplayName}…");
                    info.Provider ??= ProviderFactory.Create(info.Profile);
                    var dbs = await info.Provider.GetDatabases();
                    info.ConnState = "Connected";   // 格18：展开成功置绿点
                    SetNodeStateDot(node, info);
                    UpdateConnCount();
                    // #154 S09 取证先行：连接装载零打点致 MSSQL 红账无法定谳，装载链路入应用日志
                    Core.AppLog.Write($"[对象树] 连接展开 engine={info.Profile.Engine} dbs={dbs.Count} [{string.Join(",", dbs.Take(8))}]");
                    foreach (var db in dbs)
                    {
                        var childInfo = new NodeInfo
                        {
                            Kind = NodeKind.Database,
                            Profile = info.Profile, Provider = info.Provider, Name = db, Db = db,
                        };
                        var childNode = new TreeViewNode { Content = NodeIconText("group", db) };
                        childNode.Children.Add(new TreeViewNode { Content = "…" });
                        _nodeMap[childNode] = childInfo;
                        node.Children.Add(childNode);
                    }
                    if (dbs.Count == 0) node.Children.Add(new TreeViewNode { Content = "（空）" });
                    // 发现144：备份产物树内管理——备过份的连接挂「备份」子节点（无记录不显示，避免噪音）
                    string engineName = EngineInfo.Name(info.Profile.Engine);
                    bool hasBackups = BackupHistory.All().Any(h => h.Engine == engineName);
                    if (hasBackups)
                    {
                        var bInfo = new NodeInfo { Kind = NodeKind.BackupHistory, Profile = info.Profile, Provider = info.Provider, Name = "备份" };
                        var bNode = new TreeViewNode { Content = NodeIconText("backup", "备份") };
                        bNode.Children.Add(new TreeViewNode { Content = "…" });
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(bNode, $"ConnBackup_{info.Profile.Id}");
                        _nodeMap[bNode] = bInfo;
                        node.Children.Add(bNode);
                    }
                    SetStatus($"{info.Profile.DisplayName}：{dbs.Count} 个库");
                    break;

                case NodeKind.Database:
                    var prov = info.Provider!;
                    var children = await prov.GetChildren(info.Db);
                    // #154 S09 取证先行：库级装载打点（children 数与前三项），红账直接分辨「查询没返回 vs 读树口径」
                    Core.AppLog.Write($"[对象树] 库展开 db={info.Db} children={children.Count} [{string.Join(",", children.Take(3))}]");
                    foreach (var c in children)
                    {
                        var cInfo = new NodeInfo
                        {
                            Kind = NodeKind.Table, // Mongo 集合 / Redis 键统一走 Table 语义
                            Profile = info.Profile, Provider = prov, Name = c, Db = info.Db,
                        };
                        var cNode = new TreeViewNode { Content = NodeIconText("table", c) };
                        // D-10 修红（2026-11-22 老板令）：关系库表节点下挂列子节点占位，展开即见字段（对齐 Navicat/DBeaver）
                        if (!prov.IsNoSql) cNode.Children.Add(new TreeViewNode { Content = "…" });
                        _nodeMap[cNode] = cInfo;
                        node.Children.Add(cNode);
                    }
                    if (children.Count == 0) node.Children.Add(new TreeViewNode { Content = "（空）" });
                    SetStatus($"{info.Name}：{children.Count} 个{(prov.IsNoSql ? prov.BrowseLabel : "表")}");
                    // B2 对象树补齐：视图/索引/外键/例程/触发器挂树（目录引擎懒加载；七库全覆盖——Mongo 走 TAODBMCMD，Redis 走空清单 SCAN）
                    if (ObjectTreeCatalog.Supported(info.Profile.Engine))
                    {
                        foreach (var (cat, label) in new[] { ("views", "视图"), ("indexes", "索引"), ("fks", "外键"), ("routines", "例程"), ("triggers", "触发器") })
                        {
                            var catInfo = new NodeInfo { Kind = NodeKind.Category, Category = cat, Profile = info.Profile, Provider = prov, Name = label, Db = info.Db };
                            var catNode = new TreeViewNode { Content = "▸ " + label };
                            catNode.Children.Add(new TreeViewNode { Content = "…" });
                            _nodeMap[catNode] = catInfo;
                            node.Children.Add(catNode);
                        }
                    }
                    break;

                case NodeKind.Category:
                    // B2：按目录引擎生成查询，列出该类对象
                    var catProv = info.Provider!;
                    var catSql = info.Category switch
                    {
                        "views" => ObjectTreeCatalog.ListViews(info.Profile.Engine, info.Db),
                        "indexes" => ObjectTreeCatalog.ListIndexes(info.Profile.Engine, info.Db),
                        "fks" => ObjectTreeCatalog.ListForeignKeys(info.Profile.Engine, info.Db),
                        "routines" => ObjectTreeCatalog.ListRoutines(info.Profile.Engine, info.Db),
                        _ => ObjectTreeCatalog.ListTriggers(info.Profile.Engine, info.Db),
                    };
                    var catRs = await catProv.Execute(info.Db, catSql);
                    int objCount = 0;
                    if (catRs.Count > 0)
                        foreach (var row in catRs[0].Rows)
                        {
                            var objName = row.Length > 0 ? row[0]?.ToString() ?? "" : "";
                            if (objName.Length == 0) continue;
                            objCount++;
                            var oInfo = new NodeInfo
                            {
                                Kind = NodeKind.ObjectDef, Category = info.Category,
                                Profile = info.Profile, Provider = catProv, Name = objName, Db = info.Db,
                                RowText = string.Join("  |  ", row.Where(v => v is not null).Select(v => v!.ToString())),
                            };
                            var oNode = new TreeViewNode { Content = "▪ " + objName };
                            _nodeMap[oNode] = oInfo;
                            node.Children.Add(oNode);
                        }
                    if (objCount == 0) node.Children.Add(new TreeViewNode { Content = "（空）" });
                    SetStatus($"{info.Db} · {info.Name}：{objCount} 项");
                    break;

                case NodeKind.BackupHistory:
                    // 发现144：备份条目按时间倒序列出（账本本身最新在前）；文件已不在磁盘的置灰标注
                    {
                        // 分支内直接求值（云测 run399：Connection 分支的 engineName 跨分支引用会报未赋值）
                        var entries = BackupHistory.All().Where(h => h.Engine == EngineInfo.Name(info.Profile.Engine)).ToList();
                        int n = 0;
                        foreach (var e in entries)
                        {
                            bool exists = System.IO.File.Exists(e.File);
                            string label = $"{e.Db} · {UiBatchB.BackupEntryLabel(e, exists)}";
                            var eInfo = new NodeInfo { Kind = NodeKind.BackupEntry, Profile = info.Profile, Provider = info.Provider, Name = e.File, Db = e.Db, BackupEntry = e };
                            var eNode = new TreeViewNode { Content = NodeIconText("table", label) };
                            if (!exists)
                            {
                                var host = eNode.Content as StackPanel;
                                if (host is not null)
                                    foreach (var child in host.Children.OfType<TextBlock>())
                                        child.Foreground = Ant.TextSub;   // 置灰（颜色走令牌）
                            }
                            _nodeMap[eNode] = eInfo;
                            node.Children.Add(eNode);
                            n++;
                        }
                        if (n == 0) node.Children.Add(new TreeViewNode { Content = UiBatchB.BackupTreeEmptyText });
                        SetStatus($"备份记录：{n} 条");
                    }
                    break;

                case NodeKind.Table when info.Provider is { IsNoSql: false }:
                    // D-10 修红（2026-11-22 老板令）：表展开即列清单（列名＋类型），SQLite PRAGMA 列序特殊（cid,name,type）
                    {
                        var colProv = info.Provider;
                        var colSql = ObjectTreeCatalog.ListColumns(info.Profile.Engine, info.Db, info.Name);
                        var colRs = await colProv.Execute(info.Db, colSql);
                        bool isSqlite = info.Profile.Engine == DbEngine.SQLite;
                        int colCount = 0;
                        if (colRs.Count > 0)
                            foreach (var row in colRs[0].Rows)
                            {
                                int nameIdx = isSqlite ? 1 : 0;
                                int typeIdx = isSqlite ? 2 : 1;
                                var colName = row.Length > nameIdx ? row[nameIdx]?.ToString() ?? "" : "";
                                if (colName.Length == 0) continue;
                                var colType = row.Length > typeIdx ? row[typeIdx]?.ToString() ?? "" : "";
                                colCount++;
                                var colInfo = new NodeInfo
                                {
                                    Kind = NodeKind.ObjectDef, Category = "columns",
                                    Profile = info.Profile, Provider = colProv, Name = colName, Db = info.Db,
                                    RowText = colType,
                                };
                                var colNode = new TreeViewNode { Content = "▪ " + colName + (colType.Length > 0 ? "  " + colType : "") };
                                _nodeMap[colNode] = colInfo;
                                node.Children.Add(colNode);
                            }
                        if (colCount == 0) node.Children.Add(new TreeViewNode { Content = "（无列）" });
                        SetStatus($"{info.Db} · {info.Name}：{colCount} 列");
                    }
                    break;
            }
            MetaRetryBar.IsOpen = false;   // s2：装载成功收重试条
        }
        catch (Exception ex)
        {
            info.Loaded = false;
            node.Children.Clear();
            node.Children.Add(new TreeViewNode { Content = "⚠ " + HumanError.Say(ex, "刷新") + "（点击刷新重试）" });
            if (info.Kind == NodeKind.Connection) { info.ConnState = "Failed"; SetNodeStateDot(node, info); }   // 格18：展开失败置红点
            UpdateConnCount();
            // #154 S09 取证先行：装载异常此前静默吞掉（应用日志零行），红账无法分辨「查询异常 vs 未触发」
            Core.AppLog.Write($"[对象树] 装载异常 kind={info.Kind} name={info.Name} db={info.Db} {ex.GetType().Name}: {ex.Message}");
            SetStatus(HumanError.Say(ex, "加载失败"));
            // s2 失败重试条：元数据加载失败时顶部亮重试条，点重试即重载该节点
            _metaRetryNode = node;
            MetaRetryBar.Message = HumanError.Say(ex, $"{info.Name}");
            MetaRetryBar.IsOpen = true;
        }
    }

    private TreeViewNode? _metaRetryNode;

    /// <summary>s2 重试条动作：重载最近一次装载失败的节点，成功后收条。</summary>
    private void OnMetaRetry(object sender, RoutedEventArgs e)
    {
        MetaRetryBar.IsOpen = false;
        if (_metaRetryNode is not null) RefreshNode(_metaRetryNode);
    }

    private void OnTreeSelection(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        var info = SelectedInfo();
        if (info is null) return;
        RefreshActionBar();                       // 发现133：操作栏随选中联动
        _ = RefreshDetailPanel(info);             // 发现134：选中即详情
        UpdateDomainNav();                        // 发现132：域指示随上下文刷新
        switch (info.Kind)
        {
            case NodeKind.Connection:
                if (info.Profile.Engine == DbEngine.Redis)
                    (info.Provider as RedisProvider)!.CurrentKey = null;
                break;
            case NodeKind.Table when info.Provider is RedisProvider rp:
                rp.CurrentKey = info.Name;
                break;
            case NodeKind.Table when info.Provider is MongoProvider mp:
                mp.CurrentCollection = info.Name;
                break;
            case NodeKind.ObjectDef:
                _ = ShowObjectDefinition(info);
                break;
        }
    }

    // ---------- 工具栏动作 ----------

    private void OnRefreshSel(object sender, RoutedEventArgs e)
    {
        var node = SelectedNode;
        if (node is null) { RebuildRoots(); return; }
        if (_nodeMap.TryGetValue(node, out var info) && info.Kind == NodeKind.Connection) { RebuildRoots(); return; }
        if (!_nodeMap.TryGetValue(node, out var i2)) return;
        i2.Loaded = false;
        node.Children.Clear();
        node.Children.Add(new TreeViewNode { Content = "…" });
    }

    /// <summary>#288 S61c/S63 级联修：无选中连接也开空白查询页签（Navicat 主流做法），执行时再选连接，不再弹提示挡走查。</summary>
    private void OnNewQuerySel(object sender, RoutedEventArgs e)
        => OpenQueryTab(SelectedInfo() ?? new NodeInfo { Kind = NodeKind.Connection, Name = "SQL" });

    private void OnBrowseSel(object sender, RoutedEventArgs e) => OpenTableTab(SelectedInfo());

    /// <summary>选中表 → 改表结构；选中库 → 新建表；其他提示。U2 D-13：设计器内嵌主窗口页签（Navicat 口径），不再弹框。</summary>
    private async void OnDesignSel(object sender, RoutedEventArgs e)
    {
        var info = SelectedInfo();
        if (info?.Provider is null) { await InfoAsync("请先选中库或表。"); return; }
        if (info.Provider.IsNoSql) { await InfoAsync("MongoDB / Redis 无表结构，不支持可视化设计。"); return; }
        if (info.Kind == NodeKind.Table)
        {
            List<ColumnSpec> cols;
            try { cols = await info.Provider.GetColumns(info.Db, info.Name); }
            catch (Exception ex) { await InfoAsync(HumanError.Say(ex, "读取表结构失败")); return; }
            OpenDesignerTab(info.Provider, info.Db, info.Name, cols);
        }
        else if (info.Kind == NodeKind.Database)
        {
            OpenDesignerTab(info.Provider, info.Name, null, null);
        }
        else await InfoAsync("请选中库或表后再设计表结构。");
    }

    /// <summary>U2 D-13：打开内嵌表设计器页签（列 / 索引 / 外键分区 + SQL 预览实时刷新）。</summary>
    public void OpenDesignerTab(IDbProvider prov, string db, string? table, List<ColumnSpec>? existing)
    {
        var tab = new TabViewItem { Header = table == null ? $"新建表 · {db}" : $"设计 · {table}" };
        tab.Content = new UI.TableDesignerTab(this, prov, db, table, existing);
        WorkTabs.TabItems.Add(tab);
        SaveSession();
        WorkTabs.SelectedItem = tab;
    }

    /// <summary>整改三批三-15：弹窗改页面——备份可用性演练从模态弹窗升级为内嵌主窗口页签。</summary>
    public void OpenBackupDrillTab(NodeInfo info)
    {
        var tab = new TabViewItem { Header = $"备份演练 · {(info.Db.Length > 0 ? info.Db : info.Profile.Database)}" };
        tab.Content = new UI.BackupDrillTab(this, info);
        WorkTabs.TabItems.Add(tab);
        SaveSession();
        WorkTabs.SelectedItem = tab;
    }

    /// <summary>2026-11-28 老板令：工具面板一律内嵌主窗口页签（弹窗只留确认类）——统一打开助手，照 OpenBackupDrillTab 口径。</summary>
    public void OpenPanelTab(string header, Func<FrameworkElement> build)
    {
        var tab = new TabViewItem { Header = header };
        tab.Content = build();
        WorkTabs.TabItems.Add(tab);
        SaveSession();
        WorkTabs.SelectedItem = tab;
    }

    /// <summary>拍板（2026-11-28）：流程页完成后按内容自行收口——按 Content 引用找到所属页签移除（导入向导页签化用）。</summary>
    public void ClosePanelTab(FrameworkElement content)
    {
        var tabs = WorkTabs.TabItems.OfType<TabViewItem>().Where(t => ReferenceEquals(t.Content, content)).ToList();
        foreach (var tab in tabs) WorkTabs.TabItems.Remove(tab);
        if (tabs.Count > 0) SaveSession();
    }

    /// <summary>备份 / 还原：MySQL/PG 走官方 CLI，SQL Server 走 T-SQL，SQLite 文件复制。</summary>
    private void OnBackupSel(object sender, RoutedEventArgs e)
    {
        _navBackupOpen = true; _navTaskOpen = _navErdOpen = false;   // 发现132：域指示随入口刷新
        UpdateDomainNav();
        var info = SelectedInfo();
        if (info is null || info.Provider is null) { _ = InfoAsync("请先选中一个连接（或库、表）再备份 / 还原。"); return; }
        OpenPanelTab($"备份 / 恢复 · {info.Name}", () => UI.BackupDialog.BuildPanel(this, info));   // 页签化改造：备份向导入页签
    }

    /// <summary>数据传输与结构同步：源连接 → 目标连接，补结构、搬数据。</summary>
    private void OnSyncSel(object sender, RoutedEventArgs e)
    {
        var info = SelectedInfo();
        OpenPanelTab("数据传输与结构同步", () => UI.DataSyncDialog.BuildPanel(this, _profiles, info?.Profile));   // 页签化改造：同步入页签
    }

    /// <summary>模型设计（ER 图）：库内表结构成图，拖拽排布，导出 PNG，保存 / 打开布局。</summary>
    private void OnErdSel(object sender, RoutedEventArgs e)
    {
        _navErdOpen = true; _navBackupOpen = _navTaskOpen = false;   // 发现132：域指示随入口刷新
        UpdateDomainNav();
        var info = SelectedInfo();
        OpenPanelTab("模型设计 · ER 图", () => UI.ErdDialog.BuildPanel(this, _profiles, info?.Profile));   // 页签化改造：ER 图入页签
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        // 整改三批三-3：版本对齐标题栏 2.0；引擎清单更新为七库＋国产六家口径
        _ = new UI.MessageDialogHelper(this).Show(
            "TaoDBM v2.0",
            "WinUI3 + .NET 10 · Ant Design 视觉\n支持 MySQL / PostgreSQL / SQL Server / SQLite / Oracle / MongoDB / Redis\n以及 OceanBase / GaussDB / 达梦 / 金仓 / PolarDB / TDSQL\n\n道生一，一生二，二生三，三生万物。数据即万物。");
    }

    /// <summary>操作手册（2026-11-20 老板令）：顶栏「手册」/ 左上菜单「操作手册」/ 全局 F1 三入口，内容离线内置。</summary>
    private void OnManual(object sender, RoutedEventArgs e) => UI.ManualDialog.Show(this);

    // ---------- 打开标签页 ----------

    // 发现135：AddWelcomeTab 重做（新手条＋收藏＋最近打开），实现移至 MainWindow.LayoutBatch.cs 布局批分部。

    public void OpenQueryTab(NodeInfo? info, string? initialSql = null)
    {
        try
        {
            if (info is null) { _ = InfoAsync("请先在左侧树中选中库、表或连接。"); return; }
            Core.AppLog.Write($"[新建查询] enter kind={info.Kind} name={info.Name} provider={(info.Provider is null ? "null" : "ok")}");
            // #153 修红：走查 seg2/seg3 跳过树展开步（S07/S08）时连接节点 Provider 尚未懒建——
            // WorkTab 拿到 null provider 后会话保存（SessionConnectionId→Profile.Id）与执行账双 NRE，
            // 曾判「间歇性」，实为分段确定性；此处对齐 OnNodeExpanding 同款懒建口径
            info.Provider ??= ProviderFactory.Create(info.Profile);
            var (db, hint) = info.Kind switch
            {
                NodeKind.Database => (info.Name, ""),
                NodeKind.Table => (info.Db, DefaultSnippet(info)),
                NodeKind.ObjectDef => (info.Db, ""),
                // 023374b 移植：服务器引擎（MySQL/MSSQL/PG/Oracle）连接节点带连接配置默认库，
                // 否则 S12 裸表名 SQL 在无库上下文执行、计数账不符；SQLite 保持 main 口径
                _ => (info.Provider.IsNoSql ? "" :
                      info.Profile.Engine == DbEngine.SQLite
                          ? (info.Profile.Database.Length > 0 ? "main" : "")
                          : info.Profile.Database, ""),
            };
            if (info.Provider is MongoProvider m && info.Kind == NodeKind.Table) m.CurrentCollection = info.Name;
            if (info.Provider is RedisProvider r && info.Kind == NodeKind.Table) r.CurrentKey = info.Name;

            var tab = new TabViewItem { Header = $"查询 · {info.Name}" };
            var wt = new UI.WorkTab(this, info.Provider!, db, info.Kind == NodeKind.Table ? info.Name : "", UI.WorkMode.Query, hint.Length > 0 ? hint : initialSql);
            Core.AppLog.Write($"[新建查询] worktab built db={db}");
            tab.Content = wt;
            WorkTabs.TabItems.Add(tab);
            _workTabs[tab] = wt;
            WireDirtyHeader(tab, wt);
            SaveSession();
            WorkTabs.SelectedItem = tab;
            _navLastMode = "Query"; _navBackupOpen = _navTaskOpen = _navErdOpen = false;   // 发现132
            UpdateDomainNav();
            Core.AppLog.Write("[新建查询] tab selected");
        }
        catch (Exception ex)
        {
            // #88 S11 红场（0xC000027B）：首跑云路径异常不得带崩整个应用——落账＋界面提示，走查按 work-editor 缺席定谳
            Core.AppLog.Write($"[新建查询] {ex.GetType().Name}: {ex.Message}");
            Core.AppLog.Write($"[新建查询] stack: {ex.StackTrace}");   // 修红：只有 message 没栈无法定位抛点，补落账
            _ = InfoAsync(HumanError.Say(ex, "新建查询失败"));
        }
    }

    /// <summary>格14：把任意 SQL（如 ERD 正向 DDL）一键送进新查询页签；info 可空（无连接上下文，执行时再选连接）。</summary>
    public void OpenQueryWithSql(string sql, NodeInfo? info = null)
    {
        try
        {
            info ??= new NodeInfo { Kind = NodeKind.Connection, Name = "SQL" };
            info.Provider ??= ProviderFactory.Create(info.Profile);
            var db = info.Kind == NodeKind.Database ? info.Name
                : info.Profile.Engine == DbEngine.SQLite
                    ? (info.Profile.Database.Length > 0 ? "main" : "")
                    : info.Profile.Database;
            var tab = new TabViewItem { Header = $"查询 · {info.Name}" };
            var wt = new UI.WorkTab(this, info.Provider!, db, info.Kind == NodeKind.Table ? info.Name : "", UI.WorkMode.Query, sql);
            tab.Content = wt;
            WorkTabs.TabItems.Add(tab);
            _workTabs[tab] = wt;
            WireDirtyHeader(tab, wt);
            SaveSession();
            WorkTabs.SelectedItem = tab;
            Core.AppLog.Write($"[ERD] DDL 进查询窗口 db={db}");
        }
        catch (Exception ex)
        {
            Core.AppLog.Write($"[ERD] DDL 进查询窗口失败 {ex.GetType().Name}: {ex.Message}");
            _ = InfoAsync(HumanError.Say(ex, "打开查询窗口失败"));
        }
    }

    private static string DefaultSnippet(NodeInfo info) => info.Provider switch
    {
        MongoProvider => "{ }",
        RedisProvider => "GET ",
        _ => $"SELECT * FROM {info.Name} LIMIT 50;",
    };

    public void OpenTableTab(NodeInfo? info)
    {
        if (info is null || info.Kind != NodeKind.Table)
        {
            // Redis 库节点 → 直接打开键列表（键值视图从库级开始）；其余库仍走新建查询
            if (info is not null && info.Kind == NodeKind.Database && info.Provider is RedisProvider)
            {
                var redisTab = new TabViewItem { Header = $"键 · {info.Name}" };
                var redisWt = new UI.WorkTab(this, info.Provider, info.Name, "", UI.WorkMode.TableData, null);
                redisTab.Content = redisWt;
                WorkTabs.TabItems.Add(redisTab);
                _workTabs[redisTab] = redisWt;
                WireDirtyHeader(redisTab, redisWt);
                SaveSession();
                WorkTabs.SelectedItem = redisTab;
                return;
            }
            if (info is not null && info.Kind == NodeKind.Database) { OpenQueryTab(info); return; }
            _ = InfoAsync("请选中一个表 / 集合 / 键后查看数据。");
            return;
        }
        var prov = info.Provider!;
        // 发现53：同表重复开页签改为复用已有页签（同连接＋同库＋同表＋浏览态），不再各开一份
        foreach (var kv in _workTabs)
        {
            if (kv.Value.SessionMode == nameof(UI.WorkMode.TableData)
                && kv.Value.SessionConnectionId == prov.Profile.Id
                && kv.Value.SessionDb == info.Db
                && kv.Value.SessionChild == info.Name)
            {
                WorkTabs.SelectedItem = kv.Key;
                return;
            }
        }
        var tab = new TabViewItem
        {
            Header = $"{(prov.IsNoSql ? prov.BrowseLabel : "表")} · {info.Name}",
        };
        if (prov is MongoProvider mp) mp.CurrentCollection = info.Name;
        if (prov is RedisProvider rp) rp.CurrentKey = info.Name;
        var wt = new UI.WorkTab(this, prov, info.Db, info.Name, UI.WorkMode.TableData, null);
        tab.Content = wt;
        WorkTabs.TabItems.Add(tab);
        _workTabs[tab] = wt;
        WireDirtyHeader(tab, wt);
        SaveSession();
        WorkTabs.SelectedItem = tab;
        _navLastMode = "TableData"; _navBackupOpen = _navTaskOpen = _navErdOpen = false;   // 发现132
        UpdateDomainNav();
    }

    private void OnTabClose(TabView sender, TabViewTabCloseRequestedEventArgs args)
        => CloseTabItem(sender, args.Tab);

    /// <summary>B①：页签「＋」接新开空白查询页签（Navicat 主流做法）——有连接上下文按上下文开，无则开无连接查询页。</summary>
    private void OnTabAdd(TabView sender, object args)
        => OnNewQuerySel(this, new RoutedEventArgs());

    /// <summary>关页签公共链路：脏页签三选确认不绕过，干净页签直接关并落会话。</summary>
    private void CloseTabItem(TabView tabView, TabViewItem tab)
    {
        // P1-02：脏页签先三选确认（保存并关闭 / 不保存 / 取消），干净页签直接关
        if (_workTabs.TryGetValue(tab, out var dirtyWt) && dirtyWt.IsDirty)
        {
            _ = ConfirmCloseDirtyAsync(tabView, tab, dirtyWt);
            return;
        }
        tabView.TabItems.Remove(tab);
        if (_workTabs.Remove(tab)) SaveSession();
    }

    /// <summary>整改三批三-2：Ctrl+W 关当前页签——复用关页签公共链路（脏页签三选确认不绕过）。</summary>
    private void CloseCurrentTab()
    {
        if (WorkTabs.SelectedItem is not TabViewItem tab || WorkTabs.TabItems.Count <= 1) return;
        CloseTabItem(WorkTabs, tab);
        Core.AppLog.Write("[快捷键] w 关页签");
    }

    /// <summary>整改三批三-2：Ctrl+Tab(+Shift) 循环切页签。</summary>
    private void CycleWorkTab(bool forward)
    {
        int n = WorkTabs.TabItems.Count;
        if (n <= 1) return;
        int cur = WorkTabs.SelectedIndex;
        if (cur < 0) cur = 0;
        int next = forward ? (cur + 1) % n : (cur - 1 + n) % n;
        WorkTabs.SelectedIndex = next;
        Core.AppLog.Write($"[快捷键] tab 切页签 idx={next}");
    }

    /// <summary>P1-02：关脏页签三选确认。保存被取消（另存未选文件）则留在页签。</summary>
    private async Task ConfirmCloseDirtyAsync(TabView sender, TabViewItem tab, UI.WorkTab wt)
    {
        var dlg = new ContentDialog
        {
            Title = "未保存的更改",
            Content = new TextBlock { Text = "这个页签还有没保存的修改，直接关闭就没了。", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "保存并关闭",
            SecondaryButtonText = "不保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        var r = await dlg.ShowThemedAsync();
        if (r == ContentDialogResult.Primary)
        {
            await wt.SaveSourceFileAsync();
            if (wt.IsDirty) return;   // 另存对话框被取消 → 留在页签
        }
        else if (r == ContentDialogResult.None) return;   // 取消/关闭 → 留在页签（枚举无 Close，关闭返回 None）
        sender.TabItems.Remove(tab);
        if (_workTabs.Remove(tab)) SaveSession();
    }

    /// <summary>P1-02：脏标记 → 页签标题 ● 前缀（保存后消失）。</summary>
    private void WireDirtyHeader(TabViewItem tab, UI.WorkTab wt)
    {
        var baseHeader = tab.Header?.ToString() ?? "";
        // B④：页签标题取 SQL 首行（截 24 字）或表名并动态更新；SQL 为空回退原「查询 · 连接名」
        void Refresh()
        {
            try
            {
                var header = baseHeader;
                var sql = (wt.EditorText ?? "").Trim();
                if (sql.Length > 0)
                {
                    var first = sql.Split('\n', 2)[0].Trim().TrimEnd(';');
                    if (first.Length == 0) first = sql.Split('\n', 2)[^1].Trim();
                    if (first.Length > 0)
                    {
                        if (first.Length > 24) first = first[..24] + "…";
                        header = $"查询 · {first}";
                    }
                }
                // 发现59：标题统一走 SetTabHeader（专注态只更新暂存不上屏）
                SetTabHeader(tab, (wt.IsDirty ? "● " : "") + header);
            }
            catch { /* 标题更新失败不影响业务 */ }
        }
        wt.DirtyChanged += Refresh;
    }

    // ---------- 标签会话保持 ----------

    /// <summary>把当前打开的工作标签写盘（连接已删的自动剔除）。</summary>
    private void SaveSession()
    {
        Safe.Run(() =>
        {
            var items = _workTabs.Values.Select(t => new TabSessionItem
            {
                ConnectionId = t.SessionConnectionId,
                Mode = t.SessionMode,
                Db = t.SessionDb,
                Child = t.SessionChild,
                Sql = t.SessionSql,
                SourceFile = t.SessionSourceFile,
                LastOpened = t.SessionOpened,   // 发现135：欢迎页「最近打开」相对时间
            }).ToList();
            TabSession.Save(TabSession.FilterValid(items, _profiles));
        }, "会话保存");
    }

    /// <summary>启动时恢复上次的查询标签（表数据页不自动恢复，避免启动即连库）。</summary>
    private void RestoreSession()
    {
        Safe.Run(() =>
        {
            foreach (var item in TabSession.FilterValid(TabSession.Load(), _profiles))
            {
                if (item.ConnectionId.Length == 0 || item.Mode != "Query") continue;
                var p = _profiles.FirstOrDefault(x => x.Id == item.ConnectionId);
                if (p is null) continue;
                // P1-03：标题带库名与文件归属，完整路径进 ToolTip；恢复后一律视为干净
                var header = $"查询 · {p.DisplayName}" + (item.Db.Length > 0 ? $" · {item.Db}" : "")
                    + (item.SourceFile.Length > 0 ? $" · {System.IO.Path.GetFileName(item.SourceFile)}" : "");
                var tab = new TabViewItem { Header = header };
                if (item.SourceFile.Length > 0) { Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(tab, item.SourceFile); }
                var wt = new UI.WorkTab(this, ProviderFactory.Create(p), item.Db, "", UI.WorkMode.Query,
                    item.Sql.Length > 0 ? item.Sql : null, item.SourceFile.Length > 0 ? item.SourceFile : null);
                Core.AppLog.Write($"[会话恢复] db={item.Db} file={item.SourceFile}");
                tab.Content = wt;
                WorkTabs.TabItems.Add(tab);
                _workTabs[tab] = wt;
                WireDirtyHeader(tab, wt);
            }
        }, "会话恢复");
    }

    // ---------- 对话框辅助 ----------

    /// <summary>B2：对象定义 SQL。视图在 MySQL/PostgreSQL 有专用取定义口径（ShowDefinition 的例程口径不覆盖视图），其余走通用入口。</summary>
    private static string ObjectDefSql(NodeInfo info) => info.Category switch
    {
        "views" when info.Profile.Engine == DbEngine.MySQL =>
            $"SELECT VIEW_DEFINITION FROM information_schema.VIEWS WHERE TABLE_SCHEMA = '{info.Db.Replace("'", "''")}' AND TABLE_NAME = '{info.Name.Replace("'", "''")}'",
        "views" when info.Profile.Engine == DbEngine.PostgreSQL =>
            $"SELECT pg_get_viewdef('{info.Name.Replace("'", "''")}'::regclass, true)",
        _ => ObjectTreeCatalog.ShowDefinition(info.Profile.Engine, info.Name),
    };

    /// <summary>B2：读取对象定义文本；失败或为空时弹窗说明并返回 null。</summary>
    private async Task<string?> TryReadDefinition(NodeInfo info)
    {
        if (info.Provider is null) { await InfoAsync("连接不可用，请重新展开后再试。"); return null; }
        try
        {
            SetStatus($"正在读取 {info.Name} 定义…");
            var rs = await info.Provider.Execute(info.Db, ObjectDefSql(info));
            var ddl = rs.Count > 0 && rs[0].Rows.Count > 0
                ? rs[0].Rows[0].LastOrDefault(v => v is not null)?.ToString() ?? ""
                : "";
            if (ddl.Length == 0) { await InfoAsync("未取到定义（对象可能已被删除）。"); return null; }
            SetStatus($"已载入 {info.Name} 定义");
            return ddl;
        }
        catch (Exception ex) { await InfoAsync(HumanError.Say(ex, "读取定义失败")); return null; }
    }

    /// <summary>B2：查看对象定义。视图/例程/触发器弹等宽只读 DDL 预览（可打开到查询窗口），索引/外键无 DDL 直接展示目录行详情。</summary>
    private async Task ShowObjectDefinition(NodeInfo info)
    {
        if (info.Category is "indexes" or "fks")
        {
            await InfoAsync(info.RowText.Length > 0 ? info.RowText : "（无详情）");
            return;
        }
        var ddl = await TryReadDefinition(info);
        if (ddl is null) return;
        var text = new TextBox
        {
            Text = ddl,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            MinHeight = 300,
        };
        var dlg = new ContentDialog
        {
            Title = $"{info.Name} 定义",
            Content = new ScrollViewer
            {
                Content = text,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = "打开到查询窗口",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowThemedAsync() == ContentDialogResult.Primary) OpenQueryTab(info, ddl);
    }

    /// <summary>B2：把对象 DDL 直接打到新查询标签。</summary>
    private async Task OpenObjectToQuery(NodeInfo info)
    {
        var ddl = await TryReadDefinition(info);
        if (ddl is null) return;
        OpenQueryTab(info, ddl);
    }

    /// <summary>例程断点调试入口：v2.0 仅 MySQL（2026-11-19 拍板），把调试脚手架打开到查询窗口，过程内中间结果用 SELECT 观测。</summary>
    private async Task OpenRoutineDebug(NodeInfo info)
    {
        if (!RoutineManager.DebugSupported(info.Profile.Engine)) { await InfoAsync(RoutineManager.DebugUnsupportedText); return; }
        var kind = info.RowText.Contains("FUNCTION", StringComparison.OrdinalIgnoreCase)
            ? RoutineKind.Function
            : RoutineKind.Procedure;
        OpenQueryTab(info, RoutineManager.DebugScript(info.Profile.Engine, kind, info.Name));
    }

    /// <summary>引导/提示类消息（P1-01 降模态）：走状态栏一句话，不再弹 ContentDialog 打断操作；确认类仍走 ConfirmAsync 模态。</summary>
    private Task InfoAsync(string msg)
    {
        SetStatus(msg);
        Core.AppLog.Write($"[引导提示] {msg}");
        return Task.CompletedTask;
    }

    public async Task<bool> ConfirmAsync(string msg, string verb = "删除")
    {
        var dlg = new ContentDialog
        {
            Content = new TextBlock { Text = msg, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = verb,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        // 整改三批三-1：危险确认统一组件——主按钮红字随动作、默认焦点在取消
        dlg.PrimaryButtonStyle = new Style(typeof(Button))
        {
            Setters = { new Setter(Button.ForegroundProperty, Ant.Error) },
        };
        return await dlg.ShowThemedAsync() == ContentDialogResult.Primary;
    }
}
