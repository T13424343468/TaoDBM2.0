using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>例程管理（存储过程/函数/触发器）：左树右码，严格对齐《二期·高保真原型》§1。
/// 双击节点打开定义；断点调试仅 MySQL（v2.0 拍板），其他库灰置「v2.0 暂不支持」；
/// 空态展示引导卡「还没有例程，右键新建一个」；定义拉取时码区显示骨架条。
/// 安全口径（原稿 §20）：删除走显式确认，创建走单语句执行通道，凭据不落盘。</summary>
public static class RoutinesDialog
{
    private sealed class Item
    {
        public string Name = "";
        public string Kind = "";           // PROCEDURE / FUNCTION / TRIGGER
        public string Table = "";          // 触发器所属表（PG 删除要用）
        public RoutineKind KindEnum => Kind == "FUNCTION" ? RoutineKind.Function
            : Kind == "TRIGGER" ? RoutineKind.Trigger : RoutineKind.Procedure;
    }

    private sealed class State
    {
        public Dictionary<TreeViewNode, Item> Map = new();
        public List<TreeViewNode> GroupRoots = new();
        public List<(RoutineKind Kind, string Label)> Groups = new();
    }

    /// <summary>info 传入 Database 节点（连接入口也接受，取其首个库）。</summary>
    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        if (info.Provider is not RelationalProvider prov)
        {
            await new MessageDialogHelper(owner).Show("例程管理", "仅关系型数据库支持例程管理。");
            return;
        }
        if (!ObjectTreeCatalog.Supported(info.Profile.Engine))
        {
            await new MessageDialogHelper(owner).Show("例程管理", "该引擎暂不支持例程目录。");
            return;
        }

        string db = info.Db.Length > 0 ? info.Db : info.Profile.Database;
        var state = new State();

