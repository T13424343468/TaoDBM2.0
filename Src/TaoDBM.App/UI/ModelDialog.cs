// 批次五格1/2/4/5（2026-11-23）：三层模型对话框——概念/逻辑/物理三层切换（DataModelDoc），
// 从库生成、从 SQL 导入（DdlParser）、模型比较（ModelCompare vs 真库 GetColumns）、
// 保存/打开 DataModelDoc JSON、打印（模型转 HTML 后系统打开打印）、物理层 DDL 预览（复用 ErdForward）。
// 画布复用 ErdModel 骨架（参考 ErdDialog：拖拽排布＋关系线），但文档状态是三层各一份。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;
using Windows.Foundation;
using Windows.Storage.Pickers;

namespace TaoDBM.UI;

/// <summary>三层模型：概念（实体名）/ 逻辑（实体＋类型）/ 物理（方言 DDL），层级切换字段显隐，物理层可正向 DDL。</summary>
public static class ModelDialog
{
    private static DataModelDoc _doc = new();
    private static Canvas _canvas = null!;
    private static readonly Dictionary<string, FrameworkElement> _boxes = new(StringComparer.OrdinalIgnoreCase);
    private static TextBlock _hint = null!;
    private static ComboBox _dbBox = null!;
    private static IDbProvider? _prov;
    private static string _db = "";
    private static MainWindow _owner = null!;

    public static void Show(MainWindow owner, NodeInfo info)
    {
        _owner = owner;
        _prov = info.Provider;
        _db = info.Db ?? info.Profile.Database ?? "";
        _doc = new DataModelDoc();
        _boxes.Clear();

        _canvas = new Canvas { Background = Ant.Card, MinHeight = 560, MinWidth = 760 };
        _hint = new TextBlock
        {
            Foreground = Ant.TextSub,
            FontSize = AntTokens.FontMinor,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 10, 0, 0),
            Text = "三层模型：概念层只看实体与关系，逻辑层补类型，物理层可正向生成 DDL。先「生成」或「从 SQL 导入」。",
        };

        // 层级切换（批次五格1）：概念/逻辑/物理
        var layerBox = new ComboBox { MinWidth = 110, PlaceholderText = "层级" };
        layerBox.Items.Add("概念");
        layerBox.Items.Add("逻辑");
        layerBox.Items.Add("物理");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(layerBox, "ModelLayer");
        layerBox.SelectedIndex = 2;
        layerBox.SelectionChanged += (_, _) =>
        {
            _doc.Current = layerBox.SelectedIndex switch
            {
                0 => ModelLayer.Conceptual,
                1 => ModelLayer.Logical,
                _ => ModelLayer.Physical,
            };
            Place(_doc.CurrentModel);
            AppLog.Write($"[模型] 层={ModelLayerInfo.Name(_doc.Current)} 实体={_doc.CurrentModel.Tables.Count}");   // 批次五格1 验收锚点
        };

        // 库选择（从库生成 / 模型比较需要真库上下文）
        _dbBox = new ComboBox { MinWidth = 160, PlaceholderText = "库" };
        if (_prov is not null)
        {
            _ = Safe.RunAsync(async () =>
            {
                foreach (var d in await _prov.GetDatabases()) _dbBox.Items.Add(d);
                var idx = _dbBox.Items.IndexOf(_db);
                if (idx >= 0) _dbBox.SelectedIndex = idx;
                else if (_dbBox.Items.Count > 0) _dbBox.SelectedIndex = 0;
            }, "三层模型默认库列表");
        }
        _dbBox.SelectionChanged += (_, _) => { if (_dbBox.SelectedItem is string s) _db = s; };

