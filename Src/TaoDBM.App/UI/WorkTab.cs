using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Windows.Storage.Pickers;
using Windows.UI.Text;
using TaoDBM.Core;
using TaoDBM.Core.Streaming;

namespace TaoDBM.UI;

public enum WorkMode { Query, TableData }

/// <summary>右侧工作区的一个标签页：查询编辑器或表数据浏览。</summary>
public partial class WorkTab : Grid
{
    private readonly MainWindow _owner;
    private readonly IDbProvider _provider;
    private string _db;
    private readonly string _child;
    private readonly WorkMode _mode;

    private readonly ComboBox _dbBox = new() { MinWidth = 160, PlaceholderText = "选择库" };
    private readonly RichEditBox _editor = new()
    {
        TextWrapping = TextWrapping.NoWrap,
        FontSize = AntTokens.FontBody,
        MinHeight = 160,
        PlaceholderText = "在此输入 SQL / 过滤器 / 命令，F5 或 Ctrl+Enter 执行",
    };
    private readonly DispatcherQueueTimer _hlTimer;
    private bool _applying;
    private readonly Button _runBtn = new() { Content = "▶ 执行", Background = Ant.Primary, Foreground = Ant.OnPrimary, BorderThickness = new Thickness(0), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(14, 5, 14, 5) };
    private readonly Button _stopBtn = new() { Content = "■ 停止", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4), IsEnabled = false };
    private CancellationTokenSource? _runCts;
    private bool _cancelable;   // 发现69：仅令牌贯穿的流式通道可中止；整包 Execute 与 Browse 翻页点了要给明示
    private readonly Button _csvBtn = new() { Content = "导出 CSV", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4) };
    private readonly Button _insBtn = new() { Content = "导出 INSERT", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4) };
    // 批次四格1/格2（2026-11-23）：导出格式组——Excel（xlsx）/ XML / HTML
    private readonly Button _exportMoreBtn = new() { Content = "导出 ▾", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4) };
    private readonly Button _structBtn = new() { Content = "结构", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4) };
    private readonly Button _addRowBtn = new() { Content = "新增行", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4) };
    private readonly Button _importBtn = new() { Content = "导入", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4) };
    private readonly Button _designBtn = new() { Content = "设计表", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4) };
    private readonly Button _fakerBtn = new() { Content = "拟真数据", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4) };
    private readonly Button _histBtn = new() { Content = "历史", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4) };
    private readonly Button _reportBtn = new() { Content = "报表", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4) };
    private readonly Button _assistBtn = new() { Content = "设计辅助", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4) };   // 格15：结果上下文入口
    private readonly Button _delKeyBtn = new() { Content = "删除此键", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4) };
    private readonly Button _favBtn = new() { Content = "收藏", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4) };
    // 批次二格1：表单视图切换（浏览模式）；格4：粘贴数据（浏览模式）
    private readonly Button _formBtn = new() { Content = "表单", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4) };
    private readonly Button _pasteBtn = new() { Content = "粘贴数据", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4) };
    private bool _formMode;
    private readonly TextBlock _pageInfo = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor };
    private readonly Button _prevBtn = new() { Content = "◀ 上一页", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(8, 3, 8, 3) };
    private readonly Button _nextBtn = new() { Content = "下一页 ▶", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(8, 3, 8, 3) };
    // D⑦：分页可调可跳页——每页行数下拉＋页码跳转；浏览筛选框（当前结果关键字过滤）
    private readonly ComboBox _pageSizeBox = new() { Width = 110, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _pageJumpBox = new() { Width = 64, VerticalAlignment = VerticalAlignment.Center, PlaceholderText = "页码" };
    private readonly TextBox _browseFilterBox = new() { Width = 150, VerticalAlignment = VerticalAlignment.Center, PlaceholderText = "筛选列/值" };

    private readonly ScrollViewer _resultsHost = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TextBox _messageBox = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = (FontFamily)Application.Current.Resources["AntMono"],
        FontSize = AntTokens.FontMinor,
        MinHeight = 64,
        MaxHeight = 140,
        PlaceholderText = "消息",
    };
    // B②：底部结果条——钉死页签底部、固定行高（行数 · 耗时 · 时间），数据多走内部滚动
    private readonly TextBlock _resultBar = new()
    {
        FontSize = AntTokens.FontMinor,
        Foreground = Ant.TextSub,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(10, 0, 10, 0),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    // B⑦：Ctrl+F 结果集查找条——按关键字过滤当前结果行，Esc 清空恢复
    private readonly TextBox _findBox = new()
    {
        PlaceholderText = "查找（关键字过滤，Esc 清空）",
        FontSize = AntTokens.FontMinor,
        MinWidth = 260,
    };
    private readonly TextBlock _findHint = new()
    {
        FontSize = AntTokens.FontMinor,
        Foreground = Ant.TextSub,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
    };
    private List<QueryResult> _lastFull = new();

    private List<QueryResult> _last = new();
    private QueryResult? _browseResult;
    private List<ColumnSpec> _cols = new();
    private int _offset;
    private int _pageSize = 200;   // D⑦：分页可调（原写死 200）
    private bool _busy;

    // 表名候选预载（融合远端 D-12 能力：补全候选含当前库表名；弹层本体用格1 键盘化实现）
    private List<string> _tables = new();
    private bool _tablesLoading;
    private bool _isRelational => _provider is RelationalProvider;
    private readonly Dictionary<string, double> _colWidths = new();   // 格9：用户拖拽列宽（键=查询文本|列名 或 browse:库.表|列名）

    // ---------- P1-01 错误横幅 / P1-02 脏标记 ----------

    private readonly TextBlock _errorText = new()
    {
        IsTextSelectionEnabled = true,
        TextWrapping = TextWrapping.Wrap,
        FontSize = AntTokens.FontMinor,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 0, 0),
    };
    private readonly Button _errorCopyBtn = new()
    {
        Content = "复制详情",
        Padding = new Thickness(10, 2, 10, 2),
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = AntTokens.FontMinor,
    };
    private readonly Border _errorBanner = new()
    {
        Visibility = Visibility.Collapsed,
        Padding = new Thickness(10, 6, 10, 6),
        Margin = new Thickness(0, 0, 0, 4),
        CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg),
    };

    private bool _dirty;
    /// <summary>未保存标记（P1-02）：编辑器变更置脏，保存成功清脏。</summary>
    public bool IsDirty => _dirty;
    /// <summary>脏标记变化通知（页签标题 ● 前缀由主窗维护）。</summary>
    public event Action? DirtyChanged;
    private void SetDirty(bool v)
    {
        if (_dirty == v) return;
        _dirty = v;
        try { DirtyChanged?.Invoke(); } catch { /* 通知失败不干扰业务 */ }
    }

    /// <summary>P1-01：错误走红色横幅（全文可选中、可复制详情）；成功/引导仍走状态文本区。</summary>
    private void ShowError(string msg)
    {
        _errorText.Text = msg;
        _errorBanner.Visibility = Visibility.Visible;
        _messageBox.Text = "";
        Core.AppLog.Write($"[错误横幅] {msg}");
    }

    /// <summary>收起错误横幅（下一次成功执行后调用）。</summary>
    private void ClearError()
    {
        if (_errorBanner.Visibility == Visibility.Collapsed) return;
        _errorBanner.Visibility = Visibility.Collapsed;
        Core.AppLog.Write("[错误横幅] 收起");
    }

    public WorkTab(MainWindow owner, IDbProvider provider, string db, string child, WorkMode mode, string? initialSql, string? sourceFile = null)
    {
        _owner = owner;
        _provider = provider;
        _db = db;
        _child = child;
        _mode = mode;
        SourceFile = sourceFile ?? "";

        _hlTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hlTimer.Interval = TimeSpan.FromMilliseconds(250);
        _hlTimer.Tick += (_, _) => { _hlTimer.Stop(); ApplyHighlight(); };
        if (mode == WorkMode.Query)
        {
            _editor.TextChanged += (_, _) => { if (!_applying) { _hlTimer.Stop(); _hlTimer.Start(); SetDirty(true); } };
            _editor.TextChanged += OnAutoCompleteTextChanged;   // ㉔：输入即弹（设置页开关默认开，Ctrl+Space 兜底不变）
            _editor.Loaded += (_, _) => SetEditorFont();
            Ant.ThemeApplied += OnThemeApplied;
            Unloaded += (_, _) => Ant.ThemeApplied -= OnThemeApplied;
            AttachEditorOps();   // 批次一：编辑器右键菜单＋快捷键操作集
            EnsureTables();   // 融合远端 D-12：补全候选预载当前库表名（弹层本体用格1 键盘化实现）
        }

        // 层色底定律：标签页根板显式挂主题底色，中间内容靠网格自带层色与描边，不依赖系统纯黑
        Background = Ant.Bg;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                                          // 0 工具栏
        RowDefinitions.Add(new RowDefinition { Height = mode == WorkMode.Query ? GridLength.Auto : new GridLength(0) });             // 1 编辑器
        RowDefinitions.Add(new RowDefinition { Height = mode == WorkMode.Query ? GridLength.Auto : new GridLength(0) });             // 2 B⑤ 可拖分栏
        // D-8 修红（2026-11-22 老板令）：结果区此前挂 Auto 行随内容缩成小条、下方空 Star 行整片留白——
        // 对齐 Navicat/DBeaver 口径：结果区撑满剩余空间，数据多时出滚动条
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });                                     // 3 结果区
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                                          // 4 B⑦ 查找条（默认收起）
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                                          // 5 B② 结果条（钉底固定行高）
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                                          // 6 消息区

        // 工具栏（B⑥：高频留一级，低频收「更多」分组菜单）
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(10, 8, 10, 8) };
        bar.Children.Add(_dbBox);
        bar.Children.Add(_runBtn);
        bar.Children.Add(_stopBtn);
        // D⑤：导出收成一个菜单——CSV / INSERT 并入「导出 ▾」（原三个并列按钮）
        // B⑥：低频项收进「更多 ▾」——Flyout 内保留原按钮（名称/AutomationId 不变，走查先点「更多」再点目标）
        var morePanel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6, Margin = new Thickness(10) };
        var moreFlyout = new Flyout { Content = morePanel };
        var moreBtn = new Button { Content = "更多 ▾", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(moreBtn, "work-more");
        moreBtn.Flyout = moreFlyout;
        void MoreLow(Button b)
        {
            morePanel.Children.Add(b);
            b.Click += (_, _) => moreFlyout.Hide();
        }
        if (provider is not (MongoProvider or RedisProvider)) MoreLow(_insBtn);
        bar.Children.Add(_exportMoreBtn);   // D⑤：CSV/INSERT/Excel/XML/HTML 收成一个「导出 ▾」菜单
        if (mode == WorkMode.TableData && provider.SupportsBrowse && provider.IsNoSql == false)
        {
            bar.Children.Add(_structBtn);
            bar.Children.Add(_addRowBtn);
            bar.Children.Add(_importBtn);
            bar.Children.Add(_designBtn);
            MoreLow(_fakerBtn);
            if (provider is RelationalProvider) MoreLow(_formBtn);   // 批次二格1：表单视图（关系库浏览页）
            MoreLow(_pasteBtn);   // 批次二格4：粘贴数据
        }
        if (mode == WorkMode.TableData && provider is RedisProvider && child.Length > 0)
        {
            bar.Children.Add(_delKeyBtn);   // Redis 键详情页：删除当前键
            _delKeyBtn.Click += (_, _) => _ = DeleteKeyAsync();
        }
        if (mode == WorkMode.Query) { MoreLow(_favBtn); MoreLow(_histBtn); }
        MoreLow(_reportBtn);
        MoreLow(_assistBtn);
        bar.Children.Add(moreBtn);
        bar.Children.Add(new TextBlock { Text = "　", Width = 8 });
        bar.Children.Add(_prevBtn);
        bar.Children.Add(_pageInfo);
        bar.Children.Add(_nextBtn);
        bar.Children.Add(_pageSizeBox);   // D⑦：每页行数
        bar.Children.Add(_pageJumpBox);   // D⑦：页码跳转
        bar.Children.Add(_browseFilterBox);   // D⑦：浏览筛选（当前结果关键字过滤）
        Children.Add(bar);
        SetRow(bar, 0);

        if (mode == WorkMode.Query)
        {
            Children.Add(_editor); SetRow(_editor, 1); _editor.Height = 170;
            // B⑤：编辑器与结果区之间可拖分栏——悬停高亮（↕光标因 WinUI 3 无公开光标 API 撤除，同 GridBuilder 口径），拖完即用（会话随页签）
            var vsplit = new Border
            {
                Height = 6,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(vsplit, "work-vsplit");
            bool vDrag = false; double vStartY = 0, vStartH = 0;
            vsplit.PointerEntered += (_, _) =>
            {
                vsplit.Background = Ant.Primary;
            };
            vsplit.PointerExited += (_, _) => { if (!vDrag) vsplit.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); };
            vsplit.PointerPressed += (_, e) => { vDrag = true; vStartY = e.GetCurrentPoint(vsplit).Position.Y; vStartH = _editor.Height; vsplit.CapturePointer(e.Pointer); e.Handled = true; };
            vsplit.PointerMoved += (_, e) =>
            {
                if (!vDrag) return;
                double dy = e.GetCurrentPoint(vsplit).Position.Y - vStartY;
                _editor.Height = Math.Clamp(vStartH + dy, 60, 600);
                e.Handled = true;
            };
            vsplit.PointerReleased += (_, e) =>
            {
                if (!vDrag) return;
                vDrag = false;
                vsplit.ReleasePointerCapture(e.Pointer);
                vsplit.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                e.Handled = true;
            };
            Children.Add(vsplit);
            SetRow(vsplit, 2);
            // 分栏插入后结果区/结果条/消息区顺延一行
        }

        _resultsHost.Padding = new Thickness(4);
        Children.Add(_resultsHost);
        SetRow(_resultsHost, 3);

        var msgBorder = new Border
        {
            Child = _messageBox,
            Margin = new Thickness(8, 4, 8, 8),
            BorderBrush = Ant.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg),
        };
        // P1-01 错误横幅：浅红底＋Ant.Error 左边条，消息全文可选中，附「复制详情」
        _errorBanner.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0xB4, 0x55, 0x2E));
        _errorBanner.BorderBrush = Ant.Error;
        _errorBanner.BorderThickness = new Thickness(3, 1, 1, 1);
        _errorBanner.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        (_errorBanner.Child as StackPanel)!.Children.Add(new TextBlock { Text = "⚠", VerticalAlignment = VerticalAlignment.Center, Foreground = Ant.Error });
        (_errorBanner.Child as StackPanel)!.Children.Add(_errorText);
        (_errorBanner.Child as StackPanel)!.Children.Add(_errorCopyBtn);
        _errorCopyBtn.Click += (_, _) =>
        {
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(_errorText.Text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                _messageBox.Text = "错误详情已复制。";
            }
            catch { /* 剪贴板失败不影响业务 */ }
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_errorBanner, "work-error-banner");

        var msgArea = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };
        msgArea.Children.Add(_errorBanner);
        msgArea.Children.Add(msgBorder);
        Children.Add(msgArea);
        SetRow(msgArea, 6);   // D-8 修红：消息区随结果区上移；B② 结果条占第 5 行钉底

        // B②：底部结果条钉死页签底部、固定行高——行数 · 耗时 · 时间，数据多走内部滚动
        var resultBarHost = new Border
        {
            Child = _resultBar,
            Height = 26,
            Background = Ant.Card,
            BorderBrush = Ant.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        Children.Add(resultBarHost);
        SetRow(resultBarHost, 5);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_resultBar, "work-result-bar");

        // B⑦：Ctrl+F 结果集查找条——默认收起，Ctrl+F 唤起，Esc 清空恢复
        var findBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(8, 4, 8, 4) };
        findBar.Children.Add(_findBox);
        findBar.Children.Add(_findHint);
        var findHost = new Border { Child = findBar, Background = Ant.Card, BorderBrush = Ant.Border, BorderThickness = new Thickness(0, 1, 0, 1), Visibility = Visibility.Collapsed };
        Children.Add(findHost);
        SetRow(findHost, 4);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_findBox, "work-find-box");
        _findBox.TextChanged += (_, _) => ApplyFindFilter();
        _findBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                _findBox.Text = "";
                findHost.Visibility = Visibility.Collapsed;
                _editor?.Focus(FocusState.Programmatic);
                e.Handled = true;
            }
        };
        var ctrlF = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.F };
        KeyboardAccelerators.Add(ctrlF);
        ctrlF.Invoked += (_, args) =>
        {
            findHost.Visibility = Visibility.Visible;
            _findBox.Focus(FocusState.Programmatic);
            args.Handled = true;
        };

        // UIA 自动化标识（界面自动化走查定位用）
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_editor, "work-editor");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_runBtn, "work-run");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_stopBtn, "work-stop");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_csvBtn, "work-csv");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_messageBox, "work-message");
        // 发现72：TextBox 的 UIA Name 默认只暴露 PlaceholderText（「消息」），消息正文读屏与走查断言都拿不到——文本变化同步到 Name
        _messageBox.TextChanged += (_, _) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            _messageBox, _messageBox.Text.Length > 0 ? _messageBox.Text : "消息");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_pageInfo, "work-pageinfo");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_resultsHost, "work-results");

        _runBtn.Click += (_, _) => _ = RunOrRefresh();
        _stopBtn.Click += (_, _) =>
        {
            if (_cancelable) { _runCts?.Cancel(); return; }   // 流式懒加载通道：令牌贯穿，可中止
            _owner.SetStatus("当前执行不支持中途停止，请等待完成");   // 发现69：整包 Execute/Browse 翻页点了给明示，不再无效果无提示
            AppLog.Write("[执行账] stop-unsupported=1");
        };
        // D⑤：导出收成一个菜单——CSV / INSERT 并入「导出 ▾」（原三个并列按钮）
        var exportMenu = new MenuFlyout();
        MenuFlyoutItem ExportItem(string text, string id, ExportKind kind)
        {
            var it = new MenuFlyoutItem { Text = text };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(it, id);
            it.Click += (_, _) => _ = ExportAsync(kind);
            exportMenu.Items.Add(it);
            return it;
        }
        ExportItem("导出 CSV（.csv）", "ExportCsv", ExportKind.Csv);
        ExportItem("导出 INSERT SQL（.sql）", "ExportInsert", ExportKind.InsertSql);
        ExportItem("导出 Excel（xlsx）", "ExportExcel", ExportKind.Excel);
        ExportItem("导出 XML", "ExportXml", ExportKind.Xml);
        ExportItem("导出 HTML", "ExportHtml", ExportKind.Html);
        _exportMoreBtn.Flyout = exportMenu;
        _structBtn.Click += (_, _) => _ = ShowStructure();
        _addRowBtn.Click += (_, _) => _ = EditRow(null);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_importBtn, "ImportXlsx");   // 批次四格1：导入入口锚点
        _importBtn.Click += (_, _) => _ = ImportCsv();
        _pasteBtn.Click += (_, _) => _ = UI.PastePreviewDialog.Show(_owner, this, _provider, _db, _child.Length > 0 ? _child : "main");   // 批次二格4
        _formBtn.Click += (_, _) =>   // 批次二格1：网格/表单双视图切换
        {
            _formMode = !_formMode;
            _formBtn.Content = _formMode ? "网格" : "表单";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_formBtn, "FormViewToggle");
            if (_browseResult is not null && _isRelational)
            {
                if (_formMode)
                {
                    var host = new FormViewHost(this);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, "FormViewHost");
                    host.Render(_offset);
                    _resultsHost.Content = host;
                }
                else ShowResult(_browseResult, editable: true);
            }
        };
        _designBtn.Click += (_, _) => _owner.OpenDesignerTab(_provider, _db, _child.Length > 0 ? _child : null, _cols.Count > 0 ? _cols : null);   // U2：表设计器内嵌页签（格5 增强随 Tab 迁移）
        _fakerBtn.Click += (_, _) => _ = UI.FakerDialog.Show(_owner, _provider, _db, _child.Length > 0 ? _child : "main");
        _histBtn.Click += (_, _) => ShowHistory();
        _favBtn.Click += (_, _) => ShowFavorites();
        _reportBtn.Click += (_, _) => _ = ShowReport();
        _assistBtn.Click += (_, _) =>
        {
            // 格15：结果入口——把当前首结果集快照带进设计助手（图表输入预填）
            var info = new NodeInfo { Kind = NodeKind.Database, Profile = _provider.Profile, Provider = _provider, Db = _db, Name = _db };
            UI.DesignAssistDialog.Show(_owner, info, table: _child.Length > 0 ? _child : null, last: _last?.Count > 0 ? _last[0] : null);
        };
        _prevBtn.Click += (_, _) => { if (_offset >= _pageSize) { _offset -= _pageSize; _ = RunOrRefresh(); } };
        _nextBtn.Click += (_, _) => { _offset += _pageSize; _ = RunOrRefresh(); };
        // D⑦：分页可调可跳页——每页行数下拉＋页码跳转；表浏览筛选框（当前页关键字过滤）
        _pageSizeBox.ItemsSource = new[] { "每页 50", "每页 100", "每页 200", "每页 500" };
        _pageSizeBox.SelectedIndex = 2;
        _pageSizeBox.SelectionChanged += (_, _) =>
        {
            if (_pageSizeBox.SelectedItem is ComboBoxItem ci && ci.Tag is int n) { _pageSize = n; _offset = 0; _ = RunOrRefresh(); }
        };
        _pageJumpBox.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            if (int.TryParse(_pageJumpBox.Text?.Trim(), out var page) && page >= 1)
            {
                _offset = (page - 1) * _pageSize;
                _ = RunOrRefresh();
            }
            e.Handled = true;
        };
        _browseFilterBox.TextChanged += (_, _) => ApplyBrowseFilter();
        AddAccelerator(Windows.System.VirtualKey.F5, Windows.System.VirtualKeyModifiers.None, RunNow);
        AddAccelerator(Windows.System.VirtualKey.Enter, Windows.System.VirtualKeyModifiers.Control, RunNow);
        AddAccelerator(Windows.System.VirtualKey.Space, Windows.System.VirtualKeyModifiers.Control, ShowComplete);
        if (mode == WorkMode.Query) AddAccelerator(Windows.System.VirtualKey.S, Windows.System.VirtualKeyModifiers.Control, () => _ = SaveSourceFileAsync());

        _dbBox.ItemsSource = new List<string> { string.IsNullOrEmpty(db) ? "(默认)" : db };
        _dbBox.SelectedIndex = 0;
        _dbBox.SelectionChanged += (_, _) =>
        {
            if (_dbBox.SelectedItem is string s && s != "(默认)" && s != "(加载中…)") _db = s;
        };
        _ = LoadDatabaseListAsync(db);   // 整改二批格2：异步加载全部库

        if (initialSql != null) EditorText = initialSql;
        if (mode == WorkMode.TableData) EditorText = "";

        UpdateButtons();
        if (mode == WorkMode.TableData) _ = RunOrRefresh();
    }

    /// <summary>整改二批格2：库下拉异步加载全部库，当前库选中；加载中占位，失败回退单项并横幅提示。</summary>
    private async Task LoadDatabaseListAsync(string currentDb)
    {
        try
        {
            _dbBox.ItemsSource = new List<string> { "(加载中…)" };
            _dbBox.SelectedIndex = 0;
            var dbs = await _provider.GetDatabases();
            if (dbs.Count == 0) throw new InvalidOperationException("库列表为空");
            var items = new List<string>(dbs);
            if (string.IsNullOrEmpty(currentDb)) items.Insert(0, "(默认)");
            else if (!items.Contains(currentDb)) items.Insert(0, currentDb);
            _dbBox.ItemsSource = items;
            _dbBox.SelectedItem = string.IsNullOrEmpty(currentDb) ? "(默认)" : currentDb;
            AppLog.Write($"[库下拉] 已加载 {dbs.Count} 个库");
        }
        catch (Exception ex)
        {
            _dbBox.ItemsSource = new List<string> { string.IsNullOrEmpty(currentDb) ? "(默认)" : currentDb };
            _dbBox.SelectedIndex = 0;
            ShowError(HumanError.Say(ex, "库列表加载失败"));
            AppLog.Write("[库下拉] error=" + ex.Message.Replace('\r', ' ').Replace('\n', ' '));
        }
    }

    private void AddAccelerator(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers mod, Action action)
    {
        var acc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key, Modifiers = mod };
        acc.Invoked += (s, e) => { e.Handled = true; action(); };
        KeyboardAccelerators.Add(acc);
    }

    /// <summary>归属 .sql 文件路径（需求合并稿第一章之二：Ctrl+S 覆盖保存用；空=未归属，另存）。</summary>
    public string SourceFile { get; set; } = "";

    /// <summary>Ctrl+S：有归属文件直接覆盖（UTF-8）；否则另存对话框（UTF-8 默认，可 BOM / GBK）。只写文件不执行。P1-02 起公开供关脏页签确认复用。</summary>
    public async Task SaveSourceFileAsync()
    {
        if (_mode != WorkMode.Query) { _messageBox.Text = "表数据页没有可保存的 SQL。"; return; }
        try
        {
            var sql = EditorText;
            if (SourceFile.Length > 0)
            {
                Core.SqlFileIo.Save(SourceFile, sql);
                _messageBox.Text = $"已保存到 {SourceFile}";
                SetDirty(false);
                ClearError();
                return;
            }

            // #216 同款预防：WinUI FileSavePicker 在 Server 会话原生崩溃（导出路径 c22a0575 已换）；
            // 另存统一走 WinForms 经典对话框，失败落 managed catch
            using var picker = new System.Windows.Forms.SaveFileDialog
            {
                Title = "另存 SQL",
                Filter = "SQL 文件 (*.sql)|*.sql",
                FileName = "未命名.sql",
            };
            if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var savePath = picker.FileName;

            // 编码选择：UTF-8（默认）/ UTF-8(BOM) / GBK
            var encBox = new ComboBox { MinWidth = 200, SelectedIndex = 0 };
            encBox.Items.Add("UTF-8");
            encBox.Items.Add("UTF-8（带 BOM）");
            encBox.Items.Add("GBK");
            var dlg = new ContentDialog
            {
                Title = "保存编码",
                Content = encBox,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = _owner.Content.XamlRoot,
            };
            if (await dlg.ShowThemedAsync() != ContentDialogResult.Primary) return;
            Core.SqlFileIo.Save(savePath, sql, gbk: encBox.SelectedIndex == 2, bom: encBox.SelectedIndex == 1);
            SourceFile = savePath;
            _messageBox.Text = $"已另存到 {savePath}";
            SetDirty(false);
            ClearError();
        }
        catch (Exception ex)
        {
            ShowError(HumanError.Say(ex, "保存失败"));
        }
    }

    private async void RunNow()
    {
        try { await RunOrRefresh(); } catch { /* 已在界面提示 */ }
    }

    private void UpdateButtons()
    {
        bool browse = _mode == WorkMode.TableData && _provider.SupportsBrowse;
        _prevBtn.Visibility = browse || _mode == WorkMode.TableData ? Visibility.Visible : Visibility.Collapsed;
        _nextBtn.Visibility = _prevBtn.Visibility;
        _pageInfo.Visibility = _prevBtn.Visibility;
        _pageSizeBox.Visibility = browse ? Visibility.Visible : Visibility.Collapsed;   // D⑦：分页控件随浏览态显隐
        _pageJumpBox.Visibility = _pageSizeBox.Visibility;
        _browseFilterBox.Visibility = _pageSizeBox.Visibility;
        _prevBtn.IsEnabled = _offset > 0;
    }

    // D⑦：浏览分页显示收拢——三分支共用（显示＋分页按钮态＋页码文案）；hits=筛选命中总数
    private void ShowBrowsePage(QueryResult r, Action<int>? onRowTap = null, bool editable = false, string unit = "行", string suffix = "", int? hits = null)
    {
        ShowResult(r, editable: editable, onRowTap: onRowTap);
        // 发现52：零行不再显示「第 1-0 行」倒挂
        _pageInfo.Text = r.TotalRows is long t
            ? (r.Rows.Count == 0
                ? $"共 {t:N0} {unit}{suffix}"
                : $"共 {t:N0} {unit} · 第 {_offset + 1}-{Math.Min(_offset + r.Rows.Count, int.MaxValue)} {unit}{suffix}")
            : $"{r.Rows.Count} {unit}";
        if (hits is int h && h > r.Rows.Count) _pageInfo.Text += $" · 筛选命中 {h:N0}";
        _prevBtn.IsEnabled = _offset > 0;
        _nextBtn.IsEnabled = _offset + _pageSize < (r.TotalRows ?? long.MaxValue);
    }

    // D⑦：浏览筛选——空串还原整页；否则对当前浏览结果做关键字过滤（列名或单元格命中）
    private void ApplyBrowseFilter()
    {
        if (_browseResult is null) return;
        var q = _browseFilterBox.Text?.Trim() ?? "";
        if (q.Length == 0) { ShowBrowsePage(_browseResult); return; }
        var filtered = FilterResult(_browseResult, q);
        ShowBrowsePage(filtered, hits: _browseResult.Rows.Count);
    }

    private async Task RunOrRefresh(string? sqlOverride = null)
    {
        if (_busy) return;
        _busy = true;
        int prevOffset = _offset;   // 发现51：浏览翻页失败时回滚 offset，避免页码漂移
        _runBtn.IsEnabled = false;
        _runCts = new CancellationTokenSource();
        _cancelable = false;   // 发现69：默认不可中止，流式懒加载通道置真
        _stopBtn.IsEnabled = true;
        try
        {
            QueryResult r;
            if (_mode == WorkMode.TableData)
            {
                if (!_provider.SupportsBrowse)
                {
                    _messageBox.Text = "该引擎不支持表/集合网格浏览。";
                    return;
                }
                r = await _provider.Browse(_db, _child, _offset, _pageSize);
                _browseResult = r;
                if (_provider is RedisProvider)
                {
                    bool isKeyDetail = _child.Length > 0;
                    ShowBrowsePage(r, onRowTap: isKeyDetail ? null : idx => OpenKeyDetail(idx), unit: "个键", suffix: isKeyDetail ? "" : " · 双击键查看详情");
                }
                else if (_isRelational)
                {
                    try { _cols = await _provider.GetColumns(_db, _child); } catch { _cols.Clear(); }
                    ShowBrowsePage(r, editable: true, unit: "行", suffix: " · 单击行可编辑");
                }
                else
                {
                    ShowBrowsePage(r, unit: "条");
                }
            }
            else
            {
                var sql = sqlOverride ?? EditorText;
                if (string.IsNullOrWhiteSpace(sql)) { _messageBox.Text = "请输入要执行的语句。"; return; }
                // 批次F：关系库走流式底座列懒加载通道（宽表首屏只物化可见列，横向滚动增量拉取）；流式关闭或非关系引擎回旧整包
                List<QueryResult> list;
                if (StreamingSwitch.Enabled && _provider is RelationalProvider lazyProv)
                {
                    _cancelable = true;   // 发现69：令牌贯穿的流式通道才可中止
                    list = await QueryRouter.ExecuteLazyAsync(lazyProv, _db, sql, 64, _runCts.Token);
                }
                else
                    list = await _provider.Execute(_db, sql);
                QueryHistory.Add(_provider.EngineName, _db, sql);
                ShowResults(list);
                // 执行账（2026-11-19）：执行值直接落应用日志，供云端走查零 UIA 对账（run 94-96 实锤 UIA 读结果轮询带走云机器）
                try
                {
                    var ledger = new System.Text.StringBuilder("[执行账] sql=");
                    ledger.Append(sql.Replace('\r', ' ').Replace('\n', ' '));
                    foreach (var rowItem in list)
                    {
                        ledger.Append(" | title=").Append(rowItem.Title)
                              .Append(" rows=").Append(rowItem.Rows.Count)
                              .Append(" cols=").Append(string.Join("/", rowItem.Columns));
                        for (int i = 0; i < rowItem.Rows.Count && i < 3; i++)
                        {
                            var cells = new string[rowItem.Rows[i].Length];
                            for (int c = 0; c < rowItem.Rows[i].Length; c++) cells[c] = Convert.ToString(rowItem.Rows[i][c]) ?? "";
                            ledger.Append(" row").Append(i).Append('=').Append(string.Join(",", cells));
                        }
                    }
                    AppLog.Write(ledger.ToString());
                }
                catch { /* 账本写失败静默：诊断绝不干扰业务 */ }
            }
            _owner.SetStatus($"{_provider.EngineName} · {_db}{(string.IsNullOrEmpty(_child) ? "" : " · " + _child)} 执行完成");
        }
        catch (OperationCanceledException)
        {
            if (_mode == WorkMode.TableData) { _offset = prevOffset; UpdateButtons(); }   // 发现51：翻页被停止也回滚
            _messageBox.Text = "已取消：执行被手动停止。";
            _owner.SetStatus("执行已停止");
            AppLog.Write("[执行账] cancelled=1");
        }
        catch (Exception ex)
        {
            if (_mode == WorkMode.TableData) { _offset = prevOffset; UpdateButtons(); }   // 发现51：翻页失败回滚 offset
            ShowError(HumanError.Say(ex, "执行失败"));
            _owner.SetStatus("执行出错");
            AppLog.Write("[执行账] error=" + ex.Message.Replace('\r', ' ').Replace('\n', ' '));
        }
        finally
        {
            _busy = false;
            _runBtn.IsEnabled = true;
            _stopBtn.IsEnabled = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    /// <summary>格9：列宽记忆键——查询模式用 SQL 文本，表数据浏览用 browse:库.表（EditorText 为空）。</summary>
    private string WidthKey() => EditorText.Length > 0 ? EditorText : $"browse:{_db}.{_child}";

    private void ShowResults(List<QueryResult> list)
    {
        _last = list;
        _lastFull = list;   // B⑦：全量留存供查找过滤
        RenderResults(list);
    }

    /// <summary>B⑦：按查找关键字过滤当前结果集（空关键字恢复全量）。</summary>
    private void ApplyFindFilter()
    {
        var q = _findBox.Text;
        if (q.Length == 0)
        {
            _findHint.Text = "";
            RenderResults(_lastFull);
            return;
        }
        var filtered = _lastFull.Select(r => FilterResult(r, q)).ToList();
        _findHint.Text = $"共 {_lastFull.Sum(x => x.Rows.Count)} 行 · 命中 {filtered.Sum(x => x.Rows.Count)} 行";
        RenderResults(filtered);
    }

    private static QueryResult FilterResult(QueryResult r, string q)
    {
        var fr = new QueryResult { Title = r.Title, Message = r.Message, Elapsed = r.Elapsed, Affected = r.Affected, TotalRows = r.TotalRows };
        fr.Columns.AddRange(r.Columns);
        foreach (var row in r.Rows)
            if (row.Any(c => (Convert.ToString(c) ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)))
                fr.Rows.Add(row);
        return fr;
    }

    private void RenderResults(List<QueryResult> list)
    {
        var panel = new StackPanel { Spacing = 12 };
        var msgs = new System.Text.StringBuilder();
        QueryResult? errResult = null;
        foreach (var r in list)
        {
            panel.Children.Add(GridBuilder.Build(r, null, EditorText, _provider.EngineName, _colWidths, WidthKey(), _owner, AssistInfo()));   // 批次E：右键分析/钉住；格9 用户列宽；三-10 设计助手入口
            if (r.Message.Length > 0) msgs.AppendLine($"[{r.Title}] {r.Message}");
            // P1-01：流式/分页通道不抛异常，SQL 错误以「错误：」前缀落在结果消息里——识别后转错误横幅，与异常路径同口径
            // ㊴ 统一错误文案层后 Provider 侧消息为「执行没成功：…」人话格式，两种口径都识别
            if (errResult is null && (r.Message.StartsWith("错误：", StringComparison.Ordinal) || r.Message.Contains("没成功："))) errResult = r;
        }
        _resultsHost.Content = panel;
        if (errResult is not null)
        {
            // 发现73：㊴统一文案层消息是「执行没成功：…」整句，旧码按「错误：」前缀硬剥 3 字符把「执行没」剥掉，横幅成了「执行失败：成功：…」；两种前缀都识别、只剥不改字
            var detail = errResult.Message.StartsWith("错误：", StringComparison.Ordinal)
                ? errResult.Message["错误：".Length..]
                : errResult.Message.StartsWith("执行没成功：", StringComparison.Ordinal)
                    ? errResult.Message["执行".Length..]
                    : errResult.Message;
            ShowError("执行失败：" + detail);
            return;
        }
        ClearError();
        _messageBox.Text = msgs.Length > 0 ? msgs.ToString() : $"{list.Count} 条结果 · 总耗时 {list.Sum(x => x.Elapsed.TotalMilliseconds):N0} ms";
        // ㊲验证坐实：结果条计数在流式/分页结果上只报已物化页行数，改用 TotalRows 优先（无则回落物化行数）
        _resultBar.Text = $"{list.Sum(x => x.TotalRows ?? x.Rows.Count):N0} 行 · {list.Sum(x => x.Elapsed.TotalMilliseconds):N0} ms · {DateTime.Now:MM-dd HH:mm:ss}";   // B② 结果条
    }

    /// <summary>整改三批三-10：结果网格右键设计助手入口的连接上下文（与 _assistBtn 同构）。</summary>
    private NodeInfo AssistInfo() => new() { Kind = NodeKind.Database, Profile = _provider.Profile, Provider = _provider, Db = _db, Name = _db };

    private void ShowResult(QueryResult r, bool editable = false, Action<int>? onRowTap = null)
    {
        _last = new List<QueryResult> { r };
        _lastFull = _last;   // B⑦：全量留存供查找过滤
        FrameworkElement el;
        if (onRowTap is not null) el = GridBuilder.Build(r, onRowTap, EditorText, _provider.EngineName, _colWidths, WidthKey(), _owner, AssistInfo());
        else if (editable && _isRelational) el = GridBuilder.Build(r, idx => _ = EditRow(idx), EditorText, _provider.EngineName, _colWidths, WidthKey(), _owner, AssistInfo());
        else el = GridBuilder.Build(r, null, EditorText, _provider.EngineName, _colWidths, WidthKey(), _owner, AssistInfo());
        _resultsHost.Content = el;
        _messageBox.Text = r.Message.Length > 0 ? r.Message
            : (onRowTap is not null
                ? $"{r.Rows.Count} 行 · {r.Elapsed.TotalMilliseconds:N0} ms · 单击行进入详情"
                : $"{r.Rows.Count} 行 · {r.Elapsed.TotalMilliseconds:N0} ms" + (editable && _isRelational ? " · 单击行编辑 / 删除" : ""));
        _resultBar.Text = $"{(r.TotalRows ?? r.Rows.Count):N0} 行 · {r.Elapsed.TotalMilliseconds:N0} ms · {DateTime.Now:MM-dd HH:mm:ss}";   // B② 结果条（㊲：总行数优先）
    }

    /// <summary>Redis 键列表：单击某键 → 打开该键详情页。</summary>
    private void OpenKeyDetail(int idx)
    {
        if (_browseResult is null || idx >= _browseResult.Rows.Count) return;
        var key = Convert.ToString(_browseResult.Rows[idx][0]) ?? "";
        if (key.Length == 0) return;
        var info = new NodeInfo { Kind = NodeKind.Table, Profile = _provider.Profile, Provider = _provider, Name = key, Db = _db };
        _owner.OpenTableTab(info);
    }

    /// <summary>Redis 键详情页：删除当前键并刷新列表来源。</summary>
    private async Task DeleteKeyAsync()
    {
        if (_provider is not RedisProvider rp) return;
        bool ok = await _owner.ConfirmAsync($"确定删除键“{_child}”吗？删除后数据不可自动恢复，请确认不再需要这个键。", "删除");
        if (!ok) return;
        try
        {
            bool n = await rp.ExecuteDelKey(_db, _child);
            _messageBox.Text = n ? $"键 {_child} 已删除。" : "键不存在（可能已过期或已被删除）。";
            _owner.SetStatus(n ? $"Redis 键 {_child} 已删除" : "键不存在");
        }
        catch (Exception ex)
        {
            ShowError(HumanError.Say(ex, "错误"));
        }
    }

    /// <summary>批次四格1/2：导出格式（CSV / INSERT / Excel xlsx / XML / HTML）。</summary>
    internal enum ExportKind { Csv, InsertSql, Excel, Xml, Html }

    private static (string Title, string Filter, string FileName) DialogOf(ExportKind k) => k switch
    {
        ExportKind.Csv => ("导出 CSV", "CSV 文件 (*.csv)|*.csv", "taodbm_export.csv"),
        ExportKind.InsertSql => ("导出 SQL", "SQL 文件 (*.sql)|*.sql", "taodbm_insert.sql"),
        ExportKind.Excel => ("导出 Excel", "Excel 工作簿 (*.xlsx)|*.xlsx", "taodbm_export.xlsx"),
        ExportKind.Xml => ("导出 XML", "XML 文件 (*.xml)|*.xml", "taodbm_export.xml"),
        _ => ("导出 HTML", "HTML 文件 (*.html)|*.html", "taodbm_export.html"),
    };

    private async Task ExportAsync(ExportKind kind)
    {
        if (_last.Count == 0) { _messageBox.Text = "没有可导出的结果，请先执行。"; return; }
        try
        {
            // #216: WinUI FileSavePicker natively crashes on Windows Server sessions (walk 4/4 app-death);
            // use WinForms save dialog (classic #32770, same on Server/client), failures go to managed catch
            var d = DialogOf(kind);
            using var dlg = new System.Windows.Forms.SaveFileDialog
            {
                Title = d.Title,
                Filter = d.Filter,
                FileName = d.FileName,
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var savePath = dlg.FileName;
            // 格6：多结果集先选导出对象（单个 / 全部各存一个文件，文件名 _r2/_r3 序号）
            List<int> picks;
            if (_last.Count > 1)
            {
                var picked = await PickExportSetAsync();
                if (picked is null) return;
                picks = picked;
                if (picks.Count > 1) AppLog.Write($"[导出] 集={picks.Count}");
            }
            else picks = new List<int> { 0 };

            for (int pi = 0; pi < picks.Count; pi++)
            {
                int idx = picks[pi];
                string path = savePath;
                if (picks.Count > 1)
                {
                    var dir = System.IO.Path.GetDirectoryName(savePath) ?? ".";
                    var name = System.IO.Path.GetFileNameWithoutExtension(savePath);
                    var ext = System.IO.Path.GetExtension(savePath);
                    path = System.IO.Path.Combine(dir, $"{name}{(idx == 0 ? "" : $"_r{idx + 1}")}{ext}");
                }
                await ExportOneAsync(kind, _last[idx], path);
            }
        }
        catch (Exception ex)
        {
            ShowError(HumanError.Say(ex, "导出失败"));
        }
    }

    /// <summary>格6：多结果集导出选择器（结果集 1..n ＋ 全部）。</summary>
    private async Task<List<int>?> PickExportSetAsync()
    {
        var combo = new ComboBox { MinWidth = 260, Header = "选择结果集" };
        for (int i = 0; i < _last.Count; i++) combo.Items.Add($"结果集 {i + 1}（{_last[i].Rows.Count} 行）");
        combo.Items.Add("全部（各存一个文件）");
        combo.SelectedIndex = 0;
        var dlg = new ContentDialog
        {
            Title = "多结果集导出",
            Content = combo,
            PrimaryButtonText = "导出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _owner.Content.XamlRoot,
        };
        if (await dlg.ShowThemedAsync() != ContentDialogResult.Primary) return null;
        if (combo.SelectedIndex == _last.Count) return Enumerable.Range(0, _last.Count).ToList();
        return new List<int> { combo.SelectedIndex };
    }

    private async Task ExportOneAsync(ExportKind kind, QueryResult src0, string savePath)
    {
        // 格7：懒加载结果走流式导出（分页枚举＋非模态进度＋取消）；其余保持整包口径
        if (src0 is LazyQueryResult lz)
        {
            // 批次F：懒加载结果导出全列——先把所有列拉齐再物化（列按需通道下导出=显式全量动作）
            await lz.Store.EnsureColumnsAsync(Enumerable.Range(0, lz.Store.TotalColumns));
            var progress = new Progress<long>(n => { if (_exportProgressText is not null) _exportProgressText.Text = $"已写 {n} 行…"; });
            await StreamExportAsync(kind, EnumerateStorePages(lz.Store, progress), lz.Store.ColumnNames, savePath);
            return;
        }
        // ㊽ 导出脱敏接线：整包导出前按列名规则打码（与 MaskPreviewDialog 同一套 MaskEngine 规则，默认开）
        var maskedSrc = Exporter.Masked(src0);
        if (!ReferenceEquals(maskedSrc, src0))
            AppLog.Write("[导出] 脱敏规则已应用：敏感列按 MaskEngine 打码");   // ㊽ 验收锚点
        src0 = maskedSrc;
        switch (kind)
        {
            case ExportKind.Csv:
                System.IO.File.WriteAllText(savePath, Exporter.Csv(src0), new System.Text.UTF8Encoding(false));
                break;
            case ExportKind.InsertSql:
                System.IO.File.WriteAllText(savePath, Exporter.InsertSql(src0, _child.Length > 0 ? _child : "table"), new System.Text.UTF8Encoding(false));
                break;
            case ExportKind.Excel:
            {
                long rows = ExcelWriter.Write(savePath, src0.Columns, src0.Rows.OfType<object?[]>());
                AppLog.Write($"[导出] xlsx rows={rows}");   // 批次四格1 验收锚点
                break;
            }
            case ExportKind.Xml:
            {
                using var w = new System.IO.StreamWriter(savePath, false, new System.Text.UTF8Encoding(false));
                long rows = XmlExporter.WriteStream(new[] { new Core.Streaming.QueryPage { Rows = src0.Rows.Select(r => (object[])r).ToArray(), HasMore = false } }, src0.Columns, w);
                AppLog.Write($"[导出] xml rows={rows}");   // 批次四格2 验收锚点
                break;
            }
            default:   // Html
            {
                using var w = new System.IO.StreamWriter(savePath, false, new System.Text.UTF8Encoding(false));
                long rows = HtmlExporter.WriteStream(new[] { new Core.Streaming.QueryPage { Rows = src0.Rows.Select(r => (object[])r).ToArray(), HasMore = false } }, src0.Columns, src0.Title, w);
                AppLog.Write($"[导出] html rows={rows}");   // 批次四格2 验收锚点
                break;
            }
        }
        _messageBox.Text = $"已导出到 {savePath}";
    }

    /// <summary>格7：把已加载的懒加载存储按行分页枚举成流式页序列（页大小 5000，边枚举边报进度）。</summary>
    private static IEnumerable<Core.Streaming.QueryPage> EnumerateStorePages(Core.Streaming.ColumnLazyStore store, IProgress<long>? progress = null)
    {
        const int pageSize = 5000;
        long written = 0;
        for (int offset = 0; offset < store.RowCount; offset += pageSize)
        {
            int count = Math.Min(pageSize, store.RowCount - offset);
            var rows = new object[count][];
            for (int r = 0; r < count; r++)
            {
                var row = new object[store.TotalColumns];
                for (int c = 0; c < store.TotalColumns; c++) row[c] = store.GetValue(offset + r, c)!;
                rows[r] = row;
            }
            written += count;
            progress?.Report(written);
            yield return new Core.Streaming.QueryPage { Rows = rows, HasMore = offset + count < store.RowCount };
        }
    }

    /// <summary>㊽ 流式导出脱敏：按列名规则逐格打码（与 MaskPreviewDialog 同一套 MaskEngine 规则）。
    /// 无敏感列时原样透传页序列，不复制不缓冲；有敏感列时逐页生成打码副本。</summary>
    private static IEnumerable<Core.Streaming.QueryPage> MaskPages(IEnumerable<Core.Streaming.QueryPage> pages, IReadOnlyList<string> columns)
    {
        var rules = columns.Select(c => MaskEngine.InferRule(c)).ToList();
        bool any = rules.Any(x => x != MaskRule.None);
        foreach (var p in pages)
        {
            if (!any) { yield return p; continue; }
            var rows = new object[p.Rows.Length][];
            for (int r = 0; r < p.Rows.Length; r++)
            {
                var src = p.Rows[r];
                var dst = new object[src.Length];
                for (int c = 0; c < src.Length; c++)
                    dst[c] = c < rules.Count && rules[c] != MaskRule.None
                        ? MaskEngine.MaskCell(columns[c], Exporter.Format(src[c]))
                        : src[c];
                rows[r] = dst;
            }
            yield return new Core.Streaming.QueryPage { Rows = rows, HasMore = p.HasMore };
        }
    }

    /// <summary>格7：流式导出——非模态进度浮层（已写行数）＋取消（删半成品文件）。</summary>
    private async Task StreamExportAsync(ExportKind kind, IEnumerable<Core.Streaming.QueryPage> pages, IReadOnlyList<string> columns, string savePath)
    {
        _exportCts = new CancellationTokenSource();
        _exportProgressText = new TextBlock { Text = "导出中…", FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap };
        var cancelBtn = new Button { Content = "取消", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        cancelBtn.Click += (_, _) => _exportCts?.Cancel();
        var box = new StackPanel { Spacing = 8, MinWidth = 300 };
        box.Children.Add(_exportProgressText);
        box.Children.Add(cancelBtn);
        var border = new Border
        {
            Child = box,
            Background = Ant.Card,
            BorderBrush = Ant.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg),
            Padding = new Thickness(12),
        };
        var popup = new Popup { Child = border };
        popup.IsOpen = true;
        // ㊽ 导出脱敏接线：流式导出同样按列名规则打码（与 MaskPreviewDialog 同一套 MaskEngine 规则，默认开）
        pages = MaskPages(pages, columns);
        long rows = 0;
        try
        {
            await Task.Run(() =>
            {
                if (kind == ExportKind.Excel)
                {
                    rows = ExcelWriter.WriteStream(savePath, columns, pages, _exportCts.Token);
                    AppLog.Write($"[导出] xlsx rows={rows}");   // 批次四格1 验收锚点
                    return;
                }
                using var w = new System.IO.StreamWriter(savePath, false, new System.Text.UTF8Encoding(false));
                switch (kind)
                {
                    case ExportKind.Csv:
                        rows = Exporter.CsvStream(pages, columns, w, _exportCts.Token);
                        break;
                    case ExportKind.InsertSql:
                        rows = Exporter.InsertSqlStream(pages, columns, _child.Length > 0 ? _child : "table", w, _exportCts.Token);
                        break;
                    case ExportKind.Xml:
                        rows = XmlExporter.WriteStream(pages, columns, w, _exportCts.Token);
                        AppLog.Write($"[导出] xml rows={rows}");   // 批次四格2 验收锚点
                        break;
                    default:
                        rows = HtmlExporter.WriteStream(pages, columns, _child.Length > 0 ? _child : "结果", w, _exportCts.Token);
                        AppLog.Write($"[导出] html rows={rows}");   // 批次四格2 验收锚点
                        break;
                }
            });
            _messageBox.Text = $"已导出到 {savePath}（流式 {rows} 行）";
            AppLog.Write($"[导出] 流式 rows={rows}");
        }
        catch (OperationCanceledException)
        {
            TryDeleteHalfWritten(savePath);
            ShowError("已取消");
            AppLog.Write("[导出] 流式取消");
        }
        catch (Exception ex)
        {
            TryDeleteHalfWritten(savePath);
            ShowError(HumanError.Say(ex, "导出失败"));
        }
        finally
        {
            popup.IsOpen = false;
            _exportProgressText = null;
            _exportCts.Dispose();
            _exportCts = null;
        }
    }

    private static void TryDeleteHalfWritten(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { /* 删不掉留给用户手动清理 */ }
    }

    // ---------- 表结构 ----------

    private async Task ShowStructure()
    {
        try
        {
            _cols = await _provider.GetColumns(_db, _child);
            var qr = new QueryResult { Title = $"{_child} · 结构", TotalRows = _cols.Count };
            qr.Columns.AddRange(new[] { "列名", "类型", "可空", "主键" });
            foreach (var c in _cols)
                qr.Rows.Add(new object?[] { c.Name, c.DataType, c.Nullable ? "是" : "否", c.IsKey ? "🔑 PRI" : "" });
            if (_cols.Count == 0) qr.Message = "未取到列信息。";
            ShowResult(qr);
            _messageBox.Text = $"表 {_child} 共 {_cols.Count} 列。";
        }
        catch (Exception ex)
        {
            ShowError(HumanError.Say(ex, "读取结构失败"));
        }
    }

    // ---------- 行内编辑（新增 / 修改 / 删除） ----------

    private ColumnSpec? KeyCol => _cols.FirstOrDefault(c => c.IsKey);

    /// <summary>批次二格3：外键候选预取——本表外键列 → 参考表键值 LIMIT 200（超时/不支持回空表）。</summary>
    private async Task<Dictionary<string, List<string>>> FetchFkCandidatesAsync()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!_isRelational) return map;
        var rs = await _provider.Execute(_db, ObjectTreeCatalog.ListForeignKeys(_provider.Profile.Engine, _db));
        var r = rs.FirstOrDefault();
        if (r is null) return map;
        int tCol = r.Columns.FindIndex(c => c.Equals("table_name", StringComparison.OrdinalIgnoreCase));
        int cCol = r.Columns.FindIndex(c => c.Equals("column_name", StringComparison.OrdinalIgnoreCase));
        int rtCol = r.Columns.FindIndex(c => c.Equals("ref_table", StringComparison.OrdinalIgnoreCase));
        int rcCol = r.Columns.FindIndex(c => c.Equals("ref_column", StringComparison.OrdinalIgnoreCase));
        if (tCol < 0 || cCol < 0 || rtCol < 0 || rcCol < 0) return map;
        foreach (var row in r.Rows)
        {
            if (Convert.ToString(row[tCol]) != _child) continue;
            var refTable = Convert.ToString(row[rtCol]) ?? "";
            var refCol = Convert.ToString(row[rcCol]) ?? "";
            if (refTable.Length == 0 || refCol.Length == 0) continue;
            try
            {
                var vals = await _provider.Execute(_db, $"SELECT {refCol} FROM {_provider.Quote(refTable)} LIMIT 200");
                var v0 = vals.FirstOrDefault();
                if (v0 is null) continue;
                map[Convert.ToString(row[cCol]) ?? ""] = v0.Rows
                    .Select(x => Convert.ToString(x[0]) ?? "")
                    .Where(s => s.Length > 0).ToList();
            }
            catch { /* 参考表读不到：该列回退文本框 */ }
        }
        return map;
    }

    // ---------- 批次二格1/格4：表单视图与粘贴数据的公开出口 ----------

    /// <summary>浏览结果行（表单视图行游标用；空表返回 null）。</summary>
    public System.Collections.Generic.IReadOnlyList<object?[]>? BrowseRows => _browseResult?.Rows;
    /// <summary>当前列信息（表单视图字段渲染用）。</summary>
    public System.Collections.Generic.IReadOnlyList<ColumnSpec> Columns => _cols;
    /// <summary>打开指定行的编辑弹层（表单视图「编辑本行」入口，走既有 EditRow 全口径）。</summary>
    public Task EditRowAt(int rowIndex) => EditRow(rowIndex);
    /// <summary>刷新浏览数据（粘贴数据落库后回显）。</summary>
    public Task RefreshAsync() { _offset = 0; return RunOrRefresh(); }

    private async Task EditRow(int? rowIndex)
    {
        if (!_isRelational) { _messageBox.Text = "该引擎不支持行内编辑。"; return; }
        if (_cols.Count == 0) { _messageBox.Text = "请先点“结构”或刷新数据读取列信息。"; return; }

        object?[]? existing = rowIndex.HasValue && _browseResult != null && rowIndex.Value < _browseResult.Rows.Count
            ? _browseResult.Rows[rowIndex.Value] : null;
        if (existing != null && KeyCol is null)
        {
            _messageBox.Text = "该表没有主键列，无法安全编辑（避免误改整表）。";
            return;
        }

        var fields = new List<RowEditorFactory.Field>();
        var nullChecks = new List<CheckBox>();
        var panel = new StackPanel { Spacing = 10, MinWidth = 380, Margin = new Thickness(0, 0, 12, 0) };
        panel.Children.Add(new TextBlock { Text = "勾选 NULL＝存 NULL；留空＝存空字符串（数字列留空仍按 NULL）。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });
        RowEditorFactory.BlobOwner = _owner;   // 批次二格2：大字段弹层宿主
        // 批次二格3：外键候选预取（2 秒超时回退普通文本框，不阻断编辑；异步等待不阻塞 UI 线程）
        var fkMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var fkTask = FetchFkCandidatesAsync();
            if (await Task.WhenAny(fkTask, Task.Delay(TimeSpan.FromSeconds(2))) == fkTask) fkMap = await fkTask;
        }
        catch { /* 外键候选取不到：全部回退普通文本框 */ }
        for (int i = 0; i < _cols.Count; i++)
        {
            var nullCheck = new CheckBox { Content = "NULL", HorizontalAlignment = HorizontalAlignment.Right, FontSize = AntTokens.FontMinor };
            string? initialText = null;
            if (existing != null && i < existing.Length)
            {
                var v = existing[i];
                bool isNull = v == null || v is DBNull;
                initialText = isNull ? "" : Exporter.Format(v);
                nullCheck.IsChecked = isNull;
            }
            // 批次二格3：外键列出可编辑下拉（候选来自参考表 LIMIT 200）
            var field = fkMap.TryGetValue(_cols[i].Name, out var cands) && cands.Count > 0 && !_cols[i].IsKey
                ? RowEditorFactory.CreateFk(_cols[i], initialText, cands)
                : RowEditorFactory.Create(_cols[i], initialText, readOnly: _cols[i].IsKey);   // 格4：按列类型出控件
            nullCheck.Checked += (_, _) => field.SetEnabled(false);
            nullCheck.Unchecked += (_, _) => field.SetEnabled(true);
            if (nullCheck.IsChecked == true) field.SetEnabled(false);
            fields.Add(field);
            nullChecks.Add(nullCheck);
            var fieldRow = new StackPanel { Spacing = 4 };
            fieldRow.Children.Add(field.Control);
            fieldRow.Children.Add(nullCheck);
            panel.Children.Add(fieldRow);
        }

        var dlg = new ContentDialog
        {
            Title = existing == null ? $"新增行 · {_child}" : $"编辑行 · {_child}",
            Content = new ScrollViewer { Content = panel, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "保存",
            SecondaryButtonText = "删除该行",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _owner.Content.XamlRoot,
        };

        var result = await dlg.ShowThemedAsync();
        if (result == ContentDialogResult.None) return;

        if (result == ContentDialogResult.Secondary)
        {
            if (existing == null || KeyCol is null) return;
            var confirm = new ContentDialog
            {
                Title = "确认删除这一行",
                Content = new TextBlock
                {
                    Text = $"将删除 {_child} 中 {KeyCol!.Name} = {fields[_cols.IndexOf(KeyCol)].GetText()} 的这一行，删除后需自行恢复数据，请确认。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Ant.Error,
                },
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = _owner.Content.XamlRoot,
            };
            if (await confirm.ShowThemedAsync() != ContentDialogResult.Primary) return;
            var del = $"DELETE FROM {_provider.Quote(_child)} WHERE {_provider.Quote(KeyCol!.Name)} = {Exporter.Literal(KeyCol, fields[_cols.IndexOf(KeyCol)].GetText())}";
            await ExecWrite(del, "已删除 1 行");
            return;
        }

        // 保存：UPDATE 或 INSERT。NULL 勾选语义：勾选＝存 NULL；未勾选＋留空＝数字列存 NULL、文本列存空字符串。
        var sets = new List<string>();
        var insCols = new List<string>();
        var insVals = new List<string>();
        for (int i = 0; i < _cols.Count; i++)
        {
            bool isNull = nullChecks[i].IsChecked == true;
            bool empty = fields[i].GetText().Length == 0;
            // 发现65：二进制列——未改动不回写（防 hex 展示值整列覆盖改坏 BLOB）；改动值须为偶数位十六进制
            bool isBin = Exporter.IsBinaryColumn(_cols[i].DataType);
            if (isBin && existing != null && !_cols[i].IsKey && !fields[i].Dirty) continue;
            if (isBin && !isNull && !empty)
            {
                var hex = fields[i].GetText().Trim();
                if (hex.Length % 2 != 0 || hex.Any(c => !Uri.IsHexDigit(c)))
                {
                    _messageBox.Text = $"{_cols[i].Name} 是二进制列：请填偶数位的十六进制（0-9、A-F），或勾选 NULL。";
                    return;
                }
            }
            string val;
            if (isNull)
            {
                val = "NULL";
            }
            else if (empty)
            {
                val = CsvImporter.IsNumeric(_cols[i].DataType) ? "NULL" : "''";
            }
            else
            {
                val = Exporter.Literal(_cols[i], fields[i].GetText());
            }

            if (existing == null)
            {
                // 新增：勾选 NULL 或填了值才纳入列；未勾选且留空省略，保留列默认值语义。
                if (isNull || !empty) { insCols.Add(_provider.Quote(_cols[i].Name)); insVals.Add(val); }
            }
            else if (!_cols[i].IsKey)
            {
                sets.Add($"{_provider.Quote(_cols[i].Name)} = {val}");
            }
        }

        string sql;
        if (existing == null)
        {
            if (insCols.Count == 0) { _messageBox.Text = "没有填写任何值。"; return; }
            sql = $"INSERT INTO {_provider.Quote(_child)} ({string.Join(", ", insCols)}) VALUES ({string.Join(", ", insVals)})";
        }
        else
        {
            if (sets.Count == 0) { _messageBox.Text = "没有可更新的列（主键列不可修改）。"; return; }
            sql = $"UPDATE {_provider.Quote(_child)} SET {string.Join(", ", sets)} WHERE {_provider.Quote(KeyCol!.Name)} = {Exporter.Literal(KeyCol, fields[_cols.IndexOf(KeyCol)].GetText())}";
        }
        await ExecWrite(sql, existing == null ? "已新增 1 行" : "已保存修改");
    }

    private async Task ExecWrite(string sql, string okMsg)
    {
        try
        {
            var list = await _provider.Execute(_db, sql);
            QueryHistory.Add(_provider.EngineName, _db, sql);   // 发现63：行编辑写盘也入查询历史，与手敲执行同口径
            var qr = list.LastOrDefault();
            _messageBox.Text = qr is { Message.Length: > 0 } ? qr.Message : okMsg;
            _owner.SetStatus(okMsg);
            await RunOrRefresh();
            // 空页守卫：删除后当前页可能变空，回退一页再刷，避免停在空页。
            if (_browseResult is { Rows.Count: 0 } && _offset > 0)
            {
                _offset = Math.Max(0, _offset - _pageSize);
                await RunOrRefresh();
            }
        }
        catch (Exception ex)
        {
            ShowError(HumanError.Say(ex, "执行失败"));
        }
    }

    // ---------- CSV / JSON 导入 ----------

    /// <summary>发现61：错误行中止专用异常——与用户手动取消区分，收尾时报「已导入 N 行后中止」而非「已取消」。</summary>
    private sealed class ImportAbortException : Exception
    {
        public ImportAbortException(string message) : base(message) { }
    }

    private async Task ImportCsv()
    {
        if (!_isRelational) { _messageBox.Text = "该引擎不支持数据导入。"; return; }
        try
        {
            // 格3：FileOpenPicker 换 WinForms OpenFileDialog（#216 同款预防：Server 会话原生崩）
            using var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title = "选择导入文件",
                Filter = "数据文件 (*.csv;*.json;*.xlsx;*.txt)|*.csv;*.json;*.xlsx;*.txt|所有文件 (*.*)|*.*",
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var path = dlg.FileName;
            var file = new System.IO.FileInfo(path);

            // 发现54/55：CSV/TXT 不再整文件读入内存——按探测编码（BOM/UTF-8/GBK 自适应）开流式读取器；全文进内存仅 JSON 解析需要
            bool isJson = file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            string? text = isJson ? await System.IO.File.ReadAllTextAsync(path) : null;
            // 批次四格1（2026-11-23）：xlsx 导入分流——ExcelReader 读首表（首行表头 + 公式读缓存值），复用 CSV 映射与执行链
            bool isXlsx = file.Name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
            // 发现54：编码探测——BOM→UTF-8(BOM)；首段 GBK 严格试解含非 ASCII→GBK；否则 UTF-8（每次调用开新读取器）
            System.IO.TextReader OpenText() => new System.IO.StreamReader(path, Core.SqlFileIo.DetectEncoding(path));
            List<string> headers = new();
            List<string?[]> rows = new();
            ImportPreviewDialog.ImportPlan? plan = null;
            if (isXlsx)
            {
                try { (headers, rows) = ExcelReader.Read(path); }
                catch (Exception ex) { ShowError(HumanError.Say(ex, "xlsx 读取失败")); return; }
                if (headers.Count == 0 || rows.Count == 0) { _messageBox.Text = "xlsx 首个工作表为空或没有表头行。"; return; }
                AppLog.Write($"[导入] xlsx rows={rows.Count}");   // 批次四格1 验收锚点
            }
            else if (isJson)
            {
                (headers, rows) = JsonImporter.Parse(text ?? "");
                if (headers.Count == 0 || rows.Count == 0) { _messageBox.Text = "JSON 内容为空，或不是「对象数组 / 每行一个对象」的格式，未能读出数据。"; return; }
            }
            if (_cols.Count == 0) _cols = await _provider.GetColumns(_db, _child);

            if (isXlsx)
            {
                // 批次四格1：xlsx 先出预览与映射（前 50 行），确认后走 BuildInserts（与 JSON 同执行链）
                plan = await ImportPreviewDialog.ShowAsync(_owner, _cols, headers, rows.Take(50).ToList());
                if (plan is null) return;
            }
            else if (!isJson)
            {
                // 格3：先出预览与映射（前 50 行），确认后才执行导入
                List<string> previewHeaders = new();
                List<string?[]> previewRows = new();
                // 发现55：预览也走流式——单次枚举取前 50 行，不整文件进内存
                using (var previewReader = OpenText())
                {
                    foreach (var b in ImportStreaming.CsvBatches(previewReader, new ImportOptions { BatchSize = 50 }))
                    {
                        if (previewHeaders.Count == 0 && b.Headers is { Count: > 0 }) previewHeaders.AddRange(b.Headers);
                        previewRows.AddRange(b.Rows);
                        if (previewRows.Count >= 50) break;
                    }
                }
                if (previewHeaders.Count == 0 || previewRows.Count == 0) { _messageBox.Text = "CSV 内容为空或格式无法识别。"; return; }
                plan = await ImportPreviewDialog.ShowAsync(_owner, _cols, previewHeaders, previewRows);
                if (plan is null) return;
            }

            // 格3：导入进度（ProgressBar）＋取消（CancellationToken 贯穿 CsvBatches）
            _importCts = new CancellationTokenSource();
            var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true };
            var progressText = new TextBlock { Text = "导入中…", FontSize = AntTokens.FontMinor };
            var cancelBtn = new Button { Content = "取消", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(cancelBtn, "ImportCancel");
            var box = new StackPanel { Spacing = 8, MinWidth = 300 };
            box.Children.Add(progressText);
            box.Children.Add(progressBar);
            box.Children.Add(cancelBtn);
            var border = new Border
            {
                Child = box,
                Background = Ant.Card,
                BorderBrush = Ant.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg),
                Padding = new Thickness(12),
            };
            var popup = new Popup { Child = border };
            cancelBtn.Click += (_, _) => _importCts.Cancel();
            popup.IsOpen = true;
            var ct = _importCts.Token;
            AppLog.Write($"[导入] 开始 file={file.Name} json={isJson} xlsx={isXlsx}");

            int total = 0, ok = 0; bool truncated = false;
            var errs = new System.Text.StringBuilder();
            int errCount = 0;
            // 发现62：错误明细封顶 20 条，超出只报条数，不再无限拼进消息
            void AddErr(string msg)
            {
                errCount++;
                if (errCount <= 20) errs.AppendLine(msg);
            }
            try
            {
                if (isJson || isXlsx)
                {
                    ct.ThrowIfCancellationRequested();
                    // xlsx 列映射按预览计划重排（与 CSV 同口径）
                    List<string> execHeaders = headers;
                    List<string?[]> execRows = rows;
                    if (isXlsx && plan is not null)
                    {
                        execHeaders = plan.MappedHeaders.ToList();
                        execRows = rows.Select(r => plan.MappedCsvIndexes.Select(i => i < r.Length ? r[i] : null).ToArray()).ToList();
                    }
                    var stmts = CsvImporter.BuildInserts(_provider, _db, _child, _cols, execHeaders, execRows);
                    foreach (var s in stmts)
                    {
                        ct.ThrowIfCancellationRequested();
                        var list = await _provider.Execute(_db, s);
                        foreach (var qr in list)
                        {
                            if (qr.Message.StartsWith("错误", StringComparison.Ordinal) || qr.Message.Contains("没成功："))
                            {
                                AddErr(qr.Message);
                                if (plan is not null && !plan.SkipErrors) throw new ImportAbortException("错误行中止");
                            }
                            else ok++;
                        }
                    }
                    total = rows.Count;
                }
                else
                {
                    // CSV 走流式批次：逐批生成并执行 INSERT，内存中仅保留单批；列映射按预览计划重排
                    // 发现55：执行段同样流式——按探测编码开读取器，不再依赖整文件文本
                    using var execReader = OpenText();
                    foreach (var b in ImportStreaming.CsvBatches(execReader, new ImportOptions { BatchSize = 100 }))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (b.Headers == null || b.Rows.Count == 0) continue;
                        List<string> execHeaders;
                        List<string?[]> execRows;
                        if (plan is null) { execHeaders = b.Headers.ToList(); execRows = b.Rows.ToList(); }
                        else
                        {
                            execHeaders = plan.MappedHeaders.ToList();
                            execRows = b.Rows.Select(r => plan.MappedCsvIndexes.Select(i => i < r.Length ? r[i] : null).ToArray()).ToList();
                            if (!plan.FirstRowHeader)
                            {
                                // 首行不是表头：把原首行当数据补回（仅首批）
                                if (total == 0) execRows.Insert(0, b.Headers.Select(h => (string?)h).ToArray());
                            }
                        }
                        var stmts = CsvImporter.BuildInserts(_provider, _db, _child, _cols, execHeaders, execRows);
                        foreach (var s in stmts)
                        {
                            ct.ThrowIfCancellationRequested();
                            var list = await _provider.Execute(_db, s);
                            foreach (var qr in list)
                            {
                                if (qr.Message.StartsWith("错误", StringComparison.Ordinal) || qr.Message.Contains("没成功："))
                                {
                                    AddErr(qr.Message);
                                    if (plan is not null && !plan.SkipErrors) throw new ImportAbortException("错误行中止");
                                }
                                else ok++;
                            }
                        }
                        total = (int)b.CumulativeRows;
                        truncated |= b.Truncated;
                        progressText.Text = $"导入中…（累计 {total} 行）";
                    }
                    if (total == 0) { _messageBox.Text = "CSV 内容为空或格式无法识别。"; return; }
                }
                QueryHistory.Add(_provider.EngineName, _db, $"-- {(isXlsx ? "xlsx" : isJson ? "JSON" : "CSV")} 导入 {file.Name}（{total} 行）");
                _messageBox.Text = $"导入完成：共 {total} 行，成功 {ok} 行{(truncated ? "（已达行数上限，超出部分被截断）" : "")}{(errCount > 0 ? $"\n错误 {errCount} 条{(errCount > 20 ? "（仅显示前 20 条）" : "")}：\n" + errs : "")}";
                _owner.SetStatus($"{(isXlsx ? "xlsx" : isJson ? "JSON" : "CSV")} 导入 {file.Name}：{ok}/{total} 行");
                AppLog.Write($"[导入] 完成 file={file.Name} total={total} ok={ok}");
                _offset = 0;
                await RunOrRefresh();
            }
            catch (ImportAbortException)
            {
                // 发现61：错误行中止不再报成「已取消」
                ShowError($"导入中止：出错行未导入，中止前已成功导入 {ok} 行。{(errCount > 0 ? "\n" + errs : "")}");
                AppLog.Write($"[导入] 错误行中止 ok={ok}");
            }
            catch (OperationCanceledException)
            {
                // 发现60：手动取消也报已导入行数
                ShowError($"已取消：取消前已成功导入 {ok} 行。");
                AppLog.Write($"[导入] 取消 ok={ok}");
            }
            finally
            {
                popup.IsOpen = false;
                _importCts.Dispose();
                _importCts = null;
            }
        }
        catch (Exception ex)
        {
            ShowError(HumanError.Say(ex, "导入失败"));
        }
    }

    // ---------- 标签会话快照（重启恢复用） ----------

    public string SessionConnectionId => _provider.Profile.Id;
    public string SessionMode => _mode.ToString();
    public string SessionDb => _db;
    public string SessionChild => _child;
    public string SessionSql => EditorText;
    /// <summary>P1-03：文件归属随会话落盘（重启恢复后 Ctrl+S 直接回写原文件）。</summary>
    public string SessionSourceFile => SourceFile;

    // ---------- 查询收藏 ----------

    private async void ShowFavorites()
    {
        var lv = new ListView { SelectionMode = ListViewSelectionMode.Single, Height = 260 };
        Action refill = () =>
        {
            lv.Items.Clear();
            foreach (var f in Favorites.Load())
            {
                string oneLine = f.Sql.Replace("\r", " ").Replace("\n", " ");
                if (oneLine.Length > 70) oneLine = oneLine[..70] + "…";
                lv.Items.Add(new TextBlock
                {
                    Text = $"{f.Name}　[{f.Engine}/{(f.Database.Length > 0 ? f.Database : "(默认)")}]  {oneLine}",
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 480,
                });
            }
        };
        refill();
        if (lv.Items.Count == 0)
        {
            var empty = new ContentDialog
            {
                Title = "查询收藏",
                Content = new TextBlock { Text = "还没有收藏。把编辑器里的 SQL 写好后，点「收藏」→「保存当前 SQL」即可。", Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "好的",
                XamlRoot = _owner.Content.XamlRoot,
            };
            await empty.ShowThemedAsync();
            return;
        }

        var nameBox = new TextBox { PlaceholderText = "给这段 SQL 起个名字", Text = $"SQL {DateTime.Now:MM-dd HH:mm}" };
        var saveBtn = new Button { Content = "保存当前 SQL", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var panel = new StackPanel { Spacing = 8 };
        var saveRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        saveRow.Children.Add(nameBox);
        saveRow.Children.Add(saveBtn);
        panel.Children.Add(saveRow);
        panel.Children.Add(lv);
        var dlg = new ContentDialog
        {
            Title = "查询收藏（点选填入编辑器）",
            Content = panel,
            PrimaryButtonText = "填入所选",
            SecondaryButtonText = "删除所选",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _owner.Content.XamlRoot,
        };
        saveBtn.Click += (_, _) =>
        {
            var sql = EditorText.Trim();
            var name = nameBox.Text.Trim();
            if (sql.Length == 0 || name.Length == 0) return;
            var items = Favorites.Load();
            Favorites.Add(items, name, _provider.EngineName, _db, sql);
            Favorites.Save(items);
            nameBox.Text = $"SQL {DateTime.Now:MM-dd HH:mm}";
            refill();
        };
        var result = await dlg.ShowThemedAsync();
        var listNow = Favorites.Load();
        if (result == ContentDialogResult.Primary && lv.SelectedIndex >= 0)
            EditorText = listNow[lv.SelectedIndex].Sql;
        else if (result == ContentDialogResult.Secondary && lv.SelectedIndex >= 0)
        {
            Favorites.Remove(listNow, listNow[lv.SelectedIndex].Id);
            Favorites.Save(listNow);
        }
    }

    // ---------- 查询历史 ----------

    private async void ShowHistory()
    {
        var list = QueryHistory.Items.Take(50).ToList();
        if (list.Count == 0)
        {
            var empty = new ContentDialog
            {
                Title = "查询历史",
                Content = new TextBlock { Text = "还没有历史记录。执行过的语句会出现在这里。", Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "好的",
                XamlRoot = _owner.Content.XamlRoot,
            };
            await empty.ShowThemedAsync();
            return;
        }

        var lv = new ListView { SelectionMode = ListViewSelectionMode.Single, Height = 360 };
        foreach (var h in list)
        {
            string oneLine = h.Sql.Replace("\r", " ").Replace("\n", " ");
            if (oneLine.Length > 90) oneLine = oneLine[..90] + "…";
            lv.Items.Add(new TextBlock
            {
                Text = $"[{h.Time:MM-dd HH:mm}] {h.Engine}/{h.Database}  {oneLine}",
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 480,
            });
        }
        var dlg = new ContentDialog
        {
            Title = "查询历史（点选填入编辑器）",
            Content = lv,
            PrimaryButtonText = "填入所选",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _owner.Content.XamlRoot,
        };
        var result = await dlg.ShowThemedAsync();
        if (result == ContentDialogResult.Primary && lv.SelectedIndex >= 0)
            EditorText = list[lv.SelectedIndex].Sql;
    }

    // ---------- 报表 ----------

    private async Task ShowReport()
    {
        var qr = _last.FirstOrDefault();
        if (qr is null) { _messageBox.Text = "没有可生成报表的结果，请先执行查询或浏览数据。"; return; }
        var (rows, stats) = ReportBuilder.Build(qr);

        var lv = new ListView { Height = 320, SelectionMode = ListViewSelectionMode.None };
        foreach (var c in stats)
        {
            string min = c.Min?.ToString("0.###") ?? "-", max = c.Max?.ToString("0.###") ?? "-", avg = c.Avg?.ToString("0.###") ?? "-";
            string top = c.Top.Count == 0 ? "-" : string.Join("，", c.Top.Select(t => $"{t.Value}（{t.Count}）"));
            lv.Items.Add(new TextBlock
            {
                Text = $"{c.Name}：空 {c.NullCount} · 去重 {c.DistinctCount}" +
                       (c.Min.HasValue ? $" · 最小 {min} · 最大 {max} · 平均 {avg}" : $" · 常见值：{top}"),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 560,
            });
        }
        var exportBtn = new Button { Content = "导出 HTML 报表", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var dictBtn = new Button { Content = "导出数据字典（HTML / Word）", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dictBtn, "DataDictExport");   // 批次四格7 验收锚点
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = $"共 {rows} 行 · {qr.Columns.Count} 列", Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor });
        panel.Children.Add(lv);
        panel.Children.Add(exportBtn);
        panel.Children.Add(dictBtn);
        var dlg = new ContentDialog
        {
            Title = "报表 · " + qr.Title,
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = _owner.Content.XamlRoot,
        };
        exportBtn.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeChoices.Add("HTML 报表", new List<string> { ".html" });
                picker.SuggestedFileName = $"TaoDBM-报表-{DateTime.Now:yyyyMMdd-HHmm}";
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
                var file = await picker.PickSaveFileAsync();
                if (file is null) return;
                await Windows.Storage.FileIO.WriteTextAsync(file, ReportBuilder.ToHtml(qr, qr.Title));
                _messageBox.Text = "报表已导出到 " + file.Path;
            }
            catch (Exception ex) { ShowError(HumanError.Say(ex, "导出失败")); }
        };
        // 数据字典完整导出：当前连接的库逐表取列，HTML / Word 双口径（对账表第五章收口）
        dictBtn.Click += async (_, _) =>
        {
            try
            {
                var tables = new List<DictionaryTable>();
                var names = await _provider.GetChildren(_db);
                foreach (var name in names)
                {
                    List<ColumnSpec> cols;
                    try { cols = await _provider.GetColumns(_db, name); }
                    catch { continue; }
                    if (cols.Count == 0) continue;
                    tables.Add(DataDictionary.FromSpecs(name, cols));
                }
                if (tables.Count == 0) { _messageBox.Text = "该库没有可导出的表。"; return; }
                // 批次四格7：索引/外键两段入账（ObjectTreeCatalog 目录口径，失败静默退空段）
                string Cell(object?[] row, List<string> cols, string col)
                {
                    var i = cols.FindIndex(c => string.Equals(c, col, StringComparison.OrdinalIgnoreCase));
                    return i >= 0 && i < row.Length ? Convert.ToString(row[i]) ?? "" : "";
                }
                int idxCount = 0, fkCount = 0;
                try
                {
                    var ixRs = await _provider.Execute(_db, ObjectTreeCatalog.ListIndexes(_provider.Profile.Engine, _db));
                    var ixRows = ixRs.Count > 0 ? ixRs[0].Rows : new List<object?[]>();
                    var ixCols = ixRs.Count > 0 ? ixRs[0].Columns : new List<string>();
                    foreach (var r in ixRows)
                    {
                        var t = tables.FirstOrDefault(x => string.Equals(x.Name, Cell(r, ixCols, "table_name"), StringComparison.OrdinalIgnoreCase));
                        if (t is null) continue;
                        string nu = Cell(r, ixCols, "non_unique"), iu = Cell(r, ixCols, "is_unique"), def = Cell(r, ixCols, "indexdef");
                        bool unique = nu.Length > 0 ? nu == "0"
                            : iu.Length > 0 ? (iu == "1" || string.Equals(iu, "True", StringComparison.OrdinalIgnoreCase))
                            : def.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
                        t.Indexes.Add(new DictionaryIndex { Table = t.Name, Name = Cell(r, ixCols, "index_name"), Unique = unique });
                        idxCount++;
                    }
                }
                catch { /* 无索引视图或权限不足：退空段 */ }
                try
                {
                    var fkRs = await _provider.Execute(_db, ObjectTreeCatalog.ListForeignKeys(_provider.Profile.Engine, _db));
                    var fkRows = fkRs.Count > 0 ? fkRs[0].Rows : new List<object?[]>();
                    var fkCols = fkRs.Count > 0 ? fkRs[0].Columns : new List<string>();
                    foreach (var r in fkRows)
                    {
                        var t = tables.FirstOrDefault(x => string.Equals(x.Name, Cell(r, fkCols, "table_name"), StringComparison.OrdinalIgnoreCase));
                        if (t is null) continue;
                        t.ForeignKeys.Add(new DictionaryForeignKey
                        {
                            Table = t.Name,
                            Column = Cell(r, fkCols, "column_name"),
                            RefTable = Cell(r, fkCols, "ref_table"),
                            RefColumn = Cell(r, fkCols, "ref_column"),
                            Name = Cell(r, fkCols, "constraint_name"),
                        });
                        fkCount++;
                    }
                }
                catch { /* 无外键视图或权限不足：退空段 */ }
                AppLog.Write($"[数据字典] 表={tables.Count} 索引={idxCount} 外键={fkCount}");   // 批次四格7 验收锚点
                var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeChoices.Add("HTML 数据字典", new List<string> { ".html" });
                picker.FileTypeChoices.Add("Word 数据字典", new List<string> { ".doc" });
                picker.SuggestedFileName = $"TaoDBM-数据字典-{_db}-{DateTime.Now:yyyyMMdd-HHmm}";
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
                var file = await picker.PickSaveFileAsync();
                if (file is null) return;
                bool isWord = file.Path.EndsWith(".doc", StringComparison.OrdinalIgnoreCase);
                string content = isWord
                    ? DataDictionary.ToWord(tables, $"数据字典 · {_db}")
                    : DataDictionary.ToHtml(tables, $"数据字典 · {_db}");
                await Windows.Storage.FileIO.WriteTextAsync(file, content);
                _messageBox.Text = $"数据字典已导出（{tables.Count} 张表）到 " + file.Path;
            }
            catch (Exception ex) { ShowError(HumanError.Say(ex, "字典导出失败")); }
        };
        await dlg.ShowThemedAsync();
    }

    // ---------- SQL 语法高亮（RichEditBox 着色） ----------

    /// <summary>编辑器文本（Document 读写，末尾可能有段落符，执行前会 Trim）。</summary>
    public string EditorText
    {
        get
        {
            try { _editor.Document.GetText(Microsoft.UI.Text.TextGetOptions.None, out var t); return t ?? ""; }
            catch { return ""; }
        }
        set
        {
            _applying = true;
            Safe.Run(() => _editor.Document?.SetText(Microsoft.UI.Text.TextSetOptions.None, value ?? ""), "编辑器写文本");
            _applying = false;
            ApplyHighlight();
        }
    }

    private void SetEditorFont()
    {
        try
        {
            var fmt = _editor.Document.GetDefaultCharacterFormat();
            fmt.Name = "Consolas";
            fmt.Size = 13;
            _editor.Document.SetDefaultCharacterFormat(fmt);
            ApplyHighlight();
        }
        catch { /* 字体设置失败不影响使用 */ }
    }

    /// <summary>按 SqlHighlighter 词法结果着色，保留光标位置。</summary>
    /// <summary>换肤后重刷高亮：关键字取当前主题主色（ThemeApplied 在画刷变色后广播）。</summary>
    private void OnThemeApplied(IReadOnlyDictionary<string, string> pal)
    {
        if (_mode == WorkMode.Query) ApplyHighlight();
    }

    private void ApplyHighlight()
    {
        if (_mode != WorkMode.Query) return;
        var doc = _editor.Document;
        if (doc is null) return;
        _applying = true;
        try
        {
            string text = EditorText;
            if (text.Length == 0) return;
            int caret = doc.Selection?.StartPosition ?? 0;

            // 先整体还原默认色，避免已删关键字的残色
            doc.GetRange(0, text.Length).CharacterFormat.ForegroundColor = Ant.Text.Color;

            foreach (var t in SqlHighlighter.Tokenize(text))
            {
                if (t.Kind == TokenKind.Plain) continue;
                int s = Math.Min(t.Start, text.Length);
                int e = Math.Min(t.Start + t.Length, text.Length);
                if (e <= s) continue;
                var r = doc.GetRange(s, e);
                r.CharacterFormat.ForegroundColor = t.Kind switch
                {
                    TokenKind.Keyword => Ant.Primary.Color,
                    TokenKind.TypeName => Ant.Color("722ED1"),
                    TokenKind.String => Ant.Color("D46B08"),
                    TokenKind.Comment => Ant.Color("8C8C8C"),
                    TokenKind.Number => Ant.Color("389E0D"),
                    TokenKind.QuotedIdent => Ant.Color("13C2C2"),
                    _ => Ant.Color("595959"),
                };
                if (t.Kind is TokenKind.Keyword or TokenKind.TypeName) r.CharacterFormat.Bold = Microsoft.UI.Text.FormatEffect.On;
                if (t.Kind == TokenKind.Comment) r.CharacterFormat.Italic = Microsoft.UI.Text.FormatEffect.On;
            }

            // 批次一：括号配对高亮——光标落在括号旁时给两端加底色，先整体还原底色防残色
            try
            {
                doc.GetRange(0, text.Length).CharacterFormat.BackgroundColor = Microsoft.UI.Colors.Transparent;
                if (SqlBracketMatcher.Match(text, caret) is { } pair)
                {
                    doc.GetRange(pair.Open, pair.Open + 1).CharacterFormat.BackgroundColor = Ant.Color("FFF1B8");
                    doc.GetRange(pair.Close, pair.Close + 1).CharacterFormat.BackgroundColor = Ant.Color("FFF1B8");
                }
            }
            catch { /* 配对高亮失败不影响编辑 */ }

            doc.Selection?.SetRange(caret, caret);
        }
        catch { /* 高亮失败不影响编辑 */ }
        finally { _applying = false; }
    }

    // ---------- SQL 补全（㉔ 设计稿冻结 2026-11-25：输入即弹＋Ctrl+Space 兜底，候选三层，别名感知） ----------

    /// <summary>表名候选懒加载：一次取当前库子对象清单后缓存（融合远端 D-12 能力）；60 秒过期重取（设计稿口径）。</summary>
    private void EnsureTables()
    {
        if (_tablesLoading) return;
        if (_tables.Count > 0 && DateTimeOffset.UtcNow - _tablesAt < TimeSpan.FromSeconds(SqlComplete.MetaCacheSeconds)) return;
        _tablesLoading = true;
        _ = EnsureTablesAsync();
    }

    private async Task EnsureTablesAsync()
    {
        try { var kids = await _provider.GetChildren(_db); _tables = kids.ToList(); _tablesAt = DateTimeOffset.UtcNow; }
        catch { /* 元数据取不到就只用关键字＋列 */ }
        finally { _tablesLoading = false; }
    }

    private static readonly string[] Keywords =
    {
        "SELECT", "FROM", "WHERE", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE",
        "CREATE", "TABLE", "DROP", "ALTER", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER",
        "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET", "AND", "OR", "NOT", "NULL",
        "IS", "IN", "AS", "DISTINCT", "COUNT", "SUM", "AVG", "MIN", "MAX", "LIKE",
        "BETWEEN", "EXISTS", "UNION", "ALL", "DESC", "ASC", "PRIMARY", "KEY", "INDEX",
    };

    private Popup? _completePopup;
    private ListView? _completeList;
    private int _completeStart;
    private List<string> _completeAll = new();
    private DateTimeOffset _tablesAt = DateTimeOffset.MinValue;   // ㉔：表名缓存水位（60 秒过期）
    private readonly Dictionary<string, (List<string> Cols, DateTimeOffset At)> _colCache = new();   // ㉔：别名/表名字段缓存
    private readonly HashSet<string> _colLoading = new();
    private bool _inserting;   // ㉔：补全插入中，挡住自动弹出（插入走 GetRange 定点编辑，保撤销）
    // 格7：流式导出进度与取消
    private TextBlock? _exportProgressText;
    private CancellationTokenSource? _exportCts;
    // 格3：导入取消
    private CancellationTokenSource? _importCts;

    private void ShowComplete()
    {
        int caret = _editor.Document?.Selection?.StartPosition ?? 0;
        var candidates = ComputeCandidates(EditorText, caret, out int start, out string word);
        if (candidates is null || candidates.Count == 0) { CloseCompletePopup(); return; }
        _completeStart = start;
        _completeAll = candidates;
        OpenCompletePopup();
        AppLog.Write($"[补全] 打开 候选={candidates.Count} 词={word}");
    }

    /// <summary>输入即弹（㉔）：弹层未开时，光标前是词根字符或限定点才弹；开关关、程序写文本、插入中一律不弹。</summary>
    private void OnAutoCompleteTextChanged(object? sender, RoutedEventArgs e)
    {
        if (_applying || _inserting || _mode != WorkMode.Query) return;
        if (_completePopup is { IsOpen: true }) return;   // 已开：走 OnCompleteTextChanged 重过滤
        if (!SqlCompletePrefs.Load()) return;
        int caret = _editor.Document?.Selection?.StartPosition ?? 0;
        string text = EditorText;
        if (caret < 1 || caret > text.Length) return;
        char prev = text[caret - 1];
        if (!(char.IsLetterOrDigit(prev) || prev == '_' || prev == '.')) return;
        ShowComplete();
    }

    /// <summary>候选三层（㉔ 设计稿）：限定词 x. → 该表字段（60 秒缓存，未命中异步装载）；否则关键字＋表名＋当前列。</summary>
    private List<string>? ComputeCandidates(string text, int caret, out int start, out string word)
    {
        start = caret;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        word = text[start..caret];

        string? qualTable = SqlComplete.QualifierTable(text, caret);
        if (qualTable is not null)
        {
            var cols = CachedColumns(qualTable);
            return cols is null ? null : SqlComplete.FilterQualified(cols, word);   // null＝装载中，本轮不出候选（发现66：空词根回全量字段上限8）
        }
        if (word.Length == 0) return new List<string>();   // 无词根无限定词不弹（避免全量噪音）
        EnsureTables();
        return SqlComplete.Filter(Keywords.Concat(_tables).Concat(_cols.Select(c => c.Name)), word);
    }

    /// <summary>字段缓存命中返回清单；未命中触发异步装载（60 秒过期，失败静默降级空集）。</summary>
    private List<string>? CachedColumns(string table)
    {
        if (_colCache.TryGetValue(table, out var hit) &&
            DateTimeOffset.UtcNow - hit.At < TimeSpan.FromSeconds(SqlComplete.MetaCacheSeconds))
            return hit.Cols;
        if (_colLoading.Add(table))
        {
            _ = LoadColumnsAsync(table);
            AppLog.Write($"[补全] 字段装载中 表={table}");
        }
        return null;
    }

    private async Task LoadColumnsAsync(string table)
    {
        try
        {
            var cols = await _provider.GetColumns(_db, table);
            _colCache[table] = (cols.Select(c => c.Name).ToList(), DateTimeOffset.UtcNow);
            // 装载完成：浮层开着就重刷候选；没开且光标仍停在限定点后，就地补弹
            Safe.Run(() =>
            {
                if (_completePopup is { IsOpen: true }) RefreshCompleteList();
                else if (_mode == WorkMode.Query && !_inserting) ShowComplete();
            }, "补全刷新");
        }
        catch { _colCache[table] = (new List<string>(), DateTimeOffset.UtcNow); }   // 元数据失败静默降级
        finally { _ = _colLoading.Remove(table); }
    }

    private void RefreshCompleteList()
    {
        int caret = _editor.Document?.Selection?.StartPosition ?? 0;
        var candidates = ComputeCandidates(EditorText, caret, out int start, out _);
        if (candidates is null || candidates.Count == 0) { CloseCompletePopup(); return; }
        _completeStart = start;
        _completeAll = candidates;
        if (_completeList is not null) { FillCompleteItems(_completeList); _completeList.SelectedIndex = 0; }
    }

    private void OpenCompletePopup()
    {
        CloseCompletePopup();
        var lv = new ListView { SelectionMode = ListViewSelectionMode.Single, Height = 280, MaxWidth = 380 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(lv, "SqlCompleteList");
        FillCompleteItems(lv);
        lv.SelectedIndex = 0;
        lv.DoubleTapped += (_, _) => InsertComplete();
        _completeList = lv;
        var border = new Border
        {
            Child = lv,
            Background = Ant.Card,
            BorderBrush = Ant.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(border, "SqlCompletePopup");
        var popup = new Popup { Child = border, IsLightDismissEnabled = true };
        // 光标矩形定位：编辑器在窗口内的原点＋光标矩形偏移；取不到时退编辑器左上
        try
        {
            var origin = _editor.TransformToVisual(_owner.Content).TransformPoint(new Windows.Foundation.Point(0, 0));
            _editor.Document!.Selection!.GetRect(Microsoft.UI.Text.PointOptions.None, out var rect, out _);
            popup.HorizontalOffset = origin.X + rect.X;
            popup.VerticalOffset = origin.Y + rect.Y + rect.Height + 4;
        }
        catch
        {
            try
            {
                var origin = _editor.TransformToVisual(_owner.Content).TransformPoint(new Windows.Foundation.Point(0, 0));
                popup.HorizontalOffset = origin.X + 40;
                popup.VerticalOffset = origin.Y + 40;
            }
            catch { popup.HorizontalOffset = 80; popup.VerticalOffset = 120; }
        }
        popup.Closed += (_, _) => { if (ReferenceEquals(_completePopup, popup)) { _completePopup = null; _completeList = null; } };
        _completePopup = popup;
        popup.IsOpen = true;
        _editor.KeyDown -= OnCompleteKeyDown;
        _editor.KeyDown += OnCompleteKeyDown;
        _editor.TextChanged -= OnCompleteTextChanged;
        _editor.TextChanged += OnCompleteTextChanged;
    }

    private void FillCompleteItems(ListView lv)
    {
        lv.Items.Clear();
        foreach (var k in _completeAll)
            lv.Items.Add(new TextBlock { Text = k, FontFamily = (FontFamily)Application.Current.Resources["AntMono"] });
    }

    private void OnCompleteKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_completePopup is null || !_completePopup.IsOpen || _completeList is null) return;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Down:
                if (_completeList.SelectedIndex < _completeList.Items.Count - 1) _completeList.SelectedIndex++;
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Up:
                if (_completeList.SelectedIndex > 0) _completeList.SelectedIndex--;
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Enter:
            case Windows.System.VirtualKey.Tab:
                InsertComplete();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape:
                CloseCompletePopup();
                e.Handled = true;
                break;
        }
    }

    private void OnCompleteTextChanged(object? sender, RoutedEventArgs e)
    {
        // 继续输入重过滤；插入路径由 _applying/_inserting 挡住（高亮定时器同款口径）
        if (_completePopup is null || !_completePopup.IsOpen || _applying || _inserting) return;
        RefreshCompleteList();
    }

    private void InsertComplete()
    {
        if (_completeList is null || _completeList.SelectedIndex < 0) { CloseCompletePopup(); return; }
        string pick = _completeAll[_completeList.SelectedIndex];
        // ㉔：特殊字符表/字段自动 Provider.Quote；插入走 GetRange 定点替换词根，保撤销（发现 56 同口径）
        string insert = SqlComplete.NeedsQuote(pick) ? _provider.Quote(pick) : pick;
        var doc = _editor.Document;
        if (doc is null) { CloseCompletePopup(); return; }
        int caret = doc.Selection?.StartPosition ?? 0;
        int start = Math.Min(_completeStart, caret);
        CloseCompletePopup();   // 先摘钩再改文本，插入不触发补全重弹
        _inserting = true;
        try
        {
            doc.GetRange(start, caret - start).Text = insert;
            doc.Selection?.SetRange(start + insert.Length, start + insert.Length);
        }
        finally { _inserting = false; }
        AppLog.Write($"[补全] 插入 {pick}");
        _editor.Focus(FocusState.Programmatic);
    }

    private void CloseCompletePopup()
    {
        if (_completePopup is not null) { var p = _completePopup; _completePopup = null; p.IsOpen = false; }
        _completeList = null;
        _editor.KeyDown -= OnCompleteKeyDown;
        _editor.TextChanged -= OnCompleteTextChanged;
    }
}
