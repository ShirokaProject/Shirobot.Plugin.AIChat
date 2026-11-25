using System.Text;
using ShiroBot.AiChatPlugin.Contracts;
using ShiroBot.AiChatPlugin.Delivery;
using ShiroBot.AiChatPlugin.Proactive;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.AiChatPlugin;

/// <summary>回复发送：分条、折叠长回复、引用和撤回。</summary>
public sealed partial class AiChatPlugin
{
    private Task ReplyAssistantAsync(MessageEvent message, bool isGroup, string text) =>
        ReplyFoldableAsync(message, isGroup, text, _config.Behaviour.FoldAboveChars);

    /// <summary>
    /// 聊天模式逐条发送。只有模型选择 [chat+reply] 时第一条按引用/@ 配置回复，其余作为普通消息发出。
    /// 回复已经开始，因此间隔不受撤回取消影响。
    /// </summary>
    private async Task SendChatMessagesAsync(MessageEvent message, bool isGroup, OfficialReplyDelivery delivery, ParsedReply reply)
    {
        for (var i = 0; i < reply.Messages.Count; i++)
        {
            var text = reply.Messages[i];
            if (i > 0) await Task.Delay(ChatMessageDelay(text)).ConfigureAwait(false);
            var asReply = i == 0 && reply.Quote;
            try
            {
                if (await delivery.CompleteAsync(text, mention: asReply).ConfigureAwait(false)) continue;
                if (asReply) await ReplyAsync(message, isGroup, text).ConfigureAwait(false);
                else await Context.Message.SendMessageAsync(message.Channel, [new TextSegment(text)]).ConfigureAwait(false);
            }
            catch (Exception ex) when (i > 0)
            {
                // 首条已送达，后续单条失败不影响保存这轮历史。
                BotLog.Warning($"[AiChat] 分条发送第 {i + 1} 条失败: {ex.Message}");
            }
        }
    }

    private TimeSpan ChatMessageDelay(string next)
    {
        var baseMs = Math.Clamp(_config.Behaviour.ChatMessageDelayMs, 0, 10_000);
        if (baseMs == 0) return TimeSpan.Zero;
        var typingMs = Math.Min(next.EnumerateRunes().Count() * 60, 3000);
        return TimeSpan.FromMilliseconds(baseMs + typingMs);
    }

    /// <summary>
    /// 依次执行插件工具要求的回复后动作。没有发出文字时，由第一个动作按设置 @/引用触发消息。
    /// 返回动作给出的 History 文本，用于写入历史；失败只记日志。
    /// </summary>
    private async Task<string> RunFollowUpsAsync(List<ChatAfterReply> followUps, MessageEvent message,
        ProactiveRequest? proactive, bool textSent, CancellationToken ct)
    {
        var history = new List<string>();
        for (var i = 0; i < followUps.Count; i++)
        {
            var decorate = !textSent && i == 0 && proactive is null;
            var reply = new ChatReplyState(textSent, decorate && _config.Behaviour.ReplyWithMention, decorate && _config.Behaviour.ReplyWithQuote);
            try
            {
                var result = await ChatTools.AfterReply.CallAsync(Context, followUps[i].Name,
                    new ChatAfterReplyCall(followUps[i].Arguments, ToolContext(message, proactive), reply), ct).ConfigureAwait(false);
                if (result is { Ok: true, History.Length: > 0 }) history.Add(result.History);
                if (result is { Ok: false })
                    BotLog.Warning($"[AiChat] 回复后动作未完成: {followUps[i].Name} - {result.Error}");
                else
                    BotLog.Info($"[AiChat] 回复后动作完成: {followUps[i].Name}");
            }
            catch (Exception ex) when (ex is PluginServiceUnavailableException or PluginServiceException)
            {
                BotLog.Warning($"[AiChat] 回复后动作失败: {followUps[i].Name} - {ex.Message}");
            }
        }
        return string.Join("\n", history);
    }

