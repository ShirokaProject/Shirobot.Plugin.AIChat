using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Timers;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.SDK.Models;

internal static class TimerRepeatChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static async Task RunAsync()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        DateTimeOffset At(int month, int day, int hour, int minute) => new(2026, month, day, hour, minute, 0, TimeSpan.FromHours(8));

        var daily = CronSchedule.TryParse("0 8 * * *")!;
        Check(daily.Next(At(10, 24, 7, 59), zone) == At(10, 24, 8, 0) && daily.Next(At(10, 24, 8, 0), zone) == At(10, 25, 8, 0),
            "Daily cron must fire today if still ahead, otherwise tomorrow, strictly after the given time.");
        var weekdays = CronSchedule.TryParse("30 9 * * 1-5")!;
        Check(weekdays.Next(At(10, 24, 12, 0), zone) == At(10, 26, 9, 30), "Weekday cron must skip Saturday and Sunday (2026-10-24 is Saturday).");
        var monthly = CronSchedule.TryParse("0 9 1,15 * *")!;
        Check(monthly.Next(At(10, 24, 0, 0), zone) == At(11, 1, 9, 0), "Day-of-month list must pick the next listed day.");
        Check(CronSchedule.TryParse("0 9 8 3 *")!.Next(At(10, 24, 0, 0), zone) == new DateTimeOffset(2027, 3, 8, 9, 0, 0, TimeSpan.FromHours(8)),
            "Yearly date must roll over to next year.");
        Check(CronSchedule.TryParse("0 12 13 * 5")!.Next(At(10, 24, 0, 0), zone) == At(10, 30, 12, 0), "Day-of-month and weekday must combine with OR.");
        Check(CronSchedule.TryParse("0 0 * * 7")!.Next(At(10, 24, 1, 0), zone) == At(10, 25, 0, 0), "Weekday 7 must mean Sunday.");
        Check(CronSchedule.TryParse("*/15 * * * *")!.MinimumGap(At(10, 24, 0, 0)) == TimeSpan.FromMinutes(15), "Step syntax must expand correctly.");
        Check(CronSchedule.TryParse("0 0 30 2 *")!.Next(At(10, 24, 0, 0), zone) is null, "Impossible dates must never match.");
        foreach (var bad in new[] { "", "0 8 * *", "60 8 * * *", "0 24 * * *", "0 8 0 * *", "0 8 * 13 *", "0 8 * * 8", "a 8 * * *", "5-1 * * * *", "*/0 * * * *" })
            Check(CronSchedule.TryParse(bad) is null, $"Invalid cron '{bad}' must be rejected.");
        Console.WriteLine("PASS: cron daily/weekday/monthly/yearly, OR semantics, Sunday 7, steps, impossible dates and invalid syntax");

        var root = Path.Combine(Path.GetTempPath(), "aichat-repeat-" + Guid.NewGuid().ToString("N"));
        var message = new MessageEvent { Platform = "custom", InstanceId = "adapter-one", MessageId = "m1", Channel = Channel.Group("group"), Sender = new User("user") { Name = "用户" }, Segments = [] };
        var config = new TimerToolSection { Enabled = true, MaxPendingPerUser = 5, MinRepeatIntervalMinutes = 30, AllowRepeat = true };
        Check(!new TimerToolSection().AllowRepeat && new PluginConfig().Behaviour.ReplyStyle() == "answer", "Repeat reminders and reply modes must be off by default.");
        using (var basic = new TimerTool(null!, new TimerToolSection(), message))
        {
            var schema = JsonSerializer.Serialize(basic.Definition.Function.Parameters);
            Check(basic.SystemPrompt == TimerTool.Prompt && !schema.Contains("repeat") && schema.Contains("\"required\":[\"at\",\"text\"]"),
                "With repeat disabled the tool must keep the original one-time definition.");
            Check((await basic.ExecuteAsync("{\"repeat\":\"0 8 * * *\",\"text\":\"a\"}")).Contains("one-time") &&
                (await basic.ExecuteAsync("{\"action\":\"list\"}")).Contains("one-time"), "Disabled repeat must reject recurring and list/cancel calls.");
        }
        var sent = new List<TimerJob>();
        try
        {
            await using var scheduler = new TimerScheduler(root, () => true, (jobs, _) => { sent.AddRange(jobs); return Task.CompletedTask; });
            using (var tool = new TimerTool(scheduler, config, message))
            {
                Check((await tool.ExecuteAsync("{\"repeat\":\"*/5 * * * *\",\"text\":\"喝水\"}")).Contains("times per day"), "Too many runs per day must be rejected.");
                Check((await tool.ExecuteAsync("{\"repeat\":\"0,10 8 * * *\",\"text\":\"喝水\"}")).Contains("at least"), "Too short repeat intervals must be rejected.");
                Check((await tool.ExecuteAsync("{\"repeat\":\"0 8 * *\",\"text\":\"喝水\"}")).Contains("cron"), "Invalid cron must be rejected.");
                Check((await tool.ExecuteAsync("{\"text\":\"喝水\"}")).Contains("at is required"), "One-time reminders still need at.");
                var created = JsonDocument.Parse(await tool.ExecuteAsync("{\"repeat\":\"0 8 * * *\",\"count\":2,\"text\":\"吃早饭\"}")).RootElement;
                Check(created.GetProperty("repeat").GetString() == "0 8 * * *", "Recurring reminder must be created.");
                await tool.CommitAsync(default);
            }
            var job = (await scheduler.ListAsync(message)).Single();
            Check(job.Cron == "0 8 * * *" && job.RemainingRuns == 2 && job.DueAt.ToLocalTime().Hour == 8, "Recurring job must persist cron, count and first due time.");

            await scheduler.DispatchDueAsync(job.DueAt.AddSeconds(1), default);
            var next = (await scheduler.ListAsync(message)).Single();
            Check(sent.Count == 1 && next.DueAt == CronSchedule.TryParse("0 8 * * *")!.Next(job.DueAt) && next.RemainingRuns == 1,
                "After sending, a recurring job must move to its next occurrence.");
            // 停机错过：第二次已过去很久，不补发也不再保留（次数用完）。
            await scheduler.DispatchDueAsync(next.DueAt.AddHours(3), default);
            Check(sent.Count == 1 && (await scheduler.ListAsync(message)).Count == 0, "Missed occurrences must be skipped, and exhausted counts removed.");

            using (var tool = new TimerTool(scheduler, config, message))
            {
                var id = JsonDocument.Parse(await tool.ExecuteAsync("{\"repeat\":\"0 9 * * 1-5\",\"text\":\"上班打卡\"}")).RootElement.GetProperty("id").GetString()!;
                await tool.CommitAsync(default);
                using var other = new TimerTool(scheduler, config, message with { Sender = new User("someone") });
                Check((await other.ExecuteAsync($"{{\"action\":\"cancel\",\"id\":\"{id}\"}}")).Contains("not found"), "Users must not cancel other people's reminders.");
                using var owner = new TimerTool(scheduler, config, message with { MessageId = "m2" });
                Check((await owner.ExecuteAsync("{\"action\":\"list\"}")).Contains(id), "List must show the user's reminders.");
                Check((await owner.ExecuteAsync($"{{\"action\":\"cancel\",\"id\":\"{id}\"}}")).Contains("cancelled"), "Owner must be able to cancel.");
                Check((await scheduler.ListAsync(message)).Count == 1, "Cancel must wait for the reply to succeed.");
                await owner.CommitAsync(default);
                Check((await scheduler.ListAsync(message)).Count == 0, "Committed cancel must remove the reminder.");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: recurring reminders, minimum interval, count limit, missed-run skipping, owner-only staged cancel and listing");
    }
}
