using System.Text;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.Proactive;
using ShiroBot.AiChatPlugin.Resources;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;

namespace ShiroBot.AiChatPlugin;

/// <summary>消息入口：私聊/群聊 @ 的分发，以及群聊最近消息的记录和格式化。</summary>
public sealed partial class AiChatPlugin
{
    private bool ShouldAutoReplyDirectMessage(MessageEvent message)
    {
        if (!_config.Behaviour.AutoReplyDirectMessages || !message.IsDirect) return false;
        var text = message.GetPlainText().TrimStart();
        return !text.StartsWith('#') && !text.StartsWith('/');
    }

    private Task HandleFriendAutoReplyAsync(MessageEvent message) =>
        HandleFriendConversationAsync(message, prefixToStrip: null);

    private Task HandleFriendAiAsync(MessageEvent message) =>
        HandleFriendConversationAsync(message, prefixToStrip: "#ai");

    private async Task HandleFriendConversationAsync(MessageEvent message, string? prefixToStrip)
    {
        var key = ResolveConversationKey(ChatScene.Friend, PeerId(message), SenderId(message));
        await ProcessAiAsync(message, key, prefixToStrip, isGroup: false, invokedByMention: false).ConfigureAwait(false);
    }

    private async Task HandleGroupAiAsync(MessageEvent message)
    {
        var key = ResolveConversationKey(ChatScene.Group, PeerId(message), SenderId(message));
        await ProcessAiAsync(message, key, prefixToStrip: "#ai", isGroup: true, invokedByMention: false).ConfigureAwait(false);
    }

    private async Task HandleGroupMentionAsync(MessageEvent message)
    {
        var key = ResolveConversationKey(ChatScene.Group, PeerId(message), SenderId(message));
        // 移除触发本次路由的自身 @，避免模型把机器人名称误认成第三方。
        await ProcessAiAsync(message, key, prefixToStrip: null, isGroup: true, invokedByMention: true).ConfigureAwait(false);
    }

    /// <summary>没有 @ 机器人的普通群消息：开启群聊上下文或主动回复时记录下来。</summary>
    private bool ShouldRecordGroupMessage(MessageEvent message)
    {
        if (_config.History.GroupContextMessages <= 0 && !ProactiveTrigger.IsAllowedGroup(_config.Proactive, message.Channel.Id)) return false;
        if (message.Sender.Id == message.SelfId) return false;
        var text = message.GetPlainText().TrimStart();
        return !text.StartsWith('#') && !text.StartsWith('/');
    }

    /// <summary>
    /// 当前消息之前的群聊记录：优先用适配器的历史消息接口（包含 @ 机器人的消息和机器人自己的发言，重启后也能拿到），
    /// 不支持或失败时用内存中记录的普通消息。
    /// </summary>
    /// <param name="recordedAfter">内存记录中排在这条消息之后（含自身）的条数，兜底时跳过。</param>
    /// <param name="after">会话最近一次 #clear 的时间；只附带这之后的消息。</param>
    private async Task<string> GetRecentGroupMessagesAsync(MessageEvent message, int count, string selfName, int recordedAfter, CancellationToken ct,
        ISet<string>? excludedIds = null, DateTimeOffset? after = null)
    {
        if (count <= 0) return "";
        try
        {
            var history = await Context.Message.GetHistoryMessagesAsync(message.Channel, message.MessageId, count + 1, ct).ConfigureAwait(false);
            // 不同适配器对 beforeMessageId 是否包含自身、返回顺序不一致，统一过滤并按时间排序。
            var lines = history
                .Where(m => m.MessageId != message.MessageId && (message.Timestamp == default || m.Timestamp <= message.Timestamp))
                .DistinctBy(m => m.MessageId)
                .OrderBy(m => m.Timestamp)
                .TakeLast(count)
                .Where(m => !(excludedIds?.Contains(m.MessageId) ?? false))
                .Where(m => after is null || m.Timestamp == default || m.Timestamp > after)
                .Select(m => FormatGroupLine(m, selfName));
            return string.Join('\n', lines);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (ex is not NotSupportedException) BotLog.Warning($"[AiChat] 获取群历史消息失败，改用内存记录: {ex.Message}");
            return _proactive.Recent(ProactiveKey(message), count, recordedAfter, after);
        }
    }

    internal static string DisplayUserId(string id) => string.Concat(id.EnumerateRunes().Take(15));

    private bool IsChatMaster(MessageEvent message)
    {
        var configured = _config.OwnerPrompt.MasterIds.Any(entry =>
            string.Equals(entry.Trim(), message.Sender.Id, StringComparison.Ordinal) ||
            (!string.IsNullOrEmpty(message.InstanceId) && string.Equals(entry.Trim(),
                message.InstanceId + "/" + message.Sender.Id, StringComparison.Ordinal)));
        return configured || (!string.IsNullOrEmpty(message.InstanceId) &&
            Context.IsOwner(new UserReference(message.InstanceId, message.Sender.Id)));
    }

    private string IdentityTags(MessageEvent message)
    {
        var tags = IsChatMaster(message) ? "[Master] " : "";
        if (string.Equals(message.Platform, "qq", StringComparison.OrdinalIgnoreCase))
            tags += message.Member?.Role switch
            {
                MemberRole.Owner => "[群主] ",
                MemberRole.Admin => "[群管理员] ",
                _ => ""
            };
        return tags;
    }

    private string FormatGroupLine(MessageEvent message, string? selfName = null)
    {
        var name = message.Sender.Id == message.SelfId && selfName is not null
            ? selfName + "（你）" : message.Member?.Nick ?? message.Sender.Name ?? DisplayUserId(message.Sender.Id);
        return ProactiveTrigger.FormatLine(message.Timestamp, name + "(" + DisplayUserId(message.Sender.Id) + ")",
            DescribeMessage(message), IdentityTags(message), message.MessageId);
    }

    /// <summary>把消息段转成一行文字，图片等资源只保留类型。</summary>
    private static string DescribeMessage(MessageEvent message)
    {
        var sb = new StringBuilder();
        foreach (var segment in message.Segments)
        {
            sb.Append(segment switch
            {
                TextSegment text => text.Text,
                MentionSegment mention => "@" + (mention.DisplayName ?? mention.UserId) + " ",
                MentionAllSegment => "@全体成员 ",
                ImageSegment => "[图片]",
                EmojiSegment => "[表情]",
                AudioSegment => "[语音]",
                VideoSegment => "[视频]",
                FileSegment => "[文件]",
                QuoteSegment => "",
                RawSegment { Payload: QIncomingLightApp app } => QqCardText.Describe(app, brief: true),
                RawSegment { Payload: QIncomingXml xml } => QqCardText.Describe(xml, brief: true),
                RawSegment { Payload: QIncomingMarketFace face } => string.IsNullOrWhiteSpace(face.Summary) ? "[表情]" : $"[表情 {face.Summary.Trim().Trim('[', ']')}]",
                RawSegment { Payload: QIncomingMarkdown markdown } => markdown.Content.Length > 300 ? markdown.Content[..300] + "…" : markdown.Content,
                _ => "[消息]"
            });
        }
        var described = sb.ToString().Trim();
        return described.Length == 0 ? "[消息]" : described;
    }

    private static int CountLines(string transcript) => transcript.Length == 0 ? 0 : transcript.Count(c => c == '\n') + 1;

    private static string ProactiveKey(MessageEvent message) => $"{message.InstanceId}:{message.Channel.Id}";
}
