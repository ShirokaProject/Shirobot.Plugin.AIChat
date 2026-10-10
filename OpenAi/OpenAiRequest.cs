using System.Text.Json.Serialization;

namespace ShiroBot.AiChatPlugin.OpenAi;

/// <summary>
/// chat.completions 请求体。content 走多模态数组形式，全部模型都兼容。
/// </summary>
internal sealed class OpenAiChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<OpenAiMessage> Messages { get; set; } = [];

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("max_completion_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxCompletionTokens { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAiTool>? Tools { get; set; }
}

internal sealed class OpenAiMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    /// <summary>
    /// 可以是 string 或 List&lt;Dictionary&lt;string, object&gt;&gt;。
    /// 用 object 让 System.Text.Json 自然序列化两种情况，
    /// 不依赖 [JsonPolymorphic]，避免 type discriminator 顺序之类的隐形问题。
    /// </summary>
    [JsonPropertyName("content")]
    public object Content { get; set; } = "";

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    // 仅用于本次请求内工具轮次的协议回传，不作为可见回复或持久化历史。
    [JsonPropertyName("reasoning_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningContent { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAiToolCall>? ToolCalls { get; set; }
}

internal sealed class OpenAiTool
{
    [JsonPropertyName("type")] public string Type { get; init; } = "function";
    [JsonPropertyName("function")] public required OpenAiToolFunction Function { get; init; }
}

internal sealed class OpenAiToolFunction
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }
    [JsonPropertyName("parameters")] public required object Parameters { get; init; }
}

internal sealed class OpenAiToolCall
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public OpenAiToolCallFunction? Function { get; set; }
}

internal sealed class OpenAiToolCallFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("arguments")] public string Arguments { get; set; } = "{}";
}
