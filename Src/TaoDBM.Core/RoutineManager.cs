using TaoDBM.Infrastructure;

namespace TaoDBM.Core;

/// <summary>档三：例程管理引擎——存储过程/函数/触发器的增删改查与执行语句生成、调试支持判定。</summary>
/// <remarks>
/// 对账漏码 §1（2026-11-19 对账报告）：原型排第 1 位，本引擎按 ObjectTreeCatalog 既有口径接入。
/// 目录浏览与定义读取复用 ObjectTreeCatalog；本类补 DROP / 执行 / 校验 / 引用查找 / 调试判定。
/// 断点调试范围：2026-11-19 拍板 v2.0 仅 MySQL，其余引擎入口灰置「v2.0 暂不支持」。
/// 含分号的例程体（BEGIN…END）不得走 SqlSplitter 脚本拆分，须经 RelationalProvider.ExecuteSingle 单语句执行。
/// </remarks>

public enum RoutineKind { Procedure, Function, Trigger }

public static class RoutineManager
{
    /// <summary>断点调试支持：2026-11-19 拍板 v2.0 仅 MySQL。</summary>
    public static bool DebugSupported(DbEngine e) => e == DbEngine.MySQL;

    /// <summary>不支持引擎的调试入口灰置文案（拍板口径）。</summary>
    public static string DebugUnsupportedText => "v2.0 暂不支持";

    /// <summary>校验例程体：非空、以 CREATE 开头、类型前缀匹配、禁止 DELIMITER（程序按单语句直执）。</summary>
    public static List<string> Validate(DbEngine e, RoutineKind kind, string body)
    {
        var errs = new List<string>();
        var text = (body ?? "").Trim();
        if (text.Length == 0) { errs.Add("例程体为空"); return errs; }
        var upper = text.ToUpperInvariant();
        if (!upper.StartsWith("CREATE", StringComparison.Ordinal)) errs.Add("必须以 CREATE 开头");
        var want = kind switch
        {
            RoutineKind.Procedure => "PROCEDURE",
            RoutineKind.Function => "FUNCTION",
            _ => "TRIGGER",
        };
        var head = upper.Replace("CREATE OR REPLACE", "CREATE").TrimStart();
        if (head.StartsWith("CREATE", StringComparison.Ordinal) && !head.Contains(want, StringComparison.Ordinal))
            errs.Add($"类型前缀不匹配：期望 {want}");
        if (upper.Contains("DELIMITER", StringComparison.Ordinal))
            errs.Add("请勿在例程体内使用 DELIMITER，本程序按单语句直接执行，直接以 END 结束即可");
        return errs;
    }

