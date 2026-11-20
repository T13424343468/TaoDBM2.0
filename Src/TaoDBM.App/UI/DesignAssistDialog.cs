using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>设计辅助对话框：库级入口，聚合三个设计类引擎。
/// 图表规范（ChartSpecBuilder 对查询结果推荐图型并聚合）、表单构建（FormSchemaBuilder 按表结构生成录入表单）、
/// 血缘图（LineageMapper 从 SQL 语料建血缘地图并做影响追溯）。全部只读 / 纯计算，不改任何数据。</summary>
public static class DesignAssistDialog
{
    public static void Show(MainWindow owner, NodeInfo info)
    {
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var report = new StackPanel { Spacing = 10 };

        // ── 图表规范：粘贴查询结果 JSON，推荐图型并聚合 ──
        var chartBox = new TextBox
        {
            PlaceholderText = "查询结果（列名逗号分隔 + 数据行，分号分隔），如：name,amount; 张三,100; 李四,250",
            MinWidth = 560,
            AcceptsReturn = true,
            Height = 64,
        };
        var chartColBox = new TextBox { PlaceholderText = "聚合列名（如 amount）", MinWidth = 180 };
        var chartBtn = new Button { Content = "推荐图型", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        chartBtn.Click += (_, _) => RunChart(chartBox.Text, chartColBox.Text, report, status);

        // ── 表单构建：按表结构生成录入表单 ──
        var formTableBox = new TextBox { PlaceholderText = "表名（用当前连接的表结构生成表单）", MinWidth = 220 };
        var formBtn = new Button { Content = "生成表单", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var formValidateBtn = new Button { Content = "校验填写", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        formBtn.Click += async (_, _) => await RunForm(owner, info, formTableBox.Text, report, status);
        formValidateBtn.Click += (_, _) => RunFormValidate(report, status);

        // ── 血缘图：粘贴对象定义 SQL，建图并做影响追溯 ──
        var lineageBox = new TextBox
        {
            PlaceholderText = "对象定义语料（CREATE TABLE / VIEW / PROC 逐段粘贴，可多段）",
            MinWidth = 560,
            AcceptsReturn = true,
            Height = 72,
        };
        var lineageTargetBox = new TextBox { PlaceholderText = "要评估影响的对象名（如表名）", MinWidth = 200 };
        var lineageBuildBtn = new Button { Content = "建血缘图", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var lineageImpactBtn = new Button { Content = "追溯影响", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        lineageBuildBtn.Click += (_, _) => RunLineageBuild(lineageBox.Text, report, status);
        lineageImpactBtn.Click += (_, _) => RunLineageImpact(lineageBox.Text, lineageTargetBox.Text, report, status);

        var chartRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        chartRow.Children.Add(chartColBox);
        chartRow.Children.Add(chartBtn);

        var formRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        formRow.Children.Add(formTableBox);
        formRow.Children.Add(formBtn);
        formRow.Children.Add(formValidateBtn);

        var lineageRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        lineageRow.Children.Add(lineageTargetBox);
        lineageRow.Children.Add(lineageBuildBtn);
        lineageRow.Children.Add(lineageImpactBtn);

        var panel = new StackPanel { Spacing = 10, MinWidth = 680 };
        panel.Children.Add(new TextBlock
        {
            Text = "设计辅助（只读 / 纯计算）：图表只对粘贴结果聚合；表单只读表结构；血缘只对粘贴语料建图。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ant.TextSub,
            FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(chartBox);
        panel.Children.Add(chartRow);
        panel.Children.Add(formRow);
        panel.Children.Add(lineageBox);
        panel.Children.Add(lineageRow);

        var dlg = new ContentDialog
        {
            Title = "设计辅助（图表规范 / 表单构建 / 血缘图）",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
    }

    // ── 图表规范：解析粘贴的轻量结果 → 推荐图型 → 聚合 ──
    private static void RunChart(string text, string column, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        if (string.IsNullOrWhiteSpace(text))
        {
            status.Text = "请先粘贴查询结果（列名行 + 数据行，分号分隔）。";
            return;
        }

        try
        {
            var r = ParseResult(text);
            if (r is null || r.Columns.Count == 0)
            {
                status.Text = "结果格式：第一行列名（逗号分隔），后面每行一条数据，分号分隔。";
                return;
            }

            var col = column.Trim();
            if (col.Length == 0) col = r.Columns.FirstOrDefault(c => c != "name") ?? r.Columns[0];
            var type = ChartSpecBuilder.RecommendType(r, col);
            var spec = type == "pie" || type == "bar"
                ? ChartSpecBuilder.CategoryCount(r, col)
                : type == "line"
                    ? ChartSpecBuilder.TimeSeries(r, r.Columns[0], col)
                    : ChartSpecBuilder.Histogram(r, col);

            AddSection(report, $"推荐图型：{SpecTypeText(type)}");
            AddLine(report, $"标题：{spec.Title}");
            AddLine(report, "数据点：");
            for (int i = 0; i < spec.Labels.Count && i < spec.Values.Count; i++)
                AddLine(report, $"  {spec.Labels[i]}  =  {spec.Values[i]}");
            if (!string.IsNullOrEmpty(spec.Extra))
                AddLine(report, $"说明：{spec.Extra}");
            AddLine(report, "可存 JSON：" + ChartSpecBuilder.ToJson(spec));
            status.Text = "图表规范完成。";
        }
        catch (Exception ex)
        {
            status.Text = "出图失败：" + ex.Message;
        }
    }

    private static string SpecTypeText(string t) => t switch
    {
        "bar" => "柱状图（分类对比）",
        "pie" => "饼图（占比）",
        "line" => "折线图（趋势）",
        "histogram" => "直方图（分布）",
        _ => t,
    };

    // 把轻量文本转成 QueryResult：首行列名，其余行数据，分号分段、逗号分列
    private static QueryResult? ParseResult(string text)
    {
        var segs = text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) return null;
        var cols = segs[0].Split(',', StringSplitOptions.TrimEntries);
        var r = new QueryResult { Title = "粘贴结果" };
        r.Columns.AddRange(cols);
        for (int i = 1; i < segs.Length; i++)
        {
            var vals = segs[i].Split(',', StringSplitOptions.TrimEntries);
            var row = new object?[cols.Length];
            for (int j = 0; j < cols.Length; j++)
                row[j] = j < vals.Length ? vals[j] : null;
            r.Rows.Add(row);
        }
        return r;
    }

    // ── 表单构建：拉真实表结构 → 生成 FormField 列表 ──
    private sealed class FormDraft
    {
        public string Table = "";
        public List<FormField> Fields = new();
        public Dictionary<string, TextBox> Inputs = new();
    }
    private static FormDraft? _lastForm;

    private static async Task RunForm(MainWindow owner, NodeInfo info, string table, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        table = table.Trim();
        if (table.Length == 0)
        {
            status.Text = "请先填表名。";
            return;
        }

        try
        {
            var prov = info.Provider ?? ProviderFactory.Create(info.Profile);
            string db = CurrentDb(info);
            var columns = await prov.GetColumns(db, table);
            if (columns.Count == 0)
            {
                status.Text = $"在 {db} 下没读到 {table} 的列结构，请确认表名。";
                return;
            }

            var fields = FormSchemaBuilder.Build(table, columns);
            var draft = new FormDraft { Table = table, Fields = fields };
            AddSection(report, $"表单定义（{fields.Count} 字段）");
            foreach (var f in fields)
            {
                var input = new TextBox
                {
                    PlaceholderText = f.InputType,
                    MinWidth = 260,
                };
                draft.Inputs[f.Column] = input;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new TextBlock
                {
                    Text = f.Label + (f.Required ? " *" : "") + (f.Hidden ? "（隐藏）" : ""),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = AntTokens.FontMinor,
                    MinWidth = 160,
                    Foreground = Ant.Text,
                });
                row.Children.Add(input);
                report.Children.Add(row);
            }

            _lastForm = draft;
            status.Text = "表单已生成。填写后点「校验填写」查看结果；校验通过可生成参数化 INSERT（草稿）。";
        }
        catch (Exception ex)
        {
            status.Text = "生成表单失败：" + ex.Message;
        }
    }

    private static void RunFormValidate(StackPanel report, TextBlock status)
    {
        var draft = _lastForm;
        if (draft is null)
        {
            status.Text = "请先生成表单。";
            return;
        }

        try
        {
            var values = new Dictionary<string, string?>();
            foreach (var f in draft.Fields)
            {
                var has = draft.Inputs.TryGetValue(f.Column, out var box);
                values[f.Column] = has && box is { } b && b.Text.Length > 0 ? b.Text : null;
            }

            var v = FormSchemaBuilder.Validate(draft.Fields, values);
            if (v.Ok)
            {
                AddSection(report, "校验通过，可生成参数化 INSERT（草稿）");
                var (sql, parms) = FormSchemaBuilder.BuildInsert(draft.Table, draft.Fields, values);
                AddLine(report, sql);
                foreach (var kv in parms)
                    AddLine(report, $"  @{kv.Key} = {kv.Value}", indent: 1);
                status.Text = "校验通过。INSERT 草稿仅展示，不执行。";
            }
            else
            {
                AddSection(report, "校验未通过");
                foreach (var kv in v.Errors)
                    AddLine(report, $"  {kv.Key}: {kv.Value}", Ant.Warn, 1);
                status.Text = "有必填或类型问题，见上方明细。";
            }
        }
        catch (Exception ex)
        {
            status.Text = "校验失败：" + ex.Message;
        }
    }

    // ── 血缘图：语料建图 + 影响追溯 ──
    internal static Dictionary<string, List<LineageObject>>? ParseCorpus(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var corpus = new List<(LineageObject, string)>();
        var segs = System.Text.RegularExpressions.Regex.Split(
            text, @"(?=CREATE\s+(?:TABLE|VIEW|OR\s+REPLACE|PROC|PROCEDURE))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var seg in segs)
        {
            var seg2 = seg.Trim();
            if (seg2.Length == 0) continue;
            var m = System.Text.RegularExpressions.Regex.Match(
                seg2, @"CREATE\s+(?:OR\s+REPLACE\s+)?(TABLE|VIEW|PROC(?:EDURE)?)\s+[`\""\[]?(\w+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var kind = m.Groups[1].Value.ToUpperInvariant() switch
            {
                "TABLE" => "table",
                "VIEW" => "view",
                _ => "proc",
            };
            corpus.Add((new LineageObject(m.Groups[2].Value, kind, seg2), seg2));
        }
        return corpus.Count == 0 ? null : LineageMapper.Build(corpus);
    }

    private static void RunLineageBuild(string text, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        var map = ParseCorpus(text);
        if (map is null)
        {
            status.Text = "语料里没识别到 CREATE TABLE / VIEW / PROC 段。";
            return;
        }

        AddSection(report, "血缘地图（对象 → 引用）");
        foreach (var kv in map)
        {
            AddLine(report, $"{kv.Key}（{kv.Value.Count} 个引用）");
            foreach (var o in kv.Value)
                AddLine(report, $"  → {o.Name}（{o.Type}）", indent: 1);
        }
        status.Text = $"建图完成，共 {map.Count} 个对象。填对象名后可做影响追溯。";
    }

    private static void RunLineageImpact(string text, string target, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        target = target.Trim();
        var map = ParseCorpus(text);
        if (map is null)
        {
            status.Text = "请先粘贴语料并建图。";
            return;
        }
        if (target.Length == 0)
        {
            status.Text = "请填要评估影响的对象名。";
            return;
        }

        try
        {
            var impacts = LineageMapper.Impact(map, target);
            if (impacts.Count == 0)
            {
                status.Text = $"{target} 没有下游引用，改动风险低。";
                return;
            }

            AddSection(report, $"改动 {target} 的影响（{impacts.Count} 项）");
            foreach (var i in impacts)
            {
                var path = string.Join(" → ", i.Path);
                AddLine(report, $"{i.Object}（{i.Type}）：{path}", i.Type == "view" || i.Type == "proc" ? Ant.Warn : Ant.TextSub);
            }
            foreach (var h in LineageMapper.Hints(impacts))
                AddLine(report, h, Ant.Warn);
            status.Text = "追溯完成。下游越多，改动越要谨慎。";
        }
        catch (Exception ex)
        {
            status.Text = "追溯失败：" + ex.Message;
        }
    }

    private static void AddSection(StackPanel p, string title)
    {
        p.Children.Add(new TextBlock
        {
            Text = title, FontSize = AntTokens.FontBody, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ant.Primary, Margin = new Thickness(0, 6, 0, 0),
        });
    }

    private static void AddLine(StackPanel p, string text, Brush? brush = null, int indent = 0)
    {
        p.Children.Add(new TextBlock
        {
            Text = text, FontSize = AntTokens.FontMinor, Margin = new Thickness(indent * 16, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap, Foreground = brush ?? Ant.TextSub,
            FontFamily = new FontFamily("Consolas"),
        });
    }

    private static string CurrentDb(NodeInfo info)
    {
        if (info.Db.Length > 0) return info.Db;
        if (info.Profile.Database.Length > 0) return info.Profile.Database;
        return "";
    }
}
