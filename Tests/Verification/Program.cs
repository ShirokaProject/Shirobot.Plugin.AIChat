using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Contracts;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.AiChatPlugin.UserState;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Plugin;
using PluginJson = ShiroBot.AiChatPlugin.PluginJson;
using Tomlyn;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Set(AiChatPlugin plugin, string field, object value) =>
    typeof(AiChatPlugin).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plugin, value);
static object Invoke(AiChatPlugin plugin, string method, params object[] args) =>
    typeof(AiChatPlugin).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(plugin, args)!;
static OpenAiClient Client(StubHandler handler)
{
    var client = new OpenAiClient(10, "");
    var clients = (Dictionary<string, HttpClient>)typeof(OpenAiClient)
        .GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
    clients["https://example.invalid/v1"] = new HttpClient(handler);
    return client;
}
static ModelEntry Model(string name) => new()
{
    Name = name, BaseUrl = "https://example.invalid/v1", ApiKey = "test"
};
static string Completion(string text) => JsonSerializer.Serialize(new
{
    choices = new[] { new { message = new { role = "assistant", content = text } } }
});

if (args.Contains("--provider-models-offline")) { await ProviderModelChecks.RunAsync(); return; }

if (args.Contains("--proactive-judge-offline")) { await ProactiveJudgeChecks.RunAsync(); return; }

if (args.Contains("--conversation-tools-offline")) { await ConversationToolChecks.RunAsync(); return; }

if (args.Contains("--upstream-completion-offline")) { await UpstreamCompletionChecks.RunAsync(); await RichReplyChecks.RunAsync(); await UsageChecks.RunAsync(); return; }

if (args.Contains("--reply-modes-offline")) { await ReplyModeChecks.RunAsync(); return; }

if (args.Contains("--reference-image-offline")) { await ReferenceImageChecks.RunAsync(); return; }

if (args.Contains("--image-retry-offline")) { await ImageRetryChecks.RunAsync(); return; }

if (args.Contains("--image-command-offline")) { await ImageCommandChecks.RunAsync(); return; }

if (args.Contains("--downloads-offline")) { await DownloadedFilesChecks.RunAsync(); return; }

if (args.Contains("--http-methods-offline")) { await HttpMethodsChecks.RunAsync(); await FetchTimerChecks.RunAsync(); return; }

if (args.Contains("--group-context-offline"))
{
    var groupPlugin = new AiChatPlugin();
    var masterConfig = new PluginConfig();
    Set(groupPlugin, "_config", masterConfig);
    var groupContext = DispatchProxy.Create<IBotContext, TestProxy>();
    ((TestProxy)(object)groupContext).Call = (method, values) => method.Name == "IsOwner"
        ? values![0] is UserReference user && user.InstanceId == "qq-main" && user.UserId == "123"
        : throw new NotSupportedException(method.Name);
    typeof(PluginBase).GetProperty("Context", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(groupPlugin, groupContext);
    var sender = new User("123") { Name = "Alice" };
    var groupMessage = new MessageEvent { Platform = "qq", InstanceId = "qq-main", SelfId = "bot", MessageId = "m7",
        Channel = Channel.Group("g"), Sender = sender, Member = new Member(sender) { Role = MemberRole.Owner },
        Timestamp = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero), Segments = [new TextSegment("hello")] };
    string Format(MessageEvent value) => (string)Invoke(groupPlugin, "FormatGroupLine", value, "Bot");
    var line = Format(groupMessage);
    Check(line.StartsWith("[消息 m7] [Master] [群主] ") && line.EndsWith("Alice(123): hello"), "Owner and QQ group owner tags must precede the timestamp, with groupMessage and user IDs.");
    Check(Format(groupMessage with { InstanceId = "other", Member = new Member(sender) { Role = MemberRole.Admin } }).StartsWith("[消息 m7] [群管理员] "), "Master matching must be scoped by adapter instance.");
    Check(!Format(groupMessage with { Platform = "discord", InstanceId = "other" }).Contains("[群主]"), "Other platforms must not use QQ role tags.");
    Check(!Format(groupMessage with { InstanceId = null }).Contains("[Master]"), "Missing adapter identity must not grant Master.");
    Check(AiChatPlugin.DisplayUserId("12345678901234567890") == "123456789012345", "Long displayed user IDs must be capped at 15 characters.");
    var longId = "abcdefghijklmnopqr";
    masterConfig.OwnerPrompt.MasterIds = ["discord-main/" + longId];
    var longMessage = groupMessage with { Platform = "discord", InstanceId = "discord-main", Sender = new User(longId), Member = null };
    Check(Format(longMessage).Contains("[Master]") && !Format(longMessage).Contains(longId), "Master must compare full IDs while only displaying the shortened ID.");
    Check(!Format(longMessage with { InstanceId = "other" }).Contains("[Master]"), "Configured scoped Master must not match another instance.");
    Check(!Format(longMessage with { Sender = new User("abcdefghijklmnoOTHER") }).Contains("[Master]"), "Matching displayed prefixes must not grant Master.");
    masterConfig.OwnerPrompt.MasterIds = [longId];
    Check(Format(longMessage).Contains("[Master]"), "A plain configured Master ID must match exactly.");
    var recorded = new ShiroBot.AiChatPlugin.Proactive.ProactiveTrigger();
    recorded.Record("g", "Alice(123)", "hello", 5, groupMessage.Timestamp, "m7", "[Master] [群主] ");
    Check(recorded.Recent("g", 5) == line, "Memory fallback must preserve the same identity and groupMessage ID format.");

    var first = new ChatTurn("user", [new TextPart(GroupContext.Header + "[10:00] Alice: hello\n[10:01] Bob: yes")], 1) { MessageId = "m1" };
    Check(GroupContext.NewLines("[10:01] Bob: yes\n[10:02] Alice: next\n[10:02] Alice: next", [first]) == "[10:02] Alice: next", "Overlapping context must be deduplicated.");
    var restored = PluginJson.Deserialize<ChatTurn>(PluginJson.Serialize(first))!;
    Check(restored.MessageId == "m1" && GroupContext.NewLines("[10:00] Alice: hello", [restored]) == "", "Deduplication must survive persistence.");
    Check(GroupContext.NewLines("[10:03] Bob: yes", [first]) == "[10:03] Bob: yes", "Repeated text at a new time must remain distinct.");
    Console.WriteLine("PASS: overlapping context, duplicate lines, persisted context and distinct repeated messages");
    return;
}

