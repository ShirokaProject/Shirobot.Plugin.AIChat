using System.Net;

namespace ShiroBot.AiChatPlugin;

internal static class HttpProxy
{
    public static HttpClientHandler CreateHandler(string httpProxy)
    {
        if (string.IsNullOrWhiteSpace(httpProxy)) return new HttpClientHandler();
        if (!Uri.TryCreate(httpProxy.Trim(), UriKind.Absolute, out var proxyUri))
            throw new ArgumentException("HTTP 代理地址格式错误。");

        return new HttpClientHandler { Proxy = new WebProxy(proxyUri), UseProxy = true };
    }
}
