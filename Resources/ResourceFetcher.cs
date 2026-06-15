using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Resources;

/// <summary>
/// HTTP 下载器。共享一个 HttpClient 实例。
/// </summary>
internal sealed class ResourceFetcher
{
    private readonly HttpClient _http;
    private readonly long _maxImageBytes;
    private readonly long _maxInlineTextBytes;

    public ResourceFetcher(int timeoutSeconds, long maxImageBytes, long maxInlineTextBytes)
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds))
        };
        // QQ CDN 一些链接对 UA 比较敏感，给个浏览器化默认值。
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; ShiroBot/1.0)");
        _maxImageBytes = maxImageBytes;
        _maxInlineTextBytes = maxInlineTextBytes;
    }

    /// <summary>
    /// 下载并返回 (字节流, contentType)。超过 imageMaxBytes 会抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public async Task<(byte[] Bytes, string? ContentType)> DownloadImageAsync(string url, CancellationToken ct = default)
        => await DownloadAsync(url, _maxImageBytes, ct).ConfigureAwait(false);

    public async Task<(byte[] Bytes, string? ContentType)> DownloadTextFileAsync(string url, CancellationToken ct = default)
        => await DownloadAsync(url, _maxInlineTextBytes, ct).ConfigureAwait(false);

    private async Task<(byte[] Bytes, string? ContentType)> DownloadAsync(string url, long maxBytes, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is { } len && len > maxBytes)
            {
                throw new InvalidOperationException($"资源大小超出限制: {len} > {maxBytes}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + read > maxBytes)
                {
                    throw new InvalidOperationException($"资源大小超出限制: > {maxBytes} 字节");
                }
                await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            return (ms.ToArray(), response.Content.Headers.ContentType?.MediaType);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            BotLog.Warning($"[AiChat] 下载失败 {url}: {ex.Message}");
            throw;
        }
    }
}
