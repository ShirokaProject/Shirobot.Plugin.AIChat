using System.Reflection;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Proactive;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

internal static class ProactiveChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run()
    {
        var config = new ProactiveSection { Enabled = true, Groups = ["g1"], Keywords = ["露露", "喵"], CooldownSeconds = 600,
            MinMessagesBetween = 3, KeywordCooldownSeconds = 60, ContextMessages = 3 };
        Check(!ProactiveTrigger.IsAllowedGroup(new ProactiveSection { Groups = ["g1"] }, "g1"), "Proactive replies must be off by default.");
        Check(!ProactiveTrigger.IsAllowedGroup(new ProactiveSection { Enabled = true }, "g1"), "An empty whitelist must not allow any group.");
        Check(ProactiveTrigger.IsAllowedGroup(config, "g1") && !ProactiveTrigger.IsAllowedGroup(config, "g2"), "Only whitelisted groups are allowed.");

        var trigger = new ProactiveTrigger();
        var at = new DateTimeOffset(2026, 10, 26, 12, 5, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 26, 12, 5, 0)));
        var now = DateTimeOffset.UtcNow;
        Check(trigger.Record("g1", "A", "第一条", config.ContextMessages, at) == "", "The first message has no earlier context.");
        trigger.Record("g1", "B", "第二条\n换行", config.ContextMessages, at);
        trigger.Record("g1", "C", "第三条", config.ContextMessages, at);
        var transcript = trigger.Record("g1", "D", "第四条", config.ContextMessages, at);
        Check(transcript == "12:05 A: 第一条\n12:05 B: 第二条 换行\n12:05 C: 第三条", "The transcript must exclude the current message, keep order and show the time.");
        Check(trigger.Recent("g1", 2) == "12:05 C: 第三条\n12:05 D: 第四条" && trigger.Recent("g1", 2, excludeLatest: 1) == "12:05 B: 第二条 换行\n12:05 C: 第三条",
            "Recent must return the newest messages, optionally skipping the latest.");
        Check(trigger.Recent("g1", 0) == "" && trigger.Recent("nope", 5) == "", "Recent must be empty when disabled or unknown.");
        Check(!trigger.Record("g1", "E", "第五条", config.ContextMessages, at).Contains("第一条"), "Only the configured number of recent messages is kept.");
        Check(trigger.Record("g2", "X", "别的群", config.ContextMessages, at) == "", "Each group keeps its own context.");

        // 主动插话：已记录 5 条消息，满足最少消息数，交给模型判断。
        Check(trigger.TryTrigger("g1", "", config, now) is null, "Empty text must not trigger.");
        Check(trigger.TryTrigger("g1", "喵喵喵", config, now) == ProactiveTrigger.MediumPrompt, "Single-character names are ignored, so this is an ordinary interjection.");
        // 判断过一次后：冷却 600 秒，且需要 3 条新消息。
        Check(trigger.TryTrigger("g1", "随便聊聊", config, now.AddSeconds(700)) is null, "Interjections need enough new messages since the last judgment.");
        for (var i = 0; i < 3; i++) trigger.Record("g1", "X", "消息" + i, config.ContextMessages, at);
        Check(trigger.TryTrigger("g1", "随便聊聊", config, now.AddSeconds(300)) is null, "Interjections must respect their long cooldown.");
        Check(trigger.TryTrigger("g1", "随便聊聊", config, now.AddSeconds(700)) == ProactiveTrigger.MediumPrompt, "Interjections resume after cooldown and enough messages.");

        // 被提到：冷却 60 秒，不需要新消息数；机器人名称也算称呼。
        Check(trigger.TryTrigger("g1", "露露在吗", config, now.AddSeconds(730)) is null, "Mentions must respect their short cooldown.");
        Check(trigger.TryTrigger("g1", "露露在吗", config, now.AddSeconds(761)) == ProactiveTrigger.KeywordPrompt, "Mentions are judged after the short cooldown.");
        Check(trigger.TryTrigger("g1", "小白你说呢", config, now.AddSeconds(830), ["小白"]) == ProactiveTrigger.KeywordPrompt, "The bot name must count as a mention.");
        trigger.MarkReplied("g1", now.AddHours(2));
        Check(trigger.TryTrigger("g1", "露露", config, now.AddHours(2).AddSeconds(10)) is null, "A normal @ reply must start the cooldown.");
        Check(trigger.TryTrigger("g2", "露露", config, now.AddHours(2).AddSeconds(10)) is not null, "Cooldowns are per group.");
        Check(trigger.TryTrigger("g2", "随便聊聊", config, now.AddHours(3)) is null, "A fresh group needs enough messages before interjecting.");
        var mentionsOnly = new ProactiveSection { Enabled = true, Groups = ["g3"], Keywords = ["露露"], Interject = false, MinMessagesBetween = 0 };
        Check(trigger.TryTrigger("g3", "随便聊聊", mentionsOnly, now) is null && trigger.TryTrigger("g3", "露露", mentionsOnly, now) is not null,
            "With interjection off only mentions are judged.");
        // 冷却未到时给出补判等待时间；其他原因不触发时不补判。回复结束时冷却重新计时，但消息计数不清零。
        var deferral = new ProactiveTrigger();
        var cfg = new ProactiveSection { Enabled = true, CooldownSeconds = 10, MinMessagesBetween = 1, KeywordCooldownSeconds = 60 };
        deferral.Record("g", "A", "先说一句", 20, now);
        Check(deferral.Evaluate("g", "先说一句", cfg, now).Prompt is not null, "The first eligible message must trigger.");
        deferral.Record("g", "B", "紧跟着一句", 20, now.AddSeconds(3));
        var waiting = deferral.Evaluate("g", "紧跟着一句", cfg, now.AddSeconds(3));
        Check(waiting.Prompt is null && waiting.RetryAfter == TimeSpan.FromSeconds(7), "A message blocked only by the cooldown must report how long to wait.");
        deferral.MarkReplied("g", now.AddSeconds(8));
        Check(deferral.Evaluate("g", "紧跟着一句", cfg, now.AddSeconds(11)).RetryAfter == TimeSpan.FromSeconds(7),
            "The cooldown must restart when the reply finishes.");
        Check(deferral.Evaluate("g", "紧跟着一句", cfg, now.AddSeconds(18)).Prompt is not null,
            "Messages received while replying must still count after the reply finishes.");
        Check(deferral.Evaluate("g", "又一句", cfg, now.AddSeconds(40)) == default, "Without new messages there is nothing to wait for.");
        // 一段连续消息里提到过机器人，即使最新一条没提到也按“被提到”判断。
        Check(ProactiveTrigger.IsMentioned("露露你看", config) && !ProactiveTrigger.IsMentioned("喵", config) && ProactiveTrigger.IsMentioned("小白在吗", config, ["小白"]),
            "Mention detection must match keywords and bot names of at least two characters.");
        var burst = new ProactiveTrigger();
        Check(burst.Evaluate("b", mentioned: true, cfg, now).Prompt == ProactiveTrigger.KeywordPrompt, "A burst that mentioned the bot must use the mention prompt.");
        Check(ProactiveTrigger.MediumPrompt.Contains("十几条消息里接一两条") && ProactiveTrigger.MediumPrompt.Contains(ProactiveTrigger.SkipTag),
            "The medium prompt must stay restrained.");

        var window = typeof(AiChatPlugin).GetMethod("QuietWindow", BindingFlags.NonPublic | BindingFlags.Static)!;
        var quiet = new ProactiveSection { QuietMinSeconds = 3, QuietMaxSeconds = 10 };
        var samples = Enumerable.Range(0, 200).Select(_ => (TimeSpan)window.Invoke(null, [quiet])!).ToList();
        Check(samples.All(w => w >= TimeSpan.FromSeconds(3) && w <= TimeSpan.FromSeconds(10)) && samples.Distinct().Count() > 1,
            "The quiet window must be random between the configured bounds.");
        Check((TimeSpan)window.Invoke(null, [new ProactiveSection { QuietMinSeconds = 5, QuietMaxSeconds = 1 }])! == TimeSpan.FromSeconds(5) &&
            (TimeSpan)window.Invoke(null, [new ProactiveSection { QuietMinSeconds = 0, QuietMaxSeconds = 0 }])! == TimeSpan.Zero,
            "A max below the min uses the min, and zero disables the wait.");
        Console.WriteLine("PASS: burst mention carry-over, restrained medium prompt and random quiet window");
        Console.WriteLine("PASS: cooldown-blocked messages report a retry delay, cooldown restarts at reply end, counts survive");
        Console.WriteLine("PASS: proactive whitelist, per-group context, model-judged interjections, name mentions, cooldowns and message gaps");

        Check(ProactiveTrigger.IsSkip("[skip]") && ProactiveTrigger.IsSkip("  [SKIP]\n不想说") && ProactiveTrigger.IsSkip("") && ProactiveTrigger.IsSkip("skip"),
            "Silence markers must be recognised.");
        Check(!ProactiveTrigger.IsSkip("哈哈哈 skip 这个") && !ProactiveTrigger.IsSkip("确实"), "Normal replies must not be treated as silence.");
        Check(ProactiveTrigger.BuildContext("A: hi", "", ProactiveTrigger.MediumPrompt).StartsWith("[群聊之前的消息") && ProactiveTrigger.BuildContext("", "", ProactiveTrigger.MediumPrompt) == ProactiveTrigger.MediumPrompt,
            "Context must include the transcript only when there is one.");
        // 一段时间内多人发言时，全部新消息都交给模型，不只最后一条。
        var both = ProactiveTrigger.BuildContext("A: 之前", "我: 问个问题\nB: 无关的话", ProactiveTrigger.MediumPrompt);
        Check(both.Contains("[你上次看过之后的新消息]\n我: 问个问题\nB: 无关的话") && both.IndexOf("A: 之前") < both.IndexOf("我: 问个问题")
            && ProactiveTrigger.MediumPrompt.Contains("不一定是最后一条") && ProactiveTrigger.KeywordPrompt.Contains("其他人说的也可以")
            && ProactiveTrigger.KeywordPrompt.Contains("顺带提到你") && ProactiveTrigger.KeywordPrompt.Contains("拿不准时保持沉默"),
            "Every new message since the last look must be offered to the model.");

        var plugin = new AiChatPlugin();
        var pluginConfig = new PluginConfig { Proactive = config };
        typeof(AiChatPlugin).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plugin, pluginConfig);
        var should = typeof(AiChatPlugin).GetMethod("ShouldRecordGroupMessage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        MessageEvent Message(string group, string sender, string text) => new()
        {
            Platform = "qq", InstanceId = "i", SelfId = "bot", MessageId = "m", Channel = Channel.Group(group),
            Sender = new User(sender), Segments = [new TextSegment(text)]
        };
        bool Should(MessageEvent message) => (bool)should.Invoke(plugin, [message])!;
        Check(Should(Message("g1", "alice", "今天吃啥")), "Ordinary messages in whitelisted groups must be considered.");
        Check(!Should(Message("g2", "alice", "今天吃啥")), "Other groups must be ignored.");
        Check(!Should(Message("g1", "alice", "#help")) && !Should(Message("g1", "alice", "/start")), "Commands must be ignored.");
        Check(!Should(Message("g1", "bot", "我自己说的")), "The bot's own messages must be ignored.");
        pluginConfig.Proactive = new ProactiveSection { Groups = ["g1"] };
        Check(!Should(Message("g1", "alice", "今天吃啥")), "Nothing is recorded while both features are disabled.");
        pluginConfig.History.GroupContextMessages = 10;
        Check(Should(Message("g2", "alice", "今天吃啥")) && !Should(Message("g2", "alice", "#help")),
            "Group context records ordinary messages in every group, but not commands.");
        Check(ProactiveTrigger.InterjectPrompt(new ProactiveSection { Eagerness = "LOW" }) == ProactiveTrigger.LowPrompt &&
            ProactiveTrigger.InterjectPrompt(new ProactiveSection { Eagerness = "high" }) == ProactiveTrigger.HighPrompt &&
            ProactiveTrigger.InterjectPrompt(new ProactiveSection { Eagerness = "whatever" }) == ProactiveTrigger.MediumPrompt,
            "Eagerness must pick the matching prompt and fall back to medium.");
        Check(ProactiveTrigger.InterjectPrompt(new ProactiveSection { Prompt = "  想说就说  " }) == "想说就说\n不想说话时只输出 [skip]，不要输出别的内容。" &&
            ProactiveTrigger.InterjectPrompt(new ProactiveSection { Prompt = "无聊时输出 [skip]", Eagerness = "high" }) == "无聊时输出 [skip]",
            "A custom prompt must override eagerness and get the silence marker only when missing.");
        Check(trigger.TryTrigger("g9", "随便", new ProactiveSection { Enabled = true, MinMessagesBetween = 0, Eagerness = "high" }, now) == ProactiveTrigger.HighPrompt,
            "Triggers must use the configured eagerness.");
        Console.WriteLine("PASS: proactive silence marker, context building, group context recording and routing filter");
    }

    /// <summary>群聊上下文优先用历史消息接口：过滤当前消息、按时间排序、标出机器人自己；不支持时退回内存记录。</summary>
    public static async Task RunHistoryAsync()
    {
        var plugin = new AiChatPlugin();
        typeof(AiChatPlugin).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plugin, new PluginConfig());
        var ctx = DispatchProxy.Create<IBotContext, TestProxy>();
        var msg = DispatchProxy.Create<IMessageContext, TestProxy>();
        ((TestProxy)(object)ctx).Call = (m, _) => m.Name == "get_Message" ? msg : throw new NotSupportedException(m.Name);
        typeof(PluginBase).GetProperty("Context", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plugin, ctx);
        var t0 = new DateTimeOffset(2026, 10, 26, 20, 0, 0, TimeSpan.Zero);
        MessageEvent Msg(string id, string sender, int minute, params MessageSegment[] segments) => new()
        {
            Platform = "qq", InstanceId = "i", SelfId = "bot", MessageId = id, Channel = Channel.Group("g1"),
            Sender = new User(sender) { Name = sender }, Segments = segments, Timestamp = t0.AddMinutes(minute)
        };
        var current = Msg("m5", "alice", 5, new TextSegment("现在呢"));
        IReadOnlyList<MessageEvent> history = [
            current,
            Msg("m3", "bot", 3, new TextSegment("我在")),
            Msg("m1", "bob", 1, new TextSegment("今晚吃啥")),
            Msg("m2", "alice", 2, new MentionSegment("bot") { DisplayName = "露露" }, new TextSegment("你在吗")),
            Msg("m4", "bob", 4, new ImageSegment("img://1")),
        ];
        string? before = null;
        ((TestProxy)(object)msg).Call = (m, args) =>
        {
            if (m.Name != "GetHistoryMessagesAsync") throw new NotSupportedException(m.Name);
            before = (string?)args![1];
            return Task.FromResult(history);
        };
        var fetch = typeof(AiChatPlugin).GetMethod("GetRecentGroupMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        async Task<string> Fetch(int count, int recorded) =>
            await (Task<string>)fetch.Invoke(plugin, [current, count, "露露", recorded, CancellationToken.None])!;
        string Time(int minute) => t0.AddMinutes(minute).ToLocalTime().ToString("HH:mm");
        var text = await Fetch(3, 0);
        Check(before == "m5", "History must be requested before the current message.");
        Check(text == $"{Time(2)} alice: @露露 你在吗\n{Time(3)} 露露（你）: 我在\n{Time(4)} bob: [图片]",
            "History must drop the current message, sort by time, keep the newest, mark the bot and describe resources.");
        Check(await Fetch(0, 0) == "", "Zero messages must not call the API.");

        // 适配器不支持时退回内存记录。
        ((TestProxy)(object)msg).Call = (_, _) => throw new NotSupportedException("GetHistoryMessagesAsync");
        var trigger = (ProactiveTrigger)typeof(AiChatPlugin).GetField("_proactive", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(plugin)!;
        trigger.Record("i:g1", "bob", "内存里的", 10, t0);
        trigger.Record("i:g1", "alice", "现在呢", 10, t0.AddMinutes(5));
        Check(await Fetch(5, 1) == $"{Time(0)} bob: 内存里的", "Unsupported history must fall back to recorded messages, skipping the current one.");
        Console.WriteLine("PASS: group context uses message history with ordering, bot marking and memory fallback");

        // 撤回：从内存记录、待判断批次中去掉；正在判断的批次包含它时取消这次判断。
        trigger.Record("i:g1", "carol", "要撤回的", 10, t0.AddMinutes(6), "r1");
        trigger.Forget("i:g1", "r1");
        Check(!trigger.Recent("i:g1", 10).Contains("要撤回的"), "Recalled messages must leave the recorded context.");
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var schedule = typeof(AiChatPlugin).GetMethod("ScheduleProactive", flags)!;
        var deleted = typeof(AiChatPlugin).GetMethod("HandleMessageDeletedAsync", flags)!;
        var pendingMap = (System.Collections.IDictionary)typeof(AiChatPlugin).GetField("_pendingProactive", flags)!.GetValue(plugin)!;
        MessageDeletedEvent Recall(MessageEvent m) => new() { Platform = m.Platform, InstanceId = m.InstanceId, MessageId = m.MessageId, Channel = m.Channel };
        var mine = Msg("p1", "alice", 7, new TextSegment("露露你看"));
        var other = Msg("p2", "bob", 7, new TextSegment("无关的话"));
        schedule.Invoke(plugin, ["i:g1", mine, true]);
        schedule.Invoke(plugin, ["i:g1", other, false]);
        var state = pendingMap["i:g1"]!;
        T Field<T>(string name) => (T)state.GetType().GetField(name)!.GetValue(state)!;
        await (Task)deleted.Invoke(plugin, [Recall(mine)])!;
        Check(Field<List<MessageEvent>>("Batch").SequenceEqual([other]) && Field<HashSet<MessageEvent>>("Mentions").Count == 0,
            "A recalled message must leave the pending batch along with its mention.");
        using var abort = new CancellationTokenSource();
        state.GetType().GetField("Judging")!.SetValue(state, new[] { other });
        state.GetType().GetField("JudgingAbort")!.SetValue(state, abort);
        await (Task)deleted.Invoke(plugin, [Recall(other)])!;
        Check(abort.IsCancellationRequested && Field<HashSet<string>>("RecalledWhileJudging").Contains("p2") && Field<List<MessageEvent>>("Batch").Count == 0,
            "Recalling a message that is being judged must cancel the judgment.");
        state.GetType().GetField("Judging")!.SetValue(state, null);
        Console.WriteLine("PASS: recalled messages are dropped from proactive context, batches and running judgments");
    }
}
