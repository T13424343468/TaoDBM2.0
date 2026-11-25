using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TaoDBM.Core;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;

namespace TaoDBM.UI;

/// <summary>ER 图对话框：加载库内表结构成图，拖拽排布，导出 PNG，保存 / 打开布局。</summary>
public static class ErdDialog
{
    private static Canvas _canvas = null!;
    private static ErdModel _model = new();
    private static readonly Dictionary<string, FrameworkElement> _boxes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _pkOf = new(StringComparer.OrdinalIgnoreCase);
    private static TextBlock _hint = null!;
    private static ComboBox _connBox = null!;
    // 格14：画布缩放状态（Ctrl+滚轮 0.5–2.0）
    private static double _zoom = 1.0;
    private static readonly ScaleTransform _scaleTf = new() { ScaleX = 1.0, ScaleY = 1.0 };
    private static readonly object _edgeLabelTag = new();
    // 批次五格3：端点符号图形统一 Tag，重绘时同步清理
    private static readonly object _edgeSymbolTag = new();

    public static async Task Show(MainWindow owner, List<ConnectionProfile> profiles, ConnectionProfile? preselect)
    {
        var relational = profiles.Where(p => p.Engine is not (DbEngine.MongoDB or DbEngine.Redis)).ToList();
        if (relational.Count == 0)
        {
            await new MessageDialogHelper(owner).Show("模型设计（ER 图）",
                "ER 图基于关系型连接生成，请先新建 MySQL / PostgreSQL / SQL Server / SQLite / Oracle 连接。");
            return;
        }

        _canvas = new Canvas { Background = Ant.Card, MinHeight = 560, MinWidth = 760 };
        _canvas.RenderTransform = _scaleTf;
        _canvas.PointerWheelChanged += (_, e) =>
        {
            // 格14：按住 Ctrl 滚轮缩放（0.5–2.0），不按 Ctrl 走默认滚动
            if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
            _zoom = Math.Clamp(_zoom + (e.GetCurrentPoint(_canvas).Properties.MouseWheelDelta > 0 ? 0.1 : -0.1), 0.5, 2.0);
            _scaleTf.ScaleX = _zoom;
            _scaleTf.ScaleY = _zoom;
            e.Handled = true;
        };
        _boxes.Clear();
        _model = new ErdModel();
        _hint = new TextBlock
        {
            Foreground = Ant.TextSub,
            FontSize = AntTokens.FontMinor,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 10, 0, 0),
            Text = "选连接与库后点「生成 ER 图」；框可拖拽，线是推断出的主外键关系。",
        };

        var connBox = new ComboBox { MinWidth = 200, PlaceholderText = "连接" };
        foreach (var p in relational) connBox.Items.Add(p);
        if (preselect is not null && relational.Contains(preselect)) connBox.SelectedItem = preselect;
        var dbBox = new ComboBox { MinWidth = 160, PlaceholderText = "库" };
        connBox.SelectionChanged += async (_, _) =>
        {
            var p = connBox.SelectedItem as ConnectionProfile;
            if (p is null) return;
            dbBox.Items.Clear();
            try
            {
                using var prov = ProviderFactory.Create(p);
                foreach (var d in await prov.GetDatabases()) dbBox.Items.Add(d);
                if (dbBox.Items.Count > 0) dbBox.SelectedIndex = 0;
            }
            catch { /* 状态区提示 */ }
        };
        if (connBox.SelectedItem is ConnectionProfile selP)
        {
            await Safe.RunAsync(async () =>
            {
                using var prov0 = ProviderFactory.Create(selP);
                foreach (var d in await prov0.GetDatabases()) dbBox.Items.Add(d);
                if (dbBox.Items.Count > 0) dbBox.SelectedIndex = 0;
            }, "ER图默认库列表");
        }

