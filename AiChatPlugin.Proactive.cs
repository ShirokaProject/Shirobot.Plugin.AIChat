using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.Proactive;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;

namespace ShiroBot.AiChatPlugin;

/// <summary>主动插话：攒消息、等安静窗口、交给模型判断后回复。</summary>
public sealed partial class AiChatPlugin
{
    /// <summary>先记进最近消息；白名单群里在频率限制允许时交给模型判断是否插话。</summary>
    private async Task HandleGroupChatterAsync(MessageEvent message)
    {
        var config = _config.Proactive;
        var groupKey = ProactiveKey(message);
        var name = (message.Member?.Nick ?? message.Sender.Name ?? DisplayUserId(message.Sender.Id)) + "(" + DisplayUserId(message.Sender.Id) + ")";
        var keep = Math.Max(_config.History.GroupContextMessages, Math.Max(config.ContextMessages, config.JudgeContextMessages));
        var at = message.Timestamp == default ? DateTimeOffset.UtcNow : message.Timestamp;
        _proactive.Record(groupKey, name, DescribeMessage(message), keep, at, message.MessageId, IdentityTags(message));
        if (!ProactiveTrigger.IsAllowedGroup(config, message.Channel.Id)) return;
        string[] botNames = [await ResolveSelfNameAsync(message).ConfigureAwait(false)];
        ScheduleProactive(groupKey, message, ProactiveTrigger.IsMentioned(message.GetPlainText(), config, botNames));
    }

    private static TimeSpan QuietWindow(ProactiveSection config)
    {
        var min = Math.Clamp(config.QuietMinSeconds, 0, 120) * 1000;
        var max = Math.Max(min, Math.Clamp(config.QuietMaxSeconds, 0, 120) * 1000);
        return TimeSpan.FromMilliseconds(Random.Shared.Next(min, max + 1));
    }

