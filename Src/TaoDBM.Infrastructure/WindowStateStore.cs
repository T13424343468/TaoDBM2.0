using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>主窗口状态项：位置、尺寸、最大化态与分栏布局（重启恢复用）。</summary>
public class WindowStateItem
{
    public double X { get; set; } = double.NaN;          // NaN = 未记录，回默认居中
    public double Y { get; set; } = double.NaN;
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
    public double LeftPaneWidth { get; set; }            // 对象树列宽（拖动分隔条后）
    public bool TreeCollapsed { get; set; }              // 对象树收起态
}

/// <summary>窗口状态存取：%APPDATA%\TaoDBM\windowstate.json；损坏/缺失回落默认（默认尺寸逻辑在 UI 侧按屏幕工作区计算）。</summary>
public static class WindowStateStore
{
    private static string _file = "";
    private static string File => _file.Length > 0 ? _file : TaoPaths.FilePath("windowstate.json");

    /// <summary>测试或自定义存储位置。</summary>
    public static void UseFile(string path) => _file = path;

    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static WindowStateItem Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new WindowStateItem();
            return JsonSerializer.Deserialize<WindowStateItem>(System.IO.File.ReadAllText(File)) ?? new WindowStateItem();
        }
        catch { return new WindowStateItem(); }
    }

    public static void Save(WindowStateItem item)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        AtomicWrite.Text(File, JsonSerializer.Serialize(item, Opt));
    }

    /// <summary>默认尺寸口径（需求合并稿第十五章）：工作区 80% 封顶 1600×900，且不低于 960×640
    /// （下限不越工作区：工作区本身更小时保持吃满，微屏不受下限影响）；工作区宽 &lt; 1350 时吃满工作区。</summary>
    public static (double Width, double Height) DefaultSize(double workWidth, double workHeight)
    {
        if (workWidth < 1350) return (workWidth, workHeight);
        var w = Math.Max(960, Math.Min(workWidth * 0.8, 1600));
        var h = workHeight >= 640
            ? Math.Max(640, Math.Min(workHeight * 0.8, 900))
            : Math.Min(workHeight * 0.8, 900);
        return (w, h);
    }

    /// <summary>恢复位置与尺寸校验：记录坐标在任一显示器工作区内且尺寸可见、且记录尺寸不越出该屏
    /// （拔显示器/换小屏/越界记录回默认居中，批次H 补尺寸侧校验）。</summary>
    public static bool IsRestorable(WindowStateItem item, IReadOnlyList<(double X, double Y, double W, double H)> monitors)
    {
        if (monitors.Count == 0) return false;
        if (double.IsNaN(item.X) || double.IsNaN(item.Y) || item.Width <= 0 || item.Height <= 0) return false;
        return monitors.Any(m =>
            item.X + 40 >= m.X && item.Y + 40 >= m.Y &&
            item.X + 40 <= m.X + m.W && item.Y + 40 <= m.Y + m.H &&
            item.Width <= m.W && item.Height <= m.H);
    }

    /// <summary>分栏宽度夹取：对象树最窄 200px、主区不小于 480px（拖动分隔条口径）。</summary>
    public static double ClampLeftPane(double leftWidth, double totalWidth)
    {
        var max = Math.Max(200, totalWidth - 480);
        return Math.Clamp(leftWidth, 200, max);
    }
}
