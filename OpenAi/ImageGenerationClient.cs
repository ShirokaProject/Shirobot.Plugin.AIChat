using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Buffers;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Resources;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.OpenAi;

internal sealed class ImageGenerationException(string message) : Exception(message);

internal sealed record ImageGenerationInput(byte[] Bytes, string Mime);

internal static class ImageGenerationClient
{
    internal static bool IsValidSize(string size)
    {
        if (size == "auto") return true;
        var edges = size.Split('x');
        return edges.Length == 2 && edges.All(e => e.Length > 0 && e.All(char.IsAsciiDigit)) &&
            int.TryParse(edges[0], out var width) && int.TryParse(edges[1], out var height) &&
            width > 0 && height > 0 && width % 16 == 0 && height % 16 == 0 &&
            (long)width <= 3L * height && (long)height <= 3L * width &&
            Math.Max(width, height) <= 3840 && Math.Min(width, height) <= 2160 &&
            (long)width * height <= 3840L * 2160;
    }

    internal static void ValidateOptions(ImageGenerationSection config)
    {
        if (config.OutputFormat == "gif" && !config.EnableGif) throw new ImageGenerationException("GIF生成功能暂时关闭，请选择PNG、JPEG或WebP。");
        if (!IsValidSize(config.Size)) throw new ImageGenerationException("图片尺寸无效，请使用 auto 或宽高均为 16 倍数、宽高比在 1:3 到 3:1 之间的尺寸，最大 3840×2160（可旋转）。");
        if (config.Quality is not ("auto" or "low" or "medium" or "high" or "xhigh" or "max"))
            throw new ImageGenerationException("图片质量必须为 auto、low、medium、high、xhigh 或 max，请确认模型支持。");
        if (config.Background is not ("auto" or "opaque" or "transparent"))
            throw new ImageGenerationException("图片背景必须为 auto、opaque 或 transparent。");
        if (config.OutputFormat is not ("png" or "jpeg" or "webp" or "gif"))
            throw new ImageGenerationException("图片输出格式必须为 png、jpeg、webp 或 gif。");
        if (config.Background == "transparent" && config.OutputFormat is not ("png" or "webp"))
            throw new ImageGenerationException("透明背景需要 png 或 webp 输出格式。");
        if (config.OutputFormat == "gif" && (config.FrameCount is < 2 or > 16 || config.FrameDurationMs is < 20 or > 2000))
            throw new ImageGenerationException("GIF 帧数应为2–16，每帧时长应为20–2000毫秒。");
        if (config.OutputCompression is < 0 or > 100)
            throw new ImageGenerationException("图片压缩质量必须在 0–100 之间。");
    }

