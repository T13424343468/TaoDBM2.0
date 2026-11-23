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
    /// <summary>格15：带上下文进入——table 非空预填表单表名并自动生成；last 非空把结果快照序列化预填图表输入；
    /// 无上下文入口行为不变。预填在状态行标注「已带入上下文」。</summary>
    public static void Show(MainWindow owner, NodeInfo info, string? table = null, QueryResult? last = null)
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
        var formSubmitBtn = new Button { Content = "提交写入", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        formBtn.Click += async (_, _) => await RunForm(owner, info, formTableBox.Text, report, status);
        formValidateBtn.Click += (_, _) => RunFormValidate(report, status);
        formSubmitBtn.Click += async (_, _) =>
        {
            if (_lastForm is null) { status.Text = "请先生成表单。"; return; }
            await SubmitFormWriteAsync(_lastForm, owner, report, status);
        };

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
        formRow.Children.Add(formSubmitBtn);

        var lineageRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        lineageRow.Children.Add(lineageTargetBox);
        lineageRow.Children.Add(lineageBuildBtn);
        lineageRow.Children.Add(lineageImpactBtn);

        var panel = new StackPanel { Spacing = 10, MinWidth = 680 };
        panel.Children.Add(new TextBlock
        {
            Text = "设计辅助：图表只对粘贴结果聚合；表单可提交写入（预览确认后落库并留痕）；血缘只对粘贴语料建图。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ant.TextSub,
            FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(chartBox);
        panel.Children.Add(chartRow);
        panel.Children.Add(formRow);
        panel.Children.Add(lineageBox);
        panel.Children.Add(lineageRow);

        // 格15：上下文预填——表名进表单（自动生成），结果快照进图表输入；状态行标注可修改。
        string ctxNote = "";
        if (!string.IsNullOrEmpty(table))
        {
            formTableBox.Text = table;
            ctxNote = $"已带入上下文：表 {table}";
            AppLog.Write($"[设计辅助] 上下文 表={table}");   // 走查 S82 断言锚点
            _ = RunForm(owner, info, table, report, status);   // 自动生成表单
        }
        if (last is { Columns.Count: > 0 })
        {
            chartBox.Text = Snapshot(last);
            ctxNote = ctxNote.Length > 0 ? ctxNote : "已带入上下文：查询结果快照";
            AppLog.Write("[设计辅助] 上下文 结果快照");
        }
        if (ctxNote.Length > 0)
            panel.Children.Insert(0, new TextBlock
            {
                Text = ctxNote + "，可修改。",
                Foreground = Ant.Primary,
                FontSize = AntTokens.FontMinor,
                TextWrapping = TextWrapping.Wrap,
            });

        var dlg = new ContentDialog
        {
            Title = "设计辅助（图表规范 / 表单构建 / 血缘图）",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
    }

    /// <summary>格15：结果快照序列化为图表输入格式（列名行＋数据行，分号分隔，截断前 100 行）。</summary>
    private static string Snapshot(QueryResult r)
    {
        var parts = new List<string> { string.Join(",", r.Columns) };
        foreach (var row in r.Rows.Take(100))
            parts.Add(string.Join(",", row.Select(Exporter.Format)));
        return string.Join("; ", parts);
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
        public IDbProvider Prov = null!;
        public string Db = "";
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
            var draft = new FormDraft { Table = table, Fields = fields, Prov = prov, Db = db };
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
    // s18 表单化录入提交写入：校验 → 写入预览（参数化 INSERT + 实际执行语句）→ 确认后落库并写查询历史留痕。
    private static async Task SubmitFormWriteAsync(FormDraft draft, MainWindow owner, StackPanel report, TextBlock status)
    {
        var values = new Dictionary<string, string?>();
        foreach (var f in draft.Fields)
        {
            var has = draft.Inputs.TryGetValue(f.Column, out var box);
            values[f.Column] = has && box is { } b && b.Text.Length > 0 ? b.Text : null;
        }
        var v = FormSchemaBuilder.Validate(draft.Fields, values);
        if (!v.Ok)
        {
            AddSection(report, "校验未通过，未提交");
            foreach (var kv in v.Errors)
                AddLine(report, $"  {kv.Key}: {kv.Value}", Ant.Warn, 1);
            status.Text = "有必填或类型问题，修正后再提交。";
            return;
        }

        string sql, literal;
        try
        {
            (sql, _) = FormSchemaBuilder.BuildInsert(draft.Table, draft.Fields, values);
            literal = BuildLiteralInsert(draft, values);
        }
        catch (Exception ex)
        {
            status.Text = "生成写入语句失败：" + ex.Message;
            return;
        }

        var prevBox = new TextBox
        {
            AcceptsReturn = true,
            IsReadOnly = true,
            Text = "-- 将执行（字面值版）\n" + literal + "\n\n-- 参数化形态\n" + sql + "\n"
                + string.Join("\n", FormSchemaBuilder.BuildInsert(draft.Table, draft.Fields, values).Params.Select(p => $"@{p.Key} = {p.Value}")),
            FontFamily = new FontFamily("Consolas"),
            MinWidth = 620,
            MinHeight = 240,
            MaxHeight = 420,
            TextWrapping = TextWrapping.Wrap,
        };
        var dlg = new ContentDialog
        {
            Title = $"表单录入 · 写入预览（{draft.Db}.{draft.Table}）",
            Content = prevBox,
            PrimaryButtonText = "确认写入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = owner.Content.XamlRoot,
        };
        if (await dlg.ShowThemedAsync() != ContentDialogResult.Primary) { status.Text = "已取消，未写入。"; return; }

        try
        {
            var list = await draft.Prov.Execute(draft.Db, literal);
            var errs = list.Where(r => r.Message.StartsWith("错误", StringComparison.Ordinal)).ToList();
            if (errs.Count == 0)
            {
                QueryHistory.Add(draft.Prov.EngineName, draft.Db, "-- 表单录入：" + draft.Table + "\n" + literal);
                AddSection(report, "写入成功");
                AddLine(report, literal);
                status.Text = "已写入 1 行，语句已记入查询历史留痕。";
            }
            else
            {
                AddSection(report, "写入失败");
                foreach (var r in errs) AddLine(report, r.Message, Ant.Warn, 1);
                status.Text = "写入被数据库拒绝，未留痕。";
            }
        }
        catch (Exception ex)
        {
            status.Text = "写入失败：" + ex.Message;
        }
    }

    // 字面值版 INSERT：校验已过，文本/时间类加引号并转义单引号，数字与布尔不加引号。
    private static string BuildLiteralInsert(FormDraft draft, Dictionary<string, string?> values)
    {
        var cols = new List<string>();
        var vals = new List<string>();
        foreach (var f in draft.Fields)
        {
            if (f.Hidden) continue;
            values.TryGetValue(f.Column, out var raw);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var v = raw.Trim();
            string lit = f.InputType switch
            {
                "number" => v,
                "bool" => v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" ? "1" : "0",
                _ => "'" + v.Replace("'", "''") + "'",
            };
            cols.Add(f.Column);
            vals.Add(lit);
        }
        return $"INSERT INTO {draft.Table} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)});";
    }

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
