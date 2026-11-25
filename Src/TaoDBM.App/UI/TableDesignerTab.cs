using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>
/// 可视化表结构设计器（U2 D-13：内嵌主窗口页签，Navicat 口径）。
/// 列 / 索引 / 外键三分区 + SQL 预览实时刷新；执行前确认，不静默改库。
/// </summary>
public class TableDesignerTab : Grid
{
    private readonly MainWindow _owner;
    private readonly IDbProvider _prov;
    private readonly string _db;
    private readonly string? _table;              // null = 新建表
    private readonly List<string> _existingNames; // 改表模式旧列名（打开时取一次，预览纯本地零 DB 往返）
    private readonly List<ColumnSpec> _existingSpecs; // 格5：改表模式旧列完整规格（diff 着色用）
    private readonly bool _isRelational;

    private sealed class ColWidgets
    {
        public TextBox Name = new() { MinWidth = 90, PlaceholderText = "列名" };
        // 台账④：类型改可编辑下拉（常用类型直选，保底手输兼容 DECIMAL(10,2) 等自定义精度）
        public ComboBox Type = new() { MinWidth = 110, IsEditable = true, PlaceholderText = "类型" };
        public TextBox Def = new() { MinWidth = 80, PlaceholderText = "默认值" };
        public CheckBox Pk = new() { Content = "主键", MinWidth = 56, VerticalAlignment = VerticalAlignment.Center };
        public CheckBox Null = new() { Content = "可空", MinWidth = 56, IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        public CheckBox Inc = new() { Content = "自增", MinWidth = 56, VerticalAlignment = VerticalAlignment.Center };
        public Button Del = new() { Content = "✕", Padding = new Thickness(6, 2, 6, 2), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        public Grid? RowHost;   // 格5：该行所在 Grid，用于逐列 diff 着色
    }

    private sealed class IdxWidgets
    {
        public TextBox Name = new() { MinWidth = 120, PlaceholderText = "索引名" };
        public TextBox Cols = new() { MinWidth = 160, PlaceholderText = "列，逗号分隔" };
        public CheckBox Uni = new() { Content = "唯一", MinWidth = 56, VerticalAlignment = VerticalAlignment.Center };
        public Button Del = new() { Content = "✕", Padding = new Thickness(6, 2, 6, 2), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
    }

    private sealed class FkWidgets
    {
        public TextBox Name = new() { MinWidth = 120, PlaceholderText = "约束名" };
        public TextBox Cols = new() { MinWidth = 110, PlaceholderText = "本表列" };
        public TextBox RefTable = new() { MinWidth = 110, PlaceholderText = "引用表" };
        public TextBox RefCols = new() { MinWidth = 110, PlaceholderText = "引用列" };
        public ComboBox OnDel = FkCombo("删除行为");
        public ComboBox OnUpd = FkCombo("更新行为");
        public Button Del = new() { Content = "✕", Padding = new Thickness(6, 2, 6, 2), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };

        internal static ComboBox FkCombo(string header)
        {
            var cb = new ComboBox { MinWidth = 100, Header = header, ItemsSource = new List<string> { "（不指定）", "CASCADE", "SET NULL", "RESTRICT" }, SelectedIndex = 0 };
            return cb;
        }
    }

    private readonly StackPanel _colHost = new() { Spacing = 4 };
    private readonly StackPanel _idxHost = new() { Spacing = 4 };
    private readonly StackPanel _fkHost = new() { Spacing = 4 };
    private readonly List<ColWidgets> _colRows = new();
    private readonly List<IdxWidgets> _idxRows = new();
    private readonly List<FkWidgets> _fkRows = new();

    private readonly TextBox _nameBox = new() { PlaceholderText = "表名", MinWidth = 200, IsReadOnly = false };
    private readonly TextBox _preview = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = (FontFamily)Application.Current.Resources["AntMono"],
        FontSize = AntTokens.FontMinor,
    };
    private readonly TextBlock _status = new() { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
    private TextBlock _diffHint = new();   // 格5：删除列红字提示
    private readonly Button _runBtn = new()
    {
        Content = "执行",
        Background = Ant.Primary,
        Foreground = Ant.OnPrimary,
        BorderThickness = new Thickness(0),
        CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
    };
    private bool _busy;

    public TableDesignerTab(MainWindow owner, IDbProvider prov, string db, string? table, List<ColumnSpec>? existing)
    {
        _owner = owner;
        _prov = prov;
        _db = db;
        _table = table;
        _isRelational = prov is RelationalProvider;
        _existingNames = existing?.Select(c => c.Name).ToList() ?? new List<string>();
        _existingSpecs = existing ?? new List<ColumnSpec>();

        Background = Ant.Bg;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 工具栏
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // 分区
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // SQL 预览
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 状态行

        // 工具栏：表名 + 引擎口径 + 执行
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(10, 8, 10, 8) };
        _nameBox.Text = table ?? "";
        if (table != null) _nameBox.IsReadOnly = true;   // 改表不改名，对齐 Navicat
        bar.Children.Add(new TextBlock { Text = "表名", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_nameBox);
        bar.Children.Add(new TextBlock
        {
            Text = $"引擎：{prov.EngineName}{(table != null ? " · 改表模式" : " · 新建表")}",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.TextSub,
        });
        bar.Children.Add(_runBtn);
        Children.Add(bar);
        SetRow(bar, 0);

        // 三分区（Navicat 口径：列 / 索引 / 外键）
        if (!_isRelational)
        {
            var nosql = new TextBlock
            {
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap,
                Text = "仅关系型数据库支持可视化建表 / 改表。",
            };
            Children.Add(nosql);
            SetRow(nosql, 1);
            _runBtn.IsEnabled = false;
            return;
        }

        var pivot = new Pivot { Margin = new Thickness(8, 0, 8, 0) };
        pivot.Items.Add(MakeSection("列", _colHost, "＋ 加一列", () => AddColRow(null)));
        pivot.Items.Add(MakeSection("索引", _idxHost, "＋ 加索引", () => AddIdxRow(null)));
        pivot.Items.Add(MakeSection("外键", _fkHost, "＋ 加外键", () => AddFkRow(null)));
        Children.Add(pivot);
        SetRow(pivot, 1);

        if (existing is { Count: > 0 })
            // 发现74/75：旧列的自增 / 默认值 / 注释原样回填，改表 MODIFY 带回不再静默丢
            foreach (var c in existing)
                AddColRow(new TableColumnDesign { Name = c.Name, SqlType = c.DataType, Nullable = c.Nullable, IsPrimaryKey = c.IsKey, AutoIncrement = c.IsAutoIncrement, DefaultValue = c.DefaultValue, Comment = c.Comment });
        else AddColRow(null);
        if (table != null)
        {
            // 格5：改表模式异步拉取现有索引 / 外键清单，只读行＋删除勾选，勾选生成 DROP 并入语句。
            _ = LoadExistingConstraintsAsync();
        }

        // SQL 预览（实时）
        var previewPanel = new StackPanel { Spacing = 4, Margin = new Thickness(10, 4, 10, 4) };
        previewPanel.Children.Add(new TextBlock { Text = "SQL 预览（随编辑实时刷新）", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        // 格5：逐列 diff 提示——删除列（旧结构有而当前行无）用红字提示，随编辑刷新。
        _diffHint = new TextBlock
        {
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.Error,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        previewPanel.Children.Add(_diffHint);
        previewPanel.Children.Add(_preview);
        Children.Add(previewPanel);
        SetRow(previewPanel, 2);

        Children.Add(_status);
        SetRow(_status, 3);
        _status.Margin = new Thickness(12, 2, 12, 8);

        _runBtn.Click += (_, _) => _ = RunAsync();
        RefreshPreview();
    }

    private static PivotItem MakeSection(string header, StackPanel host, string addText, Action add)
    {
        var addBtn = new Button { Content = addText, CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Margin = new Thickness(0, 0, 0, 6) };
        addBtn.Click += (_, _) => add();
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(addBtn);
        panel.Children.Add(host);
        return new PivotItem { Header = header, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
    }

    // ---------- 行构建 ----------

    private void AddColRow(TableColumnDesign? c)
    {
        var w = new ColWidgets();
        w.Type.ItemsSource = TableDdl.TypeChoices(_prov.Profile.Engine);   // 台账④：按引擎填类型清单
        if (c != null)
        {
            w.Name.Text = c.Name;
            w.Type.Text = c.SqlType;
            w.Pk.IsChecked = c.IsPrimaryKey;
            w.Null.IsChecked = c.Nullable;
            w.Inc.IsChecked = c.AutoIncrement;
            w.Def.Text = c.DefaultValue;
        }
        UpdateDefPlaceholder(w);   // 台账⑤：默认值占位随类型
        w.Del.Click += (_, _) =>
        {
            if (_colRows.Count <= 1) return;
            if (w.Name.Parent is FrameworkElement fe) _colHost.Children.Remove(fe);
            _colRows.Remove(w);
            RefreshPreview();
            RefreshDiff();
        };
        Hook(w.Name, w.Def);
        HookCheck(w.Pk, w.Null, w.Inc);
        w.Name.TextChanged += (_, _) => RefreshDiff();   // 格5：逐列 diff 随编辑刷新
        // 台账④⑤：类型变化（下拉选中或手输）→ 默认值占位 + 预览 + diff 全刷新
        w.Type.TextChanged += (_, _) => { UpdateDefPlaceholder(w); RefreshPreview(); RefreshDiff(); };
        w.Type.SelectionChanged += (_, _) => { UpdateDefPlaceholder(w); RefreshPreview(); RefreshDiff(); };
        w.Pk.Checked += (_, _) => RefreshDiff();
        w.Pk.Unchecked += (_, _) => RefreshDiff();
        w.Null.Checked += (_, _) => RefreshDiff();
        w.Null.Unchecked += (_, _) => RefreshDiff();
        _colRows.Add(w);
        var rowGrid = ColRow(w);
        w.RowHost = rowGrid;
        _colHost.Children.Add(rowGrid);
        RefreshDiff();
    }

    private void AddIdxRow(TableIndexDesign? ix)
    {
        var w = new IdxWidgets();
        if (ix != null)
        {
            w.Name.Text = ix.Name;
            w.Cols.Text = string.Join(", ", ix.Columns);
            w.Uni.IsChecked = ix.IsUnique;
        }
        w.Del.Click += (_, _) =>
        {
            if (w.Name.Parent is FrameworkElement fe) _idxHost.Children.Remove(fe);
            _idxRows.Remove(w);
            RefreshPreview();
        };
        Hook(w.Name, w.Cols);
        HookCheck(w.Uni);
        _idxRows.Add(w);
        _idxHost.Children.Add(IdxRow(w));
    }

    private void AddFkRow(TableForeignKeyDesign? fk)
    {
        var w = new FkWidgets();
        if (fk != null)
        {
            w.Name.Text = fk.Name;
            w.Cols.Text = string.Join(", ", fk.Columns);
            w.RefTable.Text = fk.RefTable;
            w.RefCols.Text = string.Join(", ", fk.RefColumns);
            w.OnDel.SelectedIndex = FkComboIndex(fk.OnDelete);
            w.OnUpd.SelectedIndex = FkComboIndex(fk.OnUpdate);
        }
        w.Del.Click += (_, _) =>
        {
            if (w.Name.Parent is FrameworkElement fe) _fkHost.Children.Remove(fe);
            _fkRows.Remove(w);
            RefreshPreview();
        };
        Hook(w.Name, w.Cols, w.RefTable, w.RefCols);
        w.OnDel.SelectionChanged += (_, _) => RefreshPreview();
        w.OnUpd.SelectionChanged += (_, _) => RefreshPreview();
        _fkRows.Add(w);
        _fkHost.Children.Add(FkRow(w));
    }

    private static int FkComboIndex(string v) => v switch
    {
        "CASCADE" => 1,
        "SET NULL" => 2,
        "RESTRICT" => 3,
        _ => 0,
    };

    private void Hook(params TextBox[] boxes)
    {
        foreach (var b in boxes) b.TextChanged += (_, _) => RefreshPreview();
    }

    // ---------- 台账⑤：默认值占位按类型区分 ----------

    /// <summary>默认值占位随类型：数值类提示填数字，时间类提示表达式，其余提示自动加引号。</summary>
    private static void UpdateDefPlaceholder(ColWidgets w)
    {
        var type = w.Type.Text.Trim();
        if (type.Length == 0) { w.Def.PlaceholderText = "默认值"; return; }
        if (TableDdl.IsNumericType(type)) w.Def.PlaceholderText = "数值（如 0）";
        else if (TableDdl.IsTemporalType(type)) w.Def.PlaceholderText = "如 CURRENT_TIMESTAMP";
        else w.Def.PlaceholderText = "文本（自动加引号）";
    }

    /// <summary>台账⑤：执行前校验——类型非空；数值列默认值须为数字或表达式，不再静默生成带引号字符串。</summary>
    private List<string> ValidateDefaults()
    {
        var errs = new List<string>();
        foreach (var w in _colRows)
        {
            var name = w.Name.Text.Trim();
            if (name.Length == 0) continue;
            var type = w.Type.Text.Trim();
            if (type.Length == 0) { errs.Add($"列 {name}：类型不能为空，请从下拉选择或手输。"); continue; }
            var def = w.Def.Text.Trim();
            if (def.Length == 0 || !TableDdl.IsNumericType(type)) continue;
            bool ok = def.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                || def.Contains('(') || def.Contains("::")
                || decimal.TryParse(def, System.Globalization.CultureInfo.InvariantCulture, out _);
            if (!ok) errs.Add($"列 {name}：{type} 为数值类型，默认值须为数字或表达式（如 0、1.5），文本请改列类型。");
        }
        return errs;
    }

    // ---------- 格5：逐列 diff 着色 ----------

    /// <summary>行着色：新增绿 / 修改黄 / 未变不着色；删除列在预览区上方红字提示。</summary>
    private void RefreshDiff()
    {
        if (_table == null) return;   // 新建表无 diff
        var rowNames = _colRows.Select(r => r.Name.Text.Trim()).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var w in _colRows)
        {
            if (w.RowHost == null) continue;
            var name = w.Name.Text.Trim();
            if (name.Length == 0) { w.RowHost.Background = null; continue; }
            var old = _existingSpecs.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (old == null)
                w.RowHost.Background = Ant.SuccessBg; // 新增列
            else if (!old.DataType.Trim().Equals(w.Type.Text.Trim(), StringComparison.OrdinalIgnoreCase)
                || old.Nullable != (w.Null.IsChecked != false)
                || old.IsKey != (w.Pk.IsChecked == true)
                || old.IsAutoIncrement != (w.Inc.IsChecked == true)
                || old.DefaultValue.Trim() != w.Def.Text.Trim())
                w.RowHost.Background = Ant.WarnBg;    // 修改列
            else
                w.RowHost.Background = null;          // 未变
        }
        var deleted = _existingSpecs.Where(c => c.Name.Length > 0 && !rowNames.Contains(c.Name)).Select(c => c.Name).ToList();
        if (deleted.Count > 0)
        {
            _diffHint.Text = "将删除列（丢数据）：" + string.Join("、", deleted);
            _diffHint.Visibility = Visibility.Visible;
        }
        else _diffHint.Visibility = Visibility.Collapsed;
    }

    // ---------- 格5：现有索引 / 外键清单读取（改表模式，勾选删除） ----------

    private readonly List<(CheckBox Box, string Name)> _idxChecks = new();
    private readonly List<(CheckBox Box, string Name)> _fkChecks = new();

    private async Task LoadExistingConstraintsAsync()
    {
        if (_prov.Profile.Engine == DbEngine.SQLite)
            _fkHost.Children.Add(new TextBlock { Text = "SQLite 不支持单独删除外键约束（需重建表）。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        try
        {
            var idxSql = ObjectTreeCatalog.ListIndexes(_prov.Profile.Engine, _db);
            var rs = await _prov.Execute(_db, idxSql);
            int ti = 0;
            foreach (var res in rs)
                foreach (var r in res.Rows)
                {
                    var iname = r.Length > 0 ? r[0]?.ToString() ?? "" : "";
                    if (iname.Length == 0) continue;
                    // MySQL 主键索引不能按普通索引删，跳过展示。
                    if (iname.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase) && _prov.Profile.Engine == DbEngine.MySQL) continue;
                    var box = new CheckBox { Content = "删除", MinWidth = 56, VerticalAlignment = VerticalAlignment.Center };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, $"IdxDrop_{ti}");
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(new TextBlock { Text = iname, VerticalAlignment = VerticalAlignment.Center, FontSize = AntTokens.FontMinor });
                    row.Children.Add(box);
                    _idxHost.Children.Add(row);
                    _idxChecks.Add((box, iname));
                    ti++;
                }
            if (_idxChecks.Count == 0)
                _idxHost.Children.Add(new TextBlock { Text = "该表没有可管理的索引。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        }
        catch
        {
            _idxHost.Children.Add(new TextBlock { Text = "索引清单读取失败（权限或引擎限制）。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        }
        if (_prov.Profile.Engine == DbEngine.SQLite) return;
        try
        {
            var fkSql = ObjectTreeCatalog.ListForeignKeys(_prov.Profile.Engine, _db);
            var rs = await _prov.Execute(_db, fkSql);
            int fi = 0;
            foreach (var res in rs)
            {
                // 各引擎列序不一：按列头定位 table_name / constraint_name，无列头时退位置。
                int cTable = FindCol(res, "table_name", 0);
                int cName = FindCol(res, "constraint_name", res.Columns.Count - 1);
                foreach (var r in res.Rows)
                {
                    var tname = cTable >= 0 && cTable < r.Length ? r[cTable]?.ToString() ?? "" : "";
                    var cname = cName >= 0 && cName < r.Length ? r[cName]?.ToString() ?? "" : "";
                    if (!tname.Equals(_table, StringComparison.OrdinalIgnoreCase) || cname.Length == 0) continue;
                    var box = new CheckBox { Content = "删除", MinWidth = 56, VerticalAlignment = VerticalAlignment.Center };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, $"FkDrop_{fi}");
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(new TextBlock { Text = cname, VerticalAlignment = VerticalAlignment.Center, FontSize = AntTokens.FontMinor });
                    row.Children.Add(box);
                    _fkHost.Children.Add(row);
                    _fkChecks.Add((box, cname));
                    fi++;
                }
            }
            if (_fkChecks.Count == 0)
                _fkHost.Children.Add(new TextBlock { Text = "该表没有外键。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        }
        catch
        {
            _fkHost.Children.Add(new TextBlock { Text = "外键清单读取失败（权限或引擎限制）。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        }
    }

    private static int FindCol(QueryResult res, string header, int fallback)
    {
        for (int i = 0; i < res.Columns.Count; i++)
            if (res.Columns[i].Equals(header, StringComparison.OrdinalIgnoreCase)) return i;
        return fallback;
    }

    private void HookCheck(params CheckBox[] boxes)
    {
        foreach (var b in boxes)
        {
            b.Checked += (_, _) => RefreshPreview();
            b.Unchecked += (_, _) => RefreshPreview();
        }
    }

    private static Grid ColRow(ColWidgets w)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Put(FrameworkElement el, int col)
        {
            el.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(el, col);
            g.Children.Add(el);
        }
        Put(w.Name, 0);
        Put(w.Type, 1);
        Put(w.Pk, 2);
        Put(w.Null, 3);
        Put(w.Inc, 4);
        Put(w.Def, 5);
        Put(w.Del, 6);
        return g;
    }

    private static Grid IdxRow(IdxWidgets w)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Put(FrameworkElement el, int col)
        {
            el.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(el, col);
            g.Children.Add(el);
        }
        Put(w.Name, 0);
        Put(w.Cols, 1);
        Put(w.Uni, 2);
        Put(w.Del, 3);
        return g;
    }

    private static Grid FkRow(FkWidgets w)
    {
        var g = new Grid();
        for (int i = 0; i < 6; i++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Put(FrameworkElement el, int col)
        {
            el.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(el, col);
            g.Children.Add(el);
        }
        Put(w.Name, 0);
        Put(w.Cols, 1);
        Put(w.RefTable, 2);
        Put(w.RefCols, 3);
        Put(w.OnDel, 4);
        Put(w.OnUpd, 5);
        Put(w.Del, 6);
        return g;
    }

    // ---------- 设计读取与 DDL 生成 ----------

    private TableDesign Read()
    {
        var t = new TableDesign { TableName = _nameBox.Text.Trim() };
        foreach (var w in _colRows)
            t.Columns.Add(new TableColumnDesign
            {
                Name = w.Name.Text.Trim(),
                SqlType = w.Type.Text.Trim(),
                Nullable = w.Null.IsChecked != false,
                IsPrimaryKey = w.Pk.IsChecked == true,
                AutoIncrement = w.Inc.IsChecked == true,
                DefaultValue = w.Def.Text.Trim(),
            });
        foreach (var w in _idxRows)
            t.Indexes.Add(new TableIndexDesign
            {
                Name = w.Name.Text.Trim(),
                Columns = w.Cols.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                IsUnique = w.Uni.IsChecked == true,
            });
        foreach (var w in _fkRows)
            t.ForeignKeys.Add(new TableForeignKeyDesign
            {
                Name = w.Name.Text.Trim(),
                Columns = w.Cols.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                RefTable = w.RefTable.Text.Trim(),
                RefColumns = w.RefCols.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                OnDelete = w.OnDel.SelectedIndex > 0 ? (string)w.OnDel.SelectedItem! : "",
                OnUpdate = w.OnUpd.SelectedIndex > 0 ? (string)w.OnUpd.SelectedItem! : "",
            });
        return t;
    }

    /// <summary>纯本地生成语句清单（预览与执行共用同一口径，零 DB 往返）。</summary>
    private (List<string> Stmts, List<string> Skipped, string Text) Build()
    {
        var t = Read();
        if (t.TableName.Length == 0 || t.Columns.All(c => c.Name.Length == 0))
            return (new(), new(), "请先填写表名和至少一列。");
        // 台账④⑤：执行前校验（类型非空 + 数值列默认值口径），错误直接进预览不生成语句
        var errs = ValidateDefaults();
        if (errs.Count > 0)
            return (new(), new(), "校验未通过：\n  " + string.Join("\n  ", errs));
        var engine = _prov.Profile.Engine;
        var stmts = new List<string>();
        var skipped = new List<string>();
        if (_table == null)
        {
            stmts.Add(TableDdl.BuildCreate(t, engine, s => _prov.Quote(s)));
            stmts.AddRange(TableDdl.BuildIndexes(t, s => _prov.Quote(s)));
        }
        else
        {
            var (alt, sk1) = TableDdl.BuildAlter(t, _existingNames, engine, s => _prov.Quote(s));
            stmts.AddRange(alt);
            skipped.AddRange(sk1);
            stmts.AddRange(TableDdl.BuildIndexes(t, s => _prov.Quote(s)));
            var (fks, sk2) = TableDdl.BuildForeignKeys(t, engine, s => _prov.Quote(s));
            stmts.AddRange(fks);
            skipped.AddRange(sk2);
            // 格5：勾选删除的现有索引 / 外键生成 DROP 并入语句；引擎不支持则记提示。
            foreach (var (box, name) in _idxChecks.Where(x => x.Box.IsChecked == true))
            {
                var s = TableDdl.BuildDropIndex(engine, t.TableName, name, n => _prov.Quote(n));
                if (s.Length > 0) stmts.Add(s);
                else skipped.Add($"{_prov.EngineName}: 不支持删除索引 {name}。");
            }
            foreach (var (box, name) in _fkChecks.Where(x => x.Box.IsChecked == true))
            {
                var s = TableDdl.BuildDropForeignKey(engine, t.TableName, name, n => _prov.Quote(n));
                if (s.Length > 0) stmts.Add(s);
                else skipped.Add($"{_prov.EngineName}: 不支持删除外键 {name}。");
            }
        }
        if (stmts.Count == 0)
            return (stmts, skipped, "没有需要执行的变更。\n" + string.Join("\n", skipped));
        return (stmts, skipped, string.Join(";\n\n", stmts) + ";\n" + (skipped.Count > 0 ? "\n提示：\n  " + string.Join("\n  ", skipped) : ""));
    }

    private void RefreshPreview()
    {
        var (_, _, text) = Build();
        _preview.Text = text;
    }

    // ---------- 执行 ----------

    private async Task RunAsync()
    {
        if (_busy) return;
        var (stmts, _, text) = Build();
        _preview.Text = text;
        if (stmts.Count == 0) return;
        var confirm = new ContentDialog
        {
            Title = _table == null ? "确认新建表" : "确认修改表结构",
            Content = new TextBlock
            {
                Text = (_table == null
                    ? $"将在 {_db} 新建表 {Read().TableName}。"
                    : $"将修改 {_db}.{_table} 的结构，被删除 / 改类型的列会丢数据。")
                    + "\n变更语句见下方 SQL 预览，确认执行？",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "执行",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _owner.Content.XamlRoot,
        };
        if (await confirm.ShowThemedAsync() != ContentDialogResult.Primary) return;
        _busy = true;
        _runBtn.IsEnabled = false;
        try
        {
            // 格5：逐条执行逐条出账，失败弹继续 / 中止，不再整包一把梭。
            var okAll = true;
            for (int i = 0; i < stmts.Count; i++)
            {
                _status.Text = $"[表设计] stmt={i + 1}/{stmts.Count} 执行中…";
                _status.Foreground = Ant.TextSub;
                try
                {
                    var list = await _prov.Execute(_db, stmts[i] + ";");
                    var errs = list.Where(r => r.IsError).ToList();   // 发现76：结构化错误标记，不再靠文案子串
                    if (errs.Count > 0)
                    {
                        okAll = false;
                        _status.Text = $"[表设计] stmt={i + 1}/{stmts.Count} 失败：\n" + string.Join("\n", errs.Select(r => r.Message));
                        _status.Foreground = Ant.Error;   // 反馈-1：错误一律红色
                        if (i < stmts.Count - 1)
                        {
                            var go = new ContentDialog
                            {
                                Title = "语句执行失败",
                                Content = new TextBlock { Text = $"第 {i + 1}/{stmts.Count} 条失败。继续执行剩余 {stmts.Count - i - 1} 条？", TextWrapping = TextWrapping.Wrap },
                                PrimaryButtonText = "继续",
                                CloseButtonText = "中止",
                                DefaultButton = ContentDialogButton.Close,
                                XamlRoot = _owner.Content.XamlRoot,
                            };
                            if (await go.ShowThemedAsync() != ContentDialogResult.Primary) break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    okAll = false;
                    _status.Text = HumanError.Say(ex, $"[表设计] stmt={i + 1}/{stmts.Count} 失败");
                    _status.Foreground = Ant.Error;
                    if (i < stmts.Count - 1)
                    {
                        var go = new ContentDialog
                        {
                            Title = "语句执行失败",
                            Content = new TextBlock { Text = $"第 {i + 1}/{stmts.Count} 条失败。继续执行剩余 {stmts.Count - i - 1} 条？", TextWrapping = TextWrapping.Wrap },
                            PrimaryButtonText = "继续",
                            CloseButtonText = "中止",
                            DefaultButton = ContentDialogButton.Close,
                            XamlRoot = _owner.Content.XamlRoot,
                        };
                        if (await go.ShowThemedAsync() != ContentDialogResult.Primary) break;
                    }
                }
            }
            if (okAll)
            {
                _status.Text = $"[表设计] {stmts.Count} 条语句全部成功。请在左侧树点「刷新」查看结果。";
                _status.Foreground = Ant.Success;
                QueryHistory.Add(_prov.EngineName, _db, "-- 表结构设计：" + Read().TableName + "\n" + string.Join(";\n", stmts));
                // s15 改表保存后自动弹血缘影响地图：按本库查询历史语料建图，追溯该表下游引用。
                if (_table != null) _ = ShowLineageImpactAsync(_owner, _db, _table);
            }
        }
        catch (Exception ex)
        {
            _status.Text = HumanError.Say(ex, "执行失败");
            _status.Foreground = Ant.Error;
        }
        finally
        {
            _busy = false;
            _runBtn.IsEnabled = true;
        }
    }

    // s15 血缘影响地图：改表保存后自动弹出，用本库查询历史里的 CREATE 段建图做影响追溯；无语料时如实说明。
    private static async Task ShowLineageImpactAsync(MainWindow owner, string db, string tableName)
    {
        var corpusText = string.Join("\n\n", QueryHistory.Items
            .Where(h => h.Database == db)
            .Select(h => h.Sql)
            .Reverse());
        var map = DesignAssistDialog.ParseCorpus(corpusText);
        string report;
        if (map is null)
        {
            report = "本库的查询历史里没有可建图的 CREATE TABLE / VIEW / PROC 语料，无法追溯影响面。\n\n"
                + "可在「设计辅助 → 血缘图」粘贴视图 / 存储过程定义后建图追溯。";
        }
        else
        {
            var impacts = LineageMapper.Impact(map, tableName);
            var sb = new System.Text.StringBuilder();
            if (impacts.Count == 0)
                sb.AppendLine($"{tableName} 没有下游引用（视图 / 存储过程），改动风险低。");
            else
            {
                sb.AppendLine($"改动 {tableName} 的影响（{impacts.Count} 项）：");
                foreach (var i in impacts)
                    sb.AppendLine($"  {i.Object}（{i.Type}）：{string.Join(" → ", i.Path)}");
                foreach (var h in LineageMapper.Hints(impacts))
                    sb.AppendLine("  " + h);
            }
            report = sb.ToString();
        }
        await new ContentDialog
        {
            Title = $"血缘影响地图 · {tableName}",
            Content = new TextBlock { Text = report, TextWrapping = TextWrapping.Wrap, FontSize = AntTokens.FontMinor },
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        }.ShowThemedAsync();
    }
}
