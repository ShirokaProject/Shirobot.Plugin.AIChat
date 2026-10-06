using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Timers;

internal sealed record TimerJob(string Id, string? InstanceId, string Platform, ChannelType ChannelType,
    string ChannelId, string UserId, string UserName, string SourceMessageId, DateTimeOffset DueAt,
    string Text, int Attempts = 0, DateTimeOffset? RetryAt = null, string? LastError = null);

/// <summary>单个后台循环领取到期任务；修改队列时先持久化，发送成功后删除。</summary>
internal sealed class TimerScheduler : IAsyncDisposable
{
    private readonly string _path;
    private readonly Func<bool> _enabled;
    private readonly Func<TimerJob, CancellationToken, Task> _send;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private List<TimerJob> _jobs;
    private Task? _worker;
    public TimerScheduler(string pluginRoot, Func<bool> enabled, Func<TimerJob, CancellationToken, Task> send)
    {
        _path = Path.Combine(pluginRoot, "data", "timers.json");
        _enabled = enabled; _send = send;
        // 文件损坏时保留原文件并报错，避免悄悄覆盖用户任务。
        _jobs = File.Exists(_path) ? JsonSerializer.Deserialize<List<TimerJob>>(File.ReadAllText(_path)) ?? [] : [];
    }
    public void Start() { _worker ??= Task.Run(RunAsync); }
    internal static bool SameChannel(TimerJob job, MessageEvent message) =>
        job.InstanceId == message.InstanceId && job.Platform == message.Platform &&
        job.ChannelType == message.Channel.Type && job.ChannelId == message.Channel.Id;

    public async Task AddAsync(IReadOnlyList<TimerJob> jobs, TimerToolSection config, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var additions = jobs.Where(j => !_jobs.Any(existing => existing.Id == j.Id)).ToList();
            foreach (var group in additions.GroupBy(j => (j.InstanceId, j.Platform, j.ChannelType, j.ChannelId, j.UserId)))
            {
                var count = _jobs.Count(j => (j.InstanceId, j.Platform, j.ChannelType, j.ChannelId, j.UserId) == group.Key);
                if (count + group.Count() > Math.Clamp(config.MaxPendingPerUser, 1, 100))
                    throw new InvalidOperationException("该用户的待推送任务已达到上限。");
            }
            if (additions.Count == 0) return;
            var updated = _jobs.Concat(additions).ToList();
            await SaveAsync(updated, ct).ConfigureAwait(false);
            _jobs = updated;
            foreach (var job in additions)
                BotLog.Info($"[AiChat] ToolUse timer_tool 任务已保存 id={job.Id} due_at={job.DueAt:O}");
        }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<TimerJob>> ListAsync(MessageEvent message)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return _jobs.Where(j => SameChannel(j, message)).OrderBy(j => j.DueAt).ToArray(); }
        finally { _gate.Release(); }
    }
    private async Task SaveAsync(List<TimerJob> jobs, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(jobs), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        File.Move(temporary, _path, overwrite: true);
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
        try { due = _jobs.Where(j => j.Attempts < 3 && (j.RetryAt ?? j.DueAt) <= now).OrderBy(j => j.DueAt).ToArray(); }
        finally { _gate.Release(); }
        foreach (var job in due)
        {
            ct.ThrowIfCancellationRequested();
            if (!_enabled()) return;
            string? error = null;
            try
            {
                BotLog.Info($"[AiChat] ToolUse timer_tool 到期推送开始 id={job.Id} attempt={job.Attempts + 1}");
                await _send(job, ct).ConfigureAwait(false);
                BotLog.Info($"[AiChat] ToolUse timer_tool 到期推送完成 id={job.Id}");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                error = ex.Message;
                BotLog.Warning($"[AiChat] 定时消息 {job.Id} 发送失败: {error}");
            }
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var updated = _jobs.Where(j => j.Id != job.Id).ToList();
                if (error is not null)
                    updated.Add(job with { Attempts = job.Attempts + 1, RetryAt = now.AddMinutes(1), LastError = error });
                await SaveAsync(updated, ct).ConfigureAwait(false);
                _jobs = updated;
            }
            finally { _gate.Release(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_worker is not null) await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }
}
