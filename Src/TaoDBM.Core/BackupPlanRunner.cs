using System.Diagnostics;
using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>
/// 发现153：备份计划真实定时执行——策略 JSON（fullHours/incrHours/retain，凭据不入策略，
/// 执行时按连接档案现取）＋ 执行器（任务中心 TakeDue 后按 Kind="backup" 分派到本类）。
/// 引擎口径：SQLite 文件级复制 / SQL Server BACKUP（增量 WITH DIFFERENTIAL）/ MySQL·PG 官方 CLI /
/// MongoDB mongodump / Redis BGSAVE；Oracle data pump 需服务器端目录对象，暂不支持自动定时（人话如实标注）。
/// 纯逻辑可测：策略解析校验、文件命名、SQLite 执行、不支持引擎人话。
/// </summary>
public static class BackupPlanRunner
{
    /// <summary>备份计划策略：全量每 FullHours 小时一次，IncrHours=0 表示不排增量；RetainFulls 保留份数。</summary>
    public sealed record BackupPlanPolicy(int FullHours, int IncrHours, int RetainFulls)
    {
        public string ToJson() => JsonSerializer.Serialize(new Dict(FullHours, IncrHours, RetainFulls), JsonOpts);
    }

    private sealed record Dict(int FullHours, int IncrHours, int RetainFulls);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>策略序列化（先校验后人话报错，不落非法策略）。</summary>
    public static string PolicyJson(int fullHours, int incrHours, int retainFulls)
    {
        Validate(fullHours, incrHours, retainFulls);
        return JsonSerializer.Serialize(new Dict(fullHours, incrHours, retainFulls), JsonOpts);
    }

    /// <summary>策略解析：JSON 损坏或数值越界都给人话，不静默回默认（发现153 验收3）。</summary>
    public static BackupPlanPolicy ParsePolicy(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("备份计划策略为空：请重新在备份计划里点「挂到任务中心」。");
        Dict? d;
        try { d = JsonSerializer.Deserialize<Dict>(json); }
        catch (JsonException)
        {
            // 兼容旧口径：字段名大小写或多余字段导致的解析失败，按损坏处理
            throw new ArgumentException("备份计划策略读不出来（内容已损坏）：请删掉该任务重新挂一次。");
        }
        if (d is null)
            throw new ArgumentException("备份计划策略读不出来（内容为空）：请删掉该任务重新挂一次。");
        Validate(d.FullHours, d.IncrHours, d.RetainFulls);
        return new BackupPlanPolicy(d.FullHours, d.IncrHours, d.RetainFulls);
    }

    private static void Validate(int fullHours, int incrHours, int retainFulls)
    {
        if (fullHours < 1) throw new ArgumentException("全量间隔至少 1 小时。");
        if (incrHours < 0) throw new ArgumentException("增量间隔不能是负数（0 表示不排增量）。");
        if (retainFulls < 0) throw new ArgumentException("保留全量份数不能是负数。");
    }

    /// <summary>备份产物路径：数据根 backups 子目录，文件名 库-档位-时间戳（发现108/112 数据根旁路口径）。</summary>
    public static string BackupFileFor(ConnectionProfile profile, string db, string kind)
    {
        string ext = profile.Engine switch
        {
            DbEngine.SqlServer => ".bak",
            DbEngine.SQLite => ".db",
            DbEngine.MongoDB => "",      // mongodump 输出为目录
            _ => ".sql",
        };
        string name = $"{(db.Length > 0 ? db : "all")}-{kind}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}";
        return TaoPaths.FilePathIn("backups", name);
    }

