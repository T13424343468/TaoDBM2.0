using MongoDB.Bson;
using MongoDB.Driver;

namespace TaoDBM.Core;

/// <summary>MongoDB：库 = database，子节点 = collection。</summary>
public class MongoProvider : IDbProvider
{
    public ConnectionProfile Profile { get; }
    public string EngineName => EngineInfo.Name(Profile.Engine);
    public bool IsNoSql => true;
    public bool SupportsBrowse => true;
    public string BrowseLabel => "集合";

    public MongoProvider(ConnectionProfile p) => Profile = p;

    public string Quote(string ident) => ident; // Mongo 无 SQL 标识符引用

    /// <summary>拟真数据专用：向集合批量插入文档（find 命令不覆盖写路径）。</summary>
    public Task<long> InsertManyAsync(string db, string collection, List<BsonDocument> docs) => Task.Run(async () =>
    {
        if (docs.Count == 0) return 0L;
        await DB(db).GetCollection<BsonDocument>(collection).InsertManyAsync(docs);
        return docs.Count;
    });


    private IMongoDatabase DB(string? db)
    {
        var client = BuildClient();
        return client.GetDatabase(string.IsNullOrEmpty(db) ? "test" : db);
    }

    private MongoClient BuildClient()
    {
        var (host, port) = SshTunnel.Resolve(Profile, Profile.Host, Profile.Port > 0 ? Profile.Port : 27017);
        var url = $"mongodb://{Uri.EscapeDataString(Profile.User)}:{Uri.EscapeDataString(Profile.Password)}@{host}:{port}";
        if (string.IsNullOrEmpty(Profile.User)) url = $"mongodb://{host}:{port}";
        return new MongoClient(MongoUrl.Create(url + "/?serverSelectionTimeoutMS=8000&connectTimeoutMS=8000"));
    }

    public Task TestConnection() => Task.Run(() => BuildClient().ListDatabaseNames());

    public Task<List<string>> GetDatabases() => Task.Run(async () =>
    {
        var client = BuildClient();
        var names = await (await client.ListDatabaseNamesAsync()).ToListAsync();
        return names;
    });


    public Task<List<string>> GetChildren(string db) => Task.Run(async () =>
    {
        var list = await (await DB(db).ListCollectionNamesAsync()).ToListAsync();
        return list;
    });


    public Task<List<ColumnSpec>> GetColumns(string db, string child)
    {
        List<ColumnSpec> cols = new()
        {
            new ColumnSpec { Name = "_id", DataType = "ObjectId", IsKey = true },
            new ColumnSpec { Name = "(document)", DataType = "JSON" },
        };
        return Task.FromResult(cols);
    }

    public Task<List<QueryResult>> Execute(string db, string script) => Task.Run(async () =>
    {
        // 对象树目录命令（TAODBMCMD:）：listViews / listIndexes / none / collInfo:<集合>，不走 find 过滤器路径
        if (script.StartsWith("TAODBMCMD:", StringComparison.Ordinal))
            return await RunCatalog(db, script["TAODBMCMD:".Length..]);

        // 约定：输入为 JSON 过滤器（可为空 {}），对选中集合执行 find
        var col = CurrentCollection ?? throw new InvalidOperationException("请先在左侧树中选择集合，或新建查询时选中集合节点。");
        var filter = BsonDocument.Parse(string.IsNullOrWhiteSpace(script) ? "{}" : script.Trim());
        var qr = new QueryResult { Title = $"{col} · find({script.Trim()})" };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var docs = await DB(db).GetCollection<BsonDocument>(col).Find(filter).Limit(200).ToListAsync();
        qr.Columns.Add("_id");
        qr.Columns.Add("(document)");
        foreach (var d in docs)
        {
            string json = d.ToJson(new() { Indent = false });
            string id = d.TryGetValue("_id", out var idv) ? idv.ToString() ?? "" : "";
            qr.Rows.Add(new object?[] { id, json });
        }
        sw.Stop();
        qr.Elapsed = sw.Elapsed;
        return new List<QueryResult> { qr };
    });

