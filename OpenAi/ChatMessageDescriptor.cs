using System.Text.Json;

namespace ShiroBot.AiChatPlugin.OpenAi;

/// <summary>
/// 轻量消息描述，不持有图片 data URL string。
/// 图片通过 B64FilePath 引用磁盘上的 .b64 文件，发请求时流式写入。
/// </summary>
internal sealed class ChatMessageDescriptor
{
    public required string Role { get; init; }

    /// <summary>
    /// 消息内容段。纯文本消息只有一个 TextSegment；多模态消息可能混合 Text 和 Image。
    /// </summary>
    public required List<ContentSegment> Segments { get; init; }

    /// <summary>是否包含图片段。</summary>
    public bool HasImage => Segments.Any(s => s.Kind == SegmentKind.Image);
}

internal enum SegmentKind
{
    Text,
    Image
}

internal sealed class ContentSegment
{
    public SegmentKind Kind { get; init; }

    /// <summary>文本内容（Kind == Text 时使用）。</summary>
    public string? Text { get; init; }

    /// <summary>.b64 文件的绝对路径（Kind == Image 时使用）。</summary>
    public string? B64FilePath { get; init; }

    /// <summary>图片的 data URL 前缀，如 "data:image/png;base64,"（Kind == Image 且无 .b64 文件时的 inline fallback）。</summary>
    public string? InlineDataUrl { get; init; }
}
