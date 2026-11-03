using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Usage;

internal static class UsageChecks
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    private static readonly ModelEntry Model = new() { Name = "test", BaseUrl = "https://example.invalid/v1", ApiKey = "test" };

    private sealed class Upstream(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = await request.Content!.ReadAsStringAsync(ct);
            return respond(LastBody);
        }
    }

    private static OpenAiClient Client(Upstream upstream, UsageStats stats, bool streamUsage = true)
    {
        var client = new OpenAiClient(10, "", stats, streamUsage);
        var clients = (Dictionary<string, HttpClient>)typeof(OpenAiClient).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
        clients["https://example.invalid/v1"] = new HttpClient(upstream);
        return client;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static TokenUsage? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return TokenUsage.TryParse(doc.RootElement);
    }

    private static List<OpenAiMessage> Messages(params (string Role, string Text)[] messages) =>
        messages.Select(message => new OpenAiMessage { Role = message.Role, Content = message.Text }).ToList();

    public static async Task RunAsync()
    {
        // 各家 usage 字段。
        Check(Parse("""{"prompt_tokens":1000,"completion_tokens":20,"prompt_cache_hit_tokens":900,"prompt_cache_miss_tokens":100}""")
            == new TokenUsage(1000, 20, 900), "DeepSeek cache hit tokens must be read.");
        Check(Parse("""{"prompt_tokens":1000,"completion_tokens":20,"prompt_cache_miss_tokens":250}""") == new TokenUsage(1000, 20, 750),
            "DeepSeek miss tokens alone must give the cached tokens.");
        Check(Parse("""{"prompt_tokens":500,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":384}}""") == new TokenUsage(500, 5, 384),
            "OpenAI cached_tokens must be read.");
        Check(Parse("""{"prompt_tokens":500,"completion_tokens":5}""") == new TokenUsage(500, 5, null),
            "A response without cache information must not be treated as a miss.");
        Console.WriteLine("PASS: UsageFieldsFromCommonProviders");

        var root = Path.Combine(Path.GetTempPath(), "aichat-usage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stats = new UsageStats(Path.Combine(root, "usage-stats.json"));

            // 流式：请求附带 include_usage，最后一个 chunk 的 usage 被记录，并带上会话和用途。
            var stream = new Upstream(_ => Sse(
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"}}]}\n\n" +
                "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1200,\"completion_tokens\":3,\"prompt_cache_hit_tokens\":1024}}\n\n" +
                "data: [DONE]\n\n"));
            using (var client = Client(stream, stats))
            using (UsageScope.Begin("群 1", "chat"))
            {
                var text = await client.ChatIncrementalAsync(Model, new OpenAiChatRequest { Model = "test", Messages = Messages(("system", "s"), ("user", "u")) },
                    _ => Task.CompletedTask);
                Check(text == "hi", "Streaming text must still be returned.");
                using var body = JsonDocument.Parse(stream.LastBody);
                Check(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean(),
                    "Streaming requests must ask for usage.");
            }
            var recorded = stats.Recent(1).Single();
            Check(recorded is { Source: "群 1", Purpose: "chat", PromptTokens: 1200, CompletionTokens: 3, CachedTokens: 1024, Change: "首次" },
                "Streaming usage must be recorded with its source and purpose.");
            Console.WriteLine("PASS: StreamingUsageIsRequestedAndRecorded");

            // 这个上游的 usage 没有缓存字段：计入请求和 token，但不计入命中率。
            var noCache = new Upstream(_ => Sse(
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":400,\"completion_tokens\":1}}\n\n" +
                "data: [DONE]\n\n"));
            using (var client = Client(noCache, stats, streamUsage: false))
            {
                await client.ChatIncrementalAsync(Model, new OpenAiChatRequest { Model = "test", Messages = Messages(("user", "u")) }, _ => Task.CompletedTask);
                using var body = JsonDocument.Parse(noCache.LastBody);
                Check(!body.RootElement.TryGetProperty("stream_options", out _), "stream_usage = false must not send stream_options.");
            }
            Console.WriteLine("PASS: StreamUsageCanBeDisabled");

            // 非流式：普通 JSON 的 usage。
            var json = new Upstream(_ => Json(
                """{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":800,"completion_tokens":2,"prompt_tokens_details":{"cached_tokens":512}}}"""));
            using (var client = Client(json, stats))
            using (UsageScope.Begin("私聊 2", "compact"))
            {
                await client.ChatAsync(new DefaultSection(), Model, new OpenAiChatRequest { Model = "test", Messages = Messages(("user", "u")) });
                using var body = JsonDocument.Parse(json.LastBody);
                Check(!body.RootElement.TryGetProperty("stream_options", out _), "Non-streaming requests must not send stream_options.");
            }
            Check(stats.Recent(1).Single() is { Source: "私聊 2", Purpose: "compact", CachedTokens: 512, Change: "" },
                "Independent requests must be recorded without prefix comparison.");
            var today = DateOnly.FromDateTime(DateTime.Now);
            var total = stats.Total(today);
            Check(total.Requests == 3 && total.CachedTokens == 1536 && total.CacheKnownPromptTokens == 2000,
                "Requests without cache information must not count toward the cache rate.");
            Console.WriteLine("PASS: JsonUsageAndCacheRate");

            // 持久化。
            await stats.SaveAsync();
            var loaded = new UsageStats(Path.Combine(root, "usage-stats.json"));
            await loaded.LoadAsync();
            var reloaded = loaded.Total(today);
            Check(reloaded.Requests == total.Requests && reloaded.CachedTokens == total.CachedTokens && loaded.Recent(10).Count == 3,
                "Usage must survive a reload.");
            Console.WriteLine("PASS: UsagePersists");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        // 前缀变化的判断。
        RequestShape Shape(string system, IEnumerable<string> tools, params string[] turns)
        {
            var messages = Messages(("system", system));
            messages.AddRange(turns.Select((text, i) => new OpenAiMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = text }));
            return RequestShape.Of(new OpenAiChatRequest
            {
                Model = "test", Messages = messages,
                Tools = tools.Select(name => new OpenAiTool { Function = new OpenAiToolFunction { Name = name, Description = name, Parameters = new { } } }).ToList()
            });
        }
        var first = Shape("system", ["a"], "u1 [上下文]");
        Check(Shape("system", ["a"], "u1", "a1", "u2 [上下文]").CompareWith(first).Change == "仅追加",
            "The next turn replaces only the request-only context of the last message.");
        Check(Shape("system changed", ["a"], "u1", "a1", "u2").CompareWith(first).Change == "system 变化", "A changed system prompt must be reported.");
        Check(Shape("system", ["a", "b"], "u1", "a1", "u2").CompareWith(first).Change == "工具定义变化", "Changed tools must be reported.");
        var longer = Shape("system", ["a"], "u1", "a1", "u2", "a2", "u3");
        Check(Shape("system", ["a"], "u2", "a2", "u3", "a3", "u4").CompareWith(longer).Change == "第 1 条起变化",
            "Dropping the oldest history must be reported as a rewritten prefix.");
        Console.WriteLine("PASS: PrefixChangesAreClassified");
    }
}
