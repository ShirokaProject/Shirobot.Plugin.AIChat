using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;

namespace ShiroBot.AiChatPlugin.OpenAi;

internal sealed class ImageGenerationException(string message) : Exception(message);

internal sealed record ImageGenerationInput(byte[] Bytes, string Mime);

internal static class ImageGenerationClient
{
    internal static async Task<byte[]> GenerateAsync(string baseUrl, string key, string model, string prompt,
        ImageGenerationSection config, string proxy, CancellationToken ct, IReadOnlyList<ImageGenerationInput>? images = null)
    {
        using var http = new HttpClient(HttpProxy.CreateHandler(proxy)) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 30, 1800)));
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + (images is { Count: > 0 } ? "/images/edits" : "/images/generations"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (images is { Count: > 0 })
        {
            var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(model), "model"); multipart.Add(new StringContent(prompt), "prompt");
            multipart.Add(new StringContent("1"), "n"); multipart.Add(new StringContent(config.Size), "size");
            multipart.Add(new StringContent(config.Quality), "quality");
            foreach (var (image, index) in images.Select((image, index) => (image, index)))
            {
                var content = new ByteArrayContent(image.Bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(image.Mime);
                var ext = image.Mime == "image/jpeg" ? "jpg" : image.Mime == "image/webp" ? "webp" : "png";
                multipart.Add(content, "image[]", $"reference-{index}.{ext}");
            }
            request.Content = multipart;
        }
        else request.Content = new StringContent(PluginJson.Serialize(new { model, prompt, n = 1,
            size = config.Size, quality = config.Quality }), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ImageGenerationException(response.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "生图接口拒绝访问（HTTP 403），请检查 API 密钥所属分组是否已开放生图权限。"
                : response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "生图接口鉴权失败（HTTP 401），请检查服务商密钥。"
                    : $"生图接口返回 HTTP {(int)response.StatusCode}，请检查服务商状态与模型权限。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > 32_000_000) throw new InvalidOperationException("生图响应超过大小限制。");
            buffer.Write(chunk, 0, read);
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0 ||
            !data[0].TryGetProperty("b64_json", out var encoded) || encoded.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("生图接口未返回 data[0].b64_json 图片。");
        var bytes = Convert.FromBase64String(encoded.GetString()!);
        if (bytes.Length == 0 || bytes.Length > 20_000_000) throw new InvalidOperationException("生成图片为空或超过 20 MB。");
        if (!(bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
              bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff ||
              bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)))
            throw new InvalidOperationException("接口返回的不是 PNG、JPEG 或 WebP 图片。");
        return bytes;
    }
}
