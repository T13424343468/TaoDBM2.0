﻿﻿using System;
using System.Text;
using TaoDBM.Core;
using TaoDBM.UI;

int pass = 0, fail = 0;
var layerPass = new Dictionary<string, int>();
var layerFail = new Dictionary<string, int>();
string layer = "单元";
void Layer(string name)
{
    layer = name;
    layerPass.TryAdd(name, 0);
    layerFail.TryAdd(name, 0);
    Console.WriteLine($"\n========== {name}层 ==========");
}
void Check(string name, bool ok, string detail = "")
{
    layerPass.TryAdd(layer, 0);
    layerFail.TryAdd(layer, 0);
    if (ok) { pass++; layerPass[layer]++; Console.WriteLine($"  ✓ {name}"); }
    else { fail++; layerFail[layer]++; Console.WriteLine($"  ✗ {name}  {detail}"); }
}
Layer("单元");

Console.WriteLine("== SqlSplitter ==");
{
    var parts = SqlSplitter.Split("SELECT 1; SELECT 2;");
    Check("普通拆分", parts.Count == 2 && parts[0] == "SELECT 1" && parts[1] == "SELECT 2", string.Join("|", parts));
}
{
    var parts = SqlSplitter.Split("SELECT 'a;b' AS v;\nSELECT 2");
    Check("字符串内分号不拆", parts.Count == 2 && parts[0] == "SELECT 'a;b' AS v", string.Join("|", parts));
}
{
    var parts = SqlSplitter.Split("SELECT 1 -- comment; select\n; SELECT 2");
    Check("行注释内分号不拆", parts.Count == 2, string.Join("|", parts));
}
{
    var parts = SqlSplitter.Split("/* ; ; */ SELECT 1");
    Check("块注释内分号不拆", parts.Count == 1 && parts[0] == "/* ; ; */ SELECT 1", string.Join("|", parts));
}
{
    var parts = SqlSplitter.Split("SELECT `we;ird` FROM t");
    Check("反引号内分号不拆", parts.Count == 1, string.Join("|", parts));
}
{
    var parts = SqlSplitter.Split("SELECT '''; SELECT 2");
    Check("转义引号", parts.Count == 1 && parts[0].StartsWith("SELECT '''"), string.Join("|", parts));
}
{
    var parts = SqlSplitter.Split("  ;  ;  ");
    Check("空脚本返回空", parts.Count == 0, string.Join("|", parts));
}

Console.WriteLine("== Exporter ==");
{
    var r = new QueryResult { Columns = { "id", "name", "note" } };
    r.Rows.Add(new object?[] { 1, "张三", "有,逗号" });
    r.Rows.Add(new object?[] { 2, "李\"四", "换\n行" });
    var csv = Exporter.Csv(r);
    Check("CSV 表头", csv.Contains("id,name,note"));
    Check("CSV 逗号转义", csv.Contains("\"有,逗号\""), csv);
    Check("CSV 引号转义", csv.Contains("\"李\"\"四\""), csv);
    Check("CSV BOM", csv.StartsWith('\uFEFF'));
}
{
    var r = new QueryResult { Columns = { "id", "name" } };
    r.Rows.Add(new object?[] { 1, "O'Brien" });
    r.Rows.Add(new object?[] { 2, DBNull.Value });
    r.Rows.Add(new object?[] { 3, new byte[] { 0xDE, 0xAD } });
    var sql = Exporter.InsertSql(r, "users");
    Check("INSERT 引号转义", sql.Contains("'O''Brien'"), sql);
    Check("INSERT NULL", sql.Contains("NULL"), sql);
    Check("INSERT 十六进制", sql.Contains("X'DEAD'"), sql);
}
{
    // A1 流式导出：逐页写 TextWriter，不整包攒串；行为必须与整包版严格同形
    var pages = new[]
    {
        new TaoDBM.Core.Streaming.QueryPage { Rows = new object[][] { new object[] { 1, "张三" }, new object[] { 2, "有,逗号" } }, HasMore = true },
        new TaoDBM.Core.Streaming.QueryPage { Rows = new object[][] { new object[] { 3, "李\"四" } }, HasMore = false },
    };
    var sw = new StringWriter();
    long n = Exporter.CsvStream(pages, new[] { "id", "name" }, sw);
    var sameRows = new QueryResult { Columns = { "id", "name" } };
    sameRows.Rows.Add(new object?[] { 1, "张三" });
    sameRows.Rows.Add(new object?[] { 2, "有,逗号" });
    sameRows.Rows.Add(new object?[] { 3, "李\"四" });
    Check("流式 CSV 与整包同形", n == 3 && sw.ToString() == Exporter.Csv(sameRows), $"n={n}");
    var sw2 = new StringWriter();
    long n2 = Exporter.InsertSqlStream(pages, new[] { "id", "name" }, "users", sw2);
    Check("流式 INSERT 同形", n2 == 3 && sw2.ToString() == Exporter.InsertSql(sameRows, "users"), $"n2={n2}");
    // 取消语义：枚举中途取消须抛 OperationCanceledException，不吞不漏
    var cts = new CancellationTokenSource();
    IEnumerable<TaoDBM.Core.Streaming.QueryPage> CancelOnSecond()
    {
        yield return pages[0];
        cts.Cancel();
        yield return pages[1];
    }
    bool canceled = false;
    try { Exporter.CsvStream(CancelOnSecond(), new[] { "id", "name" }, new StringWriter(), cts.Token); }
    catch (OperationCanceledException) { canceled = true; }
    Check("流式导出可取消", canceled);
}

Console.WriteLine("== EngineInfo ==");
{
    Check("默认端口", EngineInfo.DefaultPort(DbEngine.MySQL) == 3306 && EngineInfo.DefaultPort(DbEngine.Redis) == 6379);
    Check("文件库判断", EngineInfo.IsFileBased(DbEngine.SQLite) && !EngineInfo.IsFileBased(DbEngine.MySQL));
    Check("NoSql 判断", EngineInfo.IsRelational(DbEngine.MongoDB) == false && EngineInfo.IsRelational(DbEngine.Oracle));
    Check("引擎数量", EngineInfo.All.Count() == 7);
}

Console.WriteLine("== CsvImporter ==");
{
    var (headers, rows) = CsvImporter.Parse("id,name,note\n1,\"有,逗号\",\"带\"\"引号\"\"的\"\n2,李四,\"跨\n行值\"");
    Check("CSV 表头", headers.SequenceEqual(new[] { "id", "name", "note" }), string.Join("|", headers));
    Check("CSV 逗号转义还原", rows[0][1] == "有,逗号");
    Check("CSV 双引号转义还原", rows[0][2] == "带\"引号\"的");
    Check("CSV 跨行值", rows[1][2] == "跨\n行值");
}
{
    var (headers, rows) = CsvImporter.Parse("\uFEFFa,b\n,x\n");
    Check("CSV BOM 剥离", headers.SequenceEqual(new[] { "a", "b" }));
    Check("CSV 空单元格为 null", rows[0][0] == null);
}
{
    var prov = new TestProv();
    var cols = new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "int", IsKey = true },
        new() { Name = "name", DataType = "varchar(50)" },
    };
    var stmts = CsvImporter.BuildInserts(prov, "db", "t", cols, new List<string> { "id", "name" },
        new List<string?[]> { new string?[] { "1", "O'Brien" }, new string?[] { "", "李四" } }, batch: 10);
    Check("导入数字不加引号", stmts[0].Contains("VALUES (1, 'O''Brien')"), stmts[0]);
    Check("导入空值→NULL", stmts[0].Contains("VALUES (NULL, '李四')"), stmts[0]);
    Check("导入标识符引用", stmts[0].Contains("INSERT INTO `t` (`id`, `name`)"), stmts[0]);
}

Console.WriteLine("== ImportStreaming ==");
{
    // 与整包解析同形：含 BOM、引号转义、跨行值
    var sample = "id,name,note\n1,\"有,逗号\",\"带\"\"引号\"\"的\"\n2,李四,\"跨\n行值\"";
    var (h0, r0) = CsvImporter.Parse(sample);
    var rr = new TaoDBM.Core.Streaming.CsvRowReader();
    var streamed = rr.Rows(new StringReader(sample)).ToList();
    Check("流式 CSV 表头一致", rr.Headers.SequenceEqual(h0), string.Join("|", rr.Headers));
    Check("流式 CSV 行数一致", streamed.Count == r0.Count, $"streamed={streamed.Count}, parse={r0.Count}");
    bool same = streamed.Count == r0.Count && streamed.Zip(r0).All(p => p.First.SequenceEqual(p.Second));
    Check("流式 CSV 行内容一致", same);
}
{
    // 250 行 → 100/100/50 三批
    var csv = "id\n" + string.Join("\n", Enumerable.Range(1, 250));
    var batches = TaoDBM.Core.Streaming.ImportStreaming.CsvBatches(new StringReader(csv)).ToList();
    Check("批次划分 100/100/50", batches.Count == 3 && batches[0].Rows.Count == 100 && batches[1].Rows.Count == 100 && batches[2].Rows.Count == 50,
        $"count={batches.Count}, sizes={string.Join(",", batches.Select(b => b.Rows.Count))}");
    Check("批次累进行数", batches[2].CumulativeRows == 250);
    Check("批次不带截断", batches.All(b => !b.Truncated));
}
{
    // 行数上限兜底：MaxRows=120 → 120 行即截断
    var csv = "id\n" + string.Join("\n", Enumerable.Range(1, 250));
    var batches = TaoDBM.Core.Streaming.ImportStreaming.CsvBatches(new StringReader(csv),
        new TaoDBM.Core.Streaming.ImportOptions { MaxRows = 120 }).ToList();
    Check("行数上限截断", batches.Count == 2 && batches[1].CumulativeRows == 120 && batches[1].Truncated,
        $"count={batches.Count}, last={batches.LastOrDefault()?.CumulativeRows}");
}
{
    // 空输入与仅表头：不产出批次
    var none = TaoDBM.Core.Streaming.ImportStreaming.CsvBatches(new StringReader("")).ToList();
    Check("空输入无批次", none.Count == 0);
    var headerOnly = TaoDBM.Core.Streaming.ImportStreaming.CsvBatches(new StringReader("id,name\n")).ToList();
    Check("仅表头无数据批", headerOnly.Count == 0);
}
{
    // 溢出暂存回放：批次入临时文件后按序读回一致，Delete 后清空
    var csv = "id,name\n" + string.Join("\n", Enumerable.Range(1, 250).Select(n => $"{n},名{n}"));
    using var spill = new TaoDBM.Infrastructure.TempFileSpillStore();
    var summary = TaoDBM.Core.Streaming.ImportStreaming.CsvIngest(new StringReader(csv), spill);
    Check("溢出批次数", summary.BatchCount == 3 && spill.BatchCount == 3, $"batchCount={summary.BatchCount}");
    Check("溢出总行数", summary.TotalRows == 250 && !summary.Truncated);
    var replay = spill.ReadBatches().ToList();
    Check("溢出回放批次数", replay.Count == 3);
    Check("溢出回放表头", replay.Count > 0 && replay[0].Headers.SequenceEqual(new[] { "id", "name" }));
    Check("溢出回放行数", replay.Sum(b => b.Rows.Count) == 250);
    Check("溢出回放内容一致", replay[0].Rows[0].SequenceEqual(new string?[] { "1", "名1" }) && replay[2].Rows[49].SequenceEqual(new string?[] { "250", "名250" }));
    spill.Delete();
    Check("删除后清空", spill.BatchCount == 0 && spill.ReadBatches().ToList().Count == 0);
}

Console.WriteLine("== QueryHistory ==");
{
    QueryHistory.Add("MySQL", "db1", "SELECT 1");
    QueryHistory.Clear();
    QueryHistory.Add("MySQL", "db1", "SELECT 'x'");
    Check("历史记录与清空", QueryHistory.Items.Count == 1 && QueryHistory.Items[0].Sql == "SELECT 'x'");
}

Console.WriteLine("== Quote（schema 支持）==");
{
    var prov = new TestProv('"');
    Check("PG 逐段引用", prov.Quote("public.users") == "\"public\".\"users\"", prov.Quote("public.users"));
    var my = new TestProv('`');
    Check("MySQL 反引号", my.Quote("mydb.t") == "`mydb`.`t`");
}

Console.WriteLine("== SqlHighlighter ==");
{
    var sql = "SELECT id, 'a;b' FROM `t1` -- 注释\nWHERE x = 1.5e-3 AND n = 0x1F";
    var toks = SqlHighlighter.Tokenize(sql);
    string[] Of(TokenKind k) => toks.Where(t => t.Kind == k).Select(t => SqlHighlighter.Text(sql, t)).ToArray();
    Check("关键字识别", Of(TokenKind.Keyword).Contains("SELECT") && Of(TokenKind.Keyword).Contains("FROM")
        && Of(TokenKind.Keyword).Contains("WHERE") && Of(TokenKind.Keyword).Contains("AND"), string.Join(",", Of(TokenKind.Keyword)));
    Check("字符串含分号整体", Of(TokenKind.String).SequenceEqual(new[] { "'a;b'" }), string.Join(",", Of(TokenKind.String)));
    Check("行注释整体", Of(TokenKind.Comment).SequenceEqual(new[] { "-- 注释" }), string.Join(",", Of(TokenKind.Comment)));
    Check("反引号为引用标识符", Of(TokenKind.QuotedIdent).SequenceEqual(new[] { "`t1`" }));
    Check("科学计数与十六进制数字", Of(TokenKind.Number).SequenceEqual(new[] { "1.5e-3", "0x1F" }), string.Join(",", Of(TokenKind.Number)));
    Check("VARCHAR 识别为类型", SqlHighlighter.Tokenize("VARCHAR(50)").Any(t => t.Kind == TokenKind.TypeName));
    Check("块注释未闭合到结尾", SqlHighlighter.Text("/* abc", SqlHighlighter.Tokenize("/* abc").Single(t => t.Kind == TokenKind.Comment)) == "/* abc");
    Check("空串安全", SqlHighlighter.Tokenize("").Count == 0);
    Check("偏移可还原原文", toks.All(t => t.Start + t.Length <= sql.Length));
}
{
    var sql = "SELECT 'it''s', \"quoted ident\";";
    var toks = SqlHighlighter.Tokenize(sql);
    Check("双引号转义字符串完整", SqlHighlighter.Text(sql, toks.First(t => t.Kind == TokenKind.String)) == "'it''s'");
    Check("PG 引号标识符", SqlHighlighter.Text(sql, toks.First(t => t.Kind == TokenKind.QuotedIdent)) == "\"quoted ident\"");
}

Console.WriteLine("== TableDdl ==");
{
    var my = (Func<string, string>)(s => "`" + s + "`");
    var t = new TableDesign
    {
        TableName = "users",
        Columns =
        {
            new TableColumnDesign { Name = "id", SqlType = "BIGINT", IsPrimaryKey = true, AutoIncrement = true, Nullable = false },
            new TableColumnDesign { Name = "name", SqlType = "VARCHAR(50)", DefaultValue = "无名" },
            new TableColumnDesign { Name = "created", SqlType = "DATETIME", DefaultValue = "CURRENT_TIMESTAMP" },
        },
    };
    var sql = TableDdl.BuildCreate(t, DbEngine.MySQL, my);
    Check("MySQL 建表反引号", sql.Contains("CREATE TABLE `users`"), sql);
    Check("MySQL 自增", sql.Contains("`id` BIGINT AUTO_INCREMENT PRIMARY KEY"), sql);
    Check("默认值字符串加引号", sql.Contains("DEFAULT '无名'"), sql);
    Check("默认值表达式原样", sql.Contains("DEFAULT CURRENT_TIMESTAMP"), sql);
}
{
    var pg = (Func<string, string>)(s => "\"" + s + "\"");
    var t = new TableDesign
    {
        TableName = "orders",
        Columns =
        {
            new TableColumnDesign { Name = "id", SqlType = "int", IsPrimaryKey = true, AutoIncrement = true, Nullable = false },
            new TableColumnDesign { Name = "memo", SqlType = "text" },
        },
    };
    var sql = TableDdl.BuildCreate(t, DbEngine.PostgreSQL, pg);
    Check("PG 身份列", sql.Contains("\"id\" int GENERATED BY DEFAULT AS IDENTITY"), sql);
    var ss = TableDdl.BuildCreate(t, DbEngine.SqlServer, pg);
    Check("SqlServer IDENTITY", ss.Contains("\"id\" int IDENTITY(1,1) PRIMARY KEY"), ss);
    var lite = TableDdl.BuildCreate(t, DbEngine.SQLite, pg);
    Check("SQLite 自增主键", lite.Contains("\"id\" INTEGER PRIMARY KEY AUTOINCREMENT"), lite);
    var ora = TableDdl.BuildCreate(t, DbEngine.Oracle, pg);
    Check("Oracle 身份列", ora.Contains("\"id\" int GENERATED ALWAYS AS IDENTITY PRIMARY KEY"), ora);
    var ora2 = TableDdl.BuildCreate(t, DbEngine.Oracle, s => s);
    Check("Oracle 无引号", ora2.Contains("CREATE TABLE orders ("), ora2);
}
{
    // 复合主键走表级约束
    var t = new TableDesign
    {
        TableName = "rel",
        Columns =
        {
            new TableColumnDesign { Name = "a", SqlType = "INT", IsPrimaryKey = true },
            new TableColumnDesign { Name = "b", SqlType = "INT", IsPrimaryKey = true },
        },
    };
    var sql = TableDdl.BuildCreate(t, DbEngine.MySQL, s => s);
    Check("复合主键表级约束", sql.Contains("PRIMARY KEY (a, b)") && !sql.Contains("a INT PRIMARY KEY"), sql);
}
{
    var my = (Func<string, string>)(s => "`" + s + "`");
    var t = new TableDesign
    {
        TableName = "t",
        Columns =
        {
            new TableColumnDesign { Name = "c1", SqlType = "INT", Nullable = false },
            new TableColumnDesign { Name = "old1", SqlType = "INT" },
        },
    };
    var (stmts, skipped) = TableDdl.BuildAlter(t, new[] { "old1", "dead" }, DbEngine.MySQL, my);
    Check("ALTER 新增列", stmts.Any(s => s == "ALTER TABLE `t` ADD `c1` INT NOT NULL"), string.Join(" | ", stmts));
    Check("ALTER 修改列", stmts.Any(s => s == "ALTER TABLE `t` MODIFY COLUMN `old1` INT"), string.Join(" | ", stmts));
    Check("ALTER 删除列", stmts.Contains("ALTER TABLE `t` DROP COLUMN `dead`"));
    Check("MySQL 改表无跳过", skipped.Count == 0, string.Join("|", skipped));

    var (liteStmts, liteSkip) = TableDdl.BuildAlter(t, new[] { "old1" }, DbEngine.SQLite, s => s);
    Check("SQLite 改列跳过", liteStmts.All(s => !s.Contains("ALTER COLUMN")) && liteSkip.Count > 0, string.Join("|", liteStmts));
    Check("SQLite 仍可加列", liteStmts.Contains("ALTER TABLE t ADD c1 INT NOT NULL"), string.Join("|", liteStmts));

    var (pgStmts, _) = TableDdl.BuildAlter(t, new[] { "old1" }, DbEngine.PostgreSQL, s => s);
    Check("PG 改列三条", pgStmts.Any(s => s == "ALTER TABLE t ALTER COLUMN old1 TYPE INT")
        && pgStmts.Any(s => s == "ALTER TABLE t ALTER COLUMN old1 DROP NOT NULL"), string.Join(" | ", pgStmts));
}
{
    Check("默认值 NULL 原样", TableDdl.FormatDefault("null") == "NULL");
    Check("默认值数字原样", TableDdl.FormatDefault("42") == "42" && TableDdl.FormatDefault("1.5") == "1.5");
    Check("默认值文本加引号", TableDdl.FormatDefault("O'Brien") == "'O''Brien'");
    Check("建表语句包装", TableDdl.AsStatements("CREATE TABLE a (id INT);").SequenceEqual(new[] { "CREATE TABLE a (id INT)" }));
}

Console.WriteLine("== BackupTools ==");
{
    Check("支持矩阵", BackupTools.Supported(DbEngine.MySQL) && BackupTools.Supported(DbEngine.PostgreSQL)
        && BackupTools.Supported(DbEngine.SqlServer) && BackupTools.Supported(DbEngine.SQLite)
        && !BackupTools.Supported(DbEngine.Oracle) && !BackupTools.Supported(DbEngine.MongoDB)
        && !BackupTools.Supported(DbEngine.Redis));
    var spec = BackupTools.MysqlDump("127.0.0.1", 3306, "root", "pw123", "mydb", "c:\\dir with space\\dump.sql");
    Check("mysqldump 参数", spec.Arguments.Contains("--host=127.0.0.1") && spec.Arguments.Contains("--port=3306")
        && spec.Arguments.Contains("--user=root") && spec.Arguments.Contains("--result-file=\"c:\\dir with space\\dump.sql\"")
        && spec.Arguments.EndsWith("mydb"), spec.Arguments);
    Check("密码走环境变量", spec.Env["MYSQL_PWD"] == "pw123" && !spec.Describe.Contains("pw123"));
    var rst = BackupTools.MysqlRestore("h", 3306, "u", "p", "db", "f.sql");
    Check("mysql 还原走 stdin", rst.FileName == "mysql" && rst.InputFile == "f.sql", rst.Describe);
    var pg = BackupTools.PgDump("h", 5432, "u", "p", "db", "o.sql");
    Check("pg_dump 参数", pg.FileName == "pg_dump" && pg.Arguments.Contains("--username=u")
        && pg.Arguments.Contains("--file=o.sql") && pg.Arguments.EndsWith("db"), pg.Arguments);
    Check("PG 密码环境变量", pg.Env["PGPASSWORD"] == "p" && !pg.Describe.Contains("p]"));
    Check("psql 还原", BackupTools.PgRestore("h", 5432, "u", "", "db", "f.sql").FileName == "psql");
    Check("参数加引号", BackupTools.QuoteArg("a b") == "\"a b\"" && BackupTools.QuoteArg("abc") == "abc");
    Check("SqlServer 备份语句", BackupTools.SqlServerBackupSql("my db", "d:\\x.bak")
        == "BACKUP DATABASE [my db] TO DISK = 'd:\\x.bak' WITH FORMAT, INIT");
    Check("SqlServer 还原语句", BackupTools.SqlServerRestoreSql("db", "d:\\x.bak")
        == "RESTORE DATABASE [db] FROM DISK = 'd:\\x.bak' WITH REPLACE");
    Check("SqlServer 单引号转义", BackupTools.SqlServerBackupSql("db", "d:\\o'brien.bak").Contains("'d:\\o''brien.bak'"));
}

