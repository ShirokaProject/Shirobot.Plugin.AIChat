using System.Text.Json;
using ShiroBot.AiChatPlugin.OpenAi;

namespace ShiroBot.AiChatPlugin.Tools;

internal sealed class ImageGenerationTool(Func<string, string?, string?, CancellationToken, Task<string>> generate) : IChatTool
{
    private Task<string>? _result;
    public int MaxCallsPerTurn => 1;
    public string SystemPrompt => "用户要求画图、生成图片或修改图片时调用 imagegen；本条消息有附图或引用图片时会把原图提交给编辑接口，请在 prompt 描述修改要求；无附图时生成新图。根据用户意图选择 size（横图 1536x1024、竖图 1024x1536、方图 1024x1024，或 auto）和 quality（auto/low/medium/high/xhigh/max）；没有要求时省略，沿用配置默认值。不要在闲聊或主动插话时生成。工具会直接发送图片，成功后简短回复即可，不要输出图片链接或 Base64。";
    public OpenAiTool Definition { get; } = new() { Function = new() {
        Name = "imagegen", Description = "Generate and send one image when the user requests an image. May take several minutes.",
        Parameters = new { type = "object", required = new[] { "prompt" }, additionalProperties = false,
            properties = new { prompt = new { type = "string", description = "Complete image description." },
                size = new { type = "string", @enum = new[] { "auto", "1024x1024", "1536x1024", "1024x1536" }, description = "Choose landscape, portrait or square to match the request. Omit to use configured default." },
                quality = new { type = "string", @enum = new[] { "auto", "low", "medium", "high", "xhigh", "max" }, description = "Quality requested by the user. Higher quality can take longer. Omit to use configured default." } } }
    } };
    public Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(prompt.GetString())) return Task.FromResult("Missing image prompt.");
            string? Optional(string name) => doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
                ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new JsonException() : null;
            var size = Optional("size"); var quality = Optional("quality");
            if (size is not null && size is not ("auto" or "1024x1024" or "1536x1024" or "1024x1536"))
                return Task.FromResult("Invalid image size. Use auto, 1024x1024, 1536x1024 or 1024x1536.");
            if (quality is not null && quality is not ("auto" or "low" or "medium" or "high" or "xhigh" or "max"))
                return Task.FromResult("Invalid image quality. Use auto, low, medium, high, xhigh or max.");
            // Chat retries/fallbacks reuse the same per-turn tool: never generate twice.
            return _result ??= generate(prompt.GetString()!, size, quality, ct);
        }
        catch (JsonException) { return Task.FromResult("Invalid imagegen arguments."); }
    }
    public void Dispose() { }
}
