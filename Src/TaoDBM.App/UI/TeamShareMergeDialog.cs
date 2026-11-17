using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>团队合并对话框：局域网共享条目的合并预演——本地与远端条目 JSON 双向合并（后写胜出），
/// 逐条净化预览（凭据键剔除留痕），合并结果可另存 JSON。只预演与导出，不落库。</summary>
public static class TeamShareMergeDialog
{
    private static ShareMergeResult? _result;
    private static List<string> _droppedAll = new();

    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        _result = null;
        _droppedAll = new List<string>();
        var localBox = new TextBox
        {
            PlaceholderText = "粘贴本地条目 JSON（ShareItem 数组）", AcceptsReturn = true, Height = 110,
            TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"),
        };
        var remoteBox = new TextBox
        {
            PlaceholderText = "粘贴远端条目 JSON（ShareItem 数组）", AcceptsReturn = true, Height = 110,
            TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"),
        };
        var report = new StackPanel { Spacing = 8 };
        var status = new TextBlock { FontSize = 12, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var mergeBtn = new Button { Content = "预览合并", CornerRadius = new CornerRadius(4) };
        var saveBtn = new Button { Content = "另存合并结果", CornerRadius = new CornerRadius(4), IsEnabled = false };

        mergeBtn.Click += async (_, _) =>
        {
            report.Children.Clear();
            _droppedAll = new List<string>();
            saveBtn.IsEnabled = false;
            var local = TeamShareMerge.FromJson(localBox.Text ?? "");
            if (local is null) { status.Text = "本地条目 JSON 无法解析（应为 ShareItem 数组）。"; return; }
            var remote = TeamShareMerge.FromJson(remoteBox.Text ?? "");
            if (remote is null) { status.Text = "远端条目 JSON 无法解析（应为 ShareItem 数组）。"; return; }

            var merged = TeamShareMerge.Merge(local, remote);
            _result = merged;
            saveBtn.IsEnabled = true;
            AddSection(report, $"合并完成：新增 {merged.Added}，更新 {merged.Updated}，收入墓碑 {merged.Tombstones}，共 {merged.Merged.Count} 条");
            foreach (var item in merged.Merged)
            {
                var (clean, dropped) = TeamShareMerge.Sanitize(item);
                _droppedAll.AddRange(dropped);
                var kind = item.Deleted ? "墓碑" : item.Kind;
                AddLine(report, $"{item.ItemId} [{kind}] 更新于 {item.UpdatedAt:yyyy-MM-dd HH:mm} 作者 {item.Author}", null, 1);
                if (dropped.Count > 0)
                    AddLine(report, $"  净化：剔除凭据键 {string.Join(", ", dropped)}（不入共享包）", Ant.Warn, 2);
            }
            status.Text = _droppedAll.Count > 0
                ? $"合并预演就绪：{_droppedAll.Distinct().Count()} 种凭据键已被剔除，合并结果可另存 JSON。"
                : "合并预演就绪：未发现凭据键，合并结果可另存 JSON。";
            await Task.CompletedTask;
        };

        saveBtn.Click += async (_, _) =>
        {
            if (_result is null) { status.Text = "请先预览合并。"; return; }
            try
            {
                var path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TaoDBM", "teamshare", $"merge-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                await System.IO.File.WriteAllTextAsync(path, TeamShareMerge.ToJson(_result.Merged));
                status.Text = $"已保存：{path}";
            }
            catch (Exception ex) { status.Text = "保存失败：" + ex.Message; }
            await Task.CompletedTask;
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(mergeBtn); row.Children.Add(saveBtn);

        var panel = new StackPanel { Spacing = 12, MinWidth = 680 };
        panel.Children.Add(new TextBlock
        {
            Text = "团队合并预演：同一 ItemId 后写胜出，本地没有的远端条目（含墓碑）收入；凭据键（password/token 等）入包前自动剔除并留痕。只预演，不落库。",
            TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub, FontSize = 12,
        });
        panel.Children.Add(localBox);
        panel.Children.Add(remoteBox);
        panel.Children.Add(row);
        panel.Children.Add(new ScrollViewer { Content = report, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "团队合并",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
        await Task.CompletedTask;
    }

    private static void AddLine(StackPanel p, string text, Brush? brush = null, int indent = 0)
    {
        p.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12, Margin = new Thickness(indent * 16, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap, Foreground = brush ?? Ant.TextSub,
            FontFamily = new FontFamily("Consolas"),
        });
    }

    private static void AddSection(StackPanel p, string title)
    {
        p.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ant.Primary, Margin = new Thickness(0, 6, 0, 0),
        });
    }
}
