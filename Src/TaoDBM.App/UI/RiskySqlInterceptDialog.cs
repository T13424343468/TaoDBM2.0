using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>执行链危险 SQL 拦截弹窗（设计稿 §7）：语句执行前过 RiskySqlGuard，
/// danger 级弹本窗——影响行数预估、输入库名解除拦截、改用安全模式（加 LIMIT 分批）、
/// 本会话不再拦截（二次确认+留痕）。确认后由调用方继续执行，本窗不代执行。</summary>
public static class RiskySqlInterceptDialog
{
    /// <summary>会话级豁免（engine|db|table|op，整改三批三-6 粒度细化）：勾选「本会话不再拦截」并二次确认后生效，进程内有效。</summary>
    public static readonly HashSet<string> SessionBypass = new();

    public sealed record Result(bool Proceed, string SqlToRun, bool Bypassed);

    /// <summary>执行前闸：返回 Proceed=false 表示用户取消或未解除拦截。</summary>
    public static async Task<Result> GateAsync(MainWindow owner, IDbProvider prov, string db, string sql)
    {
        var checks = RiskySqlGuard.InspectScript(sql, prov.Profile.Engine, prov.Quote);
        var worst = RiskySqlGuard.Worst(checks);
        if (worst.Level == "ok") return new Result(true, sql, false);

        // 整改三批三-6：豁免粒度 engine|db → engine|db|table|op——先算检查结论再查豁免，只放行同表同操作
        var key = $"{prov.EngineName}|{db}|{worst.Table}|{worst.Kind}";
        if (SessionBypass.Contains(key)) return new Result(true, sql, false);

        bool proceed = false;
        string runSql = sql;
        bool bypass = false;

        var head = new StackPanel { Spacing = 10, MinWidth = 460 };
        head.Children.Add(new TextBlock
        {
            Text = worst.Level == "danger" ? "⛔ 检测到高危语句" : "⚠ 建议确认后再执行",
            FontSize = AntTokens.FontTitle,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = worst.Level == "danger" ? Ant.Error : Ant.Warn,
        });
        head.Children.Add(new TextBlock { Text = worst.Detail, FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });
        head.Children.Add(new Border
        {
            Background = worst.Level == "danger" ? Ant.ErrorBg : Ant.WarnBg,
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
            Padding = new Thickness(8),
            Child = new TextBlock
            {
                Text = worst.Skeleton,
                FontFamily = new FontFamily("Consolas"),
                FontSize = AntTokens.FontMinor,
                TextWrapping = TextWrapping.Wrap,
            },
        });

        // 整改三批三-5：预估改按钮触发——打开弹窗不再自动打库，点了「预估影响行数」才跑
        var estimateText = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        if (worst.EstimateSql is not null)
        {
            var estBtn = new Button
            {
                Content = "预估影响行数",
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
                FontSize = AntTokens.FontMinor,
            };
            estBtn.Click += async (_, _) =>
            {
                estBtn.IsEnabled = false;
                estimateText.Text = "预估中…";
                try
                {
                    var rows = await prov.Execute(db, worst.EstimateSql);
                    var count = rows.Count > 0 && rows[0].Rows.Count > 0 ? rows[0].Rows[0].FirstOrDefault() : "";
                    estimateText.Text = $"影响行数预估：{count}（目标：{db}{(string.IsNullOrEmpty(worst.Table) ? "" : "." + worst.Table)}）";
                }
                catch (Exception ex)
                {
                    estimateText.Text = HumanError.Say(ex, "影响行数预估失败");
                }
                finally
                {
                    estBtn.IsEnabled = true;
                }
            };
            head.Children.Add(estBtn);
        }
        head.Children.Add(estimateText);

        var unlockBox = new TextBox { PlaceholderText = $"输入库名 {db} 解除拦截…", FontSize = AntTokens.FontMinor };
        head.Children.Add(unlockBox);

        var safeBtn = new Button
        {
            Content = "改用安全模式（加 LIMIT 分批）",
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
            FontSize = AntTokens.FontMinor,
        };
        var safeModeSql = RiskySqlGuard.SafeModeSql(sql, prov.Profile.Engine);
        safeBtn.IsEnabled = safeModeSql is not null;
        head.Children.Add(safeBtn);

        var bypassCheck = new CheckBox { Content = "本会话不再拦截（需二次确认）", FontSize = AntTokens.FontMinor };
        head.Children.Add(bypassCheck);

        var dialog = new ContentDialog
        {
            Title = "危险操作拦截",
            Content = head,
            PrimaryButtonText = "继续执行",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = owner.Content.XamlRoot,
        };

        safeBtn.Click += (_, _) =>
        {
            if (safeModeSql is null) return;
            runSql = safeModeSql;
            unlockBox.Text = db;   // 安全模式视为已降级，直接放行改写后的语句
        };

        var result = await dialog.ShowThemedAsync();
        if (result != ContentDialogResult.Primary) return new Result(false, sql, false);

        if (unlockBox.Text.Trim() == db)
        {
            proceed = true;
        }
        else if (bypassCheck.IsChecked == true)
        {
            // 二次确认：显式输入库名才给会话豁免（设计稿 §7 逃生条款）
            var confirm = new ContentDialog
            {
                Title = "本会话不再拦截",
                Content = new TextBlock
                {
                    // 整改三批三-6：豁免粒度细化——文案列明表与操作，只豁免同表同操作
                    Text = $"将跳过本会话（{prov.EngineName} · {db}）对{(string.IsNullOrEmpty(worst.Table) ? "未识别目标表" : $"表 {worst.Table}")}的「{worst.Kind}」拦截，其他表或操作仍会拦截。确认请输入库名 {db}：",
                    FontSize = AntTokens.FontMinor,
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "确认豁免",
                CloseButtonText = "取消",
                XamlRoot = owner.Content.XamlRoot,
            };
            var confirmBox = new TextBox { PlaceholderText = $"输入 {db} 确认", FontSize = AntTokens.FontMinor };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = $"将跳过本会话（{prov.EngineName} · {db}）对{(string.IsNullOrEmpty(worst.Table) ? "未识别目标表" : $"表 {worst.Table}")}的「{worst.Kind}」拦截，其他表或操作仍会拦截。确认请输入库名 {db}：",
                FontSize = AntTokens.FontMinor,
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(confirmBox);
            confirm.Content = panel;
            if (await confirm.ShowThemedAsync() == ContentDialogResult.Primary && confirmBox.Text.Trim() == db)
            {
                bypass = true;
                SessionBypass.Add(key);
                proceed = true;
                AppLog.Write($"[拦截留痕] 会话豁免 engine={prov.EngineName} db={db} table={worst.Table} op={worst.Kind} sql={sql.Replace('\r', ' ').Replace('\n', ' ')}");
            }
        }

        if (!proceed)
            AppLog.Write($"[拦截留痕] 拦截未放行 engine={prov.EngineName} db={db} level={worst.Level} kind={worst.Kind}");
        return new Result(proceed, runSql, bypass);
    }
}
