using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>编辑器查找替换对话框（批次一）：正则＋全字＋区分大小写，查找下一个、替换预览、全部替换。</summary>
public static class EditorFindDialog
{
    /// <summary>在编辑器页签内打开查找替换；返回后编辑器选区可能已被移动到命中处。</summary>
    public static async Task ShowAsync(WorkTab tab)
    {
        var findBox = new TextBox { PlaceholderText = "查找内容", MinWidth = 320 };
        var replBox = new TextBox { PlaceholderText = "替换为（可留空）", MinWidth = 320 };
        var rxCheck = new CheckBox { Content = "正则表达式", Margin = new Thickness(0, 4, 12, 0) };
        var wordCheck = new CheckBox { Content = "全字匹配", Margin = new Thickness(0, 4, 12, 0) };
        var caseCheck = new CheckBox { Content = "区分大小写", Margin = new Thickness(0, 4, 0, 0) };
        var optRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        optRow.Children.Add(rxCheck);
        optRow.Children.Add(wordCheck);
        optRow.Children.Add(caseCheck);

        var preview = new TextBlock
        {
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.TextSub,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 110,
        };
        var nextBtn = new Button { Content = "查找下一个", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var replBtn = new Button { Content = "全部替换", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        btnRow.Children.Add(nextBtn);
        btnRow.Children.Add(replBtn);

        var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
        panel.Children.Add(findBox);
        panel.Children.Add(replBox);
        panel.Children.Add(optRow);
        panel.Children.Add(btnRow);
        panel.Children.Add(preview);

        var dlg = new ContentDialog
        {
            Title = "查找替换",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = tab.EditorXamlRoot,
        };

        SqlFindOptions Opt() => new() { Regex = rxCheck.IsChecked == true, WholeWord = wordCheck.IsChecked == true, MatchCase = caseCheck.IsChecked == true };

        findBox.TextChanged += (_, _) =>
        {
            try
            {
                var hits = SqlFindReplace.FindAll(tab.EditorText, findBox.Text, Opt());
                var lines = SqlFindReplace.Preview(tab.EditorText, findBox.Text, Opt());
                preview.Text = hits.Count == 0
                    ? "没有找到匹配。"
                    : $"共 {hits.Count} 处匹配，预览：\n" + string.Join("\n", lines.Select(l => "· " + (l.Length > 90 ? l[..90] + "…" : l)));
            }
            catch (ArgumentException ex)
            {
                preview.Text = ex.Message;
            }
        };

        nextBtn.Click += (_, _) =>
        {
            try
            {
                var hits = SqlFindReplace.FindAll(tab.EditorText, findBox.Text, Opt());
                if (hits.Count == 0) { preview.Text = "没有找到匹配。"; return; }
                int caret = tab.EditorCaret;
                var next = hits.FirstOrDefault(h => h.Start >= caret, hits[0]);
                tab.SelectRange(next.Start, next.Length);
            }
            catch (ArgumentException ex) { preview.Text = ex.Message; }
        };

        replBtn.Click += (_, _) =>
        {
            try
            {
                var (newText, count) = SqlFindReplace.ReplaceAll(tab.EditorText, findBox.Text, replBox.Text, Opt());
                if (count == 0) { preview.Text = "没有找到匹配，未替换。"; return; }
                tab.EditorText = newText;
                preview.Text = $"已替换 {count} 处。";
            }
            catch (ArgumentException ex) { preview.Text = ex.Message; }
        };

        await dlg.ShowThemedAsync();
    }
}
