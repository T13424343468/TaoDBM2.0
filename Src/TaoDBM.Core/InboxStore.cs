using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>
/// 应用内通知收件箱（2026-11-28 拍板：告警以应用内为主——系统通知默认关后，事件必须有应用内落点）。
/// 事件落本地 JSON（TaoPaths + AtomicWrite），打开程序即可见；纯文件逻辑，NotifyPrefs 家法。
/// </summary>
public static class InboxStore
{
    private static readonly object Gate = new();
    private static string _file = "";
    private static string File => _file.Length > 0 ? _file : TaoPaths.FilePath("inbox.json");

    /// <summary>测试或自定义存储位置。</summary>
    public static void UseFile(string path) => _file = path;

    /// <summary>收件箱条目：来源种类、标题、正文、时间、已读标记。</summary>
    public sealed record InboxItem(string Kind, string Key, string Title, string Message, string OccurredAtUtc, bool Read = false);

    /// <summary>追加一条（最新在前，最多保留 500 条，防无限膨胀）。</summary>
    public static void Append(NotifyEvent evt)
    {
        lock (Gate)
        {
            var all = LoadAll();
            all.Insert(0, new InboxItem(evt.Kind.ToString(), evt.Key, evt.Title, evt.Message,
                evt.OccurredAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
            if (all.Count > 500) all.RemoveRange(500, all.Count - 500);
            SaveAll(all);
        }
    }

    /// <summary>全部条目（最新在前）。</summary>
    public static List<InboxItem> All() { lock (Gate) { return LoadAll(); } }

    /// <summary>未读数（收件箱徽标口径）。</summary>
    public static int UnreadCount() { lock (Gate) { return LoadAll().Count(x => !x.Read); } }

    /// <summary>全部标为已读。</summary>
    public static void MarkAllRead()
    {
        lock (Gate)
        {
            var all = LoadAll();
            SaveAll(all.Select(x => x with { Read = true }).ToList());
        }
    }

    /// <summary>清空收件箱。</summary>
    public static void Clear()
    {
        lock (Gate) SaveAll(new List<InboxItem>());
    }

    static List<InboxItem> LoadAll()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new List<InboxItem>();
            return JsonSerializer.Deserialize<List<InboxItem>>(System.IO.File.ReadAllText(File)) ?? new List<InboxItem>();
        }
        catch (Exception ex) { AppLog.Write($"收件箱读取失败，回落空表: {ex.Message}"); return new List<InboxItem>(); }
    }

    static void SaveAll(List<InboxItem> items)
    {
        try
        {
            var dir = Path.GetDirectoryName(File);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicWrite.Text(File, JsonSerializer.Serialize(items,
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch (Exception ex) { AppLog.Write($"收件箱保存失败: {ex.Message}"); }
    }
}