Console.WriteLine("== L6 恶意与极端输入 + L7 凭据零明文 ==");
{
    // ---- A. ConnectionStore / ProfileCrypto（L7 安全）----
    string connFile = TaoPaths.FilePath("connections.json");
    string? origConn = System.IO.File.Exists(connFile) ? System.IO.File.ReadAllText(connFile) : null;
    try
    {
        const string secret = "S3cret-P@ss-明文";
        var p = new ConnectionProfile
        {
            DisplayName = "测\"连\"接/特殊<名>", User = "u'm'e", Database = "db;drop",
            Host = "127.0.0.1", Port = 3306, Password = secret, SavePassword = true,
        };
        var protectedOne = ProfileCrypto.Protect(secret);
        Check("L7 加密失败不回明文", protectedOne != secret && (protectedOne == "" || protectedOne.StartsWith("dpapi:")),
            protectedOne == secret ? "回退明文" : protectedOne == "" ? "空串(平台无DPAPI)" : "dpapi密文");

        ConnectionStore.Save(new List<ConnectionProfile> { p });
        var onDisk = System.IO.File.ReadAllText(connFile);
        Check("L7 落盘文件零明文密码", !onDisk.Contains(secret), onDisk.Contains(secret) ? "发现明文" : "");

        var back = ConnectionStore.Load();
        var got = back.FirstOrDefault(x => x.Id == p.Id);
        // 读回语义（2026-11-13 修）：安全不变量只在磁盘——零明文、密文带 dpapi: 前缀；
        // 平台支持 DPAPI 时读回按密文还原原文（应用重连需要），不支持时落盘即空、读回为空。
        // 原断言把“读回是明文”误判为残留，实际是 DPAPI 正常解密，在 Windows 云端必假。
        bool hasDpapi = protectedOne.StartsWith("dpapi:", StringComparison.Ordinal);
        Check("L7 读回按密文还原",
            got != null && (hasDpapi ? got.Password == secret : got.Password == ""),
            got == null ? "missing" : (hasDpapi ? (got.Password == secret ? "" : "有DPAPI未还原") : "无DPAPI仍返回值"));
        Check("L7 落盘密文带前缀", !hasDpapi || onDisk.Contains("dpapi:"), "无 dpapi: 前缀");
        Check("特殊字符字段往返一致", got != null && got.DisplayName == p.DisplayName && got.User == p.User && got.Database == p.Database,
            got == null ? "missing" : $"{got.DisplayName}|{got.User}|{got.Database}");

        var p2 = new ConnectionProfile { DisplayName = "不存密码", Password = secret, SavePassword = false };
        ConnectionStore.Save(new List<ConnectionProfile> { p2 });
        var onDisk2 = System.IO.File.ReadAllText(connFile);
        Check("L7 不存密码落盘为空", !onDisk2.Contains(secret), onDisk2.Contains(secret) ? "发现明文" : "");
        Check("不存密码读回为空", ConnectionStore.Load().First(x => x.Id == p2.Id).Password == "");

        System.IO.File.WriteAllText(connFile, "{{{不是JSON");
        Check("损坏配置文件回落空列表", ConnectionStore.Load().Count == 0);
    }
    finally
    {
        if (origConn != null) System.IO.File.WriteAllText(connFile, origConn);
        else if (System.IO.File.Exists(connFile)) System.IO.File.Delete(connFile);
    }

    // ---- B. CsvImporter 恶意极端输入 ----
    var emptyCsv = CsvImporter.Parse("");
    Check("CSV 空串输入", emptyCsv.Headers.Count == 0 && emptyCsv.Rows.Count == 0);
    var unclosed = CsvImporter.Parse("h1,h2\na,\"未闭合");
    Check("CSV 未闭合引号不崩", unclosed.Headers.Count == 2 && unclosed.Rows.Count == 1 && unclosed.Rows[0][0] == "a", string.Join("|", unclosed.Rows.Select(r => string.Join(";", r))));
    var crOnly = CsvImporter.Parse("h1,h2\r1,2\r3,4");
    Check("CSV 孤立 CR 换行", crOnly.Rows.Count == 2, crOnly.Rows.Count.ToString());
    var bigField = new string('X', 1_000_000);
    var bigCsv = CsvImporter.Parse("h\n" + bigField);
    Check("CSV 百万字符单字段", bigCsv.Rows.Count == 1 && bigCsv.Rows[0][0] == bigField);
    var manyCols = "h," + string.Join(",", Enumerable.Range(1, 999).Select(i => "c" + i)) + "\nv1";
    var manyParsed = CsvImporter.Parse(manyCols);
    Check("CSV 千列表头对齐", manyParsed.Headers.Count == 1000 && manyParsed.Rows.Count == 1 && manyParsed.Rows[0][999] == null, $"{manyParsed.Headers.Count}列/{manyParsed.Rows[0].Length}值");
    {
        var cols = new List<ColumnSpec> { new() { Name = "id", DataType = "INT" }, new() { Name = "note", DataType = "VARCHAR" } };
        var stmts = CsvImporter.BuildInserts(new TestProv(), "db", "t", cols,
            new List<string> { "id", "note" },
            new List<string?[]> { new string?[] { "12; DROP TABLE x", "O'Brien" } });
        Check("数字列非数字值引号转义防注入", stmts[0].Contains("'12; DROP TABLE x'") && stmts[0].Contains("'O''Brien'"), stmts[0]);
    }

    // ---- C. SqlSplitter 恶意极端输入 ----
    Check("脚本只有分号", SqlSplitter.Split(";; ;").Count == 0);
    var unclosedQuote = SqlSplitter.Split("SELECT 'a");
    Check("未闭合引号原样单条", unclosedQuote.Count == 1 && unclosedQuote[0] == "SELECT 'a", string.Join("|", unclosedQuote));
    var deepComment = SqlSplitter.Split("/* /* /* /* never closed");
    Check("深层未闭合块注释单条", deepComment.Count == 1 && deepComment[0].StartsWith("/* /*"));
    var massParts = SqlSplitter.Split(string.Join("", Enumerable.Range(0, 10_000).Select(i => $"SELECT {i};")));
    Check("万条语句拆分无丢", massParts.Count == 10_000 && massParts[9999] == "SELECT 9999", massParts.Count.ToString());

    // ---- D. JsonImporter 恶意极端输入 ----
    var badJson = JsonImporter.Parse("[oops");
    Check("JSON 非法数组回落空", badJson.Headers.Count == 0 && badJson.Rows.Count == 0);
    var mixedJsonl = JsonImporter.Parse("{\"a\":\"1\"}\n不是JSON行\n{\"a\":\"2\"}");
    Check("JSONL 畸形行跳过", mixedJsonl.Rows.Count == 2 && mixedJsonl.Rows[1][0] == "2", string.Join("|", mixedJsonl.Rows.Select(r => string.Join(";", r))));
    var loneSurrogate = JsonImporter.Parse("[\"\\ud800\"]");
    Check("未配对代理对不崩", loneSurrogate.Headers.Count == 0 && loneSurrogate.Rows.Count == 0);
}

Console.WriteLine("== Favorites / TabSession ==");
{
    var dir = Path.Combine(Path.GetTempPath(), "taodbm-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var favFile = Path.Combine(dir, "favorites.json");
    var tabFile = Path.Combine(dir, "tabsession.json");
    Favorites.UseFile(favFile);
    TabSession.UseFile(tabFile);

    Check("收藏初始为空", Favorites.Load().Count == 0);
    var favs = new List<FavoriteItem>();
    Favorites.Add(favs, "日报", "MySQL", "shop", "SELECT * FROM orders;");
    Favorites.Add(favs, "同名覆盖", "PG", "shop", "SELECT 1;");
    Favorites.Add(favs, "同名覆盖", "PG", "shop", "SELECT 2;"); // 同名覆盖
    Check("同名覆盖保留单条", favs.Count(i => i.Name == "同名覆盖") == 1 && favs.Count == 2);
    Check("覆盖后内容更新", favs.First(i => i.Name == "同名覆盖").Sql == "SELECT 2;");
    Favorites.Save(favs);
    var reloaded = Favorites.Load();
    Check("收藏往返", reloaded.Count == 2 && reloaded[0].Name == "日报" && reloaded[0].Sql == "SELECT * FROM orders;");
    Check("删除收藏", Favorites.Remove(reloaded, reloaded[0].Id) && reloaded.Count == 1);
    Check("删除不存在的收藏返回 false", !Favorites.Remove(reloaded, "nope"));

    Check("会话初始为空", TabSession.Load().Count == 0);
    var items = new List<TabSessionItem>
    {
        new() { ConnectionId = "c1", Mode = "Query", Db = "shop", Sql = "SELECT 1;" },
        new() { ConnectionId = "c2", Mode = "TableData", Db = "db", Child = "t1" },
    };
    TabSession.Save(items);
    var rt = TabSession.Load();
    Check("会话往返", rt.Count == 2 && rt[0].ConnectionId == "c1" && rt[0].Sql == "SELECT 1;" && rt[1].Child == "t1");
    var profiles = new List<ConnectionProfile> { new() { Id = "c1" } };
    Check("会话按连接过滤", TabSession.FilterValid(rt, profiles).Count == 1);
    Check("空连接Id保留", TabSession.FilterValid(new List<TabSessionItem> { new() { ConnectionId = "" } }, profiles).Count == 1);

    Directory.Delete(dir, true);
}

Console.WriteLine("== DataSync ==");
{
    Func<string, string> bt = n => "`" + n + "`";
    var srcCols = new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "int", Nullable = false, IsKey = true },
        new() { Name = "name", DataType = "varchar(50)" },
        new() { Name = "extra", DataType = "datetime" },
    };
    var dstNames = new List<string> { "id", "name", "legacy" };
    var (stmts, skipped) = DataSync.SyncColumns("orders", srcCols, dstNames, DbEngine.MySQL, bt, alignTypes: false);
    Check("同步只加缺失列", stmts.Count == 1 && stmts[0].Contains("ADD `extra`") && stmts[0].StartsWith("ALTER TABLE `orders`"), string.Join(" | ", stmts));
    Check("同步不删目标多余列", !string.Join("|", stmts).Contains("DROP") && !string.Join("|", stmts).Contains("legacy"));
    Check("默认不对齐类型", !string.Join("|", stmts).Contains("MODIFY"));
    var (stmts2, _) = DataSync.SyncColumns("orders", srcCols, dstNames, DbEngine.MySQL, bt, alignTypes: true);
    Check("对齐类型生成 MODIFY", stmts2.Any(s => s.Contains("MODIFY COLUMN")), string.Join(" | ", stmts2));
    Check("SQLite 对齐类型入跳过", DataSync.SyncColumns("t", srcCols, dstNames, DbEngine.SQLite, n => "\"" + n + "\"", true).Skipped.Count > 0);
    var create = DataSync.CreateForTarget("orders", srcCols, DbEngine.PostgreSQL, n => "\"" + n + "\"");
    Check("目标缺表生成 CREATE", create.StartsWith("CREATE TABLE \"orders\"") && create.Contains("\"id\" int") && create.Contains("PRIMARY KEY"), create);

    var qr = new QueryResult { Title = "orders" };
    qr.Columns.AddRange(new[] { "id", "name", "flag", "created", "blob" });
    qr.Rows.Add(new object[] { 1, "it's", true, new System.DateTime(2026, 10, 31, 8, 0, 0), new byte[] { 0x01, 0xAB } });
    qr.Rows.Add(new object?[] { 2, null, DBNull.Value, null, null });
    qr.Rows.Add(new object?[] { 3, "x", false, null, null });
    var batches = DataSync.InsertBatches(qr, "`orders`", bt, batch: 2);
    Check("分批 INSERT 语句数", batches.Count == 2);
    Check("INSERT 列名引用", batches[0].StartsWith("INSERT INTO `orders` (`id`, `name`, `flag`, `created`, `blob`)"), batches[0].Split('\n')[0]);
    Check("字符串转义", batches[0].Contains("'it''s'"));
    Check("布尔转 TRUE/FALSE", batches[0].Contains("TRUE") && batches[1].Contains("FALSE"));
    Check("日期格式", batches[0].Contains("'2026-10-31 08:00:00'"));
    Check("二进制 X''", batches[0].Contains("X'01AB'"));
    Check("null 与 DBNull 转 NULL", batches[1].Contains("NULL") && !batches[1].Contains("DBNull"));
    Check("空结果零语句", DataSync.InsertBatches(new QueryResult { Title = "t" }, "t", bt).Count == 0);
}
{
    // A1 同步断点水位：凭水位续传只补剩余批次，不重灌已落库数据；水位回读容错
    Func<string, string> qt = n => "`" + n + "`";
    var mk = new QueryResult { Title = "t", Columns = { "id" } };
    for (int i = 1; i <= 5; i++) mk.Rows.Add(new object?[] { i });
    var all = DataSync.InsertBatches(mk, "`t`", qt, batch: 1);   // 5 批
    var seen = new List<int>();
    var rest = SyncCheckpoint.Resume(all, SyncWatermark.Empty("t"), w => seen.Add(w.BatchDone));
    Check("水位从零全量执行", rest.Count == 5 && seen.SequenceEqual(new[] { 1, 2, 3, 4, 5 }));
    var rest2 = SyncCheckpoint.Resume(all, new SyncWatermark { Table = "t", BatchDone = 2 });
    Check("断点续传只补剩余", rest2.Count == 3 && rest2[0] == all[2] && rest2[2] == all[4]);
    var done = SyncCheckpoint.Resume(all, new SyncWatermark { Table = "t", BatchDone = 5 });
    Check("断点已到底幂等空集", done.Count == 0);
    var over = SyncCheckpoint.Resume(all, new SyncWatermark { Table = "t", BatchDone = 99 });
    Check("水位越界不重灌", over.Count == 0);
    var json = SyncCheckpoint.ToJson(new SyncWatermark { Table = "t", BatchDone = 3, LastKey = "k3" });
    var back = SyncCheckpoint.FailSafeParse("t", json);
    Check("水位往返还原", back.Table == "t" && back.BatchDone == 3 && back.LastKey == "k3");
    var broken = SyncCheckpoint.FailSafeParse("t", "{断");
    Check("坏水位容错归零", broken.BatchDone == 0 && broken.Table == "t");
    Check("空水位容错归零", SyncCheckpoint.FailSafeParse("t", null).BatchDone == 0);
}

Console.WriteLine("== ErdModel ==");
{
    var m = new ErdModel
    {
        Tables =
        {
            new ErdTable { Name = "user", Columns = { new() { Name = "id", Type = "int", IsKey = true }, new() { Name = "name", Type = "varchar(50)" } } },
            new ErdTable { Name = "order", Columns = { new() { Name = "id", Type = "int", IsKey = true }, new() { Name = "user_id", Type = "int" }, new() { Name = "amount", Type = "decimal" } } },
            new ErdTable { Name = "items", Columns = { new() { Name = "id", Type = "int", IsKey = true }, new() { Name = "orderId", Type = "int" } } },
        },
    };
    ErdModel.AutoLayout(m);
    Check("网格布局", m.Tables[0].X == 40 && m.Tables[1].X == 340 && m.Tables[2].X == 640 && m.Tables[2].Y == 40);
    var edges = ErdModel.InferEdges(m);
    Check("推断下划线外键", edges.Count == 2
        && edges.Any(e => e.FromTable == "order" && e.FromCol == "user_id" && e.ToTable == "user")
        && edges.Any(e => e.FromTable == "items" && e.FromCol == "orderId" && e.ToTable == "order"),
        string.Join(",", edges.Select(e => e.FromTable + "." + e.FromCol + "->" + e.ToTable)));
    var m2 = new ErdModel
    {
        Tables = {
            new ErdTable { Name = "t", Columns = { new() { Name = "id", Type = "int", IsKey = true }, new() { Name = "t_id", Type = "int" } } },
        },
    };
    Check("自引用不成边", ErdModel.InferEdges(m2).Count == 0);
    Check("无主键目标不成边", ErdModel.InferEdges(new ErdModel
    {
        Tables = {
            new ErdTable { Name = "a", Columns = { new() { Name = "x_id", Type = "int" } } },
            new ErdTable { Name = "x", Columns = { new() { Name = "code", Type = "int" } } },
        },
    }).Count == 0);
    var json = m.ToJson();
    var back = ErdModel.FromJson(json);
    Check("JSON 往返", back is not null && back.Tables.Count == 3 && back.Tables[1].Columns[1].Name == "user_id"
        && back.Tables[1].X == m.Tables[1].X);
    Check("无效 JSON 返回 null", ErdModel.FromJson("{oops") is null);
}

Console.WriteLine("== ReportBuilder ==");
{
    var qr = new QueryResult { Title = "订单" };
    qr.Columns.AddRange(new[] { "id", "city", "amount", "created" });
    qr.Rows.Add(new object[] { 1, "上海", 120.5, new System.DateTime(2026, 10, 1) });
    qr.Rows.Add(new object[] { 2, "北京", 80, new System.DateTime(2026, 10, 2) });
    qr.Rows.Add(new object[] { 3, "上海", DBNull.Value, new System.DateTime(2026, 10, 2) });
    var (rows, stats) = ReportBuilder.Build(qr);
    Check("报表行数", rows == 3 && stats.Count == 4);
    var idStat = stats[0];
    Check("数值列极值均值", idStat.Min == 1 && idStat.Max == 3 && Math.Abs(idStat.Avg!.Value - 2.0) < 1e-9);
    var cityStat = stats[1];
    Check("非数值列 Top 与空值", cityStat.NullCount == 0 && cityStat.Top[0].Value == "上海" && cityStat.Top[0].Count == 2);
    var amtStat = stats[2];
    Check("DBNull 不进数值统计", amtStat.NullCount == 1 && Math.Abs(amtStat.Avg!.Value - 100.25) < 1e-9 && amtStat.Min == 80);
    var html = ReportBuilder.ToHtml(qr, "月度报表");
    Check("HTML 报表含标题与统计", html.Contains("<title>月度报表</title>") && html.Contains("月度报表") && html.Contains("上海") && html.Contains("100.25"));
    Check("HTML 转义", ReportBuilder.ToHtml(new QueryResult { Title = "a<b" }, "a<b").Contains("a&lt;b"));
    var esc = ReportBuilder.ToHtml(new QueryResult { Title = "t" }, "t");
    Check("HTML 头部完整", esc.StartsWith("<!DOCTYPE html>") && esc.EndsWith("</html>"));
}

Console.WriteLine("== JsonImporter ==");
{
    var (headers, rows) = JsonImporter.Parse("""[{"id":1,"name":"张三","active":true},{"id":2,"name":"李'd四","active":false,"extra":null}]""");
    Check("JSON 数组表头与行数", headers.Count == 4 && rows.Count == 2 && headers[0] == "id" && headers[3] == "extra", string.Join(",", headers));
    Check("JSON 值归一（数字/字符串/布尔/null）", rows[0][0] == "1" && rows[0][1] == "张三" && rows[0][2] == "TRUE" && rows[1][3] == null,
        $"{rows[0][0]},{rows[0][1]},{rows[0][2]},{rows[1][3]}");
    var prov = new TestProv();
    var stmts = CsvImporter.BuildInserts(prov, "db", "t",
        new List<ColumnSpec> { new() { Name = "id", DataType = "INT" }, new() { Name = "name", DataType = "VARCHAR(50)" }, new() { Name = "active", DataType = "INT" }, new() { Name = "extra", DataType = "INT" } },
        headers, rows);
    Check("JSON 生成 INSERT（数字不带引号、字符串转义）",
        stmts.Count == 1 && stmts[0].Contains("INSERT INTO `t` (`id`, `name`, `active`, `extra`) VALUES (1, '张三', 'TRUE', NULL);", StringComparison.Ordinal)
        && stmts[0].Contains("'李''d四'"), stmts[0]);
}
{
    var (headers, rows) = JsonImporter.Parse("{\"id\":1}\n{\"id\":2,\"name\":\"b\"}");
    Check("行式 JSONL 合并表头", headers.Count == 2 && rows.Count == 2 && rows[0][1] == null && rows[1][1] == "b", string.Join(",", headers));
}
{
    var (headers, rows) = JsonImporter.Parse("""[{"id":1,"meta":{"k":1},"tags":[1,2]}]""");
    Check("嵌套对象/数组序列化为文本", headers.Count == 3 && rows[0][1] == """{"k":1}""" && rows[0][2] == "[1,2]", $"{rows[0][1]}|{rows[0][2]}");
}
{
    var (headers, rows) = JsonImporter.Parse("[1,2,3]");
    Check("非对象数组为空", headers.Count == 0 && rows.Count == 0);
}

Console.WriteLine("== TnsNames ==");
{
    const string ora = """
# 数据库连接描述符
ORCL =
  (DESCRIPTION =
    (ADDRESS = (PROTOCOL = TCP)(HOST = dbhost1.example.com)(PORT = 1521))
    (CONNECT_DATA =
      (SERVER = DEDICATED)
      (SERVICE_NAME = orcl.example.com)
    )
  )

ERP, ERP_OLD = (DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=10.0.0.8)(PORT=1522))(CONNECT_DATA=(SERVICE_NAME=erp)))
BAD = (DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(PORT=1523)))
""";
    var list = TnsNames.Parse(ora);
    Check("解析条目数（BAD 无 HOST 跳过）", list.Count == 3, string.Join(",", list.Select(e => e.Name)));
    var orcl = list.FirstOrDefault(e => e.Name == "ORCL");
    Check("ORCL 主机端口服务名", orcl != null && orcl.Host == "dbhost1.example.com" && orcl.Port == 1521 && orcl.Service == "orcl.example.com", orcl?.ToString() ?? "missing");
    Check("多别名拆分", list.Any(e => e.Name == "ERP") && list.Any(e => e.Name == "ERP_OLD") && list.All(e => e.Name != " ERP"));
    var erp = list.First(e => e.Name == "ERP");
    Check("ERP 条目字段", erp.Host == "10.0.0.8" && erp.Port == 1522 && erp.Service == "erp");
    Check("注释被剔除", list.All(e => e.Host != "注释主机") && TnsNames.Parse("# ORCL = (HOST=x)\nREAL = (HOST=h)(PORT=1)").Count == 1);
}

