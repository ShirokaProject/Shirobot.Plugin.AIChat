using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Tools;

internal sealed class TavilySearchTool(TavilySection config, string httpProxy) : IReadOnlyChatTool
{
    private readonly TavilySearchClient _client = new(config, httpProxy);
    public bool IsConcurrencySafe(string arguments) => true;
    public int MaxCallsPerTurn => int.MaxValue;
    public string SystemPrompt => "遇到不熟悉、不确定、缺少依据的人物、角色、名称、作品或事实时，尽量先用 tavily_search 搜索核实，不要仅凭名字猜测；无需等待用户明确说联网。已有充分上下文的简单问题可直接回答。涉及最新信息时也应搜索。" +
        "用户要某个具体人物或角色的照片、形象图，或生图需要还原其外观而没有可靠参考时，先搜索该名称并设置 include_images=true；结合结果标题、来源和上下文核对是否为同一对象，不能随便拿同名人的图片。" +
        "找到合适的公开图片后，用 http_fetch 的 response_mode=file 下载；生图时把返回的 file_id 交给 imagegen.reference_files，用户要原图时交给 send_file。搜索不到或身份不明确时说明情况或询问，不要伪称找到照片，也不要直接把想象外貌当作真实外观。图片说明不代表你已看过图片。用户明确要求不联网或纯想象创作时遵从用户要求。回答事实时在合适处附来源URL。";

    public OpenAiTool Definition { get; } = new()
    {
        Function = new OpenAiToolFunction
        {
            Name = "tavily_search",
            Description = "Search proactively when a person, character, name or fact is unfamiliar, uncertain or unsupported by context, as well as for current information. Use include_images=true for requested photos or accurate visual references before image generation. Verify identity and source; do not guess appearances.",
            Parameters = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "query" },
                properties = new { query = new { type = "string", description = "A focused web search query." }, include_images = new { type = "boolean", description = "Return image URLs and descriptions when image references or sending images are needed." } }
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

            var includeImages = false;
            if (document.RootElement.TryGetProperty("include_images", out var images))
            {
                if (images.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "include_images must be boolean.";
                includeImages = images.GetBoolean();
            }
            return await _client.SearchAsync(query.GetString()!, ct, includeImages).ConfigureAwait(false);
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
