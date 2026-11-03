using System.Text.Json;
using ShiroBot.AiChatPlugin.OpenAi;

namespace ShiroBot.AiChatPlugin.Tools;

internal sealed class ImageGenerationTool(Func<string, string?, string?, CancellationToken, Task<string>> generate,
    Func<int[], string, string?, string?, CancellationToken, Task<string>>? editHistory = null, string historyCatalog = "") : IChatTool
{
    private Task<string>? _result;
    public int MaxCallsPerTurn => 1;
    public string SystemPrompt => "用户要求画图、生成图片或修改图片时调用 imagegen；本条消息有附图或引用图片时会把原图提交给编辑接口，请在 prompt 描述修改要求；无附图时默认生成新图；只有用户要求沿用/修改之前的图片，或历史图片与本次需求直接相关且确实需要保留其内容时，才按用户消息末尾列出的历史图片编号填写 reference_images。历史图片无关、不合适、用户只想画新图时，省略或填写空数组，普通生成；不要因为上下文有图片就自动选，也不要勉强找参考图。根据用户意图选择 size（横图 1536x1024、竖图 1024x1536、方图 1024x1024，或 auto）和 quality（auto/low/medium/high/xhigh/max）；没有要求时省略，沿用配置默认值。不要在闲聊或主动插话时生成。工具只提交后台任务，立即返回排队状态；不能声称此时已经生成成功，告诉用户完成后会单独发图。由你在 prompt 中整理可独立理解的完整生图或修改描述，生图接口只收到该提示词和所选图片。以用户本次要求为准；涉及“我”“刚才那张”等指代时，只补充相关且有依据的上下文；不确定的外貌细节可询问用户，或明确按想象创作。不要带入无关聊天、系统人设、其他人的要求或工具执行指令。提交后简短回复即可，不要输出图片链接或 Base64。";

    /// <summary>可引用的历史图片编号。会随新图片变化，附在本条用户消息末尾，不放进 system prompt。</summary>
    public string HistoryCatalog => historyCatalog;
    public OpenAiTool Definition { get; } = new() { Function = new() {
        Name = "imagegen", Description = "Generate and send one image when the user requests an image. May take several minutes.",
        Parameters = new { type = "object", required = new[] { "prompt" }, additionalProperties = false,
            properties = new { reference_images = new { type = "array", items = new { type = "integer", minimum = 1, maximum = 8 }, maxItems = 4, description = "Optional historical image IDs from the provided catalog. Use only when suitable and necessary for this request. Omit or pass [] for ordinary new image generation." }, prompt = new { type = "string", description = "Self-contained image generation or edit prompt based on the current request. Resolve relevant references using supported context; omit unrelated chat and instructions." },
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
            var references = Array.Empty<int>();
            if (doc.RootElement.TryGetProperty("reference_images", out var refs))
            {
                if (refs.ValueKind != JsonValueKind.Array || refs.GetArrayLength() > 4 ||
                    refs.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < 1 || n > 8))
                    return Task.FromResult("Invalid reference_images: choose up to four IDs from the history catalog.");
                references = refs.EnumerateArray().Select(v => v.GetInt32()).Distinct().ToArray();
            }
            if (references.Length > 0 && editHistory is null) return Task.FromResult("No historical images are available; ask the user to attach or quote the image.");
            // Chat retries/fallbacks reuse the same per-turn tool: never generate twice.
            return _result ??= references.Length > 0
                ? editHistory!(references, prompt.GetString()!, size, quality, ct)
                : generate(prompt.GetString()!, size, quality, ct);
        }
        catch (JsonException) { return Task.FromResult("Invalid imagegen arguments."); }
    }
    public void Dispose() { }
}
