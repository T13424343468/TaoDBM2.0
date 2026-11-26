using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>迁移续传对话框：整库迁移的断点续传预演——载入清单与状态，重算续传计划（完成表校验/半途表续传/未动表整表拷贝），
/// 支持另存状态 JSON。只做计划预演与文件读写，不执行任何迁移动作。</summary>
public static class MigrationResumeDialog
{
    private static MigrationState? _freshState;

    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        _freshState = null;
        var source = info.Profile.DisplayName;
        var manifestBox = new TextBox
        {
            PlaceholderText = "粘贴迁移清单 JSON（MigrationManifest）", AcceptsReturn = true, Height = 110,
            TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"),
        };
        var stateBox = new TextBox
        {
            PlaceholderText = "粘贴迁移状态 JSON（MigrationState，可留空则按全新状态预演）", AcceptsReturn = true, Height = 110,
            TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"),
        };
        var report = new StackPanel { Spacing = 8 };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var planBtn = new Button { Content = "重算续传计划", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var saveBtn = new Button { Content = "另存状态 JSON", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), IsEnabled = false };

        // 整改三批三-12（2026-11-24 老板令「抓紧时间」）：演示式接真实数据——清单从当前连接真实表清单与行数生成，
        // 不再只靠手填。只读查询（表清单 + COUNT），失败如实提示不阻断手填路径。
        var genBtn = new Button { Content = "从当前连接生成清单", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        genBtn.Click += async (_, _) => await RunRealManifestAsync(owner, info, manifestBox, report, status);

        planBtn.Click += async (_, _) =>
        {
            report.Children.Clear();
            saveBtn.IsEnabled = false;
            MigrationManifest? manifest;
            try { manifest = System.Text.Json.JsonSerializer.Deserialize<MigrationManifest>(manifestBox.Text ?? ""); }
            catch (System.Text.Json.JsonException) { status.Text = "清单 JSON 无法解析，请核对格式。"; return; }
            if (manifest is null || manifest.Tables.Count == 0) { status.Text = "清单为空或没有表目。"; return; }

            MigrationState? state = null;
            var stateText = (stateBox.Text ?? "").Trim();
            if (stateText.Length > 0)
            {
                state = MigrationResume.FromJson(stateText);
                if (state is null) { status.Text = "状态 JSON 无法解析，请核对格式。"; return; }
                if (!string.Equals(state.ManifestId, manifest.ManifestId, StringComparison.Ordinal))
                {
                    status.Text = $"清单与状态不匹配（清单 {manifest.ManifestId}，状态 {state.ManifestId}），拒绝续传。";
                    return;
                }
            }
            else
            {
                state = MigrationResume.Fresh(manifest);
                _freshState = state;
                saveBtn.IsEnabled = true;
            }

            try
            {
                var plan = MigrationResume.Plan(manifest, state);
                AddSection(report, $"清单 {manifest.ManifestId}：{manifest.Source} → {manifest.Target}，共 {plan.Actions.Count} 张表");
                foreach (var a in plan.Actions)
                {
                    var label = a.Action switch
                    {
                        "copy" => "整表拷贝",
                        "resume" => "断点续传",
                        "verify" => "行数校验",
                        _ => a.Action,
                    };
                    AddLine(report, $"[{label}] {a.Table}：{a.Note}", a.Action == "resume" ? Ant.Primary : null, 1);
                }
                if (plan.Drifted.Count > 0)
                    AddLine(report, $"⚠ 源行数漂移：{string.Join(", ", plan.Drifted)}（清单基线已过期，建议重建清单）", Ant.Warn, 1);
                if (plan.VerifyMismatch.Count > 0)
                    AddLine(report, $"⚠ 目标行数不符：{string.Join(", ", plan.VerifyMismatch)}（这些表已改为整表重灌）", Ant.Warn, 1);
                status.Text = stateText.Length == 0
                    ? "已按全新状态预演：全部表整表拷贝。全新状态可另存为 JSON，迁移时逐表推进。"
                    : "续传计划就绪：以上为重算结果，仅预演，不执行。";
            }
            catch (ArgumentException ex) { status.Text = HumanError.Say(ex, "续传计划失败"); }
            await Task.CompletedTask;
        };

        saveBtn.Click += async (_, _) =>
        {
            var st = _freshState;
            if (st is null) { status.Text = "没有可保存的状态（仅全新状态预演可另存）。"; return; }
            try
            {
                var path = TaoPaths.FilePathIn("migrations", $"migration-state-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
                await System.IO.File.WriteAllTextAsync(path, MigrationResume.ToJson(st));
                status.Text = $"已保存：{path}";
            }
            catch (Exception ex) { status.Text = HumanError.Say(ex, "保存失败"); }
            await Task.CompletedTask;
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(planBtn); row.Children.Add(saveBtn); row.Children.Add(genBtn);

        var panel = new StackPanel { Spacing = 12, MinWidth = 680 };
        panel.Children.Add(new TextBlock
        {
            Text = $"断点续传预演（{source}）：清单记基线，状态记水位，重启后重算——完成的表只校验、半途的表从水位续、未动的表整表拷。只预演不执行。",
            TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(manifestBox);
        panel.Children.Add(stateBox);
        panel.Children.Add(row);
        panel.Children.Add(new ScrollViewer { Content = report, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "迁移续传",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
        await Task.CompletedTask;
    }

    /// <summary>三-12：真实清单生成——读当前连接的表清单与行数，生成 MigrationManifest JSON 回填清单框。</summary>
    private static async Task RunRealManifestAsync(MainWindow owner, NodeInfo info, TextBox manifestBox, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        try
        {
            var provider = info.Provider ?? ProviderFactory.Create(info.Profile);
            string db = info.Db.Length > 0 ? info.Db : (info.Profile.Database.Length > 0 ? info.Profile.Database : info.Name);
            if (db.Length == 0) { status.Text = "未定位到当前库，请先选中库或表再进本窗口。"; return; }
            var tables = await provider.GetChildren(db);
            if (tables.Count == 0) { status.Text = $"未读到 {db} 的表清单。"; return; }

            var entries = new List<MigrationTable>();
            int taken = 0;
            foreach (var t in tables)
            {
                if (taken >= 12) break;   // 行数采样上限：前 12 张表，避免大库全量扫描
                long rows = -1;
                try
                {
                    var rs = await provider.Execute(db, $"SELECT COUNT(*) FROM {provider.Quote(t)}");
                    var stat = rs.FirstOrDefault(x => x.Rows.Count > 0);
                    if (stat is not null) rows = Convert.ToInt64(stat.Rows[0][0]);
                }
                catch { /* 单表计数失败不阻断其余表 */ }
                if (rows < 0) continue;
                int batches = (int)Math.Max(1, (rows + 9999) / 10000);   // 每万行一批，与引擎批内水位口径一致
                entries.Add(new MigrationTable(t, rows, batches));
                taken++;
            }
            if (entries.Count == 0) { status.Text = "行数采样未得到可用表目（均不可计数）。"; return; }

            var manifest = new MigrationManifest(
                $"mf-{DateTimeOffset.Now:yyyyMMdd-HHmmss}",
                info.Profile.DisplayName, "（待填目标连接）", DateTimeOffset.Now, entries);
            manifestBox.Text = System.Text.Json.JsonSerializer.Serialize(manifest,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            AddSection(report, $"真实清单：{db}（{entries.Count} 张表，共 {entries.Sum(e => e.SourceRows)} 行）");
            AddLine(report, "  清单已回填：目标连接待填，点「重算续传计划」预演。");
            status.Text = "清单生成完成。";
            AppLog.Write($"[迁移清单] db={db} tables={entries.Count}");
        }
        catch (Exception ex)
        {
            status.Text = HumanError.Say(ex, "清单生成失败");
            AppLog.Write($"[迁移清单] error={ex.Message}");
        }
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
