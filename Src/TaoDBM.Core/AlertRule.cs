using System.Text;
using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>
/// 安全线/智能线引擎：异常告警中心（设计稿 §11，P-4 通道应用内为主）。
/// 规则 = 指标 + 比较条件 + 持续时长 + 动作；评估为纯逻辑，不连库；
/// 指标采集由调用方完成（连接数/锁等待/复制状态/复制延迟）。
/// 防轰炸：同一规则在冷却窗内不重复触发，且每次巡检每条规则至多一条事件（重复触发合并）。
/// </summary>
public sealed class AlertRule
{
    public string Name { get; set; } = "";
    /// <summary>指标：connections / lock_waits / replication_break（1=从库断流）/ replication_lag（秒）。</summary>
    public string Metric { get; set; } = "connections";
    /// <summary>比较符：&gt; / &gt;= / &lt; / &lt;= / ==。</summary>
    public string Op { get; set; } = ">";
    public double Threshold { get; set; }
    /// <summary>需持续满足的分钟数（0=立即触发；持续判定由调用方按巡检历史执行，引擎只记口径）。</summary>
    public int DurationMinutes { get; set; }
    /// <summary>动作：inapp=应用内提醒；system=系统通知（P-4 默认关，走 NotifyPrefs 开关）。</summary>
    public string Action { get; set; } = "inapp";
    /// <summary>防轰炸冷却窗（分钟）：窗内同一规则不重复触发。</summary>
    public int CooldownMinutes { get; set; } = 5;
    public bool Enabled { get; set; } = true;
    public string? LastFiredUtc { get; set; }
    /// <summary>发现115：持续判定起点（首次满足条件的时刻，ISO 往返格式）；条件中断即清零。</summary>
    public string? MetSinceUtc { get; set; }
    public int TriggerCount { get; set; }
}

/// <summary>一次触发的告警事件（触发记录可回溯）。</summary>
public sealed record AlertEvent(string RuleName, string Metric, double Value, string Message, string Severity, string FiredAtUtc);

public static class AlertEngine
{
    /// <summary>规则模板（设计稿：规则模板化创建）。</summary>
    public static readonly (string Name, string Metric, string Op, double Threshold, int DurationMinutes, string Action, int CooldownMinutes)[] Templates =
    {
        ("连接数过高", "connections", ">", 80, 5, "inapp", 5),
        ("复制中断", "replication_break", "==", 1, 0, "inapp", 0),
        ("锁等待激增", "lock_waits", ">", 10, 0, "inapp", 10),
        ("复制延迟过大", "replication_lag", ">", 60, 0, "inapp", 10),
    };

    public static AlertRule FromTemplate((string Name, string Metric, string Op, double Threshold, int DurationMinutes, string Action, int CooldownMinutes) t) =>
        new() { Name = t.Name, Metric = t.Metric, Op = t.Op, Threshold = t.Threshold, DurationMinutes = t.DurationMinutes, Action = t.Action, CooldownMinutes = t.CooldownMinutes };

    public static bool IsMet(AlertRule r, double value) => r.Op switch
    {
        ">" => value > r.Threshold,
        ">=" => value >= r.Threshold,
        "<" => value < r.Threshold,
        "<=" => value <= r.Threshold,
        "==" => value == r.Threshold,
        _ => false,
    };

    public static string ConditionText(AlertRule r) => r.Metric switch
    {
        "connections" => $"连接数 {r.Op} {r.Threshold:0}（上限），持续 {r.DurationMinutes} 分钟",
        "lock_waits" => $"锁等待数 {r.Op} {r.Threshold:0}，持续 {r.DurationMinutes} 分钟",
        "replication_break" => "从库断流，立即提醒（红色）",
        "replication_lag" => $"复制延迟 {r.Op} {r.Threshold:0} 秒",
        _ => $"{r.Metric} {r.Op} {r.Threshold}",
    };

    public static string Message(AlertRule r, double value) => r.Metric switch
    {
        "connections" => $"当前连接数 {value:0}，超过上限 {r.Threshold:0}。",
        "lock_waits" => $"当前锁等待 {value:0} 个，超过 {r.Threshold:0}。",
        "replication_break" => "从库断流：复制目录未读到在副本，请立即核对主从状态。",
        "replication_lag" => $"当前复制延迟 {value:0} 秒，超过 {r.Threshold:0} 秒。",
        _ => $"{r.Metric} 当前 {value}。",
    };

    /// <summary>评估一轮：条件满足且不在冷却窗内的规则各出一条事件（每规则至多一条=合并防轰炸）。
    /// 发现115：持续时长承诺兑现——DurationMinutes>0 时须连续满足（首次满足只记 MetSinceUtc，不满持续分钟数不触发）；
    /// 复制中断为红色（Severity=error），其余 warn。命中后更新规则的 LastFiredUtc/TriggerCount。</summary>
    public static List<AlertEvent> Evaluate(IEnumerable<AlertRule> rules, IReadOnlyDictionary<string, double> metrics, DateTime nowUtc)
    {
        var fired = new List<AlertEvent>();
        foreach (var r in rules)
        {
            if (!r.Enabled || !metrics.TryGetValue(r.Metric, out var v)) continue;
            bool met = IsMet(r, v);
            if (!met)
            {
                r.MetSinceUtc = null;   // 中断即清零，重新计时
                continue;
            }
            // 发现115：持续判定——首次满足记起点，未满持续分钟数不触发（文案承诺「持续 N 分钟」即真持续）
            if (r.DurationMinutes > 0)
            {
                if (r.MetSinceUtc is string ms &&
                    DateTime.TryParse(ms, null, System.Globalization.DateTimeStyles.RoundtripKind, out var since))
                {
                    if ((nowUtc - since).TotalMinutes < r.DurationMinutes) continue;
                }
                else
                {
                    r.MetSinceUtc = nowUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                    continue;
                }
            }
            if (r.LastFiredUtc is string lf &&
                DateTime.TryParse(lf, null, System.Globalization.DateTimeStyles.RoundtripKind, out var last) &&
                (nowUtc - last).TotalMinutes < r.CooldownMinutes)
                continue;   // 冷却窗内：合并不重复触发
            bool red = r.Metric == "replication_break";
            fired.Add(new AlertEvent(r.Name, r.Metric, v, Message(r, v), red ? "error" : "warn",
                nowUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
            r.LastFiredUtc = fired[^1].FiredAtUtc;
            r.TriggerCount++;
        }
        return fired;
    }

    /// <summary>规则落盘（JSON 数组，损坏按空规则容错）。</summary>
    public static void SaveRules(string path, IReadOnlyList<AlertRule> rules)
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(rules, opts), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static List<AlertRule> LoadRules(string path)
    {
        if (!File.Exists(path)) return new List<AlertRule>();
        try { return JsonSerializer.Deserialize<List<AlertRule>>(File.ReadAllText(path)) ?? new List<AlertRule>(); }
        catch (JsonException) { return new List<AlertRule>(); }
    }

    /// <summary>触发记录落 JSONL（一行一事件，损坏行跳过）。</summary>
    public static void AppendEvent(string path, AlertEvent e) =>
        File.AppendAllText(path, JsonSerializer.Serialize(e, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n");

    public static List<AlertEvent> LoadEvents(string path)
    {
        if (!File.Exists(path)) return new List<AlertEvent>();
        var list = new List<AlertEvent>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { list.Add(JsonSerializer.Deserialize<AlertEvent>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException()); }
            catch (JsonException) { /* 损坏行跳过 */ }
        }
        return list;
    }
}
