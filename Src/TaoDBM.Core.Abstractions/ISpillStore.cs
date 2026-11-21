namespace TaoDBM.Core.Streaming;

/// <summary>
/// 溢出暂存契约：把已解析批次写入外部存储（如临时文件），供断点续传与回放。
/// 与 QueryPage/StreamingQuery 同属流式抽象层；Core 不碰文件，实现方（Infrastructure）负责 IO 与清理。
/// </summary>
public interface ISpillStore
{
    /// <summary>追加写入一个批次。</summary>
    void WriteBatch(IReadOnlyList<string> headers, IReadOnlyList<string?[]> rows);

    /// <summary>按写入顺序回放全部批次；坏行跳过不抛（参考 SyncCheckpoint.FailSafeParse 容错口径）。</summary>
    IEnumerable<(IReadOnlyList<string> Headers, IReadOnlyList<string?[]> Rows)> ReadBatches();

    /// <summary>已写入批次数。</summary>
    long BatchCount { get; }

    /// <summary>清空并删除暂存内容。</summary>
    void Delete();
}