    internal static async Task<byte[]> GenerateAsync(string baseUrl, string key, string model, string prompt,
        ImageGenerationSection config, string proxy, CancellationToken ct, IReadOnlyList<ImageGenerationInput>? images = null, HttpMessageHandler? handler = null)
    {
        ValidateOptions(config);
        using var http = new HttpClient(handler ?? HttpProxy.CreateHandler(proxy)) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 30, 1800)));
        var omitted = new HashSet<string>(StringComparer.Ordinal);
        var repaired = new HashSet<string>(StringComparer.Ordinal);
        var size = config.Size;
        var quality = config.Quality;
        for (var attempt = 0; ; attempt++)
        {
        timeout.Token.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + (images is { Count: > 0 } ? "/images/edits" : "/images/generations"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (images is { Count: > 0 })
        {
            var multipart = new MultipartFormDataContent();
            void AddField(string value, string name) { if (!omitted.Contains(name)) multipart.Add(new StringContent(value), name); }
            AddField(model, "model"); AddField(prompt, "prompt");
            AddField("1", "n"); AddField(size, "size");
            AddField(quality, "quality");
            AddField(config.Background, "background");
            AddField(config.OutputFormat, "output_format");
            if (config.OutputFormat is "jpeg" or "webp")
                AddField(config.OutputCompression.ToString(System.Globalization.CultureInfo.InvariantCulture), "output_compression");
            if (config.OutputFormat == "gif")
            {
                AddField(config.FrameCount.ToString(System.Globalization.CultureInfo.InvariantCulture), "frame_count");
                AddField(config.FrameDurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture), "frame_duration_ms");
            }
            foreach (var (image, index) in images.Select((image, index) => (image, index)))
            {
                var content = new ByteArrayContent(image.Bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(image.Mime);
                var ext = image.Mime == "image/jpeg" ? "jpg" : image.Mime == "image/webp" ? "webp" : image.Mime == "image/gif" ? "gif" : "png";
                multipart.Add(content, "image[]", $"reference-{index}.{ext}");
            }
            request.Content = multipart;
        }
        else
        {
            var body = new System.Text.Json.Nodes.JsonObject
            {
                ["model"] = model, ["prompt"] = prompt, ["n"] = 1,
                ["size"] = size, ["quality"] = quality,
                ["background"] = config.Background, ["output_format"] = config.OutputFormat
            };
            if (config.OutputFormat is "jpeg" or "webp") body["output_compression"] = config.OutputCompression;
            if (config.OutputFormat == "gif") { body["frame_count"] = config.FrameCount; body["frame_duration_ms"] = config.FrameDurationMs; }
            foreach (var name in omitted) body.Remove(name);
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var error = await ReadErrorAsync(response.Content, timeout.Token).ConfigureAwait(false);
                if (attempt < 2 && images is { Count: > 0 } && !repaired.Contains("gif_input") &&
                    error.Code is "invalid_image_file" or "invalid_image_format" or "unsupported_image_format" &&
                    images.Any(image => image.Mime == "image/gif"))
                {
                    try
                    {
                        images = images.Select(image => image.Mime == "image/gif" ? ReferenceImage.ToPng(image.Bytes) : image).ToArray();
                    }
                    catch (ArgumentException ex) { throw new ImageGenerationException(ex.Message); }
                    repaired.Add("gif_input");
                    BotLog.Info("[AiChat] 上游拒绝GIF参考图，转为首帧PNG后静默重试。");
                    continue;
                }
                if (attempt < 2 && TryRepair(error, config, omitted, repaired, ref size, ref quality))
                {
                    // Never log upstream text, prompts, URLs or credentials.
                    BotLog.Info($"[AiChat] 生图HTTP 400，静默兼容重试 {attempt + 1}/2。");
                    continue;
                }
                if (config.OutputFormat == "gif" && (error.Message.Contains("gif", StringComparison.OrdinalIgnoreCase) || error.Param == "output_format") &&
                    new[] { "not enabled", "not supported", "not available", "unsupported", "preview" }.Any(reason => error.Message.Contains(reason, StringComparison.OrdinalIgnoreCase)))
                    throw new ImageGenerationException("当前生图接口或模型未开放 GIF 输出，请更换已支持GIF的接口/模型，或选择静态图片格式。");
                throw new ImageGenerationException(attempt > 0 ? "生图接口返回 HTTP 400，兼容重试后仍失败；请检查模型是否支持所选格式、尺寸及参考图。" : "生图接口返回 HTTP 400，无法自动修正；请检查内容、模型和所选参数。" );
            }
            throw new ImageGenerationException(response.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "生图接口拒绝访问（HTTP 403），请检查 API 密钥所属分组是否已开放生图权限。"
                : response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "生图接口鉴权失败（HTTP 401），请检查服务商密钥。"
                    : $"生图接口返回 HTTP {(int)response.StatusCode}，请检查服务商状态与模型权限。");
        }
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
    }

    internal sealed record UpstreamError(string Param, string Code, string Message);
    private static async Task<UpstreamError> ReadErrorAsync(HttpContent content, CancellationToken ct)
    {
        const int limit = 16384;
        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var bytes = new byte[limit + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await input.ReadAsync(bytes.AsMemory(count), ct).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count > limit) return new("", "oversized_error", "");
        try
        {
            using var doc = JsonDocument.Parse(bytes.AsMemory(0, count));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error)) root = error;
            if (root.ValueKind == JsonValueKind.String) return new("", "", root.GetString() ?? "");
            string Get(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
            return new(Get("param"), Get("code"), Get("message"));
        }
        catch (JsonException) { return new("", "unparsed_error", ""); }
    }

    internal static bool TryRepair(UpstreamError error, ImageGenerationSection config, HashSet<string> omitted,
        HashSet<string> repaired, ref string size, ref string quality)
    {
        if (error.Code is "oversized_error" or "unparsed_error") return false;
        var diagnostic = (error.Code + " " + error.Message).ToLowerInvariant();
        if (config.OutputFormat == "gif" && diagnostic.Contains("gif") &&
            new[] { "not enabled", "not supported", "not available", "unsupported", "preview" }.Any(diagnostic.Contains)) return false;
        if (new[] { "safety", "moderation", "policy", "content_filter", "content filter", "refusal", "billing", "quota", "permission", "api key", "authentication", "unauthorized" }.Any(diagnostic.Contains)) return false;
        var param = error.Param.ToLowerInvariant().Split('.').Last();
        var adjustable = new[] { "output_compression", "background", "quality", "size", "output_format", "frame_count", "frame_duration_ms" };
        if (param.Length == 0)
        {
            // Only infer a field from a genuine parameter-related error.
            if (new[] { "unsupported", "not support", "invalid", "unknown", "unrecognized", "must be", "must have", "expected" }.Any(diagnostic.Contains))
                param = adjustable.FirstOrDefault(diagnostic.Contains) ?? "";
        }
        if (param.Length > 0)
        {
            if (!adjustable.Contains(param) || !repaired.Add(param)) return false;
            switch (param)
            {
                case "size":
                    var standard = "1024x1024";
                    var edges = size.Split('x');
                    if (edges.Length == 2 && int.TryParse(edges[0], out var width) && int.TryParse(edges[1], out var height))
                        standard = width > height ? "1536x1024" : height > width ? "1024x1536" : standard;
                    if (size == standard) return omitted.Add("size");
                    size = standard; return true;
                case "quality":
                    if (quality is "xhigh" or "max") { quality = "high"; return true; }
                    return omitted.Add("quality");
                case "background": return omitted.Add(param);
                case "output_format": return config.OutputFormat == "png" && omitted.Add(param);
                case "output_compression": return config.OutputFormat is "jpeg" or "webp" && omitted.Add(param);
                case "frame_count": return config.OutputFormat == "gif" && config.FrameCount == 9 && omitted.Add(param);
                case "frame_duration_ms": return config.OutputFormat == "gif" && config.FrameDurationMs == 200 && omitted.Add(param);
            }
            return false;
        }
        // A generic 400 may come from a strict compatible gateway: remove only defaults.
        if (!repaired.Add("defaults")) return false;
        var changed = false;
        if (config.Background == "auto") changed |= omitted.Add("background");
        if (quality == "auto") changed |= omitted.Add("quality");
        if (config.OutputFormat == "png") changed |= omitted.Add("output_format");
        return changed;
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
        if (!(bytes.AsSpan().StartsWith("GIF87a"u8) || bytes.AsSpan().StartsWith("GIF89a"u8) || bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
              bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff ||
              bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)))
            throw new InvalidOperationException("接口返回的不是 PNG、JPEG、WebP 或 GIF 图片。");
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
