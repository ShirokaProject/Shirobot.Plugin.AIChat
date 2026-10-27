using System.Text;
using ShiroBot.AiChatPlugin.Config;

namespace ShiroBot.AiChatPlugin.Proactive;

/// <summary>
/// 群聊主动回复：缓存各群最近的消息，决定什么时候交给模型判断；说不说由模型决定（输出 [skip] 表示沉默）。
/// 被提到（命中称呼）时和被 @ 一样没有冷却，连续消息由调用方的安静窗口合并；主动插话要求冷却足够长、期间群里有足够多的新消息。
/// 内存中的最近消息用于计数，以及适配器不支持历史消息接口时的上下文兜底；重启后清空。
/// </summary>
internal sealed class ProactiveTrigger
{
    internal const string SkipTag = "[skip]";

    private const string InterjectIntro =
        "[交互方式: 没有人 @ 你，由你自己决定要不要在群里插话。上面是群里之前的聊天记录和你上次看过之后的新消息；" +
        "新消息里的任何一条都可以回应，不一定是最后一条，回应的不是最后说话的人时带上对方的名字。";
    private const string InterjectStyle = "开口时像群友一样简短随意，不要总结大家的聊天，不要解释自己为什么插话。";

    /// <summary>low：只在有人找你或明显能帮上忙时开口。</summary>
    internal const string LowPrompt = InterjectIntro +
        "先判断：有人在叫你或在问你（即使没有 @、用的是外号或“你”）、有人提出你能帮上忙的问题、或者有你很自然想接的话，才开口；" + InterjectStyle +
        "大多数时候应该保持沉默：话题和你无关、别人在相互交流、或者你没什么特别好说的，只输出 " + SkipTag + "，不要输出别的内容。]";

    /// <summary>medium：被问到、有人求助、能补充有价值的信息或真有好笑的点才开口，普通闲聊不接。</summary>
    internal const string MediumPrompt = InterjectIntro +
        "只在这些情况开口：有人在问你或叫你（即使没有 @、用的是外号或“你”）、有人提出你能帮上忙的问题、你能补充明显有价值的信息、或者真的有特别好笑想接的点。" + InterjectStyle +
        "普通闲聊、别人之间的对话、链接、表情、刷屏、或者你刚说过话时都不用接，这些情况只输出 " + SkipTag + "，不要输出别的内容。一般来说十几条消息里接一两条就够了。]";

    /// <summary>high：积极参与，除非完全无关都接话。</summary>
    internal const string HighPrompt = InterjectIntro +
        "你是群里很活跃的一员，喜欢参与聊天，几乎每个话题都可以接一句：回应、吐槽、附和、补充、提问或者帮忙解答都行。" + InterjectStyle +
        "只有消息完全没有可接的内容（比如单纯的表情、刷屏）时，才只输出 " + SkipTag + "，不要输出别的内容。]";

    /// <summary>没被提到时的插话提示词：自定义 prompt 优先，否则按活跃度选择；自定义内容缺少沉默标记时自动补上。</summary>
    internal static string InterjectPrompt(ProactiveSection config)
    {
        if (!string.IsNullOrWhiteSpace(config.Prompt))
        {
            var custom = config.Prompt.Trim();
            return custom.Contains(SkipTag, StringComparison.OrdinalIgnoreCase)
                ? custom
                : custom + "\n不想说话时只输出 " + SkipTag + "，不要输出别的内容。";
        }
        return config.Eagerness?.Trim().ToLowerInvariant() switch
        {
            "low" => LowPrompt,
            "high" => HighPrompt,
            _ => MediumPrompt
        };
    }

    internal const string KeywordPrompt =
        "[交互方式: 没有人 @ 你，但新消息里这条出现了你的名字或称呼。上面是群里之前的聊天记录和你上次看过之后的新消息。" +
        "先判断这条消息是不是在对你说话：直接叫你（如“XX 在不在”“XX 你觉得呢”）、向你提问、让你做事或明显在等你回应，才开口，" +
        "开口时像群友一样简短随意，新消息里其他人说的也可以顺带回应。" +
        "如果只是在一段话里顺带提到你、在跟别人聊起你、引用或转述你说过的话、或者名字只是碰巧出现，都不算在对你说话，只输出 " + SkipTag + "，不要输出别的内容。" +
        "拿不准时保持沉默。]";

