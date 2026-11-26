using ShiroBot.AiChatPlugin.Delivery;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Usage;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;

namespace ShiroBot.AiChatPlugin;

public sealed partial class AiChatPlugin
{
    private async Task HandleQqNudgeAsync(PlatformEvent evt)
    {
        if (!_config.Behaviour.ReplyToNudge ||
            !string.Equals(evt.Platform, "qq", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(evt.InstanceId))
            return;

        var selfId = (evt.Raw as QEventPayload)?.SelfId ?? evt.SelfId;
        var channel = evt.Raw switch
        {
            QGroupNudge group when !string.IsNullOrWhiteSpace(selfId) && group.ReceiverId == selfId &&
                !string.IsNullOrWhiteSpace(group.SenderId) && group.SenderId != selfId => Channel.Group(group.GroupId),
            QFriendNudge { IsSelfReceive: true, IsSelfSend: false } friend when friend.UserId != selfId =>
                Channel.Direct(friend.UserId),
            _ => null
        };
        if (channel is null || string.IsNullOrWhiteSpace(channel.Id)) return;

        var target = new ChannelReference(evt.InstanceId, channel);
        var ct = Context.Lifetime.Stopping;
        try
        {
            await _globalConcurrency.WaitAsync(ct).ConfigureAwait(false);
            string text;
            try
            {
                if (!_config.Behaviour.ReplyToNudge) return;
                text = await GenerateNudgeReplyAsync(target, ct).ConfigureAwait(false);
            }
            finally { _globalConcurrency.Release(); }

            ct.ThrowIfCancellationRequested();
            if (_config.Behaviour.ReplyToNudge)
                await Context.Message.SendMessageAsync(target, [new TextSegment(text)], ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 戳一戳回复发送失败: {ex.Message}");
        }
    }

    private async Task<string> GenerateNudgeReplyAsync(ChannelReference target, CancellationToken ct)
    {
        const string fallback = "我在，怎么啦？";
        var model = ResolveModel(_config.Default.Model);
        if (model is null) return fallback;

        var interaction = target.Channel.Type == ChannelType.Group
            ? "刚才有一位 QQ 群友戳了你。"
            : "刚才和你私聊的 QQ 用户戳了你。";
        var messages = new List<OpenAiMessage>
        {
            new() { Role = "system", Content = _config.Default.DefaultPrompt.TrimEnd() +
                "\n你收到了 QQ 戳一戳事件。用一句简短自然的话回应戳你的人，直接输出纯文本正文。" },
            new() { Role = "user", Content = interaction }
        };
        try
        {
            using var usageScope = UsageScope.Begin(UsageSource(target.InstanceId, target.Channel.Type, target.Channel.Id), "nudge");
            var text = await ExecuteTextWithFallbackAsync(model, async requestModel =>
            {
                var reply = await _openAi.ChatAsync(_config.Default, requestModel, new OpenAiChatRequest
                {
                    Model = requestModel.Name, Messages = messages, MaxCompletionTokens = _config.Default.OutputTokenLimit()
                }, ct).ConfigureAwait(false);
                if (UpstreamRefusal.Is(reply)) throw new UpstreamRefusalException(reply);
                return reply;
            }, ct).ConfigureAwait(false);
            return PlainTextFormat.Strip(text).ReplaceLineEndings("\n")
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? fallback;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BotLog.Warning($"[AiChat] 戳一戳回复生成失败，使用默认回复: {ex.GetType().Name}");
            return fallback;
        }
    }
}
