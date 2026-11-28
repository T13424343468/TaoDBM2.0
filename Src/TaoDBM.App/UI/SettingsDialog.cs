using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;
using TaoDBM.ViewModels;

namespace TaoDBM.UI;

/// <summary>设置弹窗（MVVM 模板）：视图只负责搭建控件并绑定 ViewModel，选项文案与保存逻辑全部在 ViewModel。</summary>
public static class SettingsDialog
{
    public static FrameworkElement BuildPanel(MainWindow owner)
    {
        var vm = new SettingsViewModel(
            Core.AppPrefs.Load(),
            v =>
            {
                try { Core.AppPrefs.Save(v); }
                catch (Exception ex) { Core.AppLog.Write($"设置保存失败: {ex.Message}"); }
            });

        var panel = new StackPanel { Spacing = 8, MinWidth = 400 };
        // 502 原型：面板标题行
        panel.Children.Add(new TextBlock { Text = "系统设置", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = AntTokens.FontBody });
        panel.Children.Add(new TextBlock { Text = "关闭主窗口时", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        var desc = new TextBlock { Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        void Bind(RadioButton rb, string title, Core.CloseBehavior kind)
        {
            rb.GroupName = "CloseBehavior";
            rb.IsChecked = vm.CloseBehavior == kind;
            rb.Margin = kind == Core.CloseBehavior.Ask ? new Thickness(0, 2, 0, 0) : default;
            rb.Content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children =
                {
                    new TextBlock { Text = title },
                    new TextBlock { Text = SettingsViewModel.DescriptionFor(kind), Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor },
                }
            };
            rb.Checked += (_, _) => vm.CloseBehavior = kind;
        }
        var rbAsk = new RadioButton();
        var rbTray = new RadioButton();
        var rbExit = new RadioButton();
        Bind(rbAsk, "每次询问", Core.CloseBehavior.Ask);
        Bind(rbTray, "最小化到托盘", Core.CloseBehavior.Tray);
        Bind(rbExit, "直接退出", Core.CloseBehavior.Exit);

        desc.Text = vm.Description;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.Description)) desc.Text = vm.Description;
        };

        panel.Children.Add(rbAsk);
        panel.Children.Add(rbTray);
        panel.Children.Add(rbExit);
        panel.Children.Add(desc);

