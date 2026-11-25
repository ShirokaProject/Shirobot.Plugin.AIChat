using System.Diagnostics;
using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.SDK.Abstractions;
using ShiroBot.AiChatPlugin.Tools.Skills;
using ShiroBot.AiChatPlugin.Tools.Web;

namespace ShiroBot.AiChatPlugin.Tools;

/// <summary>统一管理工具定义、提示词、调用分发及每种工具的调用上限。</summary>
internal sealed class ChatToolRegistry(int maxParallelCalls = 4, int requestAttempts = 3, int retryDelayMs = 300) : IDisposable
{
    // 写时复制：技能在页面上修改后会替换 skill 工具，正在进行的对话继续用拿到的那份。
    private volatile Dictionary<string, IChatTool> _tools = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    public bool HasTools => _tools.Count > 0;
    public IEnumerable<string> Names => _tools.Keys;
    public bool HasReadOnlyTools => _tools.Values.OfType<IReadOnlyChatTool>().Any();
    public string ReadOnlySystemPrompt => string.Join("\n", _tools.Values.OfType<IReadOnlyChatTool>().Select(tool => tool.SystemPrompt)) + (HasReadOnlyTools ? ParallelPrompt : "");
    private const string ParallelPrompt = "\n需要多个互不依赖的查询时，可在同一条回复里发出多个工具调用，系统会并发执行支持并发的调用。有依赖的步骤分轮执行，拿到上一步结果再继续，不要猜测前一步返回值。";
    public string SystemPrompt => string.Join("\n", _tools.Values.Select(tool => tool.SystemPrompt)) + (HasTools ? ParallelPrompt : "");

    public static ChatToolRegistry Create(PluginConfig config, Func<string, CancellationToken, Task<string>>? summarizePage = null,
        IReadOnlyList<SkillDefinition>? skills = null)
    {
        var registry = new ChatToolRegistry(config.ToolUse.MaxParallelCalls, config.Default.RetryOnTimeoutMaxAttempts, config.Default.RetryOnTimeoutDelayMs);
        if (!config.ToolUse.Enabled) return registry;
        if (config.Skills.Enabled) registry.ReplaceSkills(new SkillTool(skills ?? []));
        var tavily = config.ToolUse.Tavily;
        if (!string.IsNullOrWhiteSpace(tavily.ApiKey))
            registry.Register(new TavilySearchTool(tavily, config.Network.HttpProxy));
        if (config.ToolUse.HttpFetch.Enabled)
            registry.Register(new HttpFetchTool(config.ToolUse.HttpFetch, summarizePage, proxy: config.Network.HttpProxy));
        return registry;
    }

    public void Register(IChatTool tool)
    {
        lock (_gate) _tools = new Dictionary<string, IChatTool>(_tools, StringComparer.Ordinal) { { tool.Definition.Function.Name, tool } };
    }

