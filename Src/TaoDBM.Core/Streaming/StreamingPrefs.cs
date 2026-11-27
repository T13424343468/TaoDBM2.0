using System.IO;
using System.Text.Json;

namespace TaoDBM.Core.Streaming;

/// <summary>
/// 流式偏好（评分打回311补落）：流式触发阈值（结果集行数，1千–1000万）与单查询内存上限（MB，超过即封顶）。
/// 环境变量 DBM_STREAMING 仍是总开关；本偏好只调流式的触发与封顶口径。
/// 落盘 TaoPaths 数据根（AtomicWrite 原子替换家法），损坏、缺失回落默认，绝不闪退。
/// </summary>
public static class StreamingPrefs
{
    /// <summary>流式触发阈值（行）：结果集超过该行数按流式口径统计与提示。1千–1000万。</summary>
    public static long TriggerRows { get; private set; } = 100_000;

    /// <summary>单查询内存上限（MB）：物化字节数估算超过该值即封顶截断并落账提示。</summary>
    public static int MemoryLimitMb { get; private set; } = 512;

    public const long TriggerMin = 1_000;
    public const long TriggerMax = 10_000_000;
    public const int MemoryMinMb = 64;
    public const int MemoryMaxMb = 8_192;

    /// <summary>会话统计：本次会话已流式读取的大结果集个数（超过触发阈值口径）。</summary>
    public static int StreamedLargeResults;

    /// <summary>会话统计：上次强制流式（时间＋语句摘要），空=尚无。</summary>
    public static string LastForceNote = "";

    /// <summary>恢复默认口径（滑杆/输入框回默认值用，不落盘，等保存）。</summary>
    public static (long Rows, int Mb) Defaults => (100_000, 512);

    private static string File => TaoPaths.FilePath("streaming-prefs.json");

    static StreamingPrefs() => Load();   // 启动即读盘，损坏缺失回落默认

    public static void Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return;
            var v = JsonSerializer.Deserialize<Prefs>(System.IO.File.ReadAllText(File));
            if (v is null) return;
            TriggerRows = Math.Clamp(v.TriggerRows, TriggerMin, TriggerMax);
            MemoryLimitMb = Math.Clamp(v.MemoryLimitMb, MemoryMinMb, MemoryMaxMb);
        }
        catch (Exception ex)
        {
            AppLog.Write($"流式偏好读取失败，回落默认: {ex.Message}");
        }
    }

    public static void Save(long triggerRows, int memoryLimitMb)
    {
        TriggerRows = Math.Clamp(triggerRows, TriggerMin, TriggerMax);
        MemoryLimitMb = Math.Clamp(memoryLimitMb, MemoryMinMb, MemoryMaxMb);
        AtomicWrite.Text(File, JsonSerializer.Serialize(new Prefs { TriggerRows = TriggerRows, MemoryLimitMb = MemoryLimitMb }));
        AppLog.Write($"[流式偏好] 保存 阈值={TriggerRows:N0}行 内存上限={MemoryLimitMb}MB");
    }

    /// <summary>会话统计口径：结果行数超过触发阈值时记账（两通道共用，QueryRouter/WorkTab 调用）。</summary>
    public static void NoteLargeResult(long rows, string title)
    {
        if (rows < TriggerRows) return;
        StreamedLargeResults++;
        LastForceNote = $"{DateTime.Now:HH:mm:ss}（{title}）";
    }

    private sealed class Prefs
    {
        public long TriggerRows { get; set; } = 100_000;
        public int MemoryLimitMb { get; set; } = 512;
    }
}
