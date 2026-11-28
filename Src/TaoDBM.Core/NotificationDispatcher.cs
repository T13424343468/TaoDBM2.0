using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>P-4 系统推送级：事件种类。Key 语义见 NotifyEvent；各值对应应用内的功能入口路由。</summary>
public enum NotifyKind
{
    /// <summary>危险操作执行完成（删连接、清表等带确认的操作）。</summary>
    RiskySqlDone,
    /// <summary>备份结果。</summary>
    Backup,
    /// <summary>还原结果。</summary>
    Restore,
    /// <summary>迁移 / 同步结果。</summary>
    Migration,
    /// <summary>长任务完成（任务中心队列中的查询/导出/同步/导入）。</summary>
    LongTask,
    /// <summary>健康检查异常（体检报告降分、发现 danger 级问题时）。</summary>
    HealthCheck,
}

/// <summary>待推送事件：Key=同类事件去重键（如连接名/库名/任务号），空串按种类整体去重。</summary>
public sealed record NotifyEvent(NotifyKind Kind, string Key, string Title, string Message, DateTime OccurredAt);

/// <summary>
/// 通知选项：总开关、各事件种类开关、免打扰时段（支持跨零点的「23:00-07:00」）、同类事件节流窗口。
/// 全部由用户配置；Start==End 视为零长度窗口（不拦截），理由见各判定分支。
/// </summary>
public sealed record NotifyOptions
{
    /// <summary>系统级通知总开关。2026-11-28 拍板：默认改关——应用内为主，系统通知需用户显式打开。</summary>
    public bool Enabled { get; init; } = false;
    public bool DndEnabled { get; init; }
    public TimeOnly DndStart { get; init; } = new(23, 0);
    public TimeOnly DndEnd { get; init; } = new(7, 0);
    /// <summary>同类事件节流窗口（分钟）：窗口内同 Key 只弹第一条；&lt;=0 表示不节流。</summary>
    public double ThrottleMinutes { get; init; } = 5;

    public IReadOnlyDictionary<NotifyKind, bool> KindToggles { get; init; } = AllOn();

    public static IReadOnlyDictionary<NotifyKind, bool> AllOn() =>
        Enum.GetValues<NotifyKind>().ToDictionary(k => k, _ => true);

    /// <summary>单类开关：未配置按开启处理，新增事件种类不因旧配置文件静默丢失。</summary>
    public bool IsKindEnabled(NotifyKind kind) => KindToggles.TryGetValue(kind, out var on) ? on : true;

    /// <summary>免打扰判定：t 是否落在窗口内。Start&lt;End 为普通窗口；Start&gt;End 为跨零点；
    /// Start==End 零长度窗口不拦截（避免误配成全天免打扰且无法自救）。</summary>
    public bool InDnd(TimeOnly t)
    {
        if (!DndEnabled || DndStart == DndEnd) return false;
        return DndStart < DndEnd ? t >= DndStart && t < DndEnd : t >= DndStart || t < DndEnd;
    }
}

/// <summary>判定结论：Notify=是否推送；Reason=ok/global-off/kind-off/dnd/throttled，写日志可追溯。</summary>
public sealed record NotifyDecision(bool Notify, string Reason)
{
    public static NotifyDecision Ok() => new(true, "ok");
    public static NotifyDecision Off(string reason) => new(false, reason);
}

/// <summary>
/// 通知调度引擎（纯逻辑、可注入时钟）：哪些事件够格推系统通知。
/// 判定顺序：总开关 → 事件种类开关 → 免打扰时段 → 同类事件节流；通过即记录节流时间戳，
/// 被节流/免打扰拦下的不记录（窗口过期后下一条自然通过，不会互相顶掉）。
/// </summary>
public sealed class NotificationDispatcher
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _lastAccepted = new();
    private readonly Func<DateTime> _clock;

    /// <summary>当前选项；调用方可在设置变化后整体替换。</summary>
    public NotifyOptions Options { get; set; }

    public NotificationDispatcher(NotifyOptions? options = null, Func<DateTime>? clock = null)
    {
        Options = options ?? new NotifyOptions();
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>判定并登记：够格推送返回 ok，同时记录该 Key 的节流起点。</summary>
    public NotifyDecision Evaluate(NotifyEvent evt)
    {
        if (!Options.Enabled) return NotifyDecision.Off("global-off");
        if (!Options.IsKindEnabled(evt.Kind)) return NotifyDecision.Off("kind-off");
        var now = _clock();
        if (Options.InDnd(TimeOnly.FromDateTime(now))) return NotifyDecision.Off("dnd");

        var key = ThrottleKey(evt);
        lock (_gate)
        {
            if (Options.ThrottleMinutes > 0 &&
                _lastAccepted.TryGetValue(key, out var last) &&
                (now - last).TotalMinutes < Options.ThrottleMinutes)
                return NotifyDecision.Off("throttled");
            _lastAccepted[key] = now;
        }
        return NotifyDecision.Ok();
    }

    /// <summary>便捷口：只要「推不推」的调用方用。</summary>
    public bool TryAccept(NotifyEvent evt) => Evaluate(evt).Notify;

    /// <summary>节流键：事件种类 + 去重键（键两端空白不参与区分，空串即整类共用一个桶）。</summary>
    public static string ThrottleKey(NotifyEvent evt) => evt.Kind + ":" + evt.Key.Trim();

    /// <summary>清空节流记录（切换配置或测试用）。</summary>
    public void Reset()
    {
        lock (_gate) _lastAccepted.Clear();
    }
}

/// <summary>通知选项持久化：JSON 落 TaoPaths 根（notify.json），损坏、缺失安全回落默认，绝不闪退。</summary>
public static class NotifyPrefs
{
    private static string _file = "";
    private static string File => _file.Length > 0 ? _file : TaoPaths.FilePath("notify.json");

    /// <summary>测试或自定义存储位置。</summary>
    public static void UseFile(string path) => _file = path;

    public static NotifyOptions Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new NotifyOptions();
            return JsonSerializer.Deserialize<NotifyOptions>(System.IO.File.ReadAllText(File)) ?? new NotifyOptions();
        }
        catch (Exception ex)
        {
            AppLog.Write($"通知偏好读取失败，回落默认: {ex.Message}");
            return new NotifyOptions();
        }
    }

    public static void Save(NotifyOptions options)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            AtomicWrite.Text(File, ToJson(options));
        }
        catch (Exception ex) { AppLog.Write($"通知偏好保存失败: {ex.Message}"); }
    }

    public static string ToJson(NotifyOptions options) =>
        JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true });

    public static NotifyOptions? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<NotifyOptions>(json); }
        catch (JsonException) { return null; }
        catch (ArgumentNullException) { return null; }
    }
}
