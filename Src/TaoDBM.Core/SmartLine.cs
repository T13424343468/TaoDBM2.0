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
