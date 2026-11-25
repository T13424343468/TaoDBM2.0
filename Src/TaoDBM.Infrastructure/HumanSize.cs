namespace TaoDBM.Core;

/// <summary>
/// 统一容量口径（㊵）：自适应 B/KB/MB/GB，一位小数，全应用唯一出口——
/// 替换 1536MB / 1.5G / 1610612736 三种并存口径。
/// </summary>
public static class HumanSize
{
    /// <summary>字节数转人话容量：&lt;1KB 显 B，&lt;1MB 显 KB，&lt;1GB 显 MB，其余显 GB，一位小数。</summary>
    public static string Of(long bytes)
    {
        double v = bytes;
        if (v < 1024) return $"{v:0} B";
        if (v < 1024 * 1024) return $"{v / 1024:0.#} KB";
        if (v < 1024L * 1024 * 1024) return $"{v / 1024 / 1024:0.#} MB";
        return $"{v / 1024 / 1024 / 1024:0.#} GB";
    }
}