{
    // 主题三象：配色完整性、铁律与回落规则（水墨/天青/墨夜，ant 退役映射天青；天青 2026-11-06 对齐 TaoAgent 色板）
    Check("三象恰三套且同键集",
        ThemePalette.Names.Length == 3
        && ThemePalette.Names[0] == "ink" && ThemePalette.Names[1] == "gray" && ThemePalette.Names[2] == "night"
        && new[] { ThemePalette.Ink, ThemePalette.Gray, ThemePalette.Night }
            .All(pal => pal.Count == ThemePalette.Keys.Length && ThemePalette.Keys.All(k => pal.ContainsKey(k))));
    bool HexOk(string h) => h.StartsWith('#') && h.Length == 7;
    Check("三象色值格式合法",
        new[] { ThemePalette.Ink, ThemePalette.Gray, ThemePalette.Night }
            .All(pal => ThemePalette.Keys.All(k => HexOk(pal[k]))));
    (int, int, int) Rgb(string h) => (Convert.ToInt32(h.Substring(1, 2), 16), Convert.ToInt32(h.Substring(3, 2), 16), Convert.ToInt32(h.Substring(5, 2), 16));
    var inkBg = Rgb(ThemePalette.Ink["AntBg"]);
    var grayBg = Rgb(ThemePalette.Gray["AntBg"]);
    var nightBg = Rgb(ThemePalette.Night["AntBg"]);
    // 亮度阶梯定律：水墨浅色为主 / 天青釉底浅调（与水墨同为浅色象，带内次序水墨>天青）/ 墨夜以黑为主，三带 L 单调递减
    double RelL(string h)
    {
        var (r, g, b) = Rgb(h);
        double F(int c) { var v = c / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * F(r) + 0.7152 * F(g) + 0.0722 * F(b);
    }
    double LInk = RelL(ThemePalette.Ink["AntBg"]), LGray = RelL(ThemePalette.Gray["AntBg"]), LNight = RelL(ThemePalette.Night["AntBg"]);
    Check("亮度阶梯：三带单调递减", LInk > LGray && LGray > LNight && LInk > 0.8 && LGray > 0.8 && LGray < LInk && LNight < 0.05,
        $"L={LInk:F2}/{LGray:F2}/{LNight:F2}");
    Check("墨夜铁律：以黑为主且暖序", nightBg.Item1 == 0x13 && nightBg.Item2 == 0x12 && nightBg.Item3 == 0x11);
    Check("墨夜母本锚点：宣纸白与黛青",
        ThemePalette.Night["AntText"] == "#E8E3D5" && ThemePalette.Night["AntTextSub"] == "#ABA595"
        && ThemePalette.Night["AntPrimary"] == "#6FA3BC" && ThemePalette.Night["AntCard"] == "#211E1B");
    Check("天青铁律：釉底浅调 G>B>R 且卡片面亮于底",
        grayBg.Item2 > grayBg.Item3 && grayBg.Item3 > grayBg.Item1
        && ThemePalette.Gray["AntBg"] == "#EDF2EF" && ThemePalette.Gray["AntCard"] == "#FAFCFA"
        && RelL(ThemePalette.Gray["AntCard"]) > LGray);
    Check("天青锚点值：主字与青瓷点缀",
        ThemePalette.Gray["AntText"] == "#2B3A36" && ThemePalette.Gray["AntPrimary"] == "#34655C");
    Check("水墨基调未漂移", ThemePalette.Ink["AntBg"] == "#F7F5EE" && ThemePalette.Ink["AntPrimary"] == "#52708A");
    // WCAG 体检（视觉观测验收定律第一层）：正文 4.5 / 次要 3.5 / 点缀 3.0 / 描边 2.0 / 按钮字 4.0
    double Contrast(string a, string b)
    {
        double la = RelL(a), lb = RelL(b);
        var hi = Math.Max(la, lb); var lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }
    foreach (var (tname, pal) in new[] { ("水墨", ThemePalette.Ink), ("天青", ThemePalette.Gray), ("墨夜", ThemePalette.Night) })
    {
        var bg = pal["AntBg"];
        var cText = Contrast(pal["AntText"], bg);
        var cSub = Contrast(pal["AntTextSub"], bg);
        var cPri = Contrast(pal["AntPrimary"], bg);
        var cBor = Contrast(pal["AntBorder"], bg);
        Check(tname + " WCAG：正文/次要/点缀/描边",
            cText >= 4.5 && cSub >= 3.5 && cPri >= 3.0 && cBor >= 2.0,
            tname + $" 正文 {cText:F2} 次要 {cSub:F2} 点缀 {cPri:F2} 描边 {cBor:F2}");
        // 按钮字：主色底上的文字（水墨/天青白字，墨夜近黑字）
        var btnText = tname == "墨夜" ? "#131211" : "#FFFFFF";
        var cBtn = Contrast(btnText, pal["AntPrimary"]);
        Check(tname + " WCAG：按钮字", cBtn >= 4.0, tname + $" 按钮字 {cBtn:F2}");
    }
    Check("未知主题回落水墨", ThemePalette.Get("nope") == ThemePalette.Ink && ThemePalette.Normalize("nope") == "ink" && ThemePalette.Normalize(null) == "ink");
    Check("ant 退役映射天青", ThemePalette.Get("ant") == ThemePalette.Gray && ThemePalette.Normalize("ANT") == "gray" && ThemePalette.Normalize("ant") == "gray");
    Check("三象大小写归一", ThemePalette.Normalize("Night") == "night" && ThemePalette.Normalize(" GRAY ") == "gray" && ThemePalette.Normalize("INK") == "ink");
}

Layer("集成");
{
    // CSV 全链路：导出 → 解析 → 生成 INSERT，行列数与关键值一致
    var r = new QueryResult { Columns = { "id", "name", "note" } };
    r.Rows.Add(new object?[] { 1, "张三", "有,\"逗号\"\n第二行" });
    r.Rows.Add(new object?[] { 2, DBNull.Value, 3.5 });
    var csv = Exporter.Csv(r);
    var (heads, rows) = CsvImporter.Parse(csv);
    Check("CSV 导出→解析往返", heads.SequenceEqual(r.Columns) && rows.Count == 2, string.Join(",", heads));
    Check("CSV 往返值一致", rows[0][1] == "张三" && rows[1][1] == null && rows[1][2] == "3.5", string.Join("|", rows[1]));
    var cols = new List<ColumnSpec> { new() { Name = "id", DataType = "int" }, new() { Name = "name", DataType = "varchar(50)" }, new() { Name = "note", DataType = "text" } };
    var ins = CsvImporter.BuildInserts(new TestProv(), "shop", "t", cols, heads, rows);
    Check("CSV→INSERT 全链路", ins.Count >= 1 && ins.Sum(s => s.Split("VALUES (").Length - 1) == 2 && ins[0].Contains("INSERT INTO `t`") && ins[0].Contains("'张三'"), ins.FirstOrDefault() ?? "");
}
{
    // JSON 全链路：解析（含畸形行跳过）→ 生成 INSERT
    var (h, rr) = JsonImporter.Parse("{\n{\"bad\";\n} not json line\n{\"a\":1,\"b\":null}");
    Check("JSONL 畸形行跳过", rr.Count == 1 && rr[0][0] == "1", string.Join(",", h));
    var (h2, r2) = JsonImporter.Parse("[{\"id\":1,\"extra\":{\"x\":2}}]");
    var jc = new List<ColumnSpec> { new() { Name = "id", DataType = "int" }, new() { Name = "extra", DataType = "text" } };
    var ji = JsonImporter.BuildInserts(new TestProv(), "db", "t", jc, h2, r2);
    Check("JSON→INSERT 嵌套序列化", ji.Count == 1 && ji[0].Contains("\"x\":2"), ji.FirstOrDefault() ?? "");
}
{
    // ER 模型 JSON 往返 + 报表 HTML 转义
    var m = new ErdModel { Tables = { new ErdTable { Name = "users", Columns = { new() { Name = "id", Type = "int", IsKey = true }, new() { Name = "user_id", Type = "int" } } } } };
    ErdModel.AutoLayout(m);
    var rt = ErdModel.FromJson(m.ToJson());
    Check("ER 模型 JSON 往返", rt != null && rt.Tables.Count == m.Tables.Count && rt.Tables[0].Name == "users", rt?.Tables.Count.ToString() ?? "null");
    var rep = ReportBuilder.ToHtml(new QueryResult { Columns = { "c" }, Rows = { new object?[] { "<script>alert(1)</script>" } } }, "安全");
    Check("报表 HTML 转义", rep.Contains("&lt;script&gt;") && !rep.Contains("<script>alert"), "");
}
{
    // 传输三段组合：建表 → 补列 → 分批插入 语句顺序完整
    var cols = new List<ColumnSpec> { new() { Name = "id", DataType = "int", IsKey = true } };
    var create = DataSync.CreateForTarget("t", cols, DbEngine.MySQL, n => "`" + n + "`");
    var (sync, _) = DataSync.SyncColumns("t", cols, new List<string> { "id" }, DbEngine.MySQL, n => "`" + n + "`", alignTypes: false);
    var qr = new QueryResult { Title = "t" }; qr.Columns.Add("id"); qr.Rows.Add(new object[] { 7 });
    var ins = DataSync.InsertBatches(qr, "`t`", n => "`" + n + "`");
    Check("传输语句组合完整", create.Contains("CREATE TABLE") && sync.Count == 0 && ins.Count == 1 && ins[0].Contains("VALUES (7)"), create);
}

{
    // 流式底座真库链路：SQLite 起临时表 → StreamingQuery 分页取数 → QueryRouter 整包同形返回 → TaskCenter 队列调度
    var dbPath = Path.Combine(Path.GetTempPath(), "taodbm-stream-" + Guid.NewGuid().ToString("N") + ".db");
    var profile = new ConnectionProfile { Engine = DbEngine.SQLite, DisplayName = "stream-smoke", Database = dbPath };
    await using (Microsoft.Data.Sqlite.SqliteConnection setup = new($"Data Source={dbPath}"))
    {
        setup.Open();
        using var cmd = setup.CreateCommand();
        cmd.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);";
        cmd.ExecuteNonQuery();
        using var tx = setup.BeginTransaction();
        for (int i = 1; i <= 500; i++)
        {
            using var one = setup.CreateCommand();
            one.Transaction = tx;
            one.CommandText = $"INSERT INTO t(v) VALUES ('v{i}')";
            one.ExecuteNonQuery();
        }
        tx.Commit();
    }
    var prov = (TaoDBM.Core.RelationalProvider)ProviderFactory.Create(profile);
    // 1) StreamingQuery 分页：500 行按 200/页 = 3 页（200+200+100）
    {
        await using var conn = prov.OpenStream(null);
        await using var sq = await TaoDBM.Core.Streaming.StreamingQuery.StartAsync(conn, "SELECT id, v FROM t ORDER BY id", CancellationToken.None);
        Check("流式列名", sq.Columns.SequenceEqual(new[] { "id", "v" }), string.Join(",", sq.Columns));
        int total = 0, pages = 0;
        while (true)
        {
            var page = await sq.NextPageAsync(200, CancellationToken.None);
            total += page.Rows.Length; pages++;
            foreach (var row in page.Rows) Check("流式行内值", row.Length == 2, "列数不对");
            if (!page.HasMore) break;
        }
        Check("流式分页总数", total == 500, total.ToString());
        Check("流式分页页数", pages == 3, pages.ToString());
    }
    // 2) QueryRouter 整包同形：与旧 Execute 返回同形 QueryResult，无 5000 截断
    {
        var rs = await TaoDBM.Core.Streaming.QueryRouter.ExecuteAsync(prov, null, "SELECT COUNT(*) AS n FROM t", CancellationToken.None);
        Check("路由整包行数", rs.Count == 1 && rs[0].Rows.Count == 1, rs[0].Rows.Count.ToString());
        Check("路由聚合值", Convert.ToInt64(rs[0].Rows[0][0]) == 500, rs[0].Rows[0][0]?.ToString() ?? "null");
        Check("路由列名", rs[0].Columns[0] == "n", rs[0].Columns[0]);
    }
    // 3) StreamingSwitch 开关读环境变量（off 关流式）
    Check("流式开关默认开", TaoDBM.Core.Streaming.StreamingSwitch.Enabled, "");
    // 4) TaskCenter：提交→完成快照、取消
    {
        var tc = new TaoDBM.Core.Streaming.TaskCenter();
        var ran = new TaskCompletionSource();
        // 「占位」任务挂起直至被取消：原先同步完成，消费循环可能在 Cancel 前跑完，断言成竞态
        tc.Runner = h => { ran.TrySetResult(); return h.Spec.Title == "占位" ? Task.Delay(Timeout.Infinite, h.Token) : Task.CompletedTask; };
        var h1 = tc.Submit(new TaoDBM.Core.Streaming.TaskSpec { Kind = "query", Title = "冒烟查询" });
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var t0 = DateTime.UtcNow;
        while (!tc.List().Any(s => s.TaskId == h1.TaskId && s.State == "done") && DateTime.UtcNow - t0 < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        Check("任务中心执行完成", tc.List().Any(s => s.TaskId == h1.TaskId && s.State == "done"), string.Join("|", tc.List().Select(s => s.State)));
        Check("任务取消", tc.Cancel(h1.TaskId) == false, "done 后不可再取消");
        var h2 = tc.Submit(new TaoDBM.Core.Streaming.TaskSpec { Title = "占位" });
        Check("任务取消进行中", tc.Cancel(h2.TaskId), "");
    }
    // Windows 差异：Microsoft.Data.Sqlite 连接池在 Dispose 后仍持有句柄，直接删库在 Windows 抛
    // IOException（Linux 允许删除已打开文件掩盖此问题）；先清池再删，删除失败不吞也不影响判定
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    for (int attempt = 0; attempt < 5 && System.IO.File.Exists(dbPath); attempt++)
    {
        try { System.IO.File.Delete(dbPath); }
        catch (IOException) { await Task.Delay(100); }
        catch (UnauthorizedAccessException) { await Task.Delay(100); }
    }
    Environment.SetEnvironmentVariable("DBM_STREAMING", null);
}

// 5) TaskCenter A2 实体化：暂停/续跑、重跑、快照导出/回放
{
    var tc = new TaoDBM.Core.Streaming.TaskCenter();
    var ranCount = 0;
    tc.Runner = _ => { Interlocked.Increment(ref ranCount); return Task.CompletedTask; };

    Check("暂停首次生效", tc.Pause());
    Check("重复暂停返回false", !tc.Pause());
    var hp = tc.Submit(new TaoDBM.Core.Streaming.TaskSpec { Kind = "export", Title = "暂停期任务", Db = "db1", Sql = "SELECT 1;" });
    await Task.Delay(200);
    Check("暂停期任务不开跑且停在运行态", ranCount == 0 && tc.List().First(s => s.TaskId == hp.TaskId).State == "running", $"ran={ranCount}");
    Check("暂停期运行态任务不可重跑", tc.Retry(hp.TaskId) == null, "running 态不允许 Retry");

    tc.Resume();
    var t0 = DateTime.UtcNow;
    while (ranCount < 1 && DateTime.UtcNow - t0 < TimeSpan.FromSeconds(5)) await Task.Delay(20);
    var t1 = DateTime.UtcNow;
    while (!tc.List().Any(s => s.TaskId == hp.TaskId && s.State == "done") && DateTime.UtcNow - t1 < TimeSpan.FromSeconds(5)) await Task.Delay(20);
    Check("续跑后积压任务开跑至完成", ranCount == 1 && tc.List().First(s => s.TaskId == hp.TaskId).State == "done", $"ran={ranCount}");

    var hq = tc.Retry(hp.TaskId);
    Check("终态任务重跑返回新句柄", hq != null && hq.TaskId != hp.TaskId);
    Check("重跑沿用原规格", hq != null && hq.Spec.Title == "暂停期任务" && hq.Spec.Kind == "export" && hq.Spec.Sql == "SELECT 1;");
    Check("未知任务重跑返回null", tc.Retry("no-such-id") == null);
    var t2 = DateTime.UtcNow;
    while (ranCount < 2 && DateTime.UtcNow - t2 < TimeSpan.FromSeconds(5)) await Task.Delay(20);
    Check("重跑任务执行完成", ranCount == 2, $"ran={ranCount}");

    // 快照导出/回放：新实例并入历史、重复回放幂等、坏快照静默忽略
    var json = tc.ExportStates();
    var tc2 = new TaoDBM.Core.Streaming.TaskCenter();
    tc2.ImportStates(json);
    Check("快照回放后历史可见", tc2.List().Count == tc.List().Count && tc2.List().Any(s => s.TaskId == hp.TaskId && s.State == "done"), $"tc2={tc2.List().Count} tc={tc.List().Count}");
    tc2.ImportStates(json);
    Check("重复回放不重复计数", tc2.List().Count == tc.List().Count);
    tc2.ImportStates("{坏");
    Check("损坏快照静默忽略", tc2.List().Count == tc.List().Count);
}

// 6) SchemaDiff 结构差异对比 + 同步计划（对象同步批纯引擎）
{
    SchemaTableSnapshot T(string table, params TableColumnDesign[] cols) => new()
    { Table = table, Columns = cols.ToList() };
    TableColumnDesign C(string name, string type, bool nullable = true, bool pk = false, string def = "") => new()
    { Name = name, SqlType = type, Nullable = nullable, IsPrimaryKey = pk, DefaultValue = def };

    var src = new List<SchemaTableSnapshot>
    {
        T("users", C("id", "BIGINT", false, true), C("name", "VARCHAR(50)"), C("age", "INT")),
        T("orders", C("id", "BIGINT", false, true), C("memo", "TEXT"), C("flag", "INT", true, false, "0")),
        T("logs", C("id", "BIGINT", false, true)),
    };
    var dst = new List<SchemaTableSnapshot>
    {
        T("users", C("id", "BIGINT", false, true), C("name", "VARCHAR(100)"), C("extra", "CHAR(1)")),
        T("orders", C("id", "BIGINT", false, true), C("memo", "TEXT"), C("flag", "INT", true, false, "1")),
        T("archive", C("id", "BIGINT")),
    };

    var diffs = SchemaDiff.Diff(src, dst);

    // 缺表 / 多余表
    var logs = diffs.First(d => d.Table == "logs" && d.Kind == "table-missing");
    Check("缺表检出且可执行", logs.Actionable, logs.Detail);
    Check("多余表仅提示", diffs.Any(d => d.Table == "archive" && d.Kind == "table-extra" && !d.Actionable));

    // 列级差异
    var tmiss = diffs.FirstOrDefault(d => d.Table == "users" && d.Kind == "column-missing");
    Check("缺列检出", tmiss != null && tmiss.Column == "age" && tmiss.Actionable);
    var textra = diffs.FirstOrDefault(d => d.Table == "users" && d.Kind == "column-extra");
    Check("多余列仅提示不可执行", textra != null && textra.Column == "extra" && !textra.Actionable);
    var tmis = diffs.FirstOrDefault(d => d.Table == "users" && d.Kind == "type-mismatch");
    Check("类型不一致检出", tmis != null && tmis.Column == "name" && tmis.Source == "VARCHAR(50)" && tmis.Target == "VARCHAR(100)");

    // 默认值比较默认关闭、开启后检出
    Check("默认值差异默认不报", !diffs.Any(d => d.Table == "orders" && d.Kind == "default-mismatch"));
    var d1 = SchemaDiff.Diff(src, dst, new SchemaDiffOptions { CompareDefault = true, ReportExtraColumns = false, ReportExtraTables = false });
    Check("开启默认值比较后检出", d1.Any(d => d.Table == "orders" && d.Kind == "default-mismatch" && d.Column == "flag"));
    Check("关闭多余报告后无提示项", !d1.Any(d => d.Kind is "column-extra" or "table-extra"));

    // 主键 / 可空性差异
    var srcPk = new List<SchemaTableSnapshot> { T("t1", C("id", "INT", true, true)) };
    var dstPk = new List<SchemaTableSnapshot> { T("t1", C("id", "INT", true, false)) };
    var pkDiffs = SchemaDiff.Diff(srcPk, dstPk);
    Check("主键差异检出", pkDiffs.Any(d => d.Kind == "pk-mismatch" && d.Column == "id"));
    var srcN = new List<SchemaTableSnapshot> { T("t2", C("a", "INT", true)) };
    var dstN = new List<SchemaTableSnapshot> { T("t2", C("a", "INT", false)) };
    Check("可空性差异检出", SchemaDiff.Diff(srcN, dstN).Any(d => d.Kind == "nullable-mismatch"));

    // 完全一致零差异；表名/列名大小写不敏感
    var same = SchemaDiff.Diff(src, new List<SchemaTableSnapshot>(src));
    Check("同构零差异", same.Count == 0, string.Join("|", same.Select(d => d.Table + ":" + d.Kind)));
    var caseDst = new List<SchemaTableSnapshot> { T("USERS", C("ID", "bigint", false, true), C("NAME", "varchar(50)"), C("AGE", "int")) };
    var caseUsers = SchemaDiff.Diff(src.Where(t => t.Table == "users").ToList(), caseDst);
    Check("大小写不敏感比对", caseUsers.Count == 0, string.Join("|", caseUsers.Select(d => d.Kind + ":" + d.Column)));

    // 同步计划：缺表 → CREATE；缺列+类型对齐 → ALTER；多余列不删（dropMissing=false）
    var my = (Func<string, string>)(s => "`" + s + "`");
    var (stmts, skips) = SchemaDiff.PlanSync(src, dst, diffs, DbEngine.MySQL, my);
    Check("缺表生成建表", stmts.Any(s => s.Contains("CREATE TABLE `logs`")), string.Join(" ;; ", stmts));
    Check("缺列生成加列", stmts.Contains("ALTER TABLE `users` ADD `age` INT"));
    Check("类型对齐生成改列", stmts.Contains("ALTER TABLE `users` MODIFY COLUMN `name` VARCHAR(50)"));
    Check("多余列不进计划", !stmts.Any(s => s.Contains("DROP COLUMN")));
    Check("多余表不进计划", !stmts.Any(s => s.Contains("`archive`")));
    Check("提示类差异不落语句", skips.Count == 0, string.Join("|", skips));

    // dropMissing=true 开删侧；SQLite 不支持改列进 skipped
    var (dropStmts, _) = SchemaDiff.PlanSync(src, dst, diffs, DbEngine.MySQL, my, dropMissing: true);
    Check("开删侧后多余列进计划", dropStmts.Contains("ALTER TABLE `users` DROP COLUMN `extra`"));
    var lite = new List<SchemaTableSnapshot> { T("u", C("a", "INT")) };
    var liteDst = new List<SchemaTableSnapshot> { T("u", C("a", "BIGINT")) };
    var liteDiffs = SchemaDiff.Diff(lite, liteDst);
    var (_, liteSkips) = SchemaDiff.PlanSync(lite, liteDst, liteDiffs, DbEngine.SQLite, s => s);
    Check("SQLite 改列进跳过清单", liteSkips.Count == 1 && liteSkips[0].Contains("SQLite"), string.Join("|", liteSkips));
}

// 7) SchemaTimeline 表结构变更时间线：追加切面、任意两切面对比/回放、序列化容错
{
    SchemaTableSnapshot T(string table, params TableColumnDesign[] cols) => new()
    { Table = table, Columns = cols.ToList() };
    TableColumnDesign C(string name, string type, bool nullable = true, bool pk = false, string def = "") => new()
    { Name = name, SqlType = type, Nullable = nullable, IsPrimaryKey = pk, DefaultValue = def };

    var t0 = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.FromHours(8));
    var line = SchemaTimeline.Append([], new[] { T("users", C("id", "BIGINT", false, true)) }, "基线", t0);
    var afterAdd = SchemaTimeline.Append(line, new[] { T("users", C("id", "BIGINT", false, true), C("age", "INT")) }, "加列", t0.AddMinutes(30));
    var afterMore = SchemaTimeline.Append(afterAdd, new[] {
        T("users", C("id", "BIGINT", false, true), C("age", "INT")),
        T("logs", C("id", "BIGINT", false, true)),
    }, "补日志表", t0.AddMinutes(60));

    Check("切面逐次追加且不改旧表", line.Count == 1 && afterAdd.Count == 2 && afterMore.Count == 3);
    Check("切面携带时间与说明", afterAdd[1].Label == "加列" && afterAdd[1].At > afterAdd[0].At);
    Check("追加不污染旧切面", afterAdd[0].Tables[0].Columns.Count == 1 && line[0].Tables[0].Columns.Count == 1);

    // 对比与回放：基线 → 补日志表
    var diffs = SchemaTimeline.DiffBetween(afterMore[0], afterMore[2]);
    Check("跨切面差异检出", diffs.Any(d => d.Table == "users" && d.Kind == "column-missing" && d.Column == "age")
        && diffs.Any(d => d.Table == "logs" && d.Kind == "table-missing"));
    var my = (Func<string, string>)(s => "`" + s + "`");
    var (stmts, _) = SchemaTimeline.PlanBetween(afterMore[0], afterMore[2], DbEngine.MySQL, my);
    Check("跨切面回放生成计划", stmts.Any(s => s.Contains("CREATE TABLE `logs`")) && stmts.Contains("ALTER TABLE `users` ADD `age` INT"),
        string.Join(" ;; ", stmts));

    // 序列化往返与容错
    var json = SchemaTimeline.ToJson(afterMore);
    var back = SchemaTimeline.Parse(json);
    Check("时间线往返还原", back.Count == 3 && back[1].Label == "加列" && back[1].Tables[0].Columns.Count == 2);
    Check("回读按时间升序", back.SequenceEqual(back.OrderBy(e => e.At)));
    Check("损坏时间线回空", SchemaTimeline.Parse("{坏").Count == 0 && SchemaTimeline.Parse("").Count == 0 && SchemaTimeline.Parse(null).Count == 0);
}

// 8) RiskySqlGuard 危险 SQL 拦截引擎（安全线：无 WHERE 预估影响行数）
{
    var id = (Func<string, string>)(s => s);   // 通用引擎：标识符不加引号，便于断言

    Check("无WHERE删除判危险", RiskySqlGuard.Inspect("delete from users", DbEngine.MySQL, id) is { Level: "danger", Kind: "no-where", EstimateSql: "SELECT COUNT(*) FROM users" } c1
        ? c1.Table == "users" : false);
    Check("无WHERE更新判危险并给预估", RiskySqlGuard.Inspect("UPDATE users SET age = 0", DbEngine.MySQL, id).EstimateSql == "SELECT COUNT(*) FROM users");
    Check("带WHERE改数放行", RiskySqlGuard.Inspect("delete from users where id = 1", DbEngine.MySQL, id).Level == "ok");
    Check("WHERE在字符串里不算条件", RiskySqlGuard.Inspect("update t set a = 'where x = 1'", DbEngine.MySQL, id).Kind == "no-where",
        RiskySqlGuard.Inspect("update t set a = 'where x = 1'", DbEngine.MySQL, id).Kind);
    Check("WHERE在注释里不算条件", RiskySqlGuard.Inspect("delete from t -- where id = 1\n", DbEngine.MySQL, id).Kind == "no-where");
    var esc = RiskySqlGuard.Inspect("update t set a = 'it''s ok' where id = 2", DbEngine.MySQL, id);
    Check("转义引号字面量放行", esc.Level == "ok", esc.Skeleton);
    Check("DROP判危险", RiskySqlGuard.Inspect("DROP TABLE IF EXISTS logs", DbEngine.MySQL, id) is { Level: "danger", Kind: "drop-table" } c2
        ? c2.Table == "logs" : false);
    Check("TRUNCATE判危险", RiskySqlGuard.Inspect("truncate table logs", DbEngine.MySQL, id).Kind == "truncate");
    Check("删列判危险", RiskySqlGuard.Inspect("alter table users drop column age", DbEngine.MySQL, id).Kind == "drop-column");
    Check("SELECT不拦", RiskySqlGuard.Inspect("SELECT * FROM users", DbEngine.MySQL, id).Level == "ok");

    // 带条件的预估 SQL 与方言引用符
    var est = RiskySqlGuard.EstimateRowsSql("delete from `my db`.users where age > 18", DbEngine.MySQL, s => $"`{s}`");
    Check("带条件预估走WHERE", est == "SELECT COUNT(*) FROM `users` WHERE age > 18", est ?? "(null)");
    Check("SELECT不给预估", RiskySqlGuard.EstimateRowsSql("select 1", DbEngine.MySQL, id) == null);

    // 脚本级：逐条 + 最危险档
    var script = RiskySqlGuard.InspectScript("select 1;\nupdate t set a = 1;\n", DbEngine.MySQL, id);
    Check("脚本拆分逐条检查", script.Count == 2 && script[1].Kind == "no-where");
    Check("脚本最危险档上浮", RiskySqlGuard.Worst(script).Kind == "no-where" && RiskySqlGuard.Worst(script).Level == "danger");
    Check("全ok脚本 worst 为 ok", RiskySqlGuard.Worst(RiskySqlGuard.InspectScript("select 1;", DbEngine.MySQL, id)).Level == "ok");
}

// 9) MaskEngine 一键脱敏引擎（安全线：一键脱敏导出）
{
    Check("手机号列推断", MaskEngine.InferRule("phone") == MaskRule.Phone && MaskEngine.InferRule("手机号") == MaskRule.Phone);
    Check("证件与卡号列推断", MaskEngine.InferRule("id_card") == MaskRule.IdCard && MaskEngine.InferRule("银行卡号") == MaskRule.BankCard);
    Check("密码列整值档", MaskEngine.InferRule("password") == MaskRule.Secret && MaskEngine.InferRule("密码") == MaskRule.Secret);
    Check("普通列不脱敏", MaskEngine.InferRule("order_total") == MaskRule.None && MaskEngine.InferRule("created_at") == MaskRule.None);
    Check("结构名列不误伤", MaskEngine.InferRule("table_name") == MaskRule.None && MaskEngine.InferRule("column_name") == MaskRule.None);
    Check("姓名列推断", MaskEngine.InferRule("联系人") == MaskRule.Name && MaskEngine.InferRule("user_name") == MaskRule.Name);
    Check("手机号打码", MaskEngine.Mask("13812345678", MaskRule.Phone) == "138****5678");
    Check("邮箱打码保域名", MaskEngine.Mask("zhangsan@163.com", MaskRule.Email) == "z***@163.com");
    Check("身份证打码", MaskEngine.Mask("110101199001011234", MaskRule.IdCard) == "110***********1234");
    Check("姓名打码", MaskEngine.Mask("张小明明", MaskRule.Name) == "张***");
    Check("银行卡打码", MaskEngine.Mask("6222021234561234567", MaskRule.BankCard) == new string('*', 15) + "4567");
    Check("密码整值打码", MaskEngine.Mask("p@ssw0rd", MaskRule.Secret) == "********");
    Check("空值原样", MaskEngine.Mask(null, MaskRule.Phone) == "" && MaskEngine.Mask("", MaskRule.Email) == "");
    Check("短值整段打码不越界", MaskEngine.Mask("123", MaskRule.Phone) == "***" && MaskEngine.Mask("12345", MaskRule.IdCard) == "*****");
    var row = MaskEngine.MaskRow(new List<KeyValuePair<string, string>>
    {
        new("phone", "13812345678"), new("total", "99"), new("密码", "abc123"),
    });
    Check("行级脱敏混合列", row[0].Value == "138****5678" && row[1].Value == "99" && row[2].Value == "******");
    Check("自定义覆盖规则", MaskEngine.InferRule("member_id",
        new Dictionary<string, MaskRule> { ["member_id"] = MaskRule.Phone }) == MaskRule.Phone);
}

