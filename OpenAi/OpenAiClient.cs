using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Usage;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.OpenAi;

internal sealed class IncompleteUpstreamResponseException(string reason) : HttpRequestException(reason);
internal sealed class ToolRoundResponseFailedException(Exception inner)
    : Exception("上游回复多次中断，已执行的工具任务不会重新提交。", inner);

internal sealed class EmptyUpstreamResponseException : Exception
{
    public EmptyUpstreamResponseException() : base("响应内容为空") { }
}

/// <summary>
/// 兼容 OpenAI chat.completions 协议的 HTTP 客户端。
/// 按 base_url 复用 HttpClient（每个 endpoint 一个实例）。
/// 每次收到上游的 usage 都记入 <paramref name="usage"/>：统计在发送侧做，工具轮次、压缩和摘要请求都会计入。
/// </summary>
/// <param name="streamUsage">流式请求附带 stream_options.include_usage，让上游在最后返回用量。</param>
internal sealed class OpenAiClient(int timeoutSeconds, string httpProxy, UsageStats? usage = null, bool streamUsage = true) : IDisposable
{
    private readonly HashSet<string> _missingUsageWarned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProviderEntry> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _clientsLock = new();
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds));
    private readonly string _httpProxy = httpProxy;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        TypeInfoResolver = PluginJson.Resolver,
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

        BotLog.Info($"[AiChat] 请求 {model.Name}: messages={request.Messages.Count}, body=streaming");
        httpReq.Content = new StreamingChatContent(Prepare(request));

        return await SendAndParseAsync(http, httpReq, model, request, ct).ConfigureAwait(false);
    }

    public async Task<OpenAiResponseMessage> ChatWithToolsAsync(
        ModelEntry model,
        OpenAiChatRequest request,
        CancellationToken ct = default)
    {
        var (baseUrl, apiKey) = ResolveEndpoint(model);
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("base_url 未配置");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("api_key 未配置");

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, JoinUrl(baseUrl, "chat/completions"));
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpReq.Content = new StreamingChatContent(Prepare(request));
        return await SendAndParseMessageAsync(GetClient(baseUrl), httpReq, model, request, ct).ConfigureAwait(false);
    }

    public async Task<string> ChatIncrementalAsync(ModelEntry model, OpenAiChatRequest request,
        Func<string, Task> onContent, CancellationToken ct = default)
    {
        var response = await ChatWithToolsIncrementalAsync(model, request, onContent, ct).ConfigureAwait(false);
        return GetFinalIncrementalText(response);
    }

    public async Task<OpenAiResponseMessage> ChatWithToolsIncrementalAsync(ModelEntry model, OpenAiChatRequest request,
        Func<string, Task> onContent, CancellationToken ct = default)
    {
        var (baseUrl, apiKey) = ResolveEndpoint(model);
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("base_url 未配置");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("api_key 未配置");
        request.Stream = true;
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, JoinUrl(baseUrl, "chat/completions"));
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        httpReq.Content = new StreamingChatContent(Prepare(request));
        return await SendIncrementalMessageAsync(GetClient(baseUrl), httpReq, model, request, onContent, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 流式请求：用 StreamingChatContent 直接把图片从 .b64 文件流式写入 HTTP body，
    /// 不在内存中持有完整的 data URL string。
    /// </summary>
    public async Task<string> ChatStreamingBodyAsync(
        DefaultSection defaults,
        ModelEntry model,
        List<ChatMessageDescriptor> messages,
        CancellationToken ct = default,
        Func<string, Task>? onContent = null)
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
        var request = StreamingChatContent.CreateRequest(model.Name, messages, stream: onContent is not null,
            maxCompletionTokens: defaults.OutputTokenLimit());
        httpReq.Content = new StreamingChatContent(Prepare(request));

        var imageCount = messages.Sum(m => m.Segments.Count(s => s.Kind == SegmentKind.Image));
        BotLog.Info($"[AiChat] 流式请求 {model.Name}: messages={messages.Count}, image 段={imageCount}");

        return onContent is null
            ? await SendAndParseAsync(http, httpReq, model, request, ct).ConfigureAwait(false)
            : await SendIncrementalAsync(http, httpReq, model, request, onContent, ct).ConfigureAwait(false);
    }

    private static string GetFinalIncrementalText(OpenAiResponseMessage response)
    {
        if (response.ToolCalls is { Count: > 0 })
            throw new InvalidOperationException("最终流式回答意外包含工具调用。");
        return string.IsNullOrWhiteSpace(response.Content)
            ? throw new EmptyUpstreamResponseException() : response.Content;
    }

    private async Task<string> SendIncrementalAsync(HttpClient http, HttpRequestMessage request, ModelEntry model,
        OpenAiChatRequest chatRequest, Func<string, Task> onContent, CancellationToken ct) =>
        GetFinalIncrementalText(await SendIncrementalMessageAsync(http, request, model, chatRequest, onContent, ct).ConfigureAwait(false));

    private async Task<OpenAiResponseMessage> SendIncrementalMessageAsync(HttpClient http, HttpRequestMessage request,
        ModelEntry model, OpenAiChatRequest chatRequest, Func<string, Task> onContent, CancellationToken ct)
    {
        try
        {
            return await ReadIncrementalAsync(http, request, model, chatRequest, onContent, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TaskCanceledException("上游流式请求超时。", ex);
        }
        catch (IOException) when (!ct.IsCancellationRequested)
        { throw new IncompleteUpstreamResponseException("上游流式连接读取中断。"); }
        catch (JsonException) when (!ct.IsCancellationRequested)
        { throw new IncompleteUpstreamResponseException("上游响应JSON不完整或无效。"); }
    }

    private async Task<OpenAiResponseMessage> ReadIncrementalAsync(HttpClient http, HttpRequestMessage request,
        ModelEntry model, OpenAiChatRequest chatRequest, Func<string, Task> onContent, CancellationToken ct)
    {
        // ResponseHeadersRead 的 HttpClient 超时不覆盖响应体读取，显式限制整次生成。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            throw new InvalidOperationException(TryExtractErrorMessage(error) ?? $"上游返回 HTTP {(int)response.StatusCode}");
        }
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false));
        var firstLine = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        if (firstLine is null) throw new EmptyUpstreamResponseException();
        var isSse = response.Content.Headers.ContentType?.MediaType == "text/event-stream" ||
            firstLine.TrimStart().StartsWith("data:", StringComparison.Ordinal) ||
            firstLine.StartsWith(':') || firstLine.StartsWith("event:", StringComparison.Ordinal);
        if (!isSse)
        {
            // 部分兼容接口忽略 stream:true，返回普通 JSON。
            var raw = firstLine + "\n" + await reader.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            var parsed = PluginJson.Deserialize<OpenAiChatResponse>(raw, _jsonOptions);
            if (parsed?.Error?.Message is { } error) throw new InvalidOperationException(error);
            Report(model, chatRequest, ReadUsage(raw));
            var choice = parsed?.Choices?.FirstOrDefault() ?? throw new EmptyUpstreamResponseException();
            ValidateCompletion(choice.FinishReason, choice.Message?.ToolCalls, requireReason: choice.HasFinishReason);
            BotLog.Info($"[AiChat] 上游JSON结束 finish_reason={choice.FinishReason ?? "omitted"} text_chars={choice.Message?.Content?.Length ?? 0}");
            var message = choice.Message ?? throw new EmptyUpstreamResponseException();
            if (!string.IsNullOrWhiteSpace(message.Content)) await onContent(message.Content).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(message.Content) && message.ToolCalls is not { Count: > 0 })
                throw new EmptyUpstreamResponseException();
            return message;
        }

        return await ReadSseMessageAsync(reader, firstLine, model, chatRequest, onContent, timeout.Token).ConfigureAwait(false);
    }

    private async Task<OpenAiResponseMessage> ReadSseMessageAsync(TextReader reader, string? firstLine,
        ModelEntry model, OpenAiChatRequest chatRequest, Func<string, Task> onContent, CancellationToken ct)
    {
        var content = new StringBuilder();
        var reasoningContent = new StringBuilder();
        var hasReasoningContent = false;
        var toolCalls = new SortedDictionary<int, OpenAiToolCall>();
        var eventData = new StringBuilder();
        string? finishReason = null;
        var done = false;
        UsageReading? usage = null;
        for (string? line = firstLine; line is not null && !done;
             line = done ? null : await reader.ReadLineAsync(ct).ConfigureAwait(false))
        {
            if (line.Length == 0)
            {
                await ProcessEventAsync().ConfigureAwait(false);
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (eventData.Length > 0) eventData.Append('\n');
                eventData.Append(line[5..].TrimStart());
            }
        }
        if (eventData.Length > 0 && !done) await ProcessEventAsync().ConfigureAwait(false);
        if (finishReason is null) throw new IncompleteUpstreamResponseException("上游流式响应缺少正常结束原因，连接提前结束。");
        ValidateCompletion(finishReason, toolCalls.Values.ToList());
        BotLog.Info($"[AiChat] 上游SSE正常结束 finish_reason={finishReason} text_chars={content.Length} tool_calls={toolCalls.Count}");
        Report(model, chatRequest, usage);
        if (string.IsNullOrWhiteSpace(content.ToString()) && toolCalls.Count == 0)
            throw new EmptyUpstreamResponseException();
        if (toolCalls.Values.Any(call => string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Function?.Name)))
            throw new InvalidOperationException("流式工具调用缺少 ID 或工具名称。");
        return new OpenAiResponseMessage
        {
            Role = "assistant", Content = content.ToString(),
            ReasoningContent = hasReasoningContent ? reasoningContent.ToString() : null,
            ToolCalls = toolCalls.Count > 0 ? toolCalls.Values.ToList() : null
        };

        async Task ProcessEventAsync()
        {
            if (eventData.Length == 0) return;
            var data = eventData.ToString();
            eventData.Clear();
            if (data.Trim() == "[DONE]") { done = true; return; }
            JsonDocument document;
            try { document = JsonDocument.Parse(data); }
            catch (JsonException ex) { throw new IncompleteUpstreamResponseException("上游SSE数据不完整或损坏：" + ex.GetType().Name); }
            using var doc = document;
            if (doc.RootElement.TryGetProperty("error", out _))
                throw new InvalidOperationException(TryExtractErrorMessage(data) ?? "上游流式响应出错。");
            // include_usage 时用量在最后一个 chunk（choices 为空）；也有上游在每个 chunk 里累计返回。
            if (doc.RootElement.TryGetProperty("usage", out var usageElement) && TokenUsage.TryParse(usageElement) is { } parsedUsage)
                usage = new UsageReading(parsedUsage, usageElement.GetRawText());
            if (!doc.RootElement.TryGetProperty("choices", out var choices)) return;
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("index", out var index) && index.GetInt32() != 0) continue;
                if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    finishReason = reason.GetString();
                if (!choice.TryGetProperty("delta", out var delta)) continue;
                if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
                {
                    hasReasoningContent = true;
                    reasoningContent.Append(reasoning.GetString());
                }
                if (delta.TryGetProperty("tool_calls", out var calls))
                {
                    foreach (var part in calls.EnumerateArray())
                    {
                        var callIndex = part.GetProperty("index").GetInt32();
                        if (!toolCalls.TryGetValue(callIndex, out var call))
                        {
                            call = new OpenAiToolCall { Function = new OpenAiToolCallFunction { Arguments = "" } };
                            toolCalls.Add(callIndex, call);
                        }
                        if (part.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            call.Id += id.GetString();
                        if (part.TryGetProperty("function", out var function))
                        {
                            if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                                call.Function!.Name += name.GetString();
                            if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String)
                                call.Function!.Arguments += arguments.GetString();
                        }
                    }
                }
                if (delta.TryGetProperty("content", out var chunk) && chunk.ValueKind == JsonValueKind.String)
                {
                    content.Append(chunk.GetString());
                    if (!string.IsNullOrWhiteSpace(content.ToString()))
                        await onContent(content.ToString()).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<string> SendAndParseAsync(HttpClient http, HttpRequestMessage httpReq, ModelEntry model,
        OpenAiChatRequest request, CancellationToken ct)
    {
        var message = await SendAndParseMessageAsync(http, httpReq, model, request, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(message.Content))
            throw new EmptyUpstreamResponseException();
        return message.Content;
    }

    private async Task<OpenAiResponseMessage> SendAndParseMessageAsync(HttpClient http, HttpRequestMessage httpReq,
        ModelEntry model, OpenAiChatRequest request, CancellationToken ct)
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

        // Some gateways ignore stream:false. Never accept their partial SSE as a final answer.
        var start = raw.TrimStart();
        if (resp.Content.Headers.ContentType?.MediaType == "text/event-stream" || start.StartsWith("data:", StringComparison.Ordinal) || start.StartsWith(':') || start.StartsWith("event:", StringComparison.Ordinal))
        {
            using var reader = new StringReader(raw);
            return await ReadSseMessageAsync(reader, await reader.ReadLineAsync(ct).ConfigureAwait(false), model, request,
                _ => Task.CompletedTask, ct).ConfigureAwait(false);
        }

        OpenAiChatResponse? parsed;
        try
        {
            parsed = PluginJson.Deserialize<OpenAiChatResponse>(raw, _jsonOptions);
        }
        catch (JsonException ex)
        {
            BotLog.Warning($"[AiChat] 上游JSON响应不完整或损坏: {ex.GetType().Name}");
            throw new IncompleteUpstreamResponseException("上游JSON响应不完整或损坏。");
        }

        if (parsed?.Error?.Message is { } errMsg)
        {
            throw new InvalidOperationException(errMsg);
        }
        Report(model, request, ReadUsage(raw));

        var choice = parsed?.Choices?.FirstOrDefault() ?? throw new EmptyUpstreamResponseException();
        ValidateCompletion(choice.FinishReason, choice.Message?.ToolCalls, requireReason: choice.HasFinishReason);
        BotLog.Info($"[AiChat] 上游JSON结束 finish_reason={choice.FinishReason ?? "omitted"} text_chars={choice.Message?.Content?.Length ?? 0}");
        return choice.Message ?? throw new EmptyUpstreamResponseException();
    }

    /// <summary>流式请求按配置附带 include_usage。</summary>
    private OpenAiChatRequest Prepare(OpenAiChatRequest request)
    {
        request.IncludeUsage = streamUsage && request.Stream;
        request.CapturePrefix = usage is not null;
        request.SentPrefix = null;
        return request;
    }

    /// <summary>从普通 JSON 响应或整段 SSE 文本里读出 usage；SSE 取最后一个带 usage 的 chunk。</summary>
    private static UsageReading? ReadUsage(string raw)
    {
        var trimmed = raw.TrimStart();
        if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) return ParseUsage(trimmed);
        UsageReading? last = null;
        foreach (var line in raw.Split('\n'))
        {
            var data = line.Trim();
            if (!data.StartsWith("data:", StringComparison.Ordinal)) continue;
            data = data[5..].Trim();
            if (data.Length > 0 && data != "[DONE]" && ParseUsage(data) is { } usage) last = usage;
        }
        return last;
    }

    private static UsageReading? ParseUsage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("usage", out var usage) &&
                   TokenUsage.TryParse(usage) is { } tokens
                ? new UsageReading(tokens, usage.GetRawText()) : null;
        }
        catch (JsonException) { return null; }
    }

    private void Report(ModelEntry model, OpenAiChatRequest request, UsageReading? reading)
    {
        if (usage is null) return;
        var modelId = string.IsNullOrWhiteSpace(model.Provider) ? model.Name : $"{model.Provider}-{model.Name}";
        var tokens = reading?.Tokens;
        if (reading is { } sample)
        {
            // 每个模型带缓存字段、不带缓存字段的 usage 各记一条原文，用来确认上游的字段格式。
            var kind = sample.Tokens.CachedTokens is null ? "没有缓存字段" : "带缓存字段";
            lock (_missingUsageWarned)
                if (_missingUsageWarned.Add(modelId + "\n" + kind))
                    BotLog.Info($"[AiChat] {modelId} 返回的 usage 样例（{kind}）: " +
                        (sample.Raw.Length > 1000 ? sample.Raw[..1000] + "…" : sample.Raw));
        }
        if (tokens is null)
        {
            lock (_missingUsageWarned)
                if (_missingUsageWarned.Add(modelId))
                    BotLog.Warning($"[AiChat] {modelId} 没有返回 usage，仍记录请求和本地前缀复用；上游 Token 用量未知" +
                (request.Stream && !streamUsage ? "（default.stream_usage 已关闭）" : "") + "。");
        }
        try
        {
            var (source, purpose) = UsageScope.Tag;
            var shape = RequestShape.Of(request);
            // 对话和主动插话共用会话历史，应当能复用彼此的前缀；摘要、压缩等独立请求不比较。
            var (baseUrl, apiKey) = ResolveEndpoint(model);
            var endpoint = baseUrl + "\n" + apiKey;
            var upstream = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));
            // 有的上游只在命中缓存时才返回缓存字段：同一上游模型返回过一次后，缺少字段就按 0 命中计，
            // 否则没命中的请求都被排除在命中率之外，命中率会虚高。
            if (tokens is { } returned)
                tokens = returned with { CachedTokens = usage.ResolveCachedTokens(upstream + "\n" + modelId, returned.CachedTokens) };
            var compareKey = purpose switch
            {
                "chat" or "proactive" => $"{upstream}\n{source}\n{modelId}\nconversation",
                "timer" => $"{upstream}\n{source}\n{modelId}\ntimer",
                _ => null
            };
            usage.Record(new UsageRecord(DateTimeOffset.Now, modelId, source, purpose, tokens?.PromptTokens ?? 0, tokens?.CompletionTokens ?? 0,
                tokens?.CachedTokens, request.Messages.Count, shape.Images, request.Tools?.Count ?? 0, UsageReported: tokens is not null), shape, compareKey, request.SentPrefix);
        }
        catch (Exception ex)
        {
            // 统计失败不影响回复。
            BotLog.Warning($"[AiChat] 记录用量失败: {ex.Message}");
        }
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

    private static void ValidateCompletion(string? reason, IReadOnlyCollection<OpenAiToolCall>? calls, bool requireReason = true)
    {
        // Legacy JSON-only gateways may omit finish_reason; explicit truncation never succeeds.
        if (reason is null && requireReason) throw new IncompleteUpstreamResponseException("上游响应缺少结束原因。");
        if (reason == "length") throw new IncompleteUpstreamResponseException("上游生成达到输出长度上限，回复被截断；可提高最大输出token配置。");
        if (reason == "content_filter") throw new InvalidOperationException("上游内容过滤终止了回复，未发送不完整正文。");
        if (reason is not (null or "stop" or "tool_calls" or "function_call"))
            throw new IncompleteUpstreamResponseException("上游以异常结束原因终止回复。");
        if (reason is "tool_calls" or "function_call" && calls is not { Count: > 0 })
            throw new IncompleteUpstreamResponseException("上游工具调用响应未完成。");
        foreach (var call in calls ?? [])
        {
            if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Function?.Name))
                throw new IncompleteUpstreamResponseException("上游工具调用缺少ID或名称。");
            try { using var args = JsonDocument.Parse(call.Function.Arguments); }
            catch (JsonException) { throw new IncompleteUpstreamResponseException("上游工具参数JSON不完整或无效，未执行工具。"); }
        }
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

            var client = new HttpClient(HttpProxy.CreateHandler(_httpProxy))
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

    public void Dispose()
    {
        lock (_clientsLock)
        {
            foreach (var client in _clients.Values) client.Dispose();
            _clients.Clear();
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
