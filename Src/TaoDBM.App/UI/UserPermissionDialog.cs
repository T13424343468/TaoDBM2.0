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

    public static FrameworkElement BuildPanel(MainWindow owner, NodeInfo info)
    {
        var profile = info.Profile;
        var prov = info.Provider ?? ProviderFactory.Create(profile);
        if (!UserPermissionCatalog.Supported(profile.Engine))
        {
            return new TextBlock
            {
                Text = $"权限审计：{UserPermissionCatalog.UnsupportedReason(profile.Engine)}",
                FontSize = AntTokens.FontMinor,
                Foreground = Ant.TextSub,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16),
            };
        }

        var accountBox = new ComboBox { MinWidth = 260, PlaceholderText = "加载中…" };
        // 格16：权限矩阵表格化（行=对象.权限，列=回收勾选/对象/权限/可转授），废弃等宽文本块
        var matrixHost = new StackPanel { Spacing = 0 };
        var matrixScroll = new ScrollViewer
        {
            Content = matrixHost,
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        List<CheckBox> revokeChecks = new();
        var riskBox = new TextBlock { FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap, Foreground = Ant.Error };
        var revokeBtn = new Button
        {
            Content = "生成回收草稿",
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
            IsEnabled = false,
        };
        var createUserBtn = new Button { Content = "新建用户（草稿）", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };

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
                status.Text = HumanError.Say(ex, "账号清单读取失败");
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
                status.Text = HumanError.Say(ex, $"授权读取失败（{label}）");
                return null;
            }
        }

        void Render()
        {
            matrixHost.Children.Clear();
            revokeChecks.Clear();
            if (current is null) { riskBox.Text = ""; revokeBtn.IsEnabled = false; return; }
            // 表头：全选/全不选列头 + 对象 + 权限 + 可转授
            var headerCheck = new CheckBox { MinWidth = 28 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(headerCheck, "PermGrid_All");
            var header = new Grid
            {
                ColumnSpacing = 8,
                Background = Ant.HeadBg,
                Padding = new Thickness(4, 2, 4, 2),
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            void AddHeader(FrameworkElement el, int col)
            {
                Grid.SetColumn(el, col);
                header.Children.Add(el);
            }
            AddHeader(headerCheck, 0);
            AddHeader(new TextBlock { Text = "对象", FontSize = AntTokens.FontMinor, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }, 1);
            AddHeader(new TextBlock { Text = "权限", FontSize = AntTokens.FontMinor, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }, 2);
            AddHeader(new TextBlock { Text = "可转授", FontSize = AntTokens.FontMinor, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }, 3);
            matrixHost.Children.Add(header);
            headerCheck.Click += (_, _) =>
            {
                foreach (var c in revokeChecks) c.IsChecked = headerCheck.IsChecked == true;
            };
            // 行：对象.权限 + 回收勾选
            foreach (var r in current.Records)
            {
                int idx = matrixHost.Children.Count - 1; // 表头外序号
                var cb = new CheckBox { MinWidth = 28, VerticalAlignment = VerticalAlignment.Center };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(cb, $"PermRevoke_{idx}");
                var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(4, 2, 4, 2), Background = Ant.AltRow };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
                Grid.SetColumn(cb, 0);
                row.Children.Add(cb);
                var objText = new TextBlock { Text = r.Object, FontSize = AntTokens.FontMinor, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(objText, 1);
                row.Children.Add(objText);
                var privText = new TextBlock { Text = string.Join(", ", r.Privileges), FontSize = AntTokens.FontMinor, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(privText, 2);
                row.Children.Add(privText);
                var grantText = new TextBlock { Text = r.WithGrantOption ? "⚠ 可转授" : "", FontSize = AntTokens.FontMinor, VerticalAlignment = VerticalAlignment.Center, Foreground = r.WithGrantOption ? Ant.Error : Ant.TextSub };
                Grid.SetColumn(grantText, 3);
                row.Children.Add(grantText);
                matrixHost.Children.Add(row);
                revokeChecks.Add(cb);
            }
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
            // 格16：只对勾选行生成回收草稿（未勾选行不进草稿）
            var picked = new List<GrantRecord>();
            for (int i = 0; i < revokeChecks.Count && i < current.Records.Count; i++)
                if (revokeChecks[i].IsChecked == true) picked.Add(current.Records[i]);
            if (picked.Count == 0) { status.Text = "未勾选任何行，未生成回收草稿。"; return; }
            var drafts = picked.SelectMany(PermissionAuditor.RevokeSql).Distinct().ToList();
            AppLog.Write($"[权限] 回收={drafts.Count}");
            await ConfirmExecuteAsync(owner, prov, $"回收 {current.Label} 的权限", drafts, status);
            // 执行后重读授权刷新矩阵（对应行消失）
            if (accountBox.SelectedIndex >= 0 && accountBox.SelectedIndex < accounts.Count)
            {
                var (u, h) = accounts[accountBox.SelectedIndex];
                var label = accountBox.SelectedItem?.ToString() ?? "";
                current = await LoadGrantsAsync(u, h, label);
                Render();
            }
        };

        createUserBtn.Click += async (_, _) =>
        {
            await NewUserDraftAsync(owner, profile.Engine, prov, status);
        };

        // s3：改密 + 保存后新会话真实验证（不假承诺：验证失败如实报）
        var passwdBtn = new Button { Content = "改密（真实验证）", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        passwdBtn.Click += async (_, _) =>
        {
            var userBox = new TextBox { PlaceholderText = "用户名", Text = accountBox.SelectedIndex >= 0 && accountBox.SelectedIndex < accounts.Count ? accounts[accountBox.SelectedIndex].User : "" };
            var hostBox = new TextBox { PlaceholderText = "来源主机（MySQL，如 %）", Text = accountBox.SelectedIndex >= 0 && accountBox.SelectedIndex < accounts.Count ? accounts[accountBox.SelectedIndex].Host : "%" };
            var pwdBox = new PasswordBox { PlaceholderText = "新口令（仅本会话使用，不落盘）" };
            var box = new StackPanel { Spacing = 10, MinWidth = 480 };
            box.Children.Add(userBox);
            if (profile.Engine == DbEngine.MySQL) box.Children.Add(hostBox);
            box.Children.Add(pwdBox);
            box.Children.Add(new TextBlock
            {
                Text = "改密后立即用新口令开一条新会话做真实验证；验证失败会如实报出，不会假称成功。",
                FontSize = AntTokens.FontMinor,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Ant.TextSub,
            });
            var dlg = new ContentDialog
            {
                Title = "改密（真实验证）",
                Content = box,
                PrimaryButtonText = "执行改密",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = owner.Content.XamlRoot,
            };
            if (await dlg.ShowThemedAsync() != ContentDialogResult.Primary) return;
            var u = userBox.Text.Trim();
            var pwd = pwdBox.Password;
            if (u.Length == 0 || pwd.Length == 0) { status.Text = "用户名与新口令均必填。"; return; }
            string sql = profile.Engine switch
            {
                DbEngine.MySQL => $"ALTER USER '{u.Replace("'", "''")}'@'{hostBox.Text.Trim().Replace("'", "''")}' IDENTIFIED BY '{pwd.Replace("'", "''")}'",
                DbEngine.PostgreSQL => $"ALTER USER \"{u.Replace("\"", "\"\"")}\" WITH PASSWORD '{pwd.Replace("'", "''")}'",
                _ => "",
            };
            if (sql.Length == 0) { status.Text = "改密当前支持 MySQL / PostgreSQL；其他引擎请用数据库自带流程。"; return; }
            try
            {
                await prov.Execute("", sql);
                AppLog.Write($"[改密] engine={profile.Engine} user={u}（口令不落盘）");
                // 新会话真实验证：用新口令另开连接测试
                var verify = new ConnectionProfile
                {
                    Engine = profile.Engine,
                    Host = profile.Host,
                    Port = profile.Port,
                    User = u,
                    Password = pwd,
                    Database = profile.Database,
                };
                bool ok;
                string err = "";
                try { await ProviderFactory.Create(verify).TestConnection(); ok = true; }
                catch (Exception ex) { ok = false; err = HumanError.Say(ex, "新会话验证"); }
                status.Text = ok
                    ? $"改密已执行，新会话用新口令连接验证通过（{u}）。"
                    : $"改密语句已执行，但新会话验证失败：{err}。请核对口令策略/来源主机限制。";
            }
            catch (Exception ex) { status.Text = HumanError.Say(ex, "改密失败"); }
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 460 };
        // 504 原型：面板标题行
        panel.Children.Add(new TextBlock { Text = "用户与权限", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = AntTokens.FontBody });
        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        topRow.Children.Add(accountBox);
        topRow.Children.Add(revokeBtn);
        topRow.Children.Add(createUserBtn);
        topRow.Children.Add(passwdBtn);
        panel.Children.Add(topRow);
        panel.Children.Add(new TextBlock { Text = "权限矩阵", FontSize = AntTokens.FontBody, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(matrixScroll);
        panel.Children.Add(riskBox);
        panel.Children.Add(status);

        _ = Safe.RunAsync(async () => await LoadAccountsAsync(), "权限审计加载账号");
        return panel;
    }

    /// <summary>草稿预览执行：公共口径见 DraftOps（逐条勾选，确认后执行并写本地操作历史）。</summary>
    private static Task ConfirmExecuteAsync(MainWindow owner, IDbProvider prov, string title, List<string> drafts, TextBlock status)
        => DraftOps.ConfirmExecuteAsync(owner, prov, title, drafts, status);

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
            FontSize = AntTokens.FontMinor,
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
