using System.Text.Json;
using ShiroBot.AiChatPlugin.OpenAi;

namespace ShiroBot.AiChatPlugin.Tools;

internal sealed class ImageGenerationTool(Func<string, CancellationToken, Task<string>> generate) : IChatTool
{
    private Task<string>? _result;
    public int MaxCallsPerTurn => 1;
    public string SystemPrompt => "用户要求画图、生成图片时调用 imagegen；不要在闲聊或主动插话时生成。工具会直接发送图片，成功后简短回复即可，不要输出图片链接或 Base64。";
    public OpenAiTool Definition { get; } = new() { Function = new() {
        Name = "imagegen", Description = "Generate and send one image when the user requests an image. May take several minutes.",
        Parameters = new { type = "object", required = new[] { "prompt" }, additionalProperties = false,
            properties = new { prompt = new { type = "string", description = "Complete image description." } } }
    } };
    public Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(prompt.GetString())) return Task.FromResult("Missing image prompt.");
            // Chat retries/fallbacks reuse the same per-turn tool: never generate twice.
            return _result ??= generate(prompt.GetString()!, ct);
        }
        catch (JsonException) { return Task.FromResult("Invalid imagegen arguments."); }
    }
    public void Dispose() { }
}
