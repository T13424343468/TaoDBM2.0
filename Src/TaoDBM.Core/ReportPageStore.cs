using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>㉘ 报表页（2026-11-25 设计稿冻结）：点选出图即钉住的零配置看板。
/// 卡片配置落本地 JSON（TaoPaths + AtomicWrite，损坏回落空表），重开保持原样。
/// 纯文件逻辑，KanbanPinStore 家法（UseFile 注入，LogicTest 直测）。</summary>
public static class ReportPageStore
{
    private static readonly object Gate = new();

    private static string _file = "";
    private static string File => _file.Length > 0 ? _file : TaoPaths.FilePath("report-page.json");

    /// <summary>测试或自定义存储位置。</summary>
    public static void UseFile(string path) => _file = path;

    /// <summary>报表卡：钉住时的 SQL/引擎/库/聚合列/图型；刷新走格 19 重建口径重跑同一 SQL。</summary>
    public sealed record Card(string CardId, string Title, string Sql, string Engine, string Column,
        string ChartType, string? Db = null, DateTimeOffset CreatedAt = default, string TimeRange = "all")
    {
        /// <summary>图型合法值：bar / histogram / line（ChartSpecBuilder 同口径）。</summary>
        public string NormalizedType => ChartType is "bar" or "histogram" or "line" ? ChartType : "bar";
        /// <summary>时间范围合法值：all / 1h / 24h / 7d / 30d；旧 JSON 缺省兼容 all。</summary>
        public string NormalizedTimeRange => TimeRange is "1h" or "24h" or "7d" or "30d" ? TimeRange : "all";
    }

    /// <summary>全部卡片（文件缺失/损坏回落空表，绝不闪退）。</summary>
    public static List<Card> All() { lock (Gate) { return LoadAll(); } }

    /// <summary>钉住或更新（同 CardId 覆盖，保持清单顺序）。</summary>
    public static void Upsert(Card card)
    {
        lock (Gate)
        {
            var all = LoadAll().Where(c => c.CardId != card.CardId).ToList();
            all.Add(card);
            SaveAll(all);
        }
    }

    /// <summary>移除卡片。不存在返回 false。</summary>
    public static bool Remove(string cardId)
    {
        lock (Gate)
        {
            var all = LoadAll();
            var rest = all.Where(c => c.CardId != cardId).ToList();
            if (rest.Count == all.Count) return false;
            SaveAll(rest);
            return true;
        }
    }

    static List<Card> LoadAll()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new List<Card>();
            var cards = JsonSerializer.Deserialize<List<Card>>(System.IO.File.ReadAllText(File));
            return cards ?? new List<Card>();
        }
        catch (Exception ex) { AppLog.Write($"报表页读取失败，回落空表: {ex.Message}"); return new List<Card>(); }
    }

    static void SaveAll(List<Card> cards)
    {
        try
        {
            var dir = Path.GetDirectoryName(File);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicWrite.Text(File, JsonSerializer.Serialize(cards,
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch (Exception ex) { AppLog.Write($"报表页保存失败: {ex.Message}"); }
    }
}
