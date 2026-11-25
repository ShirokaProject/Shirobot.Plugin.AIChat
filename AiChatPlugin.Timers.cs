using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Tools.Timers;
using ShiroBot.AiChatPlugin.Usage;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;

namespace ShiroBot.AiChatPlugin;

/// <summary>定时提醒：#timer 列表和到点生成推送。</summary>
public sealed partial class AiChatPlugin
{
    private async Task HandleTimerListAsync(MessageEvent message)
    {
        if (!_config.ToolUse.Enabled || !_config.ToolUse.TimerTool.Enabled) return;
        var arg = StripPrefix(message.GetPlainText(), "#timer");
        var jobs = await _timerScheduler.ListAsync(message).ConfigureAwait(false);
        if (arg.StartsWith("cancel", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("取消", StringComparison.Ordinal))
        {
            var target = arg[(arg.StartsWith("取消", StringComparison.Ordinal) ? 2 : 6)..].Trim();
            var job = int.TryParse(target, out var index) && index >= 1 && index <= jobs.Count
                ? jobs[index - 1]
                : jobs.FirstOrDefault(j => j.Id == target);
            string reply;
            if (job is null) reply = "没有找到这个定时消息，用 #timer 查看序号。";
            else if (!job.IsOwnedBy(message.Sender.Id) && !Context.IsAdmin(message.Sender.Id)) reply = "只能取消自己创建或提醒自己的定时消息。";
            else
            {
                await _timerScheduler.RemoveAsync(message, j => j.Id == job.Id).ConfigureAwait(false);
                reply = $"已取消：{job.Text}";
            }
            await ReplyAsync(message, !message.IsDirect, reply).ConfigureAwait(false);
            return;
        }
        if (arg.StartsWith("clear", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("清空", StringComparison.Ordinal))
        {
            if (!Context.IsAdmin(message.Sender.Id))
            {
                await ReplyAsync(message, !message.IsDirect, "仅机器人所有者或管理员可以清空定时消息。").ConfigureAwait(false);
                return;
            }
            // 带 @ 时只清空这些成员创建或被提醒的任务，否则清空整个会话。
            var users = message.Segments.OfType<MentionSegment>().Where(m => m.UserId != message.SelfId).Select(m => m.UserId).ToHashSet();
            var removed = await _timerScheduler.RemoveAsync(message, j => users.Count == 0 || users.Any(j.IsOwnedBy)).ConfigureAwait(false);
            await ReplyAsync(message, !message.IsDirect, $"已清空 {removed.Count} 条定时消息。").ConfigureAwait(false);
            return;
        }
        var text = jobs.Count == 0 ? "当前会话没有待推送消息。" :
            "待推送消息：\n" +
            string.Join("\n", jobs.Take(50).Select((job, index) =>
                $"{index + 1}. {job.DueAt.ToLocalTime():yyyy-MM-dd HH:mm zzz} → " +
                (job.Targets is { Count: > 0 } ? $"{string.Join("、", job.Targets.Select(t => t.Name))}（{job.UserName} 预约）" : job.UserName) +
                (job.Cron is null ? "" : $"（重复 {job.Cron}" + (job.Until is { } until ? $"，至 {until.ToLocalTime():yyyy-MM-dd HH:mm}" : "") +
                    (job.RemainingRuns is { } left ? $"，剩 {left} 次" : "") + "）") + $"\n{job.Text}" +
                (job.Stopped ? $"\n{job.LastError ?? "发送失败"}，已停止。" : job.Attempts > 0 ? "\n发送失败，等待重试。" :
                    job.LastError is not null && job.Cron is not null ? $"\n上次：{job.LastError}" : ""))) +
            (jobs.Count > 50 ? $"\n共 {jobs.Count} 条，显示前 50 条。" : "") +
            (jobs.Count > 0 ? "\n#timer cancel <序号> 取消；管理员 #timer clear [@成员] 清空" : "");
        await ReplyAsync(message, !message.IsDirect, text).ConfigureAwait(false);
    }

    /// <summary>推送同一会话同一分钟到期的一批提醒：只调用一次模型，合并为一条消息并 @ 所有提醒对象。</summary>
    private async Task SendTimerAsync(IReadOnlyList<TimerJob> jobs, CancellationToken ct)
    {
        var job = jobs[0];
        using var scope = job.InstanceId is null ? null : Context.UseInstance(job.InstanceId);
        if (Context.Platform != job.Platform || job.InstanceId is not null && Context.InstanceId != job.InstanceId)
            throw new InvalidOperationException("定时任务的来源适配器当前不可用。");
        static string Describe(TimerJob j) =>
            j.Targets is { Count: > 0 } targets ? $"提醒 {string.Join("、", targets.Select(t => t.Name))}（{j.UserName} 预约）" : $"提醒 {j.UserName}";
        var fallback = jobs.Count == 1 ? job.Text : string.Join("\n", jobs.Select(j => $"{Describe(j)}：{j.Text}"));
        var request = jobs.Count == 1
            ? $"现在是 {DateTimeOffset.Now:O}。预约时间是 {job.DueAt:O}。" + (job.Targets is { Count: > 0 } ? Describe(job) + "。" : "") + $"请回复以下提醒内容：\n{job.Text}"
            : $"现在是 {DateTimeOffset.Now:O}。以下 {jobs.Count} 条提醒同时到期，请合并成一条简短的消息，分别说明各自的提醒内容：\n" +
              string.Join("\n", jobs.Select((j, i) => $"{i + 1}. {Describe(j)}：{j.Text}"));
        await _globalConcurrency.WaitAsync(ct).ConfigureAwait(false);
        string text;
        try
        {
            var model = ResolveModel(_config.Default.Model);
            if (model is null) text = fallback;
            else
            {
                try
                {
                    using var usageScope = UsageScope.Begin(UsageSource(job.InstanceId, job.ChannelType, job.ChannelId), "timer");
                    var useTools = _config.ToolUse.Enabled && _config.ToolUse.TimerTool.UseToolsOnDelivery && _tools.HasReadOnlyTools;
                    var systemPrompt = ApplyOwnerPrompt(_config.Default.DefaultPrompt.TrimEnd() +
                        (jobs.All(j => j.Cron is null) ? "\n你正在发送用户预约的定时提醒。" : "\n你正在发送用户预约的定时提醒，其中有重复提醒，内容要贴合当天的情况，不要提到这是重复任务。") +
                        "根据提醒内容给出简短回复，不要创建新的预约，不要使用markdown。被提醒的人会在消息开头被 @，正文不需要再写 @。");
                    if (useTools) systemPrompt += "\n" + _tools.ReadOnlySystemPrompt;
                    text = await ExecuteTextWithFallbackAsync(model, requestModel =>
                    {
                        var messages = new List<OpenAiMessage> {
                            new() { Role = "system", Content = systemPrompt },
                            new() { Role = "user", Content = request }
                        };
                        return useTools
                            ? _tools.ChatAsync(_openAi, requestModel, messages, _config.ToolUse.MaxRounds, ct: ct, readOnlyOnly: true, maxOutputTokens: _config.Default.OutputTokenLimit())
                            : _openAi.ChatAsync(_config.Default, requestModel,
                                new OpenAiChatRequest { Model = requestModel.Name, Messages = messages, MaxCompletionTokens = _config.Default.OutputTokenLimit() }, ct);
                    }, ct: ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    BotLog.Warning($"[AiChat] 定时消息生成失败，直接推送预约文本: {ex.Message}");
                    text = fallback;
                }
            }
        }
        finally { _globalConcurrency.Release(); }
        ct.ThrowIfCancellationRequested();
        BotLog.Info($"[AiChat] 定时推送 {jobs.Count} 条合并为 1 条消息:\n{ForLog(text)}");
        var channel = job.ChannelType == ChannelType.Group ? Channel.Group(job.ChannelId) : Channel.Direct(job.ChannelId);
        var segments = new List<MessageSegment>();
        if (job.ChannelType == ChannelType.Group)
        {
            foreach (var target in jobs.SelectMany(j => j.Recipients).DistinctBy(t => t.UserId))
                segments.Add(new MentionSegment(target.UserId) { DisplayName = target.Name });
            segments.Add(new TextSegment(" " + text));
        }
        else segments.Add(new TextSegment(text));
        var sent = await Context.Message.SendMessageAsync(channel, segments).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(sent.MessageId)) throw new InvalidOperationException("推送未返回消息 ID。");
    }
}
