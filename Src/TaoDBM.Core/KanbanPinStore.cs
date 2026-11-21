using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>看板钉住与定时刷新（批次E，2026-11-20 老板令「全覆盖」）：钉住图表/查询卡片，按间隔自动重跑。
/// 纯文件逻辑，Tao 系列存储家法（默认 TaoPaths，测试 UseFile 注入），AtomicWrite 原子落盘，LogicTest 直测。
/// 刷新取数走流式底座由调用方（TaskCenter/StreamingQuery）承接，本类只管「钉了什么、何时到期」。</summary>
public static class KanbanPinStore
{
    private static readonly object Gate = new();

    private static string _file = "";
    private static string File => _file.Length > 0 ? _file : TaoPaths.FilePath("kanban-pins.json");

    /// <summary>测试或自定义存储位置。</summary>
    public static void UseFile(string path) => _file = path;

    /// <summary>定时刷新最小间隔：低于一律夹到 5s，防误配空转。</summary>
    public const int MinIntervalSeconds = 5;

    public sealed record Pin(string PinId, string Title, string Sql, string Engine, string? ChartId = null,
        int IntervalSeconds = 0, DateTimeOffset LastRefreshAt = default, string? Db = null)
    {
        /// <summary>intervalSeconds &lt;= 0 表示不自动刷新，仅钉住。</summary>
        public bool AutoRefresh => IntervalSeconds > 0;
    }

    /// <summary>钉住或更新（同 PinId 覆盖；间隔低于下限夹到 5s）。</summary>
    public static void Upsert(Pin pin)
    {
        lock (Gate)
        {
            var pin2 = pin with { IntervalSeconds = Math.Max(pin.IntervalSeconds, pin.IntervalSeconds <= 0 ? 0 : MinIntervalSeconds) };
            var all = LoadAll().Where(p => p.PinId != pin2.PinId).ToList();
            all.Add(pin2);
            SaveAll(all);
        }
    }

    public static bool IsPinned(string pinId) { lock (Gate) { return LoadAll().Any(p => p.PinId == pinId); } }

    /// <summary>摘除钉住。不存在返回 false。</summary>
    public static bool Unpin(string pinId)
    {
        lock (Gate)
        {
            var all = LoadAll();
            var rest = all.Where(p => p.PinId != pinId).ToList();
            if (rest.Count == all.Count) return false;
            SaveAll(rest);
            return true;
        }
    }

    /// <summary>全部钉住项（文件缺失/损坏回落空表，绝不闪退）。</summary>
    public static List<Pin> All() { lock (Gate) { return LoadAll(); } }

    /// <summary>到期清单：AutoRefresh 且 LastRefreshAt + 间隔 ≤ now。到期的 LastRefreshAt 由调用方重跑后 Touch。</summary>
    public static List<Pin> Due(DateTimeOffset now)
    {
        lock (Gate) { return LoadAll().Where(p => p.AutoRefresh && p.LastRefreshAt + TimeSpan.FromSeconds(p.IntervalSeconds) <= now).ToList(); }
    }

    /// <summary>重跑成功后刷新水位。</summary>
    public static void Touch(string pinId, DateTimeOffset at)
    {
        lock (Gate)
        {
            var all = LoadAll();
            for (int i = 0; i < all.Count; i++)
                if (all[i].PinId == pinId) { all[i] = all[i] with { LastRefreshAt = at }; SaveAll(all); return; }
        }
    }

    static List<Pin> LoadAll()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new List<Pin>();
            var pins = JsonSerializer.Deserialize<List<Pin>>(System.IO.File.ReadAllText(File));
            return pins ?? new List<Pin>();
        }
        catch (Exception ex) { Core.AppLog.Write($"看板钉住读取失败，回落空表: {ex.Message}"); return new List<Pin>(); }
    }

    static void SaveAll(List<Pin> pins)
    {
        try
        {
            var dir = Path.GetDirectoryName(File);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicWrite.Text(File, JsonSerializer.Serialize(pins, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Core.AppLog.Write($"看板钉住保存失败: {ex.Message}"); }
    }
}
