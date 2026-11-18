using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.ViewModels;

namespace TaoDBM.UI;

/// <summary>设置弹窗（MVVM 模板）：视图只负责搭建控件并绑定 ViewModel，选项文案与保存逻辑全部在 ViewModel。</summary>
public static class SettingsDialog
{
    public static async Task ShowAsync(Window owner)
    {
        var vm = new SettingsViewModel(
            Core.AppPrefs.Load(),
            v =>
            {
                try { Core.AppPrefs.Save(v); }
                catch (Exception ex) { Core.AppLog.Write($"设置保存失败: {ex.Message}"); }
            });

        var panel = new StackPanel { Spacing = 8, MinWidth = 400 };
        panel.Children.Add(new TextBlock { Text = "关闭主窗口时", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        var desc = new TextBlock { Foreground = Ant.TextSub, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
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
                    new TextBlock { Text = SettingsViewModel.DescriptionFor(kind), Foreground = Ant.TextSub, FontSize = 12 },
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
        var tpStart = new TimePicker { Time = new TimeSpan(notify.DndStart.Hour, notify.DndStart.Minute, 0), MinuteIncrement = 30 };
        var tpEnd = new TimePicker { Time = new TimeSpan(notify.DndEnd.Hour, notify.DndEnd.Minute, 0), MinuteIncrement = 30 };
        var dndRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(20, 0, 0, 0) };
        dndRow.Children.Add(chkDnd);
        dndRow.Children.Add(tpStart);
        dndRow.Children.Add(new TextBlock { Text = "到", VerticalAlignment = VerticalAlignment.Center, Foreground = Ant.TextSub });
        dndRow.Children.Add(tpEnd);
        var throttleBox = new TextBox { Header = "同类事件节流（分钟，0 不节流）", Text = notify.ThrottleMinutes.ToString("0.#"), MinWidth = 240 };
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
        panel.Children.Add(new TextBlock { Text = "推送哪些事件：", Foreground = Ant.TextSub, FontSize = 12 });
        panel.Children.Add(kindPanel);

        void SaveNotify()
        {
            var raw = throttleBox.Text.Trim();
            if (!double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var throttle) &&
                !double.TryParse(raw, out throttle))
                throttle = 5;
            if (throttle < 0) throttle = 0;
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

        panel.Children.Add(new TextBlock
        {
            Text = "设置保存即生效。主题、连接、收藏等沿用各自入口。",
            Foreground = Ant.TextSub, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });

        var dlg = new ContentDialog
        {
            Title = "设置",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = owner.Content.XamlRoot,
        };

        if (await dlg.ShowThemedAsync() == ContentDialogResult.Primary)
        {
            vm.SaveCommand.Execute(null);
            SaveNotify();
        }
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
