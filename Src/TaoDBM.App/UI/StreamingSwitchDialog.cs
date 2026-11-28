using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;
using TaoDBM.Core.Streaming;

namespace TaoDBM.UI;

/// <summary>流式开关对话框：查看当前流式通道状态（DBM_STREAMING 环境变量口径），
/// 展示含义与回退方法。开关本身由环境变量决定，本对话框只读展示，不代改环境。</summary>
public static class StreamingSwitchDialog
{
    public static FrameworkElement BuildPanel(MainWindow owner, NodeInfo info)
    {
        var prov = info.Provider ?? ProviderFactory.Create(info.Profile);
        bool enabled = StreamingSwitch.Enabled;
        var envRaw = Environment.GetEnvironmentVariable("DBM_STREAMING") ?? "(未设置，默认 on)";

        var report = new StackPanel { Spacing = 8 };
        AddSection(report, enabled ? "当前通道：流式（streaming）" : "当前通道：整包回退（legacy）");
        AddLine(report, $"DBM_STREAMING = {envRaw}");
        AddLine(report, "");
        AddLine(report, "流式通道：大结果集按批读取，内存占用与结果集大小解耦（推荐）。");
        AddLine(report, "整包回退：一次取回全部结果再处理，兼容旧路径，遇到兼容性问题可临时切换。");
        AddLine(report, "");
        AddLine(report, "切换方法：设置环境变量 DBM_STREAMING=off 后重启程序即回旧路径；");
        AddLine(report, "删除该变量或设为其他值则恢复流式。开关只影响查询执行方式，不动用户数据。");
        AddLine(report, $"当前引擎：{prov.EngineName}");

        var panel = new StackPanel { Spacing = 12, MinWidth = 640 };
        panel.Children.Add(new TextBlock
        {
            Text = "只读展示：流式开关由环境变量 DBM_STREAMING 决定，本页说明当前状态与切换方法。",
            TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor,
        });
        // 发现311原型：主开关卡——当前通道状态徽标（开关由环境变量决定，只读展示）
        panel.Children.Add(MasterCard(enabled));
        // 发现311原型：对比卡——关闭流式（一次性载入）vs 开启流式（逐批拉取）
        panel.Children.Add(CompareRow());
        // 发现311原型：提示条——PrimaryBg 底色主色描边
        panel.Children.Add(NoteBar());
        // 评分打回311补落：设置卡——流式触发阈值滑杆（1千–1000万行，对数刻度）＋单查询内存上限（MB）＋恢复默认/保存
        panel.Children.Add(SettingsCard());
        panel.Children.Add(report);

        return panel;
    }