    private async Task ReplyFoldableAsync(MessageEvent message, bool isGroup, string text, int threshold)
    {
        if (threshold > 0 && text.EnumerateRunes().Count() > threshold &&
            string.Equals(message.Platform, "qq", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(message.SelfId))
        {
            var selfName = await ResolveSelfNameAsync(message).ConfigureAwait(false);
            var nodes = ParsedReply.SplitForForward(text, _config.Behaviour.ForwardNodeMaxChars)
                .Select(node => new QForwardedMessage(message.SelfId, selfName, [new QOutgoingText(node)])).ToList();
            var forward = new QOutgoingForward(nodes)
            {
                Title = $"{selfName} 的回复",
                Preview = [CreateReplyPreview(text)],
                Summary = "点击查看全部",
                Prompt = "[AI 回复]"
            };
            try
            {
                var segments = new List<MessageSegment>();
                if (isGroup && _config.Behaviour.ReplyWithMention)
                {
                    segments.Add(CreateReplyMention(message));
                    segments.Add(new TextSegment(" "));
                }
                segments.Add(new RawSegment("qq", "forward", forward));
                var sent = await Context.Message.SendMessageAsync(message.Channel,
                    segments).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(sent.MessageId)) return;
                BotLog.Warning("[AiChat] 折叠回复未返回消息 ID，改用普通文本。");
            }
            catch (Exception ex)
            {
                BotLog.Warning($"[AiChat] 折叠回复发送失败，改用普通文本: {ex.Message}");
            }
            // 合并转发失败多半是长消息被拒，普通文本也分成几段发，只有第一段引用原消息。
            var chunks = ParsedReply.SplitForForward(text, FallbackChunkChars);
            await ReplyAsync(message, isGroup, chunks[0]).ConfigureAwait(false);
            foreach (var chunk in chunks.Skip(1))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                await Context.Message.SendMessageAsync(message.Channel, [new TextSegment(chunk)]).ConfigureAwait(false);
            }
            return;
        }
        await ReplyAsync(message, isGroup, text).ConfigureAwait(false);
    }

    /// <summary>合并转发失败时每段普通文本的最大字数。</summary>
    internal const int FallbackChunkChars = 1500;

    private static string CreateReplyPreview(string text)
    {
        var singleLine = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var preview = new StringBuilder();
        var count = 0;
        foreach (var rune in singleLine.EnumerateRunes())
        {
            if (count++ == 120) return preview.Append('…').ToString();
            preview.Append(rune.ToString());
        }
        return preview.ToString();
    }

    private async Task ReplyAsync(MessageEvent message, bool isGroup, string text)
    {
        if (isGroup)
        {
            await ReplyToGroup(message, text).ConfigureAwait(false);
        }
        else
        {
            await Context.Message.ReplyAsync(message, text).ConfigureAwait(false);
        }
    }

    private Task ReplyToGroup(MessageEvent message, string text)
    {
        if (_config.Behaviour.ReplyWithMention)
        {
            MessageSegment[] segments = [CreateReplyMention(message), new TextSegment(" " + text)];
            return _config.Behaviour.ReplyWithQuote
                ? Context.Message.QuoteReplyAsync(message, segments)
                : Context.Message.ReplyAsync(message, segments);
        }
        return _config.Behaviour.ReplyWithQuote
            ? Context.Message.QuoteReplyAsync(message, text)
            : Context.Message.ReplyAsync(message, text);
    }

    private static MentionSegment CreateReplyMention(MessageEvent message) => new(message.Sender.Id)
    {
        DisplayName = message.Member?.DisplayName ?? message.Sender.Name ?? message.Sender.Id
    };

    /// <summary>
    /// 延迟指定秒数后撤回群消息。fire-and-forget，失败静默。
    /// </summary>
    private void ScheduleRecall(Channel channel, string messageId, int delaySeconds)
    {
        lock (_backgroundGate)
        {
            if (_backgroundStop.IsCancellationRequested) return;
            var token = _backgroundStop.Token;
            var task = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
                    await Context.Message.DeleteMessageAsync(channel, messageId, token).ConfigureAwait(false);
                }
                catch { /* cancellation or missing platform permissions */ }
            });
            _recallTasks.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_backgroundGate) _recallTasks.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
