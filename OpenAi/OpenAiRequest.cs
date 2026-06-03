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
    public bool Stream { get; set; } = false;

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; set; }
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
}
