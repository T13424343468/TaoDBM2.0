using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.Text;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>一键分布式部署向导（设计稿 §19）：铺部署五步 账号→从库→播种→探活→上线。
/// MySQL 先行（P-2：能翻的翻、翻不了的报）；SSH/DB 凭据仅授权会话使用、不落盘明文；
/// 每步失败即停并给可执行排查项，不静默越权。
/// P0 整改 06（2026-11-22）：①导航按钮每步新建，来回切换不再重复挂事件（修「点一次跳多步」）；
/// ②步骤顺序改为 账号→配置从库（填信息＋验连，不启动复制）→备份播种→探活（启动复制＋读状态）→上线，
/// 从库信息先于播种填写，消除顺序错乱；③写主库/写从库的动作执行前统一走草稿预览确认，与全局安全口径一致。</summary>
public static class DeploymentWizardDialog
{
    private static readonly string[] StepNames = { "复制账号", "配置从库", "备份播种", "探活", "上线" };

    public static FrameworkElement BuildPanel(MainWindow owner, NodeInfo info)
    {
        var primary = info.Provider ?? ProviderFactory.Create(info.Profile);
        if (primary.Profile.Engine != DbEngine.MySQL)
        {
            return new TextBlock
            {
                Text = "部署向导：分布式部署向导当前支持 MySQL 主从（P-2 口径：能翻的翻、翻不了的报）。" +
                    "其他引擎的复制拓扑请用「主从管理面」查看。",
                FontSize = AntTokens.FontMinor,
                Foreground = Ant.TextSub,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16),
            };
        }

        bool[] done = new bool[5];
        var stepBar = new TextBlock { FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 10, MinWidth = 620 };

        void UpdateStepBar()
        {
            var parts = new List<string>();
            for (int i = 0; i < 5; i++)
                parts.Add($"{"①②③④⑤"[i]} {StepNames[i]}{(done[i] ? " ✔" : "")}");
            stepBar.Text = string.Join(" → ", parts);
        }

        void Fail(string step, Exception ex) =>
            status.Text = HumanError.Say(ex, $"【{step}】执行") + "\n排查项：确认账号有 REPLICATION SLAVE/CLIENT 权限；确认主从网络互相可达（ping/端口）；确认 mysqldump 在 PATH；密码含特殊字符时检查转义。";

