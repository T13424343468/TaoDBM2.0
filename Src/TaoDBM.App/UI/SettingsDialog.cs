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
        }
    }
}
