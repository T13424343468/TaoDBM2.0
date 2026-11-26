// 备份历史本地账（批次四格4，2026-11-23）：tao_backup_history 账本，备份 / 还原落账，列表可回看。
// Tao 系列存储家法：TaoPaths + AtomicWrite 原子落盘，损坏回落空表，绝不闪退。
using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>备份 / 还原历史账本（追加式，最新在前，上限 500 条）。</summary>
public static class BackupHistory
{
    private static readonly object Gate = new();

    public sealed record Entry(
        string Time, string Engine, string Db, string Kind, string File, bool Ok, long SizeBytes, string Message);

    private static string File => TaoPaths.FilePath("tao_backup_history.json");

    /// <summary>落账（成功 / 失败都记；最新在前）。</summary>
    public static void Add(string engine, string db, string kind, string file, bool ok, long sizeBytes, string message)
    {
        lock (Gate)
        {
            var all = LoadAll();
            all.Insert(0, new Entry(
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                engine, db, kind, file, ok, sizeBytes, message));
            if (all.Count > 500) all = all.Take(500).ToList();
            SaveAll(all);
        }
    }

    public static List<Entry> All() { lock (Gate) { return LoadAll(); } }

    public static void Clear() { lock (Gate) { SaveAll(new List<Entry>()); } }

    /// <summary>按文件路径移除账本记录（备份树内删除条目用；文件本身由调用方处置）。</summary>
    public static void RemoveFile(string file)
    {
        lock (Gate)
        {
            var all = LoadAll();
            if (all.RemoveAll(e => e.File == file) > 0) SaveAll(all);
        }
    }

    private static List<Entry> LoadAll()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new List<Entry>();
            return JsonSerializer.Deserialize<List<Entry>>(System.IO.File.ReadAllText(File)) ?? new List<Entry>();
        }
        catch { return new List<Entry>(); }
    }

    private static void SaveAll(List<Entry> all)
    {
        try { AtomicWrite.Text(File, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* 账本失败不阻断备份主流程 */ }
    }
}
