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
        /// <summary>发现65：用户是否实际改过值（TextChanged 置位）——二进制列未改动不回写，防 hex 展示值被整列覆盖。</summary>
        public bool Dirty { get; set; }
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
            // 发现65：二进制大值不把 hex 塞进输入框（2MB hex 卡 UI）——显示占位说明，改动走「编辑大字段」
            bool isBinCol = Exporter.IsBinaryColumn(col.DataType);
            if (isBinCol && (initialText?.Length ?? 0) > 128)
            {
                box.PlaceholderText = $"(二进制 {HumanSize.Of(initialText!.Length / 2)}，未修改；要改请点「编辑大字段」)";
            }
            else
            {
                box.Text = initialText ?? "";
            }
            if (readOnly) box.IsReadOnly = true;

            // 批次二格2：大字段（BLOB/TEXT/IMAGE/bytea 或值超 64 字节）加「编辑大字段」按钮 → BlobEditorDialog
            bool isBlob = dt.Contains("blob") || dt.Contains("text") || dt.Contains("image") || dt.Contains("bytea")
                || (initialText?.Length ?? 0) > 64;
            if (isBlob && !readOnly)
            {
                var blobBtn = new Button { Content = "编辑大字段", FontSize = AntTokens.FontMinor, Padding = new Thickness(8, 2, 8, 2) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(blobBtn, $"BlobEdit_{col.Name}");
                blobBtn.Click += async (_, _) =>
                {
                    var edited = await BlobEditorDialog.Show(BlobOwner!, col, box.Text);
                    if (edited is not null) box.Text = edited;
                };
                var wrap = new StackPanel { Spacing = 4 };
                wrap.Children.Add(box);
                wrap.Children.Add(blobBtn);
                field = new Field
                {
                    Control = wrap,
                    GetText = () => box.Text,
                    SetEnabled = v => { box.IsEnabled = v; blobBtn.IsEnabled = v; },
                };
                box.TextChanged += (_, _) => field.Dirty = true;   // 发现65：改动即置脏
                return field;
            }
            field = new Field
            {
                Control = box,
                GetText = () => box.Text,
                SetEnabled = v => box.IsEnabled = v,
            };
            box.TextChanged += (_, _) => field.Dirty = true;   // 发现65：改动即置脏
        }
        return field;
    }

    /// <summary>大字段弹层宿主（EditRow 注入；App 层单窗，静态持有即可）。</summary>
    internal static MainWindow? BlobOwner;

    /// <summary>批次二格3：外键下拉——参考表键值候选（可编辑 ComboBox，选值保存走既有 Literal 口径）。
    /// 取不到候选（超时/无外键）由调用方回退普通文本框，不阻断编辑。</summary>
    public static Field CreateFk(ColumnSpec col, string? initialText, IReadOnlyList<string> candidates)
    {
        string header = $"{col.Name}（{col.DataType}{(col.IsKey ? " · 🔑" : "")} · 外键）";
        var combo = new ComboBox { Header = header, IsEditable = true, MinWidth = 200, ItemsSource = candidates };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(combo, $"FkCombo_{col.Name}");
        if (initialText is not null) combo.Text = initialText;
        return new Field
        {
            Control = combo,
            GetText = () => combo.Text?.Trim() ?? "",
            SetEnabled = v => combo.IsEnabled = v,
        };
    }
}
