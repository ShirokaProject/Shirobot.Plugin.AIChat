using System.Reflection;
using System.Text.Json;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Timers;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

internal static class TimerLimitChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class NoopScope : IDisposable { public void Dispose() { } }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aichat-timer-limits-" + Guid.NewGuid().ToString("N"));
        var group = new MessageEvent { Platform = "custom", InstanceId = "adapter-one", SelfId = "bot", MessageId = "m1", Channel = Channel.Group("group"),
            Sender = new User("alice") { Name = "Alice" }, Segments = [] };
        var batches = new List<IReadOnlyList<TimerJob>>();
        var config = new TimerToolSection { AllowRepeat = true, MaxRecurringPerUser = 1, MaxPendingPerConversation = 3, MaxDeliveriesPerDay = 1, MaxRecurringDays = 10 };
        var at = DateTimeOffset.UtcNow.AddHours(1);
        at = new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, at.Offset);
        string Once(string text, DateTimeOffset when) => JsonSerializer.Serialize(new { at = when.ToString("O"), text });
        // 重复提醒固定在 at 之后 2 小时（本地时间），保证先于它的批次不受影响。
        var laterLocal = at.AddHours(2).ToLocalTime();
        var daily = JsonSerializer.Serialize(new { repeat = $"{laterLocal.Minute} {laterLocal.Hour} * * *", text = "早饭" });
        try
        {
            await using var scheduler = new TimerScheduler(root, () => true, (jobs, _) => { batches.Add(jobs); return Task.CompletedTask; }, () => config.MaxDeliveriesPerDay);
            using (var tool = new TimerTool(scheduler, config, group))
            {
                await tool.ExecuteAsync(daily);
                Check((await tool.ExecuteAsync("{\"repeat\":\"0 9 * * *\",\"text\":\"打卡\"}")).Contains("Recurring reminder limit"), "Recurring reminders per user must be capped.");
                await tool.ExecuteAsync(Once("开会", at));
                await tool.CommitAsync(default);
            }
            var recurring = (await scheduler.ListAsync(group)).Single(j => j.Cron is not null);
            Check(recurring.Until is { } until && (until - recurring.DueAt).TotalDays is > 9.99 and < 10.01, "Recurring reminders without until must expire after max_recurring_days.");

            var bob = group with { MessageId = "m2", Sender = new User("bob") { Name = "Bob" } };
            using (var tool = new TimerTool(scheduler, config, bob))
            {
                await tool.ExecuteAsync(Once("同时开会", at));
                Check((await tool.ExecuteAsync(Once("再来一个", at.AddMinutes(5)))).Contains("this conversation"), "Conversation-wide pending cap must apply across users.");
                await tool.CommitAsync(default);
            }
            Console.WriteLine("PASS: recurring per-user cap, automatic expiry and conversation-wide pending cap");

            // 同一会话同一分钟到期的两条合并为一次推送；当日上限为 1 时，下一批被跳过。
            await scheduler.DispatchDueAsync(at.AddSeconds(5), default);
            Check(batches.Count == 1 && batches[0].Count == 2, "Same-minute reminders in one conversation must be sent together.");
            await scheduler.DispatchDueAsync(recurring.DueAt.AddSeconds(5), default);
            var after = await scheduler.ListAsync(group);
            if (recurring.DueAt.ToLocalTime().Date == at.ToLocalTime().Date)
            {
                Check(batches.Count == 1 && after.Single().DueAt > recurring.DueAt && after.Single().LastError!.Contains("上限"),
                    "Over the daily cap, recurring reminders must move to their next run without sending.");
            }
            else Check(batches.Count == 2, "A new local day must reset the delivery counter.");
            Console.WriteLine("PASS: same-minute batching and per-conversation daily delivery cap");

            var later = at.AddDays(1);
            var stopped = new TimerScheduler(Path.Combine(root, "skip"), () => true, (jobs, _) => { batches.Add(jobs); return Task.CompletedTask; }, () => 1);
            await using (stopped)
            {
                using (var tool = new TimerTool(stopped, new TimerToolSection { MaxPendingPerUser = 2 }, group))
                {
                    await tool.ExecuteAsync(Once("第一条", later));
                    await tool.ExecuteAsync(Once("第二条", later.AddMinutes(1)));
                    await tool.CommitAsync(default);
                }
                await stopped.DispatchDueAsync(later.AddMinutes(2), default);
                var kept = (await stopped.ListAsync(group)).Single();
                Check(kept.Stopped && kept.Text == "第二条" && kept.LastError!.Contains("上限"), "Skipped one-time reminders must stay visible as stopped.");
                using var again = new TimerTool(stopped, new TimerToolSection { MaxPendingPerUser = 1 }, group with { MessageId = "m3" });
                Check(!(await again.ExecuteAsync(Once("新的", later.AddHours(1)))).Contains("limit"), "Stopped reminders must not count toward limits.");
            }
            Console.WriteLine("PASS: skipped one-time reminders stay visible and stop counting toward limits");

            var mentionConfig = new TimerToolSection { AllowMentionOthers = true };
            var withMention = group with { MessageId = "m4", Segments = [new MentionSegment("bot") { DisplayName = "喵露露" }, new MentionSegment("carol") { DisplayName = "Carol" }] };
            Check(!JsonSerializer.Serialize(new TimerTool(scheduler, new TimerToolSection(), withMention).Definition.Function.Parameters).Contains("mention"),
                "Mentioning others must not change the tool definition when disabled.");
            Check(!JsonSerializer.Serialize(new TimerTool(scheduler, mentionConfig, withMention with { Channel = Channel.Direct("alice") }).Definition.Function.Parameters).Contains("mention"),
                "Private chats must not offer mention.");
            var root2 = Path.Combine(root, "mention");
            await using var mentionScheduler = new TimerScheduler(root2, () => true, (jobs, _) => { batches.Add(jobs); return Task.CompletedTask; });
            using (var tool = new TimerTool(mentionScheduler, mentionConfig, withMention))
            {
                var schema = PluginJson.Serialize(tool.Definition.Function.Parameters);
                Check(schema.Contains("\"mention\"") && schema.Contains("\"required\":[\"at\",\"text\"]"), "Enabled mention must extend the one-time definition.");
                Check((await tool.ExecuteAsync(JsonSerializer.Serialize(new { at = at.ToString("O"), text = "开会", mention = new[] { "Dave" } }))).Contains("Carol"),
                    "Unmentioned members must be rejected with the list of mentioned members.");
                Check((await tool.ExecuteAsync(JsonSerializer.Serialize(new { at = at.ToString("O"), text = "开会", mention = new[] { "喵露露" } }))).Contains("not @-mentioned"),
                    "The bot itself must not be a target.");
                Check((await tool.ExecuteAsync(JsonSerializer.Serialize(new { at = at.ToString("O"), text = "开会", mention = new[] { "@Carol" } }))).Contains("Carol"),
                    "Mentioned members must be accepted by display name.");
                await tool.CommitAsync(default);
            }
            var targeted = (await mentionScheduler.ListAsync(group)).Single();
            Check(targeted.Recipients.Single().UserId == "carol" && targeted.IsOwnedBy("carol") && targeted.IsOwnedBy("alice") && !targeted.IsOwnedBy("bob"),
                "Targets must be stored and both creator and recipient may manage the reminder.");
            Console.WriteLine("PASS: mention others switch, definition unchanged when off, only @-mentioned members, recipient ownership");

            // 合并推送：一条消息 @ 所有对象，模型不可用时逐条列出原文。
            var plugin = new AiChatPlugin();
            void Set(string field, object value) => typeof(AiChatPlugin).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plugin, value);
            var ctx = DispatchProxy.Create<IBotContext, TestProxy>();
            var msg = DispatchProxy.Create<IMessageContext, TestProxy>();
            MessageSegment[]? segments = null;
            ((TestProxy)(object)ctx).Call = (m, _) => m.Name switch
            {
                "UseInstance" => new NoopScope(), "get_Platform" => "custom", "get_InstanceId" => "adapter-one", "get_Message" => msg,
                _ => throw new NotSupportedException(m.Name)
            };
            ((TestProxy)(object)msg).Call = (_, args) => { segments = ((IReadOnlyList<MessageSegment>)args![1]!).ToArray(); return Task.FromResult(new SentMessage("sent")); };
            typeof(PluginBase).GetProperty("Context", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plugin, ctx);
            Set("_config", new PluginConfig()); Set("_globalConcurrency", new SemaphoreSlim(1, 1));
            var own = targeted with { Id = "x", Targets = null, Text = "交周报" };
            await (Task)typeof(AiChatPlugin).GetMethod("SendTimerAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(plugin, [new[] { targeted, own }, CancellationToken.None])!;
            var mentions = segments!.OfType<MentionSegment>().Select(m => m.UserId).ToList();
            var body = segments!.OfType<TextSegment>().Single().Text;
            Check(mentions.SequenceEqual(["carol", "alice"]) && body.Contains("开会") && body.Contains("交周报") && body.Contains("Alice 预约"),
                "Batched delivery must mention every recipient once and include every reminder.");
            Console.WriteLine("PASS: batched delivery mentions all recipients in one message");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
