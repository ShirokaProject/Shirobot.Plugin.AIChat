using System.Text;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.Model.Common;
using ShiroBot.Model.File.Requests;
using ShiroBot.Model.Message.Requests;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.AiChatPlugin.Resources;

/// <summary>
/// 把一条 <see cref="IncomingMessage"/> 的 segments、引用消息和合并转发递归
/// 抽取为 <see cref="ChatPart"/> 列表。
/// </summary>
internal sealed class ResourceCollector
{
    private readonly IBotContext _bot;
    private readonly ResourceFetcher _fetcher;
    private readonly ResourceCache _cache;
    private readonly ResourcesSection _config;

    public ResourceCollector(IBotContext bot, ResourceFetcher fetcher, ResourceCache cache, ResourcesSection config)
    {
        _bot = bot;
        _fetcher = fetcher;
        _cache = cache;
        _config = config;
    }

    /// <summary>
    /// 友聊入口。peerId 即对方 uid。
    /// </summary>
    public Task<List<ChatPart>> CollectAsync(FriendIncomingMessage message, CancellationToken ct = default)
        => CollectSegmentsAsync(message.Segments, ChatScene.Friend, message.PeerId, depth: 0, ct);

    public Task<List<ChatPart>> CollectAsync(GroupIncomingMessage message, CancellationToken ct = default)
        => CollectSegmentsAsync(message.Segments, ChatScene.Group, message.PeerId, depth: 0, ct);

    private async Task<List<ChatPart>> CollectSegmentsAsync(
        IReadOnlyList<IncomingSegment> segments,
        ChatScene scene,
        long peerId,
        int depth,
        CancellationToken ct)
    {
        var parts = new List<ChatPart>();
        var textBuf = new StringBuilder();

        foreach (var seg in segments)
        {
            ct.ThrowIfCancellationRequested();
            switch (seg)
            {
                case TextIncomingSegment text:
                    textBuf.Append(text.Text);
                    break;

                case MentionIncomingSegment mention:
                    // 按 "@昵称 " 形式保留语义
                    if (!string.IsNullOrEmpty(mention.Name))
                    {
                        textBuf.Append('@').Append(mention.Name).Append(' ');
                    }
                    break;

                case MentionAllIncomingSegment:
                    textBuf.Append("@全体成员 ");
                    break;

                case ImageIncomingSegment image:
                    FlushText(textBuf, parts);
                    var imagePart = await TryDownloadImageAsync(image, ct).ConfigureAwait(false);
                    if (imagePart is not null)
                    {
                        parts.Add(imagePart);
                    }
                    break;

                case FileIncomingSegment file:
                    FlushText(textBuf, parts);
                    var filePart = await TryDownloadFileAsync(file, scene, peerId, ct).ConfigureAwait(false);
                    if (filePart is not null)
                    {
                        parts.Add(filePart);
                    }
                    break;

                case ReplyIncomingSegment reply:
                    FlushText(textBuf, parts);
                    if (depth < _config.RecursiveDepth)
                    {
                        var nested = await ExpandReplyAsync(reply, scene, peerId, depth + 1, ct).ConfigureAwait(false);
                        if (nested.Count > 0)
                        {
                            parts.Add(new TextPart($"[引用消息 #{reply.MessageSeq} 来自 {reply.SenderName ?? reply.SenderId.ToString()}]"));
                            parts.AddRange(nested);
                            parts.Add(new TextPart("[引用结束]"));
                        }
                    }
                    break;

                case ForwardIncomingSegment forward:
                    FlushText(textBuf, parts);
                    if (depth < _config.RecursiveDepth)
                    {
                        var nested = await ExpandForwardAsync(forward, scene, peerId, depth + 1, ct).ConfigureAwait(false);
                        if (nested.Count > 0)
                        {
                            parts.Add(new TextPart($"[合并转发: {forward.Title} - {forward.Summary}]"));
                            parts.AddRange(nested);
                            parts.Add(new TextPart("[合并转发结束]"));
                        }
                    }
                    break;

                case FaceIncomingSegment face:
                    textBuf.Append($"[表情:{face.FaceId}]");
                    break;

                case MarketFaceIncomingSegment market:
                    textBuf.Append($"[商城表情:{market.Url}]");
                    break;

                case VideoIncomingSegment:
                    textBuf.Append("[视频]");
                    break;

                case RecordIncomingSegment:
                    textBuf.Append("[语音]");
                    break;

                case LightAppIncomingSegment:
                    textBuf.Append("[小程序卡片]");
                    break;

                case XmlIncomingSegment:
                    textBuf.Append("[XML卡片]");
                    break;
            }
        }

        FlushText(textBuf, parts);
        return parts;
    }

    private static void FlushText(StringBuilder buf, List<ChatPart> parts)
    {
        if (buf.Length == 0)
        {
            return;
        }

        var text = buf.ToString();
        buf.Clear();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        parts.Add(new TextPart(text));
    }

    private async Task<ChatPart?> TryDownloadImageAsync(ImageIncomingSegment image, CancellationToken ct)
    {
        var url = image.TempUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            try
            {
                var resp = await _bot.Message.GetResourceTempUrlAsync(image.ResourceId).ConfigureAwait(false);
                url = resp.Url;
            }
            catch (Exception ex)
            {
                BotLog.Warning($"[AiChat] 获取图片临时URL失败: {ex.Message}");
                return new TextPart($"[图片(无法获取临时URL): {image.Summary}]");
            }
        }

        try
        {
            var (bytes, contentType) = await _fetcher.DownloadImageAsync(url, ct).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                BotLog.Warning("[AiChat] 下载到的图片为空。");
                return new TextPart($"[图片(空内容): {image.Summary}]");
            }

