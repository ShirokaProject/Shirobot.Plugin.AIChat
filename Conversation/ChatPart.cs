using System.Text.Json.Serialization;

namespace ShiroBot.AiChatPlugin.Conversation;

/// <summary>
/// 一条多模态消息片段。落盘和走内存都用这套结构，
/// 只在发请求前才把 ImagePart 转成 base64 内联。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(ImagePart), "image")]
[JsonDerivedType(typeof(FileTextPart), "file_text")]
[JsonDerivedType(typeof(FileMetaPart), "file_meta")]
public abstract record ChatPart;

/// <summary>纯文本。</summary>
public sealed record TextPart(string Text) : ChatPart;

/// <summary>
/// 图片片段。优先用 CachePath 读字节转 base64；如果缓存写入失败，
/// 会把字节直接放进 InlineBytes 兜底，保证当前一轮请求仍然能把图片送出去。
/// 落盘时只持久化 CachePath / Mime / Bytes，InlineBytes 不进 JSON。
/// </summary>
public sealed record ImagePart(
    string? CachePath,
    string Mime,
    int Bytes,
    [property: JsonIgnore] byte[]? InlineBytes = null) : ChatPart
{
    internal const string HistoryPlaceholder = "[图片；需要发送的内容附在本次请求末尾。]";

    /// <summary>
    /// 生图完成后写进历史的说明。工具调用本身不进历史，只写“生成图片：提示词”时，
    /// 模型会以为输出这段文字就能出图，转而模仿它而不调用 imagegen。
    /// </summary>
    internal const string GeneratedRecordPrefix = "[程序记录：imagegen 工具已生成并单独发送这张图片，不是你在回复里写的文字；提示词：";
    internal const string LegacyGeneratedPrefix = "[生成图片：";

    internal static string GeneratedRecord(string prompt) => GeneratedRecordPrefix + prompt + "]";
}

/// <summary>文本类文件的截断后正文。</summary>
public sealed record FileTextPart(string Name, long Size, string Excerpt, bool Truncated) : ChatPart;

/// <summary>无法读为文本的文件，仅保留元信息。</summary>
public sealed record FileMetaPart(string Name, long Size, string? Sha256) : ChatPart;
