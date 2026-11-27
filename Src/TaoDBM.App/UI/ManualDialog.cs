using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>操作手册窗口（2026-11-20 老板令「操作手册」功能）：左侧章节目录 + 右侧正文，
/// 内容离线内置（ManualContent，纯数据同源校验）。入口：顶栏「手册」、左上菜单「操作手册」、全局 F1。
/// 原型212补齐（预修批）：目录区搜索框（输入即筛＋实时命中数）、底部「上一节/下一节/打印」导航与
/// 「当前章节 x/N · 阅读位置已记忆」进度行——阅读位置经 UiPrefs 落盘，重开落在上次读到处。
/// 浅色主题遵守视觉对稿纪律，随当前主题象钉定。</summary>
public static class ManualDialog
{
    private const string PosKey = "manual-read-pos";

    /// <summary>展平后的「节」——导航与进度按节计（原型 3/72 口径），跨章节顺序推进。</summary>
    private sealed record FlatSec(int ChapterIdx, string ChapterTitle, string Heading, string Body);

    public static void Show(MainWindow owner)
    {
        var flat = new List<FlatSec>();
        for (int ci = 0; ci < ManualContent.Chapters.Length; ci++)
        {
            var ch = ManualContent.Chapters[ci];
            for (int si = 0; si < ch.Sections.Length; si++)
                flat.Add(new FlatSec(ci, ch.Title, ch.Sections[si].Heading, ch.Sections[si].Body));
        }

        var list = new ListBox
        {
            Width = 208,
            Margin = new Thickness(0, 8, 14, 0),
            SelectionMode = SelectionMode.Single,
        };
        foreach (var ch in ManualContent.Chapters)
        {
            list.Items.Add(new ListBoxItem
            {
                Content = new TextBlock { Text = ch.Title, TextWrapping = TextWrapping.Wrap, FontSize = AntTokens.FontBody },
                Padding = new Thickness(10, 7, 6, 7),
            });
        }

        // 原型212：目录区搜索框——输入即筛（章节标题/节标题/正文含关键字），实时命中数；清空恢复全量
        var search = new TextBox { PlaceholderText = "搜索手册…", Margin = new Thickness(0, 0, 14, 0) };
        var hitText = new TextBlock
        {
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.TextSub,
            Margin = new Thickness(2, 4, 14, 0),
            Visibility = Visibility.Collapsed,
        };

        var bodyHost = new StackPanel { Spacing = 12 };
        var scroll = new ScrollViewer
        {
            Content = bodyHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(4, 0, 8, 0),
        };

        // 原型212：底部导航条——进度行＋上一节/下一节/打印
        var posText = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, VerticalAlignment = VerticalAlignment.Center };
        var prevBtn = new Button { Padding = new Thickness(10, 4, 10, 4) };
        var nextBtn = new Button { Padding = new Thickness(10, 4, 10, 4) };
        var printBtn = new Button { Content = "🖨 打印", Padding = new Thickness(10, 4, 10, 4) };
        var navHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        navHost.Children.Add(prevBtn);
        navHost.Children.Add(nextBtn);
        navHost.Children.Add(printBtn);
        var foot = new Grid { Margin = new Thickness(4, 10, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(posText, 0);
        Grid.SetColumn(navHost, 1);
        foot.Children.Add(posText);
        foot.Children.Add(navHost);

        var grid = new Grid { MinHeight = 560 };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(search);
        left.Children.Add(hitText);
        left.Children.Add(list);
        Grid.SetRow(left, 0);
        Grid.SetColumn(left, 0);
        Grid.SetRow(scroll, 0);
        Grid.SetColumn(scroll, 1);
        Grid.SetRow(foot, 1);
        Grid.SetColumnSpan(foot, 2);
        grid.Children.Add(left);
        grid.Children.Add(scroll);
        grid.Children.Add(foot);

        int cur = -1;   // 当前节在展平序列中的下标
        bool muteSel = false;

        void RenderChapter(int chapterIdx)
        {
            bodyHost.Children.Clear();
            if (chapterIdx < 0 || chapterIdx >= ManualContent.Chapters.Length) return;
            foreach (var sec in ManualContent.Chapters[chapterIdx].Sections)
            {
                bodyHost.Children.Add(new TextBlock
                {
                    Text = sec.Heading,
                    FontSize = AntTokens.FontTitle,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Ant.PrimaryDark,
                    Margin = new Thickness(0, 8, 0, 0),
                });
                foreach (var line in sec.Body.Split('\n'))
                {
                    bool bullet = line.StartsWith("· ", StringComparison.Ordinal);
                    bool note = line.StartsWith('！');
                    bodyHost.Children.Add(new TextBlock
                    {
                        Text = bullet ? line[2..] : line,
                        FontSize = AntTokens.FontBody,
                        Foreground = note ? Ant.PrimaryDark : Ant.Text,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(bullet || note ? 18 : 0, 0, 0, 0),
                        LineHeight = 22,
                    });
                }
            }
            scroll.ChangeView(null, 0, null);
        }

        void GoTo(int flatIdx)
        {
            if (flatIdx < 0 || flatIdx >= flat.Count) return;
            cur = flatIdx;
            var f = flat[flatIdx];
            muteSel = true;
            list.SelectedIndex = f.ChapterIdx;
            muteSel = false;
            RenderChapter(f.ChapterIdx);
            posText.Text = $"当前章节 {flatIdx + 1} / {flat.Count} · 阅读位置已记忆";
            prevBtn.Content = flatIdx > 0 ? $"◀ 上一节：{flat[flatIdx - 1].Heading}" : "◀ 上一节";
            prevBtn.IsEnabled = flatIdx > 0;
            nextBtn.Content = flatIdx < flat.Count - 1 ? $"下一节：{flat[flatIdx + 1].Heading} ▶" : "下一节 ▶";
            nextBtn.IsEnabled = flatIdx < flat.Count - 1;
            UiPrefs.Set(PosKey, flatIdx.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        list.SelectionChanged += (_, _) =>
        {
            if (muteSel || list.SelectedIndex < 0) return;
            // 目录点选：落在该章第一节（进度与记忆按节推进）
            int flatIdx = flat.FindIndex(f => f.ChapterIdx == list.SelectedIndex);
            if (flatIdx >= 0) GoTo(flatIdx);
        };

        prevBtn.Click += (_, _) => GoTo(cur - 1);
        nextBtn.Click += (_, _) => GoTo(cur + 1);
        printBtn.Click += (_, _) => _ = PrintAsync(owner, ManualContent.Chapters[Math.Clamp(cur, 0, flat.Count - 1)]);

        search.TextChanged += (_, _) =>
        {
            string q = search.Text.Trim();
            if (q.Length == 0)
            {
                for (int i = 0; i < list.Items.Count; i++) ((ListBoxItem)list.Items[i]).Visibility = Visibility.Visible;
                hitText.Visibility = Visibility.Collapsed;
                return;
            }
            int hits = 0;
            for (int i = 0; i < ManualContent.Chapters.Length; i++)
            {
                var ch = ManualContent.Chapters[i];
                bool titleHit = ch.Title.Contains(q, StringComparison.OrdinalIgnoreCase);
                bool secHit = ch.Sections.Any(s => s.Heading.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Body.Contains(q, StringComparison.OrdinalIgnoreCase));
                ((ListBoxItem)list.Items[i]).Visibility = titleHit || secHit ? Visibility.Visible : Visibility.Collapsed;
                if (titleHit) hits++;
                if (secHit) hits += ch.Sections.Count(s => s.Heading.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Body.Contains(q, StringComparison.OrdinalIgnoreCase));
            }
            hitText.Text = $"共 {hits} 处命中";
            hitText.Visibility = Visibility.Visible;
        };

        // 原型212：标题带版本与离线口径
        var dlg = new ContentDialog
        {
            Title = "用户手册 v2.0.0 · 离线内置（F1 随时打开）",
            Content = grid,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        dlg.Resources["ContentDialogMaxWidth"] = 1080d;
        dlg.Resources["ContentDialogMaxHeight"] = 760d;
        // 阅读位置记忆：重开落在上次读到处（无记录或越界回落第一节）
        _ = int.TryParse(UiPrefs.Get(PosKey), out int saved) ? saved : 0;
        GoTo(Math.Clamp(saved, 0, flat.Count - 1));
        _ = dlg.ShowThemedAsync();
    }

    /// <summary>原型212：打印——当前章转 HTML（内联样式，标题＋正文），写临时文件后系统打开打印（同模型打印模式）。</summary>
    private static async System.Threading.Tasks.Task PrintAsync(MainWindow owner, ManualContent.Chapter chapter)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<html><head><meta charset=\"utf-8\"><title>TaoDBM 用户手册 · ").Append(System.Net.WebUtility.HtmlEncode(chapter.Title))
          .Append("</title></head><body style=\"font-family:'Microsoft YaHei',sans-serif;max-width:760px;margin:24px auto;\">")
          .Append("<h1>TaoDBM 用户手册 · ").Append(System.Net.WebUtility.HtmlEncode(chapter.Title)).Append("</h1>");
        foreach (var sec in chapter.Sections)
        {
            sb.Append("<h2>").Append(System.Net.WebUtility.HtmlEncode(sec.Heading)).Append("</h2>");
            foreach (var line in sec.Body.Split('\n'))
                sb.Append("<p style=\"line-height:1.7;margin:4px 0;\">").Append(System.Net.WebUtility.HtmlEncode(line)).Append("</p>");
        }
        sb.Append("</body></html>");
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"TaoDBM-手册-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            await System.IO.File.WriteAllTextAsync(path, sb.ToString(), System.Text.Encoding.UTF8);
            var psi = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
            AppLog.Write("[手册] 打印");
        }
        catch (Exception ex)
        {
            AppLog.Write("[手册] 打印失败: " + HumanError.Say(ex, "打印"));
        }
    }
}
