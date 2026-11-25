using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>危险语句草稿的公共执行口径：逐条勾选预览，确认后才执行；每次执行追加本地操作历史 permops.log（语句+时间）。</summary>
public static class DraftOps
{
    public static async Task ConfirmExecuteAsync(MainWindow owner, IDbProvider prov, string title, List<string> drafts, TextBlock status)
    {
        var checks = drafts.Select(d => new CheckBox { Content = new TextBlock { Text = d, FontFamily = new FontFamily("Consolas"), FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap }, IsChecked = true }).ToList();
        var list = new StackPanel { Spacing = 6 };
        foreach (var c in checks) list.Children.Add(c);
        var warn = new TextBlock
        {
            Text = "仅执行勾选项。执行前请确认连接账号具备对应权限；回收类草稿即授权的回滚路径。",
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.Error,
            TextWrapping = TextWrapping.Wrap,
        };
        var box = new StackPanel { Spacing = 10, MinWidth = 560 };
        box.Children.Add(warn);
        box.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var dlg = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "执行勾选项",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = owner.Content.XamlRoot,
        };
        if (await dlg.ShowThemedAsync() != ContentDialogResult.Primary) return;
        var picked = checks.Where(c => c.IsChecked == true)
            .Select(c => ((TextBlock)((CheckBox)c).Content).Text)
            .Where(s => s.Length > 0).ToList();
        if (picked.Count == 0) return;
        try
        {
            await prov.Execute("", string.Join("\n", picked));
            Safe.Run(() => File.AppendAllLines(TaoPaths.FilePath("permops.log"),
                picked.Select(s => $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{s}")), "权限操作历史");
            status.Text = $"已执行 {picked.Count} 条语句。";
        }
        catch (Exception ex)
        {
            status.Text = HumanError.Say(ex, "执行失败");
        }
    }
}
