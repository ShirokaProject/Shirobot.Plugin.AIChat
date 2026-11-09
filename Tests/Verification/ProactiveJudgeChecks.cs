using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Proactive;
using ShiroBot.AiChatPlugin.Usage;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

internal static class ProactiveJudgeChecks
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); }
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);

    private sealed class Upstream : HttpMessageHandler
    {
        public string Response = "[skip]";
        public bool Fail;
        public Action? BeforeResponse;
        public int Requests;
        public string LastBody = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            LastBody = await request.Content!.ReadAsStringAsync(ct);
            BeforeResponse?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (Fail) throw new HttpRequestException("fixture failure");
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content = Response } } },
                usage = new { prompt_tokens = 500, completion_tokens = 4 }
            }), Encoding.UTF8, "application/json") };
        }
    }

    public static async Task RunAsync()
    {
        var fresh = string.Join('\n', Enumerable.Range(0, 30).Select(i => $"msg-{i:D2}: " + new string('新', 1000)));
        var recent = string.Join('\n', Enumerable.Range(0, 100).Select(i => $"old-{i:D2}: " + new string('旧', 1000)));
        var request = ProactiveJudge.Request("fixture", new string('名', 10000), Enumerable.Repeat(new string('称', 1000), 100),
            recent, fresh, new string('规', 10000));
        Check(request.Messages.Count == 2 && request.Tools is null && !request.Stream && request.MaxCompletionTokens == 256,
            "Judgment must use two text messages, no tools, and a small output budget.");
        Check(request.Messages.All(m => m.Content is string) && request.Messages.Sum(m => ((string)m.Content).Length) < 8000,
            "Long policies, names, context and batches must fit the bounded judge input.");
        var user = (string)request.Messages[1].Content;
        Check(Enumerable.Range(0, 30).All(i => user.Contains($"msg-{i:D2}:")), "All messages in a valid batch must survive truncation.");
        Check(ProactiveJudge.BoundLines(string.Join('\n', Enumerable.Repeat("x", 10000)), 2000).Length <= 2000,
            "Even pathological line counts must respect the input cap.");
        Check(ProactiveJudge.ShouldReply(" [REPLY] \n") && !ProactiveJudge.ShouldReply("[skip]") &&
            !ProactiveJudge.ShouldReply("") && !ProactiveJudge.ShouldReply("[reply") &&
            !ProactiveJudge.ShouldReply("maybe [reply]") && !ProactiveJudge.ShouldReply("[reply] extra"),
            "Only an explicit, unambiguous reply marker may start a full response.");
        Check(ProactiveJudge.IsExplicitSkip(" [SKIP] ") && !ProactiveJudge.IsExplicitSkip("") &&
            !ProactiveJudge.IsExplicitSkip("[skip") && !ProactiveJudge.IsExplicitSkip("[skip] explanation"),
            "Malformed or missing decisions must be distinguishable from an explicit skip.");
        Console.WriteLine("PASS: proactive judge caps all text, preserves batch members and rejects ambiguous decisions");

        var root = Path.Combine(Path.GetTempPath(), "aichat-judge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var config = new PluginConfig();
        config.Default.Model = "fixture";
        config.Default.DefaultChatMode = "shared";
        config.Models = [new ModelEntry { Name = "fixture", BaseUrl = "https://example.invalid/v1", ApiKey = "fixture" }];
        config.Proactive = new ProactiveSection { Enabled = true, Groups = ["g1"], CooldownSeconds = 0, MinMessagesBetween = 0 };
        var plugin = new AiChatPlugin();
        Set(plugin, "_config", config);
        Set(plugin, "_userSettings", new ShiroBot.AiChatPlugin.UserState.UserSettingsStore(root));
        var gate = new SemaphoreSlim(1, 1); Set(plugin, "_globalConcurrency", gate);
        var stats = new UsageStats(Path.Combine(root, "usage.json"));
        var upstream = new Upstream();
        using var client = new OpenAiClient(10, "", stats);
        ((Dictionary<string, HttpClient>)typeof(OpenAiClient).GetField("_clients", Flags)!.GetValue(client)!)
            ["https://example.invalid/v1"] = new HttpClient(upstream);
        Set(plugin, "_openAi", client);
        ((Dictionary<string, string>)typeof(AiChatPlugin).GetField("_selfNames", Flags)!.GetValue(plugin)!)["qq:bot"] = "露露";
        var bot = DispatchProxy.Create<IBotContext, TestProxy>();
        var messages = DispatchProxy.Create<IMessageContext, TestProxy>();
        var historyCalls = 0;
        ((TestProxy)(object)bot).Call = (method, _) => method.Name switch
        {
            "get_InstanceId" => "i", "get_Platform" => "qq", "get_Message" => messages,
            _ => throw new Exception("Judge touched unrelated bot API: " + method.Name)
        };
        ((TestProxy)(object)messages).Call = (method, _) =>
        {
            if (method.Name != "GetHistoryMessagesAsync") throw new Exception("Judge attempted a resource fetch or group send: " + method.Name);
            historyCalls++;
            return Task.FromResult<IReadOnlyList<MessageEvent>>([]);
        };
        typeof(PluginBase).GetProperty("Context", Flags)!.SetValue(plugin, bot);
        var message = new MessageEvent { Platform = "qq", InstanceId = "i", SelfId = "bot", MessageId = "m1",
            Channel = Channel.Group("g1"), Sender = new User("alice") { Name = "alice" },
            Segments = [new TextSegment("今晚吃啥"), new ImageSegment("https://example.invalid/must-not-download.png")] };
        var run = typeof(AiChatPlugin).GetMethod("TryProactiveReplyAsync", Flags)!;
        async Task Run(CancellationToken ct = default) => await (Task)run.Invoke(plugin, [new[] { message }, null, ct])!;
        try
        {
            // Full chat stores and resource collectors are deliberately unset.
            await Run();
            Check(upstream.Requests == 1 && historyCalls == 1 && gate.CurrentCount == 1,
                "A skip must make only the small decision request and release concurrency.");
            Check(stats.Recent(1).Single() is { Purpose: "proactive_judge", Images: 0, Messages: 2, Tools: 0 },
                "Decisions must be accounted separately from full proactive replies.");
            using (var body = JsonDocument.Parse(upstream.LastBody))
            {
                Check(body.RootElement.GetProperty("messages").GetArrayLength() == 2 &&
                    !upstream.LastBody.Contains("must-not-download") && !body.RootElement.TryGetProperty("tools", out _),
                    "An image in the triggering event must not become a vision or tool request during judgment.");
            }
            upstream.Response = "unexpected"; await Run();
            upstream.Fail = true; await Run(); upstream.Fail = false;
            Check(upstream.Requests == 3 && gate.CurrentCount == 1, $"Invalid output and upstream failure must stay silent without leaking slots: requests={upstream.Requests}, slots={gate.CurrentCount}.");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); await Run(cancelled.Token);
                Check(gate.CurrentCount == 1, "Cancellation must release the judge slot.");
            }
            using (var interrupted = new CancellationTokenSource())
            {
                upstream.BeforeResponse = interrupted.Cancel;
                await Run(interrupted.Token);
                upstream.BeforeResponse = null;
                Check(interrupted.IsCancellationRequested && gate.CurrentCount == 1,
                    "A recall or sender follow-up during the model call must cancel without sending or holding a slot.");
            }
            await gate.WaitAsync();
            var count = upstream.Requests; await Run();
            Check(upstream.Requests == count && gate.CurrentCount == 0, "A busy host must defer judgment rather than queue another model call.");
            gate.Release();
            Console.WriteLine("PASS: proactive skip, invalid marker, failure, cancellation and busy-host paths avoid full history and resource collection");
        }
        finally { gate.Dispose(); Directory.Delete(root, true); }
    }
}
