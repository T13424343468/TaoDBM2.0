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
        var status = new TextBlock { FontSize = 12, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var planBtn = new Button { Content = "重算续传计划", CornerRadius = new CornerRadius(4) };
        var saveBtn = new Button { Content = "另存状态 JSON", CornerRadius = new CornerRadius(4), IsEnabled = false };

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
            catch (ArgumentException ex) { status.Text = "续传计划失败：" + ex.Message; }
            await Task.CompletedTask;
        };

        saveBtn.Click += async (_, _) =>
        {
            var st = _freshState;
            if (st is null) { status.Text = "没有可保存的状态（仅全新状态预演可另存）。"; return; }
            try
            {
                var path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TaoDBM", "migrations", $"migration-state-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                await System.IO.File.WriteAllTextAsync(path, MigrationResume.ToJson(st));
                status.Text = $"已保存：{path}";
            }
            catch (Exception ex) { status.Text = "保存失败：" + ex.Message; }
            await Task.CompletedTask;
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(planBtn); row.Children.Add(saveBtn);

        var panel = new StackPanel { Spacing = 12, MinWidth = 680 };
        panel.Children.Add(new TextBlock
        {
            Text = $"断点续传预演（{source}）：清单记基线，状态记水位，重启后重算——完成的表只校验、半途的表从水位续、未动的表整表拷。只预演不执行。",
            TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub, FontSize = 12,
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
