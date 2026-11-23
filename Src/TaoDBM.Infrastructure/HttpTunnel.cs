namespace TaoDBM.Core;

using System.Net;
using System.Net.Sockets;

/// <summary>
/// 批次三格4：HTTP 隧道——数据库字节流 Base64 包 POST 到目标主机上的隧道端点脚本
/// （tunnel.php / tunnel.aspx，随包归档），本地起 TcpListener 伪网关喂给驱动。
/// 零包自写：客户端只用系统 HttpClient；协议与竞品同款思路（POST {url}?act=connect/open/read/write/close）。
/// 独立文件不与 SshTunnel 混杂（冻结稿落点冲突结论）；解析入口由 TunnelResolver 收敛。
/// </summary>
public static class HttpTunnel
{
    private static readonly object _gate = new();
    private static readonly Dictionary<string, HttpTunnelSession> _active = new();

    /// <summary>未配置 HTTP 隧道时原样返回；配置了则起本地伪网关并返回 127.0.0.1:本地端口。</summary>
    public static (string Host, int Port) Resolve(ConnectionProfile p, string host, int port)
    {
        if (string.IsNullOrWhiteSpace(p.TunnelUrl) || EngineInfo.IsFileBased(p.Engine))
            return (host, port);

        int realPort = port > 0 ? port : EngineInfo.DefaultPort(p.Engine);
        lock (_gate)
        {
            if (_active.TryGetValue(p.Id, out var s) && s.Alive)
                return ("127.0.0.1", s.LocalPort);

            if (_active.TryGetValue(p.Id, out var old))
            {
                Safe.Cleanup(() => old.Dispose(), "HTTP隧道失效清理");
                _active.Remove(p.Id);
            }

            var session = new HttpTunnelSession(p.TunnelUrl, host, realPort);
            session.Start();
            _active[p.Id] = session;
            return ("127.0.0.1", session.LocalPort);
        }
    }

    public static void StopProfile(string id)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(id, out var s)) return;
            Safe.Cleanup(() => s.Dispose(), "HTTP隧道关闭");
            _active.Remove(id);
        }
    }

    public static void StopAll()
    {
        lock (_gate)
        {
            foreach (var (_, s) in _active) Safe.Cleanup(() => s.Dispose(), "HTTP隧道全量关闭");
            _active.Clear();
        }
    }
}

/// <summary>单条 HTTP 隧道会话：本地 TcpListener 接驱动连接，字节流 Base64 经 HTTP 端点转发。</summary>
public sealed class HttpTunnelSession : IDisposable
{
    private const int ChunkSize = 32 * 1024;   // 每次往返携带的原始字节数（Base64 前约 43KB）
    private readonly string _endpoint;
    private readonly string _remoteHost;
    private readonly int _remotePort;
    private readonly HttpClient _http;
    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private string _connId = "";
    private long _roundTrips;

    public int LocalPort { get; private set; }
    public long RoundTrips => Interlocked.Read(ref _roundTrips);
    /// <summary>伪网关存活（监听未停且端点可达性由请求时判定）。</summary>
    public bool Alive => _listener is not null;

    public HttpTunnelSession(string endpointUrl, string remoteHost, int remotePort)
    {
        _endpoint = endpointUrl.Trim();
        _remoteHost = remoteHost;
        _remotePort = remotePort;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };   // 冻结稿：隧道连接超时放宽 30 秒
    }

    /// <summary>起本地伪网关并经端点打开远端连接；端点不可达即抛（连接对话框给说人话错误）。</summary>
    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _connId = System.Text.Encoding.UTF8.GetString(Post("open", Array.Empty<byte>()));
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(_cts.Token);
                _ = PumpAsync(client);
            }
        }
        catch (OperationCanceledException) { /* 正常关停 */ }
        catch { /* 监听异常：会话作废，下次 Resolve 重建 */ }
    }

    /// <summary>单条客户端连接的双向泵：驱动 → 端点 → 远端，远端回包 → 端点 → 驱动。</summary>
    private async Task PumpAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var buf = new byte[ChunkSize];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf.AsMemory(0, ChunkSize), _cts.Token);
                if (n == 0) { Post("close", Array.Empty<byte>()); return; }
                var resp = Post("write", buf[..n]);
                if (resp.Length > 0)
                    await stream.WriteAsync(resp.AsMemory(0, resp.Length), _cts.Token);
                else
                {   // 端点无即时回包：短轮询读远端（竞品同款思路，延迟换兼容）
                    await Task.Delay(30, _cts.Token);
                    resp = Post("read", Array.Empty<byte>());
                    if (resp.Length > 0) await stream.WriteAsync(resp.AsMemory(0, resp.Length), _cts.Token);
                }
            }
        }
        catch { Safe.Cleanup(() => Post("close", Array.Empty<byte>()), "HTTP隧道泵清理"); }
    }

    /// <summary>端点协议：POST {endpoint}?act={act}，body=Base64(载荷)；回包 body=Base64(响应)。</summary>
    private byte[] Post(string act, byte[] payload)
    {
        try
        {
            using var content = new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(payload)));
            using var resp = _http.PostAsync($"{_endpoint}?act={act}&host={Uri.EscapeDataString(_remoteHost)}&port={_remotePort}&cid={_connId}",
                content).GetAwaiter().GetResult();
            Interlocked.Increment(ref _roundTrips);
            if (!resp.IsSuccessStatusCode) return Array.Empty<byte>();
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
            if (act == "open") return System.Text.Encoding.UTF8.GetBytes(body);   // open 回连接 Id
            return body.Length == 0 ? Array.Empty<byte>() : Convert.FromBase64String(body);
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    public void Dispose()
    {
        Safe.Cleanup(() => Post("close", Array.Empty<byte>()), "HTTP隧道远端关闭");
        Safe.Cleanup(() => { _cts.Cancel(); _listener?.Stop(); _http.Dispose(); }, "HTTP隧道本地清理");
    }
}

/// <summary>隧道解析器（冻结稿：SSH 优先，HTTP 次之，入口收敛一处）。</summary>
public static class TunnelResolver
{
    public static (string Host, int Port) Resolve(ConnectionProfile p, string host, int port)
    {
        if (!string.IsNullOrWhiteSpace(p.SshHost))
            return SshTunnel.Resolve(p, host, port);
        if (!string.IsNullOrWhiteSpace(p.TunnelUrl))
            return HttpTunnel.Resolve(p, host, port);
        return (host, port);
    }

    /// <summary>关闭指定连接的隧道（SSH 与 HTTP 双通道一并收口，入口收敛一处）。</summary>
    public static void StopProfile(string id)
    {
        SshTunnel.StopProfile(id);
        HttpTunnel.StopProfile(id);
    }

    /// <summary>关闭全部隧道（连接删除或程序退出时调用）。</summary>
    public static void StopAll()
    {
        SshTunnel.StopAll();
        HttpTunnel.StopAll();
    }
}
