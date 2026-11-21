using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaoDBM.Core;

/// <summary>问答记忆条目：一次问法 → SQL 的对应关系样本。</summary>
/// <remarks>只存问法、SQL、方言与来源标记；疑似连接串凭据的内容在入库前被拒绝，凭据永不落盘。</remarks>
public sealed class QaMemoryEntry
{
    public long Id { get; set; }
    public string Question { get; set; } = "";
    public string Sql { get; set; } = "";
    /// <summary>数据库方言（DbEngine.ToString()）；同一问法在不同方言下分开记忆。</summary>
    public string Engine { get; set; } = "";
    /// <summary>样本来源：rule=规则层产物经确认，model=模型层产物经确认，manual=人工登记。</summary>
    public string Source { get; set; } = "";
    /// <summary>只有确认过的样本才会被召回复用；未确认样本仅存档待处理。</summary>
    public bool Confirmed { get; set; }
    public string? Note { get; set; }
    public string CreatedAt { get; set; } = "";
}

/// <summary>记忆召回结果：命中的条目与相似度得分（1.0 = 归一化后完全一致）。</summary>
public sealed record QaMemoryMatch(QaMemoryEntry Entry, double Score);

/// <summary>成长机制（统一问答记忆）：本地 JSONL 落盘的问法 → SQL 缓存。
/// 规则层与模型层确认过的样本进同一份记忆；重复/相似问法优先走记忆（归一化后字符二元组 Dice 相似度，
/// 不引入模型依赖）；提供清理与导出接口。纯本机文件读写，不外发、不出内网。</summary>
public sealed class QaMemoryStore
{
    /// <summary>疑似凭据（连接串式的 password=/token= 等）一律拒绝落盘。</summary>
    static readonly Regex CredentialLike = new(
        @"(password|passwd|pwd|secret|api[_\-]?key|access[_\-]?key|token)\s*[=:]\s*[^\s;]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly JsonSerializerOptions JsonOpt = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly string _path;
    readonly object _lock = new();
    readonly List<QaMemoryEntry> _items;
    long _nextId;

    QaMemoryStore(string path, List<QaMemoryEntry> items)
    {
        _path = path;
        _items = items;
        _nextId = items.Count == 0 ? 1 : items.Max(i => i.Id) + 1;
    }

    /// <summary>从本地 JSONL 文件加载记忆仓库；坏行跳过，不因单行损坏毁掉整库。</summary>
    public static QaMemoryStore Load(string path)
    {
        var items = new List<QaMemoryEntry>();
        if (File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<QaMemoryEntry>(t, JsonOpt) is { } e) items.Add(e);
                }
                catch (JsonException)
                {
                    // 坏行跳过：记忆是缓存，允许缺一条，不允许整库不可用。
                }
            }
        }
        return new QaMemoryStore(path, items);
    }

    public int Count { get { lock (_lock) return _items.Count; } }

    /// <summary>登记一条问答样本；疑似凭据内容拒绝入库并返回 null。</summary>
    public QaMemoryEntry? Record(string question, string sql, DbEngine engine, string source,
        bool confirmed = false, string? note = null)
    {
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(sql)) return null;
        if (CredentialLike.IsMatch(sql) || CredentialLike.IsMatch(question)) return null;
        lock (_lock)
        {
            var e = new QaMemoryEntry
            {
                Id = _nextId++,
                Question = question.Trim(),
                Sql = sql.Trim(),
                Engine = engine.ToString(),
                Source = source,
                Confirmed = confirmed,
                Note = note,
                CreatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            };
            _items.Add(e);
            SaveCore();
            return e;
        }
    }

    /// <summary>确认样本（规则层或模型层的确认动作都走这里）；确认后样本进入召回复用池。</summary>
    public bool Confirm(long id)
    {
        lock (_lock)
        {
            var e = _items.FirstOrDefault(x => x.Id == id);
            if (e is null) return false;
            if (e.Confirmed) return true;
            e.Confirmed = true;
            SaveCore();
            return true;
        }
    }

    /// <summary>召回：在已确认样本中做简单相似度匹配（可选按方言过滤），取最高分且不低于阈值的样本。</summary>
    public QaMemoryMatch? Recall(string question, string? engine = null, double minScore = 0.82)
    {
        var nq = Normalize(question);
        if (nq.Length == 0) return null;
        lock (_lock)
        {
            QaMemoryMatch? best = null;
            foreach (var e in _items)
            {
                if (!e.Confirmed) continue;
                if (!string.IsNullOrEmpty(engine) &&
                    !string.Equals(e.Engine, engine, StringComparison.OrdinalIgnoreCase)) continue;
                var s = Similarity(nq, Normalize(e.Question));
                if (s < minScore) continue;
                if (best is null || s > best.Score) best = new QaMemoryMatch(e, s);
            }
            return best;
        }
    }

    /// <summary>清理：predicate 为空时清空全部，返回清理条数；落盘文件同步收缩。</summary>
    public int Clear(Func<QaMemoryEntry, bool>? predicate = null)
    {
        lock (_lock)
        {
            Func<QaMemoryEntry, bool> p = predicate ?? (_ => true);
            int n = _items.RemoveAll(new Predicate<QaMemoryEntry>(p));
            if (n > 0) SaveCore();
            return n;
        }
    }

    /// <summary>导出：把当前记忆复制为另一份 JSONL 文件（仍只在本机文件系统内，不外发），返回导出条数。</summary>
    public int Export(string path)
    {
        lock (_lock)
        {
            File.WriteAllLines(path, _items.Select(ToJson));
            return _items.Count;
        }
    }

    /// <summary>当前全部条目的只读快照（供界面列表或导出预览用）。</summary>
    public IReadOnlyList<QaMemoryEntry> Snapshot()
    {
        lock (_lock) return _items.ToList();
    }

    void SaveCore()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllLines(_path, _items.Select(ToJson));
    }

    static string ToJson(QaMemoryEntry e) => JsonSerializer.Serialize(e, JsonOpt);

    internal static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        var ga = Bigrams(a);
        var gb = Bigrams(b);
        var count = new Dictionary<string, int>();
        foreach (var g in ga) count[g] = count.TryGetValue(g, out int c) ? c + 1 : 1;
        int inter = 0;
        foreach (var g in gb)
            if (count.TryGetValue(g, out int c) && c > 0) { count[g] = c - 1; inter++; }
        return 2.0 * inter / (ga.Count + gb.Count);
    }

    static List<string> Bigrams(string s)
    {
        var r = new List<string>();
        for (int i = 0; i + 1 < s.Length; i++) r.Add(s.Substring(i, 2));
        return r;
    }
}