    /// <summary>任务中心到期分派入口：Kind="backup" 条目按策略执行一次备份，落账并返回结果一句话。</summary>
    public static async Task<string> RunEntryAsync(ScheduledSqlEntry e, IDbProvider prov)
    {
        var policy = ParsePolicy(e.Sql);
        var profile = prov.Profile;
        bool incr = e.Name.Contains("增量", StringComparison.Ordinal);
        string db = e.Db;
        string kind = incr ? "incr" : "full";
        if (!BackupTools.Supported(profile.Engine) || profile.Engine == DbEngine.Oracle)
            throw new NotSupportedException(OracleUnsupportedText);

        string file = BackupFileFor(profile, db, kind);
        string result;
        switch (profile.Engine)
        {
            case DbEngine.SQLite:
                await Task.Run(() => BackupTools.SqliteBackup(profile.Database, file));
                result = $"备份完成：{profile.Database} → {file}";
                break;
            case DbEngine.SqlServer:
            {
                var sql = incr
                    ? BackupTools.SqlServerBackupSql(db, file).Replace("WITH FORMAT, INIT", "WITH DIFFERENTIAL")
                    : BackupTools.SqlServerBackupSql(db, file);
                var rs = await prov.Execute("", sql);
                var err = rs.FirstOrDefault(r => r.IsError || r.Message.StartsWith("错误", StringComparison.Ordinal) || r.Message.Contains("没成功："));
                result = err is null ? $"备份完成：服务器已写入 {file}" : err.Message;
                break;
            }
            case DbEngine.MySQL or DbEngine.PostgreSQL:
            {
                int defPort = profile.Engine == DbEngine.MySQL ? 3306 : 5432;
                var (h, p) = TunnelResolver.Resolve(profile, profile.Host, profile.Port > 0 ? profile.Port : defPort);
                var spec = profile.Engine == DbEngine.MySQL
                    ? BackupTools.MysqlDump(h, p, profile.User, profile.Password, db, file)
                    : BackupTools.PgDump(h, p, profile.User, profile.Password, db, file);
                var (code, text) = await RunTool(spec, CancellationToken.None);
                result = code == 0
                    ? $"备份完成：{db} → {file}"
                    : $"退出码 {code}\n{text}";
                break;
            }
            case DbEngine.MongoDB:
            {
                var (h, p) = TunnelResolver.Resolve(profile, profile.Host, profile.Port > 0 ? profile.Port : 27017);
                var spec = BackupTools.MongoDump(h, p, profile.User, profile.Password, db, file);
                var (code, text) = await RunTool(spec, CancellationToken.None);
                result = code == 0 ? $"备份完成：{db} → {file}（mongodump 目录）" : $"退出码 {code}\n{text}";
                break;
            }
            case DbEngine.Redis:
            {
                await prov.Execute(db, BackupTools.RedisBackupCommand());
                result = "已触发 BGSAVE，RDB 由 Redis 服务端落盘；自动取回需配置服务器端目录，详见备份 / 还原向导。";
                file = "(server-side rdb)";
                break;
            }
            default:
                throw new NotSupportedException(OracleUnsupportedText);
        }

        bool ok = result.StartsWith("备份完成", StringComparison.Ordinal);
        long size = 0;
        try { if (ok && file.Length > 0 && System.IO.File.Exists(file)) size = new FileInfo(file).Length; } catch { /* 目录口径不取大小 */ }
        BackupHistory.Add(prov.EngineName, db, incr ? "备份(增量)" : "备份", file, ok, size, result.Split('\n')[0].Trim());
        string note = incr && profile.Engine is not (DbEngine.SqlServer or DbEngine.Oracle)
            ? "（该引擎无增量口径，本次按全量执行）"
            : "";
        AppLog.Write($"[备份计划] entry={e.Id} engine={profile.Engine} db={db} kind={kind} ok={ok} {note}");
        return result + note;
    }

    public static string OracleUnsupportedText =>
        "Oracle 备份计划暂不支持自动定时执行：data pump 需要服务器端目录对象（DBA 预建），请在「备份 / 还原」向导手动执行。";

