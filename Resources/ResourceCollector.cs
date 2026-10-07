using System.Text;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.AiChatPlugin.Resources;

/// <summary>
/// 把一条 <see cref="MessageEvent"/> 的 segments、引用消息和合并转发递归
/// 抽取为 <see cref="ChatPart"/> 列表。
/// </summary>
internal sealed class ResourceCollector(
    IBotContext bot,
    ResourceFetcher fetcher,
    ResourceCache cache,
    ResourcesSection config)
{
    public Task<List<ChatPart>> CollectAsync(MessageEvent message, CancellationToken ct = default)
        => CollectSegmentsAsync(message.Segments, message, depth: 0, ct);

    private async Task<List<ChatPart>> CollectSegmentsAsync(
        IReadOnlyList<MessageSegment> segments,
        MessageEvent context,
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
                case TextSegment text:
                    textBuf.Append(text.Text);
                    break;

                case MentionSegment mention:
                    // 按 "@昵称 " 形式保留语义
                    if (!string.IsNullOrEmpty(mention.DisplayName))
                    {
                        textBuf.Append('@').Append(mention.DisplayName).Append(' ');
                    }
                    break;

                case MentionAllSegment:
                    textBuf.Append("@全体成员 ");
                    break;

                case ImageSegment image:
                    FlushText(textBuf, parts);
                    var imagePart = await TryDownloadImageAsync(image, ct).ConfigureAwait(false);
                    if (imagePart is not null)
                    {
                        parts.Add(imagePart);
                    }
                    break;

                case FileSegment file:
                    FlushText(textBuf, parts);
                    var filePart = await TryDownloadFileAsync(file, context, ct).ConfigureAwait(false);
                    if (filePart is not null)
                    {
                        parts.Add(filePart);
                    }
                    break;

                case QuoteSegment quote:
                    FlushText(textBuf, parts);
                    if (depth < config.RecursiveDepth)
                    {
                        var nested = await ExpandReplyAsync(quote, context, depth + 1, ct).ConfigureAwait(false);
                        if (nested.Count > 0)
                        {
                            parts.Add(new TextPart($"[引用消息 #{quote.MessageId}]"));
                            parts.AddRange(nested);
                            parts.Add(new TextPart("[引用结束]"));
                        }
                    }
                    break;

                case EmojiSegment face:
                    textBuf.Append($"[表情:{face.Name ?? face.Id}]");
                    break;

                case VideoSegment:
                    textBuf.Append("[视频]");
                    break;

                case AudioSegment:
                    textBuf.Append("[语音]");
                    break;

                case RawSegment raw:
                    await HandleRawSegmentAsync(raw, context, depth, textBuf, parts, ct).ConfigureAwait(false);
                    break;
            }
        }

        FlushText(textBuf, parts);
        return parts;
    }

    /// <summary>平台特有段:QQ 的合并转发展开,其余降级为占位文本。</summary>
    private async Task HandleRawSegmentAsync(
        RawSegment raw,
        MessageEvent context,
        int depth,
        StringBuilder textBuf,
        List<ChatPart> parts,
        CancellationToken ct)
    {
        switch (raw.Payload)
        {
            case QIncomingForward forward when depth < config.RecursiveDepth:
                FlushText(textBuf, parts);
                var nested = await ExpandForwardAsync(forward, context, depth + 1, ct).ConfigureAwait(false);
                if (nested.Count > 0)
                {
                    parts.Add(new TextPart($"[合并转发: {forward.Title} - {forward.Summary}]"));
                    parts.AddRange(nested);
                    parts.Add(new TextPart("[合并转发结束]"));
                }
                break;

            case QIncomingMarketFace market:
                textBuf.Append($"[商城表情:{market.Url}]");
                break;

            case QIncomingLightApp:
                textBuf.Append("[小程序卡片]");
                break;

            case QIncomingXml:
                textBuf.Append("[XML卡片]");
                break;

            case QIncomingMarkdown markdown:
                textBuf.Append(markdown.Content);
                break;

            default:
                textBuf.Append($"[{raw.Kind}]");
                break;
        }
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

    private async Task<ChatPart?> TryDownloadImageAsync(ImageSegment image, CancellationToken ct)
    {
        var url = image.Uri;
        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(image.ResourceId))
        {
            try
            {
                url = await bot.Message.GetResourceUrlAsync(image.ResourceId).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                BotLog.Warning($"[AiChat] 获取图片临时URL失败: {ex.Message}");
                return new TextPart($"[图片(无法获取临时URL): {image.Summary}]");
            }
        }

        try
        {
            var (bytes, contentType) = await fetcher.DownloadImageAsync(url!, ct).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                BotLog.Warning("[AiChat] 下载到的图片为空。");
                return new TextPart($"[图片(空内容): {image.Summary}]");
            }

            // 用魔数 + Content-Type + URL 扩展名三重确认 mime，保证不会落到 octet-stream。
            var fallbackExt = TryParseExtensionFromUrl(url!);
            var mime = MimeGuesser.ResolveImageMime(bytes, contentType, fallbackExt);
            var ext = MimeGuesser.ExtensionForMime(mime);
            var path = await cache.StoreAsync(bytes, ext, mime).ConfigureAwait(false);

            // 即使缓存写入失败 (path 为 null)，也把字节内联起来，保证本轮请求仍然能把图片发出去。
            return new ImagePart(path, mime, bytes.Length, InlineBytes: path is null ? bytes : null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
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
        FileSegment file,
        MessageEvent context,
        CancellationToken ct)
    {
        var fileName = file.FileName ?? "unknown";
        var fileSize = file.FileSize ?? 0;

        // 仅当扩展名属于文本类、且大小符合限制时才尝试下载并读入正文。
        var isLikelyText = MimeGuesser.IsLikelyTextExtension(fileName, config.InlineTextExtensions);
        var withinSize = fileSize <= config.MaxInlineTextBytes;

        if (!isLikelyText || !withinSize)
        {
            return new FileMetaPart(fileName, fileSize, null);
        }

        try
        {
            var downloadUrl = await ResolveFileDownloadUrlAsync(file, context).ConfigureAwait(false);
            if (downloadUrl is null)
            {
                return new FileMetaPart(fileName, fileSize, null);
            }

            var (bytes, _) = await fetcher.DownloadTextFileAsync(downloadUrl, ct).ConfigureAwait(false);

            var text = TryDecodeText(bytes);
            if (text is null)
            {
                return new FileMetaPart(fileName, fileSize, null);
            }

            var truncated = false;
            if (text.Length > config.MaxFileExcerptChars)
            {
                text = text[..config.MaxFileExcerptChars];
                truncated = true;
            }

            return new FileTextPart(fileName, fileSize, text, truncated);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BotLog.Warning($"[AiChat] 文件读取失败 {fileName}: {ex.Message}");
            return new FileMetaPart(fileName, fileSize, null);
        }
    }

    /// <summary>入站文件段的下载 URL:优先直接用 Uri;QQ 平台经 IQFileApi 换取。</summary>
    private async Task<string?> ResolveFileDownloadUrlAsync(FileSegment file, MessageEvent context)
    {
        if (!string.IsNullOrWhiteSpace(file.Uri))
        {
            return file.Uri;
        }

        if (string.IsNullOrWhiteSpace(file.ResourceId))
        {
            return null;
        }

        var qqFile = bot.GetAdapterExtension<IQFileApi>();
        if (qqFile is null)
        {
            return null;
        }

        var peerId = context.Channel.Id;

        if (context.IsDirect)
        {
            // QQ 私聊文件需要 fileHash,从 Raw 里的原始段找
            var fileHash = (context.Raw as QIncomingMessage)?
                .Segments.OfType<QIncomingFile>()
                .FirstOrDefault(item => item.FileId == file.ResourceId)?.FileHash ?? string.Empty;
            return await qqFile.GetPrivateFileDownloadUrlAsync(peerId, file.ResourceId, fileHash).ConfigureAwait(false);
        }

        return await qqFile.GetGroupFileDownloadUrlAsync(peerId, file.ResourceId).ConfigureAwait(false);
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
        QuoteSegment quote,
        MessageEvent context,
        int depth,
        CancellationToken ct)
    {
        try
        {
            var original = await bot.Message.GetMessageAsync(context.Channel, quote.MessageId).WaitAsync(ct).ConfigureAwait(false);
            if (original is not null)
            {
                return await CollectSegmentsAsync(original.Segments, original, depth, ct).ConfigureAwait(false);
            }
            BotLog.Warning("[AiChat] 适配器未返回引用原消息，无法收集引用中的图片或文件。");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BotLog.Warning($"[AiChat] 获取引用原消息失败 #{quote.MessageId}: {ex.Message}");
        }

        return [];
    }

    private async Task<List<ChatPart>> ExpandForwardAsync(
        QIncomingForward forward,
        MessageEvent context,
        int depth,
        CancellationToken ct)
    {
        var aggregated = new List<ChatPart>();
        var qqMessage = bot.GetAdapterExtension<IQMessageApi>();
        if (qqMessage is null)
        {
            return aggregated;
        }

        try
        {
            var messages = await qqMessage.GetForwardedMessagesAsync(forward.ForwardId).WaitAsync(ct).ConfigureAwait(false);
            foreach (var msg in messages)
            {
                var who = string.IsNullOrEmpty(msg.SenderName) ? "unknown" : msg.SenderName;
                aggregated.Add(new TextPart($"[{who}]:"));
                var inner = await CollectQqSegmentsAsync(msg.Segments, ct).ConfigureAwait(false);
                aggregated.AddRange(inner);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BotLog.Warning($"[AiChat] 展开合并转发失败 {forward.ForwardId}: {ex.Message}");
        }

        return aggregated;
    }

    /// <summary>合并转发内部消息用 QQ 原生段,做一次轻量转换(文本/图片,其余占位)。</summary>
    private async Task<List<ChatPart>> CollectQqSegmentsAsync(
        IReadOnlyList<QIncomingSegment> segments,
        CancellationToken ct)
    {
        var parts = new List<ChatPart>();
        var textBuf = new StringBuilder();

        foreach (var seg in segments)
        {
            ct.ThrowIfCancellationRequested();
            switch (seg)
            {
                case QIncomingText text:
                    textBuf.Append(text.Text);
                    break;
                case QIncomingMention mention:
                    textBuf.Append('@').Append(mention.Name).Append(' ');
                    break;
                case QIncomingImage image:
                    FlushText(textBuf, parts);
                    var imagePart = await TryDownloadImageAsync(
                        new ImageSegment(image.TempUrl) { ResourceId = image.ResourceId, Summary = image.Summary },
                        ct).ConfigureAwait(false);
                    if (imagePart is not null)
                    {
                        parts.Add(imagePart);
                    }
                    break;
                case QIncomingFace face:
                    textBuf.Append($"[表情:{face.FaceId}]");
                    break;
                case QIncomingVideo:
                    textBuf.Append("[视频]");
                    break;
                case QIncomingRecord:
                    textBuf.Append("[语音]");
                    break;
                default:
                    textBuf.Append($"[{seg.GetType().Name}]");
                    break;
            }
        }

        FlushText(textBuf, parts);
        return parts;
    }
}
