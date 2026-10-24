using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Timers;

/// <summary>到点需要 @ 的群成员；为空时提醒创建者本人。</summary>
internal sealed record TimerTarget(string UserId, string Name);

internal sealed record TimerJob(string Id, string? InstanceId, string Platform, ChannelType ChannelType,
    string ChannelId, string UserId, string UserName, string SourceMessageId, DateTimeOffset DueAt,
    string Text, int Attempts = 0, DateTimeOffset? RetryAt = null, string? LastError = null,
    string? Cron = null, DateTimeOffset? Until = null, int? RemainingRuns = null,
    IReadOnlyList<TimerTarget>? Targets = null)
{
    /// <summary>重复任务本次之后的下一次时间；一次性任务、超过截止时间或次数用完时返回 null。</summary>
    public TimerJob? NextOccurrence(DateTimeOffset after)
    {
        if (CronSchedule.TryParse(Cron) is not { } cron) return null;
        if (RemainingRuns is <= 1) return null;
        var next = cron.Next(after > DueAt ? after : DueAt);
        if (next is null || next > Until) return null;
        return this with { DueAt = next.Value, Attempts = 0, RetryAt = null, RemainingRuns = RemainingRuns - 1 };
    }

    /// <summary>到点实际 @ 的成员。</summary>
    public IReadOnlyList<TimerTarget> Recipients => Targets is { Count: > 0 } ? Targets : [new TimerTarget(UserId, UserName)];

    /// <summary>创建者或被提醒的成员。</summary>
    public bool IsOwnedBy(string userId) => UserId == userId || Targets?.Any(t => t.UserId == userId) == true;

    /// <summary>已停止自动处理，只保留在列表中供查看。</summary>
    public bool Stopped => Attempts >= 3;
}

/// <summary>某会话某天已推送的次数，持久化后重启不清零。</summary>
internal sealed record TimerDeliveryCount(string? InstanceId, string Platform, ChannelType ChannelType, string ChannelId, DateOnly Date, int Count);

/// <summary>
/// 单个后台循环领取到期任务；修改队列时先持久化，发送成功后删除或排到下一次。
/// 同一会话同一分钟到期的任务合并为一次推送。
/// </summary>
internal sealed class TimerScheduler : IAsyncDisposable
{
    private readonly string _path;
    private readonly string _countsPath;
    private readonly Func<TimeSpan> _missedGrace;
    private readonly Func<bool> _enabled;
    private readonly Func<IReadOnlyList<TimerJob>, CancellationToken, Task> _send;
    private readonly Func<int> _maxDeliveriesPerDay;
    private readonly Dictionary<(string?, string, ChannelType, string, DateOnly), int> _deliveredToday = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private List<TimerJob> _jobs;
    private Task? _worker;
    public TimerScheduler(string pluginRoot, Func<bool> enabled, Func<IReadOnlyList<TimerJob>, CancellationToken, Task> send,
        Func<int>? maxDeliveriesPerDay = null, Func<TimeSpan>? missedGrace = null)
    {
        _path = Path.Combine(pluginRoot, "data", "timers.json");
        _countsPath = Path.Combine(pluginRoot, "data", "timer-deliveries.json");
        _enabled = enabled; _send = send;
        _maxDeliveriesPerDay = maxDeliveriesPerDay ?? (() => int.MaxValue);
        _missedGrace = missedGrace ?? (() => TimeSpan.FromHours(1));
        // 文件损坏时保留原文件并报错，避免悄悄覆盖用户任务。
        _jobs = File.Exists(_path) ? PluginJson.Deserialize<List<TimerJob>>(File.ReadAllText(_path)) ?? [] : [];
        // 计数只用于限流，损坏时从零开始即可。
        try
        {
            if (File.Exists(_countsPath))
                foreach (var c in PluginJson.Deserialize<List<TimerDeliveryCount>>(File.ReadAllText(_countsPath)) ?? [])
                    _deliveredToday[(c.InstanceId, c.Platform, c.ChannelType, c.ChannelId, c.Date)] = c.Count;
        }
        catch (Exception ex) { BotLog.Warning($"[AiChat] 读取定时推送计数失败，从零开始: {ex.Message}"); }
    }
    public void Start() { _worker ??= Task.Run(RunAsync); }
    internal static bool SameChannel(TimerJob job, MessageEvent message) =>
        job.InstanceId == message.InstanceId && job.Platform == message.Platform &&
        job.ChannelType == message.Channel.Type && job.ChannelId == message.Channel.Id;
    private static (string?, string, ChannelType, string) ChannelOf(TimerJob job) =>
        (job.InstanceId, job.Platform, job.ChannelType, job.ChannelId);

