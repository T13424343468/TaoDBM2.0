using System.Diagnostics;

namespace TaoDBM.Core;

/// <summary>外部工具执行规格：命令行 + 环境变量（密码走环境变量，不进命令行）。</summary>
public sealed class ToolSpec
{
    public string FileName { get; set; } = "";
    public string Arguments { get; set; } = "";
    /// <summary>密码等经环境变量传入，不出现在命令行与展示文本里。</summary>
    public Dictionary<string, string> Env { get; set; } = new();
    /// <summary>还原时把该文件内容写到 stdin（mysql / psql）。</summary>
    public string? InputFile { get; set; }
    /// <summary>随进程落盘的辅助文件（路径→内容）：mongodump --config 密码、expdp/impdp parfile 凭据。
    /// 执行器启动前写入、退出后删除；内容不出现在命令行与展示文本里（2026-11-20 批次N）。</summary>
    public Dictionary<string, string> Files { get; set; } = new();
    /// <summary>展示用命令行（不含密码）。</summary>
    public string Describe => FileName + " " + Arguments;
}

/// <summary>备份 / 还原能力矩阵与命令构造（纯逻辑，可测试）。</summary>
public static class BackupTools
{
    /// <summary>各引擎备份还原支持情况：MySQL/PG 用官方 CLI，SQL Server 用 T-SQL，SQLite 文件复制，
    /// Oracle 用 data pump（parfile 凭据），MongoDB 用 mongodump/mongorestore（config 密码），Redis 走 BGSAVE+RDB 文件复制。</summary>
    public static bool Supported(DbEngine e) => e is DbEngine.MySQL or DbEngine.PostgreSQL
        or DbEngine.SQLite or DbEngine.SqlServer or DbEngine.Oracle or DbEngine.MongoDB or DbEngine.Redis;

    public static string UnsupportedReason(DbEngine e) => e switch
    {
        _ => "",
    };

    /// <summary>命令行参数加引号（含空格或为空时）。</summary>
    public static string QuoteArg(string v) => v.Contains(' ') ? $"\"{v}\"" : v;

    // ---------- MySQL ----------

    public static ToolSpec MysqlDump(string host, int port, string user, string password, string db, string file)
    {
        return new ToolSpec
        {
            FileName = "mysqldump",
            Arguments = $"--host={QuoteArg(host)} --port={port} --user={QuoteArg(user)}" +
                $" --result-file={QuoteArg(file)} --routines --events {QuoteArg(db)}",
            Env = password.Length > 0 ? new() { ["MYSQL_PWD"] = password } : new(),
        };
    }

    public static ToolSpec MysqlRestore(string host, int port, string user, string password, string db, string file)
    {
        return new ToolSpec
        {
            FileName = "mysql",
            Arguments = $"--host={QuoteArg(host)} --port={port} --user={QuoteArg(user)} {QuoteArg(db)}",
            Env = password.Length > 0 ? new() { ["MYSQL_PWD"] = password } : new(),
            InputFile = file,
        };
    }

    // ---------- PostgreSQL ----------

    public static ToolSpec PgDump(string host, int port, string user, string password, string db, string file)
    {
        return new ToolSpec
        {
            FileName = "pg_dump",
            Arguments = $"--host={QuoteArg(host)} --port={port} --username={QuoteArg(user)}" +
                $" --file={QuoteArg(file)} --no-owner {QuoteArg(db)}",
            Env = password.Length > 0 ? new() { ["PGPASSWORD"] = password } : new(),
        };
    }

    public static ToolSpec PgRestore(string host, int port, string user, string password, string db, string file)
    {
        return new ToolSpec
        {
            FileName = "psql",
            Arguments = $"--host={QuoteArg(host)} --port={port} --username={QuoteArg(user)} --dbname={QuoteArg(db)} --file={QuoteArg(file)}",
            Env = password.Length > 0 ? new() { ["PGPASSWORD"] = password } : new(),
        };
    }

    // ---------- SQL Server（T-SQL，经 ADO 连接执行，路径为服务器侧路径） ----------

    public static string SqlServerBackupSql(string db, string file) =>
        $"BACKUP DATABASE {QuoteBracket(db)} TO DISK = '{file.Replace("'", "''")}' WITH FORMAT, INIT";

    public static string SqlServerRestoreSql(string db, string file) =>
        $"RESTORE DATABASE {QuoteBracket(db)} FROM DISK = '{file.Replace("'", "''")}' WITH REPLACE";

    private static string QuoteBracket(string s) => "[" + s.Replace("]", "]]") + "]";

    // ---------- SQLite（文件复制，地址为连接里填的库文件路径） ----------

