using System.Text;

namespace TaoDBM.Core;

/// <summary>分片键候选画像：列名、基数、倾斜比（最大值占比相对均匀期望的倍数）、查询谓词命中次数、是否单调递增。</summary>
public sealed record ShardKeyCandidate(string Column, long Cardinality, double SkewRatio, long PredicateHits, bool Monotonic);

/// <summary>单个候选的评估结论：得分（0–100）、扣分问题清单、建议分片策略（hash/range）。</summary>
public sealed record ShardKeyVerdict(string Column, int Score, IReadOnlyList<string> Issues, string SuggestedStrategy);

/// <summary>
/// 分库分区分表底座引擎：分片键评估与路由预演（新需求「图形化分库分区分表」的纯逻辑）。
/// 评估只依据调用方提供的统计画像，不做实时采样；路由预演为纯计算不触库，
/// 供图形化界面在选键与建分片方案前预演某行数据的落点。
/// </summary>
public static class ShardKeyEvaluator
{
    /// <summary>
    /// 评估候选分片键并按得分降序（并列按列名定序）返回：
    /// 零基数直接扣至地板；倾斜严重、谓词未命中、单调键范围分片尾部热点分别扣分并给出问题项。
    /// </summary>
    public static IReadOnlyList<ShardKeyVerdict> Evaluate(IEnumerable<ShardKeyCandidate> candidates)
    {
        var verdicts = new List<ShardKeyVerdict>();
        foreach (var c in candidates)
        {
            int score = 100;
            var issues = new List<string>();
            if (c.Cardinality <= 0)
            {
                score -= 60;
                issues.Add("无基数数据或基数为零，不可作分片键");
            }
            else
            {
                if (c.SkewRatio >= 3) { score -= 30; issues.Add($"数据倾斜严重（峰值占比 {c.SkewRatio:0.##} 倍均匀期望），易出热点分片"); }
                else if (c.SkewRatio >= 1.5) { score -= 10; issues.Add($"数据分布偏斜（峰值占比 {c.SkewRatio:0.##} 倍均匀期望）"); }
                if (c.PredicateHits <= 0) { score -= 30; issues.Add("查询不携带该键，路由退化为全分片扫描"); }
            }

            string strategy;
            if (c.Monotonic)
            {
                strategy = "hash";
                issues.Add("单调递增键走范围分片会产生尾部热点，建议哈希打散");
            }
            else if (c.Cardinality > 0 && c.SkewRatio < 1.5) strategy = "range";
            else strategy = "hash";

            verdicts.Add(new ShardKeyVerdict(c.Column, Math.Max(0, score), issues, strategy));
        }
        return verdicts.OrderByDescending(v => v.Score).ThenBy(v => v.Column, StringComparer.Ordinal).ToList();
    }

    /// <summary>哈希路由：FNV-1a 32 位对分片数取模，确定序，供图形化界面预演某行落点。</summary>
    public static int HashRoute(string keyValue, int shardCount)
    {
        if (shardCount <= 0) throw new ArgumentOutOfRangeException(nameof(shardCount), "分片数必须为正");
        uint h = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(keyValue)) { h ^= b; h *= 16777619; }
        return (int)(h % (uint)shardCount);
    }

    /// <summary>
    /// 范围路由：boundaries 为升序的各分片上界（含）；命中返回分片下标，
    /// 超出全部上界返回 -1（提示需扩分片或改用哈希）。
    /// </summary>
    public static int RangeRoute(long value, IReadOnlyList<long> boundaries)
    {
        for (int i = 0; i < boundaries.Count; i++)
            if (value <= boundaries[i]) return i;
        return -1;
    }
}