if (args.Contains("--skills-concurrency-offline")) { await SkillsConcurrencyChecks.RunAsync(); return; }

if (args.Contains("--image-api-offline")) { await ImageApiOfflineChecks.RunAsync(); return; }

// Verify the same initialization/application contract used by Dashboard and the host watcher.
{
    var root = Path.Combine(Path.GetTempPath(), "aichat-config-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var configPath = Path.Combine(root, "config.toml");
    File.WriteAllText(configPath, "# existing user configuration");
    var current = new PluginConfig();
    var configContext = DispatchProxy.Create<IConfigContext, TestProxy>();
    ((TestProxy)(object)configContext).Call = (method, _) => method.Name switch
    {
        "get_ConfigPath" => configPath,
        "Load" => current,
        _ => throw new Exception("Unexpected config operation: " + method.Name)
    };
    var bot = DispatchProxy.Create<IBotContext, TestProxy>();
    ((TestProxy)(object)bot).Call = (method, _) => method.Name == "get_Config"
        ? configContext : throw new NotSupportedException();
    var configured = new AiChatPlugin();
    typeof(PluginBase).GetProperty("Context", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(configured, bot);
    var component = (IConfigurableComponent)configured;
    Check(ReferenceEquals(await component.InitializeConfigAsync(configContext), current),
        "Host initialization must expose the loaded config.");
    Invoke(configured, "ApplyConfigDerivatives", true);
    var conversations = typeof(AiChatPlugin).GetField("_conversations", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(configured);
    var changed = new PluginConfig();
    changed.Default.DefaultPrompt = "updated from Dashboard";
    changed.Behaviour.ReplyWithMention = true;
    await component.ApplyConfigAsync(changed);
    Check(ReferenceEquals(component.CurrentConfig, changed) &&
        ((PluginConfig)component.CurrentConfig!).Behaviour.ReplyWithMention,
        "Host application must update the runtime configuration.");
    Check(ReferenceEquals(conversations, typeof(AiChatPlugin)
        .GetField("_conversations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(configured)),
        "Config application must preserve conversation state.");
    try
    {
        await component.ApplyConfigAsync(new PluginConfig(), new CancellationToken(true));
        throw new Exception("Cancelled config application unexpectedly succeeded.");
    }
    catch (OperationCanceledException) { }
    Check(ReferenceEquals(component.CurrentConfig, changed), "Cancelled application must preserve config.");
    await configured.OnUnload();
    Directory.Delete(root, true);
    Console.WriteLine("PASS: host-managed config initialization, live application, conversation preservation and cancellation");
}

if (args.Contains("--live-blog-fetch"))
{
    using var fetch = new HttpFetchTool(new HttpFetchSection { MaxTextChars = 40000, SummarizeLongText = false });
    using var home = JsonDocument.Parse(await fetch.ExecuteAsync("{\"url\":\"https://blog.greepar.uk/\"}"));
    var link = home.RootElement.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("text").GetString() == "友情链接");
    var target = link.GetProperty("url").GetString()!;
    Check(target == "https://blog.greepar.uk/friendlink/", "Homepage must provide the actual friend-link URL.");
    using var friends = JsonDocument.Parse(await fetch.ExecuteAsync(JsonSerializer.Serialize(new { url = target })));
    Check(friends.RootElement.GetProperty("text").GetString()!.Contains("PVPGOOD") &&
        friends.RootElement.GetProperty("links").EnumerateArray().Any(l => l.GetProperty("url").GetString() == "https://2x.nz/"),
        "Following the extracted link must return friend names and URLs.");
    Console.WriteLine("PASS: live blog homepage -> extracted friendlink URL -> friend names and site links.");
    return;
}

if (args.Contains("--live-qq-fetch"))
{
    using var fetch = new HttpFetchTool(new HttpFetchSection { MaxTextChars = 40000, SummarizeLongText = false });
    using var result = JsonDocument.Parse(await fetch.ExecuteAsync("{\"url\":\"https://bot.q.qq.com/wiki/develop/api-v2/openapi/emoji/model.html\"}"));
    var text = result.RootElement.GetProperty("text").GetString()!;
    Check(text.Contains("doge") && text.Contains("表情"), "Live official QQ page must return emoji table text via secure fetch.");
    Console.WriteLine($"PASS: live QQ emoji page through secure fetch with Fake-IP DNS; extracted {text.Length} characters, including emoji table.");
    return;
}

var options = new TomlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
var template = (string)typeof(AiChatPlugin).GetField("DefaultConfigTemplate", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
var config = TomlSerializer.Deserialize<PluginConfig>(template, options)!;
Check(config.ToolUse.Tavily is { Enabled: true } && config.History.CompactPrompt == HistorySection.DefaultCompactPrompt,
    "Default template must deserialize nested tool config and compact prompt.");
Check(config.ToolUse.ImageGen.PromptInstructions == "" &&
    TomlSerializer.Deserialize<PluginConfig>("[tool_use.image_gen]\nprompt_instructions = '用中文自然语言描述主体、构图和光影'", options)!
        .ToolUse.ImageGen.PromptInstructions == "用中文自然语言描述主体、构图和光影",
    "Image prompt instructions must default to empty and deserialize from their own tool configuration.");
Check(typeof(PluginConfig).GetProperty("Tavily") is null, "Root config must not expose the removed legacy Tavily section.");
var toolsConfig = new PluginConfig();
toolsConfig.ToolUse.HttpFetch.Enabled = false;
toolsConfig.ToolUse.Tavily.ApiKey = "test";
using (var registry = ChatToolRegistry.Create(toolsConfig)) Check(registry.HasTools, "Nested Tavily must register.");
toolsConfig.ToolUse.Tavily.Enabled = false;
using (var registry = ChatToolRegistry.Create(toolsConfig)) Check(!registry.HasTools, "Disabled Tavily must not register.");
toolsConfig.ToolUse.Enabled = false;
toolsConfig.ToolUse.Tavily.Enabled = true;
using (var registry = ChatToolRegistry.Create(toolsConfig)) Check(!registry.HasTools && registry.SystemPrompt == "", "Disabled tools must not advertise capabilities.");
Console.WriteLine("PASS: TOML template, nested Tavily config, removed legacy entry and tool disable");

// 0 表示不限制：最大输出 token 不发送参数，历史轮数只按 token 预算压缩；模板字段与默认值一致。
Check(config.Default.MaxOutputTokens == 0 && config.Default.OutputTokenLimit() is null && new DefaultSection { MaxOutputTokens = 4096 }.OutputTokenLimit() == 4096,
    "Zero max output tokens must mean no limit.");
Check(config.History.MaxTurns == 0 && config.History.MaxTokens == 128000 && config.Resources.RecursiveDepth == 3 && config.Resources.MaxFileExcerptChars == 16000
    && config.Default.SharedPromptSuffix == DefaultSection.DefaultSharedPromptSuffix, "Template defaults must match the config class.");
Check(!config.History.CompactPrompt.Contains("长期记忆") && DefaultSection.DefaultSharedPromptSuffix.Contains("完整回答"),
    "History compaction must be called a history summary and group replies may answer in full.");
{
    var limits = new AiChatPlugin();
    var limitConfig = new PluginConfig();
    limitConfig.History.MaxTurns = 0;
    limitConfig.History.MaxTokens = 100_000;
    Set(limits, "_config", limitConfig);
    var many = new ConversationState(new ConversationKey("i", ChatScene.Group, "g", "u"));
    for (var i = 0; i < 200; i++) many.Turns.Add(new ChatTurn(i % 2 == 0 ? "user" : "assistant", [new TextPart("嗯")], i));
    Check(!(bool)Invoke(limits, "NeedsCompaction", many), "Short unlimited histories must not compact before token or safety pressure.");
    limitConfig.History.MaxTokens = 2_560_000;
    while (many.Turns.Count < 1599) many.Turns.Add(new ChatTurn(many.Turns.Count % 2 == 0 ? "user" : "assistant", [new TextPart("嗯")], many.Turns.Count));
    Check(!(bool)Invoke(limits, "NeedsCompaction", many), "Safety compaction must not trigger below 80 percent of the cap.");
    many.Turns.Add(new ChatTurn("assistant", [new TextPart("嗯")], 1599));
    Check((bool)Invoke(limits, "NeedsCompaction", many), "Unlimited histories must compact before the storage safety cap even with a very large token budget.");
    limitConfig.History.MaxTurns = 20;
    Check((bool)Invoke(limits, "NeedsCompaction", many), "A turn limit must still trigger compaction.");
    // 旧版本默认提示词升级成当前默认值，自定义内容保持不变。
    limitConfig.History.CompactPrompt = HistorySection.LongTermMemoryCompactPrompt;
    limitConfig.Default.SharedPromptSuffix = DefaultSection.ShortOnlySharedPromptSuffix;
    AiChatPlugin.UpgradeLegacyDefaults(limitConfig);
    Check(limitConfig.History.CompactPrompt == HistorySection.DefaultCompactPrompt && limitConfig.Default.SharedPromptSuffix == DefaultSection.DefaultSharedPromptSuffix,
        "Previous default prompts must upgrade to the current defaults.");
}
Console.WriteLine("PASS: unlimited output tokens and history turns, template defaults and prompt upgrades");

var plugin = new AiChatPlugin();
var primary = Model("primary");
var backup = Model("backup");
config = new PluginConfig { Models = [primary, backup] };
config.Default.FallbackModel = "backup";
config.Default.RetryOnTimeoutMaxAttempts = 2;
config.Default.RetryOnTimeoutDelayMs = 100;
Set(plugin, "_config", config);
var handler = new StubHandler(body => body.GetProperty("model").GetString() == "primary"
    ? Completion("") : Completion("fallback answer"));
using (var client = Client(handler))
{
    Func<ModelEntry, Task<string>> operation = model => client.ChatAsync(config.Default, model,
        new OpenAiChatRequest { Model = model.Name, Messages = [new OpenAiMessage { Content = "question" }] });
    var answer = await (Task<string>)Invoke(plugin, "ExecuteTextWithFallbackAsync", primary, operation, (Action)(() => { }), CancellationToken.None);
    Check(answer == "fallback answer" && handler.Requests.Select(r => r.GetProperty("model").GetString())
        .SequenceEqual(["primary", "primary", "backup"]), "Fallback must run after primary retries.");
}
config.Default.FallbackModel = "primary";
var attempts = 0;
try
{
    Func<ModelEntry, Task<string>> fail = _ => { attempts++; throw new InvalidOperationException("failure"); };
    await (Task<string>)Invoke(plugin, "ExecuteTextWithFallbackAsync", primary, fail, (Action)(() => { }), CancellationToken.None);
    throw new Exception("Expected failure for identical fallback.");
}
catch (InvalidOperationException) { Check(attempts == 1, "Same fallback must not retry itself."); }
config.Default.FallbackModel = "missing";
attempts = 0;
try
{
    Func<ModelEntry, Task<string>> fail = _ => { attempts++; throw new InvalidOperationException("failure"); };
    await (Task<string>)Invoke(plugin, "ExecuteTextWithFallbackAsync", primary, fail, (Action)(() => { }), CancellationToken.None);
    throw new Exception("Expected failure for unregistered fallback.");
}
catch (InvalidOperationException) { Check(attempts == 1, "Missing fallback must preserve primary error."); }
Console.WriteLine("PASS: empty-response retries then fallback, identical and missing fallback guards");

handler = new StubHandler(_ => """
    {"choices":[{"message":{"role":"assistant","content":"","reasoning_content":"private reasoning payload","tool_calls":[
    {"id":"a","type":"function","function":{"name":"first","arguments":"{}"}},
    {"id":"b","type":"function","function":{"name":"second","arguments":"{}"}},
    {"id":"c","type":"function","function":{"name":"first","arguments":"{}"}},
    {"id":"d","type":"function","function":{"name":"unknown","arguments":"{}"}}
    ]}}]}
    """);
using (var client = Client(handler))
using (var registry = new ChatToolRegistry())
{
    var toolLog = new ToolLogChecks.CaptureLogger();
    using var logScope = BotLog.BeginScope(toolLog);
    var first = new FakeTool("first");
    var second = new FakeTool("second");
    registry.Register(first);
    registry.Register(second);
    handler.Respond = body => handler.Requests.Count == 1 ? handler.RespondInitial : Completion("tool answer");
    var answer = await registry.ChatAsync(client, primary, [new OpenAiMessage { Content = "question" }], 1);
    Check(answer == "tool answer" && first.Calls == 2 && second.Calls == 1, "All calls in the permitted round must execute under the shared round limit.");
    Check(handler.Requests[0].GetProperty("tools").GetArrayLength() == 2 && !handler.Requests[1].TryGetProperty("tools", out _), "Final round must omit tools.");
    var assistantWithTools = handler.Requests[1].GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "assistant");
    Check(assistantWithTools.GetProperty("reasoning_content").GetString() == "private reasoning payload" && !answer.Contains("private reasoning payload"),
        "JSON tool response reasoning must pass back unchanged without entering visible reply.");
    var results = handler.Requests[1].GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool").ToArray();
    Check(results.Length == 4 && results[2].GetProperty("content").GetString() == "first result" && results[3].GetProperty("content").GetString()!.Contains("Unknown"), "Repeated calls in the same round must execute, and unknown calls still need matching tool replies.");
    Check(toolLog.Lines.Count(l => l.Contains("ToolUse 开始")) == 3 && toolLog.Lines.Count(l => l.Contains("ToolUse 完成")) == 3 &&
        toolLog.Lines.Count(l => l.Contains("ToolUse 拒绝")) == 1, "Every executed and rejected tool call must be logged independently.");
}
using (var tool = new TavilySearchTool(new TavilySection(), ""))
    Check((await tool.ExecuteAsync("{\"query\":42}")).Contains("invalid"), "Non-string query must return a validation error.");
Console.WriteLine("PASS: shared tool round limit, repeated calls, unknown tools, final answer and argument validation");

handler = new StubHandler(_ => Completion("custom summary"));
using (var client = Client(handler))
{
    config.History.MaxTurns = 4;
    config.History.CompactPrompt = "custom compact prompt";
    Set(plugin, "_openAi", client);
    Set(plugin, "_conversations", new ConversationStore(Path.GetTempPath(), false, 4));
    var state = new ConversationState(new ConversationKey("test-instance", ChatScene.Friend, "1", "1"));
    for (var i = 0; i < 8; i++) state.Turns.Add(new ChatTurn(i % 2 == 0 ? "user" : "assistant", [new TextPart($"turn {i}")], i));
    state.Summary = "old memory";
    await (Task)Invoke(plugin, "TryCompactConversationAsync", state, primary);
    var compactMessages = handler.Requests[0].GetProperty("messages");
    Check(compactMessages[0].GetProperty("content").GetString() == "custom compact prompt", "Compact must send configured system prompt.");
    var compactInput = compactMessages[1].GetProperty("content").GetString()!;
    Check(compactMessages.GetArrayLength() == 2 && compactInput.Contains("old memory") && compactInput.Contains("用户: turn 0") &&
        compactInput.Contains("机器人: turn 3") && !compactInput.Contains("turn 4"), "Compact must merge the previous memory with only the evicted transcript.");
    Check(state.Summary == "custom summary" && state.Turns.Count == 4 && state.Turns[0].Parts.OfType<TextPart>().Single().Text == "turn 4",
        "Compact must store the merged memory separately and keep the newest turns verbatim.");

    // 只有 2 轮但 token 超过预算的 75% 时也要压缩，且至少保留最近一对。
    config.History.MaxTokens = 1000;
    var big = new ConversationState(new ConversationKey("test-instance", ChatScene.Friend, "1", "2"));
    for (var i = 0; i < 4; i++) big.Turns.Add(new ChatTurn(i % 2 == 0 ? "user" : "assistant", [new TextPart(new string('字', 300))], i));
    await (Task)Invoke(plugin, "TryCompactConversationAsync", big, primary);
    Check(big.Turns.Count == 2 && big.Summary == "custom summary", "Token pressure must compact even below the turn threshold.");

    var history = Enumerable.Range(0, 6).Select(i => new ChatTurn(i % 2 == 0 ? "user" : "assistant", [new TextPart(new string('字', 300))], i)).ToList();
    Invoke(plugin, "TrimHistoryToTokenBudget", "system", history, new ChatTurn("user", [new TextPart("hi")], 9));
    Check(history.Count == 2 && history[0].Role == "user", "Request history must drop the oldest pairs until it fits history.max_tokens.");
    config.History.MaxTokens = 16000;
}
{
    var root = Path.Combine(Path.GetTempPath(), "aichat-memory-" + Guid.NewGuid().ToString("N"));
    var store = new ConversationStore(root, true, 20);
    var key = new ConversationKey("test-instance", ChatScene.Friend, "1", "1");
    var state = await store.AcquireAsync(key);
    state.Summary = "人物：小明";
    state.Turns.Add(new ChatTurn("user", [new TextPart("hi")], 1));
    state.Turns.Add(new ChatTurn("assistant", [new TextPart("hello")], 2));
    await store.RewriteAsync(state);
    await store.AppendTurnsAsync(state, new ChatTurn("user", [new TextPart("again")], 3), new ChatTurn("assistant", [new TextPart("ok")], 4));
    store.Release(key);
    state = await store.AcquireAsync(key);
    Check(state.Summary == "人物：小明" && state.Turns.Count == 4 && state.Turns.All(t => t.Role != "summary"),
        "Long-term memory must survive reload separately from turns.");
    store.Clear(key);
    store.Release(key);
    state = await store.AcquireAsync(key);
    Check(state.Summary is null && state.Turns.Count == 0, "Clearing a conversation must also clear its long-term memory.");
    store.Release(key);

    var legacyKey = new ConversationKey("test-instance", ChatScene.Friend, "1", "3");
    var legacy = await store.AcquireAsync(legacyKey);
    legacy.Turns.Add(new ChatTurn("assistant", [new TextPart("[对话摘要] 旧摘要")], 1));
    legacy.Turns.Add(new ChatTurn("user", [new TextPart("hi")], 2));
    await store.RewriteAsync(legacy);
    store.Release(legacyKey);
    legacy = await store.AcquireAsync(legacyKey);
    Check(legacy.Summary == "旧摘要" && legacy.Turns.Single().Role == "user", "Legacy summary turns must migrate into long-term memory.");
    store.Release(legacyKey);
    Directory.Delete(root, true);
}
Console.WriteLine("PASS: incremental long-term memory, token-triggered compact, request token budget, persistence, clear and legacy migration");

// The full oldest pair must reach compaction before storage trimming, including
// an oversized history from an earlier version. Failure must preserve recent turns.
{
    var root = Path.Combine(Path.GetTempPath(), "aichat-compact-cap-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var capConfig = new PluginConfig();
        capConfig.History.MaxTurns = 0;
        capConfig.History.MaxTokens = 2_560_000;
        var capPlugin = new AiChatPlugin();
        Set(capPlugin, "_config", capConfig);
        var capStore = new ConversationStore(root, true, 0);
        Set(capPlugin, "_conversations", capStore);
        var capKey = new ConversationKey("cap", ChatScene.Group, "test", "u");
        var capState = new ConversationState(capKey) { Summary = "existing summary" };
        for (int i = 0; i < 2002; i++) capState.Turns.Add(new ChatTurn(i % 2 == 0 ? "user" : "assistant", [new TextPart("original " + i)], i));
        await capStore.RewriteAsync(capState);
        capState = await capStore.AcquireAsync(capKey);
        Check(capState.Turns.Count == 2002 && capState.Turns[0].Parts.OfType<TextPart>().Single().Text == "original 0",
            "Loading an oversized legacy history must not discard its oldest messages before compaction.");
        var summaryHandler = new StubHandler(_ => Completion("merged summary"));
        using (var capClient = Client(summaryHandler))
        {
            Set(capPlugin, "_openAi", capClient);
            var stateForCompact = capState;
            await capStore.AppendTurnsAsync(capState, new ChatTurn("user", [new TextPart("latest user")], 2002),
                new ChatTurn("assistant", [new TextPart("latest answer")], 2003),
                () => (Task<bool>)Invoke(capPlugin, "TryCompactConversationAsync", stateForCompact, primary));
            var input = summaryHandler.Requests.Single().GetProperty("messages")[1].GetProperty("content").GetString()!;
            Check(input.Contains("original 0") && input.Contains("original 1003") && !input.Contains("latest user"),
                "Compact must see the oldest transcript and keep the newest pair out of the summary request.");
            Check(capState.Summary == "merged summary" && capState.Turns.Count == 1000 &&
                capState.Turns[^1].Parts.OfType<TextPart>().Single().Text == "latest answer", "Safety compact must keep a bounded recent half and the current answer.");
        }
        capStore.Release(capKey);
        var reloaded = await capStore.AcquireAsync(capKey);
        Check(reloaded.Summary == "merged summary" && reloaded.Turns.Count == 1000 &&
            reloaded.Turns.Count(t => t.Parts.OfType<TextPart>().Any(p => p.Text == "latest answer")) == 1,
            "A compacted append must persist the summary and latest pair exactly once.");
        capStore.Release(capKey);

        var failed = new ConversationState(new ConversationKey("cap", ChatScene.Group, "failed", "u")) { Summary = "old summary" };
        for (int i = 0; i < 2000; i++) failed.Turns.Add(new ChatTurn(i % 2 == 0 ? "user" : "assistant", [new TextPart("failed " + i)], i));
        var failureHandler = new StubHandler(_ => Completion("unused"))
            { Respond = _ => throw new HttpRequestException("fixture unavailable") };
        using (var failureClient = Client(failureHandler))
        {
            Set(capPlugin, "_openAi", failureClient);
            await capStore.AppendTurnsAsync(failed, new ChatTurn("user", [new TextPart("kept user")], 2000),
                new ChatTurn("assistant", [new TextPart("kept answer")], 2001),
                () => (Task<bool>)Invoke(capPlugin, "TryCompactConversationAsync", failed, primary));
        }
        Check(failed.Summary == "old summary" && failed.Turns.Count == 2000 && failed.Turns[^1].Parts.OfType<TextPart>().Single().Text == "kept answer",
            "Failed summaries must retain the previous summary, latest pair and bounded storage.");

        var canceled = new ConversationState(new ConversationKey("cap", ChatScene.Group, "canceled", "u"));
        try
        {
            await capStore.AppendTurnsAsync(canceled, new ChatTurn("user", [new TextPart("delivered user")], 1),
                new ChatTurn("assistant", [new TextPart("delivered answer")], 2),
                () => Task.FromException<bool>(new OperationCanceledException()));
            throw new Exception("Compaction cancellation was swallowed");
        }
        catch (OperationCanceledException) { }
        var persistedCanceled = await capStore.AcquireAsync(canceled.Key);
        Check(persistedCanceled.Turns.Count == 2, "A canceled pre-trim callback must still save an already delivered pair.");
        capStore.Release(canceled.Key);

        var failingPath = Path.Combine(root, "data", "conversations", failed.Key.ToFileSafeString() + ".jsonl");
        var originalFile = File.ReadAllBytes(failingPath);
        Directory.CreateDirectory(failingPath + ".tmp");
        var snapshot = failed.Turns.ToArray();
        using (var writingClient = Client(new StubHandler(_ => Completion("must not replace old summary"))))
        {
            Set(capPlugin, "_openAi", writingClient);
            Check(!await (Task<bool>)Invoke(capPlugin, "TryCompactConversationAsync", failed, primary), "Write failure must report unsuccessful compaction.");
        }
        Check(failed.Summary == "old summary" && failed.Turns.SequenceEqual(snapshot) && originalFile.AsSpan().SequenceEqual(File.ReadAllBytes(failingPath)),
            "A summary write failure must leave both original history and summary intact.");
    }
    finally { Directory.Delete(root, recursive: true); }
    Console.WriteLine("PASS: safety-cap compaction precedes trimming; full legacy transcripts, persisted pairs, summary failure, cancellation and write failure");
}

var messageContext = DispatchProxy.Create<IMessageContext, TestProxy>();
var context = DispatchProxy.Create<IBotContext, TestProxy>();
((TestProxy)(object)context).Call = (method, _) => method.Name == "get_Message" ? messageContext : throw new NotSupportedException();
typeof(PluginBase).GetProperty("Context", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, context);
MethodInfo? sentMethod = null;
object?[] sentArgs = [];
((TestProxy)(object)messageContext).Call = (method, args) => { sentMethod = method; sentArgs = args!; return Task.FromResult(new SentMessage("sent")); };
var message = new MessageEvent { Platform = "qq", SelfId = "123", MessageId = "1", Channel = Channel.Group("456"), Sender = new User("789"), Segments = [] };
config.Behaviour.FoldAboveChars = 2;
await (Task)Invoke(plugin, "ReplyAssistantAsync", message, true, "中😀文");
Check(sentMethod!.Name == "SendMessageAsync" && ((IReadOnlyList<MessageSegment>)sentArgs[1]!).OfType<RawSegment>().Single().Payload is QOutgoingForward forward && forward.Messages.Single().Segments.OfType<QOutgoingText>().Single().Text == "中😀文", "Long QQ reply must fold and retain full text.");
var folded = (QOutgoingForward)((IReadOnlyList<MessageSegment>)sentArgs[1]!).OfType<RawSegment>().Single().Payload;
Check(folded.Preview!.Single() == "中😀文" && folded.Summary == "点击查看全部", "Folded card must preview actual reply text and show the requested footer.");
var preview = (string)typeof(AiChatPlugin).GetMethod("CreateReplyPreview", BindingFlags.Static | BindingFlags.NonPublic)!
    .Invoke(null, [string.Concat(Enumerable.Repeat("😀", 121)) + "\ncontinued"])!;
Check(preview.EnumerateRunes().Count() == 121 && preview.EndsWith('…'), "Preview must truncate without splitting Unicode characters.");
await (Task)Invoke(plugin, "ReplyAssistantAsync", message, true, "中😀");
Check(sentMethod!.Name == "QuoteReplyAsync", "Unicode character count at threshold must not fold.");
await (Task)Invoke(plugin, "ReplyAssistantAsync", message with { Platform = "discord" }, true, "long text");
Check(sentMethod!.Name == "QuoteReplyAsync", "Other platforms must retain ordinary replies.");
config.Behaviour.FoldAboveChars = 0;
await (Task)Invoke(plugin, "ReplyAssistantAsync", message, true, "long text");
Check(sentMethod!.Name == "QuoteReplyAsync", "Zero threshold must disable folding.");
config.Behaviour.FoldAboveChars = 2;
var sends = new List<string>();
((TestProxy)(object)messageContext).Call = (method, _) =>
{
    sends.Add(method.Name);
    return Task.FromResult(new SentMessage(method.Name == "SendMessageAsync" ? "" : "sent"));
};
await (Task)Invoke(plugin, "ReplyAssistantAsync", message, true, "long text");
Check(sends.SequenceEqual(["SendMessageAsync", "QuoteReplyAsync"]), "Unsupported or failed forwards must fall back to ordinary text.");
Console.WriteLine("PASS: folded payload, Unicode boundary, other platforms, disabled folding and send failure fallback");

((TestProxy)(object)messageContext).Call = (method, args) =>
{
    sentMethod = method; sentArgs = args!;
    return Task.FromResult(new SentMessage("sent"));
};
config.Behaviour.ReplyWithMention = true;
config.Behaviour.FoldAboveChars = 0;
await (Task)Invoke(plugin, "ReplyAssistantAsync", message, true, "answer");
var mentionSegments = (IReadOnlyList<MessageSegment>)sentArgs[1]!;
Check(sentMethod!.Name == "QuoteReplyAsync" && mentionSegments[0] is MentionSegment { UserId: "789" } &&
    mentionSegments.OfType<TextSegment>().Single().Text == " answer", "Mention and quote must be compatible, with mention before text.");
config.Behaviour.ReplyWithQuote = false;
await (Task)Invoke(plugin, "ReplyAssistantAsync", message, true, "answer");
Check(sentMethod!.Name == "ReplyAsync" && ((IReadOnlyList<MessageSegment>)sentArgs[1]!)[0] is MentionSegment,
    "Mention must also work without quote.");
config.Behaviour.FoldAboveChars = 2;
await (Task)Invoke(plugin, "ReplyAssistantAsync", message, true, "folded answer");
mentionSegments = (IReadOnlyList<MessageSegment>)sentArgs[1]!;
Check(sentMethod!.Name == "SendMessageAsync" && mentionSegments[0] is MentionSegment &&
    mentionSegments.OfType<RawSegment>().Single().Payload is QOutgoingForward,
    "Folded card must mention the sender outside the forwarded content.");
await (Task)Invoke(plugin, "ReplyAssistantAsync", message with { Channel = Channel.Direct("789") }, false, "answer");
Check(sentMethod!.Name == "SendMessageAsync" && !((IReadOnlyList<MessageSegment>)sentArgs[1]!).OfType<MentionSegment>().Any(),
    "Private folded cards must not contain a mention.");
config.Behaviour.FoldAboveChars = 0;
await (Task)Invoke(plugin, "ReplyAssistantAsync", message with { Channel = Channel.Direct("789") }, false, "answer");
Check(sentMethod!.Name == "ReplyAsync" && sentArgs[1] is string, "Private replies must not contain a mention.");
Console.WriteLine("PASS: mention with/without quote, folded-card mention and no mention in private replies");

config.Behaviour.AutoReplyDirectMessages = false;
Set(plugin, "_config", config);
var direct = message with { Channel = Channel.Direct("789"), Segments = [new TextSegment("你好")] };
Check(!(bool)Invoke(plugin, "ShouldAutoReplyDirectMessage", direct), "Private auto reply must default off.");
config.Behaviour.AutoReplyDirectMessages = true;
Check((bool)Invoke(plugin, "ShouldAutoReplyDirectMessage", direct), "Ordinary private text must auto reply when enabled.");
Check((bool)Invoke(plugin, "ShouldAutoReplyDirectMessage", direct with { Segments = [new ImageSegment("https://example.invalid/image")] }),
    "Private images must not require a text prefix.");
foreach (var command in new[] { "#ai question", "#clear", "  #unknown", "/help" })
    Check(!(bool)Invoke(plugin, "ShouldAutoReplyDirectMessage", direct with { Segments = [new TextSegment(command)] }),
        "Commands must not enter the auto reply route.");
Check(!(bool)Invoke(plugin, "ShouldAutoReplyDirectMessage", message), "Group messages must not trigger private auto reply.");
Console.WriteLine("PASS: private auto reply switch, ordinary text and image triggers, group and command exclusions");

Check(config.Permissions.ClearAdminOnly && typeof(PermissionsSection).GetProperty("AllowUsers") is null && typeof(PermissionsSection).GetProperty("DenyUsers") is null,
    "Only administrative restrictions remain; clear defaults to admin-only.");
config.OwnerPrompt.Enabled = false;
var identityBase = (string)Invoke(plugin, "ApplyOwnerPrompt", "base prompt");
Check(identityBase.StartsWith("base prompt\n[身份标记规则"), "Identity metadata rules must always be present.");
config.OwnerPrompt.Enabled = true;
config.OwnerPrompt.Template = "主人是 789，QQ 123456，机器人使用 ShiroBot。{自定义文字}\n第二行";
var identity = (string)Invoke(plugin, "ApplyOwnerPrompt", "base prompt");
Check(identity == identityBase + "\n" + config.OwnerPrompt.Template, "Owner text must be appended verbatim, including braces and newlines.");
var admin = false;
((TestProxy)(object)context).Call = (method, _) => method.Name switch { "IsAdmin" => admin, "get_Message" => messageContext, "get_InstanceId" => "test", _ => throw new NotSupportedException() };
Check(!await (Task<bool>)Invoke(plugin, "CanClearAsync", direct) && !await (Task<bool>)Invoke(plugin, "CanClearAsync", message),
    "Owner prompt identity must not grant clear permissions in direct or group messages.");
// Shared group clearing is guarded; private conversations are owned by their user.
var guardRoot = Path.Combine(Path.GetTempPath(), "aichat-clear-" + Guid.NewGuid().ToString("N"));
Set(plugin, "_userSettings", new UserSettingsStore(guardRoot));
var originalMode = config.Default.DefaultChatMode;
config.Default.DefaultChatMode = "shared";
try { await (Task)Invoke(plugin, "HandleGroupClearAsync", message); }
finally { config.Default.DefaultChatMode = originalMode; Directory.Delete(guardRoot, true); }
admin = true;
Check(await (Task<bool>)Invoke(plugin, "CanClearAsync", direct), "Host administrator must be allowed.");
admin = false; config.Permissions.ClearAdminOnly = false;
Check(await (Task<bool>)Invoke(plugin, "CanClearAsync", direct), "Disabled restriction must allow clearing personal conversations.");
config.Permissions.ClearAdminOnly = true; config.OwnerPrompt.Enabled = false;
Console.WriteLine("PASS: route-only access config, admin clear guard before mutation, optional owner template and no prompt-based privilege grants");

CollectibleSerializationChecks.Run();
await MemoryStreamingChecks.RunAsync();
await UsageChecks.RunAsync();
await DisabledToolCommandChecks.RunAsync();
{
    var imagePolicyPlugin = new AiChatPlugin();
    var imagePolicyConfig = new PluginConfig();
    Set(imagePolicyPlugin, "_config", imagePolicyConfig);
    var image = new ImagePart(null, "image/png", 3, [1, 2, 3]);
    var history = Enumerable.Range(0, 12).Select(i => new ChatTurn("user", [new TextPart($"消息{i}"), image], i)).ToList();
    var current = new ChatTurn("user", [new TextPart("当前"), image], 99);
    var first = imagePolicyPlugin.BuildVisionMessages("stable", history, current, true);
    Check(first.Take(history.Count + 2).All(message => !message.HasImage) && first[^1].HasImage,
        "History images must be absent from the stable text prefix.");
    Check(first.SelectMany(message => message.Segments).Count(segment => segment.Kind == SegmentKind.Image) == 1,
        "By default only current/quoted images are resent.");
    imagePolicyConfig.History.ImageMemoryTurns = 12;
    imagePolicyConfig.History.MaxImagesPerRequest = 2;
    var expanded = imagePolicyPlugin.BuildVisionMessages("stable", history, current, true);
    Check(expanded.SelectMany(message => message.Segments).Count(segment => segment.Kind == SegmentKind.Image) == 2,
        "Image memory must obey the request cap.");
    for (var i = 0; i < first.Count - 1; i++)
        Check(JsonSerializer.Serialize(first[i]) == JsonSerializer.Serialize(expanded[i]),
            "Changing or expiring selected images must not change the preceding request bytes.");
    Check(!imagePolicyPlugin.BuildVisionMessages("stable", history, current, false).Any(message => message.HasImage),
        "Non-vision fallback must never receive image payloads.");
    Console.WriteLine("PASS: image eviction, bounded tail payload and stable text prefix");
}
await CacheStabilityChecks.RunAsync();
await RichReplyChecks.RunAsync();
await ReplyModeChecks.RunAsync();
await MemoryNotebookChecks.RunAsync();
await TimerRepeatChecks.RunAsync();
await TimerLimitChecks.RunAsync();
await TimerUpdateChecks.RunAsync();
ProactiveChecks.Run();
await ProactiveJudgeChecks.RunAsync();
await ProactiveChecks.RunHistoryAsync();
await McpChecks.RunAsync();
await RecallChecks.RunAsync();
await FetchTimerChecks.RunAsync();
await ToolLogChecks.RunAsync();
await ModelApiChecks.RunAsync();
await ImageGenerationChecks.RunAsync();
await ImageResponseMemoryChecks.RunAsync();
await ImageEditQuoteChecks.RunAsync();
await IdentityScopeChecks.RunAsync();

// Plugin tools: discovered through the ChatTools contract, called with the model's arguments and the conversation,
// after-reply actions kept for after the text reply.
{
    var services = new FakePluginServices();
    services.Add("chat.tool.send_meme", "MemePlugin",
        new ChatToolInfo("Send a meme.", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } } },
            "Use memes sparingly."),
        request =>
        {
            services.LastCall = ShiroBot.SDK.Plugin.PluginJson.Deserialize<ChatToolCall>(request);
            return ShiroBot.SDK.Plugin.PluginJson.Serialize(new ChatToolResult("selected", new ChatAfterReply("send_meme", new JsonObject { ["id"] = "m1" })));
        });
    services.Add("chat.tool.web_fetch", "Other", new ChatToolInfo("Clashes with a built-in."), _ => []);
    services.Add("chat.tool.bad name", "Other", new ChatToolInfo("Invalid name."), _ => []);
    services.Add("chat.tool.no_description", "Other", new ChatToolInfo(""), _ => []);
    var toolBot = DispatchProxy.Create<IBotContext, TestProxy>();
    ((TestProxy)(object)toolBot).Call = (method, _) => method.Name == "get_Services" ? services : throw new NotSupportedException(method.Name);
    var chat = new ChatToolContext("qq", "test", "Group", "g1", "m1", "u1", true, false);

    var tools = PluginChatTool.Discover(toolBot, chat, new HashSet<string> { "web_fetch" }, []);
    Check(tools is [{ Definition.Function.Name: "send_meme", SystemPrompt: "Use memes sparingly.", MaxCallsPerTurn: 1 }],
        "Plugin tools must skip clashing, invalid and undescribed tools.");
    Check(tools[0].Definition.Function.Description == "[MemePlugin] Send a meme.", "Plugin tool descriptions name their provider.");
    Check(PluginChatTool.Discover(toolBot, chat, new HashSet<string>(), ["SEND_MEME"]).All(tool => tool.Definition.Function.Name != "send_meme"),
        "Disabled plugin tools must not be offered.");
    var result = await tools[0].ExecuteAsync("""{"query":"猫"}""");
    Check(result == "selected" && services.LastCall is { Context.ChannelId: "g1", Context.IsGroup: true } &&
          (string?)services.LastCall.Arguments["query"] == "猫",
        "Plugin tools must pass the model's arguments and the conversation context.");
    Check(tools[0].FollowUps is [{ Name: "send_meme" } followUp] && (string?)followUp.Arguments?["id"] == "m1",
        "Plugin tools must keep after-reply actions.");
    var saveRequest = new MessageEvent { Platform = "qq", InstanceId = "test", MessageId = "save-request",
        Channel = Channel.Group("g1"), Sender = new User("u1"), Timestamp = DateTimeOffset.UtcNow, Segments = [] };
    var priorImage = saveRequest with { MessageId = "image", Timestamp = saveRequest.Timestamp.AddSeconds(-10),
        Segments = [new ImageSegment("https://example.invalid/image.png")] };
    Check(AiChatPlugin.RecentSaveImage(saveRequest, [priorImage]) == priorImage,
        "Separate save request resolves the same sender's recent image.");
    Check(AiChatPlugin.RecentSaveImage(saveRequest, [priorImage with { Sender = new User("other") },
        priorImage with { InstanceId = "other" }, priorImage with { Channel = Channel.Group("other") },
        priorImage with { Timestamp = saveRequest.Timestamp.AddMinutes(-3) }]) is null,
        "Recent save images exclude other senders, instances, channels and expired context.");
    var imageBytes = new byte[] { 1, 2, 3 };
    var imageArgs = new JsonObject { ["image_ids"] = new JsonArray(2) };
    var snapshots = await AiChatPlugin.ResolvePluginImagesAsync(imageArgs,
        [new ImagePart(null, "image/png", 1, [9]), new ImagePart(null, "image/png", 3, imageBytes)], 1, default);
    Check(snapshots.Count == 1 && snapshots[0].Id == 2 && Convert.FromBase64String(snapshots[0].Base64).SequenceEqual(imageBytes),
        "Plugin tools select exact historical image bytes instead of refetching a URL.");
    var current = await AiChatPlugin.ResolvePluginImagesAsync(new JsonObject(),
        [new ImagePart(null, "image/png", 1, [9]), new ImagePart(null, "image/png", 3, imageBytes)], 1, default);
    Check(current.Count == 1 && current[0].Id == 1, "Default plugin images include current/quoted attachments only.");
    try
    {
        await AiChatPlugin.ResolvePluginImagesAsync(new JsonObject { ["image_ids"] = new JsonArray(99) }, [], 0, default);
        throw new Exception("Invalid historical image IDs were accepted.");
    }
    catch (ArgumentException) { }
    tools[0].ResolveImages = (_, _) => Task.FromResult(snapshots);
    await tools[0].ExecuteAsync("{}");
    Check(services.LastCall?.Context.Images is [{ Id: 2, Base64: "AQID" }],
        "Image snapshots survive the real plugin tool JSON boundary.");
    services.Handlers.Clear();
    Check((await tools[0].ExecuteAsync("{}")).Contains("not available", StringComparison.Ordinal),
        "A provider that went away must turn into a text reply.");
    Console.WriteLine("PASS: plugin tool discovery, calls, after-reply actions and unavailable providers");
}

sealed class FakePluginServices : IPluginServices
{
    public Dictionary<string, (string Provider, byte[] Metadata)> Infos { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Func<byte[], byte[]>> Handlers { get; } = new(StringComparer.Ordinal);
    public ChatToolCall? LastCall { get; set; }

    public void Add(string name, string provider, ChatToolInfo info, Func<byte[], byte[]> handler)
    {
        Infos[name] = (provider, ShiroBot.SDK.Plugin.PluginJson.Serialize(info));
        Handlers[name] = handler;
    }

    public IReadOnlyList<PluginServiceInfo> Find(string prefix) =>
    [
        .. Infos.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new PluginServiceInfo(pair.Key, pair.Value.Provider, null) { MetadataUtf8 = pair.Value.Metadata })
    ];

    public Task<ReadOnlyMemory<byte>> CallUtf8Async(string name, ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default) =>
        Handlers.TryGetValue(name, out var handler)
            ? Task.FromResult<ReadOnlyMemory<byte>>(handler(request.ToArray()))
            : throw new PluginServiceUnavailableException(name);
}

sealed class StubHandler : HttpMessageHandler
{
    public List<JsonElement> Requests { get; } = [];
    public string RespondInitial { get; }
    public Func<JsonElement, string> Respond { get; set; }
    public StubHandler(Func<JsonElement, string> respond)
    {
        Respond = respond;
        using var doc = JsonDocument.Parse("{\"model\":\"primary\"}");
        RespondInitial = respond(doc.RootElement);
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        Requests.Add(doc.RootElement.Clone());
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Respond(doc.RootElement), Encoding.UTF8, "application/json") };
    }
}
sealed class FakeTool(string name) : IChatTool
{
    public int Calls { get; private set; }
    public string? LastArguments { get; private set; }
    public OpenAiTool Definition => new() { Function = new() { Name = name, Description = name, Parameters = new { type = "object" } } };
    public string SystemPrompt => name;
    public int MaxCallsPerTurn => 1;
    public Task<string> ExecuteAsync(string arguments, CancellationToken ct = default) { Calls++; LastArguments = arguments; return Task.FromResult(name + " result"); }
    public void Dispose() { }
}
public class TestProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
}