        var genBtn = new Button { Content = "生成 ER 图", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        // 格14：工具栏二级分组（文件：保存/打开/PNG；画布：生成/逆向/布局；DDL：预览/进查询窗口）
        MenuFlyoutItem SubItem(string text, string id, Action onClick)
        {
            var it = new MenuFlyoutItem { Text = text };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(it, id);
            it.Click += (_, _) => onClick();
            return it;
        }
        var fileMenu = new MenuFlyoutSubItem { Text = "文件" };
        fileMenu.Items.Add(SubItem("保存布局", "ErdFile_Save", async () => await SaveLayout(owner)));
        fileMenu.Items.Add(SubItem("打开布局", "ErdFile_Open", async () => await OpenLayout(owner)));
        fileMenu.Items.Add(SubItem("导出 PNG", "ErdFile_Png", async () => await ExportPng(owner)));
        var canvasMenu = new MenuFlyoutSubItem { Text = "画布" };
        canvasMenu.Items.Add(SubItem("生成 ER 图", "ErdCanvas_Gen", async () =>
        {
            var sel = await Selected();
            if (sel is null) return;
            await Build(sel.Value.Db, sel.Value.Prov);
        }));
        canvasMenu.Items.Add(SubItem("逆向拉取（差异预览）", "ErdCanvas_Rev", async () =>
        {
            var sel = await Selected();
            if (sel is null) return;
            await ReverseDiff(owner, sel.Value.Db, sel.Value.Prov);
        }));
        canvasMenu.Items.Add(SubItem("重新布局", "ErdCanvas_Layout", () =>
        {
            ErdModel.AutoLayout(_model);
            Place(_model);
        }));
        var ddlMenu = new MenuFlyoutSubItem { Text = "DDL" };
        ddlMenu.Items.Add(SubItem("预览 DDL", "ErdDdl_Preview", async () => await PreviewDdlAsync(owner)));
        ddlMenu.Items.Add(SubItem("进查询窗口", "ErdToQuery", () => SendDdlToQuery(owner)));
        var barFlyout = new MenuFlyout();
        barFlyout.Items.Add(fileMenu);
        barFlyout.Items.Add(canvasMenu);
        barFlyout.Items.Add(ddlMenu);
        var menuBtn = new Button { Content = "操作 ▾", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        menuBtn.Flyout = barFlyout;
        // 格14：Ctrl+滚轮缩放 0.5–2.0 ＋ 适应窗口
        var fitBtn = new Button { Content = "适应窗口", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(fitBtn, "ErdZoom");
        fitBtn.Click += (_, _) => FitToWindow();
        _connBox = connBox;

        Task<(string Db, IDbProvider Prov)?> Selected()
        {
            var p = connBox.SelectedItem as ConnectionProfile;
            var db = dbBox.SelectedItem as string;
            if (p is null || string.IsNullOrEmpty(db))
            {
                _hint.Text = "请先选好连接与库。";
                return Task.FromResult<(string, IDbProvider)?>(null);
            }
            return Task.FromResult<(string, IDbProvider)?>(new ValueTuple<string, IDbProvider>(db, ProviderFactory.Create(p)));
        }

        genBtn.Click += async (_, _) =>
        {
            var sel = await Selected();
            if (sel is null) return;
            await Build(sel.Value.Db, sel.Value.Prov);
        };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(connBox);
        bar.Children.Add(dbBox);
        bar.Children.Add(genBtn);
        bar.Children.Add(menuBtn);
        bar.Children.Add(fitBtn);

        var panel = new StackPanel { Spacing = 4, MinWidth = 800, MinHeight = 640 };
        panel.Children.Add(bar);
        panel.Children.Add(_hint);
        var border = new Border
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
        };
        panel.Children.Add(border);

        var dlg = new ContentDialog
        {
            Title = "模型设计 · ER 图",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
    }

    private static async Task Build(string db, IDbProvider prov)
    {
        _model = new ErdModel();
        _boxes.Clear();
        _canvas.Children.Clear();
        _canvas.Children.Add(_hint);
        Canvas.SetZIndex(_hint, 100);
        try
        {
            var tables = await prov.GetChildren(db);
            int limit = Math.Min(tables.Count, 40);
            for (int i = 0; i < limit; i++)
            {
                var name = tables[i];
                List<ColumnSpec> cols;
                try { cols = await prov.GetColumns(db, name); }
                catch { continue; }
                if (cols.Count == 0) continue;
                _model.Tables.Add(ErdModel.FromSpecs(name, cols));
                _pkOf[name] = cols.FirstOrDefault(c => c.IsKey)?.Name ?? "";
            }
            if (_model.Tables.Count == 0) { _hint.Text = "该库没有可用表。"; return; }
            // 深度推断：先取真库外键清单（目录引擎），失败退命名约定
            List<object?[]>? fkRows = null;
            try
            {
                var fkSql = ObjectTreeCatalog.ListForeignKeys(prov.Profile.Engine, db);
                var fkRs = await prov.Execute(db, fkSql);
                fkRows = fkRs.Count > 0 ? fkRs[0].Rows : null;
            }
            catch { /* 无外键视图或权限不足：退命名约定 */ }
            _model.Edges = ErdModel.InferEdgesDeep(_model, fkRows);
            ErdModel.AutoLayout(_model);
            Place(_model);
            int fkReal = fkRows?.Count ?? 0;
            _hint.Text = $"已生成 {_model.Tables.Count} 张表、{_model.Edges.Count} 条关系线（真库外键 {fkReal} 条，最多取前 40 张表）。";
        }
        catch (Exception ex) { _hint.Text = HumanError.Say(ex, "生成失败"); }
    }

    // s6 逆向拉取：先出差异预览（新增表 / 结构变化 / 仅模型有），确认后才并入模型，不直接覆盖。
    private static async Task ReverseDiff(MainWindow owner, string db, IDbProvider prov)
    {
        if (_model.Tables.Count == 0) { _hint.Text = "当前模型为空，请先用「生成 ER 图」载入，再做逆向差异比对。"; return; }
        List<string> live;
        try { live = await prov.GetChildren(db); }
        catch (Exception ex) { _hint.Text = HumanError.Say(ex, "逆向拉取失败"); return; }

        var byName = _model.Tables.ToDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);
        var liveSet = live.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<(string Name, List<ColumnSpec> Cols)>();
        var changed = new List<(ErdTable T, List<ColumnSpec> Cols, List<string> Delta)>();
        int limit = Math.Min(live.Count, 40);
        for (int i = 0; i < limit; i++)
        {
            List<ColumnSpec> cols;
            try { cols = await prov.GetColumns(db, live[i]); }
            catch { continue; }
            if (cols.Count == 0) continue;
            if (!byName.TryGetValue(live[i], out var t)) { added.Add((live[i], cols)); continue; }
            var delta = new List<string>();
            var liveCols = cols.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
            var modelCols = t.Columns.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
            foreach (var c in cols)
            {
                if (!modelCols.TryGetValue(c.Name, out var m)) { delta.Add($"+{c.Name} {c.DataType}（新增列）"); continue; }
                if (!string.Equals(m.Type, c.DataType, StringComparison.OrdinalIgnoreCase) || m.IsKey != c.IsKey)
                    delta.Add($"~{c.Name} {m.Type}→{c.DataType}{(m.IsKey != c.IsKey ? (c.IsKey ? "、升为主键" : "、取消主键") : "")}");
            }
            foreach (var c in t.Columns)
                if (!liveCols.ContainsKey(c.Name)) delta.Add($"-{c.Name} {c.Type}（移除列）");
            if (delta.Count > 0) changed.Add((t, cols, delta));
        }
        var modelOnly = _model.Tables.Where(t => !liveSet.Contains(t.Name)).Select(t => t.Name).ToList();
        if (added.Count == 0 && changed.Count == 0 && modelOnly.Count == 0)
        {
            _hint.Text = "逆向拉取完成：模型与库内结构一致，无差异。";
            return;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var (name, cols) in added) sb.AppendLine($"【新增表】{name}（{cols.Count} 列）");
        foreach (var (t, _, delta) in changed) sb.AppendLine($"【结构变化】{t.Name}：{string.Join("；", delta)}");
        if (modelOnly.Count > 0) sb.AppendLine($"【仅模型有】{string.Join("、", modelOnly)}（库中不存在，保留不删）");
        sb.AppendLine();
        sb.AppendLine($"共 {added.Count} 张新增、{changed.Count} 张结构变化。应用后仅并入新增与更新，不删除模型中的表。");

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
        var diffCount = added.Count + changed.Count;
        var dlg = new ContentDialog
        {
            Title = "逆向拉取 · 差异预览",
            Content = box,
            PrimaryButtonText = $"应用差异到模型（{diffCount} 项）",
            IsPrimaryButtonEnabled = diffCount > 0,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = owner.Content.XamlRoot,
        };
        if (await dlg.ShowThemedAsync() != ContentDialogResult.Primary) { _hint.Text = "已取消，模型未改动。"; return; }

        double maxX = _model.Tables.Count > 0 ? _model.Tables.Max(t => t.X) : 0;
        double y = 40;
        foreach (var (name, cols) in added)
        {
            var nt = ErdModel.FromSpecs(name, cols);
            nt.X = maxX + 300;
            nt.Y = y;
            y += 260;
            _model.Tables.Add(nt);
        }
        foreach (var (t, cols, _) in changed)
            t.Columns = cols.Select(c => new ErdColumn { Name = c.Name, Type = c.DataType, IsKey = c.IsKey }).ToList();
        _model.Edges = ErdModel.InferEdges(_model);
        Place(_model);
        _hint.Text = $"已并入 {added.Count} 张新增表、更新 {changed.Count} 张表结构；关系线已按新结构重新推断。";
    }

    private static void Place(ErdModel m)
    {
        _canvas.Children.Clear();
        _canvas.Children.Add(_hint);
        Canvas.SetZIndex(_hint, 100);
        _boxes.Clear();
        foreach (var t in m.Tables) _boxes[t.Name] = AddBox(t);
        DrawEdges();
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
            DrawEdges();
        };
        box.PointerReleased += (_, e) => { dragging = false; box.ReleasePointerCapture(e.Pointer); };

        Canvas.SetLeft(box, t.X);
        Canvas.SetTop(box, t.Y);
        Canvas.SetZIndex(box, 10);
        _canvas.Children.Add(box);
        return box;
    }