    /// <summary>换成新的技能快照；没有启用的技能时不提供 skill 工具。</summary>
    public void ReplaceSkills(SkillTool? skills)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, IChatTool>(_tools, StringComparer.Ordinal);
            next.Remove("skill");
            if (skills is { HasSkills: true }) next.Add("skill", skills);
            _tools = next;
        }
    }

    public async Task<string> ChatAsync(OpenAiClient client, ModelEntry model,
        List<OpenAiMessage> messages, int maxRounds, Func<string, Task>? onContent = null, CancellationToken ct = default, IReadOnlyList<IChatTool>? contextualTools = null, bool readOnlyOnly = false, int? maxOutputTokens = null,
        Func<string, IReadOnlySet<string>, string?>? verifyFinal = null)
    {
        var tools = _tools.Where(pair => !readOnlyOnly || pair.Value is IReadOnlyChatTool)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var tool in contextualTools ?? [])
            if (!readOnlyOnly || tool is IReadOnlyChatTool)
            {
                var name = tool.Definition.Function.Name;
                if (tool is HttpFetchTool && tools.GetValueOrDefault(name) is HttpFetchTool) tools[name] = tool;
                else tools.Add(name, tool);
            }
        maxRounds = Math.Clamp(maxRounds, 1, 8);
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        var definitions = tools.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value.Definition).ToList();
        var visiblePrefix = "";
        // 本轮实际执行过的工具，用于核对回复里“已经做了”的说法；纠正最多一次，避免来回拉扯。
        var called = new HashSet<string>(StringComparer.Ordinal);
        var corrected = false;
        async Task<OpenAiResponseMessage> RequestAsync(OpenAiChatRequest request)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    request.Stream = false;
                    // Retry only this model request with the same tool-result transcript.
                    // After a partial stream use a buffered request, so no partial output is accepted.
                    return onContent is not null && attempt == 1
                        ? await client.ChatWithToolsIncrementalAsync(model, request, text => onContent(visiblePrefix + text), ct).ConfigureAwait(false)
                        : await client.ChatWithToolsAsync(model, request, ct).ConfigureAwait(false);
                }
                catch (UpstreamHttpException ex) when (request.ToolChoice is not null &&
                    (int)ex.StatusCode is 400 or 422 && ex.Message.Contains("tool_choice", StringComparison.OrdinalIgnoreCase))
                {
                    // Some compatible endpoints reject tool_choice. Preserve the tools-off behavior for them.
                    request.ToolChoice = null;
                    request.Tools = null;
                    attempt--;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException && attempt < Math.Clamp(requestAttempts, 1, 5))
                {
                    BotLog.Warning($"[AiChat] 本轮上游响应失败 ({(ex is IncompleteUpstreamResponseException ? ex.Message : ex.GetType().Name)})，保留工具结果重试 {attempt}/{Math.Clamp(requestAttempts, 1, 5)}；不重跑工具。");
                    await Task.Delay(Math.Clamp(retryDelayMs, 100, 10000) * attempt, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException && messages.Any(message => message.Role == "tool"))
                { throw new ToolRoundResponseFailedException(ex); }
            }
        }
        var hasTools = definitions.Count > 0;
        for (var round = 0; round <= maxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            // 达到轮次上限时用 tool_choice 禁用调用，同时保留工具定义和缓存前缀。
            var request = new OpenAiChatRequest
            {
                Model = model.Name,
                Messages = messages,
                Tools = hasTools ? definitions : null,
                ToolChoice = hasTools && round == maxRounds ? "none" : null,
                Stream = false,
                MaxCompletionTokens = maxOutputTokens
            };
            // 只推送正文，工具参数在客户端按 index 拼接后交给注册表执行。
            var response = await RequestAsync(request).ConfigureAwait(false);

            if (response.ToolCalls is not { Count: > 0 })
            {
                if (string.IsNullOrWhiteSpace(response.Content)) throw new EmptyUpstreamResponseException();
                // 流式时文字已经发出，无法收回，只在非流式回复上核对。
                if (!corrected && onContent is null && round < maxRounds &&
                    verifyFinal?.Invoke(response.Content, called) is { } correction)
                {
                    corrected = true;
                    BotLog.Warning($"[AiChat] 回复声称已执行操作但没有调用对应工具，要求模型重新处理一次: {correction[..Math.Min(40, correction.Length)]}");
                    messages.Add(new OpenAiMessage { Role = "assistant", Content = response.Content, ReasoningContent = response.ReasoningContent });
                    messages.Add(new OpenAiMessage { Role = "system", Content = correction });
                    continue;
                }
                return visiblePrefix + response.Content;
            }
            if (onContent is not null && !string.IsNullOrEmpty(response.Content))
                visiblePrefix += response.Content + "\n\n";
            if (round == maxRounds)
            {
                foreach (var rejected in response.ToolCalls)
                    BotLog.Warning($"[AiChat] ToolUse 拒绝 {DescribeCall(rejected.Function?.Name ?? "", rejected.Id, model.Name, round + 1)} 原因=工具调用轮数达到上限");
                messages.Add(new OpenAiMessage { Role = "assistant", Content = response.Content ?? "", ToolCalls = response.ToolCalls,
                    ReasoningContent = response.ReasoningContent });
                foreach (var rejected in response.ToolCalls)
                    messages.Add(new OpenAiMessage { Role = "tool", ToolCallId = rejected.Id, Content = "Tool round limit reached. This call was not executed. Answer from the information already obtained; disclose missing or failed fetches." });
                messages.Add(new OpenAiMessage { Role = "system", Content = "本次工具调用已达到上限，不能再请求任何工具。请根据已获取的信息回复；没有获取到的内容请明确说明，不要猜测，不要声称已成功读取失败的页面。" });
                // The endpoint returned tool calls despite the limit. Require a text-only response.
                var finalRequest = new OpenAiChatRequest { Model = model.Name, Messages = messages, MaxCompletionTokens = maxOutputTokens };
                var final = await RequestAsync(finalRequest).ConfigureAwait(false);
                if (final.ToolCalls is { Count: > 0 } || string.IsNullOrWhiteSpace(final.Content))
                    throw new InvalidOperationException("模型在工具耗尽后仍未返回最终回答。");
                return visiblePrefix + final.Content;
            }

            messages.Add(new OpenAiMessage
            {
                Role = "assistant", Content = response.Content ?? "", ToolCalls = response.ToolCalls,
                ReasoningContent = response.ReasoningContent
            });
            foreach (var call in response.ToolCalls)
                if (call.Function?.Name is { Length: > 0 } calledName) called.Add(calledName);
            var results = await ExecuteCallsAsync(response.ToolCalls, tools, calls, model.Name, round + 1, ct, readOnlyOnly).ConfigureAwait(false);
            messages.AddRange(results);
            foreach (var source in tools.Values.OfType<IToolAttachments>().Distinct())
                AppendAttachments(messages, source.TakeAttachments());
        }
        throw new InvalidOperationException("工具调用未返回最终回答。");
    }

    /// <summary>
    /// 工具读到的图片并进本轮用户消息末尾，不在工具结果后面另起一条用户消息：
    /// 上游常在最后一条用户消息之前留缓存点，另起一条时这个点落在工具结果之后，下一轮对不上，只能退回很早的缓存。
    /// 找不到可以追加的用户消息时仍然单独附加。
    /// </summary>
    internal static void AppendAttachments(List<OpenAiMessage> messages, IReadOnlyList<OpenAiMessage> attachments)
    {
        if (attachments.Count == 0) return;
        var target = messages.LastOrDefault(message => message.Role == "user");
        if (target?.Content is not ChatMessageDescriptor descriptor)
        {
            messages.AddRange(attachments);
            return;
        }
        foreach (var attachment in attachments)
        {
            if (attachment.Content is ChatMessageDescriptor extra) descriptor.Segments.AddRange(extra.Segments);
            else if (attachment.Content is string text) descriptor.Segments.Add(new ContentSegment { Kind = SegmentKind.Text, Text = text });
        }
    }

    /// <summary>Parallel read groups separated by exclusive barriers; results always retain model order.</summary>
    internal async Task<IReadOnlyList<OpenAiMessage>> ExecuteCallsAsync(IReadOnlyList<OpenAiToolCall> batch,
        IReadOnlyDictionary<string, IChatTool> tools, Dictionary<string, int> calls,
        string model, int round, CancellationToken ct, bool readOnlyOnly = false)
    {
        var results = new OpenAiMessage[batch.Count];
        var limit = Math.Clamp(maxParallelCalls, 1, 16);
        using var slots = new SemaphoreSlim(limit, limit);
        bool Parallel(OpenAiToolCall call)
        {
            if (!tools.TryGetValue(call.Function?.Name ?? "", out var tool)) return false;
            try { return tool.IsConcurrencySafe(call.Function?.Arguments ?? "{}"); }
            catch { return false; }
        }
        async Task Run(int index)
        {
            ct.ThrowIfCancellationRequested();
            var call = batch[index];
            var name = call.Function?.Name ?? "";
            var details = DescribeCall(name, call.Id, model, round);
            string result;
            if (!tools.TryGetValue(name, out var tool))
            {
                BotLog.Warning($"[AiChat] ToolUse 拒绝 {details} 原因=未知或未启用的工具");
                result = $"Unknown or disabled tool: {name}.";
            }
            else if (readOnlyOnly && tool is HttpFetchTool && !tool.IsConcurrencySafe(call.Function?.Arguments ?? "{}"))
                result = "Only GET, HEAD and OPTIONS are allowed in this read-only task.";
            else
            {
                await slots.WaitAsync(ct).ConfigureAwait(false);
                try { ct.ThrowIfCancellationRequested(); result = await ExecuteLoggedAsync(tool, call.Function?.Arguments ?? "{}", details, ct).ConfigureAwait(false); }
                finally { slots.Release(); }
            }
            results[index] = new OpenAiMessage { Role = "tool", ToolCallId = call.Id, Content = result };
        }
        for (var start = 0; start < batch.Count;)
        {
            ct.ThrowIfCancellationRequested();
            if (!Parallel(batch[start])) { await Run(start++).ConfigureAwait(false); continue; }
            var end = start + 1;
            while (end < batch.Count && Parallel(batch[end])) end++;
            // Task.WhenAll drains every started call before cancellation/failure leaves this method.
            await Task.WhenAll(Enumerable.Range(start, end - start).Select(Run)).ConfigureAwait(false);
            start = end;
        }
        return results;
    }

    private static string DescribeCall(string name, string? callId, string model, int round) =>
        $"tool={PluginJson.Serialize(name)} call_id={PluginJson.Serialize(callId)} model={PluginJson.Serialize(model)} round={round}";

    internal static async Task<string> ExecuteLoggedAsync(IChatTool tool, string arguments, string details, CancellationToken ct)
    {
        var trace = Guid.NewGuid().ToString("N")[..8];
        var watch = Stopwatch.StartNew();
        BotLog.Info($"[AiChat] ToolUse 开始 trace={trace} {details}");
        try
        {
            var result = await tool.ExecuteAsync(arguments, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            BotLog.Info($"[AiChat] ToolUse 完成 trace={trace} {details} elapsed_ms={watch.ElapsedMilliseconds} result_chars={result.Length}");
            return result;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            BotLog.Info($"[AiChat] ToolUse 取消 trace={trace} {details} elapsed_ms={watch.ElapsedMilliseconds}");
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception ex)
        {
            // 不输出完整参数、返回正文或异常消息，避免 URL token、API key 等进入日志。
            BotLog.Warning($"[AiChat] ToolUse 失败 trace={trace} {details} elapsed_ms={watch.ElapsedMilliseconds} error={ex.GetType().Name}" +
                (ex is HttpFetchException fetch ? " " + fetch.Diagnostic : ""));
            return ex is HttpFetchException http ? http.ToolResult : $"Tool execution failed: {tool.Definition.Function.Name}.";
        }
    }

    public void Dispose()
    {
        foreach (var tool in _tools.Values) tool.Dispose();
    }
}
