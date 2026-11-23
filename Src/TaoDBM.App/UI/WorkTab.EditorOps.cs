using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>查询编辑器操作扩展（批次一）：右键菜单、美化、注释切换、大小写、执行选中/当前语句、
/// 查找替换、执行计划、代码段、剪贴板堆栈、折叠/展开。</summary>
public partial class WorkTab
{
    private readonly SqlFolder _folder = new();

    /// <summary>编辑器 XamlRoot（对话框挂载用）。</summary>
    public XamlRoot EditorXamlRoot => _owner.Content.XamlRoot;

    /// <summary>当前光标位置。</summary>
    public int EditorCaret => _editor.Document?.Selection?.StartPosition ?? 0;

    /// <summary>当前选区（起点、长度）。</summary>
    public (int Start, int Length) EditorSelection
    {
        get
        {
            var sel = _editor.Document?.Selection;
            if (sel is null) return (0, 0);
            int s = sel.StartPosition, e = sel.EndPosition;
            return s <= e ? (s, e - s) : (e, s - e);
        }
    }

    /// <summary>把选区移到指定位置（查找下一个用）。</summary>
    public void SelectRange(int start, int length)
    {
        _editor.Document?.Selection?.SetRange(start, start + length);
        _editor.Focus(FocusState.Programmatic);
    }

    /// <summary>挂编辑器右键菜单与快捷键（仅查询页签调用）。</summary>
    private void AttachEditorOps()
    {
        var menu = new MenuFlyout();
        AddMenuItem(menu, "执行选中 / 当前语句", "editor-menu-run", RunSelectionOrCurrent);
        AddMenuItem(menu, "美化 SQL", "editor-menu-beautify", BeautifySql);
        AddMenuItem(menu, "注释切换（--）", "editor-menu-comment", ToggleComment);
        AddMenuItem(menu, "转大写", "editor-menu-upper", () => TransformSelection(s => s.ToUpperInvariant()));
        AddMenuItem(menu, "转小写", "editor-menu-lower", () => TransformSelection(s => s.ToLowerInvariant()));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(menu, "查找替换…", "editor-menu-find", () => _ = EditorFindDialog.ShowAsync(this));
        AddMenuItem(menu, "执行计划", "editor-menu-explain", () => _ = ExplainCurrent());
        AddMenuItem(menu, "代码段…", "editor-menu-snippets", ShowSnippets);
        AddMenuItem(menu, "粘贴自剪贴板堆栈…", "editor-menu-clipstack", ShowClipboardStack);
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(menu, "折叠选中行", "editor-menu-fold", FoldSelection);
        AddMenuItem(menu, "展开光标处折叠", "editor-menu-unfold", UnfoldAtCaret);
        _editor.ContextFlyout = menu;

        AddAccelerator(Windows.System.VirtualKey.F6, Windows.System.VirtualKeyModifiers.None, RunSelectionOrCurrent);
        AddAccelerator(Windows.System.VirtualKey.F, Windows.System.VirtualKeyModifiers.Control, () => _ = EditorFindDialog.ShowAsync(this));
        AddAccelerator(Windows.System.VirtualKey.U, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, () => TransformSelection(s => s.ToUpperInvariant()));
        AddAccelerator(Windows.System.VirtualKey.L, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, () => TransformSelection(s => s.ToLowerInvariant()));
        AddAccelerator(Windows.System.VirtualKey.J, Windows.System.VirtualKeyModifiers.Control, ShowSnippets);
        AddAccelerator(Windows.System.VirtualKey.V, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, ShowClipboardStack);

        // 复制自动入堆栈（RichEditBox 复制事件；失败静默不影响原生复制）
        try { _editor.CopyingToClipboard += (_, _) => Safe.Run(() => ClipboardStack.Push(SelectedText()), "剪贴板堆栈入栈"); }
        catch { /* 事件不可用不影响 */ }
    }

    private static void AddMenuItem(MenuFlyout menu, string label, string automationId, Action action)
    {
        var item = new MenuFlyoutItem { Text = label };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, automationId);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    /// <summary>执行选中片段；无选区执行光标所在语句（F6）。</summary>
    private async void RunSelectionOrCurrent()
    {
        try
        {
            var text = EditorText;
            var (selStart, selLen) = EditorSelection;
            var (s, l) = SqlStatementLocator.Locate(text, EditorCaret, selStart, selStart + selLen);
            if (l <= 0) { _messageBox.Text = "没有可执行的语句。"; return; }
            string stmt = text.Substring(s, l);
            if (string.IsNullOrWhiteSpace(stmt)) { _messageBox.Text = "没有可执行的语句。"; return; }
            AppLog.Write("[编辑器] run-selection current=" + (selLen > 0 ? "selection" : "statement"));
            await RunOrRefresh(stmt);
        }
        catch (Exception ex) { _messageBox.Text = "执行失败：" + ex.Message; }
    }

    private void BeautifySql()
    {
        var text = EditorText;
        if (string.IsNullOrWhiteSpace(text)) { _messageBox.Text = "没有可美化的 SQL。"; return; }
        EditorText = SqlBeautifier.Beautify(text);
        _messageBox.Text = "SQL 已美化：子句分行、括号缩进。";
        AppLog.Write("[编辑器] beautify");
    }

    private void ToggleComment()
    {
        var (start, len) = EditorSelection;
        var (newText, selStart, selLength) = SqlCommentToggler.Toggle(EditorText, start, start + len);
        EditorText = newText;
        SelectRange(selStart, selLength);
        AppLog.Write("[编辑器] comment-toggle");
    }