        // 草稿预览确认（P0 整改 06）：写库动作执行前统一过一遍，与全局安全口径一致
        static async Task<bool> ConfirmDraft(Window owner, string title, string detail)
        {
            var confirm = new ContentDialog
            {
                Title = $"草稿预览 · {title}",
                Content = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = AntTokens.FontMinor },
                PrimaryButtonText = "确认执行",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = owner.Content.XamlRoot,
            };
            return await confirm.ShowThemedAsync() == ContentDialogResult.Primary;
        }

        // ---- 从库连接（步骤②③④共用；凭据仅会话内使用，不落盘） ----
        var srcDb = new TextBox { Header = "要播种的库（主库）", Text = primary.Profile.Database };
        var repHost = new TextBox { Header = "从库主机" };
        var repPort = new TextBox { Header = "从库端口", Text = "3306" };
        var repUser = new TextBox { Header = "从库用户（需 REPLICATION_SLAVE 权限或 SUPER）", Text = "root" };
        var repPwd = new PasswordBox { Header = "从库密码（仅本会话使用，不落盘）" };
        ConnectionProfile? ReplicaProfile()
        {
            if (repHost.Text.Trim().Length == 0) return null;
            int.TryParse(repPort.Text, out int port);
            return new ConnectionProfile
            {
                Engine = DbEngine.MySQL,
                Host = repHost.Text.Trim(),
                Port = port == 0 ? 3306 : port,
                User = repUser.Text.Trim(),
                Password = repPwd.Password,
                Database = srcDb.Text.Trim(),
            };
        }

        // ---- 步骤 ①：复制账号 ----
        var replUser = new TextBox { Header = "复制账号名", Text = "taorepl" };
        var replPwd = new PasswordBox { Header = "复制账号密码（仅本会话使用，不落盘）" };
        var step1 = new StackPanel { Spacing = 8 };
        step1.Children.Add(replUser);
        step1.Children.Add(replPwd);
        var btn1 = new Button { Content = "在主库创建复制账号", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        btn1.Click += async (_, _) =>
        {
            try
            {
                string u = replUser.Text.Trim();
                if (u.Length == 0 || replPwd.Password.Length == 0) { status.Text = "请填写账号名与密码。"; return; }
                string detail = $"在主库 {primary.Profile.Host}:{primary.Profile.Port} 执行：\nCREATE USER IF NOT EXISTS '{u}'@'%';\nGRANT REPLICATION SLAVE, REPLICATION CLIENT ON *.* TO '{u}'@'%';\nFLUSH PRIVILEGES;";
                if (!await ConfirmDraft(owner, "复制账号", detail)) { status.Text = "已取消，未做任何改动。"; return; }
                string pwd = replPwd.Password.Replace("'", "''");
                await primary.Execute(info.Db.Length > 0 ? info.Db : primary.Profile.Database,
                    $"CREATE USER IF NOT EXISTS '{u}'@'%' IDENTIFIED BY '{pwd}'; GRANT REPLICATION SLAVE, REPLICATION CLIENT ON *.* TO '{u}'@'%'; FLUSH PRIVILEGES;");
                AppLog.Write($"[部署向导] 步骤①复制账号已创建 user={u}（密码不落盘）");
                done[0] = true; UpdateStepBar(); status.Text = "复制账号已创建。";
            }
            catch (Exception ex) { Fail("复制账号", ex); }
        };
        step1.Children.Add(btn1);

        // ---- 步骤 ②：配置从库（先填信息＋验连，不启动复制；启动挪到步骤④探活） ----
        var step2 = new StackPanel { Spacing = 8 };
        step2.Children.Add(repHost);
        step2.Children.Add(repPort);
        step2.Children.Add(repUser);
        step2.Children.Add(repPwd);
        var btn2 = new Button { Content = "验证从库连接", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        btn2.Click += async (_, _) =>
        {
            try
            {
                var rep = ReplicaProfile();
                if (rep is null) { status.Text = "请填写从库连接信息。"; return; }
                var prov = ProviderFactory.Create(rep);
                await prov.Execute(rep.Database, "SELECT 1");
                AppLog.Write($"[部署向导] 步骤②从库连接验证通过 replica={rep.Host}:{rep.Port}");
                done[1] = true; UpdateStepBar(); status.Text = "从库连接验证通过。";
            }
            catch (Exception ex) { Fail("配置从库", ex); }
        };
        step2.Children.Add(btn2);

        // ---- 步骤 ③：备份播种（从库信息已在步骤②填写） ----
        var dumpFile = new TextBox { Header = "备份文件（本机路径）", Text = Path.Combine(Path.GetTempPath(), "taodbm-seed.sql") };
        var step3 = new StackPanel { Spacing = 8 };
        step3.Children.Add(srcDb);
        step3.Children.Add(dumpFile);
        var btn3 = new Button { Content = "执行播种（主库导出 → 从库导入）", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        btn3.Click += async (_, _) =>
        {
            try
            {
                var rep = ReplicaProfile();
                if (rep is null) { status.Text = "请先在步骤②填写从库连接信息。"; return; }
                string detail = $"将主库 {primary.Profile.Host}:{primary.Profile.Port} 的「{srcDb.Text}」导出到 {dumpFile.Text}，" +
                    $"再导入从库 {rep.Host}:{rep.Port} 的同名库（覆盖式恢复）。";
                if (!await ConfirmDraft(owner, "备份播种", detail)) { status.Text = "已取消，未做任何改动。"; return; }
                var p = primary.Profile;
                var dump = BackupTools.MysqlDump(p.Host, p.Port, p.User, p.Password, srcDb.Text, dumpFile.Text);
                var (code, output) = await RunTool(dump);
                if (code != 0) { status.Text = $"【备份播种】主库导出失败（exit {code}），已停止。\n排查项：确认 mysqldump 在 PATH；确认账号有 SELECT/LOCK TABLES 权限。\n{output}"; return; }
                var restore = BackupTools.MysqlRestore(rep.Host, rep.Port, rep.User, rep.Password, srcDb.Text, dumpFile.Text);
                (code, output) = await RunTool(restore);
                if (code != 0) { status.Text = $"【备份播种】从库导入失败（exit {code}），已停止。\n排查项：确认从库可连、目标库已建、mysql 客户端在 PATH。\n{output}"; return; }
                AppLog.Write($"[部署向导] 步骤③播种完成 db={srcDb.Text} file={dumpFile.Text}");
                done[2] = true; UpdateStepBar(); status.Text = "播种完成。";
            }
            catch (Exception ex) { Fail("备份播种", ex); }
        };
        step3.Children.Add(btn3);

        // ---- 步骤 ④：探活（启动复制＋读复制状态） ----
        var probeText = new TextBlock { FontSize = AntTokens.FontMinor, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap };
        var step4 = new StackPanel { Spacing = 8 };
        var btn4 = new Button { Content = "启动复制并探活（读从库复制状态）", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        btn4.Click += async (_, _) =>
        {
            try
            {
                var rep = ReplicaProfile();
                if (rep is null) { status.Text = "请填写从库连接信息。"; return; }
                string u = replUser.Text.Trim().Replace("'", "''");
                string pwd = replPwd.Password.Replace("'", "''");
                string detail = $"在从库 {rep.Host}:{rep.Port} 执行：\nCHANGE REPLICATION SOURCE TO SOURCE_HOST='{primary.Profile.Host}', SOURCE_PORT={primary.Profile.Port}, SOURCE_USER='{u}', SOURCE_PASSWORD='***';\nSTART REPLICA;\n随后读取 SHOW REPLICA STATUS。";
                if (!await ConfirmDraft(owner, "启动复制", detail)) { status.Text = "已取消，未做任何改动。"; return; }
                var prov = ProviderFactory.Create(rep);
                string sql = $"CHANGE REPLICATION SOURCE TO SOURCE_HOST='{primary.Profile.Host}', SOURCE_PORT={primary.Profile.Port}, SOURCE_USER='{u}', SOURCE_PASSWORD='{pwd}'; START REPLICA;";
                await prov.Execute(rep.Database, sql);
                AppLog.Write($"[部署向导] 步骤④从库已配置并启动复制 replica={rep.Host}:{rep.Port}（凭据不落盘）");
                var r = (await prov.Execute(rep.Database, "SHOW REPLICA STATUS")).LastOrDefault();
                if (r is null || r.Rows.Count == 0) { probeText.Text = "✗ 未读到复制状态：从库可能尚未 START REPLICA。"; probeText.Foreground = Ant.Error; return; }
                var cols = r.Columns;
                string io = ReplicationCatalog.Col(cols, r.Rows[0], "Slave_IO_Running") ?? "?";
                string sql2 = ReplicationCatalog.Col(cols, r.Rows[0], "Slave_SQL_Running") ?? "?";
                string lag = ReplicationCatalog.Col(cols, r.Rows[0], "Seconds_Behind_Source", "Seconds_Behind_Master") ?? "?";
                bool ok = io == "Yes" && sql2 == "Yes";
                probeText.Text = $"Slave_IO: {io} {(io == "Yes" ? "✓" : "✗")}   Slave_SQL: {sql2} {(sql2 == "Yes" ? "✓" : "✗")}   延迟: {lag}s";
                probeText.Foreground = ok ? Ant.Success : Ant.Error;
                if (ok) { done[3] = true; UpdateStepBar(); }
            }
            catch (Exception ex) { Fail("探活", ex); }
        };
        step4.Children.Add(btn4);
        step4.Children.Add(probeText);

        // ---- 步骤 ⑤：上线 ----
        var step5 = new StackPanel { Spacing = 8 };
        step5.Children.Add(new TextBlock { Text = "上线前请确认：探活全绿、业务已切换读写。上线动作只做确认留痕，不改动数据。", TextWrapping = TextWrapping.Wrap, FontSize = AntTokens.FontMinor });
        var btn5 = new Button { Content = "确认上线（留痕）", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        btn5.Click += (_, _) =>
        {
            if (!done[3]) { status.Text = "探活未全绿，不能上线。请先完成步骤④。"; return; }
            AppLog.Write($"[部署上线] 主={primary.Profile.Host}:{primary.Profile.Port} 从={repHost.Text}:{repPort.Text} 五步完成");
            done[4] = true; UpdateStepBar();
            status.Text = "部署完成，已留痕。";
        };
        step5.Children.Add(btn5);

        var panels = new[] { step1, step2, step3, step4, step5 };
        void ShowStep(int i)
        {
            AppLog.Write($"[部署向导] 进入步骤{i + 1}");   // 走查 S27 实效断言锚点（单步导航）
            body.Children.Clear();
            body.Children.Add(stepBar);
            var head = new TextBlock { Text = $"步骤 {i + 1}/5 · {StepNames[i]}", FontSize = AntTokens.FontTitle, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            body.Children.Add(head);
            body.Children.Add(panels[i]);
            body.Children.Add(status);
            var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (i > 0)
            {
                var prev = new Button { Content = "上一步", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(prev, "wiz-prev");
                prev.Click += (_, _) => ShowStep(i - 1);
                nav.Children.Add(prev);
            }
            if (i < 4)
            {
                // P0 整改 06：导航按钮每步新建，不再往同一实例上重复挂事件（修「点一次跳多步」）
                var next = new Button { Content = "下一步", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(next, "wiz-next");
                next.Click += (_, _) => ShowStep(i + 1);
                nav.Children.Add(next);
            }
            body.Children.Add(nav);
        }

        UpdateStepBar();
        ShowStep(0);
        return new ScrollViewer { Content = body, MaxHeight = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static async Task<(int Code, string Output)> RunTool(ToolSpec spec)
    {
        var psi = new ProcessStartInfo(spec.FileName, spec.Arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        return (p.ExitCode, sb.ToString());
    }
}