    /// <summary>
    /// 安静窗口：每来一条消息就把这个群的判断时间推迟一个随机窗口，群里安静下来后把这批新消息一起判断一次。
    /// 冷却未到时也在这里等，到时再判断最新的消息。
    /// </summary>
    private void ScheduleProactive(string groupKey, MessageEvent message, bool mentioned)
    {
        var due = DateTimeOffset.UtcNow + QuietWindow(_config.Proactive);
        lock (_backgroundGate)
        {
            if (_backgroundStop.IsCancellationRequested) return;
            if (_pendingProactive.TryGetValue(groupKey, out var pending))
            {
                AddToBatch(pending, message, mentioned);
                pending.Due = due;
                // 正在判断的消息的发送者还在继续说：打断这次请求，等他说完后连同新消息一起判断。
                if (pending.Judging is { } judging && !pending.Interrupted && judging.Any(m => m.Sender.Id == message.Sender.Id))
                {
                    pending.Interrupted = true;
                    pending.JudgingAbort?.Cancel();
                    BotLog.Info($"[AiChat] 群 {message.Channel.Id} 主动回复判断被 {message.Sender.Id} 的新消息打断，稍后一起判断。");
                }
                return;
            }
            pending = new PendingProactive(due);
            AddToBatch(pending, message, mentioned);
            _pendingProactive[groupKey] = pending;
            var token = _backgroundStop.Token;
            var task = Task.Run(() => RunProactiveWindowAsync(groupKey, token));
            _recallTasks.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_backgroundGate) _recallTasks.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static void AddToBatch(PendingProactive pending, MessageEvent message, bool mentioned)
    {
        pending.Batch.Add(message);
        if (mentioned) pending.Mentions.Add(message);
        TrimBatch(pending);
    }

    private static void TrimBatch(PendingProactive pending)
    {
        while (pending.Batch.Count > MaxProactiveBatch)
        {
            pending.Mentions.Remove(pending.Batch[0]);
            pending.Batch.RemoveAt(0);
        }
    }

    /// <summary>没有判断成的消息放回批次前面，和之后的新消息一起判断。</summary>
    private static void Requeue(PendingProactive pending, IEnumerable<MessageEvent> batch, IEnumerable<MessageEvent> mentions)
    {
        pending.Batch.InsertRange(0, batch);
        foreach (var m in mentions) if (pending.Batch.Contains(m)) pending.Mentions.Add(m);
        TrimBatch(pending);
    }

    private static bool SameMessage(MessageEvent message, MessageDeletedEvent deleted) =>
        message.MessageId == deleted.MessageId && message.InstanceId == deleted.InstanceId && message.Channel.Id == deleted.Channel.Id;

    /// <summary>
    /// 群消息被撤回：从最近消息记录和待判断的批次里去掉；正在判断的批次包含它时，在发出回复前取消这次判断，
    /// 剩下的消息重新等安静窗口再判断。主动回复不是对方主动发起的请求，不受“撤回取消”开关影响。
    /// </summary>
    private void ForgetRecalledGroupMessage(MessageDeletedEvent deleted)
    {
        var groupKey = $"{deleted.InstanceId}:{deleted.Channel.Id}";
        _proactive.Forget(groupKey, deleted.MessageId);
        lock (_backgroundGate)
        {
            if (!_pendingProactive.TryGetValue(groupKey, out var pending)) return;
            var removed = pending.Batch.RemoveAll(m => SameMessage(m, deleted));
            pending.Mentions.RemoveWhere(m => SameMessage(m, deleted));
            var judging = pending.Judging?.Any(m => SameMessage(m, deleted)) == true;
            if (judging && pending.RecalledWhileJudging.Add(deleted.MessageId)) pending.JudgingAbort?.Cancel();
            if (removed > 0 || judging)
                BotLog.Info($"[AiChat] 群 {deleted.Channel.Id} 有消息撤回，主动回复不再使用这条消息。");
        }
    }

    private async Task RunProactiveWindowAsync(string groupKey, CancellationToken token)
    {
        try
        {
            while (true)
            {
                MessageEvent[] batch;
                MessageEvent[] mentions;
                TimeSpan wait;
                using var abort = new CancellationTokenSource();
                lock (_backgroundGate)
                {
                    var pending = _pendingProactive[groupKey];
                    // 批次里的消息全被撤回了，没什么可判断的。
                    if (pending.Batch.Count == 0)
                    {
                        _pendingProactive.Remove(groupKey);
                        return;
                    }
                    batch = pending.Batch.ToArray();
                    mentions = pending.Mentions.ToArray();
                    wait = pending.Due - DateTimeOffset.UtcNow;
                    // 开始判断后再进来的消息另起一批。
                    if (wait <= TimeSpan.Zero)
                    {
                        pending.Batch.Clear();
                        pending.Mentions.Clear();
                        pending.Judging = batch;
                        pending.JudgingAbort = abort;
                        pending.RecalledWhileJudging.Clear();
                        pending.Interrupted = false;
                    }
                }
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, token).ConfigureAwait(false);
                    continue;
                }

                // 有人提到机器人时以最近那条为准。
                var mentioned = batch.LastOrDefault(mentions.Contains);
                ProactiveOutcome outcome = default;
                try
                {
                    if (ProactiveTrigger.IsAllowedGroup(_config.Proactive, batch[^1].Channel.Id))
                        outcome = await TryProactiveReplyAsync(batch, mentioned, abort.Token).ConfigureAwait(false);
                }
                finally
                {
                    lock (_backgroundGate)
                    {
                        var pending = _pendingProactive[groupKey];
                        pending.Judging = null;
                        pending.JudgingAbort = null;
                    }
                }

                lock (_backgroundGate)
                {
                    var pending = _pendingProactive[groupKey];
                    var recalled = pending.RecalledWhileJudging.ToHashSet();
                    pending.RecalledWhileJudging.Clear();
                    var remaining = batch.Where(m => !recalled.Contains(m.MessageId)).ToArray();
                    var interrupted = pending.Interrupted;
                    pending.Interrupted = false;
                    if ((recalled.Count > 0 || interrupted) && !outcome.ReplyStarted)
                    {
                        // 判断期间有消息被撤回或被同一个人的新消息打断、回复还没发出：去掉撤回的消息，
                        // 剩下的放回去，等安静窗口后和新消息一起判断（打断时新消息已经设好了安静窗口）。
                        Requeue(pending, remaining, mentions);
                        if (!interrupted)
                        {
                            var again = DateTimeOffset.UtcNow + QuietWindow(_config.Proactive);
                            if (pending.Due < again) pending.Due = again;
                        }
                        continue;
                    }
                    var latest = batch[^1];
                    var age = DateTimeOffset.UtcNow - (latest.Timestamp == default ? DateTimeOffset.UtcNow : latest.Timestamp);
                    if (outcome.RetryAfter is { } r && age + r <= MaxProactiveDeferral)
                    {
                        // 没有判断成：这批消息放回去，到时和之后的新消息一起判断。
                        Requeue(pending, remaining, mentions);
                        var retryAt = DateTimeOffset.UtcNow + r + TimeSpan.FromMilliseconds(500);
                        if (pending.Due < retryAt) pending.Due = retryAt;
                        continue;
                    }
                    // 判断期间又有新消息：它已经设好了新的安静窗口，继续等。
                    if (pending.Batch.Count > 0) continue;
                    _pendingProactive.Remove(groupKey);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { BotLog.Warning($"[AiChat] 主动回复判断失败: {ex.Message}"); }
        lock (_backgroundGate) _pendingProactive.Remove(groupKey);
    }

    /// <summary>判断一次是否主动回复；只因冷却或并发已满而没有判断时返回还需等待的时间。</summary>
    private async Task<ProactiveOutcome> TryProactiveReplyAsync(IReadOnlyList<MessageEvent> batch, MessageEvent? mentionedBy, CancellationToken abort)
    {
        var config = _config.Proactive;
        // 有人提到机器人时以那条为准，否则以最后一条为准；但这批新消息都会交给模型，任何一条都可以回应。
        var message = mentionedBy ?? batch[^1];
        var mentioned = mentionedBy is not null;
        var groupKey = ProactiveKey(message);
        // 正在处理的请求已占满并发时先不插话，优先保证被 @ 的回复，稍后再看。
        if (_globalConcurrency.CurrentCount == 0) return new(TimeSpan.FromSeconds(5), false);
        var decision = _proactive.Evaluate(groupKey, mentioned, config, DateTimeOffset.UtcNow);
        if (decision.Prompt is not { } prompt) return new(decision.RetryAfter, false);
        if (!_globalConcurrency.Wait(0))
        {
            _proactive.Restore(groupKey, decision);
            return new(TimeSpan.FromSeconds(5), false);
        }
        var holdsJudgeSlot = true;
        var request = (ProactiveRequest?)null;
        try
        {
            var selfName = await ResolveSelfNameAsync(message).ConfigureAwait(false);
            var fresh = string.Join('\n', batch.Select(m => FormatGroupLine(m, selfName)));
            var key = ResolveConversationKey(ChatScene.Group, PeerId(message), SenderId(message));
            if (!await JudgeProactiveAsync(message, batch, key, selfName, fresh, prompt, abort).ConfigureAwait(false))
                return default;
            abort.ThrowIfCancellationRequested();
            _globalConcurrency.Release();
            holdsJudgeSlot = false;
            var transcript = await GetRecentGroupMessagesAsync(batch[0], config.ContextMessages, selfName, recordedAfter: batch.Count, abort,
                after: ClearedAt(key)).ConfigureAwait(false);
            // 判断模型已经决定接话，正式回复不再按活跃度重新判断一次。
            request = new ProactiveRequest(groupKey, ProactiveTrigger.BuildContext(transcript, fresh, ProactiveTrigger.ReplyPrompt(config, mentioned)), abort)
                { Addressed = mentioned };
            if (abort.IsCancellationRequested) return default;
            await ProcessAiAsync(message, key, prefixToStrip: null, isGroup: true, invokedByMention: false, request).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 群 {message.Channel.Id} 主动回复判断失败，保持沉默: {ex.Message}");
        }
        finally
        {
            if (holdsJudgeSlot) _globalConcurrency.Release();
            // 回复发出前被取消不算发言，恢复冷却；否则冷却从这次判断结束算起。
            if (abort.IsCancellationRequested && request?.ReplyStarted != true) _proactive.Restore(groupKey, decision);
            else _proactive.MarkReplied(groupKey, DateTimeOffset.UtcNow);
        }
        return new(null, request?.ReplyStarted == true);
    }
}