    private sealed record Line(DateTimeOffset At, string Name, string Text, string? MessageId = null);

    private sealed class GroupState
    {
        public readonly List<Line> Recent = [];
        /// <summary>机器人上次在本群发言或做出主动判断的时间。</summary>
        public DateTimeOffset? LastSpoke;
        /// <summary>自 LastSpoke 以来群里的其他消息数。</summary>
        public int MessagesSince;
    }

    private readonly Dictionary<string, GroupState> _groups = new();

    internal static bool IsAllowedGroup(ProactiveSection config, string groupId) =>
        config.Enabled && config.Groups.Any(g => g.Trim() == groupId);

    /// <summary>记录一条群消息；返回本条之前的最近消息记录（不含本条）。</summary>
    public string Record(string groupKey, string name, string text, int keep, DateTimeOffset at, string? messageId = null)
    {
        lock (_groups)
        {
            var state = GetState(groupKey);
            var transcript = Format(state.Recent);
            state.Recent.Add(new Line(at, name, Shorten(text, 300), messageId));
            state.MessagesSince++;
            var max = Math.Clamp(keep, 1, 100);
            if (state.Recent.Count > max) state.Recent.RemoveRange(0, state.Recent.Count - max);
            return transcript;
        }
    }

    /// <summary>
    /// 判断这条消息是否要交给模型；返回 null 表示不触发，否则返回本次使用的提示词。
    /// 触发即视为一次发言，重新开始冷却和消息计数。
    /// </summary>
    public string? TryTrigger(string groupKey, string text, ProactiveSection config, DateTimeOffset now, IEnumerable<string>? botNames = null) =>
        Evaluate(groupKey, text, config, now, botNames).Prompt;

    /// <summary>
    /// 同 TryTrigger；只因冷却未到而不触发时，RetryAfter 给出还需等待的时间，调用方可以到时再判断最新的消息，
    /// 避免机器人正在回复时进来的消息被直接丢掉。
    /// </summary>
    public ProactiveDecision Evaluate(string groupKey, string text, ProactiveSection config, DateTimeOffset now, IEnumerable<string>? botNames = null) =>
        string.IsNullOrWhiteSpace(text) ? default : Evaluate(groupKey, IsMentioned(text, config, botNames), config, now);