        // P4：系统通知设置（总开关 / 免打扰时段 / 节流 / 单类开关），保存即落盘，下一次发布事件即刻生效
        var notify = Core.NotifyPrefs.Load();
        var chkNotify = new CheckBox { Content = "启用系统级通知", IsChecked = notify.Enabled };
        var chkDnd = new CheckBox { Content = "免打扰时段", IsChecked = notify.DndEnabled };
        var tpStart = new TimePicker { Time = new TimeSpan(notify.DndStart.Hour, notify.DndStart.Minute, 0), MinuteIncrement = 1 };
        var tpEnd = new TimePicker { Time = new TimeSpan(notify.DndEnd.Hour, notify.DndEnd.Minute, 0), MinuteIncrement = 1 };
        var dndRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(20, 0, 0, 0) };
        dndRow.Children.Add(chkDnd);
        dndRow.Children.Add(tpStart);
        dndRow.Children.Add(new TextBlock { Text = "到", VerticalAlignment = VerticalAlignment.Center, Foreground = Ant.TextSub });
        dndRow.Children.Add(tpEnd);
        var throttleBox = new TextBox { Header = "同类事件节流（分钟，0 不节流）", Text = notify.ThrottleMinutes.ToString("0.#"), MinWidth = 240 };
        // 发现106：节流输入非法当场提示，不再静默回默认
        var throttleHint = new TextBlock { Foreground = Ant.Error, FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var kindChecks = new Dictionary<Core.NotifyKind, CheckBox>();
        var kindPanel = new StackPanel { Spacing = 4, Margin = new Thickness(20, 0, 0, 0) };
        foreach (Core.NotifyKind kind in Enum.GetValues<Core.NotifyKind>())
        {
            var cb = new CheckBox { Content = KindLabel(kind), IsChecked = notify.IsKindEnabled(kind) };
            kindChecks[kind] = cb;
            kindPanel.Children.Add(cb);
        }
        panel.Children.Add(new TextBlock
        {
            Text = "系统通知", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0),
        });
        panel.Children.Add(chkNotify);
        panel.Children.Add(dndRow);
        panel.Children.Add(throttleBox);
        panel.Children.Add(throttleHint);
        panel.Children.Add(new TextBlock { Text = "推送哪些事件：", Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor });
        panel.Children.Add(kindPanel);

        // 发现106：节流输入校验——非法（非数字/NaN/Infinity/负数）返回人话错误，合法时回传归一值
        string? ValidateThrottle(out double throttle)
        {
            throttle = 5;
            var raw = throttleBox.Text.Trim();
            bool ok = double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out throttle) ||
                      double.TryParse(raw, out throttle);
            if (!ok || !double.IsFinite(throttle))
                return "节流分钟数要是 0 以上的数字（0 表示不节流），请改一下再保存。";
            if (throttle < 0)
                return "节流分钟数不能是负数，最小填 0（0 表示不节流）。";
            return null;
        }

        void SaveNotify()
        {
            var err = ValidateThrottle(out var throttle);
            if (err is not null) throttle = 0;   // Closing 已拦，不会走到；兜底取不节流
            throttleBox.Text = throttle.ToString("0.#");   // 实际生效值回显进输入框
            var opts = new Core.NotifyOptions
            {
                Enabled = chkNotify.IsChecked == true,
                DndEnabled = chkDnd.IsChecked == true,
                DndStart = TimeOnly.FromTimeSpan(tpStart.Time),
                DndEnd = TimeOnly.FromTimeSpan(tpEnd.Time),
                ThrottleMinutes = throttle,
                KindToggles = kindChecks.ToDictionary(kv => kv.Key, kv => kv.Value.IsChecked == true),
            };
            try { Core.NotifyPrefs.Save(opts); }
            catch (Exception ex) { Core.AppLog.Write($"通知设置保存失败: {ex.Message}"); }
        }

        // .sql 文件打开方式（需求合并稿第十六章）：仅装载脚本不执行；关闭后拖入/双击走当前窗口
        var chkSqlLatest = new CheckBox
        {
            Content = "双击 / 拖入 .sql 文件时装载到最近激活窗口",
            IsChecked = Core.SqlOpenPrefs.Load(),
            Margin = new Thickness(0, 12, 0, 0),
        };

        // ㉔ SQL 补全开关（2026-11-25 设计稿冻结）：输入即弹默认开，Ctrl+Space 兜底不受影响
        // 发现70：Load 走缓存后，设置页打开须强制重读盘，保证外部改文件可见
        Core.SqlCompletePrefs.Invalidate();
        var chkComplete = new CheckBox
        {
            Content = "SQL 编辑器输入时自动弹出补全（Ctrl+Space 兜底）",
            IsChecked = Core.SqlCompletePrefs.Load(),
            Margin = new Thickness(0, 8, 0, 0),
        };

        panel.Children.Add(chkSqlLatest);
        panel.Children.Add(chkComplete);
        panel.Children.Add(new TextBlock
        {
            Text = "设置保存即生效。主题、连接、收藏等沿用各自入口。",
            Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap,
        });

        // 发现83：设置项增长后超出弹窗高度，底部选项（补全开关等）够不着——套 ScrollViewer 兜底，超出即滚动
        var scroll = new ScrollViewer
        {
            Content = panel,
            MaxHeight = 480,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 4, 0),
        };

        // ---------- 发现152：外观页签——编辑器 / 网格字体字号（uifonts.json，损坏回默认） ----------
        var fontPrefs = Core.UiFontPrefs.Load();
        string[] fontChoices = { "Consolas", "Cascadia Code", "微软雅黑", "宋体", "Arial" };
        var editorFontBox = new ComboBox { Header = "编辑器字体", MinWidth = 200, ItemsSource = fontChoices, SelectedItem = fontPrefs.EditorFontFamily };
        var editorSizeBox = new TextBox { Header = $"编辑器字号（{Core.UiFontPrefs.MinFontSize}-{Core.UiFontPrefs.MaxFontSize}）", Text = fontPrefs.EditorFontSize.ToString(), MinWidth = 120 };
        var gridFontBox = new ComboBox { Header = "网格字体", MinWidth = 200, ItemsSource = fontChoices, SelectedItem = fontPrefs.GridFontFamily };
        var gridSizeBox = new TextBox { Header = $"网格字号（{Core.UiFontPrefs.MinFontSize}-{Core.UiFontPrefs.MaxFontSize}）", Text = fontPrefs.GridFontSize.ToString(), MinWidth = 120 };
        var fontPreview = new TextBlock
        {
            Text = "SELECT id, name FROM orders WHERE amount > 100;  -- 字体预览",
            FontFamily = new FontFamily(fontPrefs.EditorFontFamily),
            FontSize = fontPrefs.EditorFontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        editorFontBox.SelectionChanged += (_, _) => fontPreview.FontFamily = new FontFamily(editorFontBox.SelectedItem?.ToString() ?? "Consolas");   // 预览即时生效
        editorSizeBox.TextChanged += (_, _) =>
        {
            if (int.TryParse(editorSizeBox.Text?.Trim(), out var sz))
                fontPreview.FontSize = Core.UiFontPrefs.ClampFontSize(sz);
        };
        var themeHint = new TextBlock { Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var themeLink = new HyperlinkButton { Content = "打开主题选择", Margin = new Thickness(0, 0, 0, 0), Padding = new Thickness(0) };
        themeLink.Click += (_, _) => themeHint.Text = "主题切换在主窗口顶部「主题」按钮：水墨 / 天青 / 墨夜，三主题即时生效。";

        var lookPanel = new StackPanel { Spacing = 8, MinWidth = 400 };
        lookPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { editorFontBox, editorSizeBox } });
        lookPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { gridFontBox, gridSizeBox } });
        lookPanel.Children.Add(new TextBlock { Text = "改完保存后，新开的查询页签与数据网格按新字体显示。", Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap });
        lookPanel.Children.Add(fontPreview);
        lookPanel.Children.Add(themeLink);
        lookPanel.Children.Add(themeHint);
        var lookScroll = new ScrollViewer
        {
            Content = lookPanel,
            MaxHeight = 480,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 4, 0),
        };

        // ---------- 发现152：快捷键页签——只读清单＋改绑（冲突当场人话提示不落盘） ----------
        var shortcutList = new StackPanel { Spacing = 4, MinWidth = 400 };
        var shortcutStatus = new TextBlock { Foreground = Ant.Error, FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap };
        void RefreshShortcuts()
        {
            shortcutList.Children.Clear();
            foreach (var def in Core.ShortcutPrefs.Actions)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(new TextBlock { Text = def.Label, VerticalAlignment = VerticalAlignment.Center, MinWidth = 190 });
                row.Children.Add(new TextBlock { Text = Core.ShortcutPrefs.Effective(def.Key), VerticalAlignment = VerticalAlignment.Center, Foreground = Ant.Primary, MinWidth = 110 });
                var rebindBtn = new Button { Content = "改绑", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 2, 10, 2), FontSize = AntTokens.FontMinor };
                var actionKey = def.Key;
                rebindBtn.Click += async (_, _) => await RebindShortcutAsync(owner, actionKey, shortcutStatus, RefreshShortcuts);
                row.Children.Add(rebindBtn);
                shortcutList.Children.Add(row);
            }
            shortcutList.Children.Add(new TextBlock
            {
                Text = "改绑即生效；改回出厂绑定即清除覆盖。编辑器内置四键（执行 / 补全 / 保存）随编辑器同口径生效。",
                Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
        RefreshShortcuts();
        var shortcutScroll = new ScrollViewer
        {
            Content = shortcutList,
            MaxHeight = 480,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 4, 0),
        };

        var pivot = new Pivot { MinWidth = 460 };
        pivot.Items.Add(new PivotItem { Header = "通用", Content = scroll });
        pivot.Items.Add(new PivotItem { Header = "外观", Content = lookScroll });
        pivot.Items.Add(new PivotItem { Header = "快捷键", Content = shortcutScroll });

        // 页签化改造：ContentDialog 的「保存/取消」改面板内保存钮——校验不过就地提示不落盘
        var saveBtn = new Button
        {
            Content = "保存",
            Background = Ant.Primary,
            Foreground = Ant.OnPrimary,
            BorderThickness = new Thickness(0),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
        };
        saveBtn.Click += (_, _) =>
        {
            var err = ValidateThrottle(out _);
            if (err is not null)
            {
                throttleHint.Text = err;
                throttleHint.Visibility = Visibility.Visible;
                return;
            }
            vm.SaveCommand.Execute(null);
            SaveNotify();
            Core.SqlOpenPrefs.Save(chkSqlLatest.IsChecked == true);
            Core.SqlCompletePrefs.Save(chkComplete.IsChecked == true);   // ㉔：补全开关保存即生效
            // 发现152：外观偏好保存（字号越界在 Save 内钳制；新开页签按新字体显示）
            fontPrefs.EditorFontFamily = editorFontBox.SelectedItem?.ToString() ?? "Consolas";
            fontPrefs.GridFontFamily = gridFontBox.SelectedItem?.ToString() ?? "Consolas";
            if (int.TryParse(editorSizeBox.Text?.Trim(), out var esz)) fontPrefs.EditorFontSize = esz;
            if (int.TryParse(gridSizeBox.Text?.Trim(), out var gsz)) fontPrefs.GridFontSize = gsz;
            fontPrefs.Save();
            Core.AppLog.Write($"[设置] 外观保存 editor={fontPrefs.EditorFontFamily}/{fontPrefs.EditorFontSize} grid={fontPrefs.GridFontFamily}/{fontPrefs.GridFontSize}");
        };

        var host = new StackPanel { Spacing = 8 };
        host.Children.Add(pivot);
        host.Children.Add(saveBtn);
        return host;
    }

    /// <summary>发现152：改绑对话框——按下新键组合（修饰键＋主键），冲突当场人话提示不落盘；Esc 取消。</summary>
    private static async Task RebindShortcutAsync(Window owner, string actionKey, TextBlock status, Action refresh)
    {
        var capture = new TextBox
        {
            IsReadOnly = true,
            PlaceholderText = "点这里后按下新组合，如 Ctrl+Alt+Q（Esc 取消）",
            MinWidth = 380,
        };
        var err = new TextBlock { Foreground = Ant.Error, FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 8, MinWidth = 400 };
        var def = Core.ShortcutPrefs.Def(actionKey);
        panel.Children.Add(new TextBlock
        {
            Text = $"为「{def?.Label}」按下新的键组合。当前：{Core.ShortcutPrefs.Effective(actionKey)}",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(capture);
        panel.Children.Add(err);

        var dlg = new ContentDialog
        {
            Title = "改绑快捷键",
            Content = panel,
            CloseButtonText = "取消",
            XamlRoot = owner.Content.XamlRoot,
        };
        var done = new TaskCompletionSource<bool>();
        dlg.Closed += (_, _) => done.TrySetResult(false);

        // 修饰键状态本地自记（云测 run399：InputKeyboardSource.GetKeyStateForCurrentRoot 在本项目 WinAppSDK 2.6.5 无此 API）
        var modsDown = new HashSet<Windows.System.VirtualKey>();
        capture.KeyUp += (_, e) => modsDown.Remove(e.Key);
        capture.KeyDown += (s, e) =>
        {
            e.Handled = true;
            if (e.Key == Windows.System.VirtualKey.Escape) { done.TrySetResult(false); return; }
            string key = VirtualKeyName(e.Key);
            if (key.Length == 0) { modsDown.Add(e.Key); return; }   // 只按修饰键不算完整组合，记入按下集合
            var mods = new List<string>();
            void Add(Windows.System.VirtualKey vk, string name)
            {
                if (modsDown.Contains(vk)) mods.Add(name);
            }
            Add(Windows.System.VirtualKey.Control, "Ctrl");
            Add(Windows.System.VirtualKey.Shift, "Shift");
            Add(Windows.System.VirtualKey.Menu, "Alt");
            Add(Windows.System.VirtualKey.LeftWindows, "Win");
            Add(Windows.System.VirtualKey.RightWindows, "Win");
            string combo = string.Join("+", mods.Concat(new[] { key }).Distinct());
            string conflict = Core.ShortcutPrefs.Rebind(actionKey, combo);
            if (conflict.Length > 0) { err.Text = conflict; return; }   // 冲突当场人话提示，继续捕获
            done.TrySetResult(true);
        };

        _ = dlg.ShowThemedAsync();
        bool saved = await done.Task;
        if (saved) { status.Text = ""; refresh(); }
        else if (err.Text.Length > 0) status.Text = err.Text;
    }

    /// <summary>发现152：VirtualKey → 绑定串主键名；只按修饰键返回空串。</summary>
    private static string VirtualKeyName(Windows.System.VirtualKey k)
    {
        int v = (int)k;
        if (v >= (int)Windows.System.VirtualKey.A && v <= (int)Windows.System.VirtualKey.Z)
            return ((char)('A' + v - (int)Windows.System.VirtualKey.A)).ToString();
        if (v >= (int)Windows.System.VirtualKey.Number0 && v <= (int)Windows.System.VirtualKey.Number9)
            return ((char)('0' + v - (int)Windows.System.VirtualKey.Number0)).ToString();
        if (v >= (int)Windows.System.VirtualKey.F1 && v <= (int)Windows.System.VirtualKey.F12)
            return $"F{v - (int)Windows.System.VirtualKey.F1 + 1}";
        return k switch
        {
            Windows.System.VirtualKey.Enter => "Enter",
            Windows.System.VirtualKey.Space => "Space",
            Windows.System.VirtualKey.Tab => "Tab",
            Windows.System.VirtualKey.Home => "Home",
            Windows.System.VirtualKey.End => "End",
            Windows.System.VirtualKey.PageUp => "PageUp",
            Windows.System.VirtualKey.PageDown => "PageDown",
            Windows.System.VirtualKey.Up => "Up",
            Windows.System.VirtualKey.Down => "Down",
            Windows.System.VirtualKey.Left => "Left",
            Windows.System.VirtualKey.Right => "Right",
            _ => "",
        };
    }

    /// <summary>单类事件的中文名（设置界面用）。</summary>
    private static string KindLabel(Core.NotifyKind kind) => kind switch
    {
        Core.NotifyKind.RiskySqlDone => "危险操作执行完成",
        Core.NotifyKind.Backup => "备份结果",
        Core.NotifyKind.Restore => "还原结果",
        Core.NotifyKind.Migration => "迁移 / 同步结果",
        Core.NotifyKind.LongTask => "长任务完成",
        Core.NotifyKind.HealthCheck => "健康检查异常",
        _ => kind.ToString(),
    };
}
