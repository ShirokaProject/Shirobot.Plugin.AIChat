using System.Text.Json;

namespace ShiroBot.AiChatPlugin.Usage;

/// <summary>
/// 上游返回的一次请求的 token 用量。<see cref="CachedTokens"/> 为 null 表示上游没有返回缓存信息，
/// 这类请求不计入缓存命中率。
/// </summary>
internal readonly record struct TokenUsage(int PromptTokens, int CompletionTokens, int? CachedTokens)
{
    /// <summary>读取 chat.completions 的 usage 对象；兼容 OpenAI、DeepSeek 和常见网关的缓存字段。</summary>
    public static TokenUsage? TryParse(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return null;
        var prompt = Int(usage, "prompt_tokens") ?? Int(usage, "input_tokens");
        var completion = Int(usage, "completion_tokens") ?? Int(usage, "output_tokens");
        if (prompt is null && completion is null) return null;

        var cached = Int(usage, "prompt_cache_hit_tokens")                     // DeepSeek
                     ?? Nested(usage, "prompt_tokens_details", "cached_tokens") // OpenAI 及多数兼容网关
                     ?? Nested(usage, "input_tokens_details", "cached_tokens")
                     ?? Int(usage, "cache_read_input_tokens");
        if (cached is null && prompt is { } total && Int(usage, "prompt_cache_miss_tokens") is { } miss)
            cached = Math.Max(0, total - miss);
        return new TokenUsage(prompt ?? 0, completion ?? 0, cached);
    }

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : null;

    private static int? Nested(JsonElement element, string parent, string name) =>
        element.TryGetProperty(parent, out var child) && child.ValueKind == JsonValueKind.Object ? Int(child, name) : null;
}

/// <summary>解析出的用量和上游返回的 usage 原文；原文只用于在日志里记录样例。</summary>
internal sealed record UsageReading(TokenUsage Tokens, string Raw);
