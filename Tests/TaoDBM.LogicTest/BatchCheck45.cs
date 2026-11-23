using System.Text.Json;
using TaoDBM.Core;
using TaoDBM.Core.Streaming;
using TaoDBM.Infrastructure;

namespace TaoDBM.LogicTest;

/// <summary>批次四五纯逻辑用例（2026-11-23）：xlsx 读写往返、XML/HTML 良构与流式、备份 SQL 转换重放、
/// 备份账本、DDL 解析（列/主键/外键）、三层模型 JSON 往返、模型比较差异计数、模拟数据预设、
/// ERD 边符号 JSON 往返、数据字典索引外键段、ODBC 连接串拼装（纯字符串，不碰 P/Invoke）。
/// 独立文件接线（ComboMatrix 家法），不碰 Program.cs 主体。</summary>
public static class BatchCheck45
{
    /// <summary>带默认 detail 的检查委托（与 Program.Check 同形）。</summary>
    public delegate void CheckD(string name, bool ok, string detail = "");

    public static void RunAll(Action<string, bool, string> check)
    {
        CheckD d = (name, ok, detail) => check(name, ok, detail ?? "");
        Console.WriteLine("\n== 批次四五 纯逻辑用例（2026-11-23）==");
        RunExcel(d);
        RunXmlHtml(d);
        RunBackupToSql(d);
        RunBackupHistory(d);
        RunDdlParser(d);
        RunModelDoc(d);
        RunModelCompare(d);
        RunFakerPreset(d);
        RunErdSymbols(d);
        RunDataDictionary(d);
        RunOdbcConnStr(d);
    }

