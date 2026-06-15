using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.OpenAi;

/// <summary>
/// 兼容 OpenAI chat.completions 协议的 HTTP 客户端。
/// 按 base_url 复用 HttpClient（每个 endpoint 一个实例）。
/// </summary>
internal sealed class OpenAiClient(int timeoutSeconds)
{
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProviderEntry> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _clientsLock = new();
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds));
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // content 字段是 object，需要默认 polymorphic 处理 string vs list
    };

    // content 字段是 object，需要默认 polymorphic 处理 string vs list

    public async Task<string> ChatAsync(
        DefaultSection defaults,
        ModelEntry model,
        OpenAiChatRequest request,
        CancellationToken ct = default)
    {
        var (baseUrl, apiKey) = ResolveEndpoint(model);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("base_url 未配置");
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("api_key 未配置");
        }

        var http = GetClient(baseUrl);
        var url = JoinUrl(baseUrl, "chat/completions");

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var bodyJson = JsonSerializer.Serialize(request, _jsonOptions);

        // 诊断日志
        var imageOccurrences = CountSubstring(bodyJson, "\"type\":\"image_url\"");
        var hasDataUrl = bodyJson.Contains("\"url\":\"data:image", StringComparison.Ordinal);
        BotLog.Info($"[AiChat] 请求 {model.Name}: messages={request.Messages.Count}, image_url 段={imageOccurrences}, 含data:image={hasDataUrl}, body 长度={bodyJson.Length}");

        httpReq.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        return await SendAndParseAsync(http, httpReq, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 流式请求：用 StreamingChatContent 直接把图片从 .b64 文件流式写入 HTTP body，
    /// 不在内存中持有完整的 data URL string。
    /// </summary>
    public async Task<string> ChatStreamingBodyAsync(
        DefaultSection defaults,
        ModelEntry model,
        List<ChatMessageDescriptor> messages,
        CancellationToken ct = default)
    {
        var (baseUrl, apiKey) = ResolveEndpoint(model);

        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("base_url 未配置");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("api_key 未配置");

        var http = GetClient(baseUrl);
        var url = JoinUrl(baseUrl, "chat/completions");

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpReq.Content = new StreamingChatContent(model.Name, messages);

        var imageCount = messages.Sum(m => m.Segments.Count(s => s.Kind == SegmentKind.Image));
        BotLog.Info($"[AiChat] 流式请求 {model.Name}: messages={messages.Count}, image 段={imageCount}");

        return await SendAndParseAsync(http, httpReq, ct).ConfigureAwait(false);
    }

    private async Task<string> SendAndParseAsync(HttpClient http, HttpRequestMessage httpReq, CancellationToken ct)
    {
        using var resp = await http.SendAsync(httpReq, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var trimmed = raw.Length > 800 ? raw[..800] + "…" : raw;
            BotLog.Warning($"[AiChat] OpenAI HTTP {(int)resp.StatusCode}: {trimmed}");
            string? msg = TryExtractErrorMessage(raw);
            throw new InvalidOperationException(msg ?? $"上游返回 HTTP {(int)resp.StatusCode}");
        }

        // 检测 SSE 流式响应（某些 provider 忽略 stream:false，强制返回 streaming）
        if (raw.TrimStart().StartsWith("data:", StringComparison.Ordinal))
        {
            var content = ParseSseStreamContent(raw);
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("流式响应内容为空");
            return content;
        }

        OpenAiChatResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<OpenAiChatResponse>(raw, _jsonOptions);
        }
        catch (JsonException ex)
        {
            BotLog.Warning($"[AiChat] 解析响应失败: {ex.Message} body={raw}");
            throw new InvalidOperationException("响应解析失败");
        }

        if (parsed?.Error?.Message is { } errMsg)
        {
            throw new InvalidOperationException(errMsg);
        }

        var content2 = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content2))
        {
            throw new InvalidOperationException("响应内容为空");
        }

        return content2;
    }

    private static string? TryExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String)
                {
                    return err.GetString();
                }
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m))
                {
                    return m.GetString();
                }
            }
            if (doc.RootElement.TryGetProperty("message", out var rootMsg))
            {
                return rootMsg.GetString();
            }
        }
        catch (JsonException)
        {
            // ignore
        }
        return null;
    }

    /// <summary>
    /// 解析 SSE 流式响应，拼接所有 chunk 的 delta.content 为完整文本。
    /// 兼容某些 provider 忽略 stream:false 强制返回 streaming 的情况。
    /// </summary>
    private static string ParseSseStreamContent(string raw)
    {
        var sb = new StringBuilder();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var json = trimmed["data:".Length..].Trim();
            if (string.IsNullOrEmpty(json) || json == "[DONE]")
                continue;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array)
                {
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (choice.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var content) &&
                            content.ValueKind == JsonValueKind.String)
                        {
                            sb.Append(content.GetString());
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // 跳过无法解析的行
            }
        }

        return sb.ToString();
    }

    private HttpClient GetClient(string baseUrl)
    {
        var key = baseUrl.TrimEnd('/');
        lock (_clientsLock)
        {
            if (_clients.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var client = new HttpClient
            {
                Timeout = _timeout,
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ShiroBot.AiChatPlugin/1.0");
            _clients[key] = client;
            return client;
        }
    }

    private static string JoinUrl(string baseUrl, string suffix)
    {
        var b = baseUrl.TrimEnd('/');
        var s = suffix.TrimStart('/');
        return $"{b}/{s}";
    }

    /// <summary>
    /// 解析 model 的实际 base_url 和 api_key：
    /// 1. model 自身有 Provider 引用 → 从 _providers 查
    /// 2. model 自身有 BaseUrl/ApiKey → 直接用
    /// 不再回退到 DefaultSection。
    /// </summary>
    private (string baseUrl, string apiKey) ResolveEndpoint(ModelEntry model)
    {
        if (!string.IsNullOrWhiteSpace(model.Provider) && _providers.TryGetValue(model.Provider, out var provider))
        {
            return (provider.BaseUrl, provider.ApiKey);
        }

        return (model.BaseUrl ?? "", model.ApiKey ?? "");
    }

    /// <summary>
    /// 注册 provider 列表，供 model 通过 provider 字段引用。
    /// </summary>
    public void RegisterProviders(IEnumerable<ProviderEntry> providers)
    {
        lock (_clientsLock)
        {
            _providers.Clear();
            foreach (var p in providers)
            {
                if (!string.IsNullOrWhiteSpace(p.Name))
                    _providers[p.Name] = p;
            }
        }
    }

    /// <summary>
    /// 从指定 provider 的 /v1/models 端点拉取可用模型列表。
    /// </summary>
    public async Task<List<string>> FetchModelsAsync(ProviderEntry provider, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl) || string.IsNullOrWhiteSpace(provider.ApiKey))
            return [];

        var http = GetClient(provider.BaseUrl);
        var url = JoinUrl(provider.BaseUrl, "models");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            BotLog.Warning($"[AiChat] 拉取模型列表失败: HTTP {(int)resp.StatusCode} from {provider.Name}");
            return [];
        }

        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var models = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        var modelId = id.GetString();
                        if (!string.IsNullOrWhiteSpace(modelId))
                            models.Add(modelId);
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            BotLog.Warning($"[AiChat] 解析模型列表失败: {ex.Message}");
        }

        return models;
    }

    private static int CountSubstring(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
        {
            return 0;
        }

        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