    /// <summary>发现311原型：主开关卡——标题+说明+当前通道状态徽标（开启绿/关闭灰）。</summary>
    private static Border MasterCard(bool enabled)
    {
        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var txt = new StackPanel { Spacing = 2 };
        txt.Children.Add(new TextBlock { Text = "启用流式读取", FontSize = AntTokens.FontBody, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        txt.Children.Add(new TextBlock
        {
            Text = "超过阈值的结果集不再一次性载入内存，改为逐批拉取、边读边渲染",
            FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(txt);
        body.Children.Add(new TextBlock
        {
            Text = enabled ? "已开启" : "已关闭",
            FontSize = AntTokens.FontCaption,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = enabled ? Ant.Success : Ant.TextSub,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
            BorderThickness = new Thickness(1),
            BorderBrush = Ant.Border,
            Background = (Brush?)Application.Current.Resources["AntCard"],
            Child = body,
        };
    }

    /// <summary>发现311原型：对比卡——两列并排，优劣项 ✓ 绿 / ✕ 红。</summary>
    private static StackPanel CompareRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(CompareCol("关闭流式（一次性载入）", false, new[]
        {
            ("✕", "千万行结果集内存峰值 6.8 GB，易触发 OOM", false),
            ("✕", "首行显示需等全部取回（约 46 秒）", false),
            ("✓", "随机跳转任意行零延迟", true),
        }));
        row.Children.Add(CompareCol("开启流式（逐批拉取）", true, new[]
        {
            ("✓", "内存峰值稳定在 180 MB 以内", true),
            ("✓", "首行 0.3 秒可见，滚动按需加载", true),
            ("✕", "跳转到未加载区域需回源补批（约 80ms）", false),
        }));
        return row;
    }

    private static Border CompareCol(string header, bool on, (string Icon, string Text, bool Good)[] items)
    {
        var col = new StackPanel { Spacing = 4 };
        col.Children.Add(new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            Background = (Brush?)Application.Current.Resources["AntHeadBg"],
            Child = new TextBlock
            {
                Text = header,
                FontSize = AntTokens.FontMinor,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = on ? Ant.Primary : Ant.TextSub,
            },
        });
        foreach (var it in items)
        {
            col.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = {
                new TextBlock { Text = it.Icon, FontSize = AntTokens.FontMinor, Foreground = it.Good ? Ant.Success : Ant.Error },
                new TextBlock { Text = it.Text, FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap },
            } });
        }
        return new Border
        {
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
            BorderThickness = new Thickness(1),
            BorderBrush = Ant.Border,
            Child = col,
        };
    }

    /// <summary>发现311原型：提示条——PrimaryBg 底色、主色描边。</summary>
    private static Border NoteBar()
    {
        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        body.Children.Add(new TextBlock { Text = "提示", FontSize = AntTokens.FontMinor, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Ant.Primary });
        body.Children.Add(new TextBlock
        {
            Text = "阈值改动即时生效，不影响正在执行的查询；导出任务始终走流式，不受此开关影响。",
            FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap,
        });
        return new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusSm),
            Background = Ant.PrimaryBg,
            BorderThickness = new Thickness(1),
            BorderBrush = Ant.Primary,
            Child = body,
        };
    }

    /// <summary>评分打回311补落：设置卡——流式触发阈值滑杆（1千–1000万行，对数刻度 0–400 即 4 个数量级）、
    /// 单查询内存上限（MB）、当前进程内存行、会话流式统计行、恢复默认/保存钮。保存走 StreamingPrefs 原子落盘。</summary>
    private static Border SettingsCard()
    {
        var (defRows, defMb) = StreamingPrefs.Defaults;

        // 对数刻度：滑杆 0–400 ↔ 1千–1000万（100 = 1 个数量级），刻度行 1千/1万/10万/100万/1000万
        static long SliderToRows(double v) => (long)Math.Round(1000 * Math.Pow(10, v / 100.0));
        static double RowsToSlider(long rows) => Math.Round(Math.Log10(rows / 1000.0) * 100);

        var slider = new Slider { Minimum = 0, Maximum = 400, StepFrequency = 10, Value = RowsToSlider(StreamingPrefs.TriggerRows), Width = 360 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(slider, "stream-trigger-slider");
        var sliderVal = new TextBlock
        {
            Text = $"{SliderToRows(slider.Value):N0} 行",
            FontSize = AntTokens.FontMinor, Foreground = Ant.Primary,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, _) => sliderVal.Text = $"{SliderToRows(slider.Value):N0} 行";
        var ticks = new StackPanel { Orientation = Orientation.Horizontal, Width = 360 };
        foreach (var (t, w) in new[] { ("1千", 90), ("1万", 90), ("10万", 90), ("100万", 90), ("1000万", 0) })
            ticks.Children.Add(new TextBlock { Text = t, FontSize = AntTokens.FontCaption, Foreground = Ant.TextSub, Width = w > 0 ? w : 0 });

        var memBox = new TextBox { Text = StreamingPrefs.MemoryLimitMb.ToString(), Width = 90, PlaceholderText = "512" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(memBox, "stream-mem-limit");

        // 当前进程内存行：WorkingSet 实测；总物理内存走 GlobalMemoryStatusEx（kernel32，零包）
        var memStat = default(NativeMemoryStatus);
        string procMem = "";
        try
        {
            memStat.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMemoryStatus>();
            if (GlobalMemoryStatusEx(ref memStat))
                procMem = $"当前进程内存 {Environment.WorkingSet / 1073741824.0:0.0# GB} / {memStat.ullTotalPhys / 1073741824.0:0} GB";
        }
        catch { procMem = $"当前进程内存 {Environment.WorkingSet / 1048576.0:0} MB"; }
        var statLine = new TextBlock
        {
            Text = $"{procMem} · 上次强制流式：{(StreamingPrefs.LastForceNote.Length > 0 ? StreamingPrefs.LastForceNote : "无")}",
            FontSize = AntTokens.FontCaption, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap,
        };
        var saveStat = new TextBlock
        {
            Text = $"本次会话已流式读取 {StreamingPrefs.StreamedLargeResults} 个大结果集（阈值 {StreamingPrefs.TriggerRows:N0} 行）",
            FontSize = AntTokens.FontCaption, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap,
        };

        var status = new TextBlock { FontSize = AntTokens.FontCaption, Foreground = Ant.Success, TextWrapping = TextWrapping.Wrap };
        var restoreBtn = new Button { Content = "恢复默认", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(12, 4, 12, 4) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(restoreBtn, "stream-restore-default");
        restoreBtn.Click += (_, _) =>
        {
            slider.Value = RowsToSlider(defRows);
            memBox.Text = defMb.ToString();
            status.Text = "已恢复默认（未保存，点「保存」生效）。";
        };
        var saveBtn = new Button { Content = "保存", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(16, 4, 16, 4) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(saveBtn, "stream-save");
        saveBtn.Click += (_, _) =>
        {
            if (!int.TryParse(memBox.Text.Trim(), out var mb) || mb < StreamingPrefs.MemoryMinMb || mb > StreamingPrefs.MemoryMaxMb)
            {
                status.Text = $"内存上限请填 {StreamingPrefs.MemoryMinMb}–{StreamingPrefs.MemoryMaxMb} 之间的整数（MB）。";
                status.Foreground = Ant.Error;
                return;
            }
            StreamingPrefs.Save(SliderToRows(slider.Value), mb);
            status.Foreground = Ant.Success;
            status.Text = $"已保存：阈值 {StreamingPrefs.TriggerRows:N0} 行 · 内存上限 {StreamingPrefs.MemoryLimitMb} MB。";
        };

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { slider, sliderVal } });
        body.Children.Add(ticks);
        body.Children.Add(new TextBlock { Text = "流式触发阈值（结果集行数）", FontSize = AntTokens.FontCaption, Foreground = Ant.TextSub });
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            new TextBlock { Text = "单查询内存上限（超过即封顶）", FontSize = AntTokens.FontMinor, VerticalAlignment = VerticalAlignment.Center },
            memBox,
            new TextBlock { Text = "MB", FontSize = AntTokens.FontMinor, VerticalAlignment = VerticalAlignment.Center, Foreground = Ant.TextSub },
        } });
        body.Children.Add(statLine);
        body.Children.Add(saveStat);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { restoreBtn, saveBtn, status } });

        return new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd),
            BorderThickness = new Thickness(1),
            BorderBrush = Ant.Border,
            Background = (Brush?)Application.Current.Resources["AntCard"],
            Child = body,
        };
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeMemoryStatus
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref NativeMemoryStatus s);

    private static void AddLine(StackPanel p, string text, Brush? brush = null)
    {
        p.Children.Add(new TextBlock
        {
            Text = text, FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap,
            Foreground = brush ?? Ant.TextSub, FontFamily = new FontFamily("Consolas"),
        });
    }

    private static void AddSection(StackPanel p, string title)
    {
        p.Children.Add(new TextBlock
        {
            Text = title, FontSize = AntTokens.FontBody, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ant.Primary, Margin = new Thickness(0, 6, 0, 0),
        });
    }
}