    static void RunExcel(CheckD d)
    {
        var path = Path.Combine(Path.GetTempPath(), $"taodbm-test-{Guid.NewGuid():N}.xlsx");
        try
        {
            var cols = new List<string> { "id", "name", "score", "ok", "memo" };
            var rows = new List<object?[]>
            {
                new object?[] { 1, "张三", 98.5, true, null },
                new object?[] { 2, "Li Si", 0, false, "plain" },
            };
            long n = ExcelWriter.Write(path, cols, rows);
            var (headers, back) = ExcelReader.Read(path);
            d("Excel.行数一致", n == 2 && back.Count == 2, $"write={n} read={back.Count}");
            d("Excel.表头一致", headers.Count == 5 && headers[0] == "id" && headers[4] == "memo", string.Join(",", headers));
            d("Excel.数字往返", Convert.ToString(back[0][0]) == "1" && Convert.ToString(back[0][2]) == "98.5",
                $"{back[0][0]}|{back[0][2]}");
            d("Excel.布尔往返", Convert.ToString(back[0][3]) == "1" || Convert.ToString(back[0][3])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true,
                Convert.ToString(back[0][3]) ?? "");
            d("Excel.NULL跳格", back[0][4] is null or "", Convert.ToString(back[0][4]) ?? "(null)");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    static void RunXmlHtml(CheckD d)
    {
        var qr = new QueryResult
        {
            Title = "t",
            Columns = new List<string> { "id", "val" },
            Rows = new List<object?[]> { new object?[] { 1, null }, new object?[] { 2, "<script>&" } },
        };
        var xml = XmlExporter.Write(qr);
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            d("Xml.良构解析", doc.Root is not null);
        }
        catch (Exception ex) { d("Xml.良构解析", false, ex.Message); return; }
        d("Xml.NULL写nil", xml.Contains("xsi:nil"), xml.Length.ToString());
        d("Xml.特殊字符转义", xml.Contains("&lt;script&gt;&amp;"), "");
        d("Xml.SafeName净化", XmlExporter.SafeName("1abc def") .Length > 0 && !char.IsDigit(XmlExporter.SafeName("1abc def")[0]), XmlExporter.SafeName("1abc def"));
        var html = HtmlExporter.Write(qr, "报表");
        d("Html.含表结构", html.Contains("<table") && html.Contains("id") && html.Contains("(NULL)"), html.Length.ToString());
        // 流式：两页拼接与整包一致（行数口径）
        var pages = new List<QueryPage>
        {
            new() { Rows = new object[][] { new object[] { 1, "a" } }, HasMore = true },
            new() { Rows = new object[][] { new object[] { 2, "b" } }, HasMore = false },
        };
        var cols = new List<string> { "id", "val" };
        using (var w = new StringWriter())
        {
            long n = XmlExporter.WriteStream(pages, cols, w);
            d("Xml.流式行数", n == 2, n.ToString());
        }
        using (var w = new StringWriter())
        {
            long n = HtmlExporter.WriteStream(pages, cols, "流式", w);
            d("Html.流式行数", n == 2, n.ToString());
        }
    }

    static void RunBackupToSql(CheckD d)
    {
        var dump = Path.Combine(Path.GetTempPath(), $"taodbm-dump-{Guid.NewGuid():N}.sql");
        var outf = Path.Combine(Path.GetTempPath(), $"taodbm-conv-{Guid.NewGuid():N}.sql");
        try
        {
            File.WriteAllLines(dump, new[]
            {
                "-- comment line",
                "SET NAMES utf8mb4;",
                "LOCK TABLES `t` WRITE;",
                "CREATE TABLE `t` (`id` int NOT NULL, `name` varchar(50), PRIMARY KEY (`id`));",
                "INSERT INTO `t` VALUES (1,'a');",
                "INSERT INTO `t` VALUES (2,'b');",
                "CREATE TABLE `u` (`x` int);",
                "INSERT INTO `u` VALUES (9);",
                "UNLOCK TABLES;",
            });
            var r = BackupToSql.ConvertFile(dump, outf);
            d("Backup.计数", r.Tables == 2 && r.Inserts == 3 && r.Statements == 5, $"tables={r.Tables} inserts={r.Inserts} stmts={r.Statements}");
            var outLines = File.ReadAllLines(outf);
            var joined = string.Join("\n", outLines);
            d("Backup.保留建表", joined.Contains("CREATE TABLE `t`") && joined.Contains("CREATE TABLE `u`"));
            d("Backup.保留插入原序", joined.IndexOf("1,'a'") < joined.IndexOf("2,'b'") && joined.IndexOf("2,'b'") < joined.IndexOf("(9)"), joined);
            d("Backup.丢弃杂句", !joined.Contains("SET NAMES") && !joined.Contains("LOCK TABLES") && !joined.Contains("UNLOCK"));
        }
        finally { try { File.Delete(dump); File.Delete(outf); } catch { } }
    }

    static void RunBackupHistory(CheckD d)
    {
        BackupHistory.Clear();
        BackupHistory.Add("MySQL", "db1", "备份", "f1.sql", true, 123, "ok");
        BackupHistory.Add("MongoDB", "db2", "还原", "f2", false, 0, "工具缺失");
        var all = BackupHistory.All();
        d("BackupHistory.最新在前", all.Count == 2 && all[0].File == "f2" && all[1].File == "f1.sql", string.Join(",", all.Select(x => x.File)));
        d("BackupHistory.字段完整", all[0].Engine == "MongoDB" && all[0].Db == "db2" && all[0].Kind == "还原" && !all[0].Ok && all[1].Ok && all[1].SizeBytes == 123, "");
        BackupHistory.Clear();
        d("BackupHistory.清空", BackupHistory.All().Count == 0);
    }

    static void RunDdlParser(CheckD d)
    {
        var sql = """
            CREATE TABLE dept (
              id int NOT NULL,
              name varchar(50),
              PRIMARY KEY (id)
            );
            CREATE TABLE emp (
              id int NOT NULL,
              dept_id int,
              PRIMARY KEY (id),
              FOREIGN KEY (dept_id) REFERENCES dept (id)
            );
            """;
        var ents = DdlParser.Parse(sql);
        d("Ddl.实体数", ents.Count == 2, ents.Count.ToString());
        var dept = ents.FirstOrDefault(e => e.Name == "dept");
        var emp = ents.FirstOrDefault(e => e.Name == "emp");
        d("Ddl.列与类型", dept is not null && dept.Columns.Count == 2 && dept.Columns[1].Type == "varchar(50)", dept?.Columns.Count.ToString() ?? "");
        d("Ddl.行内主键", dept is not null && dept.Columns[0].IsKey && !dept.Columns[1].IsKey);
        d("Ddl.表级主键", emp is not null && emp.Columns[0].IsKey);
        d("Ddl.外键解析", emp is not null && emp.ForeignKeys.Count == 1 && emp.ForeignKeys[0].Column == "dept_id"
            && emp.ForeignKeys[0].RefTable == "dept" && emp.ForeignKeys[0].RefColumn == "id",
            emp?.ForeignKeys.Count.ToString() ?? "");
    }

    static void RunModelDoc(CheckD d)
    {
        var doc = new DataModelDoc();
        doc.Conceptual.Tables.Add(new ErdTable { Name = "A", X = 1, Y = 2 });
        doc.Logical.Tables.Add(new ErdTable { Name = "B" });
        doc.Physical.Tables.Add(new ErdTable { Name = "C" });
        doc.Physical.Edges.Add(new ErdEdge { FromTable = "C", FromCol = "id", ToTable = "C", FromSymbol = ErdEdgeSymbol.CrowFoot, ToSymbol = ErdEdgeSymbol.DoubleLine });
        doc.Current = ModelLayer.Physical;
        var back = DataModelDoc.FromJson(doc.ToJson());
        d("Model.往返非空", back is not null);
        if (back is null) return;
        d("Model.三层各自独立", back.Conceptual.Tables.Count == 1 && back.Logical.Tables.Count == 1 && back.Physical.Tables.Count == 1,
            $"{back.Conceptual.Tables.Count}/{back.Logical.Tables.Count}/{back.Physical.Tables.Count}");
        d("Model.当前层保持", back.Current == ModelLayer.Physical);
        d("Model.边符号保持", back.Physical.Edges.Count == 1 && back.Physical.Edges[0].FromSymbol == ErdEdgeSymbol.CrowFoot
            && back.Physical.Edges[0].ToSymbol == ErdEdgeSymbol.DoubleLine);
        d("Model.Of取层", back.Of(ModelLayer.Conceptual).Tables[0].Name == "A" && back.Of(ModelLayer.Physical).Tables[0].Name == "C");
    }

    static void RunModelCompare(CheckD d)
    {
        var model = new ErdModel();
        model.Tables.Add(new ErdTable
        {
            Name = "t1",
            Columns = new List<ErdColumn> { new() { Name = "a", Type = "int", IsKey = true }, new() { Name = "b", Type = "varchar(10)" } },
        });
        model.Tables.Add(new ErdTable { Name = "t2", Columns = new List<ErdColumn> { new() { Name = "x", Type = "int" } } });
        var live = new List<(string, List<ColumnSpec>)>
        {
            ("t1", new List<ColumnSpec>
            {
                new ColumnSpec { Name = "a", DataType = "bigint", IsKey = true },   // 类型变更
                new ColumnSpec { Name = "b", DataType = "varchar(10)" },
            }),
            // t2 真库缺失 → table-missing
        };
        var diffs = ModelCompare.Diff(model, live);
        d("ModelCompare.差异计数", diffs.Count == 2, diffs.Count.ToString());
        d("ModelCompare.类型变更", diffs.Any(x => x.ToString().Contains("t1") && x.ToString().Contains("a")), string.Join("|", diffs));
        d("ModelCompare.缺表", diffs.Any(x => x.ToString().Contains("t2")), "");
        // 一致时零差异
        var live2 = new List<(string, List<ColumnSpec>)>
        {
            ("t1", new List<ColumnSpec>
            {
                new ColumnSpec { Name = "a", DataType = "int", IsKey = true },
                new ColumnSpec { Name = "b", DataType = "varchar(10)" },
            }),
            ("t2", new List<ColumnSpec> { new ColumnSpec { Name = "x", DataType = "int" } }),
        };
        d("ModelCompare.一致零差异", ModelCompare.Diff(model, live2).Count == 0);
    }

    static void RunFakerPreset(CheckD d)
    {
        var all = FakerPreset.All;
        d("Faker.四类模板", all.Length == 4, all.Length.ToString());
        // 每类模板一张命中表（表名含模式词）＋一张不命中表（logs）
        var expect = new Dictionary<string, (string Table, int Rows)>
        {
            ["name"] = ("user_info", 1000),
            ["phone"] = ("phone_contacts", 500),
            ["idcard"] = ("idcard_kyc", 200),
            ["address"] = ("address_region", 300),
        };
        foreach (var p in all)
        {
            var (table, rowsExpect) = expect[p.Key];
            var rows = p.RowsFor(new[] { table, "logs" });
            d($"Faker.预设命中[{p.Key}]", rows[table] == rowsExpect && rows["logs"] == 0,
                string.Join(",", rows.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        var none = all[0].RowsFor(new[] { "zzz" });
        d("Faker.未命中零行", none["zzz"] == 0);
    }

    static void RunErdSymbols(CheckD d)
    {
        var m = new ErdModel();
        m.Tables.Add(new ErdTable { Name = "P" });
        m.Tables.Add(new ErdTable { Name = "Q" });
        m.Edges.Add(new ErdEdge { FromTable = "P", FromCol = "pid", ToTable = "Q", FromSymbol = ErdEdgeSymbol.Circle, ToSymbol = ErdEdgeSymbol.DoubleLine });
        var back = ErdModel.FromJson(m.ToJson());
        d("Erd.符号往返", back is not null && back.Edges.Count == 1
            && back.Edges[0].FromSymbol == ErdEdgeSymbol.Circle && back.Edges[0].ToSymbol == ErdEdgeSymbol.DoubleLine,
            back?.Edges.Count.ToString() ?? "");
        // 默认推断：1:N 自动鸦爪＋双线
        var m2 = new ErdModel();
        m2.Tables.Add(new ErdTable { Name = "P", Columns = new List<ErdColumn> { new() { Name = "id", Type = "int", IsKey = true } } });
        m2.Tables.Add(new ErdTable { Name = "Q", Columns = new List<ErdColumn> { new() { Name = "pid", Type = "int" } } });
        var edges = ErdModel.InferEdges(m2);
        d("Erd.推断自动标注", edges.Count == 1 && edges[0].FromSymbol == ErdEdgeSymbol.CrowFoot && edges[0].ToSymbol == ErdEdgeSymbol.DoubleLine,
            string.Join(",", edges.Select(e => $"{e.FromSymbol}/{e.ToSymbol}")));
    }

    static void RunDataDictionary(CheckD d)
    {
        var t = DataDictionary.FromSpecs("emp", new List<ColumnSpec>
        {
            new ColumnSpec { Name = "id", DataType = "int", IsKey = true },
            new ColumnSpec { Name = "name", DataType = "varchar(50)" },
        });
        t.Indexes.Add(new DictionaryIndex { Table = "emp", Name = "idx_name", Unique = false });
        t.ForeignKeys.Add(new DictionaryForeignKey { Table = "emp", Column = "dept_id", RefTable = "dept", RefColumn = "id", Name = "fk_emp_dept" });
        var html = DataDictionary.ToHtml(new List<DictionaryTable> { t }, "字典");
        d("Dict.索引段", html.Contains("索引") && html.Contains("idx_name"), "");
        d("Dict.外键段", html.Contains("外键") && html.Contains("fk_emp_dept") && html.Contains("dept"), "");
        var word = DataDictionary.ToWord(new List<DictionaryTable> { t }, "字典");
        d("Dict.Word段", word.Contains("idx_name") && word.Contains("fk_emp_dept"), "");
    }

    static void RunOdbcConnStr(CheckD d)
    {
        // 纯字符串拼装，不碰 P/Invoke（Linux 上 odbc32 调用路径禁走）
        var p = new ConnectionProfile { Engine = DbEngine.Odbc, Database = "MyDsn", User = "u1", Password = "p1", Parameters = "Trusted_Connection=Yes;APP=TaoDBM" };
        var s = OdbcCore.BuildConnectionString(p);
        d("Odbc.连接串", s == "DSN=MyDsn;UID=u1;PWD=p1;Trusted_Connection=Yes;APP=TaoDBM", s);
        var s2 = OdbcCore.BuildConnectionString(p, "OtherDsn");
        d("Odbc.库覆盖DSN", s2.StartsWith("DSN=OtherDsn", StringComparison.Ordinal), s2);
        var p3 = new ConnectionProfile { Engine = DbEngine.Odbc, Database = "D" };
        d("Odbc.最小连接串", OdbcCore.BuildConnectionString(p3) == "DSN=D", "");
        d("Odbc.引擎注册", EngineInfo.All.Contains(DbEngine.Odbc), "");
    }
}
