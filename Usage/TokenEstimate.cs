using ShiroBot.AiChatPlugin.OpenAi;

namespace ShiroBot.AiChatPlugin.Usage;

/// <summary>
/// 粗略的 token 估算，压缩判断和本地前缀复用统计共用。
/// 图片按固定数量计，不按 base64 体积：上游按图片尺寸计费，一张图的 base64 动辄上百 KB，按字节算会把统计全部压在图片上。
/// </summary>
internal static class TokenEstimate
{
    public const int ImageTokens = 800;

    /// <summary>CJK 字符约 1 token，其他字符约 4 个 1 token。</summary>
    public static int Text(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var cjk = 0;
        var other = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value >= 0x2E80) cjk++;
            else other++;
        }
        return cjk + (other + 3) / 4;
    }

    public static long Message(OpenAiMessage message)
    {
        long tokens = 4 + Text(message.ReasoningContent);
        tokens += message.Content switch
        {
            ChatMessageDescriptor descriptor => descriptor.Segments.Sum(segment =>
                segment.Kind == SegmentKind.Image ? ImageTokens : (long)Text(segment.Text)),
            string text => Text(text),
            null => 0,
            var other => Text(PluginJson.Serialize(other))
        };
        foreach (var call in message.ToolCalls ?? [])
            tokens += Text(call.Function?.Name) + Text(call.Function?.Arguments);
        return tokens;
    }

    public static long Tools(IEnumerable<OpenAiTool> tools) =>
        tools.Sum(tool => (long)Text(tool.Function.Name) + Text(tool.Function.Description) + Text(PluginJson.Serialize(tool.Function.Parameters)));
}