    public static void SqliteBackup(string dbFile, string file)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file) ?? ".");
        File.Copy(dbFile, file, overwrite: true);
    }

    public static void SqliteRestore(string dbFile, string file)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("还原文件不存在：" + file);
        File.Copy(file, dbFile, overwrite: true);
    }

    // ---------- Oracle（data pump：凭据进 parfile，不进命令行；目录对象需 DBA 预建） ----------

    /// <summary>expdp 备份：凭据（USERID）进 parfile；directory 为服务器端目录对象名，dumpFile 为该目录下文件名。</summary>
    public static ToolSpec OracleBackup(string host, int port, string user, string password, string service,
        string directory, string dumpFile)
    {
        var logFile = System.IO.Path.ChangeExtension(dumpFile, ".log");
        var par = "USERID=" + user + "/" + password + "@" + host + ":" + port + "/" + service + "\n" +
                  "DIRECTORY=" + directory + "\nDUMPFILE=" + dumpFile + "\nLOGFILE=" + logFile;
        return new ToolSpec
        {
            FileName = "expdp",
            Arguments = "parfile=" + QuoteArg("taodbm_expdp.par"),
            Files = new() { ["taodbm_expdp.par"] = par },
        };
    }

    /// <summary>impdp 还原：凭据进 parfile；restoreDb 为目标库（REMAP_SCHEMA 场景由调用方扩展）。</summary>
    public static ToolSpec OracleRestore(string host, int port, string user, string password, string service,
        string directory, string dumpFile)
    {
        var logFile = System.IO.Path.ChangeExtension(dumpFile, ".imp.log");
        var par = "USERID=" + user + "/" + password + "@" + host + ":" + port + "/" + service + "\n" +
                  "DIRECTORY=" + directory + "\nDUMPFILE=" + dumpFile + "\nLOGFILE=" + logFile;
        return new ToolSpec
        {
            FileName = "impdp",
            Arguments = "parfile=" + QuoteArg("taodbm_impdp.par"),
            Files = new() { ["taodbm_impdp.par"] = par },
        };
    }

    // ---------- MongoDB（mongodump/mongorestore：密码进 --config yaml，不进命令行） ----------

    /// <summary>mongodump 备份整库或指定库（db 为空=全部库）；密码经 --config 文件传入。</summary>
    public static ToolSpec MongoDump(string host, int port, string user, string password, string db, string outDir)
    {
        var cfg = BuildMongoConfig(host, port, user, password);
        var args = "--config=" + QuoteArg("taodbm_mongo.conf");
        if (db.Length > 0) args += " --db=" + QuoteArg(db);
        args += " --out=" + QuoteArg(outDir);
        return new ToolSpec { FileName = "mongodump", Arguments = args, Files = new() { ["taodbm_mongo.conf"] = cfg } };
    }

    /// <summary>mongorestore 还原（--drop 先清后写，保证可重复执行）。</summary>
    public static ToolSpec MongoRestore(string host, int port, string user, string password, string db, string dumpDir)
    {
        var cfg = BuildMongoConfig(host, port, user, password);
        var args = "--config=" + QuoteArg("taodbm_mongo.conf");
        if (db.Length > 0) args += " --nsFrom=" + QuoteArg(db + ".*") + " --nsTo=" + QuoteArg(db + ".*");
        args += " --drop --dir=" + QuoteArg(dumpDir);
        return new ToolSpec { FileName = "mongorestore", Arguments = args, Files = new() { ["taodbm_mongo.conf"] = cfg } };
    }

    /// <summary>mongodump/mongorestore 共用认证配置（4.4+ 与命令行选项可混合，--uri 除外）。</summary>
    private static string BuildMongoConfig(string host, int port, string user, string password)
    {
        var escaped = password.Replace("\"", "\\\"");
        return "hosts:\n  - " + host + ":" + port + "\nuserName: \"" + user + "\"\npassword: \"" + escaped + "\"";
    }

    // ---------- Redis（BGSAVE 触发 RDB 落盘 + 服务端目录文件复制；还原需重启加载） ----------

    /// <summary>触发后台保存的 Redis 命令（经连接执行，返回 OK 后在 saveDir 找 rdb 文件）。</summary>
    public static string RedisBackupCommand() => "BGSAVE";

    /// <summary>还原：把备份 RDB 复制为服务端 dir 下的 dump 文件名；复制后需重启 Redis 加载。</summary>
    public static void RedisRestoreCopy(string rdbFile, string redisDir, string serverFileName = "dump.rdb")
    {
        if (!File.Exists(rdbFile)) throw new FileNotFoundException("还原文件不存在：" + rdbFile);
        System.IO.Directory.CreateDirectory(redisDir);
        File.Copy(rdbFile, System.IO.Path.Combine(redisDir, serverFileName), overwrite: true);
    }
}
