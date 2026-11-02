using System.Text.Json.Nodes;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.AiChatPlugin.Contracts;

/// <summary>
/// AI 聊天的工具扩展点。其他插件把 ShiroBot.AiChatPlugin.dll 放进自己的 contracts 目录，
/// 用 <c>ChatTools.Tools.Provide(Context, "工具名", handler, new ChatToolInfo(...))</c> 提供工具；
/// AIChat 每轮用 <c>ChatTools.Tools.Find</c> 找到当前所有工具交给模型，不需要知道是哪个插件提供的。
/// </summary>
[PluginContract]
public static partial class ChatTools
{
    [PluginService("chat.tool.*")]
    public static partial PluginServiceFamily<ChatToolCall, ChatToolResult, ChatToolInfo> Tools { get; }

    /// <summary>
    /// 回复后动作：工具结果里的 <see cref="ChatAfterReply"/> 指向这里的一个名称，AIChat 发出文字回复后调用它，
    /// 例如发图片、文件这类跟在回复后面的内容。
    /// </summary>
    [PluginService("chat.after_reply.*")]
    public static partial PluginServiceFamily<ChatAfterReplyCall, ChatAfterReplyResult, ChatAfterReplyInfo> AfterReply { get; }
}

/// <summary>工具说明。</summary>
/// <param name="Description">交给模型的工具说明；AIChat 会在前面加上 [提供方插件 ID]。</param>
/// <param name="Parameters">参数的 JSON Schema，用 <c>PluginJsonSchema.For&lt;参数类型&gt;()</c> 生成；不填表示没有参数。</param>
/// <param name="Prompt">追加到系统提示的使用说明，例如什么时候该用、什么时候不该用。</param>
/// <param name="MaxCallsPerTurn">每轮回复最多调用几次，1–5，默认 1。</param>
public sealed record ChatToolInfo(string Description, JsonObject? Parameters = null, string? Prompt = null, int MaxCallsPerTurn = 1);

/// <summary>一次工具调用：模型给的参数和触发回复的会话。参数用 <c>call.Arguments.As&lt;参数类型&gt;()</c> 读取。</summary>
public sealed record ChatToolCall(JsonObject Arguments, ChatToolContext Context);

/// <summary>触发这次回复的会话。</summary>
public sealed record ChatToolContext(
    string Platform,
    string? InstanceId,
    string ChannelType,
    string ChannelId,
    string? MessageId,
    string? SenderId,
    bool IsGroup,
    bool Proactive);

/// <summary>工具结果：<paramref name="Content"/> 交给模型；<paramref name="AfterReply"/> 要求 AIChat 在文字回复发出后调用某个服务。</summary>
public sealed record ChatToolResult(string Content, ChatAfterReply? AfterReply = null);

/// <summary>回复后要调用的 <c>ChatTools.AfterReply</c> 名称和传给它的参数。</summary>
public sealed record ChatAfterReply(string Name, JsonObject? Arguments = null);

/// <summary>回复后动作的说明。</summary>
public sealed record ChatAfterReplyInfo(string? Description = null);

/// <summary>AIChat 调用回复后动作时传的内容；参数用 <c>call.Arguments.As&lt;参数类型&gt;()</c> 读取。</summary>
public sealed record ChatAfterReplyCall(JsonObject? Arguments, ChatToolContext Context, ChatReplyState Reply);

/// <summary>
/// AIChat 这边的回复情况：<paramref name="TextSent"/> 为 false 表示没有发出文字，由回复后动作代替回复，
/// 并按 <paramref name="Mention"/> / <paramref name="Quote"/> @ 发送者、引用触发消息。
/// </summary>
public sealed record ChatReplyState(bool TextSent, bool Mention, bool Quote);

/// <summary>回复后动作的结果。没有发出文字时，<paramref name="History"/> 代替空回复写入对话历史。</summary>
public sealed record ChatAfterReplyResult(bool Ok, string? Error = null, string? History = null);