    private static void DrawEdges()
    {
        foreach (var child in _canvas.Children.OfType<Line>().ToList()) _canvas.Children.Remove(child);
        // 格14：同步清掉上一轮的关系标签
        foreach (var lbl in _canvas.Children.OfType<Border>().Where(b => ReferenceEquals(b.Tag, _edgeLabelTag)).ToList())
            _canvas.Children.Remove(lbl);
        // 批次五格3：同步清掉上一轮的端点符号图形
        foreach (var sym in _canvas.Children.OfType<Shape>().Where(s => ReferenceEquals(s.Tag, _edgeSymbolTag)).ToList())
            _canvas.Children.Remove(sym);
        foreach (var e in _model.Edges)
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
            // 批次五格3：按线方向在 a/b 端附近渲染端点符号（鸦爪三叉/圆圈/双线）
            var dirA = UnitTo(a, b);
            var dirB = UnitTo(b, a);
            DrawSymbol(a, dirA, e.FromSymbol);
            DrawSymbol(b, dirB, e.ToSymbol);
            // 格14：线中点 1:N/N:1 白底小标签（from=外键侧为 N，to=主键侧为 1）
            var lbl = new Border
            {
                Tag = _edgeLabelTag,
                Background = Ant.Card,
                BorderBrush = Ant.Border,
                BorderThickness = new Thickness(0.5),
                CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusSm),
                Padding = new Thickness(3, 0, 3, 0),
                Child = new TextBlock
                {
                    Text = $"N:1 {e.FromCol}",
                    FontSize = AntTokens.FontTiny,
                    Foreground = Ant.TextSub,
                },
            };
            // 批次五格3：标签点击循环切换 from 端符号（None→鸦爪→圆圈→双线），手改入口
            lbl.Tapped += (_, _) =>
            {
                e.FromSymbol = e.FromSymbol switch
                {
                    ErdEdgeSymbol.None => ErdEdgeSymbol.CrowFoot,
                    ErdEdgeSymbol.CrowFoot => ErdEdgeSymbol.Circle,
                    ErdEdgeSymbol.Circle => ErdEdgeSymbol.DoubleLine,
                    _ => ErdEdgeSymbol.None,
                };
                DrawEdges();
                AppLog.Write($"[ERD] 符号={_model.Edges.Count(x => x.FromSymbol != ErdEdgeSymbol.None || x.ToSymbol != ErdEdgeSymbol.None)}");
            };
            _canvas.Children.Add(lbl);
            Canvas.SetLeft(lbl, (a.X + b.X) / 2 - 24);
            Canvas.SetTop(lbl, (a.Y + b.Y) / 2 - 9);
            Canvas.SetZIndex(lbl, 2);
        }
    }

    /// <summary>批次五格3：a 指向 b 的单位方向向量。</summary>
    private static Point UnitTo(Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.001) return new Point(1, 0);
        return new Point(dx / len, dy / len);
    }

    /// <summary>批次五格3：在端点 p 沿 dir（指向线另一端）渲染关系符号；None 不画。</summary>
    private static void DrawSymbol(Point p, Point dir, ErdEdgeSymbol sym)
    {
        if (sym == ErdEdgeSymbol.None) return;
        var perp = new Point(-dir.Y, dir.X);
        void Add(Shape s)
        {
            s.Tag = _edgeSymbolTag;
            s.Stroke = Ant.Primary;
            s.StrokeThickness = 1.5;
            _canvas.Children.Add(s);
            Canvas.SetZIndex(s, 1);
        }
        Line Seg(double x1, double y1, double x2, double y2) => new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };
        switch (sym)
        {
            case ErdEdgeSymbol.CrowFoot:
            {
                // 鸦爪：从线上距端点 14px 的点向端点张开三条叉（0/±7px 垂直偏移）
                var q = new Point(p.X - dir.X * 14, p.Y - dir.Y * 14);
                foreach (var off in new[] { 0.0, 7.0, -7.0 })
                    Add(Seg(q.X, q.Y, p.X + perp.X * off, p.Y + perp.Y * off));
                break;
            }
            case ErdEdgeSymbol.Circle:
            {
                // 圆圈：端点内侧 7px 处半径 5 的空心圆
                var c = new Point(p.X - dir.X * 7, p.Y - dir.Y * 7);
                var el = new Ellipse { Width = 10, Height = 10 };
                Add(el);
                Canvas.SetLeft(el, c.X - 5);
                Canvas.SetTop(el, c.Y - 5);
                break;
            }
            case ErdEdgeSymbol.DoubleLine:
            {
                // 双线：端点处与端点内侧 6px 各一道垂直短笔画
                for (var d = 0.0; d <= 6.0; d += 6.0)
                {
                    var c = new Point(p.X - dir.X * d, p.Y - dir.Y * d);
                    Add(Seg(c.X - perp.X * 7, c.Y - perp.Y * 7, c.X + perp.X * 7, c.Y + perp.Y * 7));
                }
                break;
            }
        }
    }

    /// <summary>格14：适应窗口——按表包围盒与画布可视区算缩放（夹 0.5–2.0）。</summary>
    private static void FitToWindow()
    {
        if (_model.Tables.Count == 0) { _hint.Text = "先生成 ER 图再适应窗口。"; return; }
        double maxX = _model.Tables.Max(t => t.X) + 280;
        double maxY = _model.Tables.Max(t => t.Y) + 240;
        double w = _canvas.ActualWidth > 0 ? _canvas.ActualWidth : 760;
        double h = _canvas.ActualHeight > 0 ? _canvas.ActualHeight : 560;
        _zoom = Math.Clamp(Math.Min(w / maxX, h / maxY), 0.5, 2.0);
        _scaleTf.ScaleX = _zoom;
        _scaleTf.ScaleY = _zoom;
        _hint.Text = $"已适应窗口（缩放 {_zoom:0.0#}×；Ctrl+滚轮可继续缩放 0.5–2.0）。";
    }

    /// <summary>格14：正向 DDL 只读预览（原「正向生成建库 DDL」按钮口径）。</summary>
    private static async Task PreviewDdlAsync(MainWindow owner)
    {
        if (_model.Tables.Count == 0) { _hint.Text = "请先生成或载入 ER 模型，再正向生成 DDL。"; return; }
        var r = ErdForward.Generate(_model);
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
            Title = "正向生成 · 建库 DDL",
            Content = box,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        }.ShowThemedAsync();
        _hint.Text = $"已按模型生成 {r.Ddl.Count} 条 DDL（只读预览，可复制到查询窗口执行）。";
    }

    /// <summary>格14：正向 DDL 一键进查询窗口（带所选连接上下文，可直接执行）。</summary>
    private static void SendDdlToQuery(MainWindow owner)
    {
        if (_model.Tables.Count == 0) { _hint.Text = "请先生成或载入 ER 模型。"; return; }
        var r = ErdForward.Generate(_model);
        if (!r.Ok || r.Ddl.Count == 0) { _hint.Text = "生成失败：" + string.Join("；", r.Errors); return; }
        var p = _connBox.SelectedItem as ConnectionProfile;
        var info = new NodeInfo { Kind = NodeKind.Connection, Profile = p ?? new ConnectionProfile(), Name = p?.DisplayName ?? "ERD DDL" };
        owner.OpenQueryWithSql(string.Join("\n\n", r.Ddl), info);
        _hint.Text = $"已把 {r.Ddl.Count} 条 DDL 送进查询窗口（{(p is null ? "未选连接，执行前请先选库" : "已带连接上下文")}）。";
    }

    private static async Task ExportPng(MainWindow owner)
    {
        if (_model.Tables.Count == 0) { _hint.Text = "先生成 ER 图再导出。"; return; }
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeChoices.Add("PNG 图片", new List<string> { ".png" });
            picker.SuggestedFileName = $"TaoDBM-ER-{DateTime.Now:yyyyMMdd-HHmm}";
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
            var f = await picker.PickSaveFileAsync();
            if (f is null) return;

            var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await rtb.RenderAsync(_canvas);
            var pixels = await rtb.GetPixelsAsync();
            var bytes = new byte[pixels.Length];
            DataReader.FromBuffer(pixels).ReadBytes(bytes);

            var ras = await f.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, bytes);
            await encoder.FlushAsync();
            _hint.Text = $"已导出：{f.Path}（{rtb.PixelWidth}x{rtb.PixelHeight}）";
        }
        catch (Exception ex) { _hint.Text = HumanError.Say(ex, "导出失败"); }
    }

    private static async Task SaveLayout(MainWindow owner)
    {
        if (_model.Tables.Count == 0) { _hint.Text = "先布局再保存。"; return; }
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeChoices.Add("TaoDBM ER 布局", new List<string> { ".erjson" });
            picker.SuggestedFileName = $"TaoDBM-ER-布局-{DateTime.Now:yyyyMMdd}";
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
            var f = await picker.PickSaveFileAsync();
            if (f is null) return;
            await FileIO.WriteTextAsync(f, _model.ToJson());
            _hint.Text = "布局已保存：" + f.Path;
        }
        catch (Exception ex) { _hint.Text = HumanError.Say(ex, "保存失败"); }
    }

    private static async Task OpenLayout(MainWindow owner)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".erjson");
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            var text = await FileIO.ReadTextAsync(f);
            var m = ErdModel.FromJson(text);
            if (m is null || m.Tables.Count == 0) { _hint.Text = "布局文件无效。"; return; }
            _model = m;
            foreach (var t in _model.Tables)
                _pkOf[t.Name] = t.Columns.FirstOrDefault(c => c.IsKey)?.Name ?? "";
            Place(_model);
            _hint.Text = $"已载入 {_model.Tables.Count} 张表、{_model.Edges.Count} 条关系线。";
        }
        catch (Exception ex) { _hint.Text = HumanError.Say(ex, "打开失败"); }
    }
}
