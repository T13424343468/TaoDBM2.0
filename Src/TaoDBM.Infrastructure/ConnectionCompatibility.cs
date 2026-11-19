using System;
using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Runtime.Loader;
using System.Transactions;

namespace TaoDBM.Core;

/// <summary>
/// 全版本兼容矩阵连接适配（2026-11-19 12:14 矩阵令 / 13:08 两硬点令）。
/// ①SQL Server 2008 R2 及更老：新驱动默认强制加密，老系统 TLS 栈握手必败——识别后自动降级为不加密直连；
/// ②Oracle 11g 及更老：23.x 系驱动官方支持线 12.1+，识别 ORA-28040/ORA-03134 后走 19.x 系专用驱动路径（side-by-side 目录）。
/// 纯判定与装配逻辑，不依赖具体驱动程序集；重试装配由 AdaptiveDbConnection 在打开时触发。
/// </summary>
public static class ConnectionCompatibility
{
    /// <summary>解析 SQL Server 版本号（SERVERPROPERTY('ProductVersion')，如 "10.50.4000.0"=2008 R2）。</summary>
    public static (int Major, int Minor) ParseSqlServerVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return (0, 0);
        var parts = version.Trim().Split('.', ' ');
        int major = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        return (major, minor);
    }

    /// <summary>老 TLS 时代库：10.x 及更早（2008/2008 R2 及更老）默认无 TLS1.2 补丁，强制加密握手必败。</summary>
    public static bool SqlServerNeedsLegacyTls(int major, int minor) => major > 0 && major < 11;

    /// <summary>SQL Server 连接被老 TLS 时代库拒之握手的典型症状（大小写不敏感，按串匹配）。</summary>
    public static readonly string[] SqlServerLegacyTlsMarkers =
    {
        "ssl security error",
        "pre-login handshake",
        "unexpected eof or 0 bytes from the transport stream",
        "the ssl connection could not be created",
        "unsupported security algorithm",
    };

    /// <summary>连接异常是否为老库 TLS 握手失败（降级重试的判定依据）。</summary>
    public static bool IsSqlServerLegacyTlsFailure(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
        {
            var msg = ex.Message;
            if (!string.IsNullOrEmpty(msg))
                foreach (var marker in SqlServerLegacyTlsMarkers)
                    if (msg.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
            if (ex is System.ComponentModel.Win32Exception w && (w.NativeErrorCode == -2146893007 || w.NativeErrorCode == -2146893009)) return true;
        }
        return false;
    }

    /// <summary>Oracle 老版本认证失败特征：ORA-28040（无匹配认证协议）/ ORA-03134（不再支持该服务器版本）。</summary>
    public static readonly string[] OracleLegacyAuthMarkers = { "ORA-28040", "ORA-03134" };

    /// <summary>连接异常是否为 Oracle 11g 级老库认证协议不匹配（需转 19.x 系专用驱动的判定依据）。</summary>
    public static bool IsOracleLegacyAuthFailure(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
        {
            var msg = ex.Message;
            if (!string.IsNullOrEmpty(msg))
                foreach (var marker in OracleLegacyAuthMarkers)
                    if (msg.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

/// <summary>
/// 打开失败时按判定自动换装配重开的连接包装：SQL Server 老库关加密重连、Oracle 老库转 19.x 驱动。
/// 只兜第一跳：主连接成功路径零开销，降级仅一次，不无限重试。
/// </summary>
public sealed class AdaptiveDbConnection : DbConnection
{
    private readonly Func<DbConnection> _fallbackFactory;
    private readonly Func<Exception, bool> _classifier;
    private DbConnection _inner;
    private bool _fallbackTried;

    public AdaptiveDbConnection(DbConnection primary, Func<Exception, bool> classifier, Func<DbConnection> fallbackFactory)
    {
        _inner = primary;
        _classifier = classifier;
        _fallbackFactory = fallbackFactory;
    }

    /// <summary>是否已触发降级重连（诊断与界面提示用）。</summary>
    public bool FallbackUsed => _fallbackTried;

    private bool ShouldFallback(Exception ex) => !_fallbackTried && _classifier(ex);

    private void SwapToFallback()
    {
        _fallbackTried = true;
        _inner.Dispose();
        _inner = _fallbackFactory();
    }

    public override void Open()
    {
        try { _inner.Open(); }
        catch (Exception ex) when (ShouldFallback(ex)) { SwapToFallback(); _inner.Open(); }
    }

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        try { await _inner.OpenAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ShouldFallback(ex)) { SwapToFallback(); await _inner.OpenAsync(cancellationToken).ConfigureAwait(false); }
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get => _inner.ConnectionString; set => _inner.ConnectionString = value; }
    public override string Database => _inner.Database;
    public override string DataSource => _inner.DataSource;
    public override string ServerVersion => _inner.ServerVersion;
    public override ConnectionState State => _inner.State;
    public override int ConnectionTimeout => _inner.ConnectionTimeout;
    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
    public override void Close() => _inner.Close();
    protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => _inner.BeginTransaction(isolationLevel);
    protected override DbCommand CreateDbCommand() => _inner.CreateCommand();
    public override void EnlistTransaction(Transaction? transaction) => _inner.EnlistTransaction(transaction);
    public override DataTable GetSchema() => _inner.GetSchema();
    public override DataTable GetSchema(string collectionName) => _inner.GetSchema(collectionName);
    public override DataTable GetSchema(string collectionName, string?[]? restrictionValues) => _inner.GetSchema(collectionName, restrictionValues ?? Array.Empty<string>());
    public override event StateChangeEventHandler? StateChange
    {
        add => _inner.StateChange += value;
        remove => _inner.StateChange -= value;
    }
}

/// <summary>
/// Oracle 11g 级老库专用驱动路径：19.x 系 Oracle.ManagedDataAccess.Core 以 side-by-side 目录随程序分发，
/// 与常驻 23.x 驱动隔离加载（独立 AssemblyLoadContext），命中老库才装载，不污染主进程。
/// </summary>
public static class OracleLegacyDriver
{
    /// <summary>19.x 系专用驱动目录（程序目录下 drivers/oracle19）。</summary>
    public static string LegacyDriverDir => Path.Combine(AppContext.BaseDirectory, "drivers", "oracle19");

    public static bool IsAvailable() => File.Exists(Path.Combine(LegacyDriverDir, "Oracle.ManagedDataAccess.dll"));

    /// <summary>用 19.x 系专用驱动建连接；驱动缺失或装载失败时抛出带明确动作指引的异常。</summary>
    public static DbConnection Create(string dataSource, string user, string password)
    {
        var dll = Path.Combine(LegacyDriverDir, "Oracle.ManagedDataAccess.dll");
        if (!File.Exists(dll))
            throw new InvalidOperationException(
                "该 Oracle 服务器版本过老（11g 及以前），常驻驱动官方支持线为 12.1+，需转 19.x 系专用驱动：" +
                $"请将 Oracle.ManagedDataAccess.Core 19.x 的全部程序集放入 {LegacyDriverDir} 后重试。");
        var alc = new AssemblyLoadContext("taodbm-oracle19", isCollectible: true);
        var asm = alc.LoadFromAssemblyPath(Path.GetFullPath(dll));
        var type = asm.GetType("Oracle.ManagedDataAccess.Client.OracleConnection")
            ?? throw new InvalidOperationException($"19.x 专用驱动装载成功但缺少 OracleConnection 类型：{dll}");
        var conn = (DbConnection)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"19.x 专用驱动 OracleConnection 构造失败：{dll}"));
        // 连接串经 19c 驱动自带的 OracleConnectionStringBuilder（DbConnectionStringBuilder 派生）装配：
        // 密码含分号/引号/等号等特殊字符时由构造器自动加引号转义，避免手拼串在真机上被特殊字符炸开
        var sbType = asm.GetType("Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder")
            ?? throw new InvalidOperationException($"19.x 专用驱动缺少 OracleConnectionStringBuilder 类型：{dll}");
        var sb = (DbConnectionStringBuilder)(Activator.CreateInstance(sbType)
            ?? throw new InvalidOperationException($"19.x 专用驱动 OracleConnectionStringBuilder 构造失败：{dll}"));
        sb["Data Source"] = dataSource;
        sb["User Id"] = user;
        sb["Password"] = password;
        sb["Connection Timeout"] = 10;
        try { conn.ConnectionString = sb.ConnectionString; }
        catch (ArgumentException ex)
        {
            // 驱动解析器不支持引号转义：密码同时含单引号和双引号时构造器必须成对转义，真机必炸——此处给明确动作指引
            throw new InvalidOperationException(
                "19.x 专用驱动连接串装配失败（常见于密码同时含单引号和双引号，该驱动不支持引号转义）：" +
                "请改用只含一种引号的密码后重试。", ex);
        }
        return conn;
    }
}
