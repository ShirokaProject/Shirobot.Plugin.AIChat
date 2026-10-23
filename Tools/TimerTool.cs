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
    private readonly HashSet<string> _cancels = [];
    public int MaxCallsPerTurn => 3;
    /// <summary>未开启重复提醒时沿用原来的一次性工具定义，参数少，小模型也能稳定调用。</summary>
    internal const string Prompt = "仅当用户明确要求定时提醒或预约消息时，使用 timer_tool 创建一次性定时消息。根据用户消息中的当前时间计算提醒时间，at 必须是带时区偏移的 ISO 8601 时间（如 2026-10-06T20:00:00+08:00）。不确定日期或时间时先询问用户，不要擅自预约。text 是到点后交给默认模型生成提醒的内容，只能向当前会话推送并提醒请求者，不允许指定其他群或用户。工具成功后才能确认预约。群聊 #timer 可以查看任务。";
    internal const string RepeatPrompt = "仅当用户明确要求定时提醒或预约消息时，使用 timer_tool 创建定时消息。一次性提醒用 at，必须是带时区偏移的 ISO 8601 时间（如 2026-10-06T20:00:00+08:00），根据用户消息中的当前时间计算。" +
        "重复提醒用 repeat 填 5 段 cron 表达式（分 时 日 月 周，按用户消息时间的时区），例如每天 8 点 \"0 8 * * *\"、工作日 9 点半 \"30 9 * * 1-5\"、每月 1 号和 15 号 \"0 9 1,15 * *\"、每年 3 月 8 日 \"0 9 8 3 *\"；" +
        "重复提醒可同时给 at 表示最早开始时间，用户说了截止日期或次数时填 until 或 count。几个互不相关的特定日期分别创建一次性提醒。不确定日期或时间时先询问用户，不要擅自预约。" +
        "text 是到点后交给默认模型生成提醒的内容，只能向当前会话推送并提醒请求者，不允许指定其他群或用户。用户要求取消提醒时，先用 action=list 查看自己的任务，再用 action=cancel 和 id 取消。工具成功后才能确认预约或取消。群聊 #timer 可以查看任务。";
    public string SystemPrompt => config.AllowRepeat ? RepeatPrompt : Prompt;
    public OpenAiTool Definition { get; } = config.AllowRepeat ? RepeatDefinition : OneTimeDefinition;
    private static readonly OpenAiTool OneTimeDefinition = new()
    {
        Function = new OpenAiToolFunction { Name = "timer_tool", Description = "Schedule a one-time reminder only when explicitly requested by the user. At the specified time the default model generates a reply based on text and mentions the requesting user in the same conversation.",
            Parameters = new { type = "object", additionalProperties = false, required = new[] { "at", "text" }, properties = new {
                at = new { type = "string", description = "Future ISO 8601 date-time including timezone offset, e.g. 2026-10-06T20:00:00+08:00." },
                text = new { type = "string", description = "The requested reminder or message content. Maximum 2000 characters." } } } }
    };
    private static readonly OpenAiTool RepeatDefinition = new()
    {
        Function = new OpenAiToolFunction { Name = "timer_tool", Description = "Create, list or cancel reminders for the requesting user in this conversation, only when explicitly requested. At each due time the default model generates a reply based on text and mentions the requesting user.",
            Parameters = new { type = "object", additionalProperties = false, properties = new {
                action = new { type = "string", @enum = new[] { "create", "list", "cancel" }, description = "Defaults to create." },
                at = new { type = "string", description = "One-time: future ISO 8601 date-time including timezone offset. Recurring: optional earliest start." },
                repeat = new { type = "string", description = "Optional 5-field cron expression (minute hour day month weekday) in the user's local time zone for recurring reminders." },
                until = new { type = "string", description = "Optional ISO 8601 date-time with offset after which a recurring reminder stops." },
                count = new { type = "integer", description = "Optional total number of deliveries for a recurring reminder." },
                text = new { type = "string", description = "The requested reminder or message content. Maximum 2000 characters." },
                id = new { type = "string", description = "Reminder id to cancel." } } } }
    };
    public async Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var doc = JsonDocument.Parse(arguments);
        var root = doc.RootElement;
        var action = Get(root, "action") ?? "create";
        if (!config.AllowRepeat && (action != "create" || root.TryGetProperty("repeat", out _)))
            return "Only one-time reminders with at and text are supported.";
        if (action == "list")
        {
            var jobs = (await scheduler.ListAsync(message).ConfigureAwait(false))
                .Where(j => j.UserId == message.Sender.Id && !_cancels.Contains(j.Id)).Concat(_staged);
            return PluginJson.Serialize(jobs.Select(j => new { id = j.Id, next = j.DueAt, repeat = j.Cron, until = j.Until, remaining = j.RemainingRuns, text = j.Text }).ToArray());
        }
        if (action == "cancel")
        {
            var id = Get(root, "id");
            if (string.IsNullOrEmpty(id)) return "id is required for cancel.";
            if (_staged.RemoveAll(j => j.Id == id) > 0) return PluginJson.Serialize(new { id, status = "cancelled" });
            var existing = await scheduler.ListAsync(message).ConfigureAwait(false);
            // 模型只能取消请求者本人的任务。
            if (!existing.Any(j => j.Id == id && j.UserId == message.Sender.Id)) return $"Reminder {id} not found for this user.";
            _cancels.Add(id);
            return PluginJson.Serialize(new { id, status = "cancelled" });
        }
        if (action != "create") return "action must be create, list or cancel.";
        return await CreateAsync(root).ConfigureAwait(false);
    }

    private async Task<string> CreateAsync(JsonElement root)
    {
        var text = Get(root, "text")?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 2000) return "Reminder text must contain 1 to 2000 characters.";
        var now = DateTimeOffset.UtcNow;
        var maxDue = now.AddDays(Math.Clamp(config.MaxDaysAhead, 1, 365));
        var atText = Get(root, "at");
        DateTimeOffset? at = null;
        if (atText is not null)
        {
            if (!TryParseTime(atText, out var parsed)) return "Invalid time: provide ISO 8601 including a timezone offset.";
            at = parsed;
        }
        var repeat = Get(root, "repeat");
        string? cronText = null;
        DateTimeOffset? until = null;
        int? count = null;
        DateTimeOffset due;
        if (string.IsNullOrWhiteSpace(repeat))
        {
            if (at is null) return "Invalid timer arguments: at is required for one-time reminders.";
            due = at.Value;
        }
        else
        {
            if (CronSchedule.TryParse(repeat) is not { } cron) return "Invalid repeat: provide a 5-field cron expression (minute hour day month weekday).";
            var start = at is { } a && a > now ? a.AddMinutes(-1) : now;
            if (cron.Next(start) is not { } first) return "The repeat expression never matches a date.";
            var minGap = TimeSpan.FromMinutes(Math.Clamp(config.MinRepeatIntervalMinutes, 5, 1440));
            if (cron.MinimumGap(start) < minGap) return $"Recurring reminders must be at least {minGap.TotalMinutes} minutes apart.";
            if (Get(root, "until") is { } untilText)
            {
                if (!TryParseTime(untilText, out var parsedUntil)) return "Invalid until: provide ISO 8601 including a timezone offset.";
                if (parsedUntil < first) return "until is earlier than the first reminder.";
                until = parsedUntil;
            }
            if (root.TryGetProperty("count", out var countValue))
            {
                if (countValue.ValueKind != JsonValueKind.Number || !countValue.TryGetInt32(out var parsedCount) || parsedCount < 1)
                    return "count must be a positive integer.";
                count = parsedCount;
            }
            cronText = cron.Expression;
            due = first;
        }
        if (due <= now || due > maxDue)
            return "Reminder must be in the future and within the configured scheduling range.";
        // 同一消息的上游重试/回退不会重复预约相同任务。
        var identity = PluginJson.Serialize(new { message.InstanceId, message.Platform, message.Channel, message.MessageId, due = due.ToUniversalTime(), text, cronText });
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        if (_staged.Any(j => j.Id == id)) return PluginJson.Serialize(new { id, at = due, status = "scheduled" });
        var existing = await scheduler.ListAsync(message).ConfigureAwait(false);
        if (existing.Any(j => j.Id == id)) return PluginJson.Serialize(new { id, at = due, status = "scheduled" });
        if (existing.Count(j => j.UserId == message.Sender.Id && !_cancels.Contains(j.Id)) + _staged.Count >= Math.Clamp(config.MaxPendingPerUser, 1, 100))
            return "Pending reminder limit reached for this user in this conversation.";
        _staged.Add(new TimerJob(id, message.InstanceId, message.Platform, message.Channel.Type, message.Channel.Id,
            message.Sender.Id, message.Member?.DisplayName ?? message.Sender.Name ?? message.Sender.Id, message.MessageId, due, text,
            Cron: cronText, Until: until, RemainingRuns: count));
        return PluginJson.Serialize(new { id, at = due, repeat = cronText, until, count, text, status = "scheduled" });
    }

    private static bool TryParseTime(string text, out DateTimeOffset value)
    {
        value = default;
        return Regex.IsMatch(text, @"T.*(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    private static string? Get(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public void ResetAttempt()
    {
        _staged.Clear();
        _cancels.Clear();
    }
    public async Task CommitAsync(CancellationToken ct)
    {
        if (_cancels.Count > 0)
            await scheduler.RemoveAsync(message, j => _cancels.Contains(j.Id) && j.UserId == message.Sender.Id, ct).ConfigureAwait(false);
        await scheduler.AddAsync(_staged, config, ct).ConfigureAwait(false);
    }
    public void Dispose() { }
}
