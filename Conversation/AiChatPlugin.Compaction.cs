using System.Text;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Tools.Memory;
using ShiroBot.AiChatPlugin.Usage;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin;

/// <summary>历史压缩：阈值判断、后台压缩、分块总结和退避。</summary>
public sealed partial class AiChatPlugin
{
    // -------- 工具方法 --------

    private const int DefaultCompactionTurns = 1000;
    private int CompactionTurnLimit => _config.History.MaxTurns <= 0 ? DefaultCompactionTurns : Math.Max(2, _config.History.MaxTurns);
    private int CompactionTokenBudget => Math.Max(1000, _config.History.MaxTokens);
    // 以下都由预算推算，不单独配置：压缩后保留至多 16K tokens / 32 轮原文（且不超过阈值的四分之一），
    // 每个压缩请求的原文至多 24K tokens（且不超过预算的四分之一），上游报超长时再自动减半。
    private int CompactKeepTokens => Math.Min(16_000, CompactionTokenBudget / 4);
    private int CompactKeepTurns => Math.Min(32, Math.Max(1, CompactionTurnLimit / 4));
    private int CompactChunkTokens => Math.Clamp(CompactionTokenBudget / 4, MinCompactChunkTokens, 24_000);

    /// <summary>
    /// Context compact：轮数达到压缩阈值的 80% 或历史估算 token 超过预算的 75% 时，
    /// 把较早的对话合并进会话的历史摘要，只保留最近的对话原文。
    /// 历史摘要每次在旧摘要基础上增量更新，不会对摘要反复再摘要。
    /// </summary>
    private bool NeedsCompaction(ConversationState convo) =>
        convo.Turns.Count >= CompactionTurnLimit * 8L / 5 ||
        convo.Turns.Sum(turn => (long)EstimateTurnTokens(turn)) > CompactionTokenBudget * 3L / 4;