    /// <summary>保存新任务。上限以已保存的任务为准，已停止的任务不占名额。</summary>
    public async Task AddAsync(IReadOnlyList<TimerJob> jobs, TimerToolSection config, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var additions = jobs.Where(j => !_jobs.Any(existing => existing.Id == j.Id)).ToList();
            if (additions.Count == 0) return;
            var active = _jobs.Where(j => !j.Stopped).ToList();
            foreach (var group in additions.GroupBy(ChannelOf))
            {
                if (active.Count(j => ChannelOf(j) == group.Key) + group.Count() > Math.Clamp(config.MaxPendingPerConversation, 1, 1000))
                    throw new InvalidOperationException("当前会话的待推送任务已达到上限。");
                foreach (var user in group.GroupBy(j => j.UserId))
                {
                    var existing = active.Where(j => ChannelOf(j) == group.Key && j.UserId == user.Key).ToList();
                    if (existing.Count + user.Count() > Math.Clamp(config.MaxPendingPerUser, 1, 100))
                        throw new InvalidOperationException("该用户的待推送任务已达到上限。");
                    if (existing.Count(j => j.Cron is not null) + user.Count(j => j.Cron is not null) > Math.Clamp(config.MaxRecurringPerUser, 0, 100))
                        throw new InvalidOperationException("该用户的重复提醒已达到上限。");
                }
            }
            var updated = _jobs.Concat(additions).ToList();
            await SaveAsync(updated, ct).ConfigureAwait(false);
            _jobs = updated;
            foreach (var job in additions)
                BotLog.Info($"[AiChat] ToolUse timer_tool 任务已保存 id={job.Id} due_at={job.DueAt:O}" + (job.Cron is null ? "" : $" cron=\"{job.Cron}\" until={job.Until:O}"));
        }
        finally { _gate.Release(); }
    }
    /// <summary>删除当前会话中满足条件的任务，返回被删除的任务。</summary>
    public async Task<IReadOnlyList<TimerJob>> RemoveAsync(MessageEvent message, Func<TimerJob, bool> match, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var removed = _jobs.Where(j => SameChannel(j, message) && match(j)).ToList();
            if (removed.Count == 0) return removed;
            var updated = _jobs.Except(removed).ToList();
            await SaveAsync(updated, ct).ConfigureAwait(false);
            _jobs = updated;
            foreach (var job in removed) BotLog.Info($"[AiChat] ToolUse timer_tool 任务已取消 id={job.Id}");
            return removed;
        }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<TimerJob>> ListAsync(MessageEvent message)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return _jobs.Where(j => SameChannel(j, message)).OrderBy(j => j.DueAt).ToArray(); }
        finally { _gate.Release(); }
    }
    private Task SaveAsync(List<TimerJob> jobs, CancellationToken ct) => WriteAtomicAsync(_path, PluginJson.Serialize(jobs), ct);

    private async Task SaveCountsAsync(DateOnly today)
    {
        foreach (var key in _deliveredToday.Keys.Where(k => k.Item5 != today).ToList()) _deliveredToday.Remove(key);
        var counts = _deliveredToday.Select(p => new TimerDeliveryCount(p.Key.Item1, p.Key.Item2, p.Key.Item3, p.Key.Item4, p.Key.Item5, p.Value)).ToList();
        try { await WriteAtomicAsync(_countsPath, PluginJson.Serialize(counts), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { BotLog.Warning($"[AiChat] 保存定时推送计数失败: {ex.Message}"); }
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, content, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        File.Move(temporary, path, overwrite: true);
    }
    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try { await DispatchDueAsync(DateTimeOffset.UtcNow, _stop.Token).ConfigureAwait(false); }
                catch (Exception ex) when (!_stop.IsCancellationRequested) { BotLog.Warning($"[AiChat] 定时推送处理失败: {ex.Message}"); }
                await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    internal async Task DispatchDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!_enabled()) return;
        TimerJob[] due;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { due = _jobs.Where(j => !j.Stopped && (j.RetryAt ?? j.DueAt) <= now).OrderBy(j => j.DueAt).ToArray(); }
        finally { _gate.Release(); }
        var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        foreach (var batch in due.GroupBy(j => (Channel: ChannelOf(j), Minute: (j.RetryAt ?? j.DueAt).ToUnixTimeSeconds() / 60)))
        {
            ct.ThrowIfCancellationRequested();
            if (!_enabled()) return;
            // 停机或暂停期间错过的时间点不补发：重复任务排到下一次，一次性任务超过补发时限后标记为已错过。
            var missed = batch.Where(j => j.Attempts == 0 && now - j.DueAt > (j.Cron is null ? _missedGrace() : TimeSpan.FromMinutes(10))).ToList();
            foreach (var job in missed)
                BotLog.Info($"[AiChat] ToolUse timer_tool 任务错过 {job.DueAt:O}，" + (job.Cron is null ? "不再补发" : "跳到下一次") + $" id={job.Id}");
            var jobs = batch.Except(missed).ToList();
            var (instance, platform, type, channel) = batch.Key.Channel;
            var dayKey = (instance, platform, type, channel, today);
            string? error = null;
            string? skipped = null;
            if (jobs.Count > 0 && _deliveredToday.GetValueOrDefault(dayKey) >= Math.Max(1, _maxDeliveriesPerDay()))
            {
                skipped = "当日推送已达上限，已跳过";
                BotLog.Warning($"[AiChat] 会话 {type}:{channel} 今日定时推送已达上限，跳过 {jobs.Count} 条: {string.Join(", ", jobs.Select(j => j.Id))}");
            }
            else if (jobs.Count > 0)
            {
                var ids = string.Join(", ", jobs.Select(j => j.Id));
                try
                {
                    BotLog.Info($"[AiChat] ToolUse timer_tool 到期推送开始 id={ids} attempt={jobs.Max(j => j.Attempts) + 1}");
                    await _send(jobs, ct).ConfigureAwait(false);
                    _deliveredToday[dayKey] = _deliveredToday.GetValueOrDefault(dayKey) + 1;
                    await SaveCountsAsync(today).ConfigureAwait(false);
                    BotLog.Info($"[AiChat] ToolUse timer_tool 到期推送完成 id={ids}");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    error = ex.Message;
                    BotLog.Warning($"[AiChat] 定时消息 {ids} 发送失败: {error}");
                }
            }
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var updated = _jobs.ToList();
                foreach (var job in batch)
                {
                    // 期间任务可能已被取消，此时不再放回队列。
                    var index = updated.FindIndex(j => j.Id == job.Id);
                    if (index < 0) continue;
                    updated.RemoveAt(index);
                    var wasMissed = missed.Contains(job);
                    var failure = wasMissed ? null : error;
                    if (failure is not null && job.Attempts + 1 < 3)
                        updated.Add(job with { Attempts = job.Attempts + 1, RetryAt = now.AddMinutes(1), LastError = failure });
                    else if (job.NextOccurrence(now) is { } next)
                        updated.Add(next with { LastError = failure ?? skipped ?? job.LastError });
                    else if (wasMissed && job.Cron is null)
                        updated.Add(job with { Attempts = 3, RetryAt = null, LastError = "停机或暂停期间错过，未补发" });
                    else if (failure is not null || skipped is not null && !wasMissed)
                        updated.Add(job with { Attempts = 3, RetryAt = null, LastError = failure ?? skipped });
                }
                await SaveAsync(updated, ct).ConfigureAwait(false);
                _jobs = updated;
            }
            finally { _gate.Release(); }
        }
        // 只保留当天的计数。
        if (_deliveredToday.Keys.Any(k => k.Item5 != today)) await SaveCountsAsync(today).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_worker is not null) await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }
}
