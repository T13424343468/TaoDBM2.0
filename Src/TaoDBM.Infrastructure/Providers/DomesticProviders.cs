using System.Data.Common;
using System.Reflection;
using System.Runtime.Loader;

namespace TaoDBM.Core;

/// <summary>
/// 批次七（2026-11-24 老板令瀑布落码）：国产六家 Provider。
/// 底座策略＝协议兼容引擎复用现有 Provider（MySQL/PG/Oracle 协议），本文件只做方言增量：
/// 端口档、租户用户名解析、只读节点探针、分片元数据只读、达梦厂商驱动引导。
/// </summary>
public class OceanBaseProvider : MySqlProvider
{
    public OceanBaseProvider(ConnectionProfile p) : base(p)
    {
        // 七-1：租户连接串 user@tenant#cluster 解析——格式校验在构造期即抛，错误信息说人话
        DomesticDialect.NormalizeObUser(p.User);
    }
}

public class GaussDbProvider : PostgreSqlProvider
{
    public GaussDbProvider(ConnectionProfile p) : base(p) { }
    // 七-2：schema=库+用户双空间——ListChildrenSql 已按 schema.table 返回，树与 Quote 逐段引用均支持，无需覆写
}

public class KingbaseProvider : PostgreSqlProvider
{
    public KingbaseProvider(ConnectionProfile p) : base(p) { }
    // 七-4：大小写敏感模式开关走附加参数（k=v 直传驱动），系统目录兼容视图与 PG 同口径
}

public class PolarDbProvider : MySqlProvider
{
    public PolarDbProvider(ConnectionProfile p) : base(p) { }

    /// <summary>七-5：只读节点识别探针——连接后可查 innodb_read_only 判定节点角色（ON=只读节点）。</summary>
    public const string ReadOnlyProbeSql = "SHOW VARIABLES LIKE 'innodb_read_only'";

    /// <summary>当前连接是否只读节点（探针结果缓存；未探测为 null）。</summary>
    public bool? IsReadOnlyNode { get; private set; }

    public async Task ProbeReadOnlyAsync(string? db)
    {
        await using var conn = Open(db);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ReadOnlyProbeSql;
        await using var r = await cmd.ExecuteReaderAsync();
        if (await r.ReadAsync())
            IsReadOnlyNode = string.Equals(Convert.ToString(r.GetValue(1)), "ON", StringComparison.OrdinalIgnoreCase);
    }
}

public class TdsqlProvider : MySqlProvider
{
    public TdsqlProvider(ConnectionProfile p) : base(p) { }
    // 七-6：Proxy 连接串（主机字段填 Proxy 地址）；分片表元数据走 information_schema 兼容口径只读展示；
    // 分布式执行计划透传＝EXPLAIN 原样下发由 Proxy 路由，无需本地改写
}

/// <summary>七-3 达梦：Oracle 兼容方言；厂商私有驱动不自带，启动/连接时枚举 drivers/dameng 装载，缺驱动给说人话指引。</summary>
public class DamengProvider : OracleProvider
{
    /// <summary>达梦驱动目录（程序目录下 drivers/dameng）。</summary>
    public static string DriverDir => Path.Combine(AppContext.BaseDirectory, "drivers", "dameng");

    public static bool IsDriverAvailable() => File.Exists(Path.Combine(DriverDir, "DmProvider.dll"));

    public DamengProvider(ConnectionProfile p) : base(p) { }

    protected override DbConnection Open(string? db)
    {
        if (!IsDriverAvailable())
            throw new InvalidOperationException(
                "连接达梦需要厂商私有驱动，本程序不自带：请从达梦官网获取 DmProvider.dll（随 DM 安装包附带），" +
                $"放入 {DriverDir} 后重新连接。");
        return DmConnection.Create(DriverDir, BuildDataSource(), Profile.User, Profile.Password);
    }

    private string BuildDataSource()
    {
        var (host, port) = Endpoint();
        return $"{host}:{(port > 0 ? port : EngineInfo.DefaultPort(DbEngine.Dameng))}";
    }
}

/// <summary>达梦 DmProvider 反射装载（独立 AssemblyLoadContext，与 Oracle 19 专用驱动同口径隔离）。</summary>
public static class DmConnection
{
    /// <summary>用 drivers/dameng 下的 DmProvider.dll 建连接；装载失败抛带明确动作指引的异常。</summary>
    public static DbConnection Create(string driverDir, string dataSource, string user, string password)
    {
        var dll = Path.Combine(driverDir, "DmProvider.dll");
        var alc = new AssemblyLoadContext("taodbm-dameng", isCollectible: true);
        Assembly asm;
        try { asm = alc.LoadFromAssemblyPath(Path.GetFullPath(dll)); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"达梦驱动装载失败：{dll}（{ex.Message}）。请确认文件完整且与程序位数一致后重试。", ex);
        }
        var type = asm.GetType("Dm.DmConnection")
            ?? throw new InvalidOperationException($"达梦驱动装载成功但缺少 DmConnection 类型：{dll}");
        var conn = (DbConnection)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"达梦驱动 DmConnection 构造失败：{dll}"));
        var sb = new DbConnectionStringBuilder
        {
            { "Server", dataSource },
            { "User ID", user },
            { "Password", password },
            { "Connection Timeout", 10 },
        };
        conn.ConnectionString = sb.ConnectionString;
        return conn;
    }
}