        var genBtn = new Button { Content = "生成", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        genBtn.Click += async (_, _) => await BuildAsync();

        var importBtn = new Button { Content = "从 SQL 导入", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(importBtn, "ModelImportSql");
        importBtn.Click += async (_, _) => await ImportSqlAsync();

        var cmpBtn = new Button { Content = "模型比较", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(cmpBtn, "ModelCompare");
        cmpBtn.Click += async (_, _) => await CompareAsync();

        MenuFlyoutItem SubItem(string text, string id, Action onClick)
        {
            var it = new MenuFlyoutItem { Text = text };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(it, id);
            it.Click += (_, _) => onClick();
            return it;
        }
        var fileMenu = new MenuFlyoutSubItem { Text = "文件" };
        fileMenu.Items.Add(SubItem("保存模型", "ModelFile_Save", () => _ = SaveModelAsync()));
        fileMenu.Items.Add(SubItem("打开模型", "ModelFile_Open", () => _ = OpenModelAsync()));
        fileMenu.Items.Add(SubItem("打印", "ModelPrint", () => _ = PrintAsync()));
        var ddlMenu = new MenuFlyoutSubItem { Text = "DDL" };
        ddlMenu.Items.Add(SubItem("预览 DDL（物理层）", "ModelDdl_Preview", () => _ = PreviewDdlAsync()));
        var barFlyout = new MenuFlyout();
        barFlyout.Items.Add(fileMenu);
        barFlyout.Items.Add(ddlMenu);
        var menuBtn = new Button { Content = "操作 ▾", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        menuBtn.Flyout = barFlyout;

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(layerBox);
        bar.Children.Add(_dbBox);
        bar.Children.Add(genBtn);
        bar.Children.Add(importBtn);
        bar.Children.Add(cmpBtn);
        bar.Children.Add(menuBtn);

        var panel = new StackPanel { Spacing = 4, MinWidth = 800, MinHeight = 640 };
        panel.Children.Add(bar);
        panel.Children.Add(_hint);
        panel.Children.Add(new Border
        {
            Child = new ScrollViewer
            {
                Content = _canvas,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            BorderBrush = Ant.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg),
            Height = 560,
        });

        var dlg = new ContentDialog
        {
            Title = "三层模型（概念 / 逻辑 / 物理）",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
    }

    /// <summary>批次五格1：从库拉表进当前层模型（口径同 ErdDialog：最多 40 张，外键深度推断）。</summary>
    private static async Task BuildAsync()
    {
        if (_prov is null || string.IsNullOrEmpty(_db)) { _hint.Text = "请先选好库。"; return; }
        var m = _doc.CurrentModel;
        m.Tables.Clear();
        m.Edges.Clear();
        _boxes.Clear();
        _canvas.Children.Clear();
        _canvas.Children.Add(_hint);
        Canvas.SetZIndex(_hint, 100);
        try
        {
            var tables = await _prov.GetChildren(_db);
            int limit = Math.Min(tables.Count, 40);
            for (int i = 0; i < limit; i++)
            {
                List<ColumnSpec> cols;
                try { cols = await _prov.GetColumns(_db, tables[i]); }
                catch { continue; }
                if (cols.Count == 0) continue;
                m.Tables.Add(ErdModel.FromSpecs(tables[i], cols));
            }
            if (m.Tables.Count == 0) { _hint.Text = "该库没有可用表。"; return; }
            List<object?[]>? fkRows = null;
            try
            {
                var fkSql = ObjectTreeCatalog.ListForeignKeys(_prov.Profile.Engine, _db);
                var fkRs = await _prov.Execute(_db, fkSql);
                fkRows = fkRs.Count > 0 ? fkRs[0].Rows : null;
            }
            catch { /* 无外键视图或权限不足：退命名约定 */ }
            m.Edges = ErdModel.InferEdgesDeep(m, fkRows);
            ErdModel.AutoLayout(m);
            Place(m);
            _hint.Text = $"已生成 {m.Tables.Count} 张表、{m.Edges.Count} 条关系线（{ModelLayerInfo.Name(_doc.Current)}层）。";
        }
        catch (Exception ex) { _hint.Text = "生成失败：" + ex.Message; }
    }

    /// <summary>批次五格4：从 SQL 导入——DdlParser 全量解析 CREATE TABLE（列/类型/主键/外键），并入当前层模型。</summary>
    private static async Task ImportSqlAsync()
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            Text = "",
            FontFamily = new FontFamily("Consolas"),
            MinWidth = 620,
            MinHeight = 320,
            MaxHeight = 420,
            TextWrapping = TextWrapping.NoWrap,
            PlaceholderText = "粘贴含 CREATE TABLE 的建表 SQL（可多条，外键一并解析）…",
        };
        var dlg = new ContentDialog
        {
            Title = "从 SQL 导入到模型",
            Content = box,
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _owner.Content.XamlRoot,
        };
        if (await dlg.ShowThemedAsync() != ContentDialogResult.Primary) return;
        var entities = DdlParser.Parse(box.Text);
        if (entities.Count == 0) { _hint.Text = "没有解析到 CREATE TABLE，请检查 SQL。"; return; }
        var m = _doc.CurrentModel;
        var byName = m.Tables.ToDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);
        foreach (var ent in entities)
        {
            if (byName.ContainsKey(ent.Name)) continue;
            var t = new ErdTable
            {
                Name = ent.Name,
                Columns = ent.Columns.Select(c => new ErdColumn { Name = c.Name, Type = c.Type, IsKey = c.IsKey }).ToList(),
            };
            m.Tables.Add(t);
            byName[ent.Name] = t;
        }
        // 外键关系线：DdlForeignKey → ErdEdge（from=外键侧鸦爪，to=主键侧双线）
        foreach (var ent in entities)
        {
            if (!byName.TryGetValue(ent.Name, out var from)) continue;
            foreach (var fk in ent.ForeignKeys)
            {
                if (!byName.TryGetValue(fk.RefTable, out var to)) continue;
                if (m.Edges.Any(e => string.Equals(e.FromTable, from.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.FromCol, fk.Column, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.ToTable, to.Name, StringComparison.OrdinalIgnoreCase))) continue;
                m.Edges.Add(new ErdEdge
                {
                    FromTable = from.Name,
                    FromCol = fk.Column,
                    ToTable = to.Name,
                    FromSymbol = ErdEdgeSymbol.CrowFoot,
                    ToSymbol = ErdEdgeSymbol.DoubleLine,
                });
            }
        }
        ErdModel.AutoLayout(m);
        Place(m);
        AppLog.Write($"[模型] 导入实体={entities.Count}");   // 批次五格4 验收锚点
        _hint.Text = $"已从 SQL 导入 {entities.Count} 个实体、{m.Edges.Count} 条关系线（{ModelLayerInfo.Name(_doc.Current)}层）。";
    }

    /// <summary>批次五格2：模型比较——当前层模型 vs 真库 GetColumns，出差异清单（新增/缺失/类型变更）。</summary>
    private static async Task CompareAsync()
    {
        if (_prov is null || string.IsNullOrEmpty(_db)) { _hint.Text = "模型比较需要真库上下文，请先选好库。"; return; }
        if (_doc.CurrentModel.Tables.Count == 0) { _hint.Text = "当前层模型为空，先「生成」或「从 SQL 导入」。"; return; }
        try
        {
            var live = new List<(string Table, List<ColumnSpec> Cols)>();
            var tables = await _prov.GetChildren(_db);
            int limit = Math.Min(tables.Count, 40);
            for (int i = 0; i < limit; i++)
            {
                List<ColumnSpec> cols;
                try { cols = await _prov.GetColumns(_db, tables[i]); }
                catch { continue; }
                if (cols.Count == 0) continue;
                live.Add((tables[i], cols));
            }
            var diffs = ModelCompare.Diff(_doc.CurrentModel, live);
            AppLog.Write($"[模型比较] 差异={diffs.Count}");   // 批次五格2 验收锚点
            var sb = new System.Text.StringBuilder();
            if (diffs.Count == 0) sb.Append("模型与库内结构一致，无差异。");
            foreach (var d in diffs.Take(200)) sb.AppendLine(d.ToString());
            if (diffs.Count > 200) sb.AppendLine($"…（其余 {diffs.Count - 200} 条省略）");
            var box = new TextBox
            {
                AcceptsReturn = true,
                IsReadOnly = true,
                Text = sb.ToString(),
                FontFamily = new FontFamily("Consolas"),
                MinWidth = 620,
                MinHeight = 320,
                MaxHeight = 420,
                TextWrapping = TextWrapping.Wrap,
            };
            await new ContentDialog
            {
                Title = $"模型比较 · {diffs.Count} 条差异",
                Content = box,
                CloseButtonText = "关闭",
                XamlRoot = _owner.Content.XamlRoot,
            }.ShowThemedAsync();
            _hint.Text = $"模型比较完成：{diffs.Count} 条差异（{ModelLayerInfo.Name(_doc.Current)}层 vs {_db}）。";
        }
        catch (Exception ex) { _hint.Text = "模型比较失败：" + ex.Message; }
    }

    /// <summary>批次五格1：保存三层模型文档（DataModelDoc JSON，与 ERD 布局文件同源格式）。</summary>
    private static async Task SaveModelAsync()
    {
        if (_doc.Conceptual.Tables.Count == 0 && _doc.Logical.Tables.Count == 0 && _doc.Physical.Tables.Count == 0)
        {
            _hint.Text = "模型还是空的，先「生成」或「从 SQL 导入」再保存。";
            return;
        }
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeChoices.Add("TaoDBM 三层模型", new List<string> { ".modeljson" });
            picker.SuggestedFileName = $"TaoDBM-模型-{DateTime.Now:yyyyMMdd}";
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
            var f = await picker.PickSaveFileAsync();
            if (f is null) return;
            await Windows.Storage.FileIO.WriteTextAsync(f, _doc.ToJson());
            _hint.Text = "模型已保存：" + f.Path;
        }
        catch (Exception ex) { _hint.Text = "保存失败：" + ex.Message; }
    }

