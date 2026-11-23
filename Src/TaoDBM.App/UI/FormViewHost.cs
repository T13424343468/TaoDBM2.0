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
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button B(string text, string id, Action click) { var b = new Button { Content = text }; Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, id); b.Click += (_, _) => click(); return b; }
        nav.Children.Add(B("首行", "FormFirst", () => Go(0)));
        nav.Children.Add(B("上一行", "FormPrev", () => Go(_rowIdx - 1)));
        nav.Children.Add(B("下一行", "FormNext", () => Go(_rowIdx + 1)));
        nav.Children.Add(B("末行", "FormLast", () => Go(Math.Max(0, Rows.Count - 1))));
        nav.Children.Add(B("编辑本行", "FormEdit", () => _ = _tab.EditRowAt(_rowIdx)));
        nav.Children.Add(_pos);
        Children.Add(nav);
        Children.Add(new ScrollViewer { Content = _fields, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
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
        _pos.Text = $"第 {_rowIdx + 1} / {rows.Count} 行";
        AppLog.Write($"[表单视图] 行={_rowIdx + 1}");
    }

    private void Go(int idx)
    {
        if (Rows.Count == 0) return;
        Render(Math.Clamp(idx, 0, Rows.Count - 1));
    }
}
