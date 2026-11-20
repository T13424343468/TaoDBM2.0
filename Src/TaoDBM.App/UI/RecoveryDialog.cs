using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>误删恢复对话框：快照捕获 + 拼回草稿生成（DeletedRowRecovery）。
/// 数据安全口径：捕获只读；拼回语句只生成草稿，弹窗逐条勾选确认后才执行，执行写入 permops.log。</summary>
public static class RecoveryDialog
{
    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        var profile = info.Profile;
        var prov = info.Provider ?? ProviderFactory.Create(profile);
        if (prov.IsNoSql || !prov.SupportsBrowse)
        {
            await new MessageDialogHelper(owner).Show("误删恢复", "该引擎不支持行级快照与拼回。");
            return;
        }

        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        var tableBox = new TextBox { PlaceholderText = "表名", MinWidth = 200, Text = info.Kind == NodeKind.Table ? info.Name : "" };
        var pkBox = new TextBox { PlaceholderText = "主键列（留空自动识别）", MinWidth = 200 };
        var limitBox = new TextBox { PlaceholderText = "最多捕获行数", Text = "1000", MinWidth = 90 };

        var captureBtn = new Button { Content = "捕获快照", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var pathBox = new TextBox { PlaceholderText = "快照文件路径（捕获后自动填充或手动粘贴）", MinWidth = 480 };
        var restoreBtn = new Button { Content = "生成拼回草稿", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        restoreBtn.Foreground = Ant.Error;

        var output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 200,
            FontFamily = new FontFamily("Consolas"),
            FontSize = AntTokens.FontMinor,
            Text = "捕获快照后，误删数据可用「生成拼回草稿」恢复。",
        };

        captureBtn.Click += async (_, _) => await CaptureAsync(prov, CurrentDb(info), tableBox.Text, pkBox.Text, limitBox.Text, pathBox, output, status);
        restoreBtn.Click += async (_, _) => await RestoreAsync(owner, prov, pathBox.Text, output, status);

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row1.Children.Add(tableBox);
        row1.Children.Add(pkBox);
        row1.Children.Add(limitBox);
        row1.Children.Add(captureBtn);

        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row2.Children.Add(pathBox);
        row2.Children.Add(restoreBtn);

        var panel = new StackPanel { Spacing = 10, MinWidth = 680 };
        panel.Children.Add(new TextBlock
        {
            Text = "第一步：捕获快照（只读，把当前表数据存到本机文件，危险操作前建议先留底）；第二步：误删后从快照生成拼回 INSERT 草稿，逐条勾选确认后才执行。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ant.TextSub,
            FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(row1);
        panel.Children.Add(row2);
        panel.Children.Add(output);
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "误删恢复",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        await dlg.ShowThemedAsync();
    }

    private static string CurrentDb(NodeInfo info)
    {
        if (info.Db.Length > 0) return info.Db;
        if (info.Profile.Database.Length > 0) return info.Profile.Database;
        return "";
    }

    private static async Task CaptureAsync(IDbProvider prov, string db, string table, string pk, string limitText,
        TextBox pathBox, TextBox output, TextBlock status)
    {
        table = (table ?? "").Trim();
        if (table.Length == 0) { status.Text = "请先填写表名。"; return; }
        int limit = int.TryParse(limitText?.Trim(), out var n) ? Math.Min(Math.Max(n, 1), 20000) : 1000;

        status.Text = "正在读取表数据…";
        QueryResult rs;
        try
        {
            rs = await prov.Browse(db, table, 0, limit);
        }
        catch (Exception ex)
        {
            status.Text = "读取表数据失败：" + ex.Message;
            return;
        }
        if (rs.Rows.Count == 0) { status.Text = "表为空，无需快照。"; return; }

        if (pk.Length == 0)
        {
            try
            {
                var cols = await prov.GetColumns(db, table);
                pk = cols.FirstOrDefault(c => c.IsKey)?.Name ?? cols.FirstOrDefault()?.Name ?? "id";
            }
            catch { pk = "id"; }
        }

        var dictRows = new List<IReadOnlyDictionary<string, string?>>();
        foreach (var r in rs.Rows)
        {
            var dict = new Dictionary<string, string?>();
            for (int i = 0; i < rs.Columns.Count && i < r.Length; i++)
            {
                var v = r[i];
                dict[rs.Columns[i]] = v is null ? null : v.ToString();
            }
            dictRows.Add(dict);
        }

        try
        {
            var snap = DeletedRowRecovery.Capture(table, pk, dictRows);
            var path = TaoPaths.FilePath(Path.Combine("snapshots", $"{Sanitize(table)}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"));
            DeletedRowRecovery.SaveJsonl(path, snap);
            pathBox.Text = path;
            output.Text = $"快照 {snap.Table}：{snap.RowCount} 行，主键 {snap.PkColumn}\n列：{string.Join(", ", snap.Columns)}\n文件：{path}";
            status.Text = "快照已保存到本机（只读，未修改数据库）。";
        }
        catch (Exception ex)
        {
            status.Text = "快照保存失败：" + ex.Message;
        }
    }

    private static async Task RestoreAsync(MainWindow owner, IDbProvider prov, string path, TextBox output, TextBlock status)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) { status.Text = "请先填写快照文件路径。"; return; }

        var snap = DeletedRowRecovery.LoadJsonl(path);
        if (snap is null) { status.Text = "快照文件缺失或损坏，无法读取。"; return; }
        if (snap.RowCount == 0) { status.Text = "快照里没有行，无需拼回。"; return; }

        var script = DeletedRowRecovery.ReinsertScript(snap, prov.Profile.Engine);
        var stmts = System.Text.RegularExpressions.Regex.Split(script, @"(?m)^(?=INSERT INTO )")
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (stmts.Count == 0) { status.Text = "快照里没有可拼回的语句。"; return; }

        output.Text = $"快照 {snap.Table}（{snap.RowCount} 行，捕获于 {snap.CapturedUtc}）已生成 {stmts.Count} 条拼回草稿，请逐条勾选确认。";
        await DraftOps.ConfirmExecuteAsync(owner, prov, "误删恢复拼回（高危草稿）", stmts, status);
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "table" : sb.ToString();
    }
}