            // 用魔数 + Content-Type + URL 扩展名三重确认 mime，保证不会落到 octet-stream。
            var fallbackExt = TryParseExtensionFromUrl(url);
            var mime = MimeGuesser.ResolveImageMime(bytes, contentType, fallbackExt);
            var ext = MimeGuesser.ExtensionForMime(mime);
            var path = await _cache.StoreAsync(bytes, ext, mime).ConfigureAwait(false);

            // 即使缓存写入失败 (path 为 null)，也把字节内联起来，保证本轮请求仍然能把图片发出去。
            return new ImagePart(path, mime, bytes.Length, InlineBytes: path is null ? bytes : null);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 下载图片失败: {ex.Message}");
            return new TextPart($"[图片(下载失败): {image.Summary}]");
        }
    }

    private static string? TryParseExtensionFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url, UriKind.Absolute);
            var path = uri.AbsolutePath;
            var ext = Path.GetExtension(path);
            return string.IsNullOrEmpty(ext) ? null : ext;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private async Task<ChatPart?> TryDownloadFileAsync(
        FileIncomingSegment file,
        ChatScene scene,
        long peerId,
        CancellationToken ct)
    {
        // 仅当扩展名属于文本类、且大小符合限制时才尝试下载并读入正文。
        var isLikelyText = MimeGuesser.IsLikelyTextExtension(file.FileName, _config.InlineTextExtensions);
        var withinSize = file.FileSize <= _config.MaxInlineTextBytes;

        if (!isLikelyText || !withinSize)
        {
            return new FileMetaPart(file.FileName, file.FileSize, file.FileHash);
        }

        try
        {
            string downloadUrl;
            if (scene == ChatScene.Friend)
            {
                var resp = await _bot.File.GetPrivateFileDownloadUrlAsync(
                    new GetPrivateFileDownloadUrlRequest(peerId, file.FileId, file.FileHash ?? string.Empty))
                    .ConfigureAwait(false);
                downloadUrl = resp.DownloadUrl;
            }
            else
            {
                var resp = await _bot.File.GetGroupFileDownloadUrlAsync(
                    new GetGroupFileDownloadUrlRequest(peerId, file.FileId))
                    .ConfigureAwait(false);
                downloadUrl = resp.DownloadUrl;
            }

            var (bytes, _) = await _fetcher.DownloadTextFileAsync(downloadUrl, ct).ConfigureAwait(false);

            var text = TryDecodeText(bytes);
            if (text is null)
            {
                return new FileMetaPart(file.FileName, file.FileSize, file.FileHash);
            }

            var truncated = false;
            if (text.Length > _config.MaxFileExcerptChars)
            {
                text = text[.._config.MaxFileExcerptChars];
                truncated = true;
            }

            return new FileTextPart(file.FileName, file.FileSize, text, truncated);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 文件读取失败 {file.FileName}: {ex.Message}");
            return new FileMetaPart(file.FileName, file.FileSize, file.FileHash);
        }
    }

    private static string? TryDecodeText(byte[] bytes)
    {
        // 简单嗅探：UTF-8 BOM、UTF-8 反序列化失败则尝试 GBK，再失败放弃。
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        try
        {
            var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // ignore
        }

        try
        {
            // GB18030 编码 ID 是 54936
            var gbk = Encoding.GetEncoding(54936);
            return gbk.GetString(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<List<ChatPart>> ExpandReplyAsync(
        ReplyIncomingSegment reply,
        ChatScene scene,
        long peerId,
        int depth,
        CancellationToken ct)
    {
        // 优先用 GetMessageAsync 拿完整原消息，失败时回退到 reply.Segments。
        try
        {
            var sceneEnum = scene == ChatScene.Friend
                ? GetMessageRequestMessageScene.Friend
                : GetMessageRequestMessageScene.Group;
            var resp = await _bot.Message.GetMessageAsync(sceneEnum, peerId, reply.MessageSeq).ConfigureAwait(false);

            var segments = ExtractSegments(resp.Message);
            if (segments is not null)
            {
                return await CollectSegmentsAsync(segments, scene, peerId, depth, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 获取引用原消息失败 #{reply.MessageSeq}: {ex.Message}");
        }

        return await CollectSegmentsAsync(reply.Segments, scene, peerId, depth, ct).ConfigureAwait(false);
    }

    private async Task<List<ChatPart>> ExpandForwardAsync(
        ForwardIncomingSegment forward,
        ChatScene scene,
        long peerId,
        int depth,
        CancellationToken ct)
    {
        var aggregated = new List<ChatPart>();
        try
        {
            var resp = await _bot.Message.GetForwardedMessagesAsync(forward.ForwardId).ConfigureAwait(false);
            foreach (var msg in resp.Messages)
            {
                var who = string.IsNullOrEmpty(msg.SenderName) ? "unknown" : msg.SenderName;
                aggregated.Add(new TextPart($"[{who}]:"));
                var inner = await CollectSegmentsAsync(msg.Segments, scene, peerId, depth, ct).ConfigureAwait(false);
                aggregated.AddRange(inner);
            }
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 展开合并转发失败 {forward.ForwardId}: {ex.Message}");
        }

        return aggregated;
    }

    private static IReadOnlyList<IncomingSegment>? ExtractSegments(IncomingMessage msg)
        => msg switch
        {
            FriendIncomingMessage f => f.Segments,
            GroupIncomingMessage g => g.Segments,
            TempIncomingMessage t => t.Segments,
            _ => null
        };
}
