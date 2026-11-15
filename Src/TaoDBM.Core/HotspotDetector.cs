namespace TaoDBM.Core;

/// <summary>分片负载画像：分片名与负载值（行数或请求数均可，口径由调用方统一定）。</summary>
public sealed record ShardLoad(string Name, long Load);

/// <summary>单个分片负载判定：相对均值的倍数与档位（hot/normal/cold）。</summary>
public sealed record ShardLoadVerdict(string Name, long Load, double RatioToMean, string Level);

/// <summary>整体倾斜结论：均值、峰值倍数、逐分片判定与处置建议。</summary>
public sealed record SkewReport(double MeanLoad, double PeakRatio, IReadOnlyList<ShardLoadVerdict> Verdicts, IReadOnlyList<string> Suggestions)
{
    /// <summary>是否存在热点分片（负载 ≥ 均值 3 倍）。</summary>
    public bool HasHotSpot => Verdicts.Any(v => v.Level == "hot");
}

/// <summary>
/// 分库分区分表底座引擎：热点倾斜检测（新需求「图形化分库分区分表」配套）。
/// 只依据调用方提供的负载画像纯计算，不做实时采样；输出热点/冷清分片与处置建议，
/// 供图形化界面在再均衡、拆分、加盐前定位问题分片。
/// </summary>
public static class HotspotDetector
{
    /// <summary>
    /// 检测分片负载倾斜：负载 ≥ 均值 3 倍判热点（hot），不足均值一半判冷清（cold），其余正常；
    /// 判定按负载降序（并列按名字定序），附逐项处置建议。
    /// </summary>
    public static SkewReport Analyze(IEnumerable<ShardLoad> shards)
    {
        var list = shards.ToList();
        double mean = list.Count == 0 ? 0 : list.Average(s => (double)s.Load);
        var verdicts = list.Select(s =>
        {
            double ratio = mean > 0 ? s.Load / mean : 0;
            string level = ratio >= 3 ? "hot" : (mean > 0 && ratio <= 0.5) ? "cold" : "normal";
            return new ShardLoadVerdict(s.Name, s.Load, ratio, level);
        }).OrderByDescending(v => v.Load).ThenBy(v => v.Name, StringComparer.Ordinal).ToList();

        var suggestions = new List<string>();
        foreach (var v in verdicts.Where(v => v.Level == "hot"))
            suggestions.Add($"分片 {v.Name} 负载为均值 {v.RatioToMean:0.##} 倍，建议拆分或加盐打散");
        foreach (var v in verdicts.Where(v => v.Level == "cold"))
            suggestions.Add($"分片 {v.Name} 负载不足均值一半，可在再均衡时承接数据");
        if (list.Count > 0 && suggestions.Count == 0)
            suggestions.Add("负载分布正常，无需处置");
        else if (list.Count == 0)
            suggestions.Add("无分片负载数据");

        return new SkewReport(mean, list.Count == 0 ? 0 : verdicts[0].RatioToMean, verdicts, suggestions);
    }
}
