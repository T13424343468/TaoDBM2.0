using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>智能线纯引擎第一批：慢查询诊断+一键优化闭环（含复测）、容量增长预测、查询复现包。</summary>
/// <remarks>全部免界面稿；联网问库与告警通道依赖拍板项（P-3/P-4），不在本批。</remarks>

/// <summary>单条诊断：Code=规则号，Severity=info/warn/danger。</summary>
public sealed record SlowQueryFinding(string Code, string Severity, string Message, string? Suggestion = null);

/// <summary>慢查询静态诊断：SQL 文本体检（引擎无关，正则口径，见各规则注释）。</summary>
public static class SlowQueryDiagnoser
{
    /// <summary>对单条 SQL 做静态体检。刻意保守：只报有把握的形态，不猜执行环境。</summary>
    public static List<SlowQueryFinding> Analyze(string sql)
    {
        var findings = new List<SlowQueryFinding>();
        if (string.IsNullOrWhiteSpace(sql)) return findings;
        var s = sql.Trim();
        var head = s.Length >= 6 ? s[..6].ToUpperInvariant() : s.ToUpperInvariant();

        // D001 危险：UPDATE/DELETE 不带 WHERE（全表改写），安全与性能双红线
        if ((head.StartsWith("UPDATE") || head.StartsWith("DELETE")) && !ContainsWhere(s))
            findings.Add(new SlowQueryFinding("D001", "danger", "UPDATE/DELETE 未带 WHERE，将作用于全表",
                "补上明确的 WHERE 条件或先 SELECT 确认影响范围"));

        bool isSelect = head.StartsWith("SELECT");
        if (!isSelect) return findings;

        // S001：SELECT * 需要回列裁剪
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"SELECT\s+\*", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            findings.Add(new SlowQueryFinding("S001", "info", "SELECT * 会读取并传输全部列",
                "改为显式列名，配合覆盖索引可免回表"));

        // S002：LIKE 前导通配，索引失效
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"LIKE\s+'%"))
            findings.Add(new SlowQueryFinding("S002", "warn", "LIKE 以 % 开头，无法走普通索引",
                "改用后缀通配（'x%'）或全文/倒排索引"));

        // S003：OR 条件可能放弃索引
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"\bWHERE\b.{0,400}\bOR\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            findings.Add(new SlowQueryFinding("S003", "info", "WHERE 含 OR，优化器可能放弃索引走全表",
                "考虑 UNION 拆分或改写为 IN"));

        // S004：列上套函数，索引失效
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"\bWHERE\b.{0,400}(UPPER|LOWER|SUBSTR|TRIM|CAST)\s*\(\s*\w+\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            findings.Add(new SlowQueryFinding("S004", "warn", "WHERE 中对列使用函数，索引失效",
                "改为对常量套函数（如 UPPER('x')）或使用函数索引"));

        // S005：无 WHERE 无 LIMIT 的全量读
        if (!ContainsWhere(s) && !System.Text.RegularExpressions.Regex.IsMatch(s, @"\bLIMIT\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && !IsAggregateOnly(s))
            findings.Add(new SlowQueryFinding("S005", "warn", "SELECT 无 WHERE 也无 LIMIT，数据量大会全量拉取",
                "补 WHERE 过滤或加 LIMIT 分页"));

        // D002：SELECT 里挂标量子查询，逐行执行
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"SELECT\s+(?!.*\bFROM\b)\s*.*\(\s*SELECT\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(s, @"SELECT\s+[^;]*,\s*\(\s*SELECT\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            findings.Add(new SlowQueryFinding("D002", "warn", "SELECT 列中含子查询，可能对外层每行执行一次",
                "改写为 JOIN 或先单独聚合"));

        // S007：ORDER BY 无 LIMIT，全量排序
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"\bORDER\s+BY\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
            !System.Text.RegularExpressions.Regex.IsMatch(s, @"\bLIMIT\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            findings.Add(new SlowQueryFinding("S007", "info", "ORDER BY 未配 LIMIT，会做全量排序",
                "配合 LIMIT 取 Top-N，或为排序列建索引"));

        return findings;
    }

    /// <summary>执行计划体检：SQLite EXPLAIN QUERY PLAN 输出为主，关键字兼容 MySQL EXPLAIN 文本。</summary>
    public static List<SlowQueryFinding> AnalyzePlan(string planText)
    {
        var findings = new List<SlowQueryFinding>();
        if (string.IsNullOrWhiteSpace(planText)) return findings;
        bool hasScan = System.Text.RegularExpressions.Regex.IsMatch(planText, @"\bSCAN\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                       && !System.Text.RegularExpressions.Regex.IsMatch(planText, @"SCAN\s+\S+\s+USING\s+(COVERING\s+)?INDEX", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (hasScan)
            findings.Add(new SlowQueryFinding("P001", "warn", "执行计划存在全表扫描（SCAN）",
                "为过滤/连接列建索引，或缩小读取范围"));
        if (System.Text.RegularExpressions.Regex.IsMatch(planText, @"USE\s+TEMP\s+B-TREE", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            findings.Add(new SlowQueryFinding("P002", "info", "计划使用临时 B 树（排序/去重未走索引）",
                "为 ORDER BY / GROUP BY 列建索引可消除"));
        if (System.Text.RegularExpressions.Regex.IsMatch(planText, @"SCALAR\s+SUBQUERY", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            findings.Add(new SlowQueryFinding("P003", "warn", "计划含标量子查询，可能逐行执行",
                "改写为 JOIN，或确认子查询可被缓存"));
        return findings;
    }

    /// <summary>索引建议：从 WHERE 等值条件提取列，生成幂等建索引 DDL（IF NOT EXISTS）。</summary>
    public static List<string> SuggestIndexDdl(string sql, string table)
    {
        var cols = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     sql, @"\b([A-Za-z_]\w*)\s*=\s*(@\w+|\?|'(?:[^']|'')*'|[0-9.]+)",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var col = m.Groups[1].Value;
            if (Reserved(col)) continue;
            if (!cols.Contains(col)) cols.Add(col);
        }
        var ddl = new List<string>();
        foreach (var c in cols)
            ddl.Add($"CREATE INDEX IF NOT EXISTS idx_{table}_{c} ON {table}({c});");
        return ddl;
    }

    private static bool ContainsWhere(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s, @"\bWHERE\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsAggregateOnly(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s, @"\b(COUNT|SUM|AVG|MIN|MAX)\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool Reserved(string w)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "WHERE", "AND", "OR", "NOT", "NULL", "IN", "LIKE", "BETWEEN", "IS", "ON", "SET", "SELECT", "FROM" };
        return reserved.Contains(w);
    }
}

/// <summary>一键优化闭环的复测环节：同口径重复执行取中位耗时，改造前后对比给提速比。</summary>
public static class QueryRetest
{
    /// <summary>中位数复测：run(sql, ct) 由调用方提供（经 QueryRouter 或原生连接均可），内部串行执行 iterations 次取中位。</summary>
    public static async Task<double> MedianMsAsync(Func<string, CancellationToken, Task> run, string sql, int iterations = 5, CancellationToken ct = default)
    {
        if (iterations < 1) iterations = 1;
        var times = new List<double>();
        for (int i = 0; i < iterations; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await run(sql, ct);
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return times[times.Count / 2];
    }

    /// <summary>改造前后对比：Speedup = Before/After，&gt;1 表示改造后更快。</summary>
    public static async Task<RetestReport> CompareAsync(Func<string, CancellationToken, Task> run, string beforeSql, string afterSql, int iterations = 5, CancellationToken ct = default)
    {
        var before = await MedianMsAsync(run, beforeSql, iterations, ct);
        var after = await MedianMsAsync(run, afterSql, iterations, ct);
        var speedup = after > 0 ? before / after : 0;
        return new RetestReport(before, after, speedup, iterations);
    }

    public sealed record RetestReport(double BeforeMs, double AfterMs, double Speedup, int Iterations);
}

/// <summary>
/// 优化回访登记与闭环复测（2026-11-20 批次 D）：优化动作执行后登记回访任务，
/// 到期自动重跑诊断项（静态规则 + 计划体检），产出前后对比卡（耗时/计划/建议命中变化），
/// 「复测通过/未通过」落账。纯内存引擎：持久化由调用方拿 Json 走落盘通道（对齐 TaskCenter 快照口径）。
/// </summary>
public static class OptimizationFollowup
{
    /// <summary>回访任务：一次优化动作的登记记录与复测结论。</summary>
    public sealed record FollowupTask
    {
        public string TaskId { get; init; } = Guid.NewGuid().ToString("N")[..12];
        public string Title { get; init; } = "";
        public string Sql { get; init; } = "";
        public string Engine { get; init; } = "";
        /// <summary>登记时间（优化动作完成时点）。</summary>
        public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.Now;
        /// <summary>回访间隔（天，可配）：到期判定基于 RegisteredAt + IntervalDays。</summary>
        public int IntervalDays { get; init; } = 1;
        /// <summary>登记时的静态诊断命中（变化对比基准）。</summary>
        public List<string> BaselineCodes { get; init; } = new();
        /// <summary>登记时的计划体检命中（空=未做计划体检）。</summary>
        public List<string> BaselinePlanCodes { get; init; } = new();
        /// <summary>登记时中位耗时（ms；null=未计时）。</summary>
        public double? BaselineMs { get; init; }
        /// <summary>复测结论：pending / passed / failed。</summary>
        public string Verdict { get; init; } = "pending";
        public DateTimeOffset? RetestedAt { get; init; }
        public double? RetestMs { get; init; }
        public List<string> RetestCodes { get; init; } = new();
        public List<string> RetestPlanCodes { get; init; } = new();
        public string? Note { get; init; }
        /// <summary>已并入任务中心的任务号（并入后回填）。</summary>
        public string? CenterTaskId { get; init; }
    }

    /// <summary>登记回访任务：以当前诊断命中（静态+计划）与耗时为基准。间隔须为正。</summary>
    public static FollowupTask Register(string title, string sql, string engine, IEnumerable<string> baselineCodes,
        IEnumerable<string> baselinePlanCodes, double? baselineMs, int intervalDays = 1, DateTimeOffset? now = null)
    {
        if (intervalDays <= 0) throw new ArgumentOutOfRangeException(nameof(intervalDays));
        return new FollowupTask
        {
            Title = title?.Trim() ?? "",
            Sql = sql ?? "",
            Engine = engine ?? "",
            RegisteredAt = now ?? DateTimeOffset.Now,
            IntervalDays = intervalDays,
            BaselineCodes = baselineCodes?.Distinct().ToList() ?? new(),
            BaselinePlanCodes = baselinePlanCodes?.Distinct().ToList() ?? new(),
            BaselineMs = baselineMs,
        };
    }

    /// <summary>到期判定：now >= 登记时间 + 间隔天；结论已出的不再触发。</summary>
    public static bool IsDue(FollowupTask t, DateTimeOffset? now = null) =>
        t.Verdict == "pending" && (now ?? DateTimeOffset.Now) >= t.RegisteredAt.AddDays(t.IntervalDays);

    /// <summary>到期未复测的任务（按到期时间先后）。</summary>
    public static List<FollowupTask> DueOf(IEnumerable<FollowupTask> tasks, DateTimeOffset? now = null) =>
        tasks.Where(t => IsDue(t, now)).OrderBy(t => t.RegisteredAt).ToList();

    /// <summary>
    /// 复测：到期任务重跑诊断项（静态规则 + 计划体检），与登记基准逐项对比出对比卡。
    /// 耗时比基准下降或持平（<=1.05 倍，噪声容差）且静态/计划命中均未恶化 → 通过；否则未通过。
    /// 传入 planText 时做计划对比；耗时未计时（null）时只对比诊断命中。
    /// </summary>
    public static FollowupTask Retest(FollowupTask t, double? retestMs, IEnumerable<string> retestCodes,
        string? planText = null, string? note = null, DateTimeOffset? now = null)
    {
        if (t.Verdict != "pending") throw new InvalidOperationException("该回访已出结论，不可重复复测");
        var codes = retestCodes?.Distinct().ToList() ?? new();
        var planCodes = planText is null ? t.BaselinePlanCodes : SlowQueryDiagnoser.AnalyzePlan(planText).Select(x => x.Code).Distinct().ToList();

        bool timeOk = t.BaselineMs is null || retestMs is null || retestMs.Value <= t.BaselineMs.Value * 1.05;
        bool staticOk = !codes.Except(t.BaselineCodes).Any();
        bool planOk = t.BaselinePlanCodes.Count == 0 || !planCodes.Except(t.BaselinePlanCodes).Any();
        bool passed = timeOk && staticOk && planOk;

        return t with
        {
            Verdict = passed ? "passed" : "failed",
            RetestedAt = now ?? DateTimeOffset.Now,
            RetestMs = retestMs,
            RetestCodes = codes,
            RetestPlanCodes = planCodes,
            Note = note,
        };
    }

    /// <summary>前后对比卡：逐行文字账（耗时/静态/计划/结论），UI 直接展示。</summary>
    public static List<string> CompareCard(FollowupTask t)
    {
        var lines = new List<string>
        {
            $"[{t.TaskId}] {t.Title}（{t.Engine}）",
            $"登记：{t.RegisteredAt:yyyy-MM-dd HH:mm}  间隔：{t.IntervalDays} 天  基准命中：{(t.BaselineCodes.Count > 0 ? string.Join(",", t.BaselineCodes) : "无")}",
        };
        if (t.BaselinePlanCodes.Count > 0) lines.Add($"基准计划命中：{string.Join(",", t.BaselinePlanCodes)}");
        if (t.BaselineMs is { } baseMs) lines.Add($"基准耗时：{baseMs:F1} ms");
        if (t.Verdict == "pending")
        {
            lines.Add(IsDue(t) ? "状态：到期待复测" : string.Format("状态：未到期（约 {0:F0} 小时后到期）", Math.Max(0, (t.RegisteredAt.AddDays(t.IntervalDays) - DateTimeOffset.Now).TotalHours)));
            return lines;
        }
        var delta = t.BaselineMs is { } b && t.RetestMs is { } a ? $"（{b:F1} → {a:F1} ms）" : "";
        lines.Add($"复测：{t.RetestedAt:yyyy-MM-dd HH:mm}  命中：{(t.RetestCodes.Count > 0 ? string.Join(",", t.RetestCodes) : "无")}{delta}");
        if (t.BaselinePlanCodes.Count > 0) lines.Add($"复测计划命中：{(t.RetestPlanCodes.Count > 0 ? string.Join(",", t.RetestPlanCodes) : "无")}");
        lines.Add($"结论：复测{(t.Verdict == "passed" ? "通过" : "未通过")}{(string.IsNullOrEmpty(t.Note) ? "" : " — " + t.Note)}");
        return lines;
    }

    /// <summary>并入任务中心：以 retest 类型提交长活（由宿主 Runner 解释执行）；重复并入返回原任务号。</summary>
    public static string AttachToCenter(Core.Streaming.TaskCenter center, FollowupTask t)
    {
        if (!string.IsNullOrEmpty(t.CenterTaskId)) return t.CenterTaskId;
        var h = center.Submit(new Core.Streaming.TaskSpec
        {
            Kind = "retest",
            Title = $"优化回访复测：{t.Title}",
            Db = "",
            Sql = t.Sql,
        });
        return h.TaskId;
    }

    // ── 快照往返：调用方落盘/回放（对齐 TaskCenter.ExportStates 口径） ──
    public static string ToJson(IEnumerable<FollowupTask> tasks) =>
        System.Text.Json.JsonSerializer.Serialize(tasks.ToList());

    public static List<FollowupTask>? FromJson(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<FollowupTask>>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}

/// <summary>容量增长预测：对 (时间, 已用空间) 采样做最小二乘线性拟合，外推阈值触达时间。</summary>
public static class CapacityForecaster
{
    public sealed record CapacitySample(DateTimeOffset At, double UsedBytes);
    public sealed record CapacityForecast(double GrowthPerDay, double RSquared, double? DaysToThreshold, double Intercept, double Slope, double MeanX)
    {
        /// <summary>按拟合直线外推 daysAhead 天后的用量。</summary>
        public double PredictAt(int daysAhead) => Intercept + Slope * (MeanX + daysAhead);
    }

    /// <summary>至少需要 2 个样本；样本跨度按天计（不足 1 天按 1 天计，避免除零）。</summary>
    public static CapacityForecast Forecast(IReadOnlyList<CapacitySample> samples, double? thresholdBytes = null)
    {
        if (samples == null || samples.Count < 2)
            return new CapacityForecast(0, 0, null, 0, 0, 0);
        var t0 = samples[0].At;
        var xs = samples.Select(s => Math.Max((s.At - t0).TotalDays, 0)).ToArray();
        var ys = samples.Select(s => s.UsedBytes).ToArray();
        double mx = xs.Average(), my = ys.Average();
        double sxx = xs.Sum(x => (x - mx) * (x - mx));
        double sxy = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum();
        // 跨度不足 1 天：斜率按 0 处理（增长不可辨），不外推
        double b = sxx >= 1e-9 ? sxy / sxx : 0;
        double a = my - b * mx;
        double ssTot = ys.Sum(y => (y - my) * (y - my));
        double ssRes = ys.Zip(xs, (y, x) => (y - (a + b * x)) * (y - (a + b * x))).Sum();
        double r2 = ssTot > 0 ? 1 - ssRes / ssTot : 1;
        double? days = null;
        if (thresholdBytes is double th)
        {
            if (b > 0) days = Math.Max((th - a) / b - mx, 0);
            else if (a >= th) days = 0; // 已越线且不再增长
        }
        return new CapacityForecast(b, r2, days, a, b, mx);
    }
}

/// <summary>查询复现包：SQL+绑定参数+方言+最小建表脚本打包为 JSON，可在他机回放；凭据禁止入包。</summary>
public static class QueryReproPack
{
    public sealed record ReproPack(string PackVersion, string Title, string Engine, string Sql,
        Dictionary<string, string> Params, List<string> SetupSql, string? Notes, DateTimeOffset CreatedAt);

    private static readonly HashSet<string> ForbiddenKeys = new(StringComparer.OrdinalIgnoreCase)
        { "password", "pwd", "secret", "token", "connectionstring", "connstring", "connection_string", "apikey", "api_key" };

    public static string ToJson(ReproPack pack) =>
        JsonSerializer.Serialize(pack, new JsonSerializerOptions { WriteIndented = true });

    public static ReproPack? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<ReproPack>(json); }
        catch (JsonException) { return null; }
        catch (ArgumentNullException) { return null; }
    }

    /// <summary>入包前净化：键名命中凭据黑名单的参数整键剔除并留痕；SetupSql/Sql 不动（由审查者人工过目）。</summary>
    public static ReproPack Sanitize(ReproPack pack)
    {
        if (pack.Params.Count == 0) return pack;
        var dropped = pack.Params.Keys.Where(ForbiddenKeys.Contains).ToList();
        if (dropped.Count == 0) return pack;
        var clean = pack.Params.Where(kv => !ForbiddenKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var note = string.IsNullOrWhiteSpace(pack.Notes) ? "" : pack.Notes + "；";
        return pack with { Params = clean, Notes = $"{note}已剔除凭据类参数：{string.Join(",", dropped)}" };
    }
}
