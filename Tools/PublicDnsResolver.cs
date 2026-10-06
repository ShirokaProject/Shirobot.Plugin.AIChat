using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace ShiroBot.AiChatPlugin.Tools;

/// <summary>遇到本机 Fake-IP 时查询可信公网 DNS；DoH 连接固定公网地址，不依赖本机 DNS，也不关闭 TLS 验证。</summary>
internal static class PublicDnsResolver
{
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5), MaxResponseHeadersLength = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) =>
            {
                if (context.DnsEndPoint.Host != "cloudflare-dns.com" || context.DnsEndPoint.Port != 443)
                    throw new InvalidOperationException("Unexpected public DNS endpoint.");
                Exception? last = null;
                foreach (var address in new[] { "1.1.1.1", "1.0.0.1" })
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(address), 443), ct).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { socket.Dispose(); last = ex; }
                    catch { socket.Dispose(); throw; }
                }
                throw new HttpRequestException("Public DNS endpoint is unavailable.", last);
            }
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.Accept.ParseAdd("application/dns-json");
        return client;
    }
    public static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        var answers = await Task.WhenAll(QueryAsync(host, 1, ct), QueryAsync(host, 28, ct)).ConfigureAwait(false);
        return answers.SelectMany(a => a).Distinct().ToArray();
    }
    private static async Task<IPAddress[]> QueryAsync(string host, int type, CancellationToken callerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;
        var url = $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type={type}";
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 32768) throw new InvalidOperationException("DNS response exceeds size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(bytes, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > 32768) throw new InvalidOperationException("DNS response exceeds size limit.");
            buffer.Write(bytes, 0, count);
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        if (doc.RootElement.GetProperty("Status").GetInt32() != 0 ||
            doc.RootElement.TryGetProperty("TC", out var truncated) && truncated.GetBoolean())
            throw new InvalidOperationException("Public DNS resolution failed.");
        var ips = new List<IPAddress>();
        if (doc.RootElement.TryGetProperty("Answer", out var answers))
            foreach (var answer in answers.EnumerateArray())
                if (answer.GetProperty("type").GetInt32() == type &&
                    IPAddress.TryParse(answer.GetProperty("data").GetString(), out var ip)) ips.Add(ip);
        return ips.ToArray();
    }
}
