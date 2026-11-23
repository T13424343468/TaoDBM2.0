using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>整改二批格4：行编辑按列类型出控件——整型/小数出 NumberBox（非数字拦截、非法值红框）、
/// 日期/时间出 DatePicker＋TimePicker（可清空）、布尔出三态 CheckBox（未设置/TRUE/FALSE，NULL 仍由外层 NULL 勾选框表达）、
/// 其余保持 TextBox；控件值统一转字符串，提交仍走 Exporter.Literal 同口径，INSERT/UPDATE 字面量与旧口径一致。</summary>
public static class RowEditorFactory
{
    /// <summary>行编辑字段：控件＋取值/启停委托（保存与删除路径统一走 GetText）。</summary>
    public sealed class Field
    {
        public required FrameworkElement Control { get; init; }
        public required Func<string> GetText { get; init; }
        public required Action<bool> SetEnabled { get; init; }
    }

    public static Field Create(ColumnSpec col, string? initialText, bool readOnly)
    {
        string dt = (col.DataType ?? "").ToLowerInvariant();
        bool isBool = dt.Contains("bool") || dt == "bit";
        bool isDate = !isBool && (dt.Contains("date") || dt.Contains("time"));
        bool isNumeric = !isBool && !isDate && CsvImporter.IsNumeric(col.DataType ?? "");
        string header = $"{col.Name}（{col.DataType}{(col.IsKey ? " · 🔑" : "")}）";

        Field field;
        if (isNumeric)
        {
            var nb = new NumberBox
            {
                Header = header,
                PlaceholderText = "数字",
                MinWidth = 200,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(nb, $"EditRow_{col.Name}");
            if (double.TryParse(initialText, System.Globalization.CultureInfo.InvariantCulture, out var d)) nb.Value = d;
            else if (!string.IsNullOrEmpty(initialText)) nb.Text = initialText;
            if (readOnly) nb.IsEnabled = false;   // NumberBox 无 IsReadOnly，与日期/布尔分支同口径用禁用表达只读
            field = new Field
            {
                Control = nb,
                GetText = () =>
                {
                    string t = nb.Text?.Trim() ?? "";
                    if (t.Length > 0 && double.IsNaN(nb.Value))
                    {
                        nb.BorderBrush = Ant.Error;   // 非法数字红框提示
                        return t;
                    }
                    return t.Length > 0 ? t : (double.IsNaN(nb.Value) ? "" : nb.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                },
                SetEnabled = v => nb.IsEnabled = v,
            };
        }
        else if (isDate)
        {
            var dp = new DatePicker { Header = header, DayVisible = true, MonthVisible = true, YearVisible = true };
            var tp = new TimePicker { MinuteIncrement = 1 };
            var clearBtn = new Button { Content = "清空", FontSize = AntTokens.FontMinor };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(dp);
            row.Children.Add(tp);
            row.Children.Add(clearBtn);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(row, $"EditRow_{col.Name}");
            // DatePicker.Date 为非可空 DateTimeOffset，清空语义用 hasDate 标记承载；重选日期经 DateChanged 恢复。
            var hasDate = false;
            if (DateTime.TryParse(initialText, out var parsed))
            {
                dp.Date = new DateTimeOffset(parsed);
                tp.SelectedTime = parsed.TimeOfDay;
                hasDate = true;
            }
            dp.DateChanged += (_, _) => hasDate = true;
            clearBtn.Click += (_, _) => { hasDate = false; tp.SelectedTime = null; };
            field = new Field
            {
                Control = row,
                GetText = () => !hasDate ? "" :
                    (dp.Date.DateTime.Date + (tp.SelectedTime ?? TimeSpan.Zero)).ToString("yyyy-MM-dd HH:mm:ss"),
                SetEnabled = v => { dp.IsEnabled = v; tp.IsEnabled = v; clearBtn.IsEnabled = v; },
            };
            if (readOnly) { dp.IsEnabled = false; tp.IsEnabled = false; clearBtn.IsEnabled = false; }
        }
        else if (isBool)
        {
            var cb = new CheckBox { Content = header, MinWidth = 200 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(cb, $"EditRow_{col.Name}");
            if (bool.TryParse(initialText, out var b)) cb.IsChecked = b;
            else if (initialText == "1") cb.IsChecked = true;
            else if (initialText == "0") cb.IsChecked = false;
            else cb.IsChecked = null;   // 三态：未设置（留空语义，新增行省略该列保留默认值）
            if (readOnly) cb.IsEnabled = false;
            field = new Field
            {
                Control = cb,
                GetText = () => cb.IsChecked switch { true => "TRUE", false => "FALSE", _ => "" },
                SetEnabled = v => cb.IsEnabled = v,
            };
        }
        else
        {
            var box = new TextBox
            {
                Header = header,
                PlaceholderText = "留空＝空字符串；勾选左侧 NULL 存 NULL",
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, $"EditRow_{col.Name}");
            box.Text = initialText ?? "";
            if (readOnly) box.IsReadOnly = true;
            field = new Field
            {
                Control = box,
                GetText = () => box.Text,
                SetEnabled = v => box.IsEnabled = v,
            };
        }
        return field;
    }
}
