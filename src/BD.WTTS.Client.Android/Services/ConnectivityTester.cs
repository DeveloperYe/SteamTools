using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace WattToolkit;

/// <summary>
/// 加速连通性三层测试：
///   1. 代理层：直连 127.0.0.1:26501 发 HTTP CONNECT（tun2socks→代理链路）
///   2. DoH 层：走 WattProxyServer 同款 DoH 解析加速域名
///   3. 端到端：HttpClient 走系统路由访问 https 加速站点（真实用户路径）
/// </summary>
public static class ConnectivityTester
{
    public sealed record TestResult(string Name, bool Ok, string Detail, long ElapsedMs);

    public static readonly (string Name, string Host)[] Targets =
    {
        ("Steam 社区", "steamcommunity.com"),
        ("GitHub", "github.com"),
    };

    /// <summary>依次跑三层测试，逐项回调（UI 线程更新）。</summary>
    public static async Task RunAllAsync(Action<TestResult> onResult, CancellationToken ct = default)
    {
        // ---- 1. 代理层自检 ----
        foreach (var (name, host) in Targets)
        {
            var r = await TestProxyAsync(host, ct);
            onResult(new TestResult($"[代理] {name} CONNECT", r.Ok, r.Detail, r.Ms));
        }

        // ---- 2. DoH 解析 ----
        foreach (var (name, host) in Targets)
        {
            var r = await TestDohAsync(host, ct);
            onResult(new TestResult($"[DoH] {name}", r.Ok, r.Detail, r.Ms));
        }

        // ---- 3. 端到端 HTTPS（真实用户路径）----
        foreach (var (name, host) in Targets)
        {
            var r = await TestEndToEndAsync(host, ct);
            onResult(new TestResult($"[端到端] {name} HTTPS", r.Ok, r.Detail, r.Ms));
        }
    }

    /// <summary>
    /// 直连本地代理 26501 发 CONNECT host:443，收到 "200" 即链路通。
    /// 注意：这个测试绕过 VPN（本机 App 已被 AddDisallowedApplication 排除），
    /// 测的是 WattProxyServer 自身是否存活可用。
    /// </summary>
    static async Task<(bool Ok, string Detail, long Ms)> TestProxyAsync(string host, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(5000);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, 26501, timeoutCts.Token);
            using var stream = client.GetStream();

            var req = Encoding.ASCII.GetBytes($"CONNECT {host}:443 HTTP/1.1\r\nHost: {host}:443\r\n\r\n");
            await stream.WriteAsync(req, timeoutCts.Token);

            var buf = new byte[256];
            var n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), timeoutCts.Token);
            var resp = Encoding.ASCII.GetString(buf, 0, n);
            var ok = resp.StartsWith("HTTP/1.1 200");
            return (ok, ok ? "CONNECT 200 ✓" : resp.Split('\r')[0], sw.ElapsedMilliseconds);
        }
        catch (System.Exception ex)
        {
            return (false, $"失败: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// DoH 解析（与 WattProxyServer 内同款 RFC8484 实现，独立跑一次验证 DNS 修复通道）。
    /// </summary>
    static async Task<(bool Ok, string Detail, long Ms)> TestDohAsync(string host, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(6000);
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            var query = BuildDohQuery(host);
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://dns.alidns.com/dns-query?dns={query}");
            req.Headers.Add("accept", "application/dns-message");
            using var resp = await http.SendAsync(req, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode)
                return (false, $"DoH HTTP {(int)resp.StatusCode}", sw.ElapsedMilliseconds);
            var data = await resp.Content.ReadAsByteArrayAsync(timeoutCts.Token);
            var ips = ParseDnsARecords(data);
            if (ips.Length == 0)
                return (false, "DoH 响应无 A 记录", sw.ElapsedMilliseconds);
            return (true, $"解析到 {string.Join(", ", ips.Take(2))}", sw.ElapsedMilliseconds);
        }
        catch (System.Exception ex)
        {
            return (false, $"失败: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 端到端：走系统网络栈（VPN 生效时即真实加速路径）访问目标 HTTPS。
    /// 成功标准：任何 HTTP 响应（哪怕 403）都证明 TCP+TLS 全链路通。
    /// </summary>
    static async Task<(bool Ok, string Detail, long Ms)> TestEndToEndAsync(string host, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(10000);
            using var http = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            });
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Linux; Android) WattToolkit/1.0 ConnectivityTest");
            using var resp = await http.GetAsync($"https://{host}/", HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            return (true, $"HTTP {(int)resp.StatusCode} {resp.StatusCode}", sw.ElapsedMilliseconds);
        }
        catch (System.Exception ex)
        {
            var reason = ex is HttpRequestException hre && hre.InnerException != null
                ? hre.InnerException.Message : ex.Message;
            return (false, $"失败: {reason}", sw.ElapsedMilliseconds);
        }
    }

    // ---- 与 WattProxyServer 相同的 DoH 报文构造/解析（复制以保持测试独立性） ----

    static string BuildDohQuery(string host)
    {
        var labels = host.Split('.');
        var len = 12 + 1 + labels.Sum(l => l.Length + 1) + 4;
        var buf = new byte[len];
        buf[2] = 0x01; buf[3] = 0x00;
        buf[5] = 1;
        var pos = 12;
        foreach (var label in labels)
        {
            buf[pos++] = (byte)label.Length;
            Encoding.ASCII.GetBytes(label, 0, label.Length, buf, pos);
            pos += label.Length;
        }
        buf[pos++] = 0;
        buf[pos++] = 0; buf[pos++] = 1;
        buf[pos++] = 0; buf[pos] = 1;
        return Convert.ToBase64String(buf).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    static IPAddress[] ParseDnsARecords(byte[] data)
    {
        var result = new List<IPAddress>();
        try
        {
            if (data.Length < 12) return result.ToArray();
            var qd = (data[4] << 8) | data[5];
            var an = (data[6] << 8) | data[7];
            var pos = 12;
            for (var i = 0; i < qd && pos < data.Length; i++)
            {
                while (pos < data.Length && data[pos] != 0) pos += data[pos] + 1;
                pos += 5;
            }
            for (var i = 0; i < an && pos + 12 <= data.Length; i++)
            {
                if ((data[pos] & 0xC0) == 0xC0) pos += 2;
                else { while (pos < data.Length && data[pos] != 0) pos += data[pos] + 1; pos++; }
                if (pos + 10 > data.Length) break;
                var type = (data[pos] << 8) | data[pos + 1];
                var rdLength = (data[pos + 8] << 8) | data[pos + 9];
                pos += 10;
                if (type == 1 && rdLength == 4 && pos + 4 <= data.Length)
                    result.Add(new IPAddress(data.AsSpan(pos, 4)));
                pos += rdLength;
            }
        }
        catch { }
        return result.ToArray();
    }
}
