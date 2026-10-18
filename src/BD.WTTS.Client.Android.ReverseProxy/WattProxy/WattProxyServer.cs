// WattProxy：Android 端轻量 HTTP CONNECT 代理 + 加速分流。
// 架构对应上游桌面端的 Kestrel ListenHttpProxy + TunnelMiddleware，
// 但服务器层用纯 BCL TcpListener 实现（Android 无 ASP.NET Core runtime pack）。
//
// 工作方式（与 tun2socks 对接）：
//   tun2socks 把设备 TCP 流量转发到本代理（HTTP CONNECT），
//   本代理按加速域名表分流：
//     命中 → DomainResolver(DoH/可靠 DNS) 解析真实 IP 后直连（修复 DNS 污染）
//     未命中 → 按客户端原始目标直接 TCP 透传
//
// 上游对应物：
//   TunnelMiddleware.GetUpstreamEndPointsAsync 的分流判定（IsMatch/ResolveAsync）

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BD.WTTS;
using BD.WTTS.Models;
using NLog;

// ReSharper disable once CheckNamespace
namespace BD.WTTS.Services;

public sealed class WattProxyServer
{
    const string Tag = "WattProxy";
    static readonly Logger Logger = LogManager.GetLogger(Tag);

    readonly TcpListener _listener;
    readonly HashSet<string> _accelerateDomains;
    readonly string[] _dohServers = { "https://dns.alidns.com/dns-query", "https://doh.pub/dns-query" };
    CancellationTokenSource? _cts;
    Task? _acceptLoop;

    public int Port { get; }

