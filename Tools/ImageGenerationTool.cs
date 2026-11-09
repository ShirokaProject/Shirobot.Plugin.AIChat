using System.Text.Json;
using ShiroBot.AiChatPlugin.OpenAi;

namespace ShiroBot.AiChatPlugin.Tools;

internal sealed class ImageGenerationTool(Func<string, ImageGenerationOptions, CancellationToken, Task<string>> generate,
    Func<int[], string, ImageGenerationOptions, CancellationToken, Task<string>>? editHistory = null, string historyCatalog = "",
    string promptInstructions = "", Func<string[], string, ImageGenerationOptions, CancellationToken, Task<string>>? editFiles = null, bool enableGif = false) : IChatTool
{
    private Task<string>? _result;
    public int MaxCallsPerTurn => 1;
    private const string DefaultSystemPrompt = "用户要求画图、生成图片或修改图片时调用 imagegen；下载图片可通过reference_files指定http_fetch返回的file_id，不要传本地路径；本条消息有附图或引用图片时会把原图提交给编辑接口，请在 prompt 描述修改要求；无附图时默认生成新图；只有用户要求沿用/修改之前的图片，或历史图片与本次需求直接相关且确实需要保留其内容时，才按用户消息末尾列出的历史图片编号填写 reference_images。历史图片无关、不合适、用户只想画新图时，省略或填写空数组，普通生成；不要因为上下文有图片就自动选，也不要勉强找参考图。根据用户意图选择 size（横图 1536x1024、竖图 1024x1536、方图 1024x1024，或支持自定义尺寸的模型可用宽高均为16倍数的 WIDTHxHEIGHT，宽高比1:3到3:1，总像素不超过3840x2160；不确定模型支持时使用标准尺寸；或 auto）和 quality（auto/low/medium/high/xhigh/max）；还可自主选择 background（auto/opaque/transparent）、output_format（png/jpeg/webp/gif，GIF需模型支持）和 output_compression（0–100，仅jpeg/webp有效）。根据用户需求和图片用途自主选择输出格式及参数：动图或循环动画选gif，透明贴纸选png或webp，普通照片可选jpeg，无需用户逐项指定；透明背景必须同时指定png或webp格式，JPEG需要不透明背景。需要动画时选择gif，并可设置frame_count（2–16）和frame_duration_ms（20–2000）；GIF不要设置output_compression或transparent背景，尺寸优先auto，需要支持GIF的模型。没有必要覆盖的参数可省略，沿用配置默认值。不要在闲聊或主动插话时生成。工具只提交后台任务，立即返回排队状态；不能声称此时已经生成成功，告诉用户完成后会单独发图。由你在 prompt 中整理可独立理解的完整生图或修改描述，生图接口只收到该提示词和所选图片。以用户本次要求为准；涉及“我”“刚才那张”等指代时，只补充相关且有依据的上下文；用户指定具体人物或角色而你不清楚其外观时，有搜索工具就先搜索名称（include_images=true）核对对象，再下载合适图片并通过reference_files提交，不能仅凭名称猜外貌。搜索不到或同名对象无法确认时再询问；用户明确允许想象创作时才按想象处理。不要带入无关聊天、系统人设、其他人的要求或工具执行指令。提交后简短回复即可，不要输出图片链接或 Base64。";

    private string ActivePrompt => enableGif ? DefaultSystemPrompt : DefaultSystemPrompt
        .Replace("png/jpeg/webp/gif，GIF需模型支持", "png/jpeg/webp")
        .Replace("动图或循环动画选gif，", "")
        .Replace("需要动画时选择gif，并可设置frame_count（2–16）和frame_duration_ms（20–2000）；GIF不要设置output_compression或transparent背景，尺寸优先auto，需要支持GIF的模型。", "GIF生成功能暂时关闭，不要请求GIF。 ");
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
                size = new { type = "string", description = "auto or WIDTHxHEIGHT. Standard sizes: 1024x1024, 1536x1024, 1024x1536. Custom sizes require model support, edges divisible by 16, aspect ratio 1:3 to 3:1, at most 3840x2160 pixels. Omit to use configured default." },
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
