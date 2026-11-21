using System.Text.Json;
using ShiroBot.AiChatPlugin.OpenAi;

namespace ShiroBot.AiChatPlugin.Tools.Images;

internal sealed class ImageGenerationTool(Func<string, ImageGenerationOptions, CancellationToken, Task<string>> generate,
    Func<int[], string, ImageGenerationOptions, CancellationToken, Task<string>>? editHistory = null, string historyCatalog = "",
    string promptInstructions = "", Func<string[], string, ImageGenerationOptions, CancellationToken, Task<string>>? editFiles = null, bool enableGif = false) : IChatTool
{
    private Task<string>? _result;
    public int MaxCallsPerTurn => 1;
    // 只放何时调用、如何写 prompt、参考图和提交后的回复；尺寸、格式、背景、GIF 等参数说明在工具定义里，
    // 不在 system prompt 里重复，避免冲淡“需要出图就调用工具”这条最重要的规则。
    private const string DefaultSystemPrompt =
        "用户要求画图、生成或修改图片时，必须调用 imagegen；不能用文字或方括号标记代替工具调用，历史中的“[程序记录：imagegen 工具已生成…]”是程序在工具完成后自动添加的。没人提出要求时（闲聊、自己主动插话）不要生成。" +
        "在 prompt 中写出可独立理解的完整描述：以用户本次要求为准，“刚才那张”等指代只补充相关且有依据的上下文，不带入无关聊天、人设或其他人的要求。" +
        "本条消息有附图或引用图片时会提交给编辑接口，在 prompt 里写修改要求；只有用户要沿用或修改之前的图片时，才按程序附加参考里的历史图片编号填写 reference_images，不要因为上下文有图就自动选。" +
        "用户指定具体人物或角色而你不清楚外观时，有搜索工具就先搜索（include_images=true）核对，再用 http_fetch 下载合适图片，通过 reference_files 提交 file_id；无法确认时再询问。" +
        "其余参数按工具参数说明根据用途自主选择，没必要的省略。工具只提交后台任务，不能声称已经生成成功；简短告诉用户完成后会单独发图，不要输出图片链接或 Base64。";

    private string ActivePrompt => DefaultSystemPrompt;
    public string SystemPrompt => string.IsNullOrWhiteSpace(promptInstructions)
        ? ActivePrompt
        : ActivePrompt + "\n[生图提示词编写规则]\n" + promptInstructions.Trim() +
          "\n以上规则仅用于编写 imagegen 的 prompt，用户本次明确的内容、风格和修改要求优先；" +
          "不要把这些编写规则本身复制进 prompt，也不要改变参考图片选择、工具调用和后台任务规则。";

    /// <summary>可引用的历史图片编号。会随新图片变化，附在本条用户消息末尾，不放进 system prompt。</summary>
    public string HistoryCatalog => historyCatalog;
    private OpenAiTool FullDefinition { get; } = new() { Function = new() {
        Name = "imagegen", Description = "Generate and send one image when the user requests an image. May take several minutes.",
        Parameters = new { type = "object", required = new[] { "prompt" }, additionalProperties = false,
            properties = new { reference_files = new { type = "array", items = new { type = "string" }, maxItems = 4, description = "Optional file IDs from http_fetch file mode in this turn; PNG/JPEG/WebP/GIF references. GIF uploads retain original bytes; rejected GIF inputs fall back to first-frame PNG. Do not combine with reference_images." }, reference_images = new { type = "array", items = new { type = "integer", minimum = 1, maximum = 8 }, maxItems = 4, description = "Optional historical image IDs from the provided catalog. Use only when suitable and necessary for this request. Omit or pass [] for ordinary new image generation." }, prompt = new { type = "string", description = "Self-contained image generation or edit prompt based on the current request. Resolve relevant references using supported context; omit unrelated chat and instructions." },
                size = new { type = "string", description = "auto or WIDTHxHEIGHT. Choose by intent: landscape 1536x1024, portrait 1024x1536, square 1024x1024. Custom sizes require model support, edges divisible by 16, aspect ratio 1:3 to 3:1, at most 3840x2160 pixels. Omit to use configured default." },
                quality = new { type = "string", @enum = new[] { "auto", "low", "medium", "high", "xhigh", "max" }, description = "Quality requested by the user. Higher quality can take longer. Omit to use configured default." },
                frame_count = new { type = "integer", minimum = 2, maximum = 16, description = "GIF only: animation frame count. Requires output_format=gif and a supporting model. Omit for configured default." },
                frame_duration_ms = new { type = "integer", minimum = 20, maximum = 2000, description = "GIF only: milliseconds per frame, rounded to 10 ms. Omit for configured default." },
                background = new { type = "string", @enum = new[] { "auto", "opaque", "transparent" }, description = "Choose background for the intended use. For transparency also specify png or webp output_format. Omit to use configured default." },
                output_format = new { type = "string", @enum = new[] { "png", "jpeg", "webp", "gif" }, description = "Choose automatically from user intent: gif for animation, png/webp for transparency, jpeg for photos. JPEG requires opaque background. Omit for configured default." },
                output_compression = new { type = "integer", minimum = 0, maximum = 100, description = "JPEG/WebP quality from 0 to 100; higher preserves more detail. Ignored for PNG. Omit to use configured default." } } }
    } };
    public OpenAiTool Definition
    {
        get
        {
            if (enableGif) return FullDefinition;
            var parameters = System.Text.Json.Nodes.JsonNode.Parse(PluginJson.Serialize(FullDefinition.Function.Parameters))!.AsObject();
            var properties = parameters["properties"]!.AsObject();
            properties.Remove("frame_count"); properties.Remove("frame_duration_ms");
            properties["output_format"]!["enum"] = new System.Text.Json.Nodes.JsonArray("png", "jpeg", "webp");
            properties["output_format"]!["description"] = "Choose png, jpeg or webp. GIF is disabled.";
            return new() { Function = new() { Name = FullDefinition.Function.Name, Description = FullDefinition.Function.Description, Parameters = parameters } };
        }
    }

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
            if (size is not null && !ImageGenerationClient.IsValidSize(size))
                return Task.FromResult("Invalid image size. Use auto or WIDTHxHEIGHT with edges divisible by 16, aspect ratio 1:3 to 3:1 and at most 3840x2160 pixels.");
            if (quality is not null && quality is not ("auto" or "low" or "medium" or "high" or "xhigh" or "max"))
                return Task.FromResult("Invalid image quality. Use auto, low, medium, high, xhigh or max.");
            var background = Optional("background");
            var format = Optional("output_format");
            if (!enableGif && (format == "gif" || doc.RootElement.TryGetProperty("frame_count", out _) || doc.RootElement.TryGetProperty("frame_duration_ms", out _)))
                return Task.FromResult("GIF generation is currently disabled. Choose png, jpeg or webp.");
            int? compression = null;
            if (doc.RootElement.TryGetProperty("output_compression", out var compressionValue) && compressionValue.ValueKind != JsonValueKind.Null)
            {
                if (compressionValue.ValueKind != JsonValueKind.Number || !compressionValue.TryGetInt32(out var number) || number is < 0 or > 100)
                    return Task.FromResult("Invalid output_compression. Use an integer from 0 to 100.");
                compression = number;
            }
            if (background is not null && background is not ("auto" or "opaque" or "transparent"))
                return Task.FromResult("Invalid background. Use auto, opaque or transparent.");
            if (format is not null && format is not ("png" or "jpeg" or "webp" or "gif"))
                return Task.FromResult("Invalid output_format. Use png, jpeg, webp or gif.");
            if (background == "transparent" && format == "jpeg")
                return Task.FromResult("Transparent backgrounds require png or webp output_format.");
            int? Number(string name, int min, int max)
            {
                if (!doc.RootElement.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max) throw new JsonException();
                return number;
            }
            var frames = Number("frame_count", 2, 16);
            var duration = Number("frame_duration_ms", 20, 2000);
            if ((frames is not null || duration is not null) && format != "gif")
                return Task.FromResult("GIF frame parameters require output_format=gif.");
            if (format == "gif" && compression is not null)
                return Task.FromResult("GIF does not support output_compression; omit it.");
            if (format == "gif" && background == "transparent")
                return Task.FromResult("Transparent background requires png or webp.");
            var options = new ImageGenerationOptions(size, quality, background, format, compression, frames, duration);
            var references = Array.Empty<int>();
            if (doc.RootElement.TryGetProperty("reference_images", out var refs))
            {
                if (refs.ValueKind != JsonValueKind.Array || refs.GetArrayLength() > 4 ||
                    refs.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < 1 || n > 8))
                    return Task.FromResult("Invalid reference_images: choose up to four IDs from the history catalog.");
                references = refs.EnumerateArray().Select(v => v.GetInt32()).Distinct().ToArray();
            }
            var fileIds = Array.Empty<string>();
            if (doc.RootElement.TryGetProperty("reference_files", out var fileRefs))
            {
                if (fileRefs.ValueKind != JsonValueKind.Array || fileRefs.GetArrayLength() > 4 ||
                    fileRefs.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
                    return Task.FromResult("reference_files must contain up to four downloaded file IDs.");
                fileIds = fileRefs.EnumerateArray().Select(value => value.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
            }
            if (fileIds.Length > 0)
            {
                if (references.Length > 0) return Task.FromResult("Use reference_files or reference_images, not both.");
                if (editFiles is null) return Task.FromResult("Downloaded references are not available here.");
                return _result ??= editFiles(fileIds, prompt.GetString()!, options, ct);
            }
            if (references.Length > 0 && editHistory is null) return Task.FromResult("No historical images are available; ask the user to attach or quote the image.");
            // Chat retries/fallbacks reuse the same per-turn tool: never generate twice.
            return _result ??= references.Length > 0
                ? editHistory!(references, prompt.GetString()!, options, ct)
                : generate(prompt.GetString()!, options, ct);
        }
        catch (JsonException) { return Task.FromResult("Invalid imagegen arguments."); }
    }
    public void Dispose() { }
}
