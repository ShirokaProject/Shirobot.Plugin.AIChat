using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Delivery;
using ShiroBot.AiChatPlugin.Timers;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.SDK.Models;

internal static class TimerUpdateChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aichat-timer-update-" + Guid.NewGuid().ToString("N"));
        var group = new MessageEvent { Platform = "custom", InstanceId = "adapter-one", SelfId = "bot", MessageId = "m1", Channel = Channel.Group("group"),
            Sender = new User("alice") { Name = "Alice" }, Segments = [] };
        var config = new TimerToolSection { AllowRepeat = true };
        var at = DateTimeOffset.UtcNow.AddHours(3);
        at = new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, at.Offset);
        var sent = new List<TimerJob>();
        try
        {
            await using (var scheduler = new TimerScheduler(root, () => true, (jobs, _) => { sent.AddRange(jobs); return Task.CompletedTask; }))
            {
                using (var tool = new TimerTool(scheduler, config, group))
                {
                    await tool.ExecuteAsync(JsonSerializer.Serialize(new { at = at.ToString("O"), text = "开会" }));
                    await tool.ExecuteAsync("{\"repeat\":\"0 8 * * *\",\"text\":\"吃早饭\",\"count\":5}");
                    await tool.CommitAsync(default);
                }
                var jobs = await scheduler.ListAsync(group);
                var once = jobs.Single(j => j.Cron is null);
                var daily = jobs.Single(j => j.Cron is not null);

                using (var tool = new TimerTool(scheduler, config, group with { MessageId = "m2" }))
                {
                    var moved = await tool.ExecuteAsync(JsonSerializer.Serialize(new { action = "update", id = once.Id, at = at.AddHours(1).ToString("O") }));
                    Check(moved.Contains($"\"replaces\":\"{once.Id}\""), "Updating must report the replaced id.");
                    var renamed = await tool.ExecuteAsync(JsonSerializer.Serialize(new { action = "update", id = daily.Id, text = "吃午饭", repeat = "0 12 * * *" }));
                    Check(renamed.Contains("0 12 * * *") && renamed.Contains("\"count\":5"), "Updating a recurring reminder must keep fields that were not given.");
                    Check((await tool.ExecuteAsync("{\"action\":\"update\",\"id\":\"missing\",\"text\":\"x\"}")).Contains("not found"), "Unknown ids must be rejected.");
                    await tool.CommitAsync(default);
                }
                jobs = await scheduler.ListAsync(group);
                Check(jobs.Count == 2 && jobs.All(j => j.Id != once.Id && j.Id != daily.Id), "Updated reminders must replace the old ones.");
                Check(jobs.Single(j => j.Cron is null) is { Text: "开会" } moved1 && moved1.DueAt == at.AddHours(1) && jobs.Single(j => j.Cron is not null).Text == "吃午饭",
                    "Updated fields must be stored.");

                var current = jobs.Single(j => j.Cron is null);
                using (var tool = new TimerTool(scheduler, config, group with { MessageId = "m3" }))
                {
                    Check((await tool.ExecuteAsync(JsonSerializer.Serialize(new { action = "update", id = current.Id, at = "明天" }))).Contains("Invalid time"),
                        "Invalid updates must report the error.");
                    await tool.CommitAsync(default);
                }
                Check((await scheduler.ListAsync(group)).Any(j => j.Id == current.Id), "A failed update must keep the old reminder.");
                using (var bob = new TimerTool(scheduler, config, group with { MessageId = "m4", Sender = new User("bob") { Name = "Bob" } }))
                    Check((await bob.ExecuteAsync(JsonSerializer.Serialize(new { action = "update", id = current.Id, text = "改掉" }))).Contains("not found"),
                        "Other users must not update someone else's reminder.");
                using (var off = new TimerTool(scheduler, new TimerToolSection(), group with { MessageId = "m5" }))
                    Check((await off.ExecuteAsync(JsonSerializer.Serialize(new { action = "update", id = current.Id, text = "改掉" }))).Contains("Only one-time"),
                        "Update must be unavailable when repeat reminders are disabled.");
            }
            Console.WriteLine("PASS: timer update keeps unspecified fields, replaces the old reminder and rolls back on failure");

            // 停机太久的一次性提醒不补发，标记为已错过；稍晚一点仍然补发。
            var missedRoot = Path.Combine(root, "missed");
            await using (var scheduler = new TimerScheduler(missedRoot, () => true, (jobs, _) => { sent.AddRange(jobs); return Task.CompletedTask; },
                missedGrace: () => TimeSpan.FromMinutes(30)))
            {
                using (var tool = new TimerTool(scheduler, new TimerToolSection(), group))
                {
                    await tool.ExecuteAsync(JsonSerializer.Serialize(new { at = at.ToString("O"), text = "早就过了" }));
                    await tool.ExecuteAsync(JsonSerializer.Serialize(new { at = at.AddHours(2).ToString("O"), text = "晚一点也发" }));
                    await tool.CommitAsync(default);
                }
                sent.Clear();
                await scheduler.DispatchDueAsync(at.AddHours(2).AddMinutes(10), default);
                var left = await scheduler.ListAsync(group);
                Check(sent.Single().Text == "晚一点也发", "Reminders within the grace period must still be delivered late.");
                Check(left.Single() is { Stopped: true, Text: "早就过了" } missed && missed.LastError!.Contains("错过"),
                    "Reminders past the grace period must be kept as missed without sending.");
            }
            Console.WriteLine("PASS: one-time reminders missed beyond the grace period are not sent");

            // 每日推送计数写入磁盘，重启后仍然生效。
            var countRoot = Path.Combine(root, "count");
            var when = at.AddDays(1);
            for (var round = 0; round < 2; round++)
            {
                await using var scheduler = new TimerScheduler(countRoot, () => true, (jobs, _) => { sent.AddRange(jobs); return Task.CompletedTask; }, () => 1);
                using (var tool = new TimerTool(scheduler, new TimerToolSection(), group with { MessageId = "c" + round }))
                {
                    await tool.ExecuteAsync(JsonSerializer.Serialize(new { at = when.AddMinutes(round).ToString("O"), text = "第" + round + "条" }));
                    await tool.CommitAsync(default);
                }
                sent.Clear();
                await scheduler.DispatchDueAsync(when.AddMinutes(round).AddSeconds(5), default);
                Check(round == 0 ? sent.Count == 1 : sent.Count == 0, "The daily delivery count must survive a restart.");
            }
            Check(File.Exists(Path.Combine(countRoot, "data", "timer-deliveries.json")), "Delivery counts must be persisted.");
            Console.WriteLine("PASS: daily delivery count persists across restarts");

            var longText = string.Concat(Enumerable.Repeat("这是一个比较长的句子，用来测试拆分。", 200));
            var chunks = ParsedReply.SplitForForward(longText, ShiroBot.AiChatPlugin.AiChatPlugin.FallbackChunkChars);
            Check(chunks.Count > 1 && chunks.All(c => c.Length <= ShiroBot.AiChatPlugin.AiChatPlugin.FallbackChunkChars) && string.Concat(chunks) == longText,
                "Fallback text must be split into bounded chunks without losing content.");
            Console.WriteLine("PASS: forward fallback splits long text into bounded chunks");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
