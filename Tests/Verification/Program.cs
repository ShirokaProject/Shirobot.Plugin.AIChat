using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.AiChatPlugin.UserState;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Plugin;
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
    Check(answer == "tool answer" && first.Calls == 1 && second.Calls == 1, "Tools must dispatch independently and respect individual limits.");
    Check(handler.Requests[0].GetProperty("tools").GetArrayLength() == 2 && !handler.Requests[1].TryGetProperty("tools", out _), "Final round must omit tools.");
    var assistantWithTools = handler.Requests[1].GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "assistant");
    Check(assistantWithTools.GetProperty("reasoning_content").GetString() == "private reasoning payload" && !answer.Contains("private reasoning payload"),
        "JSON tool response reasoning must pass back unchanged without entering visible reply.");
    var results = handler.Requests[1].GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool").ToArray();
    Check(results.Length == 4 && results[2].GetProperty("content").GetString()!.Contains("limit") && results[3].GetProperty("content").GetString()!.Contains("Unknown"), "All calls need matching tool replies, including rejected calls.");
    Check(toolLog.Lines.Count(l => l.Contains("ToolUse 开始")) == 2 && toolLog.Lines.Count(l => l.Contains("ToolUse 完成")) == 2 &&
        toolLog.Lines.Count(l => l.Contains("ToolUse 拒绝")) == 2, "Every executed and rejected tool call must be logged independently.");
}
using (var tool = new TavilySearchTool(new TavilySection(), ""))
    Check((await tool.ExecuteAsync("{\"query\":42}")).Contains("invalid"), "Non-string query must return a validation error.");
Console.WriteLine("PASS: two independent tools, call limits, unknown tools, final answer and argument validation");

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
Check((string)Invoke(plugin, "ApplyOwnerPrompt", "base prompt") == "base prompt", "Owner prompt must default off.");
config.OwnerPrompt.Enabled = true;
config.OwnerPrompt.Template = "主人是 789，QQ 123456，机器人使用 ShiroBot。{自定义文字}\n第二行";
var identity = (string)Invoke(plugin, "ApplyOwnerPrompt", "base prompt");
Check(identity == "base prompt\n" + config.OwnerPrompt.Template, "Owner text must be appended verbatim, including braces and newlines.");
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
await RichReplyChecks.RunAsync();
await ReplyModeChecks.RunAsync();
await MemoryNotebookChecks.RunAsync();
await TimerRepeatChecks.RunAsync();
await RecallChecks.RunAsync();
await FetchTimerChecks.RunAsync();
await ToolLogChecks.RunAsync();
await IdentityScopeChecks.RunAsync();

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
