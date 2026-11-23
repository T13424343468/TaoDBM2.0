using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>可视化表结构设计器：编辑列定义 → 预览 DDL → 执行。</summary>
public static class TableDesignerDialog
{
    private sealed class RowWidgets
    {
        public TextBox Name = new() { MinWidth = 90 };
        public TextBox Type = new() { MinWidth = 110 };
        public CheckBox Pk = new() { Content = "主键", MinWidth = 56, VerticalAlignment = VerticalAlignment.Center };
        public CheckBox Null = new() { Content = "可空", MinWidth = 56, IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        public CheckBox Inc = new() { Content = "自增", MinWidth = 56, VerticalAlignment = VerticalAlignment.Center };
        public Button Del = new() { Content = "✕", Padding = new Thickness(6, 2, 6, 2), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        /// <summary>该行所在 Grid，用于逐列 diff 着色。</summary>
        public Grid? RowHost;
    }

    /// <summary>table 为 null 表示新建表；existing 传入旧列定义（改表用）。</summary>
    public static async Task Show(MainWindow owner, IDbProvider prov, string db, string? table, List<ColumnSpec>? existing)
    {
        if (prov is not RelationalProvider)
        {
            await new MessageDialogHelper(owner).Show("设计表", "仅关系型数据库支持可视化建表 / 改表。");
            return;
        }

        var rows = new List<RowWidgets>();
        var rowsHost = new StackPanel { Spacing = 4 };
        var existingSpecs = existing ?? new List<ColumnSpec>();

        // 逐列 diff 提示：删除列（旧结构有而当前行无）用红字提示。
        var diffHint = new TextBlock
        {
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.Error,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        // 改表模式：索引 / 外键清单（只读行 + 删除勾选），勾选生成 DROP 并入 stmts。
        var idxChecks = new List<(CheckBox Box, string Name)>();
        var fkChecks = new List<(CheckBox Box, string Name)>();
        var idxHost = new StackPanel { Spacing = 2 };
        var fkHost = new StackPanel { Spacing = 2 };
        var idxSection = new StackPanel { Spacing = 4 };
        var fkSection = new StackPanel { Spacing = 4 };
        if (table != null)
        {
            idxSection.Children.Add(new TextBlock { Text = "索引（勾选删除）", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
            idxSection.Children.Add(idxHost);
            if (prov.Profile.Engine == DbEngine.SQLite)
                fkHost.Children.Add(new TextBlock { Text = "SQLite 不支持单独删除外键约束（需重建表）。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
            else
            {
                fkSection.Children.Add(new TextBlock { Text = "外键（勾选删除）", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
                fkSection.Children.Add(fkHost);
            }
        }

        void RefreshDiff()
        {
            // 行着色：新增绿 / 修改黄 / 未变不着色；删除列在下方红字提示。
            var rowNames = rows.Select(r => r.Name.Text.Trim()).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var w in rows)
            {
                if (w.RowHost == null) continue;
                var name = w.Name.Text.Trim();
                if (name.Length == 0 || table == null) { w.RowHost.Background = null; continue; }
                var old = existingSpecs.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (old == null)
                    w.RowHost.Background = Ant.SuccessBg; // 新增列
                else if (!old.DataType.Trim().Equals(w.Type.Text.Trim(), StringComparison.OrdinalIgnoreCase)
                    || old.Nullable != (w.Null.IsChecked != false)
                    || old.IsKey != (w.Pk.IsChecked == true))
                    w.RowHost.Background = Ant.WarnBg;    // 修改列
                else
                    w.RowHost.Background = null;          // 未变
            }
            var deleted = existingSpecs.Where(c => c.Name.Length > 0 && !rowNames.Contains(c.Name)).Select(c => c.Name).ToList();
            if (deleted.Count > 0 && table != null)
            {
                diffHint.Text = "将删除列（丢数据）：" + string.Join("、", deleted);
                diffHint.Visibility = Visibility.Visible;
            }
            else diffHint.Visibility = Visibility.Collapsed;
        }

        void AddRow(TableColumnDesign? c)
        {
            var w = new RowWidgets();
            if (c != null)
            {
                w.Name.Text = c.Name;
                w.Type.Text = c.SqlType;
                w.Pk.IsChecked = c.IsPrimaryKey;
                w.Null.IsChecked = c.Nullable;
                w.Inc.IsChecked = c.AutoIncrement;
            }
            w.Name.TextChanged += (_, _) => RefreshDiff();
            w.Type.TextChanged += (_, _) => RefreshDiff();
            w.Pk.Checked += (_, _) => RefreshDiff();
            w.Pk.Unchecked += (_, _) => RefreshDiff();
            w.Null.Checked += (_, _) => RefreshDiff();
            w.Null.Unchecked += (_, _) => RefreshDiff();
            w.Del.Click += (_, _) =>
            {
                if (rows.Count > 1 && w.Name.Parent is FrameworkElement fe)
                {
                    rowsHost.Children.Remove(fe);
                    rows.Remove(w);
                    RefreshDiff();
                }
            };
            rows.Add(w);
            rowsHost.Children.Add(ColumnRow(w));
            RefreshDiff();
        }

        if (existing is { Count: > 0 })
            foreach (var c in existing)
                AddRow(new TableColumnDesign { Name = c.Name, SqlType = c.DataType, Nullable = c.Nullable, IsPrimaryKey = c.IsKey });
        else AddRow(null);

        var nameBox = new TextBox
        {
            PlaceholderText = "表名",
            Text = table ?? "",
            IsReadOnly = table != null,
            MinWidth = 220,
        };

        var preview = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = (FontFamily)Application.Current.Resources["AntMono"],
            FontSize = AntTokens.FontMinor,
            Height = 170,
            Text = "点「生成 SQL」预览将执行的语句。",
        };

        var addBtn = new Button { Content = "＋ 加一列", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        addBtn.Click += (_, _) => AddRow(null);

        var genBtn = new Button
        {
            Content = "生成 SQL",
            Background = Ant.Primary,
            Foreground = Ant.OnPrimary,
            BorderThickness = new Thickness(0),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
        };
        var runBtn = new Button { Content = "执行", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(addBtn);
        actions.Children.Add(genBtn);
        actions.Children.Add(runBtn);

        var head = new TextBlock
        {
            Text = $"列定义（引擎：{prov.EngineName}{(table != null ? " · 改表模式" : " · 新建表")}）",
            FontSize = AntTokens.FontMinor,
            Foreground = Ant.TextSub,
        };

        var content = new StackPanel { Spacing = 10, MinWidth = 640, Margin = new Thickness(0, 4, 0, 0) };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameRow.Children.Add(new TextBlock { Text = "表名", VerticalAlignment = VerticalAlignment.Center });
        nameRow.Children.Add(nameBox);
        content.Children.Add(nameRow);
        content.Children.Add(head);
        content.Children.Add(rowsHost);
        content.Children.Add(diffHint);
        content.Children.Add(actions);
        content.Children.Add(idxSection);
        content.Children.Add(fkSection);
        content.Children.Add(preview);

        var dlg = new ContentDialog
        {
            Title = table == null ? $"新建表 · {db}" : $"改表结构 · {table}",
            Content = content,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };

        TableDesign Read()
        {
            var t = new TableDesign { TableName = nameBox.Text.Trim() };
            foreach (var w in rows)
                t.Columns.Add(new TableColumnDesign
                {
                    Name = w.Name.Text.Trim(),
                    SqlType = w.Type.Text.Trim(),
                    Nullable = w.Null.IsChecked != false,
                    IsPrimaryKey = w.Pk.IsChecked == true,
                    AutoIncrement = w.Inc.IsChecked == true,
                    DefaultValue = "",
                });
            return t;
        }

        async Task<(List<string> Stmts, List<string> Skipped, string Preview)> BuildAsync()
        {
            var t = Read();
            if (t.TableName.Length == 0 || t.Columns.All(c => c.Name.Length == 0))
                return (new(), new(), "请先填写表名和至少一列。");
            if (table == null)
            {
                var sql = TableDdl.BuildCreate(t, prov.Profile.Engine, s => prov.Quote(s));
                return (TableDdl.AsStatements(sql), new(), sql);
            }
            try
            {
                var olds = (await prov.GetColumns(db, table)).Select(c => c.Name).ToList();
                var (stmts, skipped) = TableDdl.BuildAlter(t, olds, prov.Profile.Engine, s => prov.Quote(s));
                // 勾选删除的索引 / 外键：生成 DROP 并入 stmts；引擎不支持则记提示。
                foreach (var (box, name) in idxChecks.Where(x => x.Box.IsChecked == true))
                {
                    var s = TableDdl.BuildDropIndex(prov.Profile.Engine, t.TableName, name, n => prov.Quote(n));
                    if (s.Length > 0) stmts.Add(s);
                    else skipped.Add($"{prov.EngineName}: 不支持删除索引 {name}。");
                }
                foreach (var (box, name) in fkChecks.Where(x => x.Box.IsChecked == true))
                {
                    var s = TableDdl.BuildDropForeignKey(prov.Profile.Engine, t.TableName, name, n => prov.Quote(n));
                    if (s.Length > 0) stmts.Add(s);
                    else skipped.Add($"{prov.EngineName}: 不支持单独删除外键约束 {name}。");
                }
                var text = stmts.Count == 0
                    ? "没有需要执行的变更。\n" + string.Join("\n", skipped)
                    : string.Join(";\n\n", stmts) + ";\n" + (skipped.Count > 0 ? "\n提示：\n  " + string.Join("\n  ", skipped) : "");
                return (stmts, skipped, text);
            }
            catch (Exception ex)
            {
                return (new(), new(), "读取现有结构失败：" + ex.Message);
            }
        }

        async void OnGenerate()
        {
            var (_, _, text) = await BuildAsync();
            preview.Text = text;
        }

        async void OnRun()
        {
            var (stmts, _, text) = await BuildAsync();
            preview.Text = text;
            if (stmts.Count == 0) return;
            // 建表 / 改表直接落库：执行前给预览 + 确认，不静默改库。
            var confirm = new ContentDialog
            {
                Title = table == null ? "确认新建表" : "确认修改表结构",
                Content = new TextBlock
                {
                    Text = (table == null
                        ? $"将在 {db} 新建表 {Read().TableName}。"
                        : $"将修改 {db}.{table} 的结构，被删除 / 改类型的列会丢数据。")
                        + "\n变更语句见上方预览框，确认执行？",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "执行",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = owner.Content.XamlRoot,
            };
            if (await confirm.ShowThemedAsync() != ContentDialogResult.Primary) return;
            runBtn.IsEnabled = false;
            var done = new List<string>();
            var failed = new List<string>();
            try
            {
                // 逐条执行逐条出账：失败可选继续 / 中止，不整包回滚。
                for (int i = 0; i < stmts.Count; i++)
                {
                    try
                    {
                        await prov.Execute(db, stmts[i] + ";");
                        AppLog.Write($"[表设计] stmt={i + 1}/{stmts.Count}");
                        done.Add(stmts[i]);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"[表设计] stmt={i + 1}/{stmts.Count} 失败");
                        failed.Add($"第 {i + 1} 条：{ex.Message}");
                        var go = new ContentDialog
                        {
                            Title = $"第 {i + 1}/{stmts.Count} 条执行失败",
                            Content = new TextBlock
                            {
                                Text = ex.Message + "\n\n继续执行剩余语句？",
                                TextWrapping = TextWrapping.Wrap,
                            },
                            PrimaryButtonText = "继续",
                            CloseButtonText = "中止",
                            DefaultButton = ContentDialogButton.Close,
                            XamlRoot = owner.Content.XamlRoot,
                        };
                        if (await go.ShowThemedAsync() != ContentDialogResult.Primary) break;
                    }
                }
                preview.Text = failed.Count == 0
                    ? $"已执行 {done.Count} 条语句，成功。请在左侧树点「刷新」查看结果。"
                    : $"已执行 {done.Count} 条，失败 {failed.Count} 条：\n" + string.Join("\n", failed);
                if (done.Count > 0)
                {
                    QueryHistory.Add(prov.EngineName, db, "-- 表结构设计：" + Read().TableName + "\n" + string.Join(";\n", done));
                    // s15 改表保存后自动弹血缘影响地图：按本库查询历史语料建图，追溯该表下游引用。
                    if (table != null) _ = ShowLineageImpactAsync(owner, db, table);
                }
            }
            finally { runBtn.IsEnabled = true; }
        }

        genBtn.Click += (_, _) => OnGenerate();
        runBtn.Click += (_, _) => OnRun();
        _ = dlg.ShowThemedAsync();
        _ = LoadIndexFkAsync();
    }

    // 改表模式：异步拉取本表索引 / 外键清单，只读行 + 删除勾选；失败如实提示不阻断。
    async Task LoadIndexFkAsync()
    {
        if (table == null) return;
        try
        {
            var idxSql = ObjectTreeCatalog.ListIndexes(prov.Profile.Engine, db);
            var rs = await prov.Execute(db, idxSql);
            int ti = 0;
            foreach (var r in rs.SelectMany(x => x.Rows))
            {
                var tname = r.Length > 0 ? r[0]?.ToString() ?? "" : "";
                var iname = r.Length > 1 ? r[1]?.ToString() ?? "" : "";
                if (!tname.Equals(table, StringComparison.OrdinalIgnoreCase) || iname.Length == 0) continue;
                // MySQL 主键索引不能按普通索引删，跳过展示。
                if (iname.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase) && prov.Profile.Engine == DbEngine.MySQL) continue;
                var box = new CheckBox { Content = "删除", MinWidth = 56, VerticalAlignment = VerticalAlignment.Center };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, $"IdxDrop_{ti}");
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new TextBlock { Text = iname, VerticalAlignment = VerticalAlignment.Center, FontSize = AntTokens.FontMinor });
                row.Children.Add(box);
                idxHost.Children.Add(row);
                idxChecks.Add((box, iname));
                ti++;
            }
            if (idxChecks.Count == 0)
                idxHost.Children.Add(new TextBlock { Text = "该表没有可管理的索引。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        }
        catch
        {
            idxHost.Children.Add(new TextBlock { Text = "索引清单读取失败（权限或引擎限制）。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        }
        if (prov.Profile.Engine == DbEngine.SQLite) return;
        try
        {
            var fkSql = ObjectTreeCatalog.ListForeignKeys(prov.Profile.Engine, db);
            var rs = await prov.Execute(db, fkSql);
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
                    if (!tname.Equals(table, StringComparison.OrdinalIgnoreCase) || cname.Length == 0) continue;
                    var box = new CheckBox { Content = "删除", MinWidth = 56, VerticalAlignment = VerticalAlignment.Center };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, $"FkDrop_{fi}");
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(new TextBlock { Text = cname, VerticalAlignment = VerticalAlignment.Center, FontSize = AntTokens.FontMinor });
                    row.Children.Add(box);
                    fkHost.Children.Add(row);
                    fkChecks.Add((box, cname));
                    fi++;
                }
            }
            if (fkChecks.Count == 0)
                fkHost.Children.Add(new TextBlock { Text = "该表没有外键。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        }
        catch
        {
            fkHost.Children.Add(new TextBlock { Text = "外键清单读取失败（权限或引擎限制）。", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
        }
    }

    private static int FindCol(QueryResult res, string header, int fallback)
    {
        for (int i = 0; i < res.Columns.Count; i++)
            if (res.Columns[i].Equals(header, StringComparison.OrdinalIgnoreCase)) return i;
        return fallback;
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

    private static Grid ColumnRow(RowWidgets w)
    {
        var g = new Grid();
        w.RowHost = g;
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
        Put(w.Del, 5);
        return g;
    }
}
