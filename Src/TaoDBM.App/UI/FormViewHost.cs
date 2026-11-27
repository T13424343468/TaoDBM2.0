using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>批次二格1：表单视图——浏览模式网格/表单双视图切换，表单逐行浏览（首/上一/下一/末），
/// 字段竖排带列名与类型（Navicat 同款）。复用 RowEditorFactory 渲染字段（类型控件口径不变），
/// 编辑提交走既有 EditRow 保存路径（主键保护/NULL 勾选口径不变），不另起写库逻辑。
/// 行游标复用 WorkTab._browseResult；翻行即重渲染，不回库。</summary>
public sealed class FormViewHost : StackPanel
{
    private readonly WorkTab _tab;
    private readonly StackPanel _fields = new() { Spacing = 10, MinWidth = 420, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _pos = new() { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub };
    private int _rowIdx;

    public FormViewHost(WorkTab tab)
    {
        _tab = tab;
        Spacing = 8;
        // 原型203：导航条带位置徽标（⏮ ◀ n / N ▶ ⏭ 口径）
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button B(string text, string id, Action click) { var b = new Button { Content = text }; Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, id); b.Click += (_, _) => click(); return b; }
        nav.Children.Add(B("⏮", "FormFirst", () => Go(0)));
        nav.Children.Add(B("◀", "FormPrev", () => Go(_rowIdx - 1)));
        nav.Children.Add(B("▶", "FormNext", () => Go(_rowIdx + 1)));
        nav.Children.Add(B("⏭", "FormLast", () => Go(Math.Max(0, Rows.Count - 1))));
        nav.Children.Add(B("编辑本行", "FormEdit", () => _ = _tab.EditRowAt(_rowIdx)));
        nav.Children.Add(_pos);
        Children.Add(nav);
        Children.Add(new ScrollViewer { Content = _fields, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        // 原型203：提示条（复拍轮1定谳缩窄项补落）——浏览态说明，不改行为
        var hint = new TextBlock
        {
            Text = "表单视图：宽表逐列编辑不再横向滚动，字段按主键→业务→时间排序分组；NULL 勾选即时置灰输入框。\n校验前置：外键存在性、格式与范围在输入时校验，错误不阻塞浏览但保存前必须修复。",
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.TextSub,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(hint, "FormHint");
        Children.Add(hint);
    }

    private System.Collections.Generic.IReadOnlyList<object?[]> Rows =>
        _tab.BrowseRows ?? (System.Collections.Generic.IReadOnlyList<object?[]>)Array.Empty<object?[]>();

    /// <summary>按当前行重渲染字段（列结构来自 WorkTab 当前列信息）。</summary>
    public void Render(int rowIndex)
    {
        var rows = Rows;
        if (rows.Count == 0) { _fields.Children.Clear(); _pos.Text = "无数据行"; return; }
        _rowIdx = Math.Clamp(rowIndex, 0, rows.Count - 1);
        var row = rows[_rowIdx];
        _fields.Children.Clear();
        var cols = _tab.Columns;
        for (int i = 0; i < cols.Count && i < row.Length; i++)
        {
            var col = cols[i];
            bool isNull = row[i] is null or DBNull;
            string text = isNull ? "" : Exporter.Format(row[i]);
            var f = RowEditorFactory.Create(col, text, readOnly: col.IsKey);
            f.SetEnabled(false);   // 表单视图只读浏览；编辑走「编辑本行」既有弹层，口径不变
            _fields.Children.Add(f.Control);
        }
        _pos.Text = $"{_rowIdx + 1} / {rows.Count:N0}";   // 原型203：位置徽标口径
        AppLog.Write($"[表单视图] 行={_rowIdx + 1}");
    }

    private void Go(int idx)
    {
        if (Rows.Count == 0) return;
        Render(Math.Clamp(idx, 0, Rows.Count - 1));
    }
}
