using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.Usage;
using ShiroBot.SDK.Models;

namespace ShiroBot.AiChatPlugin;

/// <summary>零散的辅助方法：token 估算、用户和会话标识、命令前缀处理。</summary>
public sealed partial class AiChatPlugin
{
    private static int EstimateRequestTokens(string systemPrompt, IEnumerable<ChatTurn> history, ChatTurn currentUserTurn, IReadOnlyList<ChatPart> extras) =>
        EstimateTokens(systemPrompt) + history.Sum(EstimateTurnTokens) + EstimateTurnTokens(currentUserTurn)
        + (extras.Count == 0 ? 0 : EstimateTurnTokens(new ChatTurn("user", [.. extras], 0)));

    private static int EstimateTurnTokens(ChatTurn turn) => 4 + turn.Parts.Sum(part => part switch
    {
        TextPart text => EstimateTokens(text.Text),
        FileTextPart file => EstimateTokens(file.Excerpt) + 20,
        ImagePart => TokenEstimate.ImageTokens,
        _ => 20
    });

    private static int EstimateTokens(string text) => TokenEstimate.Text(text);

    private static string TurnToTranscript(ChatTurn turn) => string.Join(" ", turn.Parts.Select(part => part switch
    {
        TextPart text => text.Text,
        ImagePart => "[图片]",
        FileTextPart file => $"[文件 {file.Name}: {Truncate(file.Excerpt, 300)}]",
        FileMetaPart file => $"[文件 {file.Name}]",
        _ => ""
    }));

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "…";

    /// <summary>调试日志中的模型文本，过长时截断。</summary>
    private static string ForLog(string text) => Truncate(text, 2000);

    private static string SenderId(MessageEvent message) => message.Sender.Id;

    private static string PeerId(MessageEvent message) => message.Channel.Id;

    private async Task<string> ResolveSelfNameAsync(MessageEvent message)
    {
        var key = message.Platform + ":" + message.SelfId;
        lock (_selfNames)
        {
            if (_selfNames.TryGetValue(key, out var cached)) return cached;
        }

        try
        {
            var self = await Context.User.GetSelfAsync().ConfigureAwait(false);
            var name = string.IsNullOrWhiteSpace(self.Name) ? self.Id : self.Name;
            lock (_selfNames) _selfNames[key] = name;
            return name;
        }
        catch
        {
            return string.IsNullOrWhiteSpace(message.SelfId) ? "机器人" : message.SelfId;
        }
    }

    private static List<ChatPart> AddSharedSpeakerPrefix(List<ChatPart> parts, MessageEvent message)
    {
        var displayName = message.Member?.Nick ?? message.Sender.Name ?? DisplayUserId(message.Sender.Id);
        var prefix = $"[{displayName}/{DisplayUserId(message.Sender.Id)}] ";

        var result = new List<ChatPart>(parts.Count + 1);
        var prefixed = false;
        foreach (var part in parts)
        {
            if (!prefixed && part is TextPart text)
            {
                result.Add(new TextPart(prefix + text.Text));
                prefixed = true;
            }
            else
            {
                result.Add(part);
            }
        }

        if (!prefixed)
        {
            result.Insert(0, new TextPart(prefix + "发送了一条非文本消息。"));
        }

        return result;
    }

    private TextPart SessionContext(MessageEvent message, string selfName)
    {
        var identityTags = IdentityTags(message).TrimEnd();
        var context = message.IsDirect
            ? $"[会话上下文: 机器人={selfName}; 私聊; 用户={identityTags}{message.Sender.Name ?? DisplayUserId(message.Sender.Id)}/{DisplayUserId(message.Sender.Id)}]"
            : $"[会话上下文: 机器人={selfName}; 群={message.Channel.Name ?? message.Channel.Id}/{message.Channel.Id}; 用户={identityTags}{message.Member?.Nick ?? message.Sender.Name ?? DisplayUserId(message.Sender.Id)}/{DisplayUserId(message.Sender.Id)}]";
        return new TextPart(context);
    }

    /// <summary>
    /// 根据用户的 ChatMode 设置决定 ConversationKey：
    /// - single: 返回一个唯一 key（不会命中缓存，等效于无历史）
    /// - multi: 按用户独立
    /// - shared: 群聊时所有人共享同一个 key（userId 固定为 0）
    /// </summary>
    private string SourceInstance() => Context.InstanceId
        ?? throw new InvalidOperationException("AI conversation requires a source adapter instance.");
    private UserReference ScopedUser(string userId) => new(SourceInstance(), userId);

    private ConversationKey ResolveConversationKey(ChatScene scene, string peerId, string userId)
    {
        var settings = _userSettings.GetOrCreate(ScopedUser(userId));
        var mode = settings.ChatMode ?? _config.Default.DefaultChatMode;

        return mode switch
        {
            "single" => new ConversationKey(SourceInstance(), scene, peerId, Guid.NewGuid().ToString("N")),
            "shared" when scene == ChatScene.Group => new ConversationKey(SourceInstance(), scene, peerId, "0"),
            _ => new ConversationKey(SourceInstance(), scene, peerId, userId)
        };
    }

    private static string StripPrefix(string text, string prefix)
    {
        text = text.TrimStart();
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[prefix.Length..];
        }
        return text.Trim();
    }

    /// <summary>
    /// 从抽取出的 parts 列表中找到第一个 TextPart，去掉指令前缀（例如 "#ai"），
    /// 如果剩余文本为空则把这一段整个移除，避免把 "#ai" 字面量传给模型。
    /// </summary>
    private static void StripPrefixFromFirstTextPart(List<ChatPart> parts, string prefix)
    {
        var quoteDepth = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i] is not TextPart tp) continue;
            if (tp.Text.StartsWith("[引用消息 #", StringComparison.Ordinal)) { quoteDepth++; continue; }
            if (tp.Text.StartsWith("[引用结束", StringComparison.Ordinal)) { quoteDepth = Math.Max(0, quoteDepth - 1); continue; }
            if (quoteDepth > 0 || !tp.Text.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var stripped = StripPrefix(tp.Text, prefix);
            if (string.IsNullOrEmpty(stripped))
            {
                parts.RemoveAt(i);
            }
            else
            {
                parts[i] = new TextPart(stripped);
            }
            return;
        }
    }
}