    /// <summary>生成 DROP 语句；PG/SQLite 删触发器需 onTable（PG 语法要求 ON 表名）。</summary>
    public static string BuildDropSql(DbEngine e, RoutineKind kind, string name, string? schema = null, string? onTable = null)
    {
        if (e == DbEngine.MySQL)
        {
            var head = kind switch
            {
                RoutineKind.Procedure => "DROP PROCEDURE ",
                RoutineKind.Function => "DROP FUNCTION ",
                _ => "DROP TRIGGER ",
            };
            return head + (schema != null && schema.Length > 0
                ? "`" + Esc(schema) + "`.`" + Esc(name) + "`"
                : "`" + Esc(name) + "`");
        }
        if (e == DbEngine.PostgreSQL)
        {
            if (kind == RoutineKind.Trigger)
            {
                if (string.IsNullOrEmpty(onTable)) throw new ArgumentException("PostgreSQL 删触发器需要表名（ON table）");
                return $"DROP TRIGGER \"{name.Replace("\"", "\"\"")}\" ON \"{onTable.Replace("\"", "\"\"")}\"";
            }
            var head = kind == RoutineKind.Procedure ? "DROP PROCEDURE " : "DROP FUNCTION ";
            return head + "\"" + name.Replace("\"", "\"\"") + "\"";
        }
        if (e == DbEngine.SqlServer)
        {
            var head = kind switch
            {
                RoutineKind.Procedure => "DROP PROCEDURE ",
                RoutineKind.Function => "DROP FUNCTION ",
                _ => "DROP TRIGGER ",
            };
            return head + "[" + name.Replace("]", "]]") + "]";
        }
        if (e == DbEngine.SQLite)
        {
            if (kind != RoutineKind.Trigger) throw new NotSupportedException("SQLite 不支持存储过程与函数");
            return "DROP TRIGGER \"" + name.Replace("\"", "\"\"") + "\"";
        }
        throw new NotSupportedException("该引擎不支持例程删除");
    }

    /// <summary>生成执行语句：过程 CALL/EXEC，函数 SELECT；触发器由数据事件触发，不能手动执行。</summary>
    public static string BuildExecuteSql(DbEngine e, RoutineKind kind, string name, string args = "")
    {
        if (kind == RoutineKind.Trigger)
            throw new NotSupportedException("触发器由数据事件触发，不能手动执行；如需验证请对相关表执行变更");
        var call = $"({args})";
        return (e, kind) switch
        {
            (DbEngine.MySQL, RoutineKind.Procedure) => $"CALL `{name.Replace("`", "``")}`{call}",
            (DbEngine.MySQL, RoutineKind.Function) => $"SELECT `{name.Replace("`", "``")}`{call}",
            (DbEngine.PostgreSQL, RoutineKind.Procedure) => $"CALL \"{name.Replace("\"", "\"\"")}\"{call}",
            (DbEngine.PostgreSQL, RoutineKind.Function) => $"SELECT \"{name.Replace("\"", "\"\"")}\"{call}",
            (DbEngine.SqlServer, RoutineKind.Procedure) => args.Length > 0
                ? $"EXEC [{name.Replace("]", "]]")}] {args}"
                : $"EXEC [{name.Replace("]", "]]")}]",
            (DbEngine.SqlServer, RoutineKind.Function) => $"SELECT dbo.[{name.Replace("]", "]]")}]{call}",
            (DbEngine.SQLite, _) => throw new NotSupportedException("SQLite 不支持存储过程与函数"),
            _ => throw new NotSupportedException("该引擎不支持例程执行"),
        };
    }

    /// <summary>引用查找：在库内例程与触发器定义中搜关键词，返回列表查询语句（name/kind/definition 三列）。</summary>
    public static string ReferencesSql(DbEngine e, string db, string keyword)
    {
        var kw = e == DbEngine.SqlServer ? keyword.Replace("'", "''") : keyword.Replace("'", "''");
        return e switch
        {
            DbEngine.MySQL =>
                "SELECT ROUTINE_NAME AS name, ROUTINE_TYPE AS kind, ROUTINE_DEFINITION AS definition" +
                $" FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{Esc(db)}' AND ROUTINE_DEFINITION LIKE '%{Esc(kw)}%'" +
                " UNION ALL" +
                " SELECT TRIGGER_NAME, 'TRIGGER', ACTION_STATEMENT FROM information_schema.TRIGGERS" +
                $" WHERE TRIGGER_SCHEMA = '{Esc(db)}' AND ACTION_STATEMENT LIKE '%{Esc(kw)}%'",
            DbEngine.PostgreSQL =>
                "SELECT proname AS name, CASE prokind WHEN 'p' THEN 'PROCEDURE' ELSE 'FUNCTION' END AS kind, pg_get_functiondef(oid) AS definition" +
                " FROM pg_proc WHERE pronamespace = (SELECT oid FROM pg_namespace WHERE nspname = '" + Esc(db) + "')" +
                $" AND pg_get_functiondef(oid) LIKE '%{Esc(kw)}%'",
            DbEngine.SqlServer =>
                "SELECT name, CASE WHEN type = 'P' THEN 'PROCEDURE' ELSE 'FUNCTION' END AS kind, OBJECT_DEFINITION(object_id) AS definition" +
                " FROM sys.objects WHERE type IN ('P', 'FN', 'TR')" +
                $" AND OBJECT_DEFINITION(object_id) LIKE '%{Esc(kw)}%'",
            DbEngine.SQLite =>
                "SELECT name, 'TRIGGER' AS kind, sql AS definition FROM sqlite_master" +
                $" WHERE type = 'trigger' AND sql LIKE '%{Esc(kw)}%'",
            _ => throw new NotSupportedException("该引擎不支持引用查找"),
        };
    }

    /// <summary>新建模板：不带 DELIMITER，直接整段执行（配合 ExecuteSingle 单语句通道）。</summary>
    public static string Template(DbEngine e, RoutineKind kind, string name)
    {
        var safe = name.Length > 0 ? name : "new_routine";
        return e switch
        {
            DbEngine.MySQL => kind switch
            {
                RoutineKind.Procedure => $"CREATE PROCEDURE `{safe}`()\nBEGIN\n  -- 在此编写过程体；不需要 DELIMITER，整段执行即可\n  SELECT 1;\nEND",
                RoutineKind.Function => $"CREATE FUNCTION `{safe}`() RETURNS INT\nBEGIN\n  -- 在此编写函数体\n  RETURN 0;\nEND",
                _ => $"CREATE TRIGGER `{safe}` AFTER INSERT ON 表名 FOR EACH ROW\nBEGIN\n  -- 在此编写触发器体\nEND",
            },
            DbEngine.PostgreSQL => kind switch
            {
                RoutineKind.Procedure => $"CREATE PROCEDURE \"{safe}\"()\nLANGUAGE plpgsql\nAS $$\nBEGIN\n  -- 在此编写过程体\nEND;\n$$",
                RoutineKind.Function => $"CREATE FUNCTION \"{safe}\"() RETURNS INT AS $$\nBEGIN\n  RETURN 0;\nEND;\n$$ LANGUAGE plpgsql",
                _ => $"CREATE TRIGGER \"{safe}\" AFTER INSERT ON 表名 FOR EACH ROW EXECUTE FUNCTION \"new_function\"()",
            },
            DbEngine.SqlServer => kind switch
            {
                RoutineKind.Procedure => $"CREATE PROCEDURE [{safe}]\nAS\nBEGIN\n  -- 在此编写过程体\n  SELECT 1;\nEND",
                RoutineKind.Function => $"CREATE FUNCTION [{safe}]()\nRETURNS INT\nAS\nBEGIN\n  RETURN 0;\nEND",
                _ => $"CREATE TRIGGER [{safe}] ON 表名 AFTER INSERT\nAS\nBEGIN\n  -- 在此编写触发器体\nEND",
            },
            _ => kind == RoutineKind.Trigger
                ? $"CREATE TRIGGER \"{safe}\"\nAFTER INSERT ON 表名 FOR EACH ROW\nBEGIN\n  -- 在此编写触发器体\nEND"
                : throw new NotSupportedException("SQLite 仅支持触发器"),
        };
    }

    /// <summary>断点调试脚手架（v2.0 仅 MySQL）：把过程/函数的调用脚本连同参数占位打开到查询窗口，过程内中间结果用 SELECT 观测。</summary>
    public static string DebugScript(DbEngine e, RoutineKind kind, string name)
    {
        if (!DebugSupported(e)) throw new NotSupportedException(DebugUnsupportedText);
        var q = "`" + name.Replace("`", "``") + "`";
        return kind switch
        {
            RoutineKind.Procedure =>
                $"-- 断点调试（v2.0 · MySQL）：先填入过程参数再整段执行；过程体内的中间结果用 SELECT 观测\nSET @arg1 = NULL; -- TODO 填入第一个参数\nCALL {q}(@arg1);\nSELECT @arg1;",
            RoutineKind.Function =>
                $"-- 断点调试（v2.0 · MySQL）：填入函数参数后整段执行\nSELECT {q}(NULL /* TODO 参数 */);",
            _ => throw new NotSupportedException("触发器由数据事件触发，不支持手动断点调试；请在定义中查看触发逻辑"),
        };
    }

    private static string Esc(string s) => s.Replace("'", "''");
}
