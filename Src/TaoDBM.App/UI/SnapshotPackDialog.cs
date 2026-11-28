using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>快照包对话框：把查询结果打包成可校验指纹的快照包，预览表结构/行数/指纹，
/// 并可另存为 JSON 文件。快照采集是只读查询；保存由用户点按钮完成。</summary>
public static class SnapshotPackDialog
{
    private static SnapshotPack? _pending;

    public static FrameworkElement BuildPanel(MainWindow owner, NodeInfo info)
    {
        var prov = info.Provider ?? ProviderFactory.Create(info.Profile);
        var db = CurrentDb(info);
        var titleBox = new TextBox { PlaceholderText = "快照标题，例如：上线前 orders 对照", MinWidth = 320 };
        var tableBox = new TextBox { PlaceholderText = "表名（当前库）", MinWidth = 160 };
        var limitBox = new TextBox { PlaceholderText = "每表最多行数（默认 1000）", MinWidth = 160 };
        var report = new StackPanel { Spacing = 8 };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        _pending = null;

        var packBtn = new Button { Content = "采集快照", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var saveBtn = new Button { Content = "另存为 JSON", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), IsEnabled = false };
        packBtn.Click += async (_, _) =>
        {
            var table = (tableBox.Text ?? "").Trim();
            if (table.Length == 0) { status.Text = "请填写表名。"; return; }
            if (db.Length == 0) { status.Text = "当前连接没有可用库，请从库节点使用本功能。"; return; }
            int maxRows = 1000;
            if (int.TryParse((limitBox.Text ?? "").Trim(), out var n) && n > 0) maxRows = n;
            status.Text = "采集快照中（只读查询）…";
            report.Children.Clear();
            try
            {
                var rs = await prov.Execute(db, $"SELECT * FROM {prov.Quote(table)}");
                if (rs.Count == 0) { status.Text = "查询未返回结果。"; return; }
                var snap = SnapshotPackFile.FromQueryResult(table, rs[0], maxRows);
                var title = (titleBox.Text ?? "").Trim();
                var pack = SnapshotPackFile.Create(title.Length == 0 ? $"快照 {table}" : title,
                    $"{prov.EngineName}/{db}", new[] { snap });
                _pending = pack;
                saveBtn.IsEnabled = true;
                AddSection(report, $"快照 {pack.Title}");
                AddLine(report, $"来源：{pack.SourceLabel}；共 {pack.Tables.Count} 张表；指纹 {SnapshotPackFile.Fingerprint(pack)}");
                foreach (var t in pack.Tables)
                {
                    AddLine(report, $"表 {t.Name}：{t.Columns.Count} 列，已载 {t.Rows.Count} 行（全表 {t.TotalRows} 行）");
                    AddLine(report, $"  列：{string.Join(", ", t.Columns)}", null, 1);
                }
                status.Text = "快照就绪：可另存为 JSON 归档，或留作比对。";
            }
            catch (Exception ex) { status.Text = HumanError.Say(ex, "采集失败"); }
            await Task.CompletedTask;
        };
        saveBtn.Click += async (_, _) =>
        {
            if (_pending is null) { status.Text = "请先采集快照。"; return; }
            try
            {
                var json = SnapshotPackFile.ToJson(_pending);
                var path = TaoPaths.FilePathIn("snapshots", $"snapshot-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
                await System.IO.File.WriteAllTextAsync(path, json);
                status.Text = $"已保存：{path}";
                AppLog.Write($"[快照包] 打包落盘: {path}");   // 发现145：关键操作补落账
            }
            catch (Exception ex) { status.Text = HumanError.Say(ex, "保存失败"); }
            await Task.CompletedTask;
        };

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row1.Children.Add(titleBox); row1.Children.Add(tableBox); row1.Children.Add(limitBox);
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row2.Children.Add(packBtn); row2.Children.Add(saveBtn);

        var panel = new StackPanel { Spacing = 12, MinWidth = 680 };
        panel.Children.Add(new TextBlock
        {
            Text = "只读采集：把当前查询结果打成带指纹的快照包，可另存 JSON 归档或留作上线前后比对。",
            TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(row1);
        panel.Children.Add(row2);
        panel.Children.Add(new ScrollViewer { Content = report, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(status);

        return panel;
    }

    private static string CurrentDb(NodeInfo info)
    {
        if (info.Db.Length > 0) return info.Db;
        if (info.Profile.Database.Length > 0) return info.Profile.Database;
        return "";
    }

    private static void AddLine(StackPanel p, string text, Brush? brush = null, int indent = 0)
    {
        p.Children.Add(new TextBlock
        {
            Text = text, FontSize = AntTokens.FontMinor, Margin = new Thickness(indent * 16, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap, Foreground = brush ?? Ant.TextSub,
            FontFamily = new FontFamily("Consolas"),
        });
    }

    private static void AddSection(StackPanel p, string title)
    {
        p.Children.Add(new TextBlock
        {
            Text = title, FontSize = AntTokens.FontBody, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ant.Primary, Margin = new Thickness(0, 6, 0, 0),
        });
    }
}