    /// <summary>发现144：备份树还原——走与向导同源的引擎分流；文件缺失当场人话拦下。</summary>
    public static async Task<string> RestoreAsync(ConnectionProfile profile, IDbProvider prov, string db, string file)
    {
        if (!System.IO.File.Exists(file) && profile.Engine != DbEngine.MongoDB)
            throw new FileNotFoundException("备份文件已不在磁盘上，无法还原：" + file);
        switch (profile.Engine)
        {
            case DbEngine.SQLite:
                await Task.Run(() => BackupTools.SqliteRestore(profile.Database, file));
                return $"还原完成：{file} → {profile.Database}";
            case DbEngine.SqlServer:
            {
                var rs = await prov.Execute("", BackupTools.SqlServerRestoreSql(db, file));
                var err = rs.FirstOrDefault(r => r.IsError || r.Message.StartsWith("错误", StringComparison.Ordinal) || r.Message.Contains("没成功："));
                return err is null ? $"还原完成：{db} 已从 {file} 恢复" : err.Message;
            }
            case DbEngine.MySQL or DbEngine.PostgreSQL:
            {
                int defPort = profile.Engine == DbEngine.MySQL ? 3306 : 5432;
                var (h, p) = TunnelResolver.Resolve(profile, profile.Host, profile.Port > 0 ? profile.Port : defPort);
                var spec = profile.Engine == DbEngine.MySQL
                    ? BackupTools.MysqlRestore(h, p, profile.User, profile.Password, db, file)
                    : BackupTools.PgRestore(h, p, profile.User, profile.Password, db, file);
                var (code, text) = await RunTool(spec, CancellationToken.None);
                return code == 0 ? $"还原完成：{file} → {db}" : $"退出码 {code}\n{text}";
            }
            case DbEngine.MongoDB:
            {
                var (h, p) = TunnelResolver.Resolve(profile, profile.Host, profile.Port > 0 ? profile.Port : 27017);
                var spec = BackupTools.MongoRestore(h, p, profile.User, profile.Password, db, file);
                var (code, text) = await RunTool(spec, CancellationToken.None);
                return code == 0 ? $"还原完成：{file} → {db}" : $"退出码 {code}\n{text}";
            }
            default:
                throw new NotSupportedException("该引擎暂不支持一键还原：请使用「备份 / 还原」向导或恢复演练。");
        }
    }

    /// <summary>外部工具执行（与备份向导同口径：凭据走环境变量 / 辅助文件，不进命令行）。</summary>
    private static async Task<(int Code, string Output)> RunTool(ToolSpec spec, CancellationToken ct)
    {
        var tempFiles = new List<string>();
        foreach (var kv in spec.Files)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), kv.Key);
            await System.IO.File.WriteAllTextAsync(path, kv.Value, ct);
            tempFiles.Add(path);
            spec.Arguments = spec.Arguments.Replace(BackupTools.QuoteArg(kv.Key), BackupTools.QuoteArg(path));
        }
        var psi = new ProcessStartInfo(spec.FileName, spec.Arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var kv in spec.Env) psi.Environment[kv.Key] = kv.Value;
        if (spec.InputFile != null) psi.RedirectStandardInput = true;
        try
        {
            using var p = new Process { StartInfo = psi };
            using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { /* 进程可能已退出 */ } });
            p.Start();
            if (spec.InputFile != null)
            {
                var content = await System.IO.File.ReadAllTextAsync(spec.InputFile, ct);
                await p.StandardInput.WriteAsync(content.AsMemory(), ct);
                p.StandardInput.Close();
            }
            await p.WaitForExitAsync(ct);
            string output = await p.StandardOutput.ReadToEndAsync(ct) + await p.StandardError.ReadToEndAsync(ct);
            return (p.ExitCode, output.Trim());
        }
        finally
        {
            foreach (var f in tempFiles)
            {
                try { System.IO.File.Delete(f); } catch { /* 清理失败不掩盖主流程结果 */ }
            }
        }
    }
}