    private void TransformSelection(Func<string, string> transform)
    {
        var (start, len) = EditorSelection;
        if (len == 0) { _messageBox.Text = "请先选中要转换的文本。"; return; }
        var text = EditorText;
        if (start + len > text.Length) return;
        ReplaceRange(start, len, transform(text.Substring(start, len)));
        AppLog.Write("[编辑器] case-transform");
    }

    /// <summary>替换编辑器指定范围的文本并重刷高亮。</summary>
    private void ReplaceRange(int start, int length, string newText)
    {
        var doc = _editor.Document;
        if (doc is null) return;
        _applying = true;
        try
        {
            var range = doc.GetRange(start, start + length);
            range.Text = newText ?? "";
        }
        catch { /* 替换失败保持原文 */ }
        finally { _applying = false; }
        ApplyHighlight();
    }

    private string SelectedText()
    {
        var (start, len) = EditorSelection;
        if (len == 0) return "";
        var text = EditorText;
        return start + len <= text.Length ? text.Substring(start, len) : "";
    }

    /// <summary>执行计划入口：对当前语句包一层 EXPLAIN（估算计划，不执行本体）。</summary>
    private async Task ExplainCurrent()
    {
        if (_provider is not RelationalProvider) { _messageBox.Text = "该引擎不支持执行计划。"; return; }
        var text = EditorText;
        var (selStart, selLen) = EditorSelection;
        var (s, l) = SqlStatementLocator.Locate(text, EditorCaret, selStart, selStart + selLen);
        string stmt = l > 0 ? text.Substring(s, l).Trim().TrimEnd(';') : "";
        if (stmt.Length == 0) { _messageBox.Text = "没有可解释的语句。"; return; }
        AppLog.Write($"[编辑器] explain engine={_provider.EngineName}");
        string planSql;
        try { planSql = ExplainPlanEngine.PlanSql(_provider.Profile.Engine, stmt); }
        catch (NotSupportedException ex) { _messageBox.Text = ex.Message; return; }
        await RunOrRefresh(planSql);
    }

    private async void ShowSnippets()
    {
        var searchBox = new TextBox { PlaceholderText = "搜索代码段（名称或内容）", MinWidth = 340 };
        var lv = new ListView { SelectionMode = ListViewSelectionMode.Single, Height = 300 };
        List<SqlSnippets.Snippet> current = new();
        void Refill()
        {
            current = SqlSnippets.Search(searchBox.Text);
            lv.Items.Clear();
            foreach (var s in current)
            {
                string one = s.Sql.Replace("\r", " ").Replace("\n", " ");
                if (one.Length > 60) one = one[..60] + "…";
                lv.Items.Add(new TextBlock { Text = $"{s.Name}（{s.Category}）  {one}", TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 460 });
            }
        }
        Refill();
        searchBox.TextChanged += (_, _) => Refill();

        var dlg = new ContentDialog
        {
            Title = "代码段（点选插入光标处）",
            Content = new StackPanel { Spacing = 8, Children = { searchBox, lv } },
            PrimaryButtonText = "插入",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = EditorXamlRoot,
        };
        var result = await dlg.ShowThemedAsync();
        if (result == ContentDialogResult.Primary && lv.SelectedIndex >= 0 && lv.SelectedIndex < current.Count)
        {
            int caret = EditorCaret;
            ReplaceRange(caret, 0, current[lv.SelectedIndex].Sql);
            SelectRange(caret + current[lv.SelectedIndex].Sql.Length, 0);
            AppLog.Write("[编辑器] snippet-insert name=" + current[lv.SelectedIndex].Name);
        }
    }

    private async void ShowClipboardStack()
    {
        var items = ClipboardStack.Snapshot();
        if (items.Count == 0)
        {
            _messageBox.Text = "剪贴板堆栈为空：复制 SQL 片段后会自动入栈（保留最近 20 条）。";
            return;
        }
        var lv = new ListView { SelectionMode = ListViewSelectionMode.Single, Height = 300 };
        foreach (var t in items)
        {
            string one = t.Replace("\r", " ").Replace("\n", " ");
            if (one.Length > 80) one = one[..80] + "…";
            lv.Items.Add(new TextBlock { Text = one, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 460 });
        }
        var dlg = new ContentDialog
        {
            Title = "剪贴板堆栈（点选插入光标处）",
            Content = lv,
            PrimaryButtonText = "插入",
            CloseButtonText = "清空堆栈",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = EditorXamlRoot,
        };
        var result = await dlg.ShowThemedAsync();
        if (result == ContentDialogResult.Primary && lv.SelectedIndex >= 0 && lv.SelectedIndex < items.Count)
        {
            int caret = EditorCaret;
            ReplaceRange(caret, 0, items[lv.SelectedIndex]);
            SelectRange(caret + items[lv.SelectedIndex].Length, 0);
            AppLog.Write("[编辑器] clipstack-paste");
        }
        else if (result == ContentDialogResult.Close) ClipboardStack.Clear();
    }

    private void FoldSelection()
    {
        var (start, len) = EditorSelection;
        var before = EditorText;
        var (newText, selStart, selLength) = _folder.Fold(before, start, start + len);
        if (newText == before) { _messageBox.Text = "请选中至少两行再折叠。"; return; }
        EditorText = newText;
        SelectRange(selStart, selLength);
        AppLog.Write("[编辑器] fold pending=" + _folder.PendingCount);
    }

    private void UnfoldAtCaret()
    {
        var before = EditorText;
        var (newText, selStart, selLength) = _folder.Unfold(before, EditorCaret);
        if (newText == before) { _messageBox.Text = "光标不在折叠标记上。"; return; }
        EditorText = newText;
        SelectRange(selStart, Math.Min(selLength, 0));
        AppLog.Write("[编辑器] unfold pending=" + _folder.PendingCount);
    }
}
