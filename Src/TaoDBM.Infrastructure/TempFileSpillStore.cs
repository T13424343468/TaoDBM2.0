using System.Text;
using System.Text.Json;
using TaoDBM.Core.Streaming;

namespace TaoDBM.Infrastructure;

/// <summary>
/// 临时文件溢出暂存：批次以 JSONL 追加写入系统临时目录，支持回放与清理。
/// 只存解析后的表头与行数据，不落连接凭据；Dispose 即删除临时文件。
/// </summary>
public sealed class TempFileSpillStore : ISpillStore, IDisposable
{
    private sealed record SpillLine(string[] H, string?[][] R);

    private readonly string _path;
    private long _batchCount;

    public TempFileSpillStore()
    {
        _path = Path.Combine(Path.GetTempPath(), "taodbm-spill-" + Guid.NewGuid().ToString("N") + ".jsonl");
    }

    /// <summary>暂存文件路径（诊断用）。</summary>
    public string FilePath => _path;

    public long BatchCount => _batchCount;

    public void WriteBatch(IReadOnlyList<string> headers, IReadOnlyList<string?[]> rows)
    {
        var line = JsonSerializer.Serialize(new SpillLine(headers.ToArray(), rows.ToArray()));
        File.AppendAllText(_path, line + "\n", Encoding.UTF8);
        _batchCount++;
    }

    public IEnumerable<(IReadOnlyList<string> Headers, IReadOnlyList<string?[]> Rows)> ReadBatches()
    {
        if (!File.Exists(_path)) yield break;
        foreach (var line in File.ReadLines(_path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            SpillLine? parsed;
            try { parsed = JsonSerializer.Deserialize<SpillLine>(line); }
            catch (JsonException) { continue; }   // 坏行跳过，不拖垮回放
            if (parsed is null) continue;
            yield return (parsed.H, parsed.R);
        }
    }

    public void Delete()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (IOException) { /* 删除失败不阻断主流程，留给系统清理临时目录 */ }
        _batchCount = 0;
    }

    public void Dispose() => Delete();
}