    /// <summary>批次五格1：打开三层模型文档，载入后停在文档记录的当前层。</summary>
    private static async Task OpenModelAsync()
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".modeljson");
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            var text = await Windows.Storage.FileIO.ReadTextAsync(f);
            var doc = DataModelDoc.FromJson(text);
            if (doc is null) { _hint.Text = "模型文件无效。"; return; }
            _doc = doc;
            Place(_doc.CurrentModel);
            _hint.Text = $"已载入模型，当前层 {ModelLayerInfo.Name(_doc.Current)}：{_doc.CurrentModel.Tables.Count} 张表、{_doc.CurrentModel.Edges.Count} 条关系线。";
        }
        catch (Exception ex) { _hint.Text = "打开失败：" + ex.Message; }
    }

    /// <summary>批次五格5：打印——当前层模型转 HTML（内联样式，实体表＋关系清单），写临时文件后系统打开打印。</summary>
    private static async Task PrintAsync()
    {
        var m = _doc.CurrentModel;
        if (m.Tables.Count == 0) { _hint.Text = "当前层模型为空，先「生成」或「从 SQL 导入」。"; return; }
        var sb = new System.Text.StringBuilder();
        sb.Append("<html><head><meta charset=\"utf-8\"><title>TaoDBM 模型</title></head><body style=\"font-family:'Microsoft YaHei',sans-serif;\">");
        sb.Append("<h2>TaoDBM 三层模型 · ").Append(ModelLayerInfo.Name(_doc.Current)).Append("层</h2>");
        foreach (var t in m.Tables)
        {
            sb.Append("<h3>").Append(System.Net.WebUtility.HtmlEncode(t.Name)).Append("</h3><table border=\"1\" cellspacing=\"0\" cellpadding=\"4\" style=\"border-collapse:collapse;font-size:12px;\">");
            foreach (var c in t.Columns)
                sb.Append("<tr><td>").Append(c.IsKey ? "🔑 " : "").Append(System.Net.WebUtility.HtmlEncode(c.Name))
                  .Append("</td><td>").Append(System.Net.WebUtility.HtmlEncode(c.Type)).Append("</td></tr>");
            sb.Append("</table>");
        }
        if (m.Edges.Count > 0)
        {
            sb.Append("<h3>关系</h3><ul style=\"font-size:12px;\">");
            foreach (var e in m.Edges)
                sb.Append("<li>").Append(System.Net.WebUtility.HtmlEncode($"{e.FromTable}.{e.FromCol} → {e.ToTable}")).Append("</li>");
            sb.Append("</ul>");
        }
        sb.Append("</body></html>");
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"TaoDBM-模型-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            await System.IO.File.WriteAllTextAsync(path, sb.ToString(), System.Text.Encoding.UTF8);
            var psi = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
            AppLog.Write("[模型] 打印");   // 批次五格5 验收锚点
            _hint.Text = "已生成打印页并在系统默认浏览器打开（Ctrl+P 打印，画布完整不裁切）。";
        }
        catch (Exception ex) { _hint.Text = "打印失败：" + ex.Message; }
    }

    /// <summary>批次五格1：物理层 DDL 只读预览（复用 ErdForward 正向生成）。</summary>
    private static async Task PreviewDdlAsync()
    {
        var m = _doc.CurrentModel;
        if (m.Tables.Count == 0) { _hint.Text = "当前层模型为空，先「生成」或「从 SQL 导入」。"; return; }
        var r = ErdForward.Generate(m);
        if (!r.Ok || r.Ddl.Count == 0) { _hint.Text = "生成失败：" + string.Join("；", r.Errors); return; }
        var box = new TextBox
        {
            AcceptsReturn = true,
            IsReadOnly = true,
            Text = string.Join("\n\n", r.Ddl),
            FontFamily = new FontFamily("Consolas"),
            MinWidth = 640,
            MinHeight = 420,
            TextWrapping = TextWrapping.NoWrap,
        };
        await new ContentDialog
        {
            Title = "物理层 · 正向生成建库 DDL",
            Content = box,
            CloseButtonText = "关闭",
            XamlRoot = _owner.Content.XamlRoot,
        }.ShowThemedAsync();
        _hint.Text = $"已按物理层模型生成 {r.Ddl.Count} 条 DDL（只读预览，可复制到查询窗口执行）。";
    }

    private static void Place(ErdModel m)
    {
        _canvas.Children.Clear();
        _canvas.Children.Add(_hint);
        Canvas.SetZIndex(_hint, 100);
        _boxes.Clear();
        foreach (var t in m.Tables) _boxes[t.Name] = AddBox(t);
        DrawEdges(m);
    }

    private static FrameworkElement AddBox(ErdTable t)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in t.Columns.Take(12))
            sb.AppendLine((c.IsKey ? "🔑 " : "    ") + c.Name + "  " + c.Type + (t.Columns.Count > 12 && c == t.Columns[11] ? "  …" : ""));
        var box = new Border
        {
            Background = Ant.Card,
            BorderBrush = Ant.Primary,
            BorderThickness = new Thickness(1.5),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusLg),
            Padding = new Thickness(10, 6, 10, 6),
            MinWidth = 180,
            MaxWidth = 260,
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = t.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = Ant.Primary,
            Margin = new Thickness(0, 0, 0, 4),
        });
        stack.Children.Add(new TextBlock { Text = sb.ToString(), FontSize = AntTokens.FontMinor, LineHeight = 16, Foreground = Ant.TextSub });
        box.Child = stack;

        double startX = 0, startY = 0, baseX = t.X, baseY = t.Y;
        bool dragging = false;
        box.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(box).Properties.IsLeftButtonPressed)
            {
                var pos = e.GetCurrentPoint(_canvas).Position;
                startX = pos.X; startY = pos.Y; baseX = t.X; baseY = t.Y;
                dragging = true;
                box.CapturePointer(e.Pointer);
            }
        };
        box.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var pos = e.GetCurrentPoint(_canvas).Position;
            t.X = Math.Max(0, baseX + pos.X - startX);
            t.Y = Math.Max(0, baseY + pos.Y - startY);
            Canvas.SetLeft(box, t.X);
            Canvas.SetTop(box, t.Y);
            DrawEdges(_doc.CurrentModel);
        };
        box.PointerReleased += (_, e) => { dragging = false; box.ReleasePointerCapture(e.Pointer); };

        Canvas.SetLeft(box, t.X);
        Canvas.SetTop(box, t.Y);
        Canvas.SetZIndex(box, 10);
        _canvas.Children.Add(box);
        return box;
    }

    private static void DrawEdges(ErdModel m)
    {
        foreach (var child in _canvas.Children.OfType<Line>().ToList()) _canvas.Children.Remove(child);
        foreach (var lbl in _canvas.Children.OfType<Border>().Where(b => ReferenceEquals(b.Tag, _edgeLabelTag)).ToList())
            _canvas.Children.Remove(lbl);
        foreach (var e in m.Edges)
        {
            if (!_boxes.TryGetValue(e.FromTable, out var from) || !_boxes.TryGetValue(e.ToTable, out var to)) continue;
            var a = new Point(Canvas.GetLeft(from) + 90, Canvas.GetTop(from) + 20);
            var b = new Point(Canvas.GetLeft(to) + 90, Canvas.GetTop(to) + 20);
            var line = new Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                Stroke = Ant.Primary,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 2 },
            };
            _canvas.Children.Add(line);
            Canvas.SetZIndex(line, 1);
            var lbl = new Border
            {
                Tag = _edgeLabelTag,
                Background = Ant.Card,
                BorderBrush = Ant.Border,
                BorderThickness = new Thickness(0.5),
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusSm),
                Padding = new Thickness(3, 0, 3, 0),
                Child = new TextBlock { Text = $"N:1 {e.FromCol}", FontSize = 10, Foreground = Ant.TextSub },
            };
            _canvas.Children.Add(lbl);
            Canvas.SetLeft(lbl, (a.X + b.X) / 2 - 24);
            Canvas.SetTop(lbl, (a.Y + b.Y) / 2 - 9);
            Canvas.SetZIndex(lbl, 2);
        }
    }

    private static readonly object _edgeLabelTag = new();
}
