using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>协作线：团队局域网共享引擎（连接元信息/收藏/注释内网互传合并；数据不出内网由部署保证，引擎侧先保证凭据不入共享包）。</summary>
/// <remarks>纯引擎免界面稿：离线优先——导出共享包 JSON、双向合并；无云中继，同步由局域网文件/接口搬运。</remarks>

/// <summary>共享条目：Kind=connection/favorite/comment；Deleted=true 为墓碑（删除也要同步）；Payload 为 JSON 文本。</summary>
public sealed record ShareItem(string ItemId, string Kind, string Payload, DateTimeOffset UpdatedAt, string Author, bool Deleted = false);

/// <summary>合并结果：winner 按条目维度后写胜出；Stats 记录新增/更新/墓碑数。</summary>
public sealed record ShareMergeResult(List<ShareItem> Merged, int Added, int Updated, int Tombstones);

public static class TeamShareMerge
{
    private static readonly HashSet<string> ForbiddenKeys = new(StringComparer.OrdinalIgnoreCase)
        { "password", "pwd", "secret", "token", "connectionstring", "connstring", "connection_string", "apikey", "api_key" };

    /// <summary>双向合并：同 ItemId 取 UpdatedAt 新者；平局保留 local（确定性）；本地没有的远端条目（含墓碑）收入。</summary>
    public static ShareMergeResult Merge(IReadOnlyList<ShareItem> local, IReadOnlyList<ShareItem> remote)
    {
        var byId = local.ToDictionary(i => i.ItemId, i => i, StringComparer.Ordinal);
        int added = 0, updated = 0, tombstones = 0;
        foreach (var r in remote)
        {
            if (!byId.TryGetValue(r.ItemId, out var l))
            {
                byId[r.ItemId] = r; added++;
                if (r.Deleted) tombstones++;
            }
            else if (r.UpdatedAt > l.UpdatedAt)
            {
                byId[r.ItemId] = r; updated++;
                if (r.Deleted) tombstones++;
            }
        }
        var merged = byId.Values.OrderBy(i => i.ItemId, StringComparer.Ordinal).ToList();
        return new ShareMergeResult(merged, added, updated, tombstones);
    }

    /// <summary>入包净化：Payload JSON 中命中凭据黑名单的键整键剔除；被剔键名随返回值留痕，坏 JSON 原样放行交由人工过目。</summary>
    public static (ShareItem Clean, List<string> Dropped) Sanitize(ShareItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Payload)) return (item, new List<string>());
        try
        {
            using var doc = JsonDocument.Parse(item.Payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (item, new List<string>());
            var dropped = root.EnumerateObject().Where(p => ForbiddenKeys.Contains(p.Name)).Select(p => p.Name).ToList();
            if (dropped.Count == 0) return (item, dropped);
            var buffer = new System.Text.StringBuilder("{");
            bool first = true;
            foreach (var p in root.EnumerateObject())
            {
                if (ForbiddenKeys.Contains(p.Name)) continue;
                if (!first) buffer.Append(',');
                first = false;
                buffer.Append(JsonEncodedText.Encode(p.Name)).Append(':').Append(p.Value.GetRawText());
            }
            buffer.Append('}');
            return (item with { Payload = buffer.ToString() }, dropped);
        }
        catch (JsonException)
        {
            return (item, new List<string>());
        }
    }

    public static string ToJson(IReadOnlyList<ShareItem> items) =>
        JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });

    public static List<ShareItem>? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<List<ShareItem>>(json); }
        catch (JsonException) { return null; }
        catch (ArgumentNullException) { return null; }
    }
}