    private int CountMessagesToCompact(IReadOnlyList<ChatTurn> messages)
    {
        var keepTokenLimit = CompactKeepTokens;
        var keepTurnLimit = CompactKeepTurns;
        var firstKept = messages.Count;
        var keptTurns = 0;
        long suffixTokens = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            suffixTokens += EstimateTurnTokens(message);
            if (message.Role != "user") continue;
            // A round starts at the user message and includes all subsequent replies and image notices.
            if (keptTurns > 0 && (keptTurns >= keepTurnLimit || suffixTokens > keepTokenLimit)) break;
            firstKept = i;
            keptTurns++;
        }
        return keptTurns == 0 ? Math.Max(0, messages.Count - 1) : firstKept;
    }

    private const int MinCompactChunkTokens = 1000;
    private static readonly TimeSpan[] CompactionBackoffSteps =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];
    private readonly Dictionary<ConversationKey, (int Failures, DateTimeOffset RetryAt)> _compactionBackoff = new();
    internal Func<DateTimeOffset> CompactionClock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>历史已超过压缩预算（不只是接近），再不压缩这次请求可能超出上下文。</summary>
    private bool NeedsUrgentCompaction(ConversationState convo) =>
        convo.Turns.Count >= CompactionTurnLimit * 2L ||
        convo.Turns.Sum(turn => (long)EstimateTurnTokens(turn)) > CompactionTokenBudget;

    /// <summary>
    /// 对话流程使用的压缩入口。压缩没能把历史降到阈值以下时按会话退避，冷却期内直接用现有历史回复，
    /// 历史和前缀都不变；成功后清除退避。对用户完全静默。
    /// </summary>
    private async Task<bool> CompactWithBackoffAsync(ConversationState convo, ModelEntry model, CancellationToken ct)
    {
        if (!NeedsCompaction(convo)) return false;
        lock (_compactionBackoff)
        {
            if (_compactionBackoff.TryGetValue(convo.Key, out var backoff) && CompactionClock() < backoff.RetryAt)
            {
                BotLog.Info($"[AiChat] 上次压缩未完成，{backoff.RetryAt.ToLocalTime():HH:mm:ss} 前跳过压缩，使用现有历史。");
                return false;
            }
        }
        var rewritten = await TryCompactConversationAsync(convo, model, ct).ConfigureAwait(false);
        lock (_compactionBackoff)
        {
            if (!NeedsCompaction(convo))
            {
                _compactionBackoff.Remove(convo.Key);
            }
            else
            {
                var failures = (_compactionBackoff.TryGetValue(convo.Key, out var previous) ? previous.Failures : 0) + 1;
                var delay = CompactionBackoffSteps[Math.Min(failures, CompactionBackoffSteps.Length) - 1];
                _compactionBackoff[convo.Key] = (failures, CompactionClock() + delay);
                BotLog.Warning($"[AiChat] 历史仍需压缩（连续 {failures} 次未完成），{delay.TotalMinutes:0} 分钟后再试。");
            }
        }
        return rewritten;
    }

    /// <summary>
    /// 把保留边界之前的原文分块并入历史摘要。保留边界在开始时确定一次，分块只限制每个压缩请求的大小，
    /// 最终保留的原文与一次压完相同；所有块在同一次调用里连续完成，聊天请求只会看到一次前缀变化。
    /// 每块完成后立即写盘，中途失败时已完成的部分不会丢失。返回会话文件是否已被重写。
    /// </summary>
    private async Task<bool> TryCompactConversationAsync(ConversationState convo, ModelEntry model, CancellationToken ct)
    {
        if (!NeedsCompaction(convo)) return false;
        var remaining = CountMessagesToCompact(convo.Turns);
        if (remaining == 0) return false;
        var (compacted, chunks) = await CompactLeadingTurnsAsync(convo, remaining, model, persistChunks: true, ct).ConfigureAwait(false);
        if (compacted < remaining) return compacted > 0;

        var keptTokens = convo.Turns.Sum(turn => (long)EstimateTurnTokens(turn));
        BotLog.Info($"[AiChat] Context compact 完成: {chunks} 块共 {compacted} 条消息并入历史摘要, 保留 {convo.Turns.Count} 条原文 (估算 {keptTokens} tokens), 摘要 {convo.Summary?.Length ?? 0} 字:\n{ForLog(convo.Summary ?? "")}");
        return true;
    }

    /// <summary>
    /// 把 convo 开头的 remaining 条原文分块并入摘要。persistChunks 为 true 时每块完成后写盘；
    /// 后台压缩在副本上运行，只在全部完成后由调用方一次写回。返回已并入的条数和块数，中途失败时停止。
    /// </summary>
    private async Task<(int Compacted, int Chunks)> CompactLeadingTurnsAsync(ConversationState convo, int remaining, ModelEntry model,
        bool persistChunks, CancellationToken ct)
    {
        using var usageScope = UsageScope.Begin(UsageSource(convo.Key), "compact");
        if (!string.IsNullOrWhiteSpace(_config.History.CompactModel))
        {
            if (ResolveModel(_config.History.CompactModel) is { } compactModel) model = compactModel;
            else BotLog.Warning($"[AiChat] 压缩模型 {_config.History.CompactModel} 未注册，改用当前对话模型 {model.Name}。");
        }

        var chunkTokens = CompactChunkTokens;
        var compacted = 0;
        var chunks = 0;
        while (remaining > 0)
        {
            var count = CountChunkMessages(convo.Turns, remaining, chunkTokens);
            try
            {
                await CompactChunkAsync(convo, count, model, chunkTokens, persistChunks, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && chunkTokens > MinCompactChunkTokens && IsContextLengthError(ex))
            {
                chunkTokens = Math.Max(MinCompactChunkTokens, chunkTokens / 2);
                BotLog.Warning($"[AiChat] Context compact 请求超出压缩模型上下文，分块缩小到 {chunkTokens} tokens 后重试: {ex.Message}");
                continue;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                BotLog.Warning(compacted == 0
                    ? $"[AiChat] Context compact 失败，保留原历史: {ex.Message}"
                    : $"[AiChat] Context compact 部分完成（{chunks} 块 {compacted} 条已并入摘要），剩余 {remaining} 条下次继续: {ex.Message}");
                return (compacted, chunks);
            }
            remaining -= count;
            compacted += count;
            chunks++;
        }
        return (compacted, chunks);
    }

    /// <summary>正在后台压缩的会话；同一会话同时只跑一个，紧急压缩遇到它也让路。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ConversationKey, Task> _backgroundCompactions = new();

    private void ScheduleBackgroundCompaction(ConversationKey key, ModelEntry model)
    {
        if (_backgroundStop.IsCancellationRequested) return;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_backgroundCompactions.TryAdd(key, started.Task)) return;
        _ = Task.Run(async () =>
        {
            try { await RunBackgroundCompactionAsync(key, model, _backgroundStop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_backgroundStop.IsCancellationRequested) { }
            catch (Exception ex) { BotLog.Warning($"[AiChat] 后台压缩异常: {ex.Message}"); }
            finally
            {
                _backgroundCompactions.TryRemove(key, out _);
                started.TrySetResult();
            }
        });
    }

    /// <summary>
    /// 后台压缩：先在会话锁里取出要压缩的开头原文，释放锁后在副本上分块总结，最后再拿锁检查开头没被改动
    /// （期间没有 #clear 或其他压缩），把新摘要和压缩后仍在的原文（含期间新增的对话）一次写回。
    /// 期间的聊天请求照常追加在旧前缀后面，缓存不受影响；写回只让前缀变化一次，和同步压缩相同。
    /// </summary>
    private async Task RunBackgroundCompactionAsync(ConversationKey key, ModelEntry model, CancellationToken ct)
    {
        ConversationState work;
        List<string> original;
        string? baseSummary;
        var convo = await _conversations.AcquireAsync(key, ct).ConfigureAwait(false);
        try
        {
            if (!NeedsCompaction(convo) || InCompactionBackoff(key)) return;
            var count = CountMessagesToCompact(convo.Turns);
            if (count == 0) return;
            baseSummary = convo.Summary;
            original = convo.Turns.Take(count).Select(_conversations.Fingerprint).ToList();
            work = new ConversationState(key) { Summary = convo.Summary, MemorySnapshot = convo.MemorySnapshot };
            work.Turns.AddRange(convo.Turns.Take(count));
        }
        finally { _conversations.Release(key); }

        BotLog.Info($"[AiChat] 后台压缩开始: {original.Count} 条原文并入历史摘要。");
        var (compacted, chunks) = await CompactLeadingTurnsAsync(work, original.Count, model, persistChunks: false, ct).ConfigureAwait(false);
        if (compacted == 0)
        {
            RecordCompactionResult(key, success: false);
            return;
        }

        var current = await _conversations.AcquireAsync(key, ct).ConfigureAwait(false);
        try
        {
            if (current.Summary != baseSummary || current.Turns.Count < compacted ||
                !current.Turns.Take(compacted).Select(_conversations.Fingerprint).SequenceEqual(original.Take(compacted)))
            {
                BotLog.Info("[AiChat] 后台压缩期间历史已被清空或改写，放弃本次结果。");
                return;
            }
            var rewritten = new ConversationState(key) { Summary = work.Summary, MemorySnapshot = work.MemorySnapshot };
            rewritten.Turns.AddRange(current.Turns.Skip(compacted));
            await _conversations.RewriteAsync(rewritten, throwOnFailure: true).ConfigureAwait(false);
            RecordCompactionResult(key, success: !NeedsCompaction(rewritten));
            var keptTokens = rewritten.Turns.Sum(turn => (long)EstimateTurnTokens(turn));
            BotLog.Info($"[AiChat] 后台压缩完成: {chunks} 块共 {compacted} 条消息并入历史摘要, 保留 {rewritten.Turns.Count} 条原文 (估算 {keptTokens} tokens), 摘要 {rewritten.Summary?.Length ?? 0} 字:\n{ForLog(rewritten.Summary ?? "")}");
        }
        finally { _conversations.Release(key); }
    }

    private bool InCompactionBackoff(ConversationKey key)
    {
        lock (_compactionBackoff)
            return _compactionBackoff.TryGetValue(key, out var backoff) && CompactionClock() < backoff.RetryAt;
    }

    private void RecordCompactionResult(ConversationKey key, bool success)
    {
        lock (_compactionBackoff)
        {
            if (success)
            {
                _compactionBackoff.Remove(key);
                return;
            }
            var failures = (_compactionBackoff.TryGetValue(key, out var previous) ? previous.Failures : 0) + 1;
            var delay = CompactionBackoffSteps[Math.Min(failures, CompactionBackoffSteps.Length) - 1];
            _compactionBackoff[key] = (failures, CompactionClock() + delay);
            BotLog.Warning($"[AiChat] 历史仍需压缩（连续 {failures} 次未完成），{delay.TotalMinutes:0} 分钟后再试。");
        }
    }

    /// <summary>从最早的原文开始取一块，优先在一轮开始处切开；单条超长时独占一块（转写时截断）。</summary>
    private static int CountChunkMessages(IReadOnlyList<ChatTurn> turns, int limit, int chunkTokens)
    {
        long tokens = 0;
        var lastBoundary = 0;
        for (var i = 0; i < limit; i++)
        {
            if (i > 0 && turns[i].Role == "user") lastBoundary = i;
            tokens += TranscriptTokens(turns[i], chunkTokens);
            if (i > 0 && tokens > chunkTokens) return lastBoundary > 0 ? lastBoundary : i;
        }
        return limit;
    }

    private static int TranscriptTokens(ChatTurn turn, int chunkTokens) =>
        Math.Min(EstimateTokens(TurnToTranscript(turn)), chunkTokens) + 4;

    private static string ChunkTranscript(ChatTurn turn, int chunkTokens)
    {
        var text = TurnToTranscript(turn);
        return EstimateTokens(text) <= chunkTokens ? text : Truncate(text, chunkTokens) + "…（过长，已截断）";
    }

    /// <summary>上游因请求过长拒绝：413，或带有上下文/长度提示的 400/422。</summary>
    internal static bool IsContextLengthError(Exception ex)
    {
        if (ex is not UpstreamHttpException http) return false;
        if (http.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge) return true;
        if ((int)http.StatusCode is not (400 or 422)) return false;
        string[] hints = ["context", "too long", "too large", "maximum", "exceed", "length", "上下文", "长度", "过长", "超出"];
        return hints.Any(hint => http.Message.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把最早的 compactCount 条原文并入摘要并写盘；失败时抛出，内存和磁盘上的历史都保持不变。</summary>
    private async Task CompactChunkAsync(ConversationState convo, int compactCount, ModelEntry model, int chunkTokens, bool persist, CancellationToken ct)
    {
        var transcript = new StringBuilder();
        foreach (var turn in convo.Turns.Take(compactCount))
            transcript.Append(turn.Role == "user" ? "用户: " : "机器人: ").AppendLine(ChunkTranscript(turn, chunkTokens));

        // 被压缩的原文在这之后就只剩摘要，顺带把其中的长期信息补进记忆本，不依赖聊天模型主动调用工具。
        var memoryEnabled = _config.ToolUse.Enabled && _config.ToolUse.Memory.Enabled;
        var maxMemoryChars = Math.Clamp(_config.ToolUse.Memory.MaxEntryChars, 20, 1000);
        List<MemoryEntry>? notes = null;
        if (memoryEnabled)
        {
            try { notes = await _memory.LoadAsync(convo.Key).ConfigureAwait(false); }
            catch (Exception ex) { BotLog.Warning($"[AiChat] 压缩时读取记忆本失败，本次不提取记忆: {ex.Message}"); }
        }

        var input = new StringBuilder();
        input.AppendLine("【已有历史摘要】").AppendLine(string.IsNullOrWhiteSpace(convo.Summary) ? "（暂无）" : convo.Summary)
            .AppendLine().AppendLine("【新增对话】").Append(transcript);
        if (notes is not null)
            input.AppendLine().AppendLine("【现有记忆本】").AppendLine(notes.Count == 0 ? "（空）" : MemoryNotebookStore.Format(notes))
                .AppendLine().Append(CompactMemoryInstruction(maxMemoryChars));
        else
            input.AppendLine().Append("请输出合并后的完整历史摘要。");
        var summaryMessages = new List<OpenAiMessage>
        {
            new()
            {
                Role = "system",
                Content = string.IsNullOrWhiteSpace(_config.History.CompactPrompt)
                    ? HistorySection.DefaultCompactPrompt
                    : _config.History.CompactPrompt
            },
            new() { Role = "user", Content = input.ToString() }
        };

        var outputLimit = notes is null ? 2048 : 3072;
        var response = (await ExecuteTextWithFallbackAsync(model, requestModel => _openAi.ChatAsync(
            _config.Default, requestModel, new OpenAiChatRequest
            {
                Model = requestModel.Name,
                Messages = summaryMessages,
                MaxCompletionTokens = _config.Default.OutputTokenLimit() is { } limit ? Math.Min(limit, outputLimit) : outputLimit
            }, ct), ct, retryTransientHttpErrors: true).ConfigureAwait(false)).Trim();
        var (summary, memoryJson) = notes is null ? (response, null) : SplitCompactResponse(response);
        if (summary.Length == 0) throw new InvalidOperationException("摘要为空");
        ct.ThrowIfCancellationRequested();

        // 记忆本先于会话文件写入：会话重写失败时，补记的条目会作为记忆本更新出现，不会丢失。
        var snapshot = convo.MemorySnapshot;
        if (memoryEnabled)
        {
            try
            {
                if (memoryJson is not null)
                {
                    var applied = "";
                    await _memory.UpdateAsync(convo.Key, entries =>
                    {
                        var result = ApplyCompactMemory(entries, memoryJson, maxMemoryChars,
                            Math.Clamp(_config.ToolUse.Memory.MaxEntries, 1, 200), DateTimeOffset.UtcNow);
                        applied = result.Log;
                        return (result.Changed, applied);
                    }).ConfigureAwait(false);
                    if (applied.Length > 0) BotLog.Info($"[AiChat] Context compact 补记记忆本: {applied}");
                }
                else if (notes is not null)
                {
                    BotLog.Warning("[AiChat] Context compact 未返回可解析的记忆本修改，本次不补记。");
                }
                snapshot = await _memory.LoadAsync(convo.Key).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                BotLog.Warning($"[AiChat] Context compact 补记记忆本失败: {ex.Message}");
            }
        }

        // Persist first; if writing fails keep the original in-memory history
        // so AppendTurnsAsync can still save the current pair.
        var rewritten = new ConversationState(convo.Key) { Summary = summary, MemorySnapshot = snapshot };
        rewritten.Turns.AddRange(convo.Turns.Skip(compactCount));
        if (persist) await _conversations.RewriteAsync(rewritten, throwOnFailure: true).ConfigureAwait(false);
        convo.Turns.RemoveRange(0, compactCount);
        convo.Summary = summary;
        convo.MemorySnapshot = snapshot;
        BotLog.Info($"[AiChat] Context compact 分块: {compactCount} 条消息并入历史摘要, 剩余 {convo.Turns.Count} 条原文");
    }

    private static string CompactMemoryInstruction(int maxChars) =>
        "请输出合并后的完整历史摘要。另外，从【新增对话】中找出以后对话仍然有用的长期信息（某人的称呼、身份、偏好、约定、重要事实），" +
        "与【现有记忆本】对比：新信息放进 add；已有条目需要修正或补充时放进 update 并沿用原 id。" +
        $"不要重复已有内容，不要记录寒暄、一次性问题和已经过时的信息；每条一句话、不超过 {maxChars} 字，群聊中写清是谁的信息。" +
        "严格按以下格式输出：\n<summary>\n合并后的完整历史摘要\n</summary>\n<memory>\n" +
        "{\"add\":[\"条目内容\"],\"update\":[{\"id\":\"m1\",\"text\":\"修改后的内容\"}]}\n</memory>\n" +
        "没有需要记录的内容时 memory 输出 {\"add\":[],\"update\":[]}。";

    /// <summary>拆出 &lt;summary&gt; 和 &lt;memory&gt;；模型没按格式输出时，去掉记忆块后的全文作为摘要。</summary>
    internal static (string Summary, string? MemoryJson) SplitCompactResponse(string response)
    {
        var memory = ExtractTag(response, "memory", out var rest);
        var summary = ExtractTag(rest, "summary", out _) ?? rest;
        return (summary.Trim(), memory);
    }

    private static string? ExtractTag(string text, string tag, out string rest)
    {
        var open = $"<{tag}>";
        var close = $"</{tag}>";
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            rest = text;
            return null;
        }
        var contentStart = start + open.Length;
        var end = text.IndexOf(close, contentStart, StringComparison.Ordinal);
        rest = text[..start] + (end < 0 ? "" : text[(end + close.Length)..]);
        return (end < 0 ? text[contentStart..] : text[contentStart..end]).Trim();
    }

    private const int MaxCompactMemoryAdds = 10;

    /// <summary>
    /// 应用压缩时提取的记忆：只做新增和修改，删除留给模型工具和每日整理。
    /// 重复内容、超出条目上限和不存在的 id 都会被跳过；格式错误时不修改。
    /// </summary>
    internal static (bool Changed, string Log) ApplyCompactMemory(List<MemoryEntry> entries, string json, int maxChars, int maxEntries, DateTimeOffset now)
    {
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start) return (false, "");
        var log = new List<string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return (false, "");
            string? Clean(string? text)
            {
                text = text?.Trim();
                if (string.IsNullOrEmpty(text)) return null;
                return text.Length > maxChars ? text[..maxChars] : text;
            }

            if (root.TryGetProperty("update", out var updates) && updates.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var item in updates.EnumerateArray())
                {
                    if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    var id = item.TryGetProperty("id", out var idValue) && idValue.ValueKind == System.Text.Json.JsonValueKind.String
                        ? idValue.GetString()!.TrimStart('#') : null;
                    var text = Clean(item.TryGetProperty("text", out var textValue) && textValue.ValueKind == System.Text.Json.JsonValueKind.String
                        ? textValue.GetString() : null);
                    var index = id is null ? -1 : entries.FindIndex(e => e.Id == id);
                    if (index < 0 || text is null || entries[index].Text == text) continue;
                    entries[index] = entries[index] with { Text = text, UpdatedAt = now };
                    log.Add($"update #{id} {text}");
                }

            if (root.TryGetProperty("add", out var adds) && adds.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var added = 0;
                foreach (var item in adds.EnumerateArray())
                {
                    var text = Clean(item.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => item.GetString(),
                        System.Text.Json.JsonValueKind.Object when item.TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String => t.GetString(),
                        _ => null
                    });
                    if (text is null || entries.Any(e => e.Text == text)) continue;
                    if (entries.Count >= maxEntries || added >= MaxCompactMemoryAdds)
                    {
                        log.Add($"skip (记忆本已满或本次新增过多) {text}");
                        continue;
                    }
                    var id = MemoryNotebookStore.NextId(entries);
                    entries.Add(new MemoryEntry(id, text, now));
                    added++;
                    log.Add($"add #{id} {text}");
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return (false, "");
        }
        return (log.Any(line => !line.StartsWith("skip", StringComparison.Ordinal)), string.Join("; ", log));
    }
}
