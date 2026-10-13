using System.Diagnostics;
using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Tools;

/// <summary>统一管理工具定义、提示词、调用分发及每种工具的调用上限。</summary>
internal sealed class ChatToolRegistry : IDisposable
{
    private readonly Dictionary<string, IChatTool> _tools = new(StringComparer.Ordinal);
    public bool HasTools => _tools.Count > 0;
    public bool HasReadOnlyTools => _tools.Values.OfType<IReadOnlyChatTool>().Any();
    public string ReadOnlySystemPrompt => string.Join("\n", _tools.Values.OfType<IReadOnlyChatTool>().Select(tool => tool.SystemPrompt));
    public string SystemPrompt => string.Join("\n", _tools.Values.Select(tool => tool.SystemPrompt));

    public static ChatToolRegistry Create(PluginConfig config, Func<string, CancellationToken, Task<string>>? summarizePage = null)
    {
        var registry = new ChatToolRegistry();
        if (!config.ToolUse.Enabled) return registry;
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
            if (!readOnlyOnly || tool is IReadOnlyChatTool) tools.Add(tool.Definition.Function.Name, tool);
        maxRounds = Math.Clamp(maxRounds, 1, 8);
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        var visiblePrefix = "";
        for (var round = 0; round <= maxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            var available = tools.Values.Where(tool =>
                calls.GetValueOrDefault(tool.Definition.Function.Name) < tool.MaxCallsPerTurn)
                .Select(tool => tool.Definition).ToList();
            var request = new OpenAiChatRequest
            {
                Model = model.Name,
                Messages = messages,
                Tools = round < maxRounds && available.Count > 0 ? available : null,
                Stream = false,
                MaxCompletionTokens = maxOutputTokens
            };
            // 只推送正文，工具参数在客户端按 index 拼接后交给注册表执行。
            var response = onContent is null
                ? await client.ChatWithToolsAsync(model, request, ct).ConfigureAwait(false)
                : await client.ChatWithToolsIncrementalAsync(model, request,
                    text => onContent(visiblePrefix + text), ct).ConfigureAwait(false);

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
                var final = onContent is null
                    ? await client.ChatWithToolsAsync(model, finalRequest, ct).ConfigureAwait(false)
                    : await client.ChatWithToolsIncrementalAsync(model, finalRequest, text => onContent(visiblePrefix + text), ct).ConfigureAwait(false);
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
            {
                ct.ThrowIfCancellationRequested();
                var name = call.Function?.Name ?? "";
                var details = DescribeCall(name, call.Id, model.Name, round + 1);
                string result;
                if (!tools.TryGetValue(name, out var tool))
                {
                    BotLog.Warning($"[AiChat] ToolUse 拒绝 {details} 原因=未知或未启用的工具");
                    result = $"Unknown or disabled tool: {name}.";
                }
                else if (calls.GetValueOrDefault(name) >= tool.MaxCallsPerTurn)
                {
                    BotLog.Warning($"[AiChat] ToolUse 拒绝 {details} 原因=单轮工具调用次数达到上限");
                    result = $"Tool call limit reached: {name}.";
                }
                else
                {
                    calls[name] = calls.GetValueOrDefault(name) + 1;
                    result = await ExecuteLoggedAsync(tool, call.Function!.Arguments, details, ct).ConfigureAwait(false);
                }
                messages.Add(new OpenAiMessage { Role = "tool", ToolCallId = call.Id, Content = result });
            }
        }
        throw new InvalidOperationException("工具调用未返回最终回答。");
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
