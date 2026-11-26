using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>定时 SQL 任务条目：到点由任务中心提交执行。</summary>
public class ScheduledSqlEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Db { get; set; } = "";
    public string Sql { get; set; } = "";
    /// <summary>间隔分钟数（≥1）；到点重复执行。</summary>
    public int IntervalMinutes { get; set; } = 60;
    public bool Enabled { get; set; } = true;
    /// <summary>下次执行时间（绝对时间）。</summary>
    public DateTimeOffset NextRunAt { get; set; }
    /// <summary>最近一次执行结果（done / failed / 未跑过为空）。</summary>
    public string LastResult { get; set; } = "";
    /// <summary>批次三格1：任务类型（query/backup/sync/import/export/dictionary/faker）。
    /// 旧 JSON 缺省兼容为 query；分派执行时 Kind 为空或未知按 query 处理，不阻断旧条目。</summary>
    public string Kind { get; set; } = "query";
}

/// <summary>
/// 定时 SQL 管理面引擎（对账表「定时 SQL 管理面」收口）：任务 CRUD + 到点扫描 + JSON 持久化。
/// 纯逻辑可测试；执行由调用方（任务中心）接管，本引擎只负责「该跑什么」。
/// </summary>
public static class SqlScheduler
{
    private static readonly object Gate = new();
    private static readonly string StorePath = TaoPaths.FilePath("scheduled-sql.json");   // 发现108/112：改走数据根

    private static List<ScheduledSqlEntry> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return new List<ScheduledSqlEntry>();
            return JsonSerializer.Deserialize<List<ScheduledSqlEntry>>(File.ReadAllText(StorePath)) ?? new List<ScheduledSqlEntry>();
        }
        catch { return new List<ScheduledSqlEntry>(); }
    }

    private static void Save(List<ScheduledSqlEntry> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static List<ScheduledSqlEntry> List() { lock (Gate) return Load(); }

    /// <summary>新增任务：Id 自动生成，NextRunAt = now + interval。返回带 Id 的条目。</summary>
    public static ScheduledSqlEntry Add(string name, string db, string sql, int intervalMinutes, string kind = "query")
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("任务名不能为空");
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("SQL 不能为空");
        if (intervalMinutes < 1) throw new ArgumentException("间隔至少 1 分钟");
        lock (Gate)
        {
            var list = Load();
            var e = new ScheduledSqlEntry
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = name.Trim(),
                Db = db,
                Sql = sql.Trim(),
                IntervalMinutes = intervalMinutes,
                Enabled = true,
                NextRunAt = DateTimeOffset.Now.AddMinutes(intervalMinutes),
                Kind = NormalizeKind(kind),
            };
            list.Add(e);
            Save(list);
            return e;
        }
    }

    /// <summary>批次三格1：任务类型归一——空/未知回退 query，旧条目兼容口径。</summary>
    public static string NormalizeKind(string? kind)
    {
        var k = (kind ?? "").Trim().ToLowerInvariant();
        return k switch
        {
            "backup" or "sync" or "import" or "export" or "dictionary" or "faker" => k,
            _ => "query",
        };
    }

    /// <summary>更新任务；不存在返回 false。</summary>
    public static bool Update(string id, string? name, string? db, string? sql, int? intervalMinutes, bool? enabled)
    {
        lock (Gate)
        {
            var list = Load();
            var e = list.FirstOrDefault(x => x.Id == id);
            if (e is null) return false;
            if (name is not null && name.Trim().Length > 0) e.Name = name.Trim();
            if (db is not null) e.Db = db;
            if (sql is not null && sql.Trim().Length > 0) e.Sql = sql.Trim();
            if (intervalMinutes is int iv)
            {
                if (iv < 1) throw new ArgumentException("间隔至少 1 分钟");
                e.IntervalMinutes = iv;
            }
            if (enabled is bool en) e.Enabled = en;
            Save(list);
            return true;
        }
    }

    public static bool Delete(string id)
    {
        lock (Gate)
        {
            var list = Load();
            int n = list.RemoveAll(x => x.Id == id);
            if (n > 0) Save(list);
            return n > 0;
        }
    }

    /// <summary>到点扫描：返回已到期且启用的任务，并把各自 NextRunAt 顺延一个间隔（错过的按当前时间顺延）。</summary>
    public static List<ScheduledSqlEntry> TakeDue(DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.Now;
        lock (Gate)
        {
            var list = Load();
            var due = list.Where(x => x.Enabled && x.NextRunAt <= t).ToList();
            if (due.Count > 0)
            {
                foreach (var e in due)
                {
                    e.NextRunAt = e.NextRunAt.AddMinutes(e.IntervalMinutes) > t
                        ? e.NextRunAt.AddMinutes(e.IntervalMinutes)
                        : t.AddMinutes(e.IntervalMinutes);
                }
                Save(list);
            }
            return due;
        }
    }

    /// <summary>回写最近执行结果（任务中心跑完调用）。</summary>
    public static void RecordResult(string id, string result)
    {
        lock (Gate)
        {
            var list = Load();
            var e = list.FirstOrDefault(x => x.Id == id);
            if (e is null) return;
            e.LastResult = result;
            Save(list);
        }
    }
}
