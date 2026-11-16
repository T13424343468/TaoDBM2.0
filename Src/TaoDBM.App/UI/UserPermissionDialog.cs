using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>B3 权限接线对话框：用户列表 + 风险发现 + 权限矩阵 + 对等回收草稿。
/// 数据安全口径：所有授权/回收仅生成语句草稿，弹窗预览逐条勾选确认后才执行；
/// 口令不经界面不落盘；每次执行追加本地操作历史（语句+时间）。</summary>
public static class UserPermissionDialog
{
    private sealed record AccountGrants(string Label, List<GrantRecord> Records, List<PermissionFinding> Findings);

    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        var profile = info.Profile;
        var prov = info.Provider ?? ProviderFactory.Create(profile);
        if (!UserPermissionCatalog.Supported(profile.Engine))
        {
            await new MessageDialogHelper(owner).Show("权限审计", UserPermissionCatalog.UnsupportedReason(profile.Engine));
            return;
        }

        var accountBox = new ComboBox { MinWidth = 260, PlaceholderText = "加载中…" };
        var matrix = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        var riskBox = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Ant.Error };
        var revokeBtn = new Button
        {
            Content = "生成回收草稿",
            CornerRadius = new CornerRadius(4),
            IsEnabled = false,
        };
        var createUserBtn = new Button { Content = "新建用户（草稿）", CornerRadius = new CornerRadius(4) };
        var status = new TextBlock { FontSize = 12, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

        List<(string User, string Host)> accounts = new();
        AccountGrants? current = null;

        async Task LoadAccountsAsync()
        {
            accountBox.Items.Clear();
            accountBox.Items.Add("加载中…");
            accountBox.SelectedIndex = 0;
            accountBox.IsEnabled = false;
            status.Text = "正在读取账号清单…";
            try
            {
                var rs = await prov.Execute("", UserPermissionCatalog.ListAccountsSql(profile.Engine));
                accounts = UserPermissionCatalog.ExtractAccounts(rs.Count > 0 ? rs[0].Rows : new List<object?[]>());
                accountBox.Items.Clear();
                foreach (var a in accounts) accountBox.Items.Add(UserPermissionCatalog.AccountLabel(profile.Engine, a.User, a.Host));
                status.Text = accounts.Count == 0 ? "未读到账号（需要相应服务器级读权限）。" : $"共 {accounts.Count} 个账号。";
                if (accounts.Count > 0) { accountBox.IsEnabled = true; accountBox.SelectedIndex = 0; }
                else accountBox.SelectedIndex = -1;
            }
            catch (Exception ex)
            {
                accountBox.Items.Clear();
                accountBox.SelectedIndex = -1;
                accountBox.IsEnabled = false;
                status.Text = "账号清单读取失败：" + ex.Message;
            }
        }

        async Task<AccountGrants?> LoadGrantsAsync(string user, string host, string label)
        {
            try
            {
                status.Text = $"正在读取 {label} 的授权…";
                var rs = await prov.Execute("", UserPermissionCatalog.GrantsSql(profile.Engine, user, host));
                var rows = rs.Count > 0 ? rs[0].Rows : new List<object?[]>();
                var texts = profile.Engine == DbEngine.MySQL
                    ? UserPermissionCatalog.ExtractGrantTexts(rows)
                    : rows.Select(r => r.LastOrDefault(v => v is not null)?.ToString() ?? "")
                          .Where(s => s.StartsWith("GRANT", StringComparison.OrdinalIgnoreCase))
                          .ToList();
                var recs = PermissionAuditor.Parse(texts)
                    .Where(r => profile.Engine != DbEngine.MySQL
                                || r.Grantee.Equals(label, StringComparison.OrdinalIgnoreCase)
                                || r.Grantee.Replace("`", "").Equals(label.Replace("'", ""), StringComparison.OrdinalIgnoreCase)).ToList();
                var finds = PermissionAuditor.Audit(recs);
                status.Text = $"{label}：{recs.Count} 条授权，{finds.Count} 项风险。";
                return new AccountGrants(label, recs, finds);
            }
            catch (Exception ex)
            {
                status.Text = $"授权读取失败（{label}）：" + ex.Message;
                return null;
            }
        }

        void Render()
        {
            if (current is null) { matrix.Text = ""; riskBox.Text = ""; revokeBtn.IsEnabled = false; return; }
            matrix.Text = string.Join("\n", current.Records.Select(r =>
                $"{r.Object,-28} {string.Join(", ", r.Privileges)}{(r.WithGrantOption ? "   ⚠ 可转授" : "")}"));
            riskBox.Text = current.Findings.Count == 0
                ? ""
                : string.Join("\n", current.Findings.Select(f => $"{(f.Severity == "danger" ? "⚠" : "△")} {f.Code} {f.Message}"));
            revokeBtn.IsEnabled = current.Records.Count > 0;
        }

        accountBox.SelectionChanged += async (_, _) =>
        {
            if (accountBox.SelectedIndex < 0 || accountBox.SelectedIndex >= accounts.Count) return;
            var (u, h) = accounts[accountBox.SelectedIndex];
            var label = accountBox.SelectedItem?.ToString() ?? "";
            current = await LoadGrantsAsync(u, h, label);
            Render();
        };

        revokeBtn.Click += async (_, _) =>
        {
            if (current is null || current.Records.Count == 0) return;
            var drafts = current.Records.SelectMany(PermissionAuditor.RevokeSql).Distinct().ToList();
            await ConfirmExecuteAsync(owner, prov, $"回收 {current.Label} 的权限", drafts, status);
        };

        createUserBtn.Click += async (_, _) =>
        {
            await NewUserDraftAsync(owner, profile.Engine, prov, status);
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 460 };
        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        topRow.Children.Add(accountBox);
        topRow.Children.Add(revokeBtn);
        topRow.Children.Add(createUserBtn);
        panel.Children.Add(topRow);
        panel.Children.Add(new TextBlock { Text = "权限矩阵", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new ScrollViewer { Content = matrix, MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(riskBox);
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "用户与权限审计",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        var loadTask = LoadAccountsAsync();
        _ = dlg.ShowThemedAsync();
        await loadTask;
    }

    /// <summary>草稿预览：逐条勾选，确认后执行并写本地操作历史。</summary>
    private static async Task ConfirmExecuteAsync(MainWindow owner, IDbProvider prov, string title, List<string> drafts, TextBlock status)
    {
        var checks = drafts.Select(d => new CheckBox { Content = new TextBlock { Text = d, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap }, IsChecked = true }).ToList();
        var list = new StackPanel { Spacing = 6 };
        foreach (var c in checks) list.Children.Add(c);
        var warn = new TextBlock
        {
            Text = "仅执行勾选项。回收是授权的回滚路径；执行前请确认连接账号具备对应权限。",
            FontSize = 12,
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
            status.Text = "执行失败：" + ex.Message;
        }
    }

    /// <summary>新建用户草稿：只生成 CREATE USER（不含口令，口令由数据库自身流程设置），预览确认后执行。</summary>
    private static async Task NewUserDraftAsync(MainWindow owner, DbEngine engine, IDbProvider prov, TextBlock status)
    {
        if (engine != DbEngine.MySQL)
        {
            status.Text = "新建用户当前支持 MySQL；请用数据库自带工具或 SQL 窗口完成其他引擎。";
            return;
        }
        var userBox = new TextBox { PlaceholderText = "用户名" };
        var hostBox = new TextBox { PlaceholderText = "允许来源，如 10.0.% 或 %", Text = "%" };
        var box = new StackPanel { Spacing = 10, MinWidth = 480 };
        box.Children.Add(userBox);
        box.Children.Add(hostBox);
        box.Children.Add(new TextBlock
        {
            Text = "只生成 CREATE USER 草稿；口令不由本界面设置，创建后请在数据库侧用 ALTER USER 设口令。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ant.TextSub,
        });
        var dlg = new ContentDialog
        {
            Title = "新建用户（草稿）",
            Content = box,
            PrimaryButtonText = "生成并预览",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = owner.Content.XamlRoot,
        };
        if (await dlg.ShowThemedAsync() != ContentDialogResult.Primary) return;
        var user = userBox.Text.Trim();
        var host = hostBox.Text.Trim();
        if (user.Length == 0) { status.Text = "用户名为空，未生成草稿。"; return; }
        var draft = $"CREATE USER '{user.Replace("'", "''")}'@'{host.Replace("'", "''")}';";
        await ConfirmExecuteAsync(owner, prov, "新建用户预览", new List<string> { draft }, status);
    }
}
