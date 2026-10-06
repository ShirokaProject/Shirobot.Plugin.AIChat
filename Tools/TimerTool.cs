using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Timers;
using ShiroBot.SDK.Models;

namespace ShiroBot.AiChatPlugin.Tools;

/// <summary>待提交任务只属于本次请求，成功生成最终回复后统一持久化；撤回和失败不会遗留任务。</summary>
internal sealed class TimerTool(TimerScheduler scheduler, TimerToolSection config, MessageEvent message) : IChatTool
{
    private readonly List<TimerJob> _staged = [];
    public int MaxCallsPerTurn => 3;
    internal const string Prompt = "仅当用户明确要求定时提醒或预约消息时，使用 timer_tool 创建一次性定时消息。根据用户消息中的当前时间计算提醒时间，at 必须是带时区偏移的 ISO 8601 时间（如 2026-10-06T20:00:00+08:00）。不确定日期或时间时先询问用户，不要擅自预约。text 是到点后交给默认模型生成提醒的内容，只能向当前会话推送并提醒请求者，不允许指定其他群或用户。工具成功后才能确认预约。群聊 #timer 可以查看任务。";
    public string SystemPrompt => Prompt;
    public OpenAiTool Definition { get; } = new()
    {
        Function = new OpenAiToolFunction { Name = "timer_tool", Description = "Schedule a one-time reminder only when explicitly requested by the user. At the specified time the default model generates a reply based on text and mentions the requesting user in the same conversation.",
            Parameters = new { type = "object", additionalProperties = false, required = new[] { "at", "text" }, properties = new {
                at = new { type = "string", description = "Future ISO 8601 date-time including timezone offset, e.g. 2026-10-06T20:00:00+08:00." },
                text = new { type = "string", description = "The requested reminder or message content. Maximum 2000 characters." } } } }
    };
    public async Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var doc = JsonDocument.Parse(arguments);
        if (!doc.RootElement.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.String ||
            !doc.RootElement.TryGetProperty("text", out var textValue) || textValue.ValueKind != JsonValueKind.String)
            return "Invalid timer arguments: at and text are required strings.";
        var atText = at.GetString()!;
        if (!Regex.IsMatch(atText, @"T.*(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) ||
            !DateTimeOffset.TryParse(atText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var due))
            return "Invalid time: provide ISO 8601 including a timezone offset.";
        var now = DateTimeOffset.UtcNow;
        if (due <= now || due > now.AddDays(Math.Clamp(config.MaxDaysAhead, 1, 365)))
            return "Reminder must be in the future and within the configured scheduling range.";
        var text = textValue.GetString()!.Trim();
        if (text.Length is 0 or > 2000) return "Reminder text must contain 1 to 2000 characters.";
        // 同一消息的上游重试/回退不会重复预约相同任务。
        var identity = JsonSerializer.Serialize(new { message.InstanceId, message.Platform, message.Channel, message.MessageId, due = due.ToUniversalTime(), text });
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        if (_staged.Any(j => j.Id == id)) return JsonSerializer.Serialize(new { id, at = due, status = "scheduled" });
        var existing = await scheduler.ListAsync(message).ConfigureAwait(false);
        if (existing.Any(j => j.Id == id)) return JsonSerializer.Serialize(new { id, at = due, status = "scheduled" });
        if (existing.Count(j => j.UserId == message.Sender.Id) + _staged.Count >= Math.Clamp(config.MaxPendingPerUser, 1, 100))
            return "Pending reminder limit reached for this user in this conversation.";
        _staged.Add(new TimerJob(id, message.InstanceId, message.Platform, message.Channel.Type, message.Channel.Id,
            message.Sender.Id, message.Member?.DisplayName ?? message.Sender.Name ?? message.Sender.Id, message.MessageId, due, text));
        return JsonSerializer.Serialize(new { id, at = due, text, status = "scheduled" });
    }
    public void ResetAttempt() => _staged.Clear();
    public Task CommitAsync(CancellationToken ct) => scheduler.AddAsync(_staged, config, ct);
    public void Dispose() { }
}