        // 左树：过程 / 函数 / 触发器 三组（原稿 §1 左栏）
        var tree = new TreeView
        {
            SelectionMode = TreeSelectionMode.Single,
            MinWidth = 220,
            MinHeight = 300,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // 加载态：码区骨架条（原稿：定义拉取时码区显示骨架条）

        // 右侧码区 + 空态
        var defBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            MinHeight = 300,
            MinWidth = 460,
            PlaceholderText = "双击或选中左侧例程查看定义",
        };
        var emptyCard = new TextBlock
        {
            Text = "还没有例程，右键新建一个",
            Foreground = Ant.TextSub,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 80, 0, 0),
        };
        var rightHost = new Grid { MinHeight = 300 };
        rightHost.Children.Add(defBox);
        rightHost.Children.Add(emptyCard);

        // 工具条：查看定义 / 断点调试 / 引用查找 / 删除（原稿 §1 按钮组）＋ 新建
        var btnNew = new Button { Content = "新建", CornerRadius = new CornerRadius(4) };
        var btnDef = new Button { Content = "查看定义", CornerRadius = new CornerRadius(4) };
        bool dbg = RoutineManager.DebugSupported(info.Profile.Engine);
        var btnDebug = new Button
        {
            Content = "断点调试",
            CornerRadius = new CornerRadius(4),
            IsEnabled = dbg,
        };
        var btnRef = new Button { Content = "引用查找", CornerRadius = new CornerRadius(4) };
        var btnDel = new Button
        {
            Content = "删除",
            CornerRadius = new CornerRadius(4),
            Foreground = Ant.Error,
        };
        var debugNote = dbg
            ? new TextBlock { Text = "", Foreground = Ant.TextSub, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }
            : new TextBlock { Text = RoutineManager.DebugUnsupportedText, Foreground = Ant.TextSub, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var b in new[] { btnNew, btnDef, btnDebug, btnRef, btnDel }) bar.Children.Add(b);
        bar.Children.Add(debugNote);

        var status = new TextBlock { Text = "", Foreground = Ant.TextSub, FontSize = 12, TextWrapping = TextWrapping.Wrap };

        var grid = new Grid
        {
            ColumnSpacing = 14,
            MinWidth = 760,
            RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() },
        };
        var leftCol = new ColumnDefinition { Width = new GridLength(230) };
        var rightCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        grid.ColumnDefinitions.Add(leftCol);
        grid.ColumnDefinitions.Add(rightCol);
        var leftHost = new Grid { MinHeight = 300 };
        var leftSkel = new StackPanel { Spacing = 8 };
        for (int i = 0; i < 3; i++)
            leftSkel.Children.Add(new Border { Height = 14, CornerRadius = new CornerRadius(4), Background = Ant.AltRow, Margin = new Thickness(0, 0, 30, 0) });
        leftHost.Children.Add(leftSkel);
        Grid.SetRow(leftHost, 1);
        Grid.SetColumn(leftHost, 0);
        Grid.SetRow(rightHost, 1);
        Grid.SetColumn(rightHost, 1);
        Grid.SetRow(bar, 0);
        grid.Children.Add(bar);
        grid.Children.Add(leftHost);
        grid.Children.Add(rightHost);

        var dlg = new ContentDialog
        {
            Title = $"例程 — {db}@{info.Profile.Engine}",
            Content = grid,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };

        Item? Sel()
        {
            var n = tree.SelectedItem;
            return n is null ? null : state.Map.GetValueOrDefault(n);
        }

        async Task<string?> LoadDefAsync(Item it)
        {
            rightHost.Children.Clear();
            var host = new Grid { MinHeight = 300 };
            var s = new StackPanel { Spacing = 8, Margin = new Thickness(0, 40, 0, 0) };
            for (int i = 0; i < 4; i++)
                s.Children.Add(new Border { Height = 14, CornerRadius = new CornerRadius(4), Background = Ant.AltRow, Margin = new Thickness(0, 0, 40 * (i % 2), 0) });
            host.Children.Add(s);
            rightHost.Children.Add(host);
            try
            {
                var rs = await prov.Execute(db, ObjectTreeCatalog.ShowDefinition(info.Profile.Engine, it.Name));
                var ddl = rs.Count > 0 && rs[0].Rows.Count > 0
                    ? rs[0].Rows[0].LastOrDefault(v => v is not null)?.ToString() ?? ""
                    : "";
                rightHost.Children.Clear();
                rightHost.Children.Add(defBox);
                if (ddl.Length == 0)
                {
                    defBox.Text = "";
                    status.Text = $"未取到 {it.Name} 的定义（对象可能已被删除）。";
                    return null;
                }
                defBox.Text = ddl;
                status.Text = $"已载入 {it.Name} 定义";
                return ddl;
            }
            catch (Exception ex)
            {
                rightHost.Children.Clear();
                rightHost.Children.Add(defBox);
                defBox.Text = "";
                status.Text = "读取定义失败：" + ex.Message;
                return null;
            }
        }

        async Task ReloadAsync()
        {
            leftHost.Children.Clear();
            leftHost.Children.Add(leftSkel);
            tree.RootNodes.Clear();
            state.Map.Clear();
            state.GroupRoots.Clear();
            int total = 0;

            var groups = new List<(RoutineKind Kind, string Label, List<Item> Items)>
            {
                (RoutineKind.Procedure, "过程", new()),
                (RoutineKind.Function, "函数", new()),
                (RoutineKind.Trigger, "触发器", new()),
            };

            try
            {
                var rs = await prov.Execute(db, ObjectTreeCatalog.ListRoutines(info.Profile.Engine, db));
                if (rs.Count > 0)
                    foreach (var row in rs[0].Rows)
                    {
                        var name = row.Length > 0 ? row[0]?.ToString() ?? "" : "";
                        var kind = row.Length > 1 ? row[1]?.ToString() ?? "" : "";
                        if (name.Length == 0) continue;
                        var it = new Item { Name = name, Kind = kind.ToUpperInvariant().Contains("FUNC") ? "FUNCTION" : "PROCEDURE" };
                        groups.First(g => g.Kind == it.KindEnum).Items.Add(it);
                    }
                {
                    var ts = await prov.Execute(db, ObjectTreeCatalog.ListTriggers(info.Profile.Engine, db));
                    if (ts.Count > 0)
                        foreach (var row in ts[0].Rows)
                        {
                            var name = row.Length > 0 ? row[0]?.ToString() ?? "" : "";
                            if (name.Length == 0) continue;
                            groups[2].Items.Add(new Item
                            {
                                Name = name,
                                Kind = "TRIGGER",
                                Table = row.Length > 1 ? row[1]?.ToString() ?? "" : "",
                            });
                        }
                }
                leftHost.Children.Clear();
                leftHost.Children.Add(tree);
                foreach (var (kind, label, items) in groups)
                {
                    var root = new TreeViewNode { Content = $"📁 {label}（{items.Count}）", IsExpanded = items.Count > 0 };
                    state.GroupRoots.Add(root);
                    tree.RootNodes.Add(root);
                    foreach (var it in items)
                    {
                        total++;
                        var node = new TreeViewNode { Content = "▪ " + it.Name };
                        state.Map[node] = it;
                        root.Children.Add(node);
                    }
                    if (items.Count == 0) root.Children.Add(new TreeViewNode { Content = "（空）" });
                }
                emptyCard.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
                status.Text = total == 0
                    ? "还没有例程。点「新建」创建第一个过程 / 函数 / 触发器。"
                    : $"{db}：共 {total} 个例程";
                if (total > 0) defBox.Text = "选中左侧例程查看定义。";
            }
            catch (Exception ex)
            {
                leftHost.Children.Clear();
                leftHost.Children.Add(tree);
                status.Text = "目录加载失败：" + ex.Message;
            }
        }

        btnNew.Click += async (_, _) =>
        {
            var sel = Sel();
            var kind = sel?.KindEnum ?? RoutineKind.Procedure;
            // 分级确认（原稿 §20 低风险确认）：新建只打开模板到查询窗口，执行由用户在查询窗口自己按键。
            owner.OpenQueryTab(info, RoutineManager.Template(info.Profile.Engine, kind, "new_routine"));
            status.Text = "已把模板打开到查询窗口，填好后整段执行（过程体内分号不会被误拆）。";
        };

        btnDef.Click += async (_, _) =>
        {
            var sel = Sel();
            if (sel is null) { status.Text = "先在左侧选中一个例程。"; return; }
            await LoadDefAsync(sel);
        };

        btnDebug.Click += async (_, _) =>
        {
            var sel = Sel();
            if (sel is null) { status.Text = "先在左侧选中一个例程。"; return; }
            if (!RoutineManager.DebugSupported(info.Profile.Engine)) { status.Text = RoutineManager.DebugUnsupportedText; return; }
            if (sel.KindEnum == RoutineKind.Trigger)
            {
                status.Text = "触发器由数据事件触发，不支持手动断点调试；选中后点「查看定义」查看触发逻辑。";
                return;
            }
            owner.OpenQueryTab(info, RoutineManager.DebugScript(info.Profile.Engine, sel.KindEnum, sel.Name));
            status.Text = "调试脚手架已打开到查询窗口：填入参数整段执行，过程内中间结果用 SELECT 观测。";
        };

        btnRef.Click += async (_, _) =>
        {
            var sel = Sel();
            if (sel is null) { status.Text = "先在左侧选中一个例程。"; return; }
            try
            {
                var rs = await prov.Execute(db, RoutineManager.ReferencesSql(info.Profile.Engine, db, sel.Name));
                rightHost.Children.Clear();
                rightHost.Children.Add(defBox);
                defBox.Text = rs.Count == 0 || rs[0].Rows.Count == 0
                    ? $"（没有在例程/触发器定义中找到引用「{sel.Name}」的对象）"
                    : string.Join("\n\n" + new string('─', 60) + "\n\n", rs[0].Rows
                        .Select(r => $"【{(r.Length > 1 ? r[1]?.ToString() ?? "" : "")}】{(r.Length > 0 ? r[0]?.ToString() ?? "" : "")}\n{(r.Length > 2 ? r[2]?.ToString() ?? "" : "")}"));
                status.Text = rs.Count > 0 ? $"引用查找完成：{rs[0].Rows.Count} 处" : "引用查找完成";
            }
            catch (Exception ex)
            {
                status.Text = "引用查找失败：" + ex.Message;
            }
        };

        btnDel.Click += async (_, _) =>
        {
            var sel = Sel();
            if (sel is null) { status.Text = "先在左侧选中一个例程。"; return; }
            // 高风险显式确认（原稿 §20 分级确认）：删除为不可逆操作，弹确认。
            if (!await owner.ConfirmAsync($"确定删除{sel.Kind}「{sel.Name}」？此操作不可恢复。")) return;
            try
            {
                var sql = RoutineManager.BuildDropSql(info.Profile.Engine, sel.KindEnum, sel.Name,
                    sel.KindEnum == RoutineKind.Trigger && info.Profile.Engine == DbEngine.MySQL ? db : null,
                    sel.KindEnum == RoutineKind.Trigger ? sel.Table : null);
                var qr = await prov.ExecuteSingle(db, sql);
                status.Text = qr.Message.StartsWith("错误", StringComparison.Ordinal)
                    ? "删除失败：" + qr.Message
                    : $"已删除 {sel.Name}。";
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                status.Text = "删除失败：" + ex.Message;
            }
        };

        tree.SelectionChanged += async (_, _) =>
        {
            var sel = Sel();
            if (sel is null) return;
            await LoadDefAsync(sel);
        };

        _ = dlg.ShowThemedAsync();
        await ReloadAsync();
    }
}
