using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        "text 是到点后交给默认模型生成提醒的内容，只能向当前会话推送并提醒请求者，不允许指定其他群或用户。用户要求取消或修改提醒时，先用 action=list 查看自己的任务，再用 action=cancel 和 id 取消，或用 action=update 和 id 修改（只填要改的字段，其余保持不变）。工具成功后才能确认预约、修改或取消。群聊 #timer 可以查看任务。";
    internal const string MentionPrompt = "例外：用户要求提醒当前消息中 @ 到的其他群成员时，把这些成员的昵称填入 mention，到点会 @ 他们而不是请求者；只能提醒当前消息里 @ 到的人。";
    private bool MentionEnabled => config.AllowMentionOthers && message.Channel.Type == ChannelType.Group;
    public string SystemPrompt => (config.AllowRepeat ? RepeatPrompt : Prompt) + (MentionEnabled ? MentionPrompt : "");
    public OpenAiTool Definition => !MentionEnabled ? config.AllowRepeat ? RepeatDefinition : OneTimeDefinition
        : WithMention(config.AllowRepeat ? RepeatDefinition : OneTimeDefinition);

    private static OpenAiTool WithMention(OpenAiTool definition)
    {
        var parameters = JsonNode.Parse(PluginJson.Serialize(definition.Function.Parameters))!.AsObject();
        parameters["properties"]!["mention"] = JsonNode.Parse("""
            {"type":"array","items":{"type":"string"},"description":"Display names of group members @-mentioned in the user's current message who should be reminded instead of the requester."}
            """);
        return new OpenAiTool { Function = new OpenAiToolFunction
            { Name = definition.Function.Name, Description = definition.Function.Description, Parameters = parameters } };
    }
    private static readonly OpenAiTool OneTimeDefinition = new()
    {
        Function = new OpenAiToolFunction { Name = "timer_tool", Description = "Schedule a one-time reminder only when explicitly requested by the user. At the specified time the default model generates a reply based on text and mentions the requesting user in the same conversation.",
            Parameters = new { type = "object", additionalProperties = false, required = new[] { "at", "text" }, properties = new {
                at = new { type = "string", description = "Future ISO 8601 date-time including timezone offset, e.g. 2026-10-06T20:00:00+08:00." },
                text = new { type = "string", description = "The requested reminder or message content. Maximum 2000 characters." } } } }
    };
    private static readonly OpenAiTool RepeatDefinition = new()
    {
        Function = new OpenAiToolFunction { Name = "timer_tool", Description = "Create, list, update or cancel reminders for the requesting user in this conversation, only when explicitly requested. At each due time the default model generates a reply based on text and mentions the requesting user.",
            Parameters = new { type = "object", additionalProperties = false, properties = new {
                action = new { type = "string", @enum = new[] { "create", "list", "update", "cancel" }, description = "Defaults to create." },
                at = new { type = "string", description = "One-time: future ISO 8601 date-time including timezone offset. Recurring: optional earliest start." },
                repeat = new { type = "string", description = "Optional 5-field cron expression (minute hour day month weekday) in the user's local time zone for recurring reminders." },
                until = new { type = "string", description = "Optional ISO 8601 date-time with offset after which a recurring reminder stops." },
                count = new { type = "integer", description = "Optional total number of deliveries for a recurring reminder." },
                text = new { type = "string", description = "The requested reminder or message content. Maximum 2000 characters." },
                id = new { type = "string", description = "Reminder id to update or cancel." } } } }
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
                .Where(j => j.IsOwnedBy(message.Sender.Id) && !_cancels.Contains(j.Id)).Concat(_staged);
            return PluginJson.Serialize(jobs.Select(j => new { id = j.Id, next = j.DueAt, repeat = j.Cron, until = j.Until, remaining = j.RemainingRuns,
                remind = j.Recipients.Select(t => t.Name).ToArray(), text = j.Text }).ToArray());
        }
        if (action == "cancel")
        {
            var id = Get(root, "id");
            if (string.IsNullOrEmpty(id)) return "id is required for cancel.";
            if (_staged.RemoveAll(j => j.Id == id) > 0) return PluginJson.Serialize(new { id, status = "cancelled" });
            var existing = await scheduler.ListAsync(message).ConfigureAwait(false);
            // 模型只能取消请求者创建或提醒请求者的任务。
            if (!existing.Any(j => j.Id == id && j.IsOwnedBy(message.Sender.Id))) return $"Reminder {id} not found for this user.";
            _cancels.Add(id);
            return PluginJson.Serialize(new { id, status = "cancelled" });
        }
        if (action == "update") return await UpdateAsync(root).ConfigureAwait(false);
        if (action != "create") return "action must be create, list, update or cancel.";
        return await CreateAsync(root).ConfigureAwait(false);
    }

    /// <summary>修改 = 取消旧任务 + 按合并后的参数新建；没给的字段沿用旧值，失败时旧任务保持不变。</summary>
    private async Task<string> UpdateAsync(JsonElement root)
    {
        var id = Get(root, "id");
        if (string.IsNullOrEmpty(id)) return "id is required for update.";
        var staged = _staged.FirstOrDefault(j => j.Id == id);
        var old = staged ?? (await scheduler.ListAsync(message).ConfigureAwait(false))
            .FirstOrDefault(j => j.Id == id && j.IsOwnedBy(message.Sender.Id) && !_cancels.Contains(j.Id));
        if (old is null) return $"Reminder {id} not found for this user.";
        var merged = JsonNode.Parse(root.GetRawText())!.AsObject();
        merged.Remove("action");
        merged.Remove("id");
        merged["text"] ??= old.Text;
        if (!merged.ContainsKey("repeat") && old.Cron is not null) merged["repeat"] = old.Cron;
        var recurring = !string.IsNullOrWhiteSpace(merged["repeat"]?.GetValue<string>());
        if (recurring && old.Cron is not null)
        {
            // 重复提醒改规则时沿用原来的截止时间和剩余次数。
            if (!merged.ContainsKey("until") && old.Until is { } until) merged["until"] = until.ToString("O");
            if (!merged.ContainsKey("count") && old.RemainingRuns is { } remaining) merged["count"] = remaining;
        }
        else if (!recurring && old.Cron is null && !merged.ContainsKey("at"))
            merged["at"] = old.DueAt.ToString("O");
        using var mergedDoc = JsonDocument.Parse(merged.ToJsonString());

        if (staged is not null) _staged.Remove(staged);
        else _cancels.Add(id);
        var before = _staged.Count;
        var result = await CreateAsync(mergedDoc.RootElement, old.Targets).ConfigureAwait(false);
        // 已有任务的 id 是十六进制串，可以直接拼进结果。
        if (_staged.Count > before) return $"{{\"replaces\":\"{id}\",{result[1..]}";
        // 新参数无效或与旧任务完全相同，恢复旧任务。
        if (staged is not null) _staged.Add(staged);
        else _cancels.Remove(id);
        return result;
    }

    private async Task<string> CreateAsync(JsonElement root, IReadOnlyList<TimerTarget>? keepTargets = null)
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
            var maxRuns = Math.Clamp(config.MaxRunsPerDay, 1, 48);
            if (cron.RunsPerDay > maxRuns) return $"Recurring reminders may fire at most {maxRuns} times per day.";
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
            // 没有截止时间或截止时间过远时，按配置的最长天数自动结束。
            var latest = first.AddDays(Math.Clamp(config.MaxRecurringDays, 1, 3650));
            if (until is null || until > latest) until = latest;
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
        var active = existing.Where(j => !j.Stopped && !_cancels.Contains(j.Id)).ToList();
        if (active.Count + _staged.Count >= Math.Clamp(config.MaxPendingPerConversation, 1, 1000))
            return "Pending reminder limit reached for this conversation. Ask the user to cancel some reminders first.";
        var mine = active.Where(j => j.UserId == message.Sender.Id).Concat(_staged).ToList();
        if (mine.Count >= Math.Clamp(config.MaxPendingPerUser, 1, 100))
            return "Pending reminder limit reached for this user in this conversation.";
        if (cronText is not null && mine.Count(j => j.Cron is not null) >= Math.Clamp(config.MaxRecurringPerUser, 0, 100))
            return "Recurring reminder limit reached for this user. Cancel an existing recurring reminder first.";
        var (targets, targetError) = ResolveTargets(root);
        if (targetError is not null) return targetError;
        if (!root.TryGetProperty("mention", out _)) targets ??= keepTargets?.ToList();
        _staged.Add(new TimerJob(id, message.InstanceId, message.Platform, message.Channel.Type, message.Channel.Id,
            message.Sender.Id, message.Member?.DisplayName ?? message.Sender.Name ?? message.Sender.Id, message.MessageId, due, text,
            Cron: cronText, Until: until, RemainingRuns: count, Targets: targets));
        return PluginJson.Serialize(new { id, at = due, repeat = cronText, until, count, text,
            remind = targets?.Select(t => t.Name).ToArray(), status = "scheduled" });
    }

    /// <summary>只接受当前消息中 @ 到的成员（按昵称或 ID 匹配），不能提醒机器人自己。</summary>
    private (List<TimerTarget>? Targets, string? Error) ResolveTargets(JsonElement root)
    {
        if (!root.TryGetProperty("mention", out var mention) || mention.ValueKind == JsonValueKind.Null) return (null, null);
        if (!MentionEnabled) return (null, "Reminding other members is not enabled.");
        if (mention.ValueKind != JsonValueKind.Array) return (null, "mention must be an array of names.");
        var mentioned = message.Segments.OfType<MentionSegment>().Where(m => m.UserId != message.SelfId).ToList();
        var targets = new List<TimerTarget>();
        foreach (var item in mention.EnumerateArray())
        {
            var name = item.ValueKind == JsonValueKind.String ? item.GetString()!.Trim().TrimStart('@') : "";
            var match = mentioned.FirstOrDefault(m => m.UserId == name || string.Equals(m.DisplayName, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return (null, $"\"{name}\" was not @-mentioned in the current message. Mentioned members: " +
                    (mentioned.Count == 0 ? "none" : string.Join(", ", mentioned.Select(m => m.DisplayName ?? m.UserId))) + ".");
            if (targets.All(t => t.UserId != match.UserId)) targets.Add(new TimerTarget(match.UserId, match.DisplayName ?? match.UserId));
        }
        return targets.Count == 0 ? (null, null) : (targets, null);
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
            await scheduler.RemoveAsync(message, j => _cancels.Contains(j.Id) && j.IsOwnedBy(message.Sender.Id), ct).ConfigureAwait(false);
        await scheduler.AddAsync(_staged, config, ct).ConfigureAwait(false);
    }
    public void Dispose() { }
}