// 10) ChangeAudit 改数留痕审计 + 单条回滚（安全线）
{
    var before = new Dictionary<string, string?> { ["id"] = "7", ["age"] = "25", ["city"] = "北京", ["note"] = "it's ok", ["tag"] = null };
    var after = new Dictionary<string, string?> { ["id"] = "7", ["age"] = "30", ["city"] = "北京", ["note"] = null, ["tag"] = "vip" };
    var entry = ChangeAudit.CaptureRow("users", "id", "7", before, after, "update users set age = 30 where id = 7");
    Check("留痕只记改动列", entry.HasChanges && entry.Changes.Count == 3 &&
        entry.Changes[0].Column == "age" && entry.Changes[1].Column == "note" && entry.Changes[2].Column == "tag",
        string.Join(",", entry.Changes.Select(c => c.Column)));
    Check("留痕带时间与语句", entry.TimestampUtc.Length > 0 && entry.Statement.Contains("users", StringComparison.Ordinal));
    Check("单条回滚语句含NULL旧值", ChangeAudit.UndoSql(entry) ==
        "UPDATE `users` SET `age` = '25', `note` = 'it''s ok', `tag` = NULL WHERE `id` = '7'", ChangeAudit.UndoSql(entry));
    Check("回滚转义单引号", ChangeAudit.UndoSql(ChangeAudit.CaptureRow("t", "id", "1",
        new Dictionary<string, string?> { ["v"] = "it's ok" }, new Dictionary<string, string?> { ["v"] = "x" })) ==
        "UPDATE `t` SET `v` = 'it''s ok' WHERE `id` = '1'");

    var noChange = ChangeAudit.CaptureRow("users", "id", "7", before, new Dictionary<string, string?>(before));
    Check("无差异不落账", !noChange.HasChanges && ChangeAudit.UndoSql(noChange) == "");

    var e1 = ChangeAudit.CaptureRow("users", "id", "1", new Dictionary<string, string?> { ["a"] = "1" }, new Dictionary<string, string?> { ["a"] = "2" });
    var e2 = ChangeAudit.CaptureRow("users", "id", "2", new Dictionary<string, string?> { ["a"] = "1" }, new Dictionary<string, string?> { ["a"] = "3" });
    var script = ChangeAudit.UndoScript(new List<AuditEntry> { e1, e2 });
    Check("回滚脚本倒序分号换行", script == "UPDATE `users` SET `a` = '1' WHERE `id` = '2';\nUPDATE `users` SET `a` = '1' WHERE `id` = '1';\n", script.Replace("\n", "\\n", StringComparison.Ordinal));

    var dir = Path.Combine(Path.GetTempPath(), "taodbm-audit-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var journal = Path.Combine(dir, "audit.jsonl");
    ChangeAudit.SaveJsonl(journal, new List<AuditEntry> { entry, e1 });
    var back = ChangeAudit.LoadJsonl(journal);
    Check("留痕账 JSONL 往返", back.Count == 2 && back[0].Table == "users" &&
        back[0].Changes.Count == 3 && back[0].Changes[1].OldValue == "it's ok" && back[1].PkValue == "1");
    File.WriteAllText(journal, "{broken\n" + File.ReadAllText(journal));
    Check("损坏行容错跳过", ChangeAudit.LoadJsonl(journal).Count == 2);
    Check("缺账文件按空账", ChangeAudit.LoadJsonl(Path.Combine(dir, "none.jsonl")).Count == 0);
}

// 11) SqlReleaseCheck SQL 上线检查评分（安全线）
{
    var (okScore, okFindings) = SqlReleaseCheck.Review("select id, name from users where id = 1;", DbEngine.MySQL);
    Check("干净脚本满分可上线", okScore == 100 && okFindings.Count == 0 && SqlReleaseCheck.Verdict(okScore) == "可上线");

    var (badScore, bad) = SqlReleaseCheck.Review("delete from users;\ndrop table logs;", DbEngine.MySQL);
    Check("无WHERE与DROP均拦截", bad.Count(f => f.Level == "block") == 2 && badScore == 50 && SqlReleaseCheck.Verdict(badScore) == "建议整改");

    var (s1, f1) = SqlReleaseCheck.Review("select * from orders;", DbEngine.MySQL);
    Check("SELECT星告警", s1 == 90 && f1.Any(x => x.Rule == "select-star" && x.Level == "warn") && SqlReleaseCheck.Verdict(s1) == "可上线");

    var (s2, f2) = SqlReleaseCheck.Review("select * from users where name like '%张';", DbEngine.MySQL);
    Check("前导通配与星合计", f2.Count == 2 && f2.All(x => x.Level == "warn") && s2 == 80);

    var (s3, f3) = SqlReleaseCheck.Review("update t set a = 1 where id = 2;", DbEngine.MySQL);
    Check("改数无LIMIT无事务提示", f3.Any(x => x.Rule == "no-limit") && f3.Any(x => x.Rule == "no-transaction") && s3 == 94);

    var (s4, f4) = SqlReleaseCheck.Review("begin;\nupdate t set a = 1 where id = 2;\n", DbEngine.MySQL);
    Check("包事务后不提示", !f4.Any(x => x.Rule == "no-transaction") && s4 == 97);

    var (s5, f5) = SqlReleaseCheck.Review("update t set a = 'where x = 1';", DbEngine.MySQL);
    Check("字符串含WHERE不算条件仍判无WHERE危险", f5.Any(x => x.Rule == "dangerous-statement") && s5 == 75);

    var (s6, _) = SqlReleaseCheck.Review("xxx;\ntruncate table huge;", DbEngine.MySQL);
    Check("TRUNCATE拦截扣25分", s6 == 75 && SqlReleaseCheck.Verdict(s6) == "谨慎上线");
    var (s7, _) = SqlReleaseCheck.Review("truncate table a; truncate table b; truncate table c; truncate table d;", DbEngine.MySQL);
    Check("分数下限不为负", s7 == 0 && SqlReleaseCheck.Verdict(0) == "建议整改");
}

// 12) BatchCleanup 大表分批清理（安全线）
{
    var plan = BatchCleanup.Plan("logs", "created_at < '2026-01-01'", "id", 2500, 1000, DbEngine.MySQL);
    Check("计划按估计行数分批", plan.TotalBatches == 3 && plan.BatchSqls.Count == 3 && !plan.WholeTable);
    Check("MySQL批次SQL带LIMIT", plan.BatchSqls[0] == "DELETE FROM `logs` WHERE created_at < '2026-01-01' LIMIT 1000", plan.BatchSqls[0]);

    var pg = BatchCleanup.Plan("logs", "created_at < '2026-01-01'", "id", 2500, 1000, DbEngine.PostgreSQL);
    Check("PG批次SQL主键子查询", pg.BatchSqls[0] == "DELETE FROM \"logs\" WHERE \"id\" IN (SELECT \"id\" FROM \"logs\" WHERE created_at < '2026-01-01' ORDER BY \"id\" LIMIT 1000)", pg.BatchSqls[0]);

    var whole = BatchCleanup.Plan("logs", null, "id", 500, 200, DbEngine.MySQL);
    Check("整表清空计划带警示", whole.WholeTable && whole.TotalBatches == 3 && whole.BatchSqls[0] == "DELETE FROM `logs` LIMIT 200");

    Check("非法参数空计划", BatchCleanup.Plan("t", "x=1", "id", 0, 100, DbEngine.MySQL).TotalBatches == 0);

    var batchHit = 0;
    var run = BatchCleanup.Run(plan, _ => ++batchHit switch { 1 => 1000, 2 => 1000, _ => 400 });
    Check("执行收敛统计行数", run.RowsDeleted == 2400 && run.BatchesDone == 3 && run.Exhausted, $"{run.RowsDeleted}/{run.BatchesDone}");

    var hit = 0;
    var early = BatchCleanup.Run(plan, _ => ++hit == 2 ? 0 : 1000);
    Check("删除0行提前收敛", early.BatchesDone == 2 && early.RowsDeleted == 1000 && !early.Exhausted);

    var capped = BatchCleanup.Run(plan, _ => 1000, maxBatches: 1);
    Check("最大批次封顶", capped.BatchesDone == 1 && capped.RowsDeleted == 1000 && !capped.Exhausted);
}

// 13) LockAnalyzer 锁与死锁现场还原 + 终止会话（安全线）
{
    var sessions = new Dictionary<string, SessionInfo>
    {
        ["101"] = new("101", "alice", "update t set a = 1", 5000),
        ["102"] = new("102", "bob", "delete from t", 200),
        ["103"] = new("103", "carol", "select * from t", 1),
    };
    var cycle = LockAnalyzer.FindDeadlocks(new List<LockWait>
    {
        new("101", "102", "t.pk=1"),
        new("102", "101", "t.pk=2"),
    });
    Check("双会话死锁环检出", cycle.Count == 1 && cycle[0].SessionIds.Contains("101") && cycle[0].SessionIds.Contains("102"));
    Check("牺牲者选扫描最少的", LockAnalyzer.PickVictim(cycle[0], sessions) == "102");

    var three = LockAnalyzer.FindDeadlocks(new List<LockWait>
    {
        new("a", "b", "r1"), new("b", "c", "r2"), new("c", "a", "r3"),
    });
    Check("三会话环检出", three.Count == 1 && three[0].SessionIds.Count == 3 && three[0].Resources.Count == 3);

    var none = LockAnalyzer.FindDeadlocks(new List<LockWait> { new("x", "y", "r1"), new("y", "z", "r2") });
    Check("无环链不误报", none.Count == 0);

    Check("MySQL终止语句", LockAnalyzer.KillSql("101", DbEngine.MySQL) == "KILL 101");
    Check("PG终止语句走后端函数", LockAnalyzer.KillSql("101", DbEngine.PostgreSQL) == "SELECT pg_terminate_backend(101);");
    Check("SQLite无终止能力返回空", LockAnalyzer.KillSql("101", DbEngine.SQLite) == null);

    var timeline = LockAnalyzer.Timeline(new List<LockWait>
    {
        new("w1", "h1", "r1"), new("w2", "h2", "r2"), new("w3", "h3", "r3"),
    }, new Dictionary<string, long> { ["w1"] = 5, ["w2"] = 90, ["w3"] = 1 });
    Check("现场按等待时长倒序", timeline[0].Waiter == "w2" && timeline[^1].Waiter == "w3");
}

// 14) DeletedRowRecovery 误删找回：快照+拼回（安全线收官项）
{
    var rows = new List<IReadOnlyDictionary<string, string?>>
    {
        new Dictionary<string, string?> { ["id"] = "2", ["name"] = "it's bob", ["tag"] = null },
        new Dictionary<string, string?> { ["id"] = "1", ["name"] = "alice" },
    };
    var snap = DeletedRowRecovery.Capture("users", "id", rows);
    Check("快照列集取并集定序", snap.Columns.SequenceEqual(new[] { "id", "name", "tag" }) && snap.RowCount == 2 && snap.CapturedUtc.Length > 0);

    var script = DeletedRowRecovery.ReinsertScript(snap, DbEngine.MySQL);
    Check("拼回按主键排序且缺列补NULL", script ==
        "INSERT INTO `users` (`id`, `name`, `tag`) VALUES ('1', 'alice', NULL), ('2', 'it''s bob', NULL);\n",
        script.Replace("\n", "\\n", StringComparison.Ordinal));

    var pgScript = DeletedRowRecovery.ReinsertScript(snap, DbEngine.PostgreSQL);
    Check("PG拼回用双引号标识符", pgScript.StartsWith("INSERT INTO \"users\" (\"id\"", StringComparison.Ordinal));

    var many = DeletedRowRecovery.Capture("t", "id", Enumerable.Range(0, 250)
        .Select(i => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["id"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
    Check("拼回分块每100行一条", DeletedRowRecovery.ReinsertScript(many, DbEngine.MySQL).Count(c => c == '\n') == 3);

    var empty = DeletedRowRecovery.Capture("t", "id", Array.Empty<IReadOnlyDictionary<string, string?>>());
    Check("空快照拼回为空脚本", DeletedRowRecovery.ReinsertScript(empty, DbEngine.MySQL) == "");

    var dir = Path.Combine(Path.GetTempPath(), "taodbm-recv-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var snapFile = Path.Combine(dir, "users.snapshot.json");
    DeletedRowRecovery.SaveJsonl(snapFile, snap);
    var back = DeletedRowRecovery.LoadJsonl(snapFile);
    Check("快照 JSONL 往返", back is not null && back.Table == "users" && back.RowCount == 2 && back.Columns.Count == 3 && back.Rows[0][1] == "it's bob");
    File.WriteAllText(snapFile, "{broken");
    Check("损坏快照按无快照降级", DeletedRowRecovery.LoadJsonl(snapFile) == null);
    Check("缺文件按无快照降级", DeletedRowRecovery.LoadJsonl(Path.Combine(dir, "none.json")) == null);
}

// 15) ReadRouter 延迟感知读路由（主从底座）
{
    var nodes = new List<ReplicaNode>
    {
        new("r2", 3, true), new("r1", 1, true), new("r3", 30, true), new("r4", 2, false),
    };
    var d = ReadRouter.Route(nodes, maxLagSeconds: 5);
    Check("路由按延迟升序过滤", d.EligibleReplicas.SequenceEqual(new[] { "r1", "r2" }) && !d.FellBackToPrimary);

    var lagged = ReadRouter.Route(nodes, maxLagSeconds: 0);
    Check("全部超阈值回落主库", lagged.FellBackToPrimary && lagged.EligibleReplicas.Count == 0 && lagged.HasTarget);

    var tie = ReadRouter.Route(new List<ReplicaNode> { new("b", 5, true), new("a", 5, true) }, 5);
    Check("同延迟按名字定序", tie.EligibleReplicas.SequenceEqual(new[] { "a", "b" }));

    var toggle = ReadRouter.AutoToggle(nodes, 5);
    Check("自动摘除超时与故障副本", toggle.First(t => t.Name == "r3").ShouldServeReads == false &&
        toggle.First(t => t.Name == "r4").ShouldServeReads == false &&
        toggle.First(t => t.Name == "r1").ShouldServeReads == true);
}

// 16) ReconcileEngine 迁移后数据对账（主从/搬迁配套）
{
    Check("行数对账SQL", ReconcileEngine.CountSql("orders", null, DbEngine.MySQL) == "SELECT COUNT(*) AS row_count FROM `orders`");
    Check("带条件行数SQL去分号", ReconcileEngine.CountSql("orders", "created_at < '2026-01-01';", DbEngine.MySQL) ==
        "SELECT COUNT(*) AS row_count FROM `orders` WHERE created_at < '2026-01-01'");

    var mysqlCk = ReconcileEngine.ChecksumSql("orders", new[] { "id", "amount" }, null, DbEngine.MySQL);
    Check("MySQL校验和CRC32", mysqlCk == "SELECT COUNT(*) AS row_count, COALESCE(SUM(CRC32(CONCAT_WS('|', `id`, `amount`))), 0) AS checksum FROM `orders`", mysqlCk ?? "");
    var pgCk = ReconcileEngine.ChecksumSql("orders", new[] { "id" }, null, DbEngine.PostgreSQL);
    Check("PG校验和md5截段", pgCk is not null && pgCk.Contains("md5(concat_ws('|', \"id\"))", StringComparison.Ordinal));
    Check("无校验和函数方言退化", ReconcileEngine.ChecksumSql("t", new[] { "a" }, null, DbEngine.SQLite) == null);
    Check("空列集不给校验和", ReconcileEngine.ChecksumSql("t", Array.Empty<string>(), null, DbEngine.MySQL) == null);

    Check("两端一致判match", ReconcileEngine.Compare(100, 100, 7, 7).Level == "match");
    Check("行数不一致判档", ReconcileEngine.Compare(100, 98, 7, 7).Level == "count-mismatch");
    Check("行数同内容漂移判档", ReconcileEngine.Compare(100, 100, 7, 9).Level == "checksum-mismatch");
    Check("单端缺校验和判档", ReconcileEngine.Compare(100, 100, 7, null).Level == "checksum-mismatch");

    Check("抽样核对SQL", ReconcileEngine.SampleSql("users", "id", 50, DbEngine.MySQL) == "SELECT * FROM `users` ORDER BY `id` LIMIT 50");
}

// 17) ShardKeyEvaluator 分片键评估与路由预演（分库分区分表底座）
{
    var ideal = new ShardKeyCandidate("user_id", 100000, 1.1, 500, Monotonic: false);
    var skewed = new ShardKeyCandidate("region", 40, 4.0, 300, Monotonic: false);
    var cold = new ShardKeyCandidate("remark", 5000, 1.0, 0, Monotonic: false);
    var monotonic = new ShardKeyCandidate("id", 1000000, 1.0, 800, Monotonic: true);

    var verdicts = ShardKeyEvaluator.Evaluate(new[] { skewed, cold, monotonic, ideal });
    Check("评估按得分降序", verdicts[0].Column == "id" && verdicts[0].Score == 100 && verdicts[^1].Column == "remark",
        string.Join(",", verdicts.Select(v => $"{v.Column}={v.Score}")));
    Check("理想键满分建议范围分片", verdicts.First(v => v.Column == "user_id").Score == 100 &&
        verdicts.First(v => v.Column == "user_id").SuggestedStrategy == "range");
    Check("倾斜键扣分并出热点提示", verdicts.First(v => v.Column == "region").Score == 70 &&
        verdicts.First(v => v.Column == "region").Issues.Any(i => i.Contains("热点")));
    Check("谓词未命中扣分提示全分片扫描", verdicts.First(v => v.Column == "remark").Score == 70 &&
        verdicts.First(v => v.Column == "remark").Issues.Any(i => i.Contains("全分片扫描")));
    Check("单调键建议哈希并提示尾部热点", verdicts.First(v => v.Column == "id").SuggestedStrategy == "hash" &&
        verdicts.First(v => v.Column == "id").Issues.Any(i => i.Contains("尾部热点")));
    Check("并列得分按列名定序", ShardKeyEvaluator.Evaluate(new[] { new ShardKeyCandidate("b", 10, 1.0, 5, false), new ShardKeyCandidate("a", 10, 1.0, 5, false) })
        .Select(v => v.Column).SequenceEqual(new[] { "a", "b" }));

    Check("哈希路由确定性", ShardKeyEvaluator.HashRoute("order-42", 8) == ShardKeyEvaluator.HashRoute("order-42", 8));
    Check("哈希路由取值在分片范围内", ShardKeyEvaluator.HashRoute("order-42", 8) is >= 0 and < 8);
    var spread = Enumerable.Range(0, 1000).Select(i => ShardKeyEvaluator.HashRoute($"k{i}", 8)).Distinct().Count();
    Check("哈希路由千键铺满八分片", spread == 8, spread.ToString());

    bool threw = false;
    try { ShardKeyEvaluator.HashRoute("x", 0); } catch (ArgumentOutOfRangeException) { threw = true; }
    Check("非法分片数抛参数异常", threw);

    var bounds = new long[] { 1000, 2000, 3000 };
    Check("范围路由落首段", ShardKeyEvaluator.RangeRoute(500, bounds) == 0);
    Check("范围路由上界含入本段", ShardKeyEvaluator.RangeRoute(2000, bounds) == 1);
    Check("范围路由超界提示扩分片", ShardKeyEvaluator.RangeRoute(3001, bounds) == -1);
}

// 18) HotspotDetector 热点倾斜检测（分库分区分表底座）
{
    var skewed = HotspotDetector.Analyze(new[] { new ShardLoad("p0", 100), new ShardLoad("p1", 10), new ShardLoad("p2", 10), new ShardLoad("p3", 10), new ShardLoad("p4", 10) });
    Check("热点分片检出", skewed.HasHotSpot && skewed.Verdicts[0].Name == "p0" && skewed.Verdicts[0].Level == "hot");
    Check("冷清分片检出", skewed.Verdicts.Count(v => v.Level == "cold") == 4);
    Check("热点建议含拆分或加盐", skewed.Suggestions.Any(s => s.Contains("p0") && s.Contains("加盐")));
    Check("峰值倍数正确", skewed.PeakRatio > 3.5 && skewed.PeakRatio < 3.6, skewed.PeakRatio.ToString("0.###"));

    var even = HotspotDetector.Analyze(new[] { new ShardLoad("a", 10), new ShardLoad("b", 10), new ShardLoad("c", 10) });
    Check("均匀分布无热点无冷清", !even.HasHotSpot && even.Verdicts.All(v => v.Level == "normal"));
    Check("均匀分布建议为无需处置", even.Suggestions.SequenceEqual(new[] { "负载分布正常，无需处置" }));

    var zeros = HotspotDetector.Analyze(new[] { new ShardLoad("x", 0), new ShardLoad("y", 0) });
    Check("全零负载不误报热点", !zeros.HasHotSpot && zeros.MeanLoad == 0 && zeros.Verdicts.All(v => v.Level == "normal"));

    var empty = HotspotDetector.Analyze(Array.Empty<ShardLoad>());
    Check("空输入安全降级", empty.Verdicts.Count == 0 && !empty.HasHotSpot && empty.Suggestions.Single().Contains("无分片负载数据"));

    var tie = HotspotDetector.Analyze(new[] { new ShardLoad("b", 5), new ShardLoad("a", 5) });
    Check("并列负载按名字定序", tie.Verdicts.Select(v => v.Name).SequenceEqual(new[] { "a", "b" }));
}

// 19) PartitionRoller 时间分区自动滚动（分库分区分表底座）
{
    var day = TimeSpan.FromDays(1);
    var noon = new DateTime(2026, 11, 15, 12, 0, 0);

    var fresh = PartitionRoller.EnsureCoverage(Array.Empty<TimePartition>(), noon, day, retainCount: 7);
    Check("空表从目标槽位起建一个分区", fresh.ToAdd.Count == 1 && fresh.ToAdd[0].Name == "p_20261115" &&
        fresh.ToAdd[0].StartInclusive == new DateTime(2026, 11, 15) && fresh.CoversHorizon);

    var existing = new[] { new TimePartition("p_20261114", new DateTime(2026, 11, 14), new DateTime(2026, 11, 15)) };
    var roll = PartitionRoller.EnsureCoverage(existing, noon, day, 7);
    Check("逐日滚动补当前槽", roll.ToAdd.Count == 1 && roll.ToAdd[0].Name == "p_20261115" && roll.CoversHorizon);
    Check("保留窗口内不归档", roll.ToArchive.Count == 0);

    var span = PartitionRoller.EnsureCoverage(new[] { new TimePartition("p_20261113", new DateTime(2026, 11, 13), new DateTime(2026, 11, 14)) }, noon, day, 7);
    Check("断档多槽按序补齐", span.ToAdd.Select(p => p.Name).SequenceEqual(new[] { "p_20261114", "p_20261115" }));

    var withOld = new[]
    {
        new TimePartition("p_20261101", new DateTime(2026, 11, 1), new DateTime(2026, 11, 2)),
        new TimePartition("p_20261114", new DateTime(2026, 11, 14), new DateTime(2026, 11, 15)),
    };
    var arch = PartitionRoller.EnsureCoverage(withOld, noon, day, 7);
    Check("超保留窗口旧分区建议归档", arch.ToArchive.SequenceEqual(new[] { "p_20261101" }));

    var covered = PartitionRoller.EnsureCoverage(new[] { new TimePartition("p_20261115", new DateTime(2026, 11, 15), new DateTime(2026, 11, 16)) }, noon, day, 7);
    Check("已覆盖时不重复建分区", covered.ToAdd.Count == 0 && covered.CoversHorizon);

    var hourGrain = TimeSpan.FromHours(1);
    var hourly = PartitionRoller.EnsureCoverage(new[] { new TimePartition("p_20261115_1100", new DateTime(2026, 11, 15, 11, 0, 0), new DateTime(2026, 11, 15, 12, 0, 0)) }, new DateTime(2026, 11, 15, 12, 30, 0), hourGrain, 24);
    Check("小时粒度命名与滚动", hourly.ToAdd.Count == 1 && hourly.ToAdd[0].Name == "p_20261115_1200");

    bool threw = false;
    try { PartitionRoller.EnsureCoverage(Array.Empty<TimePartition>(), noon, TimeSpan.Zero, 1); } catch (ArgumentOutOfRangeException) { threw = true; }
    Check("非法粒度抛参数异常", threw);
}

// 20) RebalancePlanner 数据再均衡计划（分库分区分表底座）
{
    var skewed = RebalancePlanner.Plan(new[] { new RebalanceShard("p0", 100), new RebalanceShard("p1", 10), new RebalanceShard("p2", 10), new RebalanceShard("p3", 10), new RebalanceShard("p4", 10) });
    Check("热点搬出量正确", skewed.Moves.Count == 4 && skewed.Moves.All(m => m.From == "p0"));
    Check("每片承接缺口相等", skewed.Moves.All(m => m.Rows == 18) && skewed.Moves.Select(m => m.To).SequenceEqual(new[] { "p1", "p2", "p3", "p4" }));
    Check("均值计算正确", skewed.MeanLoad == 28 && !skewed.Balanced);

    var even = RebalancePlanner.Plan(new[] { new RebalanceShard("a", 10), new RebalanceShard("b", 10) });
    Check("已均衡不出搬迁", even.Balanced && even.Moves.Count == 0);

    var empty = RebalancePlanner.Plan(Array.Empty<RebalanceShard>());
    Check("空输入视为均衡", empty.Balanced && empty.MeanLoad == 0);

    var over = RebalancePlanner.Plan(new[] { new RebalanceShard("big", 90), new RebalanceShard("s1", 10), new RebalanceShard("s2", 20) });
    Check("溢出量受承接缺口封顶", over.Moves.Sum(m => m.Rows) == 50 && over.Moves[0].To == "s1", string.Join(",", over.Moves.Select(m => $"{m.From}->{m.To}:{m.Rows}")));

    var tie = RebalancePlanner.Plan(new[] { new RebalanceShard("h2", 40), new RebalanceShard("h1", 40), new RebalanceShard("c1", 10), new RebalanceShard("c2", 30) });
    Check("并列热点按超出量与名字定序", tie.Moves[0].From == "h1" && tie.Moves[0].To == "c1", string.Join(",", tie.Moves.Select(m => $"{m.From}->{m.To}:{m.Rows}")));
}

// 21) PartitionDdl 分区 DDL 方言生成（分库分区分表底座）
{
    var p = new TimePartition("p_20261115", new DateTime(2026, 11, 15), new DateTime(2026, 11, 16));
    Check("MySQL 新增分区走 TO_DAYS 上界", PartitionDdl.AddSql(DbEngine.MySQL, "orders", p) ==
        "ALTER TABLE `orders` ADD PARTITION (PARTITION p_20261115 VALUES LESS THAN (TO_DAYS('2026-11-16')))");
    Check("PG 新增分区声明式半开区间", PartitionDdl.AddSql(DbEngine.PostgreSQL, "orders", p) ==
        "CREATE TABLE orders_p_20261115 PARTITION OF orders FOR VALUES FROM ('2026-11-15 00:00:00') TO ('2026-11-16 00:00:00')");
    Check("不支持分区方言返回空", PartitionDdl.AddSql(DbEngine.SQLite, "orders", p) == null && PartitionDdl.DropSql(DbEngine.SQLite, "orders", "p_x") == null);
    Check("MySQL 归档分区 DROP PARTITION", PartitionDdl.DropSql(DbEngine.MySQL, "orders", "p_20261101") == "ALTER TABLE `orders` DROP PARTITION p_20261101");
    Check("PG 归档分区 DROP 子表", PartitionDdl.DropSql(DbEngine.PostgreSQL, "orders", "p_20261101") == "DROP TABLE IF EXISTS orders_p_20261101");

    var noon = new DateTime(2026, 11, 15, 12, 0, 0);
    var day = TimeSpan.FromDays(1);
    var plan = PartitionRoller.EnsureCoverage(new[]
    {
        new TimePartition("p_20261101", new DateTime(2026, 11, 1), new DateTime(2026, 11, 2)),
        new TimePartition("p_20261114", new DateTime(2026, 11, 14), new DateTime(2026, 11, 15)),
    }, noon, day, 7);
    var mysqlSqls = PartitionDdl.PlanSql(DbEngine.MySQL, "orders", plan);
    Check("整计划先归档后新增有序", mysqlSqls.Count == 2 && mysqlSqls[0].Contains("DROP PARTITION p_20261101") && mysqlSqls[1].Contains("ADD PARTITION (PARTITION p_20261115"));
    Check("不支持分区整计划返回空表", PartitionDdl.PlanSql(DbEngine.SQLite, "orders", plan).Count == 0);
}

// 22) HotspotSalting 热点盐化（分库分区分表底座，承接 A5-4「拆分加盐」处置）
{
    var plan = HotspotSalting.Plan("merchant_88", 4);
    Check("默认命名子键确定序", plan.SaltedKeys.SequenceEqual(new[] { "merchant_88#0", "merchant_88#1", "merchant_88#2", "merchant_88#3" }),
        string.Join("|", plan.SaltedKeys));
    Check("方案记录键与槽数", plan.HotKey == "merchant_88" && plan.SaltCount == 4);

    var custom = HotspotSalting.Plan("big_shop", 3, (k, i) => $"{k}_{i:00}");
    Check("自定义命名生效", custom.SaltedKeys.SequenceEqual(new[] { "big_shop_00", "big_shop_01", "big_shop_02" }));

    Check("读扇出覆盖全槽", HotspotSalting.FanOutRead(plan).Count == 4 && HotspotSalting.FanOutRead(plan)[2] == "merchant_88#2");

    int r1 = HotspotSalting.RouteWrite("row-123", 8);
    int r2 = HotspotSalting.RouteWrite("row-123", 8);
    Check("写路由确定且落在槽内", r1 == r2 && r1 >= 0 && r1 < 8);

    var hits = new bool[8];
    for (int i = 0; i < 200; i++) hits[HotspotSalting.RouteWrite($"row-{i}", 8)] = true;
    Check("写路由实际打散到多槽", hits.Count(h => h) >= 4, $"覆盖 {hits.Count(h => h)}/8 槽");

    bool threw;
    threw = false; try { HotspotSalting.Plan("", 4); } catch (ArgumentException) { threw = true; }
    Check("空热点键拒绝", threw);
    threw = false; try { HotspotSalting.Plan("k", 1); } catch (ArgumentOutOfRangeException) { threw = true; }
    Check("盐槽数 1 拒绝", threw);
    threw = false; try { HotspotSalting.Plan("k", 1025); } catch (ArgumentOutOfRangeException) { threw = true; }
    Check("盐槽数超上界拒绝", threw);
    threw = false; try { HotspotSalting.RouteWrite("r", 0); } catch (ArgumentOutOfRangeException) { threw = true; }
    Check("写路由零槽拒绝", threw);
}

// 23) OnlineDdl 在线改表扇出（新需求「图形化改表」配套，对标 pt-osc 步骤）
{
    var plan = OnlineDdl.Plan("orders", "ADD COLUMN remark VARCHAR(200)", "id", 2500, 1000);
    Check("影子表与旧表确定命名", plan.ShadowTable == "_osc_orders_new" && plan.OldTable == "_osc_orders_old");
    var phases = plan.Steps.Select(s => s.Phase).ToList();
    Check("阶段顺序 build→alter→backfill→swap→cleanup",
        phases[0] == "build" && phases[1] == "alter" && phases[2] == "backfill" && phases[^2] == "swap" && phases[^1] == "cleanup",
        string.Join(",", phases));
    Check("回填批数向上取整", plan.Steps.Count(s => s.Phase == "backfill") == 4, $"{plan.Steps.Count(s => s.Phase == "backfill")} 批");
    Check("换名是单语句原子操作", plan.Steps.Single(s => s.Phase == "swap").Sql ==
        "RENAME TABLE `orders` TO `_osc_orders_old`, `_osc_orders_new` TO `orders`");
    Check("换名步唯一且在回填之后", plan.Steps.Count(s => s.Phase == "swap") == 1);
    Check("清理步提示先对账", plan.Steps[^1].Note.Contains("A5-2 数据对账"));
    Check("回填携带主键分段谓词", plan.Steps.Where(s => s.Phase == "backfill").All(s => s.Sql.Contains("WHERE s.`id` > @last")));

    var empty = OnlineDdl.Plan("t", "ADD COLUMN c INT", "id", 0, 500);
    Check("零行无回填步", empty.Steps.Count(s => s.Phase == "backfill") == 0 && empty.Steps.Count == 4);

    bool threw;
    threw = false; try { OnlineDdl.Plan("", "ADD COLUMN c INT", "id", 10, 100); } catch (ArgumentException) { threw = true; }
    Check("空表名拒绝", threw);
    threw = false; try { OnlineDdl.Plan("t", " ", "id", 10, 100); } catch (ArgumentException) { threw = true; }
    Check("空变更子句拒绝", threw);
    threw = false; try { OnlineDdl.Plan("t", "ADD COLUMN c INT", "", 10, 100); } catch (ArgumentException) { threw = true; }
    Check("空主键列拒绝", threw);
    threw = false; try { OnlineDdl.Plan("t", "ADD COLUMN c INT", "id", -1, 100); } catch (ArgumentOutOfRangeException) { threw = true; }
    Check("负行数拒绝", threw);
    threw = false; try { OnlineDdl.Plan("t", "ADD COLUMN c INT", "id", 10, 0); } catch (ArgumentOutOfRangeException) { threw = true; }
    Check("零批大小拒绝", threw);
}

// 智能线 C1 第一批：慢查询诊断+一键优化闭环（含复测）、容量增长预测、查询复现包（纯引擎，SQLite 真库验证）
{
    // 1) 静态诊断
    var f1 = SlowQueryDiagnoser.Analyze("SELECT * FROM big_table");
    Check("智能线-SELECT*与全量读双报", f1.Any(x => x.Code == "S001") && f1.Any(x => x.Code == "S005"), string.Join(",", f1.Select(x => x.Code)));
    var f2 = SlowQueryDiagnoser.Analyze("DELETE FROM t");
    Check("智能线-无WHERE删除判危险", f2.Any(x => x.Code == "D001" && x.Severity == "danger"), "");
    var f3 = SlowQueryDiagnoser.Analyze("SELECT a FROM t WHERE name LIKE '%x'");
    Check("智能线-前导通配LIKE", f3.Any(x => x.Code == "S002"), "");
    var f4 = SlowQueryDiagnoser.Analyze("SELECT a FROM t ORDER BY a");
    Check("智能线-排序无LIMIT提示", f4.Any(x => x.Code == "S007"), "");
    Check("智能线-正常查询不误报", SlowQueryDiagnoser.Analyze("SELECT id, name FROM t WHERE id = 1 LIMIT 10").Count == 0,
        string.Join(",", SlowQueryDiagnoser.Analyze("SELECT id, name FROM t WHERE id = 1 LIMIT 10").Select(x => x.Code)));

    // 2) 计划诊断（SQLite EXPLAIN QUERY PLAN 口径）
    Check("智能线-计划全表扫描", SlowQueryDiagnoser.AnalyzePlan("SCAN t").Any(x => x.Code == "P001"), "");
    Check("智能线-走索引不报扫描", !SlowQueryDiagnoser.AnalyzePlan("SEARCH t USING INDEX idx_t_code").Any(x => x.Code == "P001"), "");
    Check("智能线-临时B树提示", SlowQueryDiagnoser.AnalyzePlan("USE TEMP B-TREE FOR ORDER BY").Any(x => x.Code == "P002"), "");
    Check("智能线-标量子查询提示", SlowQueryDiagnoser.AnalyzePlan("SCALAR SUBQUERY 1").Any(x => x.Code == "P003"), "");

    // 3) 真库闭环：诊断→建索引→计划改善→复测提速
    var dbPath = Path.Combine(Path.GetTempPath(), "taodbm-smartline-" + Guid.NewGuid().ToString("N") + ".db");
    await using (var setup = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
    {
        setup.Open();
        using (var c = setup.CreateCommand())
        {
            c.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT);";
            c.ExecuteNonQuery();
        }
        using (var tx = setup.BeginTransaction())
        {
            for (int i = 0; i < 20000; i++)
            using (var one = setup.CreateCommand())
            {
                one.Transaction = tx;
                one.CommandText = "INSERT INTO t(code) VALUES (@c)";
                one.Parameters.AddWithValue("@c", "X" + i);
                one.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
    await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
    {
        conn.Open();
        string Explain(string sql)
        {
            using var c = conn.CreateCommand();
            c.CommandText = "EXPLAIN QUERY PLAN " + sql;
            var parts = new List<string>();
            using var r = c.ExecuteReader();
            while (r.Read()) parts.Add(r.GetString(3));
            return string.Join("|", parts);
        }
        string probe = "SELECT * FROM t WHERE code = 'X12345'";
        Check("智能线-真库初始全表扫描", Explain(probe).Contains("SCAN"), Explain(probe));
        var ddl = SlowQueryDiagnoser.SuggestIndexDdl(probe, "t");
        Check("智能线-索引建议含code列", ddl.Count == 1 && ddl[0].Contains("(code)"), ddl.FirstOrDefault() ?? "");

        async Task RunAsync(string sql, CancellationToken ct)
        {
            using var c = conn.CreateCommand();
            c.CommandText = sql;
            using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { }
        }
        var before = await QueryRetest.MedianMsAsync(RunAsync, probe, 3);
        using (var c = conn.CreateCommand()) { c.CommandText = ddl[0]; c.ExecuteNonQuery(); }
        Check("智能线-建索引后走索引", System.Text.RegularExpressions.Regex.IsMatch(Explain(probe), @"USING\s+(COVERING\s+)?INDEX"), Explain(probe));
        Check("智能线-计划体检识别改善", !SlowQueryDiagnoser.AnalyzePlan(Explain(probe)).Any(x => x.Code == "P001"), "");
        var after = await QueryRetest.MedianMsAsync(RunAsync, probe, 3);
        Check("智能线-建索引复测提速", after < before, $"before={before:F2}ms after={after:F2}ms");
        var cmp = await QueryRetest.CompareAsync(RunAsync, "SELECT COUNT(*) FROM t", "SELECT COUNT(*) FROM t", 3);
        Check("智能线-复测报告口径", cmp.Speedup > 0 && cmp.Iterations == 3, $"speedup={cmp.Speedup:F2}");
    }
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    for (int attempt = 0; attempt < 5 && System.IO.File.Exists(dbPath); attempt++)
    {
        try { System.IO.File.Delete(dbPath); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); }
    }

    // 4) 容量增长预测：线性样本拟合 + 阈值外推
    var t0 = DateTimeOffset.Parse("2026-11-01T00:00:00+08:00");
    var samples = Enumerable.Range(0, 5).Select(i => new CapacityForecaster.CapacitySample(t0.AddDays(i), 100.0 * (i + 1))).ToList();
    var fc = CapacityForecaster.Forecast(samples, 1000.0);
    Check("智能线-日增速拟合", Math.Abs(fc.GrowthPerDay - 100.0) < 1e-6, fc.GrowthPerDay.ToString("F4"));
    Check("智能线-拟合优度", fc.RSquared > 0.999, fc.RSquared.ToString("F4"));
    Check("智能线-阈值外推", Math.Abs(fc.DaysToThreshold!.Value - 7.0) < 1e-6, fc.DaysToThreshold!.Value.ToString("F3"));
    Check("智能线-用量外推", Math.Abs(fc.PredictAt(10) - 1300.0) < 1e-6, fc.PredictAt(10).ToString("F2"));
    Check("智能线-单样本不外推", CapacityForecaster.Forecast(new List<CapacityForecaster.CapacitySample> { samples[0] }).DaysToThreshold == null, "");

    // 5) 查询复现包：净化→打包→往返→真库回放
    var pack = new QueryReproPack.ReproPack("1.0", "订单查询复现", "SQLite", "SELECT COUNT(*) AS n FROM orders WHERE region = @r",
        new Dictionary<string, string> { ["r"] = "华东", ["password"] = "should-drop" },
        new List<string> { "CREATE TABLE orders(id INTEGER PRIMARY KEY, region TEXT);", "INSERT INTO orders(region) VALUES ('华东');", "INSERT INTO orders(region) VALUES ('华北');" },
        "用于复现慢查询", DateTimeOffset.UtcNow);
    var clean = QueryReproPack.Sanitize(pack);
    Check("智能线-复现包剔凭据", !clean.Params.ContainsKey("password") && (clean.Notes ?? "").Contains("password"), clean.Notes ?? "");
    var packJson = QueryReproPack.ToJson(clean);
    Check("智能线-复现包不含凭据值", !packJson.Contains("should-drop"), "");
    var back = QueryReproPack.FromJson(packJson);
    Check("智能线-复现包往返", back != null && back.Sql == clean.Sql && back.Params["r"] == "华东", "");
    Check("智能线-坏包不炸", QueryReproPack.FromJson("{坏") == null, "");
    var db2 = Path.Combine(Path.GetTempPath(), "taodbm-repro-" + Guid.NewGuid().ToString("N") + ".db");
    await using (var conn2 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db2}"))
    {
        conn2.Open();
        foreach (var ddlSql in back!.SetupSql)
            using (var c = conn2.CreateCommand()) { c.CommandText = ddlSql; c.ExecuteNonQuery(); }
        using (var c = conn2.CreateCommand())
        {
            c.CommandText = back.Sql;
            c.Parameters.AddWithValue("@r", back.Params["r"]);
            var n = Convert.ToInt64(c.ExecuteScalar());
            Check("智能线-复现包真库回放", n == 1, n.ToString());
        }
    }
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    for (int attempt = 0; attempt < 5 && System.IO.File.Exists(db2); attempt++)
    {
        try { System.IO.File.Delete(db2); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); }
    }
}

// 智能线 C2 第二批：数据体检报告（空值/重复/分布突变）、查询结果出图聚合、离线快照包（SQLite 真库采样→离线浏览再聚合）
{
    // 1) 数据体检：空值与重复
    var profs = new List<ColumnProfile>
    {
        new("orders", "region", 1000, 5, 10, null, new Dictionary<string, double> { ["华东"] = 0.6, ["华北"] = 0.4 }),
        new("orders", "phone", 1000, 600, 0),
        new("orders", "email", 1000, 200, 500),
    };
    var rep = DataHealthCheck.Inspect(profs);
    Check("智能线2-空值红线判danger", rep.Findings.Any(f => f.Column == "phone" && f.Code == "H001" && f.Severity == "danger"), "");
    Check("智能线2-空值偏高判warn", rep.Findings.Any(f => f.Column == "email" && f.Code == "H001" && f.Severity == "warn"), "");
    Check("智能线2-重复率报H002", rep.Findings.Any(f => f.Column == "email" && f.Code == "H002"), "");
    Check("智能线2-健康列不误报", !rep.Findings.Any(f => f.Column == "region"), string.Join(",", rep.Findings.Select(f => f.Column)));
    Check("智能线2-扣分口径", rep.Score == 70, rep.Score.ToString());
    var repBack = DataHealthCheck.FromJson(DataHealthCheck.ToJson(rep));
    Check("智能线2-体检报告往返", repBack != null && repBack.Score == rep.Score && repBack.Findings.Count == rep.Findings.Count, "");

    // 2) 分布突变对比：类别占比漂移与均值漂移
    var before = new List<ColumnProfile>
    {
        new("orders", "region", 1000, 0, 0, null, new Dictionary<string, double> { ["华东"] = 0.6, ["华北"] = 0.4 }),
        new("orders", "amount", 1000, 0, 0, 100.0),
    };
    var afterSame = new List<ColumnProfile>
    {
        new("orders", "region", 1000, 0, 0, null, new Dictionary<string, double> { ["华东"] = 0.61, ["华北"] = 0.39 }),
        new("orders", "amount", 1000, 0, 0, 101.0),
    };
    Check("智能线2-无突变不报", DataHealthCheck.Compare(afterSame, before).Count == 0, "");
    var afterDrift = new List<ColumnProfile>
    {
        new("orders", "region", 1000, 0, 0, null, new Dictionary<string, double> { ["华东"] = 0.2, ["华北"] = 0.8 }),
        new("orders", "amount", 1000, 0, 0, 150.0),
    };
    var drifts = DataHealthCheck.Compare(afterDrift, before);
    Check("智能线2-类别占比突变", drifts.Any(f => f.Column == "region" && f.Code == "H003"), string.Join(",", drifts.Select(f => f.Code)));
    Check("智能线2-均值漂移", drifts.Any(f => f.Column == "amount" && f.Code == "H004"), string.Join(",", drifts.Select(f => f.Code)));
    Check("智能线2-新列不参与对比", DataHealthCheck.Compare(new List<ColumnProfile> { new("t", "c", 10, 0, 0) }, before).Count == 0, "");

    // 3) 出图聚合：分类 TopN、直方图、时间序列、图型推荐
    var qr = new QueryResult { Columns = { "city", "age", "day" } };
    foreach (var (c, a, d) in new[] { ("上海", 20.0, "2026-01-01"), ("北京", 30.0, "2026-01-01"), ("上海", 40.0, "2026-02-01"), ("广州", 50.0, "2026-02-01"), ("上海", 60.0, "2026-03-01") })
        qr.Rows.Add(new object?[] { c, a, DateTime.Parse(d) });
    var cat = ChartSpecBuilder.CategoryCount(qr, "city", 2);
    Check("智能线2-分类TopN计数", cat.Type == "bar" && cat.Labels[0] == "上海" && cat.Values[0] == 3 && cat.Labels.Count == 2, string.Join(",", cat.Labels));
    var hist = ChartSpecBuilder.Histogram(qr, "age", 4);
    Check("智能线2-直方图桶数守恒", hist.Type == "histogram" && hist.Labels.Count == 4 && hist.Values.Sum() == 5, string.Join("|", hist.Labels));
    Check("智能线2-直方图末桶闭区间", hist.Labels[0].StartsWith("[20,") && hist.Labels[3].EndsWith("]"), hist.Labels[3]);
    Check("智能线2-图型推荐", ChartSpecBuilder.RecommendType(qr, "age") == "histogram" && ChartSpecBuilder.RecommendType(qr, "day") == "line" && ChartSpecBuilder.RecommendType(qr, "city") == "bar", "");
    var ts = ChartSpecBuilder.TimeSeries(qr, "day", null, "month");
    Check("智能线2-月度分桶计数", ts.Type == "line" && ts.Labels.Count == 3 && ts.Values[0] == 2 && ts.Values[1] == 2 && ts.Values[2] == 1, string.Join(",", ts.Values));
    var tsv = ChartSpecBuilder.TimeSeries(qr, "day", "age", "month");
    Check("智能线2-月度求和", Math.Abs(tsv.Values[0] - 50) < 1e-9 && Math.Abs(tsv.Values[1] - 90) < 1e-9, string.Join(",", tsv.Values));
    var specBack = ChartSpecBuilder.FromJson(ChartSpecBuilder.ToJson(ts));
    Check("智能线2-图表规格往返", specBack != null && specBack.ChartId == ts.ChartId && specBack.Values.SequenceEqual(ts.Values), "");

    // 4) 离线快照包：真库采样→打包→往返→指纹→不带库浏览与再聚合
    var dbPath2 = Path.Combine(Path.GetTempPath(), "taodbm-snap-" + Guid.NewGuid().ToString("N") + ".db");
    QueryResult sample;
    await using (var conn3 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath2}"))
    {
        conn3.Open();
        using (var c = conn3.CreateCommand())
        {
            c.CommandText = "CREATE TABLE orders(id INTEGER PRIMARY KEY, region TEXT, amount REAL);" +
                "INSERT INTO orders(region, amount) VALUES ('华东',10),('华东',20),('华北',30),('华南',40),('华东',50),('华北',60);";
            c.ExecuteNonQuery();
        }
        using var q = conn3.CreateCommand();
        q.CommandText = "SELECT region, amount FROM orders";
        using var rd = q.ExecuteReader();
        sample = new QueryResult { Title = "orders" };
        for (int i = 0; i < rd.FieldCount; i++) sample.Columns.Add(rd.GetName(i));
        while (rd.Read()) sample.Rows.Add(Enumerable.Range(0, rd.FieldCount).Select(i => (object?)rd.GetValue(i)).ToArray());
    }
    var tbl = SnapshotPackFile.FromQueryResult("orders", sample, maxRows: 4);
    Check("智能线2-快照截断留痕", tbl.Rows.Count == 4 && tbl.TotalRows == 6, $"{tbl.Rows.Count}/{tbl.TotalRows}");
    var pack = SnapshotPackFile.Create("订单离线包", "demo", new[] { tbl }, "体检样例");
    pack.Tables.Add(SnapshotPackFile.FromQueryResult("meta", new QueryResult { Columns = { "k" }, Rows = { new object?[] { "v" } } }));
    var fp = SnapshotPackFile.Fingerprint(pack);
    var packJson = SnapshotPackFile.ToJson(pack);
    Check("智能线2-快照无凭据键", !packJson.Contains("password", StringComparison.OrdinalIgnoreCase), "");
    var packBack = SnapshotPackFile.FromJson(packJson);
    Check("智能线2-快照往返", packBack != null && packBack.Tables.Count == 2 && packBack.Tables[0].TotalRows == 6, "");
    Check("智能线2-指纹一致", packBack != null && SnapshotPackFile.Fingerprint(packBack) == fp, "");
    packBack!.Tables[0].Rows[0][1] = "999999";
    Check("智能线2-篡改可发现", SnapshotPackFile.Fingerprint(packBack) != fp, "");
    var offline = SnapshotPackFile.GetTable(packBack, "orders");
    Check("智能线2-离线取表不碰原库", offline != null && offline!.Columns.SequenceEqual(new[] { "region", "amount" }), "");
    Check("智能线2-取不到表返回空", SnapshotPackFile.GetTable(packBack, "nope") == null, "");
    var offChart = ChartSpecBuilder.CategoryCount(SnapshotPackFile.ToQueryResult(offline!), "region");
    Check("智能线2-离线再聚合出图", offChart.Labels[0] == "华东" && offChart.Values[0] == 2, string.Join(",", offChart.Labels));
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    for (int attempt = 0; attempt < 5 && System.IO.File.Exists(dbPath2); attempt++)
    {
        try { System.IO.File.Delete(dbPath2); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); }
    }
}

// 协作线 C3：表单化录入、数据血缘影响地图、团队局域网共享合并（纯引擎，免界面稿）
{
    // 1) 表单化录入：字段生成→校验→参数化 INSERT
    var cols = new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "INT AUTO_INCREMENT", Nullable = false, IsKey = true },
        new() { Name = "name", DataType = "VARCHAR(50)", Nullable = false },
        new() { Name = "age", DataType = "INT", Nullable = true },
        new() { Name = "day", DataType = "DATETIME", Nullable = false },
        new() { Name = "flag", DataType = "BIT", Nullable = true },
    };
    var form = FormSchemaBuilder.Build("customers", cols, new Dictionary<string, (string?, bool?, bool?)> { ["name"] = ("客户名", null, null) });
    Check("协作-自增主键隐藏", form.First(f => f.Column == "id").Hidden, "");
    Check("协作-类型推断与必填", form.First(f => f.Column == "age").InputType == "number" && !form.First(f => f.Column == "age").Required
        && form.First(f => f.Column == "day").InputType == "datetime" && form.First(f => f.Column == "day").Required
        && form.First(f => f.Column == "flag").InputType == "bool", "");
    Check("协作-覆盖改标签", form.First(f => f.Column == "name").Label == "客户名" && form.First(f => f.Column == "name").MaxLength == 50, "");
    var bad = FormSchemaBuilder.Validate(form, new Dictionary<string, string?> { ["name"] = "  ", ["age"] = "abc", ["day"] = "", ["flag"] = "maybe" });
    Check("协作-必填与类型校验拦截", !bad.Ok && bad.Errors.ContainsKey("name") && bad.Errors.ContainsKey("age") && bad.Errors.ContainsKey("day") && bad.Errors.ContainsKey("flag"),
        string.Join("|", bad.Errors));
    var over = FormSchemaBuilder.Validate(form, new Dictionary<string, string?> { ["name"] = new string('长', 51) });
    Check("协作-超长校验", over.Errors.ContainsKey("name") && over.Errors["name"].Contains("超长"), over.Errors.GetValueOrDefault("name") ?? "");
    var good = FormSchemaBuilder.Validate(form, new Dictionary<string, string?> { ["name"] = "张三", ["age"] = "30", ["day"] = "2026-11-15 10:00:00", ["flag"] = "true" });
    Check("协作-合法值通过", good.Ok && good.Errors.Count == 0, string.Join("|", good.Errors));
    var (insSql, insParams) = FormSchemaBuilder.BuildInsert("customers", form,
        new Dictionary<string, string?> { ["name"] = "张三", ["age"] = "30", ["day"] = "2026-11-15 10:00:00", ["flag"] = "true", ["id"] = "9" });
    Check("协作-INSERT跳过隐藏列且参数化", insSql.StartsWith("INSERT INTO customers") && !insSql.Contains("id") && insSql.Contains("@name") && insParams["name"] == "张三", insSql);

    // 2) 数据血缘：建图→逐级追溯→抽验提示
    var corpus = new List<(LineageObject, string)>
    {
        (new LineageObject("orders", "table", "CREATE TABLE orders(id INT)"), "CREATE TABLE orders(id INT)"),
        (new LineageObject("v_order_sum", "view", "CREATE VIEW v_order_sum AS SELECT region, SUM(amt) FROM orders GROUP BY region"),
         "CREATE VIEW v_order_sum AS SELECT region, SUM(amt) FROM orders GROUP BY region"),
        (new LineageObject("v_dash", "view", "CREATE VIEW v_dash AS SELECT * FROM v_order_sum"),
         "CREATE VIEW v_dash AS SELECT * FROM v_order_sum"),
        (new LineageObject("sp_alert", "routine", "CREATE PROCEDURE sp_alert AS BEGIN SELECT COUNT(*) FROM v_dash END"),
         "CREATE PROCEDURE sp_alert AS BEGIN SELECT COUNT(*) FROM v_dash END"),
    };
    var map = LineageMapper.Build(corpus);
    Check("协作-血缘建图直接依赖", map.ContainsKey("orders") && map["orders"].Any(o => o.Name == "v_order_sum"), string.Join(",", map.Keys));
    var impacts = LineageMapper.Impact(map, "orders");
    Check("协作-逐级追溯全部下游", impacts.Count == 3 && impacts[0].Object == "v_order_sum" && impacts[1].Object == "v_dash" && impacts[2].Object == "sp_alert",
        string.Join(",", impacts.Select(i => $"{i.Object}:{i.Path.Count}")));
    Check("协作-追溯路径逐级", impacts[2].Path.SequenceEqual(new[] { "orders", "v_order_sum", "v_dash", "sp_alert" }), string.Join("←", impacts[2].Path));
    var hints = LineageMapper.Hints(impacts);
    Check("协作-提示含抽验片段", hints.Count == 3 && hints[0].Contains("抽验片段") && hints[0].Contains("GROUP BY"), hints.FirstOrDefault() ?? "");
    Check("协作-无依赖对象空影响", LineageMapper.Impact(map, "items").Count == 0, "");

    // 3) 团队共享：双向合并（后写胜出/平局保本地/墓碑同步）+ 净化 + 往返
    var t0 = DateTimeOffset.Parse("2026-11-15T10:00:00+08:00");
    var local = new List<ShareItem>
    {
        new("f1", "favorite", "{\"sql\":\"SELECT 1\"}", t0, "甲"),
        new("f2", "comment", "{\"text\":\"旧评论\"}", t0, "甲"),
        new("c1", "connection", "{\"host\":\"10.0.0.5\",\"port\":3306}", t0, "甲"),
    };
    var remote = new List<ShareItem>
    {
        new("f1", "favorite", "{\"sql\":\"SELECT 2\"}", t0.AddMinutes(5), "乙"),
        new("f2", "comment", "{\"text\":\"旧评论\"}", t0, "乙"),
        new("f3", "comment", "{\"text\":\"新评论\"}", t0.AddMinutes(1), "乙", true),
        new("c9", "connection", "{\"host\":\"10.0.0.9\",\"password\":\"leak-me\",\"apikey\":\"k2\"}", t0.AddMinutes(2), "乙"),
    };
    var merged = TeamShareMerge.Merge(local, remote);
    Check("协作-后写胜出", merged.Merged.First(i => i.ItemId == "f1").Payload.Contains("SELECT 2"), "");
    Check("协作-平局保本地", merged.Merged.First(i => i.ItemId == "f2").Author == "甲", "");
    Check("协作-合并统计口径", merged.Added == 2 && merged.Updated == 1 && merged.Tombstones == 1, $"{merged.Added}/{merged.Updated}/{merged.Tombstones}");
    Check("协作-墓碑同步删除意图", merged.Merged.First(i => i.ItemId == "f3").Deleted, "");
    var (c9, dropped) = TeamShareMerge.Sanitize(merged.Merged.First(i => i.ItemId == "c9"));
    Check("协作-共享包剔凭据", !c9.Payload.Contains("leak-me") && !c9.Payload.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        && dropped.Contains("password") && dropped.Contains("apikey"), string.Join(",", dropped));
    var round = TeamShareMerge.FromJson(TeamShareMerge.ToJson(merged.Merged));
    Check("协作-共享包往返", round != null && round.Count == merged.Merged.Count && round.First(i => i.ItemId == "f3").Deleted, "");
    Check("协作-坏包不炸", TeamShareMerge.FromJson("{坏") == null, "");
}

// 档三纯引擎：定时备份计划（节奏生成/下次执行/保留清理）+ 权限审计（GRANT 解析/风险发现/对等回收）
{
    var anchor = DateTimeOffset.Parse("2026-11-15T00:00:00+08:00");
    var policy = new BackupPolicy(24, 6, 3, anchor);

    // 1) 槽位生成：窗口截取与增量挂靠
    var slots = BackupPlanner.Schedule(policy,
        DateTimeOffset.Parse("2026-11-15T12:00:00+08:00"), DateTimeOffset.Parse("2026-11-17T00:00:00+08:00"));
    Check("备份-窗口外全量跳过", slots.Count == 5 && slots.Count(s => s.Kind == "full") == 2
        && !slots.Any(s => s.At <= DateTimeOffset.Parse("2026-11-15T12:00:00+08:00")),
        string.Join(",", slots.Select(s => $"{s.Kind}:{s.At:MM-dd HH}:{s.Seq}")));
    Check("备份-增量挂靠全量序号", slots.Where(s => s.Kind == "incr").All(s => s.Seq == 1) && slots.First(s => s.Kind == "full").Seq == 1, "");
    var next = BackupPlanner.NextRun(policy, DateTimeOffset.Parse("2026-11-15T23:30:00+08:00"));
    Check("备份-下次执行时间", next == DateTimeOffset.Parse("2026-11-16T00:00:00+08:00"), next?.ToString() ?? "null");

    // 2) 保留清理：过期满量连同挂靠增量出列
    var existing = new List<BackupSlot>();
    for (int i = 0; i <= 4; i++)
    {
        existing.Add(new BackupSlot(anchor.AddDays(i), "full", i));
        existing.Add(new BackupSlot(anchor.AddDays(i).AddHours(6), "incr", i));
        if (i >= 3) existing.Add(new BackupSlot(anchor.AddDays(i).AddHours(12), "incr", i));
    }
    var prune = BackupPlanner.Prune(existing, policy);
    Check("备份-过期满量出列", prune.Expired.Count(s => s.Kind == "full") == 2 && prune.RetainedFulls == 3, $"fulls={prune.RetainedFulls}");
    Check("备份-挂靠增量随全量出列", prune.Expired.Count(s => s.Kind == "incr") == 2 && prune.RetainedIncrs == 5, $"incrs={prune.RetainedIncrs}");
    Check("备份-策略JSON往返", BackupPlanner.FromJson(BackupPlanner.ToJson(policy)) is { } bp && bp.RetainFulls == 3 && bp.IncrEveryHours == 6, "");
    Check("备份-坏策略不炸", BackupPlanner.FromJson("{坏") == null, "");
    bool threwBak = false;
    try { BackupPlanner.Schedule(new BackupPolicy(0, 0, 1, anchor), anchor, anchor.AddDays(1)); } catch (ArgumentOutOfRangeException) { threwBak = true; }
    Check("备份-零间隔拒绝", threwBak, "");

    // 3) 权限审计：解析→风险发现→对等回收
    var grants = PermissionAuditor.Parse(new[]
    {
        "GRANT SELECT, INSERT ON app.* TO 'app'@'10.0.0.%';",
        "GRANT ALL ON *.* TO 'dev'@'%', 'ops'@'10.0.0.5' WITH GRANT OPTION;",
        "GRANT FILE ON *.* TO 'report'@'10.0.0.7';",
        "GRANT SELECT ON *.* TO PUBLIC;",
        "REVOKE SELECT ON app.* FROM 'app'@'10.0.0.%';",
    });
    Check("权限-GRANT解析计数", grants.Count == 5, grants.Count.ToString());
    var app = grants.First(r => r.Grantee == "'app'@'10.0.0.%'");
    Check("权限-权限与对象解析", app.Privileges.SequenceEqual(new[] { "SELECT", "INSERT" }) && app.Object == "app.*" && !app.WithGrantOption, "");
    Check("权限-多账号拆分与转授", grants.Count(r => r.Grantee == "'dev'@'%'") == 1 && grants.First(r => r.Grantee == "'ops'@'10.0.0.5'").WithGrantOption, "");
    var findings = PermissionAuditor.Audit(grants);
    Check("权限-宽账号全权判danger", findings.Any(f => f.Grantee == "'dev'@'%'" && f.Code == "R101" && f.Severity == "danger"), "");
    Check("权限-可转授逐条报R102", findings.Count(f => f.Code == "R102") == 2, findings.Count(f => f.Code == "R102").ToString());
    Check("权限-高危权限报R103", findings.Any(f => f.Grantee == "'report'@'10.0.0.7'" && f.Code == "R103" && f.Message.Contains("FILE")), "");
    Check("权限-受限SELECT不误报", !findings.Any(f => f.Grantee == "'app'@'10.0.0.%'"), "");
    Check("权限-PUBLIC只读不算全权", !findings.Any(f => f.Grantee == "PUBLIC" && f.Code == "R101"), "");
    var revokes = PermissionAuditor.RevokeSql(app);
    Check("权限-对等REVOKE生成", revokes.Single() == "REVOKE SELECT, INSERT ON app.* FROM 'app'@'10.0.0.%';", revokes.FirstOrDefault() ?? "");
}

// 档三纯引擎：ER 正向生成（模型→建库 DDL）+ 执行计划树解析与瓶颈标注 + 对象树目录语句生成
{
    // 1) ER 正向：user ← orders（故意乱序放入，验证依赖排序）
    var user = new ErdTable { Name = "user" };
    user.Columns.Add(new ErdColumn { Name = "id", Type = "INT", IsKey = true });
    user.Columns.Add(new ErdColumn { Name = "city", Type = "VARCHAR(50)" });
    var orders = new ErdTable { Name = "orders" };
    orders.Columns.Add(new ErdColumn { Name = "id", Type = "INT", IsKey = true });
    orders.Columns.Add(new ErdColumn { Name = "user_id", Type = "INT" });
    var em = new ErdModel { Tables = { orders, user }, Edges = { new ErdEdge { FromTable = "orders", FromCol = "user_id", ToTable = "user" } } };
    var fwd = ErdForward.Generate(em);
    Check("ER正向-生成成功", fwd.Ok && fwd.Ddl.Count == 2, string.Join("|", fwd.Errors));
    Check("ER正向-被引用表在前", fwd.Ddl.Count == 2 && fwd.Ddl[0].StartsWith("CREATE TABLE `user`", StringComparison.Ordinal), fwd.Ddl.FirstOrDefault() ?? "");
    Check("ER正向-主键非空", fwd.Ddl[0].Contains("`id` INT NOT NULL", StringComparison.Ordinal) && fwd.Ddl[0].Contains("PRIMARY KEY (`id`)", StringComparison.Ordinal), fwd.Ddl[0]);
    Check("ER正向-外键约束", fwd.Ddl[1].Contains("FOREIGN KEY (`user_id`) REFERENCES `user` (`id`)", StringComparison.Ordinal), fwd.Ddl[1]);
    Check("ER正向-可空列不带NOT NULL", fwd.Ddl[0].Contains("`city` VARCHAR(50) NULL", StringComparison.Ordinal), fwd.Ddl[0]);

    var weird = new ErdTable { Name = "we`ird" };
    weird.Columns.Add(new ErdColumn { Name = "id", IsKey = true });
    var fwdWeird = ErdForward.Generate(new ErdModel { Tables = { weird } });
    Check("ER正向-反引号转义", fwdWeird.Ok && fwdWeird.Ddl[0].Contains("`we``ird`", StringComparison.Ordinal), fwdWeird.Ddl.FirstOrDefault() ?? "");
    Check("ER正向-模型为空拒绝", !ErdForward.Generate(null).Ok, "");

    var dupA = new ErdTable { Name = "dup" };
    dupA.Columns.Add(new ErdColumn { Name = "id", IsKey = true });
    var dupB = new ErdTable { Name = "dup" };
    dupB.Columns.Add(new ErdColumn { Name = "id", IsKey = true });
    Check("ER正向-重名表拒绝", !ErdForward.Generate(new ErdModel { Tables = { dupA, dupB } }).Ok, "");

    var noPk = new ErdTable { Name = "t" };
    noPk.Columns.Add(new ErdColumn { Name = "a", Type = "INT" });
    Check("ER正向-无主键拒绝", !ErdForward.Generate(new ErdModel { Tables = { noPk } }).Ok, "");

    var ta = new ErdTable { Name = "a" };
    ta.Columns.Add(new ErdColumn { Name = "id", IsKey = true });
    var tb = new ErdTable { Name = "b" };
    tb.Columns.Add(new ErdColumn { Name = "c", Type = "INT" });
    Check("ER正向-目标无主键拒绝", !ErdForward.Generate(new ErdModel
    {
        Tables = { ta, tb },
        Edges = { new ErdEdge { FromTable = "a", FromCol = "id", ToTable = "b" } }
    }).Ok, "");
    var badColFwd = ErdForward.Generate(new ErdModel
    {
        Tables = { new ErdTable { Name = "a", Columns = { new ErdColumn { Name = "id", IsKey = true } } } },
        Edges = { new ErdEdge { FromTable = "a", FromCol = "ghost", ToTable = "a" } }
    });
    Check("ER正向-坏列外键拒绝", !badColFwd.Ok && badColFwd.Errors.Any(e => e.Contains("列不存在")), string.Join("|", badColFwd.Errors));
}

{
    // 2) 执行计划：缩进树解析 + 瓶颈标注 + 索引建议
    var planText = "SCAN user\n  SEARCH user USING INDEX idx_user_city (city=?)\nSCAN orders\n  USE TEMP B-TREE FOR ORDER BY\n    Using filesort";
    var plan = ExplainPlanEngine.Parse(planText);
    Check("计划-解析节点数与根数", plan.Flat.Count == 5 && plan.Roots.Count == 2, $"flat={plan.Flat.Count} roots={plan.Roots.Count}");
    Check("计划-父子层级挂接", plan.Roots[0].Children.Count == 1 && plan.Roots[1].Children.Count == 1
        && plan.Roots[1].Children[0].Children.Count == 1,
        $"r0={plan.Roots[0].Children.Count} r1={plan.Roots[1].Children.Count}");
    Check("计划-空文本不炸", ExplainPlanEngine.Parse("  \n ").Flat.Count == 0, "");
    Check("计划-深度跳级收敛", ExplainPlanEngine.Parse("A\n        B").Flat.Last().Depth == 1, "");
    var pf = ExplainPlanEngine.Annotate(plan);
    Check("计划-全表扫描标注P201", pf.Count(f => f.Code == "P201") == 2, pf.Count.ToString());
    Check("计划-文件排序P202为danger", pf.Any(f => f.Code == "P202" && f.Severity == "danger"), "");
    Check("计划-临时表P203", pf.Any(f => f.Code == "P203"), "");
    Check("计划-命中索引不误报P201", !pf.Any(f => f.Node.Contains("USING INDEX")), "");
    Check("计划-SQLite口径提取", ExplainPlanEngine.ExtractFullScanTable("SCAN user") == "user", "");
    Check("计划-MySQL口径提取", ExplainPlanEngine.ExtractFullScanTable("Table scan on `orders`") == "orders", "");
    Check("计划-JSON往返", ExplainPlan.FromJson(plan.ToJson()) is { } back && back.Flat.Count == plan.Flat.Count, "");
}

{
    // 3) 对象树目录：按引擎生成元数据查询语句
    Check("目录-支持引擎判定", ObjectTreeCatalog.Supported(DbEngine.MySQL) && ObjectTreeCatalog.Supported(DbEngine.SQLite) && !ObjectTreeCatalog.Supported(DbEngine.Redis), "");
    var myViews = ObjectTreeCatalog.ListViews(DbEngine.MySQL, "shop");
    Check("目录-MySQL视图语句", myViews.Contains("information_schema.VIEWS") && myViews.Contains("'shop'"), myViews);
    Check("目录-PG视图语句", ObjectTreeCatalog.ListViews(DbEngine.PostgreSQL, "shop").Contains("pg_views"), "");
    Check("目录-SQLite视图语句", ObjectTreeCatalog.ListViews(DbEngine.SQLite, "main").Contains("sqlite_master"), "");
    Check("目录-MySQL索引语句", ObjectTreeCatalog.ListIndexes(DbEngine.MySQL, "shop").Contains("information_schema.STATISTICS"), "");
    Check("目录-MySQL外键语句", ObjectTreeCatalog.ListForeignKeys(DbEngine.MySQL, "shop").Contains("REFERENCED_TABLE_NAME IS NOT NULL"), "");
    Check("目录-MySQL触发器语句", ObjectTreeCatalog.ListTriggers(DbEngine.MySQL, "shop").Contains("information_schema.TRIGGERS"), "");
    Check("目录-SQLite触发器语句", ObjectTreeCatalog.ListTriggers(DbEngine.SQLite, "main").Contains("sqlite_master"), "");
    Check("目录-SQLite例程空清单", ObjectTreeCatalog.ListRoutines(DbEngine.SQLite, "main").Contains("WHERE 0"), "");
    Check("目录-引号转义", ObjectTreeCatalog.ListViews(DbEngine.MySQL, "it's").Contains("it''s"), "");
    bool threwCat = false;
    try { ObjectTreeCatalog.ListViews(DbEngine.MongoDB, "x"); } catch (NotSupportedException) { threwCat = true; }
    Check("目录-不支持引擎报错", threwCat, "");
}

Layer("系统");
{
    // 持久化完整工作流（临时目录）：收藏 + 会话 文件往返、损坏文件容错、失败路径不残留
    var dir = Path.Combine(Path.GetTempPath(), "taodbm-sys-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        Favorites.UseFile(Path.Combine(dir, "favorites.json"));
        TabSession.UseFile(Path.Combine(dir, "tabsession.json"));
        var favs = Favorites.Add(new List<FavoriteItem>(), "系统层", "MySQL", "db", "SELECT 1;");
        Favorites.Save(favs);
        Check("系统层：收藏文件往返", Favorites.Load().Count == 1 && Favorites.Load()[0].Name == "系统层");

        var badFav = Path.Combine(dir, "favorites.json");
        var good = File.ReadAllText(badFav);
        File.WriteAllText(badFav, "{corrupted json!!");
        Check("损坏收藏文件安全降级", Favorites.Load().Count == 0);
        Check("损坏文件原样保留", File.ReadAllText(badFav) == "{corrupted json!!");
        Check("失败路径无临时残留", Directory.GetFiles(dir).Length == 1);
        File.WriteAllText(badFav, good);

        File.WriteAllText(Path.Combine(dir, "tabsession.json"), "not-json-at-all");
        Check("损坏会话文件安全降级", TabSession.Load().Count == 0);
    }
    finally
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
{
    // 主题选择持久化：往返、损坏文件安全回落、未知值回落、失败路径无残留
    var dir = Path.Combine(Path.GetTempPath(), "taodbm-theme-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        ThemeStore.UseFile(Path.Combine(dir, "theme.json"));
        Check("主题默认水墨", ThemeStore.Load() == "ink");
        ThemeStore.Save("night");
        Check("主题选择往返", ThemeStore.Load() == "night");
        ThemeStore.Save("ant");
        Check("旧 ant 选择映射天青", ThemeStore.Load() == "gray");
        File.WriteAllText(Path.Combine(dir, "theme.json"), "{oops");
        Check("损坏主题文件回落水墨", ThemeStore.Load() == "ink");
        Check("主题文件原样保留", File.ReadAllText(Path.Combine(dir, "theme.json")) == "{oops");
        Check("失败路径无临时残留", Directory.GetFiles(dir).Length == 1);
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}
{
    // 托盘关闭偏好持久化：默认询问、tray/exit 往返、损坏文件安全回落、失败路径无残留
    var dir = Path.Combine(Path.GetTempPath(), "taodbm-prefs-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        AppPrefs.UseFile(Path.Combine(dir, "appprefs.json"));
        Check("关闭行为默认询问", AppPrefs.Load() == CloseBehavior.Ask);
        AppPrefs.Save(CloseBehavior.Tray);
        Check("托盘最小化偏好往返", AppPrefs.Load() == CloseBehavior.Tray);
        AppPrefs.Save(CloseBehavior.Exit);
        Check("直接退出偏好往返", AppPrefs.Load() == CloseBehavior.Exit);
        File.WriteAllText(Path.Combine(dir, "appprefs.json"), "{broken");
        Check("损坏偏好文件回落询问", AppPrefs.Load() == CloseBehavior.Ask);
        Check("偏好文件原样保留", File.ReadAllText(Path.Combine(dir, "appprefs.json")) == "{broken");
        Check("失败路径无临时残留", Directory.GetFiles(dir).Length == 1);
    }
    finally { AppPrefs.UseFile(""); if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}
{
    // 托盘健在断言（2026-11-03 真机托盘不见的教训常驻）：窗口句柄必须随消息上送，增改删同源；
    // 添加结果不得被掩盖；explorer 重启后自动补挂；右键必须带「我的连接」常用区且主窗已接线。
    string ProjRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent!)
            if (File.Exists(Path.Combine(d.FullName, "Src", "TaoDBM.App", "MainWindow.xaml"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    var root = ProjRoot();
    var tray = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "UI", "TrayIcon.cs"));
    Check("托盘注册带主窗句柄（NIM_ADD 成败的唯一键）", tray.Contains("hWnd = _ownerHwnd"));
    Check("托盘添加结果如实记录不被掩盖", tray.Contains("_added = Shell_NotifyIconW(NIM_ADD"));
    Check("explorer 重启后托盘自动补挂", tray.Contains("TaskbarCreated") && tray.Contains("AddToShell"));
    Check("托盘右键带我的连接动态区", tray.Contains("RecentProvider") && tray.Contains("RecentRequested") && tray.Contains("我的连接"));
    var mwCs3 = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "MainWindow.xaml.cs"));
    Check("主窗接线托盘连接快捷区", mwCs3.Contains("RecentProvider") && mwCs3.Contains("TrayConnect"));
    // 关窗分流铁律（2026-11-03 真机反馈常驻）：托盘没挂上时绝不隐藏玩消失——要么真挂上再拦，要么如实退出
    Check("托盘挂载状态如实可查", tray.Contains("public bool IsAdded"));
    Check("托盘未挂上时关窗不拦截", mwCs3.Contains("!_tray.IsAdded") && mwCs3.Contains("try { AppWindow.Hide(); }"));
    // 2026-11-03 二轮兜底（真机复测仍看不到托盘）：资源缺失不连坐、失败自动重试、版本可验装
    Check("托盘创建不以图标文件存在为前提", mwCs3.Contains("StartTray(trayIcoPath);"));
    Check("托盘图标缺失回落系统图标", tray.Contains("LoadIconW"));
    Check("托盘未挂上自动重试", tray.Contains("public void RetryAdd") && mwCs3.Contains("StartTrayRetry()"));
    // 2026-11-03 三轮（真机日志实锤：自建消息窗口 CreateWindowExW 返回空，托盘整链失效）：改托管主窗句柄
    Check("托盘消息托管主窗不再自建窗口", tray.Contains("SetWindowLongPtrW") && !tray.Contains("CreateWindowExW"));
    Check("子类化其余消息链回原过程", tray.Contains("CallWindowProcW"));
    Check("退出时还原主窗过程", tray.Contains("GWLP_WNDPROC") && tray.Contains("_prevProc"));
    Check("托盘诊断写入日志且带错误码", tray.Contains("Core.AppLog.Write") && tray.Contains("GetLastWin32Error"));
    Check("日志目录固定在数据根 logs", File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.Infrastructure", "AppLog.cs")).Contains("\"logs\""));
    // 陶瓷质感（2026-11-06 用户定稿，对齐 TaoAgent/TaoPad）：卡片面 XAML 资源换纵向釉面微渐变，
    // 三套主题统一生效；异常退回纯色不阻断换肤；墨夜配色本体不被触碰（母本锚点断言在上文主题块）。
    var antCs = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "UI", "Ant.cs"));
    Check("陶瓷质感：釉面微渐变挂载到卡片资源且随主题重涂",
        antCs.Contains("ApplyCeramicCard(pal)") && antCs.Contains("LinearGradientBrush") && antCs.Contains("Offset = 0.55"));
    Check("陶瓷质感：通道增减受钳制且异常退回纯色画刷",
        antCs.Contains("Math.Min(255, v + 15)") && antCs.Contains("Math.Max(0, v - 12)")
        && antCs.Contains("Application.Current.Resources[\"AntCard\"] = brush") && antCs.Contains("Application.Current.Resources[\"AntCard\"] = Card"));
    var csprojText = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "TaoDBM.App.csproj"));
    // 二期拆分后安装包工程暂未纳入，wxs 存在才校验
    var wxsPath = Path.Combine(root, "Src", "Installer", "Package.wxs");
    var versionOk = csprojText.Contains("<Version>2.0.0<")
        && (!File.Exists(wxsPath) || File.ReadAllText(wxsPath).Contains("Version=\"2.0.0\""));
    Check("版本号升级可验装（标题栏 v2.0.0）", versionOk);
    // 弹窗表单右缘让出滚动条带（2026-11-03 真机截图：浮层滚动条压在输入框右缘像一条越界竖线）
    int bandOk = 0;
    foreach (var f in new[] { "ConnectionDialog.cs", "DataSyncDialog.cs", "FakerDialog.cs", "WorkTab.cs" })
        if (File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "UI", f)).Contains("Margin = new Thickness(0, 0, 12, 0)")) bandOk++;
    Check("弹窗表单统一让出滚动条带（4 处）", bandOk == 4, $"{bandOk}/4");
}
{
    // 存储落 D 盘（Tao 系列）：回退目录契约 + 旧数据迁移（已有同名不覆盖）+ 失败安全
    Check("存储根目录可用", Directory.Exists(TaoPaths.Root) && Path.IsPathRooted(TaoPaths.Root));
    Check("存储根即回退目录（非 Windows）", OperatingSystem.IsWindows() || TaoPaths.Root.Contains("TaoDBM"));
    var fp = TaoPaths.FilePath("probe-test.json");
    Check("FilePath 自动建目录", File.Exists(fp) == false && Directory.Exists(TaoPaths.Root));
    var migDir = Path.Combine(Path.GetTempPath(), "taodbm-mig-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(Path.Combine(migDir, "old"));
        Directory.CreateDirectory(Path.Combine(migDir, "new"));
        File.WriteAllText(Path.Combine(migDir, "old", "favorites.json"), "[{\"Name\":\"迁移\"}]");
        File.WriteAllText(Path.Combine(migDir, "old", "connections.json"), "[]");
        var newRoot = Path.Combine(migDir, "new");
        File.WriteAllText(Path.Combine(newRoot, "favorites.json"), "[{\"Name\":\"已有\"}]");
        var moved = TaoPaths.MigrateLegacyFiles([Path.Combine(migDir, "old"), Path.Combine(migDir, "new")], targetRoot: newRoot);
        Check("迁移复制缺失文件", File.ReadAllText(Path.Combine(newRoot, "connections.json")) == "[]" && moved.Contains("connections.json"), string.Join(",", moved));
        Check("迁移不覆盖已有文件", File.ReadAllText(Path.Combine(newRoot, "favorites.json")) == "[{\"Name\":\"已有\"}]" && !moved.Contains("favorites.json"));
        Check("迁移失败路径安全（源目录缺失不抛）", TaoPaths.MigrateLegacyFiles([Path.Combine(migDir, "no-such-dir")], targetRoot: newRoot).Count == 0);
    }
    finally { if (Directory.Exists(migDir)) Directory.Delete(migDir, true); }
}
{
    // 大数据分块往返：5 万行 CSV → 解析 → INSERT，行数字节级一致
    var sb = new StringBuilder("id,tag\n");
    for (int i = 0; i < 50000; i++) sb.Append(i).Append(",tag_").Append(i % 7).Append('\n');
    var (bh, br) = CsvImporter.Parse(sb.ToString());
    var bc = new List<ColumnSpec> { new() { Name = "id", DataType = "int" }, new() { Name = "tag", DataType = "varchar(20)" } };
    var inserts = CsvImporter.BuildInserts(new TestProv(), "db", "big", bc, bh, br, batch: 500);
    var total = inserts.Sum(x => x.Split("VALUES (").Length - 1);
    Check("5 万行往返行数一致", br.Count == 50000 && total == 50000, $"{br.Count}/{total}");
}

