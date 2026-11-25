using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Resources;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.AiChatPlugin.Delivery;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin;

/// <summary>上游请求：重试和备用模型、拼装发送给模型的消息（图片、程序记录、本次附加内容）。</summary>
public sealed partial class AiChatPlugin
{
    private async Task<string> ExecuteWithRetryAsync(Func<Task<string>> operation, CancellationToken ct,
        bool retryTransientHttpErrors = false)
    {
        var maxAttempts = Math.Clamp(_config.Default.RetryOnTimeoutMaxAttempts, 1, 5);
        var delayMs = Math.Clamp(_config.Default.RetryOnTimeoutDelayMs, 100, 10_000);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var result = await operation().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < maxAttempts &&
                (ex is TaskCanceledException or HttpRequestException or EmptyUpstreamResponseException ||
                    retryTransientHttpErrors && ex is UpstreamHttpException { IsTransient: true }))
            {
                var reason = ex switch
                {
                    EmptyUpstreamResponseException => "空响应",
                    UpstreamHttpException http => $"HTTP {(int)http.StatusCode}",
                    _ => ex.GetType().Name
                };
                var delay = TimeSpan.FromMilliseconds(delayMs * attempt);
                BotLog.Warning($"[AiChat] 上游请求第 {attempt}/{maxAttempts} 次失败 ({reason})，{delay.TotalMilliseconds:0}ms 后重试。");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<string> ExecuteReplyAttemptAsync(OfficialReplyDelivery delivery,
        Func<Func<string, Task>?, Task<string>> operation, CancellationToken ct = default)
    {
        var streaming = delivery.IsStreaming;
        try
        {
            return await operation(streaming ? delivery.AppendAsync : null).ConfigureAwait(false);
        }
        catch (Exception ex) when (streaming && !ct.IsCancellationRequested && ex is not ToolRoundResponseFailedException)
        {
            BotLog.Warning($"[AiChat] 上游流式请求失败，改用普通文本请求: {ex.Message}");
            await delivery.DisableStreamingAsync().ConfigureAwait(false);
            // 不支持 SSE、响应截断或请求失败时，重建非流式请求；后续仍使用原有重试/备用模型规则。
            return await operation(null).ConfigureAwait(false);
        }
    }

    private async Task<string> ExecuteTextWithFallbackAsync(ModelEntry model,
        Func<ModelEntry, Task<string>> operation, CancellationToken ct, bool retryTransientHttpErrors = false)
    {
        try
        {
            return await ExecuteWithRetryAsync(() => operation(model), ct, retryTransientHttpErrors).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not ToolRoundResponseFailedException && (ex is not OperationCanceledException || ex is TaskCanceledException))
        {
            if (string.IsNullOrWhiteSpace(_config.Default.FallbackModel)) throw;
            var fallback = ResolveModel(_config.Default.FallbackModel);
            if (fallback is null)
            {
                BotLog.Warning($"[AiChat] 备用模型 {_config.Default.FallbackModel} 未注册，无法回退。");
                throw;
            }
            if (string.Equals(GetModelDisplayId(model), GetModelDisplayId(fallback), StringComparison.OrdinalIgnoreCase))
                throw;

            BotLog.Warning($"[AiChat] 模型 {GetModelDisplayId(model)} 请求失败，回退到 {GetModelDisplayId(fallback)}: {ex.Message}");
            return await ExecuteWithRetryAsync(() => operation(fallback), ct, retryTransientHttpErrors).ConfigureAwait(false);
        }
    }

    private List<OpenAiMessage> BuildOpenAiMessages(string systemPrompt, IReadOnlyList<ChatTurn> history, ChatTurn currentUserTurn,
        IReadOnlyList<ChatPart>? extras = null, bool supportsVision = true)
        => BuildVisionMessages(systemPrompt, history, currentUserTurn, supportsVision, extras)
            .Select(message => new OpenAiMessage { Role = message.Role, Content = message }).ToList();

    private List<ChatMessageDescriptor> BuildMessageDescriptors(string systemPrompt, IReadOnlyList<ChatTurn> history, ChatTurn currentUserTurn,
        IReadOnlyList<ChatPart>? extras = null)
        => BuildVisionMessages(systemPrompt, history, currentUserTurn, true, extras);

    // Historical text is serialized identically whether an image expires or is selected again.
    // Only the vision payload at the end of the current user message changes; cache files remain available for explicit image tools.
    /// <param name="extras">只属于本次请求的内容（会话上下文、程序附加参考等），单独成一条放在当前用户消息之后，不进历史。</param>
    internal List<ChatMessageDescriptor> BuildVisionMessages(string systemPrompt, IReadOnlyList<ChatTurn> history, ChatTurn current, bool supportsVision,
        IReadOnlyList<ChatPart>? extras = null)
    {
        var messages = new List<ChatMessageDescriptor>
        {
            new() { Role = "system", Segments = [new() { Kind = SegmentKind.Text, Text = systemPrompt }] }
        };
        // 程序记录存在助手消息里，发送时移到下一条用户消息开头：留在助手消息里时模型会当成自己说过的话并照着写。
        var records = new List<string>();
        ChatMessageDescriptor UserWithRecords(ChatTurn turn)
        {
            var descriptor = TurnToDescriptor(turn, false);
            if (records.Count == 0) return descriptor;
            descriptor.Segments.Insert(0, new ContentSegment
            {
                Kind = SegmentKind.Text,
                Text = string.Join("\n", records.Select(ToolCallRecord.ForUserSide)) + "\n"
            });
            records.Clear();
            return descriptor;
        }
        foreach (var turn in history)
        {
            if (turn.Role != "assistant")
            {
                messages.Add(UserWithRecords(turn));
                continue;
            }
            var texts = turn.Parts.OfType<TextPart>().Select(part => part.Text).ToList();
            records.AddRange(texts.Where(ToolCallRecord.IsRecord));
            var reply = string.Concat(texts.Where(text => !ToolCallRecord.IsRecord(text)));
            if (reply.Length > 0)
                messages.Add(new ChatMessageDescriptor
                {
                    Role = "assistant",
                    Segments = [new ContentSegment { Kind = SegmentKind.Text, Text = reply }]
                });
        }
        messages.Add(UserWithRecords(current));
        if (extras is { Count: > 0 }) messages.Add(TurnToDescriptor(new ChatTurn("user", [.. extras], current.Timestamp), false));
        if (!supportsVision) return messages;

        var selected = new List<ChatPart>();
        var limit = Math.Clamp(_config.History.MaxImagesPerRequest, 1, 16);
        var count = 0;
        void AddImages(ChatTurn turn, string label)
        {
            foreach (var image in turn.Parts.OfType<ImagePart>())
            {
                if (count >= limit) break;
                selected.Add(new TextPart(label));
                selected.Add(image);
                count++;
            }
        }
        AddImages(current, "[本条用户消息或引用中的图片]");
        for (var i = history.Count - 1; i >= 0 && count < limit; i--)
            if (ShouldIncludeHistoryImages(history, i)) AddImages(history[i], HistoryImageLabel(history[i]));
        // 图片并进最后一条消息（有本次附加内容时就是那一条），不再单独成一条：上游常在“最后一条消息之前”留缓存点，
        // 图片单独一条时那个点落在会被换掉的附加内容之后，下一轮对不上，只能退回很早的缓存。
        if (count > 0) messages[^1].Segments.AddRange(TurnToDescriptor(new ChatTurn("user", selected, current.Timestamp), true).Segments);
        BotLog.Info($"[AiChat] 本轮携带图片 {count} 张，上限 {limit}，历史图片记忆轮数 {_config.History.ImageMemoryTurns}");
        return messages;
    }

    /// <summary>
    /// 历史图片附在当前消息之后，不写明来源时模型会以为是当前用户刚发的；标出发送者和时间，并说明不是本次发送。
    /// </summary>
    internal static string HistoryImageLabel(ChatTurn turn)
    {
        var first = turn.Parts.OfType<TextPart>().FirstOrDefault()?.Text ?? "";
        var speaker = HistorySpeaker().Match(first);
        var who = speaker.Success ? $"{speaker.Groups[1].Value}({speaker.Groups[2].Value}) " : "之前的消息中";
        var time = DateTimeOffset.FromUnixTimeMilliseconds(turn.Timestamp).ToLocalTime().ToString("MM-dd HH:mm");
        return $"[历史图片：{who}在 {time} 发送或引用的图片，不是当前用户这次发的；只在当前消息提到或明显相关时参考]";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\[([^\]/\n]+)/(\d+)\]")]
    private static partial System.Text.RegularExpressions.Regex HistorySpeaker();

    private bool ShouldIncludeHistoryImages(IReadOnlyList<ChatTurn> history, int index)
    {
        if (_config.History.ImageMemoryTurns <= 0 || history[index].Role != "user") return false;

        var newerUserTurns = 0;
        for (var i = history.Count - 1; i >= index; i--)
        {
            if (history[i].Role != "user") continue;
            if (i == index) return newerUserTurns < _config.History.ImageMemoryTurns;
            newerUserTurns++;
        }

        return false;
    }

    private ChatMessageDescriptor TurnToDescriptor(ChatTurn turn, bool includeImages)
    {
        if (turn.Role == "assistant")
        {
            var combined = string.Concat(turn.Parts.OfType<TextPart>().Select(p => p.Text));
            return new ChatMessageDescriptor
            {
                Role = "assistant",
                Segments = [new ContentSegment { Kind = SegmentKind.Text, Text = combined }]
            };
        }

        var segments = new List<ContentSegment>();
        foreach (var p in turn.Parts)
        {
            switch (p)
            {
                case TextPart tp when !string.IsNullOrEmpty(tp.Text):
                    segments.Add(new ContentSegment { Kind = SegmentKind.Text, Text = tp.Text });
                    break;

                case ImagePart ip:
                    if (!includeImages)
                    {
                        segments.Add(new ContentSegment { Kind = SegmentKind.Text, Text = ImagePart.HistoryPlaceholder });
                        break;
                    }

                    // 获取 .b64 文件路径（不读取内容）
                    var b64Path = GetB64FilePath(ip.CachePath);
                    if (b64Path is not null)
                    {
                        segments.Add(new ContentSegment { Kind = SegmentKind.Image, B64FilePath = b64Path });
                    }
                    else if (ip.InlineBytes is { Length: > 0 })
                    {
                        // 缓存失败时保留原字节，发送阶段分块编码
                        var mime = MimeGuesser.SniffImageMime(ip.InlineBytes)
                                   ?? (ip.Mime.StartsWith("image/", StringComparison.Ordinal) ? ip.Mime : "image/png");
                        segments.Add(new ContentSegment { Kind = SegmentKind.Image,
                            InlineBytes = ip.InlineBytes, Mime = mime });
                    }
                    else
                    {
                        segments.Add(new ContentSegment { Kind = SegmentKind.Text, Text = "[图片读取失败]" });
                    }
                    break;

                case FileTextPart ft:
                    var truncMark = ft.Truncated ? " (已截断)" : "";
                    segments.Add(new ContentSegment
                    {
                        Kind = SegmentKind.Text,
                        Text = $"[文件: {ft.Name} ({ft.Size} 字节){truncMark}]\n```\n{ft.Excerpt}\n```"
                    });
                    break;

                case FileMetaPart fm:
                    var hashStr = string.IsNullOrEmpty(fm.Sha256) ? "" : $", sha256={fm.Sha256}";
                    segments.Add(new ContentSegment
                    {
                        Kind = SegmentKind.Text,
                        Text = $"[文件: {fm.Name} ({fm.Size} 字节{hashStr}) (二进制内容未加载)]"
                    });
                    break;
            }
        }

        AppendUserTimestamp(segments, turn);

        if (segments.Count == 0)
            segments.Add(new ContentSegment { Kind = SegmentKind.Text, Text = "" });

        return new ChatMessageDescriptor { Role = turn.Role, Segments = segments };
    }

    /// <summary>
    /// 从 cachePath 推导 .b64 文件路径。
    /// </summary>
    private static string? GetB64FilePath(string? cachePath)
    {
        if (string.IsNullOrEmpty(cachePath)) return null;
        var dir = Path.GetDirectoryName(cachePath);
        var name = Path.GetFileNameWithoutExtension(cachePath);
        var b64Path = Path.Combine(dir ?? "", name + ".b64");
        return File.Exists(b64Path) ? b64Path : null;
    }

    private OpenAiMessage TurnToMessage(ChatTurn turn, bool includeImages)
    {
        var descriptor = TurnToDescriptor(turn, includeImages);
        return new OpenAiMessage { Role = turn.Role, Content = descriptor };
    }

    private static void AppendUserTimestamp(List<ContentSegment> segments, ChatTurn turn)
    {
        if (turn.Role != "user") return;

        segments.Add(new ContentSegment { Kind = SegmentKind.Text, Text = FormatUserTimestamp(turn.Timestamp) });
    }

    private static void AppendUserTimestamp(List<Dictionary<string, object>> contentParts, ChatTurn turn)
    {
        if (turn.Role != "user") return;

        contentParts.Add(new Dictionary<string, object>
        {
            ["type"] = "text",
            ["text"] = FormatUserTimestamp(turn.Timestamp)
        });
    }

    private static string FormatUserTimestamp(long timestamp) =>
        $"[消息时间: {DateTimeOffset.FromUnixTimeMilliseconds(timestamp).LocalDateTime:yyyy-MM-dd HH:mm:ss zzz}]";
}
