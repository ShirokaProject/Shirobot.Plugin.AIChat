using System.Diagnostics;
using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Tools;

/// <summary>统一管理工具定义、提示词、调用分发及每种工具的调用上限。</summary>
internal sealed class ChatToolRegistry(int maxParallelCalls = 4, int requestAttempts = 3, int retryDelayMs = 300) : IDisposable
{
    private readonly Dictionary<string, IChatTool> _tools = new(StringComparer.Ordinal);
    public bool HasTools => _tools.Count > 0;
    public IEnumerable<string> Names => _tools.Keys;
    public bool HasReadOnlyTools => _tools.Values.OfType<IReadOnlyChatTool>().Any();
    public string ReadOnlySystemPrompt => string.Join("\n", _tools.Values.OfType<IReadOnlyChatTool>().Select(tool => tool.SystemPrompt)) + (HasReadOnlyTools ? ParallelPrompt : "");
    private const string ParallelPrompt = "\n需要多个互不依赖的查询时，可在同一条回复里发出多个工具调用，系统会并发执行支持并发的调用。有依赖的步骤分轮执行，拿到上一步结果再继续，不要猜测前一步返回值。";
    public string SystemPrompt => string.Join("\n", _tools.Values.Select(tool => tool.SystemPrompt)) + (HasTools ? ParallelPrompt : "");

    public static ChatToolRegistry Create(PluginConfig config, Func<string, CancellationToken, Task<string>>? summarizePage = null)
    {
        var registry = new ChatToolRegistry(config.ToolUse.MaxParallelCalls, config.Default.RetryOnTimeoutMaxAttempts, config.Default.RetryOnTimeoutDelayMs);
        if (!config.ToolUse.Enabled) return registry;
        var skills = new SkillTool(config.Skills);
        if (skills.HasSkills) registry.Register(skills);
        else skills.Dispose();
        var tavily = config.ToolUse.Tavily;
        if (tavily is { Enabled: true } && !string.IsNullOrWhiteSpace(tavily.ApiKey))
            registry.Register(new TavilySearchTool(tavily, config.Network.HttpProxy));
        if (config.ToolUse.HttpFetch.Enabled)
            registry.Register(new HttpFetchTool(config.ToolUse.HttpFetch, summarizePage));
        return registry;
    }

    public void Register(IChatTool tool) => _tools.Add(tool.Definition.Function.Name, tool);

    public async Task<string> ChatAsync(OpenAiClient client, ModelEntry model,
        List<OpenAiMessage> messages, int maxRounds, Func<string, Task>? onContent = null, CancellationToken ct = default, IReadOnlyList<IChatTool>? contextualTools = null, bool readOnlyOnly = false, int? maxOutputTokens = null)
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
        var definitions = tools.Values.Select(tool => tool.Definition).ToList();
        var visiblePrefix = "";
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
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException && attempt < Math.Clamp(requestAttempts, 1, 5))
                {
                    BotLog.Warning($"[AiChat] 本轮上游响应失败 ({(ex is IncompleteUpstreamResponseException ? ex.Message : ex.GetType().Name)})，保留工具结果重试 {attempt}/{Math.Clamp(requestAttempts, 1, 5)}；不重跑工具。");
                    await Task.Delay(Math.Clamp(retryDelayMs, 100, 10000) * attempt, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException && messages.Any(message => message.Role == "tool"))
                { throw new ToolRoundResponseFailedException(ex); }
            }
        }
        for (var round = 0; round <= maxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            // 每轮保留相同工具定义以稳定前缀；达到全局轮数后才移除工具。
            var request = new OpenAiChatRequest
            {
                Model = model.Name,
                Messages = messages,
                Tools = round < maxRounds && definitions.Count > 0 ? definitions : null,
                Stream = false,
                MaxCompletionTokens = maxOutputTokens
            };
            // 只推送正文，工具参数在客户端按 index 拼接后交给注册表执行。
            var response = await RequestAsync(request).ConfigureAwait(false);

            if (response.ToolCalls is not { Count: > 0 })
            {
                if (string.IsNullOrWhiteSpace(response.Content)) throw new EmptyUpstreamResponseException();
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
                var finalRequest = new OpenAiChatRequest { Model = model.Name, Messages = messages, Stream = false, MaxCompletionTokens = maxOutputTokens };
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
            var results = await ExecuteCallsAsync(response.ToolCalls, tools, calls, model.Name, round + 1, ct, readOnlyOnly).ConfigureAwait(false);
            messages.AddRange(results);
            foreach (var source in tools.Values.OfType<IToolAttachments>().Distinct())
                messages.AddRange(source.TakeAttachments());
        }
        throw new InvalidOperationException("工具调用未返回最终回答。");
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
