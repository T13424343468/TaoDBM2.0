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
    private readonly bool _isRelational;

    private sealed class ColWidgets
    {
        public TextBox Name = new() { MinWidth = 90, PlaceholderText = "列名" };
        public TextBox Type = new() { MinWidth = 110, PlaceholderText = "类型" };
        public TextBox Def = new() { MinWidth = 80, PlaceholderText = "默认值" };
        public CheckBox Pk = new() { Content = "主键", MinWidth = 56, VerticalAlignment = VerticalAlignment.Center };
        public CheckBox Null = new() { Content = "可空", MinWidth = 56, IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        public CheckBox Inc = new() { Content = "自增", MinWidth = 56, VerticalAlignment = VerticalAlignment.Center };
        public Button Del = new() { Content = "✕", Padding = new Thickness(6, 2, 6, 2), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
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
            foreach (var c in existing)
                AddColRow(new TableColumnDesign { Name = c.Name, SqlType = c.DataType, Nullable = c.Nullable, IsPrimaryKey = c.IsKey });
        else AddColRow(null);
        if (table != null)
        {
            // 改表模式：索引 / 外键分区只支持新增（现有约束读取接口未开放，如实提示，不假装已加载）
            _idxHost.Children.Add(new TextBlock { Text = "改表模式下此处仅新增索引；现有索引请在数据库侧查看。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            _fkHost.Children.Add(new TextBlock { Text = "改表模式下此处仅新增外键；现有外键请在数据库侧查看。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        }

        // SQL 预览（实时）
        var previewPanel = new StackPanel { Spacing = 4, Margin = new Thickness(10, 4, 10, 4) };
        previewPanel.Children.Add(new TextBlock { Text = "SQL 预览（随编辑实时刷新）", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
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
        if (c != null)
        {
            w.Name.Text = c.Name;
            w.Type.Text = c.SqlType;
            w.Pk.IsChecked = c.IsPrimaryKey;
            w.Null.IsChecked = c.Nullable;
            w.Inc.IsChecked = c.AutoIncrement;
            w.Def.Text = c.DefaultValue;
        }
        w.Del.Click += (_, _) =>
        {
            if (_colRows.Count <= 1) return;
            if (w.Name.Parent is FrameworkElement fe) _colHost.Children.Remove(fe);
            _colRows.Remove(w);
            RefreshPreview();
        };
        Hook(w.Name, w.Type, w.Def);
        HookCheck(w.Pk, w.Null, w.Inc);
        _colRows.Add(w);
        _colHost.Children.Add(ColRow(w));
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
            var list = await _prov.Execute(_db, string.Join(";\n", stmts) + ";");
            var errs = list.Where(r => r.Message.StartsWith("错误", StringComparison.Ordinal)).ToList();
            if (errs.Count == 0)
            {
                _status.Text = $"已执行 {stmts.Count} 条语句，成功。请在左侧树点「刷新」查看结果。";
                _status.Foreground = Ant.Success;
                QueryHistory.Add(_prov.EngineName, _db, "-- 表结构设计：" + Read().TableName + "\n" + string.Join(";\n", stmts));
                // s15 改表保存后自动弹血缘影响地图：按本库查询历史语料建图，追溯该表下游引用。
                if (_table != null) _ = ShowLineageImpactAsync(_owner, _db, _table);
            }
            else
            {
                _status.Text = $"执行出错（{errs.Count} 条失败）：\n" + string.Join("\n", errs.Select(r => r.Message));
                _status.Foreground = Ant.Error;   // 反馈-1：错误一律红色
            }
        }
        catch (Exception ex)
        {
            _status.Text = "执行失败：" + ex.Message;
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