{
    // 层色底定律（TaoZip v62 母本）断言锁：页底显式、空态禁三级灰、标签根板层色
    string SrcRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent!)
            if (File.Exists(Path.Combine(d.FullName, "Src", "TaoDBM.App", "MainWindow.xaml"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    var srcRoot = SrcRoot();
    var xaml = File.ReadAllText(Path.Combine(srcRoot, "Src", "TaoDBM.App", "MainWindow.xaml"));
    Check("主窗根板钉主题底色", xaml.Contains("<Grid Background=\"{ThemeResource AntBg}\">"));
    var tab = File.ReadAllText(Path.Combine(srcRoot, "Src", "TaoDBM.App", "UI", "WorkTab.cs"));
    Check("标签根板层色底在位", tab.Contains("Background = Ant.Bg;"));
    bool EmptyHintOk(string marker)
    {
        int i = tab.IndexOf(marker, StringComparison.Ordinal);
        return i >= 0 && tab[i..Math.Min(i + 300, tab.Length)].Contains("Foreground = Ant.TextSub");
    }
    Check("空态提示用次要前景", EmptyHintOk("还没有收藏") && EmptyHintOk("还没有历史记录"));
}
{
    // 命名与 BOM 铁律（2026-11-01 全系避坑常驻，源自 TaoPad v74 审计）：源码树本身必须干净
    string TreeRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent!)
            if (File.Exists(Path.Combine(d.FullName, "Src", "TaoDBM.App", "MainWindow.xaml"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    var treeRoot = TreeRoot();
    bool clean = true; string? hit = null;
    foreach (var p in Directory.EnumerateFiles(treeRoot, "*", SearchOption.AllDirectories))
    {
        var rel = p.Substring(treeRoot.Length + 1).Replace('\\', '/');
        if (rel.Contains("/obj/") || rel.Contains("/bin/") || rel.Contains("/.git/") || rel.Contains("/.vs/")
            || rel.StartsWith("typecheck/") || rel.StartsWith("logictest/") || rel.StartsWith("Docs/")) continue;
        if (rel.Any(c => c > 127)) { clean = false; hit = rel; break; }
        if (p.EndsWith(".cs") || p.EndsWith(".xaml"))
        {
            var b = File.ReadAllBytes(p);
            if (b.Length < 3 || b[0] != 0xEF || b[1] != 0xBB || b[2] != 0xBF) { clean = false; hit = rel; break; }
        }
    }
    Check("源码树全 ASCII 且 .cs/.xaml 带 BOM（首违规: " + (hit ?? "无") + "）", clean);
}
{
    // 工程文件结构铁律（2026-11-01 真机 MSB4066 教训）：PropertyGroup 内只许属性，条目元素必须在 ItemGroup
    string ProjRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent!)
            if (File.Exists(Path.Combine(d.FullName, "Src", "TaoDBM.App", "MainWindow.xaml"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    var csproj = Path.Combine(ProjRoot(), "Src", "TaoDBM.App", "TaoDBM.App.csproj");
    var doc = System.Xml.Linq.XDocument.Load(csproj);
    string[] itemTags = { "Content", "Page", "None", "PackageReference", "ProjectReference", "Reference", "EmbeddedResource", "Compile", "ApplicationDefinition", "Resource", "Folder" };
    var stray = doc.Descendants("PropertyGroup")
        .SelectMany(pg => pg.Elements())
        .Where(e => itemTags.Contains(e.Name.LocalName))
        .Select(e => e.Name.LocalName)
        .ToList();
    Check("csproj PropertyGroup 内无条目元素（发现: " + string.Join(",", stray) + "）", stray.Count == 0);
    // 批处理换行铁律（2026-11-01 真机乱码嚼行教训）：.bat 必须 CRLF；根目录=GBK 编码，build/*.utf8.bat=UTF-8 编码
    var projRoot = ProjRoot();
    int batBad = 0; string batHit = "";
    foreach (var p in Directory.EnumerateFiles(projRoot, "*.bat", SearchOption.AllDirectories))
    {
        var rel = p.Substring(projRoot.Length + 1).Replace('\\', '/');
        if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
        var b = File.ReadAllBytes(p);
        int bareLf = 0;
        for (int i = 0; i < b.Length; i++)
            if (b[i] == 10 && (i == 0 || b[i - 1] != 13)) bareLf++;
        bool isUtf8Variant = rel.Contains(".utf8.");
        bool utf8Clean = !System.Text.Encoding.UTF8.GetString(b).Contains('\uFFFD');
        bool hasHighBytes = b.Any(x => x >= 0x80);
        bool encOk = isUtf8Variant ? utf8Clean : (hasHighBytes ? !utf8Clean : true);
        if (bareLf > 0 || !encOk) { batBad++; batHit = rel; }
    }
    Check("全部 .bat 为 CRLF 且编码合规（首违规: " + batHit + "）", batBad == 0);
}
{
    // 换肤三闸（2026-11-01 对照 TaoDown v2.36 换肤失效教训）：页面笔刷消费 + 标题栏钉色 + 强调色随象
    string ProjRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent!)
            if (File.Exists(Path.Combine(d.FullName, "Src", "TaoDBM.App", "MainWindow.xaml"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    var root = ProjRoot();
    var mwXaml = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "MainWindow.xaml"));
    Check("页面层无写死旧蓝 #1677FF", !mwXaml.Contains("#1677FF"));
    var mwCs = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "MainWindow.xaml.cs"));
    Check("标题栏随象而染（AppWindow.TitleBar 钉色在位）", mwCs.Contains("AppWindow.TitleBar") && mwCs.Contains("ButtonHoverBackgroundColor"));
    var ant = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "UI", "Ant.cs"));
    Check("系统强调色族随主题（accent 挂会变色画刷）", ant.Contains("SystemControlForegroundAccentBrush") && ant.Contains("ThemeApplied"));
}
{
    // 换肤收尾（2026-11-02 同步全系最新要求）：accent 全族随象 + 主色底字随象 + 高亮/报告随象
    string ProjRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent!)
            if (File.Exists(Path.Combine(d.FullName, "Src", "TaoDBM.App", "MainWindow.xaml"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    var root = ProjRoot();
    var appXaml = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "App.xaml"));
    Check("App.xaml 不写死 accent 色族（含浅深变体全由 Ant 运行时管）", !appXaml.Contains("SystemAccentColor") && !appXaml.Contains("#1677FF"));
    var antCs = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "UI", "Ant.cs"));
    Check("主色底字随象（OnPrimary 画刷，墨夜近黑，accent 浅深变体同源）",
        antCs.Contains("OnPrimary") && antCs.Contains("131211") && antCs.Contains("SystemAccentColorLight1") && antCs.Contains("SystemAccentColorDark1"));
    string uiHit = "";
    foreach (var f in Directory.GetFiles(Path.Combine(root, "Src", "TaoDBM.App", "UI"), "*.cs"))
    {
        var s = File.ReadAllText(f);
        bool hardWhite = s.Contains("Colors.White");
        bool hardBlue = Path.GetFileName(f) != "Ant.cs" && s.Contains("#1677FF");
        bool hlOld = s.Contains("Ant.Color(\"1677FF\")");
        if (hardWhite || hardBlue || hlOld) { uiHit = Path.GetFileName(f); break; }
    }
    Check("界面层无写死旧蓝与死白字（首违规: " + uiHit + "）", uiHit == "");
    var mwXaml = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "MainWindow.xaml"));
    Check("顶栏文字随象（AntOnPrimary 资源在位）", mwXaml.Contains("AntOnPrimary") && !mwXaml.Contains("Foreground=\"White\""));
    var wtCs = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "UI", "WorkTab.cs"));
    Check("SQL 高亮关键字取主题主色且换肤重刷", wtCs.Contains("Ant.Primary.Color") && wtCs.Contains("Ant.Text.Color") && wtCs.Contains("ThemeApplied"));
    var rb = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.Core", "ReportBuilder.cs"));
    Check("HTML 报告取色随主题", !rb.Contains("#1677FF") && rb.Contains("AntPrimary"));
}
{
    // 构建闸流程铁律（2026-11-02 真机教训：成功路径掉进失败块，闸把完整好包全拦了）：
    // 两份 bat 必须三段式——探针失败→:unpackbad，自检失败→:checkbad，全过→显式 goto :gateok 放行
    string ProjRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent!)
            if (File.Exists(Path.Combine(d.FullName, "Src", "TaoDBM.App", "MainWindow.xaml"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    static int BCount(byte[] arr, byte[] pat)
    {
        int c = 0;
        for (int i = 0; i <= arr.Length - pat.Length; i++)
        {
            bool m = true;
            for (int j = 0; j < pat.Length; j++) if (arr[i + j] != pat[j]) { m = false; break; }
            if (m) c++;
        }
        return c;
    }
    static int BIndex(byte[] arr, byte[] pat)
    {
        for (int i = 0; i <= arr.Length - pat.Length; i++)
        {
            bool m = true;
            for (int j = 0; j < pat.Length; j++) if (arr[i + j] != pat[j]) { m = false; break; }
            if (m) return i;
        }
        return -1;
    }
    var root = ProjRoot();
    int badFlow = 0; string batHit = "";
    int batTotal = 0;
    foreach (var rel in new[] { "build-installer.bat", "build/build-installer.utf8.bat" })
    {
        if (!File.Exists(Path.Combine(root, rel))) { continue; }
        batTotal++;
        var bb = File.ReadAllBytes(Path.Combine(root, rel));
        byte[] P(string s) => System.Text.Encoding.UTF8.GetBytes(s);
        bool hasGates = BCount(bb, P("goto :gateok")) == 1 && BCount(bb, P("goto :checkbad")) == 1
            && BCount(bb, P("goto :unpackbad")) == 3 && BCount(bb, P("if errorlevel 1 goto :checkbad")) == 1
            && BCount(bb, P("-pdbtype none")) == 1 && BCount(bb, P("out\\*.msi")) == 1;
        int iUn = BIndex(bb, P("\r\n:unpackbad\r\n")), iCh = BIndex(bb, P("\r\n:checkbad\r\n")), iGo = BIndex(bb, P("\r\n:gateok\r\n"));
        bool ordered = iUn >= 0 && iUn < iCh && iCh < iGo;
        if (!hasGates || !ordered) { badFlow++; batHit = rel; }
    }
    Check(batTotal == 0 ? "构建闸三段式流程（二期暂无 bat，跳过）" : "构建闸三段式流程齐备（首违规: " + batHit + "）", badFlow == 0);
}
{
    // 快捷方式与顶栏铁律（2026-11-02 真机教训）：
    // ①自定义动作设置的目录属性值必须以目录分隔符结尾（MSI 官方规则），否则快捷方式 Target 拼接黏连成坏路径；
    // ②两条快捷方式必须显式带 Icon 引用（TaoZip/TaoDown 同款）；③开始菜单目录须带卸载清理；
    // ④主题菜单独立成右上角按钮（关于左侧），图标菜单不再含主题项；
    // ⑤图标菜单贴图标左缘防卡片溢出主窗，主题菜单右缘对齐防右溢。
    string ProjRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent!)
            if (File.Exists(Path.Combine(d.FullName, "Src", "TaoDBM.App", "MainWindow.xaml"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    var root = ProjRoot();
    // 二期拆分后安装包工程暂未纳入，wxs 存在才校验
    var wxsPath = Path.Combine(root, "Src", "Installer", "Package.wxs");
    if (File.Exists(wxsPath))
    {
        var wxs = File.ReadAllText(wxsPath);
    Check("目录属性赋值以分隔符结尾（快捷方式目标防黏连）", wxs.Contains("Value=\"D:\\Program Files\\TaoDBM\\\""));
    Check("两条快捷方式显式带安装包图标引用", System.Text.RegularExpressions.Regex.Matches(wxs, "Icon=\"TaoDBMIco\"").Count == 2);
    Check("开始菜单目录卸载清理在位", wxs.Contains("RemoveStartMenuDir"));
    }
    var mwXaml = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "MainWindow.xaml"));
    Check("主题菜单独立成钮且在关于左侧（全系勾选样式）",
        mwXaml.Contains("BtnTheme") && mwXaml.IndexOf("BtnTheme") < mwXaml.IndexOf("BtnAbout")
        && System.Text.RegularExpressions.Regex.Matches(mwXaml, "ToggleMenuFlyoutItem").Count == 3
        && !mwXaml.Contains("主题："));
    var mwCs2 = File.ReadAllText(Path.Combine(root, "Src", "TaoDBM.App", "MainWindow.xaml.cs"));
    Check("主题对勾随象刷新（切象同步勾选+启动初始化）",
        mwCs2.Contains("UpdateThemeChecks") && mwCs2.Contains("ThemeInk.IsChecked")
        && mwCs2.Contains("ThemeSky.IsChecked") && mwCs2.Contains("ThemeNight.IsChecked"));
    Check("菜单卡片贴边防溢出（图标左缘对齐/主题右缘对齐）",
        mwXaml.Contains("Placement=\"BottomEdgeAlignedLeft\"") && mwXaml.Contains("Placement=\"BottomEdgeAlignedRight\""));
}
{
    // 全测三件套之真人模拟操作链：拟人完整路径（切主题→查历史→收藏→会话恢复→退出重载）
    var dir = Path.Combine(Path.GetTempPath(), "taodbm-human-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    ThemeStore.UseFile(Path.Combine(dir, "theme.json"));
    Favorites.UseFile(Path.Combine(dir, "favorites.json"));
    TabSession.UseFile(Path.Combine(dir, "tabsession.json"));
    QueryHistory.UseFile(Path.Combine(dir, "history.json"));
    try
    {
        // ① 挑主题（三象各切一遍，退出前停在水墨）
        foreach (var th in ThemePalette.Names) ThemeStore.Save(th);
        Check("拟人：换装一轮后停在墨夜", ThemeStore.Load() == "night");
        ThemeStore.Save("ink");
        // ② 查历史（连续 5 条，最新在前）
        for (int i = 0; i < 5; i++) QueryHistory.Add("mysql", "db1", $"SELECT {i}");
        var hist = QueryHistory.Items;
        Check("拟人：历史最新在前且全在位", hist.Count >= 5 && hist[0].Sql == "SELECT 4");
        // ③ 收藏一条常用 SQL 并能找回
        var favs = Favorites.Load();
        Favorites.Save(Favorites.Add(favs, "常用-用户", "mysql", "db1", "SELECT * FROM users WHERE name LIKE '%tao%'"));
        Check("拟人：收藏往返", Favorites.Load().Any(f => f.Name == "常用-用户"));
        // ④ 会话恢复（两个标签）
        var tabs = new List<TabSessionItem> { new() { ConnectionId = "c1", Mode = "Query", Db = "db1", Sql = "SELECT 1" }, new() { ConnectionId = "c2", Mode = "Browse", Db = "db0", Child = "users" } };
        TabSession.Save(tabs);
        var back = TabSession.Load();
        Check("拟人：会话标签完整恢复", back.Count == 2 && back[0].ConnectionId == "c1" && back[1].Child == "users");
    }
    finally
    {
        ThemeStore.UseFile(""); Favorites.UseFile(""); TabSession.UseFile(""); QueryHistory.UseFile("");
        Directory.Delete(dir, true);
    }
}

Layer("暴力");

// A1 App 工程引用自洽（2026-11-01 TaoPad 真机 CS0103 避坑，全系同型）：
// 沙箱不编译 WinUI App 工程，界面侧编译错误只有真机能看见——
// 源码层兜底：App 工程所有 .cs 对 ThemePalette/ThemeStore 的静态成员引用必须有定义。
{
    string baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Src"));
    // 二期拆分后：tests/TaoDBM.LogicTest/bin/<cfg>/<tfm> 需上跳五级到解决方案根（旧布局为四级）；
    // 定义在 Infrastructure，被扫描的是 App 工程（沙箱不编译 WinUI，源码层兜底）
    var defs = File.ReadAllText(Path.Combine(baseDir, "TaoDBM.Infrastructure", "ThemePalette.cs"))
        + File.ReadAllText(Path.Combine(baseDir, "TaoDBM.Infrastructure", "ThemeStore.cs"));
    var orphans = new List<string>();
    foreach (var f in Directory.GetFiles(Path.Combine(baseDir, "TaoDBM.App"), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 && !f.EndsWith("ThemePalette.cs") && !f.EndsWith("ThemeStore.cs")))
    {
        var src = File.ReadAllText(f);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(src, @"(?:ThemePalette|ThemeStore)\.(\w+)"))
            if (!System.Text.RegularExpressions.Regex.IsMatch(defs, @"\b" + m.Groups[1].Value + @"\b"))
                orphans.Add($"{Path.GetFileName(f)}:{m.Groups[1].Value}");
    }
    Check("A1 App工程引用自洽：ThemePalette/ThemeStore 成员引用皆有定义",
        orphans.Count == 0, orphans.Count > 0 ? $"孤儿引用 {string.Join(",", orphans)}" : "自洽");
}

{
    // 模糊输入 1 万条 × 全部解析入口，全都不允许抛异常
    var rnd = new Random(20261101);
    string Nasty()
    {
        var pool = "'\"; --/*\n { } []() {},:0aA#\u4e2d\u6587\ud83d\ude00\t\\x00', ' OR 1=1--, DROP TABLE t; {}{{ }}[, ]";
        var n = rnd.Next(1, 40);
        var cs = new char[n];
        for (int i = 0; i < n; i++) cs[i] = pool[rnd.Next(pool.Length)];
        return new string(cs);
    }
    int exceptions = 0;
    for (int i = 0; i < 10000; i++)
    {
        var s = Nasty();
        try { SqlSplitter.Split(s); } catch { exceptions++; }
        try { TnsNames.Parse(s); } catch { exceptions++; }
        try { CsvImporter.Parse(s); } catch { exceptions++; }
        try { JsonImporter.Parse(s); } catch { exceptions++; }
        try { SqlHighlighter.Tokenize(s); } catch { exceptions++; }
    }
    Check("1 万条模糊输入全入口零异常", exceptions == 0, $"异常 {exceptions} 次");
    var inject = SqlSplitter.Split("'; DROP TABLE users; --");
    Check("注入载荷不产生可执行边界", inject.All(x => x.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || !x.Contains("DROP", StringComparison.OrdinalIgnoreCase)) || inject.Count <= 2, string.Join("|", inject));
}
{
    // 并发压测：8 线程 × 500 次，四个解析入口并行无异常无串扰
    int errs = 0;
    Parallel.For(0, 8, _ =>
    {
        var rnd = new Random(Environment.CurrentManagedThreadId);
        string N() { var pool = "';\"/*\n{}[]:,"; var n = rnd.Next(1, 30); var cs = new char[n]; for (int i = 0; i < n; i++) cs[i] = pool[rnd.Next(pool.Length)]; return new string(cs); }
        for (int i = 0; i < 500; i++)
        {
            var s = N();
            try { SqlSplitter.Split(s); } catch { Interlocked.Increment(ref errs); }
            try { CsvImporter.Parse(s); } catch { Interlocked.Increment(ref errs); }
            try { TnsNames.Parse(s); } catch { Interlocked.Increment(ref errs); }
        }
    });
    Check("8 线程并发解析零异常", errs == 0, $"异常 {errs} 次");
}
{
    // 模糊主题文件 2000 次：Load 永不抛、恒返回合法主题名
    var dir = Path.Combine(Path.GetTempPath(), "taodbm-themefuzz-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    ThemeStore.UseFile(Path.Combine(dir, "theme.json"));
    var rnd = new Random(77);
    int bad = 0;
    for (int i = 0; i < 2000; i++)
    {
        var bytes = new byte[rnd.Next(0, 40)];
        rnd.NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(dir, "theme.json"), bytes);
        try
        {
            var v = ThemeStore.Load();
            if (v != "ink" && v != "gray" && v != "night") bad++;
        }
        catch { bad++; }
    }
    Check("2000 条模糊主题文件零异常", bad == 0, $"异常 {bad} 次");
    ThemeStore.UseFile("");   // 恢复默认路径，避免影响后续
    Directory.Delete(dir, true);
}


// ================= 拟真数据（v1.4.0 全测；v1.4.2 多表化） =================
Console.WriteLine("== Faker 语义推断 ==");
{
    var cases = new (string name, string type, SemanticKind kind)[]
    {
        ("id", "BIGINT", SemanticKind.Id), ("user_id", "INT", SemanticKind.Id),
        ("order_guid", "CHAR(36)", SemanticKind.Guid), ("order_no", "VARCHAR(32)", SemanticKind.OrderNo),
        ("user_name", "VARCHAR(50)", SemanticKind.UserName), ("nickname", "VARCHAR(50)", SemanticKind.NickName),
        ("real_name", "VARCHAR(20)", SemanticKind.PersonName), ("email", "VARCHAR(64)", SemanticKind.Email),
        ("mobile_phone", "VARCHAR(20)", SemanticKind.Phone), ("id_card_no", "VARCHAR(18)", SemanticKind.IdCard),
        ("passwd", "VARCHAR(64)", SemanticKind.PasswordHash), ("access_token", "VARCHAR(128)", SemanticKind.Token),
        ("avatar_url", "VARCHAR(255)", SemanticKind.ImageUrl), ("home_page", "VARCHAR(255)", SemanticKind.Url),
        ("client_ip", "VARCHAR(15)", SemanticKind.Ip), ("listen_port", "INT", SemanticKind.Port),
        ("age", "INT", SemanticKind.Age), ("gender", "ENUM('男','女')", SemanticKind.Gender),
        ("total_amount", "DECIMAL(10,2)", SemanticKind.Money), ("unit_price", "DECIMAL(8,2)", SemanticKind.Price),
        ("stock_num", "INT", SemanticKind.Quantity), ("pay_rate", "DECIMAL(5,2)", SemanticKind.Percent),
        ("order_status", "TINYINT", SemanticKind.Status), ("member_type", "VARCHAR(20)", SemanticKind.Category),
        ("tags", "VARCHAR(100)", SemanticKind.Tag), ("article_title", "VARCHAR(120)", SemanticKind.Title),
        ("content", "TEXT", SemanticKind.ContentText), ("remark", "VARCHAR(255)", SemanticKind.Remark),
        ("address_detail", "VARCHAR(200)", SemanticKind.Address), ("city_name", "VARCHAR(30)", SemanticKind.City),
        ("province_name", "VARCHAR(30)", SemanticKind.Province), ("company_name", "VARCHAR(80)", SemanticKind.CompanyName),
        ("birthday", "DATE", SemanticKind.Birthday), ("created_at", "DATETIME", SemanticKind.DateTime),
        ("deleted", "TINYINT(1)", SemanticKind.Boolean), ("is_enabled", "BIT", SemanticKind.Boolean),
        ("color_hex", "CHAR(7)", SemanticKind.ColorCode), ("job_title", "VARCHAR(40)", SemanticKind.Job),
        ("education_level", "VARCHAR(20)", SemanticKind.Education), ("version_code", "VARCHAR(20)", SemanticKind.Version),
        ("lang", "VARCHAR(10)", SemanticKind.Language), ("tag_json", "JSON", SemanticKind.Json),
    };
    int wrong = 0;
    foreach (var (nm, ty, k) in cases)
        if (Faker.InferSemantic(nm, ty) != k) { wrong++; Console.WriteLine($"    · {nm}({ty}) → {Faker.InferSemantic(nm, ty)} 期望 {k}"); }
    Check($"语义推断 {cases.Length} 例全中", wrong == 0, $"错 {wrong} 例");
}
{
    Check("长度解析", Faker.ParseLen("VARCHAR(50)") == 50 && Faker.ParseLen("CHAR(36)") == 36 && Faker.ParseLen("TEXT") == 0);
    Check("精度解析", Faker.ParsePrecision("DECIMAL(10,2)") == (10, 2) && Faker.ParsePrecision("INT") == (0, 0));
    Check("枚举解析", string.Join("|", Faker.ParseEnumValues("ENUM('男','女','未知')")) == "男|女|未知");
    Check("类型判断", Faker.IsNumericType("BIGINT") && Faker.IsDateType("DATETIME") && Faker.IsTextType("NVARCHAR(50)") &&
        Faker.IsUuidType("UUID") && Faker.IsEnumType("SET('a','b')") && Faker.IsTinyInt("BIT"));
}
{
    // 各槽位生成值类型合法 + 确定性
    var col = new ColumnCtx { Name = "email", RawType = "VARCHAR(64)", Nullable = false, MaxLen = 64 };
    var r1 = new Random(42);
    var v1 = Faker.Gen(Faker.InferSemantic("email", "VARCHAR(64)"), col, r1, 1);
    var r2 = new Random(42);
    var v2 = Faker.Gen(Faker.InferSemantic("email", "VARCHAR(64)"), col, r2, 1);
    Check("同 seed 同值", v1 as string == v2 as string, $"{v1}|{v2}");
    var vMail = v1 as string ?? "";
    Check("邮箱合法", vMail.Contains('@') && vMail.Contains('.') && vMail.Length <= 64, vMail);

    var moneyCol = new ColumnCtx { Name = "amount", RawType = "DECIMAL(10,2)", Nullable = false, Scale = 2 };
    var mv = Faker.Gen(SemanticKind.Money, moneyCol, new Random(7), 1);
    Check("金额两位小数", mv is decimal d && decimal.Round(d, 2) == d, mv?.ToString() ?? "");
    var dtCol = new ColumnCtx { Name = "created_at", RawType = "DATETIME", Nullable = false };
    var dv = Faker.Gen(SemanticKind.DateTime, dtCol, new Random(9), 1) as string ?? "";
    Check("日期格式", System.Text.RegularExpressions.Regex.IsMatch(dv, @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$"), dv);
    var bCol = new ColumnCtx { Name = "is_enabled", RawType = "TINYINT(1)", Nullable = false };
    var bv = Faker.Gen(SemanticKind.Boolean, bCol, new Random(3), 1);
    Check("布尔 0/1", bv is int i && i is 0 or 1, bv?.ToString() ?? "");
    var eCol = new ColumnCtx { Name = "gender", RawType = "ENUM('男','女')", Nullable = false, EnumValues = Faker.ParseEnumValues("ENUM('男','女')") };
    var ev = Faker.Gen(SemanticKind.Gender, eCol, new Random(5), 1) as string ?? "";
    Check("枚举取值合法", ev is "男" or "女", ev);
    var phoneCol = new ColumnCtx { Name = "phone", RawType = "VARCHAR(11)", Nullable = false, MaxLen = 11 };
    var pv = Faker.Gen(SemanticKind.Phone, phoneCol, new Random(11), 1) as string ?? "";
    Check("手机号 11 位", System.Text.RegularExpressions.Regex.IsMatch(pv, @"^1[3-9]\d{9}$"), pv);
    var nameCol = new ColumnCtx { Name = "real_name", RawType = "VARCHAR(20)", Nullable = false, MaxLen = 20 };
    var nv = Faker.Gen(SemanticKind.PersonName, nameCol, new Random(13), 1) as string ?? "";
    Check("中文姓名", nv.Length >= 2 && nv.Length <= 20, nv);
    var nullableCol = new ColumnCtx { Name = "remark", RawType = "VARCHAR(255)", Nullable = true };
    var rndN = new Random(21);
    bool sawNull = false;
    for (long row = 1; row <= 14; row++)
        if (Faker.Gen(SemanticKind.Remark, nullableCol, rndN, row) is null) { sawNull = true; break; }
    Check("可空列稀疏 NULL", sawNull);
    var notNullCol = new ColumnCtx { Name = "remark", RawType = "VARCHAR(255)", Nullable = false };
    rndN = new Random(21);
    bool anyNull = Enumerable.Range(1, 14).Any(row => Faker.Gen(SemanticKind.Remark, notNullCol, rndN, row) is null);
    Check("非空列不出 NULL", !anyNull);
    var poolCol = new ColumnCtx { Name = "user_id", RawType = "BIGINT", Nullable = false, IsPrimaryKey = false };
    var pv2 = Faker.Gen(SemanticKind.Id, poolCol, new Random(31), 1, poolValue: "88");
    Check("关联列取池值", pv2 as string == "88", pv2?.ToString() ?? "");
}
{
    // 万行零异常 + 断言规模（暴力）
    var cols = new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "BIGINT", IsKey = true },
        new() { Name = "real_name", DataType = "VARCHAR(20)" },
        new() { Name = "email", DataType = "VARCHAR(64)" },
        new() { Name = "mobile", DataType = "VARCHAR(11)" },
        new() { Name = "total_amount", DataType = "DECIMAL(10,2)" },
        new() { Name = "created_at", DataType = "DATETIME" },
        new() { Name = "is_enabled", DataType = "TINYINT(1)" },
        new() { Name = "remark", DataType = "VARCHAR(255)" },
        new() { Name = "city_name", DataType = "VARCHAR(30)" },
        new() { Name = "gender", DataType = "ENUM('男','女')" },
    };
    var table = new SeedTable { Name = "orders", Columns = cols, Rows = 10000 };
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var rows = SeedPlan.GenerateRows(DbEngine.MySQL, table, new List<SeedEdge>(), new(), 2026);
    sw.Stop();
    Check("万行生成零异常", rows.Count == 10000 && rows.All(r => r.Count == cols.Count), $"{rows.Count} 行 {sw.ElapsedMilliseconds} ms");
    var ids = rows.Select(r => r[0]).Cast<long>().ToList();
    Check("PK 唯一递增", ids.Count == 10000 && ids.Distinct().Count() == 10000 && ids[0] == 1 && ids[9999] == 10000);
    var emails = rows.Select(r => r[2] as string).OfType<string>().ToList();
    Check("万行邮箱全合法", emails.Count > 0 && emails.All(e => e.Contains('@') && e.Length <= 64));
    Check("枚举列全合法", rows.All(r => r[9] == null || r[9]!.ToString() is "男" or "女"));
    Check("确定性重放", SeedPlan.GenerateRows(DbEngine.MySQL, table, new(), new(), 2026)
        .Zip(rows, (a, b) => a.Select((x, i) => (x?.ToString()) == (b[i]?.ToString())).All(y => y)).All(x => x));
    Check("不同 seed 不同值", SeedPlan.GenerateRows(DbEngine.MySQL, table, new(), new(), 2027)[1][2]!.ToString() != rows[1][2]!.ToString());
}

Console.WriteLine("== SeedPlan 关联与脚本 ==");
{
    var users = new SeedTable
    {
        Name = "users",
        Columns = new() { new ColumnSpec { Name = "id", DataType = "BIGINT", IsKey = true }, new ColumnSpec { Name = "real_name", DataType = "VARCHAR(20)" } },
    };
    var orders = new SeedTable
    {
        Name = "orders",
        Columns = new()
        {
            new ColumnSpec { Name = "id", DataType = "BIGINT", IsKey = true },
            new ColumnSpec { Name = "user_id", DataType = "BIGINT" },
            new ColumnSpec { Name = "total_amount", DataType = "DECIMAL(10,2)" },
        },
    };
    var items = new SeedTable
    {
        Name = "order_items",
        Columns = new()
        {
            new ColumnSpec { Name = "id", DataType = "BIGINT", IsKey = true },
            new ColumnSpec { Name = "order_id", DataType = "BIGINT" },
            new ColumnSpec { Name = "product_id", DataType = "BIGINT" },
        },
    };
    var tables = new List<SeedTable> { items, orders, users };
    var edges = SeedPlan.BuildRelations(tables, null);
    Check("字段名关联", edges.Count == 2 && edges.Any(e => e.FromTable == "orders" && e.FromCol == "user_id" && e.ToTable == "users") &&
        edges.Any(e => e.FromTable == "order_items" && e.FromCol == "order_id" && e.ToTable == "orders"),
        string.Join(";", edges.Select(e => $"{e.FromTable}.{e.FromCol}->{e.ToTable}")));
    Check("自引用跳过", !SeedPlan.BuildRelations(new List<SeedTable> { users }, null)
        .Any(e => e.FromTable == "users" && e.ToTable == "users"));
    var order = SeedPlan.TopoOrder(tables, edges);
    Check("拓扑：被引用表先", order[0].Name == "users" && order[1].Name == "orders" && order[2].Name == "order_items",
        string.Join("→", order.Select(t => t.Name)));
    Check("环不炸", SeedPlan.TopoOrder(tables, new List<SeedEdge> { new() { FromTable = "users", FromCol = "id", ToTable = "orders", ToCol = "id" }, new() { FromTable = "orders", FromCol = "id", ToTable = "users", ToCol = "id" } }).Count == 3);

    // == 多表生成计划（v1.4.2：每表独立行数，拓扑序执行） ==
    {
        var plan = SeedPlan.PlanFromCounts(new Dictionary<string, int> { ["users"] = 100, ["orders"] = 0, ["order_items"] = 20 },
            tables, edges);
        Check("多表计划：零行剔除、拓扑先行",
            plan.Count == 2 && plan[0].Table.Name == "users" && plan[1].Table.Name == "order_items",
            string.Join("→", plan.Select(p => $"{p.Table.Name}×{p.Count}")));
        Check("多表计划：行数逐表独立", plan[0].Count == 100 && plan[1].Count == 20);
        var all = SeedPlan.PlanFromCounts(new Dictionary<string, int> { ["users"] = 10, ["orders"] = 5, ["order_items"] = 3 },
            tables, edges);
        Check("多表计划：全表拓扑序", all.Select(p => p.Table.Name).SequenceEqual(new[] { "users", "orders", "order_items" }),
            string.Join(",", all.Select(p => p.Table.Name)));
        Check("多表计划：无输入为空", SeedPlan.PlanFromCounts(new Dictionary<string, int>(), tables, edges).Count == 0);
        // 多表生成确定性：同库同表同参必同值（多表计划复用单表生成器）
        var poolUsers = new Dictionary<string, Dictionary<string, List<object?>>> { ["users"] = new() { ["id"] = new List<object?> { 1, 2, 3 } } };
        var g1 = SeedPlan.GenerateRows(DbEngine.MySQL, all[1].Table, edges, poolUsers, 2026);
        var g2 = SeedPlan.GenerateRows(DbEngine.MySQL, all[1].Table, edges, poolUsers, 2026);
        Check("多表生成确定性", g1.Count == 5 && g1.Zip(g2, (a, b) => a.SequenceEqual(b)).All(x => x),
            g1.Count.ToString());
        // 引用列取值来自池（多表模式：被引用表先写入，子表引用列采样其值）
        Check("多表引用值来自池", g1.All(r => new object?[] { 1, 2, 3 }.Contains(r[1])), string.Join(",", g1.Select(r => r[1])));
    }

    // 生成：orders.user_id 从 users 值池采样
    var userRows = SeedPlan.GenerateRows(DbEngine.MySQL, users, edges, new(), 100);
    var pools = new Dictionary<string, Dictionary<string, List<object?>>> { ["users"] = SeedPlan.PoolFrom(DbEngine.MySQL, users, userRows) };
    var orderRows = SeedPlan.GenerateRows(DbEngine.MySQL, orders, edges, pools, 200);
    var userIds = orderRows.Select(r => r[1]).Cast<object>().Where(v => v is not null).Select(v => Convert.ToInt64(v)).ToList();
    Check("关联值来自池", userIds.Count > 0 && userIds.All(v => v >= 1 && v <= 100), string.Join(",", userIds.Take(5)));
}
{
    var cols = new List<ColumnSpec>
    {
        new() { Name = "id", DataType = "INT", IsKey = true },
        new() { Name = "name", DataType = "VARCHAR(20)" },
        new() { Name = "note", DataType = "VARCHAR(50)" },
    };
    var rows = new List<List<object?>>
    {
        new() { 1, "张三", "他说:'你好'" },
        new() { 2, null, null },
    };
    var my = SeedPlan.BuildInsertScripts(DbEngine.MySQL, "t_users", cols, rows);
    Check("MySQL 方言", my.Count == 1 && my[0].Contains("INSERT INTO `t_users` (`id`, `name`, `note`) VALUES") &&
        my[0].Contains("'张三'") && my[0].Contains("'他说:''你好'''") && my[0].Contains("NULL"), my[0]);
    var pg = SeedPlan.BuildInsertScripts(DbEngine.PostgreSQL, "t users", cols, rows);
    Check("PG 双引号", pg[0].Contains("INSERT INTO \"t users\" (\"id\", \"name\", \"note\")"), pg[0]);
    var ss = SeedPlan.BuildInsertScripts(DbEngine.SqlServer, "t", cols, rows);
    Check("SQL Server N 前缀", ss[0].Contains("N'张三'"), ss[0]);
    var lite = SeedPlan.BuildInsertScripts(DbEngine.SQLite, "t", cols, rows);
    Check("SQLite 方言", lite[0].Contains("INSERT INTO \"t\" (\"id\", \"name\", \"note\")"), lite[0]);
    Check("Oracle 裸值", SeedPlan.BuildInsertScripts(DbEngine.Oracle, "T", cols, rows)[0].Contains("'张三'"));
    // 批 500
    var big = new List<List<object?>>();
    for (int i = 0; i < 1201; i++) big.Add(new List<object?> { i, "n", null });
    var scripts = SeedPlan.BuildInsertScripts(DbEngine.MySQL, "t", cols, big);
    Check("分批 500", scripts.Count == 3, scripts.Count.ToString());
    Check("批首 500 行", System.Text.RegularExpressions.Regex.Matches(scripts[0], "\n\\(").Count == 500);
    Check("空列/空行不产出", SeedPlan.BuildInsertScripts(DbEngine.MySQL, "t", new(), rows).Count == 0 &&
        SeedPlan.BuildInsertScripts(DbEngine.MySQL, "t", cols, new()).Count == 0);
}
{
    Check("分页方言", SeedPlan.SelectTopSql(DbEngine.SqlServer, "t", "id", 100).Contains("TOP (100)") &&
        SeedPlan.SelectTopSql(DbEngine.Oracle, "t", "id", 100).Contains("ROWNUM <= 100") &&
        SeedPlan.SelectTopSql(DbEngine.MySQL, "t", "id", 100).EndsWith("LIMIT 100"));
    Check("外键发现 SQL", SeedPlan.ForeignKeySql(DbEngine.MySQL, "shop", "orders")!.Contains("KEY_COLUMN_USAGE") &&
        SeedPlan.ForeignKeySql(DbEngine.SqlServer, "shop", "orders")!.Contains("sys.foreign_keys") &&
        SeedPlan.ForeignKeySql(DbEngine.Oracle, "shop", "orders")!.Contains("all_constraints") &&
        SeedPlan.ForeignKeySql(DbEngine.MongoDB, "shop", "orders") is null);
    Check("SQLite PRAGMA", SeedPlan.SqliteForeignKeySql("orders").Contains("foreign_key_list"));
    Check("引号转义", SeedPlan.QuoteIdent(DbEngine.MySQL, "we`ird") == "`we``ird`" &&
        SeedPlan.QuoteIdent(DbEngine.PostgreSQL, "a\"b") == "\"a\"\"b\"" &&
        SeedPlan.QuoteIdent(DbEngine.SqlServer, "a]b") == "[a]]b]");
}


Layer("万链");
{
    // 万链归账（万级补则）：万链并行走核心纯函数域，链内不写输出，Interlocked 归账终局断言。
    // 四个领域：SQL 分词区间不变量 / SqlSplitter 分段非空 / CSV 导出解析往返 / ER 模型 JSON 往返。
    const int ChainCount = 10000, StepsPerChain = 30;
    long totalAsserts = 0, violations = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var valuePool = new[] { "1", "42", "abc", "张三", "x y", "3.5", "true", "2026-01-01", "", "O'Brien" };
    var tokPool = new[] { "SELECT", "id", "FROM", "`t`", "'a;b'", "--c\n", "/*x*/", ";", "1.5e-3", "中", "\"i\"", ",", "(", ")", "WHERE", "x=", "0x1F" };
    Parallel.For(0, ChainCount, chain =>
    {
        var rnd = new Random(20261101 + chain);   // 每链独立种子：确定性不因并行改变，失败可复现
        long a = 0, v = 0;
        for (int step = 0; step < StepsPerChain; step++)
        {
            switch (rnd.Next(4))
            {
                case 0:   // SQL 分词：区间界内、逐段不越原文
                {
                    var sb = new StringBuilder();
                    int n = rnd.Next(0, 30);
                    for (int i = 0; i < n; i++) sb.Append(tokPool[rnd.Next(tokPool.Length)]);
                    var sql = sb.ToString();
                    try
                    {
                        var toks = SqlHighlighter.Tokenize(sql);
                        int lastEnd = 0;
                        foreach (var t in toks)
                        {
                            if (t.Start < lastEnd || t.Start < 0 || t.Length <= 0 || t.Start + t.Length > sql.Length) v++;
                            a++;
                            lastEnd = Math.Max(lastEnd, t.Start + t.Length);
                        }
                    }
                    catch { v++; a++; }
                    break;
                }
                case 1:   // SqlSplitter：每段非空、永不抛
                {
                    var sb = new StringBuilder();
                    int n = rnd.Next(1, 8);
                    for (int i = 0; i < n; i++) { sb.Append("SELECT ").Append(rnd.Next(1000)); if (i < n - 1) sb.Append(';'); }
                    try
                    {
                        var parts = SqlSplitter.Split(sb.ToString());
                        if (parts.Any(p => string.IsNullOrWhiteSpace(p))) v++;
                        a++;
                    }
                    catch { v++; a++; }
                    break;
                }
                case 2:   // CSV 往返：导出→解析，行列一致、值还原（空串归 null 口径）
                {
                    var r = new QueryResult { Columns = { "c1", "c2", "c3" } };
                    var vals = new object?[3];
                    for (int i = 0; i < 3; i++)
                        vals[i] = rnd.Next(10) == 0 ? DBNull.Value : valuePool[rnd.Next(valuePool.Length)];
                    r.Rows.Add(vals);
                    try
                    {
                        var (heads, rows) = CsvImporter.Parse(Exporter.Csv(r));
                        bool ok = heads.SequenceEqual(r.Columns) && rows.Count == 1;
                        if (ok)
                            for (int i = 0; i < 3; i++)
                            {
                                // CSV 口径：DBNull/null/空串 输出为空单元格，还原一律为 null
                                bool eq = vals[i] is DBNull or null or ""
                                    ? rows[0][i] is null
                                    : rows[0][i] == (string?)vals[i];
                                if (!eq) { ok = false; break; }
                            }
                        if (!ok) v++;
                        a++;
                    }
                    catch { v++; a++; }
                    break;
                }
                case 3:   // ER 模型：JSON 往返表列全等
                {
                    var m = new ErdModel();
                    int tableN = rnd.Next(1, 5);
                    for (int ti = 0; ti < tableN; ti++)
                    {
                        var tab = new ErdTable { Name = $"t{ti}_{rnd.Next(100)}" };
                        int colN = rnd.Next(1, 6);
                        for (int ci = 0; ci < colN; ci++)
                            tab.Columns.Add(new ErdColumn { Name = $"c{ci}", Type = rnd.Next(2) == 0 ? "int" : "varchar(50)", IsKey = ci == 0 });
                        m.Tables.Add(tab);
                    }
                    try
                    {
                        var back = ErdModel.FromJson(m.ToJson());
                        if (back is null || back.Tables.Count != tableN
                            || !back.Tables.Zip(m.Tables, (b, o) => b.Name == o.Name && b.Columns.Count == o.Columns.Count).All(x => x)) v++;
                        a++;
                    }
                    catch { v++; a++; }
                    break;
                }
            }
        }
        Interlocked.Add(ref totalAsserts, a);
        Interlocked.Add(ref violations, v);
    });
    sw.Stop();
    Check($"万链归账：{ChainCount} 链 × {StepsPerChain} 步零违规",
        violations == 0 && totalAsserts >= ChainCount,
        $"断言 {totalAsserts} 条，违规 {violations}，耗时 {sw.Elapsed.TotalSeconds:0.0}s");
    Console.WriteLine($"  （万链归账：{ChainCount} 链 / {ChainCount * StepsPerChain} 步 / 断言 {totalAsserts} 条 / 违规 {violations} / 耗时 {sw.Elapsed.TotalSeconds:0.0}s）");

Console.WriteLine("== AtomicWrite 原子落盘（三.6 断电模拟）==");
{
    var dir = Path.Combine(Path.GetTempPath(), "taodbm-atw-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var f = Path.Combine(dir, "cfg.json");
    // 正常写：目标完整、tmp 消失（已原子 move 而非残留）
    AtomicWrite.Text(f, "{\"v\":1}");
    Check("原子写：目标完整且无残留tmp", System.IO.File.ReadAllText(f) == "{\"v\":1}" && !System.IO.File.Exists(f + ".tmp"),
        System.IO.File.Exists(f + ".tmp") ? "tmp 残留" : "");
    // 覆盖写：旧版被新版完整替换，中途绝不出现半截目标
    AtomicWrite.Text(f, "{\"v\":2}");
    Check("原子覆盖：新版完整替换", System.IO.File.ReadAllText(f) == "{\"v\":2}" && !System.IO.File.Exists(f + ".tmp"));
    // 断电模拟：写 tmp 后、move 前进程死亡 → 目标仍是完整旧版（不清零）
    var f2 = Path.Combine(dir, "conn.json");
    AtomicWrite.Text(f2, "{\"good\":true}");
    System.IO.File.WriteAllText(f2 + ".tmp", "{\"half\":");   // 模拟断电时半截 tmp
    Check("断电模拟：目标保留完整旧版", System.IO.File.ReadAllText(f2) == "{\"good\":true}",
        "目标被半截污染");
    // 断电后恢复：残留半截 tmp 不污染下次写入，且被收敛
    AtomicWrite.Text(f2, "{\"good\":false}");
    Check("断电恢复：残留tmp被收敛且目标完整", System.IO.File.ReadAllText(f2) == "{\"good\":false}" && !System.IO.File.Exists(f2 + ".tmp"));
    // 六处落盘点源码级断言：Core 六个存储文件不得再直写目标文件（家法三.6 收敛零遗漏）
    {
        string[] six = ["AppPrefs.cs", "ConnectionStore.cs", "Favorites.cs", "QueryHistory.cs", "TabSession.cs", "ThemeStore.cs"];
        var root = AppContext.BaseDirectory;
        while (root != null && !System.IO.File.Exists(Path.Combine(root, "TaoDBM.slnx"))) root = Path.GetDirectoryName(root);
        var bad = six.Where(n => !System.IO.File.Exists(Path.Combine(root!, "Src", "TaoDBM.Infrastructure", n)) ||
            System.IO.File.ReadAllText(Path.Combine(root!, "Src", "TaoDBM.Infrastructure", n)).Contains("File.WriteAllText(File,"))
            .ToList();
        Check("六处落盘点统一走 AtomicWrite 零遗漏", bad.Count == 0, string.Join(",", bad));
    }
}

RealDbCheck.Run(Check);
}

Console.WriteLine("\n== 分层结果 ==");
foreach (var name in new[] { "单元", "集成", "系统", "暴力", "万链" })
    Console.WriteLine($"{name}: {layerPass.GetValueOrDefault(name)} / {layerPass.GetValueOrDefault(name) + layerFail.GetValueOrDefault(name)}");
Console.WriteLine($"\n通过 {pass} / {pass + fail}");
return fail == 0 ? 0 : 1;

// 测试用最小 Provider：复用 RelationalProvider 的 Quote 逻辑
class TestProv : TaoDBM.Core.RelationalProvider
{
    private readonly char _q;
    public TestProv(char q = '`') : base(new TaoDBM.Core.ConnectionProfile()) => _q = q;
    protected override System.Data.Common.DbConnection Open(string? db) => throw new NotSupportedException();
    protected override char OpenQuote => _q;
    protected override char CloseQuote => _q;
    public override bool SupportsBrowse => true;
    protected override string ListDatabasesSql() => "";
    protected override string ListChildrenSql(string db) => "";
    protected override string ListColumnsSql(string db, string table) => "";
    protected override string PagingClause(int offset, int limit) => "";
}
