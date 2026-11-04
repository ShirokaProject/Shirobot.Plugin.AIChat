using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Buffers;
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
        const int maximumResponseBytes = 32_000_000;
        var length = response.Content.Headers.ContentLength;
        if (length > maximumResponseBytes) throw new InvalidOperationException("生图响应超过大小限制。");
        using var buffer = new MemoryStream(length is >= 0 ? checked((int)length.Value) : 16 * 1024);
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(0, 16 * 1024), timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maximumResponseBytes) throw new InvalidOperationException("生图响应超过大小限制。");
                buffer.Write(chunk, 0, read);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(chunk); }
        return DecodeResponse(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }

    // A DOM also rents a metadata buffer based on the multi-MB JSON size even
    // though the response contains just one image. Walk tokens without a DOM.
    internal static byte[] DecodeResponse(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected an image response object.");
        byte[]? bytes = null;
        bool ended = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) { ended = true; break; }
            bool data = reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("data"u8);
            if (reader.TokenType != JsonTokenType.PropertyName || !reader.Read()) throw new JsonException();
            if (data) bytes = null;
            if (!data || reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); continue; }
            if (!reader.Read()) throw new JsonException();
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    bool image = reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("b64_json"u8);
                    if (reader.TokenType != JsonTokenType.PropertyName || !reader.Read()) throw new JsonException();
                    if (image && reader.TokenType == JsonTokenType.String) bytes = DecodeBase64(ref reader);
                    else
                    {
                        if (image) bytes = null;
                        reader.Skip();
                    }
                }
            }
            else if (reader.TokenType != JsonTokenType.EndArray) reader.Skip();
            if (reader.TokenType != JsonTokenType.EndArray)
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) reader.Skip();
        }
        if (!ended || reader.Read()) throw new JsonException("Incomplete or trailing image response JSON.");
        if (bytes is null) throw new InvalidOperationException("生图接口未返回 data[0].b64_json 图片。");
        if (bytes.Length == 0 || bytes.Length > 20_000_000) throw new InvalidOperationException("生成图片为空或超过 20 MB。");
        if (!(bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
              bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff ||
              bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)))
            throw new InvalidOperationException("接口返回的不是 PNG、JPEG 或 WebP 图片。");
        return bytes;
    }

    private static byte[] DecodeBase64(ref Utf8JsonReader reader)
    {
        // Utf8JsonReader.GetBytesFromBase64 rents another image-sized buffer and
        // then copies it. Decode unescaped UTF-8 directly into the final array.
        if (reader.ValueIsEscaped) return reader.GetBytesFromBase64();
        var encoded = reader.ValueSpan;
        if (!System.Buffers.Text.Base64.IsValid(encoded, out int length)) throw new FormatException("Invalid image Base64.");
        if (length is <= 0 or > 20_000_000) throw new InvalidOperationException("生成图片为空或超过 20 MB。");
        var bytes = new byte[length];
        if (System.Buffers.Text.Base64.DecodeFromUtf8(encoded, bytes, out _, out int written) != OperationStatus.Done || written != length)
            throw new FormatException("Invalid image Base64.");
        return bytes;
    }
}
