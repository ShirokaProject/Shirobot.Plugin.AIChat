using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.Memory;
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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"index":0,"message":{"role":"assistant","content":"好的"}}],"usage":{"prompt_tokens":10,"completion_tokens":1}}""",
                    Encoding.UTF8, "application/json")
            };
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
            Check(!System(first).Contains("[记忆本]\n") && LastUser(upstream.Requests[1]).Contains("alice 喜欢猫"),
                "The memory notebook must be sent with the current user message, not in the system prompt.");
            Check(!System(first).Contains("当前没有可用历史图片") && LastUser(first).Contains("当前没有可用历史图片"),
                "The history image catalog must be sent with the current user message, not in the system prompt.");
            Console.WriteLine("PASS: system prompt and tool definitions stay identical across speakers, memory changes and proactive replies");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
