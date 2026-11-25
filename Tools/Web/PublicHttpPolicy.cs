using System.Net;
using System.Net.Sockets;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Tools.Web;

/// <summary>公网抓取专用策略。重定向逐次检查；连接时解析并直接连接核验后的 IP，避免 DNS 重绑定。</summary>
internal static class PublicHttpPolicy
{
    public static void ValidateUrl(Uri url)
    {
        // 任意端口都允许；目标是否安全由下面的主机名检查和连接时的公网 IP 校验决定。
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.UserInfo))
            throw new InvalidOperationException("Only public HTTP/HTTPS URLs without credentials are allowed.");
        var host = url.DnsSafeHost.TrimEnd('.').Trim('[', ']').ToLowerInvariant();
        if (IPAddress.TryParse(host, out var address))
        {
            if (!IsPublicAddress(address)) throw new InvalidOperationException("Local, private and reserved addresses are blocked.");
        }
        else if (!host.Contains('.') || host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".internal") ||
                 host is "metadata.google.internal" or "metadata.azure.internal")
            throw new InvalidOperationException("Local and internal host names are blocked.");
    }
    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            // Conservatively exclude IANA special-purpose ranges, including cloud metadata/link-local.
            // https://www.iana.org/assignments/iana-ipv4-special-registry/
            return !(ip.Equals(IPAddress.Parse("168.63.129.16")) || b[0] is 0 or 10 or 127 || b[0] >= 224 ||
                b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 169 && b[1] == 254 ||
                b[0] == 172 && b[1] is >= 16 and <= 31 ||
                b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2 || b[1] == 88 && b[2] == 99) ||
                b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100) ||
                b[0] == 203 && b[1] == 0 && b[2] == 113);
        }
        if (ip.AddressFamily != AddressFamily.InterNetworkV6 || ip.ScopeId != 0) return false;
        // Only native global unicast, excluding special-purpose/tunnel/documentation ranges.
        // https://www.iana.org/assignments/iana-ipv6-special-registry/
        return (b[0] & 0xe0) == 0x20 &&
            !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || b[2] == 0x0d && b[3] == 0xb8)) &&
            !(b[0] == 0x20 && b[1] == 0x02) &&
            !(b[0] == 0x3f && (b[1] == 0xfe || b[1] == 0xff));
    }
    internal static async Task<IPAddress[]> ResolvePublicAsync(string host, CancellationToken ct,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? publicResolver = null)
    {
        var addresses = await (resolver ?? Dns.GetHostAddressesAsync)(host.Trim('[', ']'), ct).ConfigureAwait(false);
        // 只识别域名全部解析到 198.18.0.0/15 的 Fake-IP 场景；绝不直接连接该地址。
        // 真正的内网解析、混合地址和用户输入的保留地址仍会被拒绝。
        if (!IPAddress.TryParse(host.Trim('[', ']'), out _) && addresses.Length > 0 && addresses.All(IsFakeIp))
        {
            BotLog.Info($"[AiChat] 检测到 Fake-IP DNS，使用公网 DNS 重新解析 {host}。");
            addresses = await (publicResolver ?? PublicDnsResolver.ResolveAsync)(host, ct).ConfigureAwait(false);
        }
        if (addresses.Length == 0 || addresses.Any(ip => !IsPublicAddress(ip)))
            throw new InvalidOperationException("The host resolves to a local, private or reserved address and is blocked.");
        return addresses;
    }
    private static bool IsFakeIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 198 && bytes[1] is 18 or 19;
    }

    /// <param name="proxy">可选 HTTP 代理（network.http_proxy）。走代理时仍由本地解析并核验目标 IP，
    /// 再向代理发 CONNECT 到该 IP，代理只做转发，不替我们解析域名，因此私网拦截和防重绑定不受影响。
    /// 代理不可用时退回直连。</param>
    public static SocketsHttpHandler CreateHandler(string? proxy = null)
    {
        var tunnel = ParseProxy(proxy);
        return new()
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            MaxResponseHeadersLength = 32,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await ResolvePublicAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
                var port = context.DnsEndPoint.Port;
                Exception? last = null;
                if (tunnel is not null)
                {
                    foreach (var ip in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6))
                    {
                        try { return await ConnectViaProxyAsync(tunnel, ip, port, ct).ConfigureAwait(false); }
                        catch (Exception ex) when (!ct.IsCancellationRequested) { last = ex; }
                    }
                    BotLog.Warning($"[AiChat] http_fetch 经代理连接 {context.DnsEndPoint.Host} 失败，改为直连：{last?.Message}");
                }
                foreach (var ip in addresses)
                {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(ip, port), ct).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { socket.Dispose(); last = ex; }
                    catch { socket.Dispose(); throw; }
                }
                throw new HttpRequestException("Could not connect to the public HTTP target.", last);
            }
        };
    }

    internal sealed record ProxyEndpoint(string Host, int Port, string? Authorization);

    internal static ProxyEndpoint? ParseProxy(string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return null;
        if (!Uri.TryCreate(proxy.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "http")
        {
            BotLog.Warning("[AiChat] network.http_proxy 不是 http:// 代理，http_fetch 将直连。");
            return null;
        }
        string? auth = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            auth = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(uri.UserInfo)));
        return new ProxyEndpoint(uri.DnsSafeHost, uri.Port, auth);
    }

    /// <summary>连到运维配置的代理，CONNECT 到已核验的公网 IP。TLS 由 SocketsHttpHandler 在隧道上按原域名完成。</summary>
    internal static async Task<Stream> ConnectViaProxyAsync(ProxyEndpoint proxy, IPAddress target, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(proxy.Host, proxy.Port, ct).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            var authority = target.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{target}]:{port}" : $"{target}:{port}";
            var request = $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n" +
                (proxy.Authorization is null ? "" : $"Proxy-Authorization: {proxy.Authorization}\r\n") + "\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(request), ct).ConfigureAwait(false);
            // 逐字节读到空行为止，避免把隧道里的后续数据读进缓冲区。
            var header = new List<byte>(256);
            var one = new byte[1];
            while (header.Count < 8192)
            {
                if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0) throw new IOException("Proxy closed the connection.");
                header.Add(one[0]);
                var n = header.Count;
                if (n >= 4 && header[n - 4] == '\r' && header[n - 3] == '\n' && header[n - 2] == '\r' && header[n - 1] == '\n') break;
            }
            var statusLine = System.Text.Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", 2)[0];
            var parts = statusLine.Split(' ', 3);
            if (parts.Length < 2 || !parts[0].StartsWith("HTTP/1.") || parts[1] != "200")
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw new HttpRequestException($"Proxy CONNECT failed: {statusLine}");
            }
            return stream;
        }
        catch { socket.Dispose(); throw; }
    }
}
