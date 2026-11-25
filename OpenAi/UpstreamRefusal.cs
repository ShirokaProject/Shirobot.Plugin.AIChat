namespace ShiroBot.AiChatPlugin.OpenAi;

/// <summary>
/// 网关在内容审核拒绝时不返回错误，而是把拒绝说明当成正常回复（finish_reason=stop）返回。
/// 这类文本不能发到群里，也不能进历史，否则之后每轮都带着它。
/// </summary>
internal static class UpstreamRefusal
{
    private static readonly string[] Markers =
    [
        "The prompt could not be submitted",
        "Generative AI Prohibited Use policy"
    ];

    public static bool Is(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.TrimStart();
        return trimmed.StartsWith(Markers[0], StringComparison.OrdinalIgnoreCase)
            || trimmed.Length < 600 && trimmed.Contains(Markers[1], StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>上游内容审核拒绝；抛出后由备用模型重试，备用模型也拒绝时提示用户。</summary>
internal sealed class UpstreamRefusalException(string text) : Exception("上游内容审核拒绝")
{
    public string Text { get; } = text;
}
