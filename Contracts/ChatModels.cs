using ShiroBot.SDK.Plugin;

namespace ShiroBot.AiChatPlugin.Contracts;

/// <summary>
/// 借用 AI 聊天插件配置好的模型：其他插件不需要自己配置接口地址和密钥。
/// AIChat 的 <c>[plugin_api]</c> 可以关闭这项能力、限定可用的模型和默认模型。
/// </summary>
[PluginContract]
public static partial class ChatModels
{
    /// <summary>调用一次模型，返回文本回复。可以带图片，需要识图时设 <c>NeedsVision</c>。</summary>
    [PluginService("aichat.model.complete")]
    public static partial PluginService<ChatModelRequest, ChatModelResponse> Complete { get; }

    /// <summary>列出其他插件可以使用的模型。</summary>
    [PluginService("aichat.model.list")]
    public static partial PluginService<ChatModelList> List { get; }
}

/// <param name="Messages">按顺序的对话消息。</param>
/// <param name="Model">模型名称，取自 <see cref="ChatModels.List"/>；不填用 AIChat 给其他插件的默认模型。</param>
/// <param name="NeedsVision">消息里有图片时设为 true：没有指定模型时选一个支持识图的模型，指定的模型不支持识图时报错。</param>
/// <param name="MaxOutputTokens">最多输出多少 token；不填用模型默认值。</param>
/// <param name="Temperature">采样温度；不填用模型默认值。</param>
public sealed record ChatModelRequest(
    IReadOnlyList<ChatModelMessage> Messages,
    string? Model = null,
    bool NeedsVision = false,
    int? MaxOutputTokens = null,
    double? Temperature = null);

/// <param name="Role">system、user 或 assistant。</param>
/// <param name="Text">文本内容。</param>
/// <param name="Images">图片，只在 user 消息里有效。</param>
public sealed record ChatModelMessage(string Role, string Text = "", IReadOnlyList<ChatModelImage>? Images = null);

/// <param name="Mime">图片类型，例如 image/png。</param>
/// <param name="Base64">图片内容的 Base64。</param>
public sealed record ChatModelImage(string Mime, string Base64);

/// <param name="Text">模型的文本回复。</param>
/// <param name="Model">实际使用的模型。</param>
public sealed record ChatModelResponse(string Text, string Model);

/// <param name="DefaultModel">不指定模型时使用的模型。</param>
public sealed record ChatModelList(string DefaultModel, IReadOnlyList<ChatModelInfo> Models);

/// <param name="Name">调用时填写的模型名称。</param>
public sealed record ChatModelInfo(string Name, string? DisplayName, bool SupportsVision);
