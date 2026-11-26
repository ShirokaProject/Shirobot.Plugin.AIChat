using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Contracts;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Resources;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.AiChatPlugin.Proactive;
using ShiroBot.AiChatPlugin.Usage;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;

namespace ShiroBot.AiChatPlugin;

/// <summary>给其他插件用的模型接口和插件工具。</summary>
public sealed partial class AiChatPlugin
{
    /// <summary>其他插件提供的工具；宿主不支持或已关闭时为空。名称与内置工具冲突的跳过。</summary>
    /// <summary>其他插件可以使用的模型：[plugin_api] models 留空时为全部已注册模型。</summary>
    private List<ModelEntry> PluginModels()
    {
        var allowed = _config.PluginApi.Models.Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        var models = _config.Models.Count > 0
            ? _config.Models
            : ResolveModel(_config.Default.Model) is { } single ? [single] : [];
        return allowed.Count == 0
            ? models.ToList()
            : allowed.Select(ResolveModel).OfType<ModelEntry>().Distinct().ToList();
    }

    private ModelEntry? PluginDefaultModel(List<ModelEntry> models)
    {
        var name = string.IsNullOrWhiteSpace(_config.PluginApi.DefaultModel) ? _config.Default.Model : _config.PluginApi.DefaultModel;
        var preferred = ResolveModel(name);
        return preferred is not null && models.Contains(preferred) ? preferred : models.FirstOrDefault();
    }

    private ChatModelList ListPluginModels()
    {
        if (!_config.PluginApi.Enabled) return new ChatModelList("", []);
        var models = PluginModels();
        return new ChatModelList(PluginDefaultModel(models) is { } model ? GetModelDisplayId(model) : "",
            models.Select(model => new ChatModelInfo(GetModelDisplayId(model), model.DisplayName, model.SupportsVision)).ToList(), SupportsImageFiles: true);
    }

    /// <summary>其他插件借用模型：按 [plugin_api] 选模型，和聊天共用并发上限、重试和备用模型。</summary>
    private async Task<ChatModelResponse> HandleModelCompleteAsync(ChatModelRequest request, CancellationToken ct)
    {
        if (!_config.PluginApi.Enabled) throw new InvalidOperationException("AI 聊天插件没有对其他插件开放模型。");
        if (request.Messages.Count == 0) throw new ArgumentException("至少需要一条消息。");
        using var usageScope = UsageScope.Begin("-", "plugin_api");

        var models = PluginModels();
        ModelEntry model;
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            model = ResolveModel(request.Model) is { } named && models.Contains(named)
                ? named
                : throw new InvalidOperationException($"模型 {request.Model} 不存在或没有对其他插件开放。");
            if (request.NeedsVision && !model.SupportsVision) throw new InvalidOperationException($"模型 {request.Model} 不支持识图。");
        }
        else
        {
            var preferred = PluginDefaultModel(models) ?? throw new InvalidOperationException("没有可供其他插件使用的模型。");
            model = !request.NeedsVision || preferred.SupportsVision
                ? preferred
                : models.FirstOrDefault(candidate => candidate.SupportsVision)
                  ?? throw new InvalidOperationException("没有对其他插件开放的识图模型。");
        }

        var messages = request.Messages.Select(message =>
        {
            var role = message.Role is "system" or "assistant" ? message.Role : "user";
            if (role != "user" || message.Images is not { Count: > 0 } images)
                return new OpenAiMessage { Role = role, Content = message.Text };
            var segments = new List<ContentSegment>();
            if (message.Text.Length > 0) segments.Add(new ContentSegment { Kind = SegmentKind.Text, Text = message.Text });
            segments.AddRange(images.Select(image => CreatePluginImageSegment(image)));
            return new OpenAiMessage { Role = role, Content = new ChatMessageDescriptor { Role = role, Segments = segments } };
        }).ToList();

        await _globalConcurrency.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? used = null;
            var text = await ExecuteTextWithFallbackAsync(model, requestModel =>
            {
                used = GetModelDisplayId(requestModel);
                return _openAi.ChatAsync(_config.Default, requestModel, new OpenAiChatRequest
                {
                    Model = requestModel.Name, Messages = messages,
                    Temperature = request.Temperature, MaxCompletionTokens = request.MaxOutputTokens
                }, ct);
            }, ct: ct).ConfigureAwait(false);
            return new ChatModelResponse(text, used ?? GetModelDisplayId(model));
        }
        finally
        {
            _globalConcurrency.Release();
        }
    }

    private static ContentSegment CreatePluginImageSegment(ChatModelImage image)
    {
        if (image.Mime is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
            throw new ArgumentException("插件图片格式必须是 PNG、JPEG、GIF 或 WebP。");
        if (image.FilePath is { Length: > 0 } path)
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new ArgumentException("插件图片文件不存在或不是绝对路径。");
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 20_000_000) throw new ArgumentException("插件图片为空或超过 20 MB。");
            using (var file = File.OpenRead(path))
            {
                Span<byte> header = stackalloc byte[16];
                int read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
                if (MimeGuesser.SniffImageMime(header[..read]) != image.Mime)
                    throw new ArgumentException("插件图片内容与声明的格式不符。");
            }
            return new ContentSegment { Kind = SegmentKind.Image, Mime = image.Mime, ImageFilePath = path };
        }
        if (image.Base64.Length > 26_666_668) throw new ArgumentException("插件图片超过 20 MB。");
        return new ContentSegment { Kind = SegmentKind.Image, Mime = image.Mime, InlineBytes = Convert.FromBase64String(image.Base64) };
    }

    private List<PluginChatTool> PluginToolsFor(MessageEvent message, ProactiveRequest? proactive, IEnumerable<IChatTool> builtIn)
    {
        var config = _config.ToolUse.PluginTools;
        // 主动插话不允许插件工具时仍然列出，调用时拒绝，见 ProcessAiCoreAsync。
        if (!_config.ToolUse.Enabled || !config.Enabled) return [];
        var reserved = new HashSet<string>(_tools.Names.Concat(builtIn.Select(tool => tool.Definition.Function.Name)), StringComparer.Ordinal);
        return PluginChatTool.Discover(Context, ToolContext(message, proactive), reserved, config.Disabled);
    }

    /// <summary>传给插件工具的会话信息，提供方据此选择内容或自行发送消息。</summary>
    internal static bool IsImageSaveRequest(string text) =>
        (text.Contains("保存") || text.Contains("收藏") || text.Contains("收")) &&
        (text.Contains("图片") || text.Contains("表情") || text.Contains("这张图"));

    internal static MessageEvent? RecentSaveImage(MessageEvent request, IEnumerable<MessageEvent> history) => history
        .Where(item => item.Platform == request.Platform && item.InstanceId == request.InstanceId &&
            item.Channel.Id == request.Channel.Id && item.Channel.Type == request.Channel.Type && item.Sender.Id == request.Sender.Id &&
            item.MessageId != request.MessageId && item.Timestamp != default && request.Timestamp != default &&
            item.Timestamp <= request.Timestamp && request.Timestamp - item.Timestamp <= TimeSpan.FromMinutes(2))
        .OrderByDescending(item => item.Timestamp)
        .FirstOrDefault(item => item.Segments.Any(segment => segment is ImageSegment or RawSegment { Payload: QIncomingImage or QIncomingMarketFace }));

    private static ChatToolContext ToolContext(MessageEvent message, ProactiveRequest? proactive) => new(
        message.Platform, message.InstanceId, message.Channel.Type.ToString(), message.Channel.Id,
        message.MessageId, message.Sender.Id, !message.IsDirect, proactive is { Addressed: false });
}
