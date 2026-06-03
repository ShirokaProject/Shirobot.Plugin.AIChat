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
    [property: JsonIgnore] byte[]? InlineBytes = null) : ChatPart;

/// <summary>文本类文件的截断后正文。</summary>
public sealed record FileTextPart(string Name, long Size, string Excerpt, bool Truncated) : ChatPart;

/// <summary>无法读为文本的文件，仅保留元信息。</summary>
public sealed record FileMetaPart(string Name, long Size, string? Sha256) : ChatPart;
