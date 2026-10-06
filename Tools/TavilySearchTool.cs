using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Tools;

internal sealed class TavilySearchTool(TavilySection config, string httpProxy) : IReadOnlyChatTool
{
    private readonly TavilySearchClient _client = new(config, httpProxy);
    public int MaxCallsPerTurn => Math.Clamp(config.MaxCallsPerTurn, 1, 10);
    public string SystemPrompt => $"你可以按需使用 tavily_search 搜索网络。仅当用户明确要求联网/搜索，或问题依赖最新、实时、外部可验证信息时才调用；不要为闲聊、写作、翻译、编程、常识或已有上下文可回答的问题搜索。每次回复最多搜索 {MaxCallsPerTurn} 次。拿到结果后基于结果回答，并在合适时附上来源 URL。";
    public OpenAiTool Definition { get; } = new()
    {
        Function = new OpenAiToolFunction
        {
            Name = "tavily_search",
            Description = "Search the web only when the user explicitly requests a search or the answer requires current, real-time, or externally verifiable information. Do not use for casual chat, writing, translation, coding, general knowledge, or questions answerable from the conversation.",
            Parameters = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "query" },
                properties = new { query = new { type = "string", description = "A focused web search query." } }
            }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("query", out var query) ||
                query.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(query.GetString()))
                return "Search query was missing or invalid.";

            return await _client.SearchAsync(query.GetString()!, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return "Search arguments were invalid.";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BotLog.Warning($"[AiChat] Tavily search failed: {ex.Message}");
            return "Web search failed.";
        }
    }

    public void Dispose() => _client.Dispose();
}