/// <summary>统一问库入口：先查问答记忆（已确认样本），未命中再走规则层（多表联查或单表问法）。
/// 规则生成的 SQL 经用户确认后由调用方回写记忆，样本随使用越积越多，重复相似问法越来越快。</summary>
public static class QaAssistant
{
    /// <summary>多表问法：记忆优先，未命中走 NaturalLanguageJoinQuery 规则层。</summary>
    public static NLQueryResult AskMultiTable(string question, IReadOnlyList<MultiTableSpec> tables,
        DbEngine engine, QaMemoryStore memory, IReadOnlyList<ErdEdge>? knownEdges = null)
    {
        var hit = memory.Recall(question, engine.ToString());
        if (hit is not null)
            return new NLQueryResult(true, hit.Entry.Sql,
                $"命中本地问答记忆（相似度 {hit.Score:0.00}），直接复用已确认的 SQL。", "high");
        return NaturalLanguageJoinQuery.Ask(question, tables, engine, knownEdges);
    }

    /// <summary>单表问法：记忆优先，未命中走 NaturalLanguageQuery 规则层。</summary>
    public static NLQueryResult AskSingleTable(string question, string table,
        IReadOnlyList<ColumnSpec> columns, DbEngine engine, QaMemoryStore memory)
    {
        var hit = memory.Recall(question, engine.ToString());
        if (hit is not null)
            return new NLQueryResult(true, hit.Entry.Sql,
                $"命中本地问答记忆（相似度 {hit.Score:0.00}），直接复用已确认的 SQL。", "high");
        return NaturalLanguageQuery.Ask(question, table, columns, engine);
    }
}
