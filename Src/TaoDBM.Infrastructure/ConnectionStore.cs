using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>一条连接配置。</summary>
public class ConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "新建连接";
    public DbEngine Engine { get; set; } = DbEngine.MySQL;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public string User { get; set; } = "";
    public string Password { get; set; } = "";   // 存储时为 DPAPI 密文
    public string Database { get; set; } = "";   // 默认库 / 文件路径
    public string Parameters { get; set; } = ""; // 附加连接参数 k=v;k2=v2
    public bool SavePassword { get; set; } = true;
    public string ColorTag { get; set; } = "#1677FF";
    // SSH 隧道（留空不启用）
    public string SshHost { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string SshUser { get; set; } = "";
    public string SshPassword { get; set; } = "";
    // 批次三格4：HTTP 隧道端点（留空不启用；SSH 优先，HTTP 次之）
    public string TunnelUrl { get; set; } = "";
    // 批次三格5：认证方式（password=账号密码 / integrated=Windows 集成；缺省兼容旧 JSON）
    public string AuthMode { get; set; } = "password";
    // E①：Redis 哨兵模式（direct=直连 / sentinel=哨兵；缺省兼容旧 JSON）
    public string RedisMode { get; set; } = "direct";
    public string SentinelMaster { get; set; } = "";   // 哨兵 master 名称（如 mymaster）
    public string SentinelNodes { get; set; } = "";    // 哨兵节点列表 host:port;host:port（分号或换行分隔）
    // E②：连接树四件套——分组 / 星标 / 隐藏（对齐 Navicat；缺省兼容旧 JSON）
    public string Group { get; set; } = "";
    public bool Starred { get; set; }
    public bool Hidden { get; set; }
    // 发现143：连接备注（连接对话框「备注」页签；缺省兼容旧 JSON）
    public string Remark { get; set; } = "";
}

public static class ProfileCrypto
{
    private static readonly byte[] Entropy = "TaoDBM.v1"u8.ToArray();

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            var data = System.Text.Encoding.UTF8.GetBytes(plain);
            // 非 Windows 平台此调用抛 PlatformNotSupportedException，由 catch 统一回落空串，属预期路径
#pragma warning disable CA1416
            var enc = System.Security.Cryptography.ProtectedData.Protect(data, Entropy, System.Security.Cryptography.DataProtectionScope.LocalMachine);
            return "dpapi:" + Convert.ToBase64String(enc);
#pragma warning restore CA1416
        }
        catch
        {
            // L7 安全铁律：密码不明文落盘（docs/01-REQUIREMENTS.md）。加密失败时宁可返回空串
            // 让用户下次连接重输，绝不静默回退明文（原实现 catch 后回明文，属安全缺陷，2026-11-13 修）。
            return "";
        }
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith("dpapi:", StringComparison.Ordinal)) return stored;
        try
        {
            var enc = Convert.FromBase64String(stored["dpapi:".Length..]);
#pragma warning disable CA1416
            var dec = System.Security.Cryptography.ProtectedData.Unprotect(enc, Entropy, System.Security.Cryptography.DataProtectionScope.LocalMachine);
            return System.Text.Encoding.UTF8.GetString(dec);
#pragma warning restore CA1416
        }
        catch { return ""; }
    }
}

/// <summary>连接配置存取：TaoPaths 数据根 connections.json（D 盘优先，发现96：注释口径与实码对齐）。</summary>
public static class ConnectionStore
{
    private static string File => TaoPaths.FilePath("connections.json");   // Tao 系列存储默认 D 盘

    public static List<ConnectionProfile> Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new List<ConnectionProfile>();
            var json = System.IO.File.ReadAllText(File);
            var list = JsonSerializer.Deserialize<List<ConnectionProfile>>(json) ?? new();
            foreach (var p in list) p.Password = ProfileCrypto.Unprotect(p.Password);
            return list;
        }
        catch { return new List<ConnectionProfile>(); }
    }

    public static void Save(List<ConnectionProfile> profiles)
    {
        var copies = profiles.Select(p =>
        {
            var c = Clone(p);
            if (c.SavePassword) c.Password = ProfileCrypto.Protect(c.Password);
            else c.Password = "";
            return c;
        }).ToList();
        var opt = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        AtomicWrite.Text(File, JsonSerializer.Serialize(copies, opt));
    }

    private static ConnectionProfile Clone(ConnectionProfile p) => new()
    {
        Id = p.Id, DisplayName = p.DisplayName, Engine = p.Engine, Host = p.Host,
        Port = p.Port, User = p.User, Password = p.Password, Database = p.Database,
        Parameters = p.Parameters, SavePassword = p.SavePassword, ColorTag = p.ColorTag,
        SshHost = p.SshHost, SshPort = p.SshPort, SshUser = p.SshUser, SshPassword = p.SshPassword,
        // 发现131：白名单漏 8 字段——每次存盘经 Clone 静默清空（隧道/认证/哨兵/分组/标记）
        TunnelUrl = p.TunnelUrl, AuthMode = p.AuthMode,
        RedisMode = p.RedisMode, SentinelMaster = p.SentinelMaster, SentinelNodes = p.SentinelNodes,
        Group = p.Group, Starred = p.Starred, Hidden = p.Hidden,
        Remark = p.Remark,   // 发现143：备注入白名单，存盘不被 Clone 清空
    };
}
