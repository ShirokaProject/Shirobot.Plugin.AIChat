using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.Proactive;
using ShiroBot.AiChatPlugin.Usage;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;

namespace ShiroBot.AiChatPlugin;

public sealed partial class AiChatPlugin
{
    private async Task<bool> JudgeProactiveAsync(MessageEvent message, IReadOnlyList<MessageEvent> batch,
        ConversationKey key, string selfName, string fresh, string policy, CancellationToken ct)
    {
        var model = ResolveModel(_config.Default.Model) ?? throw new InvalidOperationException("未找到接话判断使用的默认模型。");
        var recent = await GetRecentGroupMessagesAsync(batch[0], Math.Clamp(_config.Proactive.JudgeContextMessages, 1, 20),
            selfName, recordedAfter: batch.Count, ct).ConfigureAwait(false);
        using var usageScope = UsageScope.Begin(UsageSource(key), "proactive_judge");
        var request = ProactiveJudge.Request(model.Name, selfName, _config.Proactive.Keywords, recent, fresh, policy);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        // An optional interjection is not worth retrying or holding up explicit requests.
        var response = await _openAi.ChatAsync(_config.Default, model, request, timeout.Token).ConfigureAwait(false);
        var reply = ProactiveJudge.ShouldReply(response);
        var result = reply ? "接话" : ProactiveJudge.IsExplicitSkip(response) ? "沉默" : "无效判定，保持沉默";
        var marker = response.ReplaceLineEndings(" ").Trim();
        BotLog.Info($"[AiChat] 群 {message.Channel.Id} 轻量接话判断: 机器人={Truncate(selfName, 64)}，新消息 {batch.Count} 条，输入 {request.Messages.Sum(m => (m.Content as string)?.Length ?? 0)} 字符，结果 {result}，模型输出={Truncate(marker, 80)}。");
        return reply;
    }
}
