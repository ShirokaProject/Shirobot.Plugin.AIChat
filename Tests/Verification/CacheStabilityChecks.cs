using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.Tools.Memory;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Proactive;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

/// <summary>
/// 同一会话的相邻请求，system prompt 和工具定义必须逐字节相同，上游的前缀缓存才能命中：
/// 换人说话、记忆本变化、主动插话都不能改动它们。
/// </summary>
internal static class CacheStabilityChecks
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    private sealed class Upstream : HttpMessageHandler
    {
        public List<JsonDocument> Requests { get; } = [];
        public Func<JsonElement, (HttpStatusCode Status, string Text)> Reply { get; set; } = _ => (HttpStatusCode.OK, "好的");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Requests.Add(body);
            var (status, text) = Reply(body.RootElement);
            var response = JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, message = new { role = "assistant", content = text } } },
                usage = new { prompt_tokens = 10, completion_tokens = 1 }
            });
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private static object? Default(Type type)
    {
        if (type == typeof(void)) return null;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var result = type.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result)
                .Invoke(null, [result.IsValueType ? Activator.CreateInstance(result) : null]);
        }
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aichat-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var admins = new HashSet<string>();
            var ctx = DispatchProxy.Create<IBotContext, TestProxy>();
            var msg = DispatchProxy.Create<IMessageContext, TestProxy>();
            var config = DispatchProxy.Create<IConfigContext, TestProxy>();
            ((TestProxy)(object)config).Call = (m, _) => m.Name == "get_ConfigPath" ? Path.Combine(root, "config.toml") : Default(m.ReturnType);
            ((TestProxy)(object)msg).Call = (m, _) => m.Name == "GetHistoryMessagesAsync"
                ? Task.FromResult<IReadOnlyList<MessageEvent>>([])
                : Default(m.ReturnType);
            ((TestProxy)(object)ctx).Call = (m, args) => m.Name switch
            {
                "get_Message" => msg,
                "get_Config" => config,
                "get_InstanceId" => "i",
                "get_Platform" => "qq",
                "IsAdmin" => args![0] is string id && admins.Contains(id),
                _ => Default(m.ReturnType)
            };

            var plugin = new AiChatPlugin();
            var settings = new PluginConfig();
            settings.Default.Model = "test";
            settings.Models = [new ModelEntry { Name = "test", BaseUrl = "https://example.invalid/v1", ApiKey = "test" }];
            settings.Default.DefaultChatMode = "shared";
            settings.ToolUse.Enabled = true;
            settings.ToolUse.Memory.Enabled = true;
            settings.ToolUse.TimerTool.Enabled = true;
            settings.ToolUse.ImageGen.Enabled = true;
            settings.ToolUse.ImageGen.Model = "test";
            settings.Proactive.Enabled = true;
            settings.ToolUse.PluginTools.Enabled = false;
            typeof(AiChatPlugin).GetField("_config", Flags)!.SetValue(plugin, settings);
            typeof(PluginBase).GetProperty("Context", Flags)!.SetValue(plugin, ctx);
            typeof(AiChatPlugin).GetMethod("ApplyConfigDerivatives", Flags)!.Invoke(plugin, [true]);

            var upstream = new Upstream();
            var client = (OpenAiClient)typeof(AiChatPlugin).GetField("_openAi", Flags)!.GetValue(plugin)!;
            ((Dictionary<string, HttpClient>)typeof(OpenAiClient).GetField("_clients", Flags)!.GetValue(client)!)["https://example.invalid/v1"] =
                new HttpClient(upstream);

            var process = typeof(AiChatPlugin).GetMethod("ProcessAiAsync", Flags)!;
            var resolveKey = typeof(AiChatPlugin).GetMethod("ResolveConversationKey", Flags)!;
            var group = Enum.Parse(resolveKey.GetParameters()[0].ParameterType, "Group");
            var t0 = DateTimeOffset.UtcNow;
            async Task Send(string id, string sender, string text, ProactiveRequest? proactive = null)
            {
                var message = new MessageEvent
                {
                    Platform = "qq", InstanceId = "i", SelfId = "bot", MessageId = id, Channel = Channel.Group("g1"),
                    Sender = new User(sender) { Name = sender }, Segments = [new TextSegment(text)], Timestamp = t0
                };
                var key = resolveKey.Invoke(plugin, [group, "g1", sender]);
                await (Task)process.Invoke(plugin, [message, key, null, true, proactive is null, proactive])!;
            }

            await Send("m1", "alice", "你好");
            // 记忆本变化、换成管理员说话、主动插话：都不能改动 system prompt 和工具定义。
            var memory = (MemoryNotebookStore)typeof(AiChatPlugin).GetField("_memory", Flags)!.GetValue(plugin)!;
            var sharedKey = (ConversationKey)resolveKey.Invoke(plugin, [group, "g1", "alice"])!;
            await memory.UpdateAsync(sharedKey, entries =>
            {
                entries.Add(new MemoryEntry("1", "alice 喜欢猫", DateTimeOffset.UtcNow));
                return (true, "ok");
            });
            admins.Add("bob");
            await Send("m2", "bob", "画只猫");
            await Send("m3", "carol", "大家在聊什么", new ProactiveRequest("i:g1", "[可以插话]"));

            Check(upstream.Requests.Count == 3, $"Expected three upstream requests, got {upstream.Requests.Count}.");
            string System(JsonDocument request) => request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            string Tools(JsonDocument request) => request.RootElement.TryGetProperty("tools", out var tools) ? tools.GetRawText() : "";
            string LastUser(JsonDocument request)
            {
                var messages = request.RootElement.GetProperty("messages");
                var content = messages[messages.GetArrayLength() - 1].GetProperty("content");
                return content.ValueKind == JsonValueKind.String
                    ? content.GetString()!
                    : string.Concat(content.EnumerateArray().Select(part => part.TryGetProperty("text", out var text) ? text.GetString() : ""));
            }
            var first = upstream.Requests[0];
            Check(Tools(first).Contains("timer_tool") && Tools(first).Contains("imagegen") && Tools(first).Contains("memory_note"),
                "The first request must list the enabled tools.");
            foreach (var later in upstream.Requests.Skip(1))
            {
                Check(System(later) == System(first), "The system prompt must not change between requests of one conversation.");
                Check(Tools(later) == Tools(first), "Tool definitions must not change between requests of one conversation.");
            }
            Check(!System(first).Contains("[记忆本：长期记忆条目") && LastUser(upstream.Requests[1]).Contains("[记忆本更新") &&
                LastUser(upstream.Requests[1]).Contains("#1 alice 喜欢猫"),
                "Memory changes after the snapshot must be sent with the current user message, not in the system prompt.");
            Check(!System(first).Contains("当前没有可用历史图片") && LastUser(first).Contains("当前没有可用历史图片"),
                "The history image catalog must be sent with the current user message, not in the system prompt.");
            // 图片目录和记忆本包在“程序附加参考”里，排在群聊记录之后也不会被当成某个群友发的内容。
            var tail = LastUser(upstream.Requests[1]);
            var header = tail.IndexOf(AiChatPlugin.ProgramReferenceHeader, StringComparison.Ordinal);
            Check(header >= 0 && tail.IndexOf("当前没有可用历史图片", StringComparison.Ordinal) > header &&
                tail.IndexOf("[记忆本更新", StringComparison.Ordinal) > header &&
                tail.IndexOf(AiChatPlugin.ProgramReferenceFooter, StringComparison.Ordinal) > tail.IndexOf("alice 喜欢猫", StringComparison.Ordinal) &&
                System(first).Contains("程序附加参考，都由程序自动添加"),
                "Program-attached catalogs and memory must be wrapped and explained as not sent by anyone.");
            Console.WriteLine("PASS: system prompt and tool definitions stay identical across speakers, memory changes and proactive replies");
            // 本次请求才附带的内容单独成最后一条；下一轮只换掉这一条，之前的消息（包括上一条用户消息）逐字节不变。
            for (var i = 1; i < upstream.Requests.Count; i++)
            {
                var previous = upstream.Requests[i - 1].RootElement.GetProperty("messages");
                var next = upstream.Requests[i].RootElement.GetProperty("messages");
                for (var j = 0; j < previous.GetArrayLength() - 1; j++)
                    Check(previous[j].GetRawText() == next[j].GetRawText(),
                        $"Request {i} must keep message {j} of the previous request byte-identical; only the request-only tail may change.");
            }
            Console.WriteLine("PASS: successive requests only replace the request-only tail message");

            settings.History.MaxTokens = 1000;
            settings.Default.RetryOnTimeoutMaxAttempts = 2;
            settings.Default.RetryOnTimeoutDelayMs = 100;
            var store = (ConversationStore)typeof(AiChatPlugin).GetField("_conversations", Flags)!.GetValue(plugin)!;
            var history = await store.AcquireAsync(sharedKey);
            var fileExcerpt = new string('文', 1600) + "文件末尾";
            try
            {
                history.Turns.InsertRange(0,
                [
                    new ChatTurn("user", [new FileTextPart("old.txt", 4800, fileExcerpt, false)], 1),
                    new ChatTurn("assistant", [new TextPart("old answer")], 2)
                ]);
                await store.RewriteAsync(history, throwOnFailure: true);
            }
            finally { store.Release(sharedKey); }

            var attempts = 0;
            upstream.Reply = request => request.TryGetProperty("tools", out _)
                ? (HttpStatusCode.OK, ++attempts == 1 ? "" : "好的")
                : (HttpStatusCode.BadRequest, "");
            await Send("m4", "alice", "继续");
            await Send("m5", "bob", "再继续");
            var chatRequests = upstream.Requests.Skip(3)
                .Where(request => request.RootElement.TryGetProperty("tools", out _))
                .Select(request => request.RootElement.GetProperty("messages")).ToArray();
            Check(chatRequests.Length == 3 && chatRequests[0].GetRawText() == chatRequests[1].GetRawText(),
                "Empty chat responses must retry with byte-identical history after compaction fails.");
            Check(chatRequests[0][1].GetRawText() == chatRequests[2][1].GetRawText() &&
                chatRequests[2][1].GetProperty("content").EnumerateArray().Any(part => part.GetProperty("text").GetString()!.Contains(fileExcerpt)),
                "Over-budget chat must keep the oldest user message and its complete excerpt across successive requests.");
            Console.WriteLine("PASS: failed compaction preserves over-budget history and identical retry payloads, including old file excerpts");

            // 被叫到名字的主动回复和被 @ 一样能用生图等工具，会拿到历史图片目录；自己插话时没有。
            upstream.Reply = _ => (HttpStatusCode.OK, "好的");
            await Send("m6", "alice", "露露帮我画只猫", new ProactiveRequest("i:g1", "[被叫到]") { Addressed = true });
            var addressedTail = LastUser(upstream.Requests[^1]);
            await Send("m7", "carol", "随便聊聊", new ProactiveRequest("i:g1", "[可以插话]"));
            var interjectTail = LastUser(upstream.Requests[^1]);
            Check(addressedTail.Contains("当前没有可用历史图片") || addressedTail.Contains("当前会话历史图片"),
                "A proactive reply that called the bot by name must receive the image catalog for imagegen.");
            Check(!interjectTail.Contains("当前没有可用历史图片") && !interjectTail.Contains("当前会话历史图片"),
                "An unprompted interjection must not receive the image catalog.");
            Console.WriteLine("PASS: proactive replies addressed by name may use action tools; interjections may not");

            // 主模型被上游内容审核拒绝时换备用模型；拒绝原文不发出、不进历史。
            const string refusalText = "The prompt could not be submitted. The prompt contains sensitive words that violate Google's Generative AI Prohibited Use policy.";
            settings.Models.Add(new ModelEntry { Name = "backup", BaseUrl = "https://example.invalid/v1", ApiKey = "test" });
            settings.Default.FallbackModel = "backup";
            typeof(AiChatPlugin).GetMethod("ApplyConfigDerivatives", Flags)!.Invoke(plugin, [true]);
            client = (OpenAiClient)typeof(AiChatPlugin).GetField("_openAi", Flags)!.GetValue(plugin)!;
            ((Dictionary<string, HttpClient>)typeof(OpenAiClient).GetField("_clients", Flags)!.GetValue(client)!)["https://example.invalid/v1"] =
                new HttpClient(upstream);
            var models = new List<string>();
            upstream.Reply = request =>
            {
                var name = request.GetProperty("model").GetString()!;
                models.Add(name);
                return (HttpStatusCode.OK, name == "test" ? refusalText : "备用模型的回复");
            };
            await Send("m8", "alice", "换个模型试试");
            Check(models.SequenceEqual(["test", "backup"]), $"A refused request must be retried once on the fallback model, got [{string.Join(", ", models)}].");
            var afterFallback = await store.AcquireAsync(sharedKey);
            List<ChatTurn> savedTurns;
            try { savedTurns = afterFallback.Turns.ToList(); }
            finally { store.Release(sharedKey); }
            Check(savedTurns[^1].Parts.OfType<TextPart>().Any(part => part.Text.Contains("备用模型的回复"))
                && !savedTurns.Any(turn => turn.Parts.OfType<TextPart>().Any(part => part.Text.Contains("could not be submitted"))),
                "The fallback reply must be saved and the refusal text must never reach history.");

            models.Clear();
            var turnsBefore = savedTurns.Count;
            upstream.Reply = request =>
            {
                models.Add(request.GetProperty("model").GetString()!);
                return (HttpStatusCode.OK, refusalText);
            };
            await Send("m9", "alice", "两个都拒绝");
            var afterBoth = await store.AcquireAsync(sharedKey);
            try { Check(afterBoth.Turns.Count == turnsBefore, "When every model refuses, the turn must not be saved."); }
            finally { store.Release(sharedKey); }
            Check(models.SequenceEqual(["test", "backup"]), "The fallback model must be tried exactly once when the main model refuses.");
            Console.WriteLine("PASS: upstream refusals retry on the fallback model and never reach history");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