    public WattProxyServer(int port, IEnumerable<string> accelerateDomains)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
        _accelerateDomains = new HashSet<string>(
            accelerateDomains.Select(d => d.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
    }

    public static WattProxyServer? Instance { get; private set; }

    public static void Start(int port, IReadOnlyList<AccelerateProjectDTO> projects)
    {
        if (Instance != null) return;
        var domains = projects
            .Where(p => !string.IsNullOrWhiteSpace(p.MatchDomainNames))
            .SelectMany(p => p.MatchDomainNames!.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(NormalizePattern)
            .Where(d => !string.IsNullOrEmpty(d));
        Instance = new WattProxyServer(port, domains);
        Instance.StartCore();
    }

    public static void Stop()
    {
        Instance?.StopCore();
        Instance = null;
    }

    static string NormalizePattern(string pattern)
    {
        // 上游 DomainPattern 形如 "steamcommunity.com" 或 "*.steampowered.com"
        pattern = pattern.Trim().ToLowerInvariant();
        if (pattern.StartsWith("*.")) pattern = pattern[2..];
        return pattern;
    }

    void StartCore()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Logger.Info("WattProxy listening on 0.0.0.0:{Port}, {Count} accelerate domains", Port, _accelerateDomains.Count);
    }

    void StopCore()
    {
        try
        {
            _cts?.Cancel();
            _listener.Stop();
        }
        catch { }
    }

    async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            using var stream = client.GetStream();
            var (method, target, bufferRemainder) = await ReadRequestHeadAsync(stream, ct);
            if (method == null || target == null) return;

            var colonIdx = target.LastIndexOf(':');
            var host = colonIdx > 0 ? target[..colonIdx] : target;
            var port = colonIdx > 0 && int.TryParse(target[(colonIdx + 1)..], out var p) ? p : 80;

            if (method == "CONNECT")
            {
                // 回 200 建立隧道
                var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                await stream.WriteAsync(ok, ct);

                using var upstream = await ConnectUpstreamAsync(host, port, ct);
                if (upstream == null) return;
                await PumpAsync(stream, upstream.GetStream(), ct);
            }
            else
            {
                // 普通 HTTP（tun2socks 场景基本全是 CONNECT，这里兜底直连）
                using var upstream = await ConnectUpstreamAsync(host, port, ct);
                if (upstream == null) return;
                var us = upstream.GetStream();
                await us.WriteAsync(bufferRemainder, ct);
                await PumpAsync(stream, us, ct);
            }
        }
        catch (System.Exception ex)
        {
            Logger.Debug(ex, "client handler end");
        }
    }

    static async Task<(string? method, string? target, byte[] remainder)> ReadRequestHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        var total = 0;
        while (total < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct);
            if (n == 0) return (null, null, Array.Empty<byte>());
            total += n;
            var headEnd = Encoding.ASCII.GetString(buf, 0, total).IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headEnd >= 0)
            {
                var head = Encoding.ASCII.GetString(buf, 0, headEnd);
                var firstLine = head.Split('\n')[0].Trim();
                var parts = firstLine.Split(' ');
                if (parts.Length >= 2)
                {
                    var remainder = new byte[total - headEnd - 4];
                    Array.Copy(buf, headEnd + 4, remainder, 0, remainder.Length);
                    return (parts[0].ToUpperInvariant(), parts[1], remainder);
                }
                return (null, null, Array.Empty<byte>());
            }
        }
        return (null, null, Array.Empty<byte>());
    }

    /// <summary>
    /// 分流连接：加速域名走 DoH 解析直连（绕过污染 DNS），其余普通直连。
    /// </summary>
    async Task<TcpClient?> ConnectUpstreamAsync(string host, int port, CancellationToken ct)
    {
        var isAccelerate = IsAccelerateDomain(host);
        IPAddress[] addresses;

        if (isAccelerate)
        {
            addresses = await ResolveViaDohAsync(host, ct);
            if (addresses.Length == 0)
            {
                Logger.Warn("DoH resolve failed for {Host}, fallback system DNS", host);
                addresses = await Dns.GetHostAddressesAsync(host, ct);
            }
            else
            {
                Logger.Info("Accelerate {Host} -> {IP}", host, addresses.FirstOrDefault());
            }
        }
        else
        {
            if (IPAddress.TryParse(host, out var directIp))
            {
                addresses = new[] { directIp };
            }
            else
            {
                addresses = await Dns.GetHostAddressesAsync(host, ct);
            }
        }

        foreach (var ip in addresses)
        {
            var tcp = new TcpClient();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
                await tcp.ConnectAsync(ip, port, timeoutCts.Token);
                return tcp;
            }
            catch (System.Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                tcp.Dispose();
            }
        }
        return null;
    }

    bool IsAccelerateDomain(string host)
    {
        host = host.ToLowerInvariant();
        if (_accelerateDomains.Contains(host)) return true;
        // 逐级父域匹配（host = a.steamcommunity.com 匹配 steamcommunity.com）
        var idx = host.IndexOf('.');
        while (idx > 0)
        {
            var parent = host[(idx + 1)..];
            if (_accelerateDomains.Contains(parent)) return true;
            idx = parent.IndexOf('.');
        }
        return false;
    }

    /// <summary>
    /// DoH 解析（RFC 8484，GET 带 dns= base64url 参数），使用上游同款公共 DoH 服务。
    /// </summary>
    async Task<IPAddress[]> ResolveViaDohAsync(string host, CancellationToken ct)
    {
        foreach (var doh in _dohServers)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                var query = BuildDohQuery(host);
                var url = $"{doh}?dns={query}";
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("accept", "application/dns-message");
                using var resp = await http.SendAsync(req, timeoutCts.Token);
                if (!resp.IsSuccessStatusCode) continue;
                var data = await resp.Content.ReadAsByteArrayAsync(timeoutCts.Token);
                var ips = ParseDnsMessage(data, host);
                if (ips.Length > 0) return ips;
            }
            catch (System.Exception ex)
            {
                Logger.Warn(ex, "DoH {Server} failed", doh);
            }
        }
        return Array.Empty<IPAddress>();
    }

    static string BuildDohQuery(string host)
    {
        // 构造 DNS 查询报文：header(12) + QNAME + QTYPE(2) + QCLASS(2)
        var labels = host.Split('.');
        var len = 12 + 1 + labels.Sum(l => l.Length + 1) + 4;
        var buf = new byte[len];
        // ID=0, flags=0x0100 (RD)
        buf[2] = 0x01; buf[3] = 0x00;
        buf[5] = 1; // QDCOUNT=1
        var pos = 12;
        foreach (var label in labels)
        {
            buf[pos++] = (byte)label.Length;
            Encoding.ASCII.GetBytes(label, 0, label.Length, buf, pos);
            pos += label.Length;
        }
        buf[pos++] = 0; // root
        buf[pos++] = 0; buf[pos++] = 1; // QTYPE=A
        buf[pos++] = 0; buf[pos] = 1;   // QCLASS=IN
        return Convert.ToBase64String(buf).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    static IPAddress[] ParseDnsMessage(byte[] data, string host)
    {
        var result = new List<IPAddress>();
        try
        {
            if (data.Length < 12) return result.ToArray();
            var qd = (data[4] << 8) | data[5];
            var an = (data[6] << 8) | data[7];
            var pos = 12;
            // skip questions
            for (var i = 0; i < qd && pos < data.Length; i++)
            {
                while (pos < data.Length && data[pos] != 0) pos += data[pos] + 1;
                pos += 5; // root + qtype + qclass
            }
            // parse answers
            for (var i = 0; i < an && pos + 12 <= data.Length; i++)
            {
                // name (可能是指针压缩 0xC0)
                if ((data[pos] & 0xC0) == 0xC0) pos += 2;
                else { while (pos < data.Length && data[pos] != 0) pos += data[pos] + 1; pos++; }
                if (pos + 10 > data.Length) break;
                var type = (data[pos] << 8) | data[pos + 1];
                var rdLength = (data[pos + 8] << 8) | data[pos + 9];
                pos += 10;
                if (type == 1 && rdLength == 4 && pos + 4 <= data.Length) // A record
                {
                    result.Add(new IPAddress(data.AsSpan(pos, 4)));
                }
                pos += rdLength;
            }
        }
        catch { }
        return result.ToArray();
    }

    static async Task PumpAsync(Stream a, Stream b, CancellationToken ct)
    {
        var t1 = a.CopyToAsync(b, ct);
        var t2 = b.CopyToAsync(a, ct);
        await Task.WhenAny(t1, t2);
        try { b.Dispose(); } catch { }
    }
}