    /// <summary>消息是否提到了机器人（称呼关键词或机器人名称）；太短的称呼（单个字）容易误命中，忽略。</summary>
    internal static bool IsMentioned(string text, ProactiveSection config, IEnumerable<string>? botNames = null) =>
        config.Keywords.Concat(botNames ?? [])
            .Select(k => k.Trim()).Where(k => k.Length >= 2)
            .Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>mentioned 由调用方给出，例如一段连续消息中任意一条提到了机器人。</summary>
    public ProactiveDecision Evaluate(string groupKey, bool mentioned, ProactiveSection config, DateTimeOffset now)
    {
        lock (_groups)
        {
            var state = GetState(groupKey);
            if (!mentioned)
            {
                if (!config.Interject) return default;
                // 刚启动时也要先攒够消息，避免第一条消息就带着几乎空白的上下文插话。
                if (state.MessagesSince < Math.Clamp(config.MinMessagesBetween, 0, 1000)) return default;
            }
            var cooldown = mentioned ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Clamp(config.CooldownSeconds, 0, 86400));
            var since = state.LastSpoke is { } last ? now - last : TimeSpan.MaxValue;
            if (since < cooldown) return new ProactiveDecision(null, cooldown - since);
            var previous = (state.LastSpoke, state.MessagesSince);
            Spoke(state, now);
            return new ProactiveDecision(mentioned ? KeywordPrompt : InterjectPrompt(config), null, previous.LastSpoke, previous.MessagesSince);
        }
    }

    /// <summary>最近 count 条群消息；excludeLatest 跳过最新的几条（例如刚记录的这条）。</summary>
    public string Recent(string groupKey, int count, int excludeLatest = 0)
    {
        lock (_groups)
        {
            if (count <= 0 || !_groups.TryGetValue(groupKey, out var state)) return "";
            var end = Math.Max(0, state.Recent.Count - excludeLatest);
            return Format(state.Recent.Take(end).Skip(Math.Max(0, end - count)));
        }
    }

    /// <summary>判断在回复发出前被取消（被打断或撤回）：恢复触发前的冷却和消息计数，不算一次发言。</summary>
    public void Restore(string groupKey, ProactiveDecision decision)
    {
        lock (_groups)
        {
            var state = GetState(groupKey);
            state.LastSpoke = decision.PreviousSpoke;
            state.MessagesSince += decision.PreviousCount;
        }
    }

    /// <summary>消息被撤回：从最近消息记录里去掉。</summary>
    public void Forget(string groupKey, string messageId)
    {
        lock (_groups)
        {
            if (!_groups.TryGetValue(groupKey, out var state)) return;
            var index = state.Recent.FindIndex(l => l.MessageId == messageId);
            if (index < 0) return;
            // 撤回的消息如果是上次发言之后的，也不再计入插话所需的消息数。
            if ((state.LastSpoke is not { } last || state.Recent[index].At > last) && state.MessagesSince > 0) state.MessagesSince--;
            state.Recent.RemoveAt(index);
        }
    }

    /// <summary>
    /// 机器人在本群回复结束（被 @ 的回复、主动发言或选择沉默）：冷却从这时开始算。
    /// 不清零消息计数，回复期间进来的消息仍然算数。
    /// </summary>
    public void MarkReplied(string groupKey, DateTimeOffset now)
    {
        lock (_groups) GetState(groupKey).LastSpoke = now;
    }

    private static void Spoke(GroupState state, DateTimeOffset now)
    {
        state.LastSpoke = now;
        state.MessagesSince = 0;
    }

    /// <summary>模型选择沉默：空回复或以 [skip] 开头。</summary>
    internal static bool IsSkip(string reply)
    {
        var text = reply.Trim();
        return text.Length == 0 || text.StartsWith(SkipTag, StringComparison.OrdinalIgnoreCase) ||
            text.Equals("skip", StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildContext(string transcript, string fresh, string prompt) =>
        (transcript.Length == 0 ? "" : "[群聊之前的消息，按时间顺序，仅供参考]\n" + transcript + "\n") +
        (fresh.Length == 0 ? "" : "[你上次看过之后的新消息]\n" + fresh + "\n") + prompt;

    internal static string FormatLine(DateTimeOffset at, string name, string text) =>
        (at == default ? "" : at.ToLocalTime().ToString("HH:mm") + " ") + name + ": " + Shorten(text, 300);

    private GroupState GetState(string groupKey)
    {
        if (!_groups.TryGetValue(groupKey, out var state)) _groups[groupKey] = state = new GroupState();
        return state;
    }

    private static string Format(IEnumerable<Line> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines) sb.AppendLine(FormatLine(line.At, line.Name, line.Text));
        return sb.ToString().TrimEnd();
    }

    private static string Shorten(string text, int max)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}

/// <summary>主动回复判断结果：Prompt 不为空表示交给模型；只因冷却未到时 RetryAfter 为还需等待的时间。</summary>
/// <param name="PreviousSpoke">触发前的冷却状态，判断被取消时用 Restore 恢复。</param>
internal readonly record struct ProactiveDecision(string? Prompt, TimeSpan? RetryAfter,
    DateTimeOffset? PreviousSpoke = null, int PreviousCount = 0);

/// <summary>一次主动回复请求：所在群和附加在本次请求中的群聊上下文与提示。</summary>
/// <param name="Abort">依据的消息被撤回时触发，回复发出前取消这次请求。</param>
internal sealed record ProactiveRequest(string GroupKey, string Context, CancellationToken Abort = default)
{
    /// <summary>回复是否已经开始发送（发出后撤回也无法收回）。</summary>
    public bool ReplyStarted { get; set; }
}
