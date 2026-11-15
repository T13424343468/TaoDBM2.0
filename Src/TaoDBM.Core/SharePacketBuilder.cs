using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>协作线：局域网搬运引擎（团队共享包的封包/验包/分块传输——纯引擎，实际网络收发由宿主接）。</summary>
/// <remarks>
/// 免界面稿：TeamShareMerge 已管合并与净化，本引擎管"包怎么搬"——
/// 封包（载荷+SHA256 指纹头）、验包（重算指纹防篡改/截断）、分块（大包切固定块，逐块过线后重装）。
/// 载荷即 TeamShareMerge.ToJson 的产物；凭据已在净化阶段剔除，本引擎不做二次清洗但验包可见性留证。
/// </remarks>

/// <summary>包指纹头：接收方凭它校验载荷完整性与来源。</summary>
public sealed record SharePacketHeader(string PacketId, string Sender, DateTimeOffset CreatedAt, int ItemCount, long PayloadBytes, string Sha256);

/// <summary>共享包：指纹头 + 载荷（团队共享条目 JSON）。</summary>
public sealed record SharePacket(SharePacketHeader Header, string Payload);

/// <summary>分块：大包过线时切固定大小，Index 从 0 起，Total 为总块数。</summary>
public sealed record ShareChunk(string PacketId, int Index, int Total, string Data);

/// <summary>验包结果：Ok=false 时 Reason 给出可展示的原因。</summary>
public sealed record PacketCheck(bool Ok, string Reason, string? Payload);

public static class SharePacketBuilder
{
    /// <summary>封包：载荷取团队共享条目序列化产物，指纹对载荷字节计算。</summary>
    public static SharePacket Build(IReadOnlyList<ShareItem> items, string sender, DateTimeOffset createdAt)
    {
        var payload = TeamShareMerge.ToJson(items);
        return BuildRaw(payload, sender, createdAt);
    }

    /// <summary>封包（原始载荷版）：供重装后复验等场景复用。</summary>
    public static SharePacket BuildRaw(string payload, string sender, DateTimeOffset createdAt) =>
        new(new SharePacketHeader(Guid.NewGuid().ToString("N")[..12], sender, createdAt,
            CountItems(payload), Encoding.UTF8.GetByteCount(payload), Fingerprint(payload)), payload);

    /// <summary>验包：指纹一致且条目数相符才算过；载荷损坏/被改/被截都能抓住。</summary>
    public static PacketCheck Verify(SharePacket packet)
    {
        if (packet.Header.Sha256 != Fingerprint(packet.Payload))
            return new PacketCheck(false, "指纹不符：载荷疑似被篡改或损坏", null);
        var count = CountItems(packet.Payload);
        if (count != packet.Header.ItemCount)
            return new PacketCheck(false, $"条目数不符：头记 {packet.Header.ItemCount}，载荷实为 {count}", null);
        return new PacketCheck(true, "校验通过", packet.Payload);
    }

    /// <summary>分块：按 chunkChars 字符切载荷（UTF-8 安全按字符切，重组后整体验签）。</summary>
    public static List<ShareChunk> Chunk(SharePacket packet, int chunkChars)
    {
        if (chunkChars <= 0) throw new ArgumentOutOfRangeException(nameof(chunkChars));
        var total = (int)Math.Ceiling(packet.Payload.Length / (double)chunkChars);
        if (total == 0) total = 1;
        var chunks = new List<ShareChunk>(total);
        for (int i = 0; i < total; i++)
        {
            var len = Math.Min(chunkChars, packet.Payload.Length - i * chunkChars);
            chunks.Add(new ShareChunk(packet.Header.PacketId, i, total,
                len > 0 ? packet.Payload.Substring(i * chunkChars, len) : ""));
        }
        return chunks;
    }

    /// <summary>重装：按包聚合、按序拼接；缺块/混包/块数不符返回 null（不产出半截载荷）。</summary>
    public static string? Assemble(IReadOnlyList<ShareChunk> chunks)
    {
        if (chunks.Count == 0) return null;
        var packetId = chunks[0].PacketId;
        var total = chunks[0].Total;
        if (chunks.Any(c => c.PacketId != packetId || c.Total != total)) return null;
        if (chunks.Select(c => c.Index).Distinct().Count() != total) return null;
        var ordered = chunks.OrderBy(c => c.Index).ToList();
        for (int i = 0; i < total; i++)
            if (ordered[i].Index != i) return null;
        return string.Concat(ordered.Select(c => c.Data));
    }

    /// <summary>载荷指纹：SHA256（UTF-8 字节），与 SnapshotPack 同口径。</summary>
    public static string Fingerprint(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static int CountItems(string payload)
    {
        try { return TeamShareMerge.FromJson(payload)?.Count ?? -1; }
        catch (JsonException) { return -1; }
    }
}