    /// <summary>对象树目录命令执行：视图清单 / 索引清单（集合.索引）/ 集合信息；Mongo 无外键、例程、触发器概念，none 返回空。</summary>
    private async Task<List<QueryResult>> RunCatalog(string db, string command)
    {
        var qr = new QueryResult { Title = "Mongo 目录 · " + command };
        var database = DB(db);
        switch (command)
        {
            case "listViews":
                qr.Columns.Add("name");
                qr.Columns.Add("definition");
                var viewFilter = new BsonDocument("type", "view");
                foreach (var c in await (await database.ListCollectionsAsync(
                    new ListCollectionsOptions { Filter = viewFilter })).ToListAsync())
                    qr.Rows.Add(new object?[] { c["name"].AsString, c.TryGetValue("options", out var o) ? o.ToJson() : "" });
                break;
            case "listIndexes":
                qr.Columns.Add("index_name");
                qr.Columns.Add("collection");
                qr.Columns.Add("keys");
                var collNames = await (await database.ListCollectionNamesAsync()).ToListAsync();
                foreach (var name in collNames)
                {
                    var cursor = await database.GetCollection<BsonDocument>(name).Indexes.ListAsync();
                    foreach (var ix in await cursor.ToListAsync())
                    {
                        string ixName = ix.TryGetValue("name", out var n) ? n.AsString : "";
                        if (ixName == "_id_") continue;
                        qr.Rows.Add(new object?[] { ixName, name, ix.TryGetValue("keys", out var k) ? k.ToJson() : "" });
                    }
                }
                break;
            case "none":
                qr.Columns.Add("name");
                qr.Message = "MongoDB 无该对象类型";
                break;
            case "serverStatus":
            {
                var status = await database.RunCommandAsync<BsonDocument>(new BsonDocument("serverStatus", 1));
                qr.Columns.Add("指标");
                qr.Columns.Add("值");
                qr.Rows.Add(new object?[] { "host", status.TryGetValue("host", out var h) ? h.AsString : "" });
                if (status.TryGetValue("version", out var v)) qr.Rows.Add(new object?[] { "version", v.AsString });
                if (status.TryGetValue("uptime", out var up)) qr.Rows.Add(new object?[] { "uptime(秒)", up.ToString() });
                if (status.TryGetValue("connections", out var conn) && conn is BsonDocument connDoc)
                {
                    qr.Rows.Add(new object?[] { "connections.current", connDoc.TryGetValue("current", out var c) ? c.ToString() : "" });
                    qr.Rows.Add(new object?[] { "connections.available", connDoc.TryGetValue("available", out var a) ? a.ToString() : "" });
                }
                if (status.TryGetValue("opcounters", out var ops) && ops is BsonDocument opsDoc)
                    foreach (var name in new[] { "insert", "query", "update", "delete", "command" })
                        if (opsDoc.TryGetValue(name, out var val))
                            qr.Rows.Add(new object?[] { "opcounters." + name, val.ToString() });
                if (status.TryGetValue("mem", out var mem) && mem is BsonDocument memDoc)
                {
                    qr.Rows.Add(new object?[] { "mem.resident(MB)", memDoc.TryGetValue("resident", out var r) ? r.ToString() : "" });
                    qr.Rows.Add(new object?[] { "mem.virtual(MB)", memDoc.TryGetValue("virtual", out var vi) ? vi.ToString() : "" });
                }
                break;
            }
            case "topCollections":
            {
                qr.Columns.Add("collection");
                qr.Columns.Add("count");
                var collNames2 = await (await database.ListCollectionNamesAsync()).ToListAsync();
                foreach (var name in collNames2)
                {
                    var est = await database.GetCollection<BsonDocument>(name).EstimatedDocumentCountAsync();
                    qr.Rows.Add(new object?[] { name, est.ToString() });
                }
                qr.Message = "（按集合估算文档数，最多展示 8 项）";
                break;
            }
            default:
                if (command.StartsWith("collInfo:", StringComparison.Ordinal))
                {
                    var coll = command["collInfo:".Length..];
                    qr.Columns.Add("name");
                    qr.Columns.Add("info");
                    var nameFilter = new BsonDocument("name", coll);
                    var infos = await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = nameFilter });
                    foreach (var c in await infos.ToListAsync())
                        qr.Rows.Add(new object?[] { c["name"].AsString, c.ToJson() });
                }
                else throw new InvalidOperationException("未知 Mongo 目录命令：" + command);
                break;
        }
        return new List<QueryResult> { qr };
    }


    public Task<QueryResult> Browse(string db, string collection, int offset, int limit) => Task.Run(async () =>
    {
        var col = DB(db).GetCollection<BsonDocument>(collection);
        var qr = new QueryResult { Title = collection };
        qr.TotalRows = await col.CountDocumentsAsync(new BsonDocument());
        var docs = await col.Find(new BsonDocument()).Skip(offset).Limit(limit).ToListAsync();
        qr.Columns.Add("_id");
        qr.Columns.Add("(document)");
        foreach (var d in docs)
        {
            string id = d.TryGetValue("_id", out var idv) ? idv.ToString() ?? "" : "";
            qr.Rows.Add(new object?[] { id, d.ToJson() });
        }
        return qr;
    });


    /// <summary>当前操作的集合（由界面在展开集合/新建查询时设置）。</summary>
    public string? CurrentCollection { get; set; }

    public void Dispose() { }
}
