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
    // 从没返回过缓存字段的模型：缺少字段仍视为未知。
    private static readonly ModelEntry NoCacheModel = new() { Name = "no-cache", BaseUrl = "https://example.invalid/v1", ApiKey = "test" };
    // 只在命中时才返回缓存字段的模型。
    private static readonly ModelEntry HitOnlyModel = new() { Name = "hit-only", BaseUrl = "https://example.invalid/v1", ApiKey = "test" };

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
                await client.ChatIncrementalAsync(NoCacheModel, new OpenAiChatRequest { Model = "test", Messages = Messages(("user", "u")) }, _ => Task.CompletedTask);
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

            // 只在命中时才返回缓存字段的上游：返回过一次之后，缺少字段按 0 命中计，命中率不会只按命中的请求算。
            var hitOnlyResponses = new Queue<string>([
                """{"prompt_tokens":1000,"completion_tokens":1}""",
                """{"prompt_tokens":1000,"completion_tokens":1,"prompt_tokens_details":{"cached_tokens":600}}""",
                """{"prompt_tokens":1000,"completion_tokens":1}"""]);
            var hitOnly = new Upstream(_ => Json(
                "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}],\"usage\":" + hitOnlyResponses.Dequeue() + "}"));
            using (var client = Client(hitOnly, stats))
                for (var i = 0; i < 3; i++)
                    await client.ChatAsync(new DefaultSection(), HitOnlyModel, new OpenAiChatRequest { Model = "hit-only", Messages = Messages(("user", "u")) });
            var hitOnlyRecords = stats.Recent(3).Reverse().Select(record => record.CachedTokens).ToArray();
            Check(hitOnlyRecords.SequenceEqual(new int?[] { null, 600, 0 }),
                "A miss after the upstream has reported cache tokens must count as 0, before that as unknown: " + string.Join(",", hitOnlyRecords));
            var hitOnlyTotal = stats.GroupBy(today, key => key.Model).Single(row => row.Name == "hit-only").Totals;
            Check(hitOnlyTotal.CacheKnownPromptTokens == 2000 && hitOnlyTotal.CachedTokens == 600 && hitOnlyTotal.CacheRate == 0.3,
                "The cache rate must include misses once the upstream reports cache tokens.");
            Console.WriteLine("PASS: HitOnlyCacheFieldsCountMissesAsZero");

            // 持久化。
            await stats.SaveAsync();
            var loaded = new UsageStats(Path.Combine(root, "usage-stats.json"));
            await loaded.LoadAsync();
            var reloaded = loaded.Total(today);
            var saved = stats.Total(today);
            Check(reloaded.Requests == saved.Requests && reloaded.CachedTokens == saved.CachedTokens && reloaded.CacheKnownPromptTokens == saved.CacheKnownPromptTokens,
                "Usage must survive a reload.");
            // 哪些上游返回过缓存字段也随文件保存：重启后缺少字段仍按 0 计。
            var reporters = typeof(UsageStats).GetField("_cacheReporters", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var known = ((HashSet<string>)reporters.GetValue(loaded)!).Single(key => key.EndsWith("\nhit-only", StringComparison.Ordinal));
            Check(loaded.ResolveCachedTokens(known, null) == 0 && loaded.ResolveCachedTokens("unknown\nmodel", null) is null,
                "Which upstreams report cache tokens must survive a reload.");
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
        await LocalPrefixesAsync();
    }
    private static async Task LocalPrefixesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aichat-local-prefix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var stats = new UsageStats(Path.Combine(root, "stats.json"));
            var withUsage = false;
            var upstream = new Upstream(_ => Json(withUsage
                ? """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1000,"completion_tokens":3,"prompt_tokens_details":{"cached_tokens":0}}}"""
                : """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}"""));
            using var client = Client(upstream, stats);
            const string secretText = "Private prompt must never be persisted";
            OpenAiChatRequest Request(bool append = false, string schema = "string") => new()
            {
                Model = "test", Messages = append
                    ? Messages(("system", secretText), ("user", "first"), ("assistant", "ok"), ("user", "next"))
                    : Messages(("system", secretText), ("user", "first")),
                Tools = [new OpenAiTool { Function = new OpenAiToolFunction
                {
                    Name = "lookup", Description = "lookup",
                    Parameters = new { type = "object", properties = new { query = new { type = schema } } }
                } }]
            };
            async Task Send(OpenAiChatRequest request, string source = "one / 群 1", ModelEntry? model = null)
            {
                using var scope = UsageScope.Begin(source, "chat");
                await client.ChatAsync(new DefaultSection(), model ?? Model, request);
            }
            var first = Request(); await Send(first);
            Check(stats.Recent(1).Single() is { UsageReported: false, LocalInputBytes: > 0, LocalReusableBytes: 0, CachedTokens: null },
                "Missing usage must still record a local first-request baseline, with upstream fields unknown.");
            var next = Request(append: true); await Send(next);
            var row = stats.Recent(1).Single();
            Check(row.LocalReusableBytes == next.SentPrefix!.CommonBytes(first.SentPrefix) && row.LocalReusableBytes > 0 && row.LocalReusableBytes < row.LocalInputBytes,
                "Appended history must reuse the actual shared serialized prefix, including tools.");
            withUsage = true;
            var identical = Request(append: true); identical.Temperature = 0.2; identical.MaxCompletionTokens = 42;
            await Send(identical);
            row = stats.Recent(1).Single();
            Check(row.LocalReusableBytes == row.LocalInputBytes && row.CachedTokens == 0 && row.UsageReported,
                "Unchanged prompt bytes must be locally reusable even if upstream reports zero cache hits; sampling controls are excluded.");
            var total = stats.Total(today);
            Check(total.Requests == 3 && total.UsageRequests == 1 && total.LocalRequests == 3 && total.PromptTokens == 1000 && total.CacheRate == 0 &&
                total.ComparedLocalRate == 1 && total.ComparedCacheRate == 0 && total.LocalRate > 0 && total.LocalRate < 1,
                "Upstream and local metrics must use their own covered denominators and comparable cohort.");
            withUsage = false;
            await Send(Request(append: true, schema: "integer"));
            Check(stats.Recent(1).Single() is { LocalReusableBytes: 0, Change: "工具定义变化" },
                "Changing only a tool parameter schema must invalidate the prefix.");
            await Send(Request(append: true, schema: "integer"), "two / 群 1");
            Check(stats.Recent(1).Single().LocalReusableBytes == 0, "Instances must not share local prefix baselines.");
            await Send(Request(append: true, schema: "integer"), model: new ModelEntry { Name = "test", BaseUrl = Model.BaseUrl, ApiKey = "other-key" });
            Check(stats.Recent(1).Single().LocalReusableBytes == 0, "Different upstream credentials must not share local prefix baselines.");

            // 同一路径中的图片改变，或同长度的内联图片改变，都必须使该条消息开始的前缀失效。
            var imageFile = Path.Combine(root, "image.b64");
            await File.WriteAllTextAsync(imageFile, "data:image/png;base64,AAAA");
            OpenAiChatRequest ImageRequest(bool inline, byte value = 1) => new()
            {
                Model = "test", Messages = [new OpenAiMessage { Role = "system", Content = "image-test" },
                    new OpenAiMessage { Role = "user", Content = new ChatMessageDescriptor { Role = "user", Segments =
                        [inline ? new ContentSegment { Kind = SegmentKind.Image, InlineBytes = [value, 2, 3], Mime = "image/png" }
                            : new ContentSegment { Kind = SegmentKind.Image, B64FilePath = imageFile }] } }]
            };
            var image1 = ImageRequest(false); await Send(image1, "images");
            await File.WriteAllTextAsync(imageFile, "data:image/png;base64,BBBB");
            var image2 = ImageRequest(false); await Send(image2, "images");
            Check(stats.Recent(1).Single().LocalReusableBytes == image2.SentPrefix!.Points[1].Bytes,
                "Changing image bytes at the same path must only reuse the preceding system prefix.");
            var inline1 = ImageRequest(true); await Send(inline1, "inline");
            var inline2 = ImageRequest(true, 4); await Send(inline2, "inline");
            Check(stats.Recent(1).Single().LocalReusableBytes == inline2.SentPrefix!.Points[1].Bytes,
                "Different inline images of equal length must not be counted as identical.");
            var wire = upstream.LastBody;
            using (var doc = JsonDocument.Parse(wire))
                Check(!doc.RootElement.TryGetProperty("CapturePrefix", out _) && !doc.RootElement.TryGetProperty("SentPrefix", out _),
                    "Local diagnostics must not alter the upstream request schema.");
            await stats.SaveAsync();
            var persisted = await File.ReadAllTextAsync(Path.Combine(root, "stats.json"));
            Check(!persisted.Contains(secretText) && !persisted.Contains(imageFile), "Statistics must not persist prompt text or image paths.");
            var loaded = new UsageStats(Path.Combine(root, "stats.json")); await loaded.LoadAsync();
            Check(loaded.Total(today).LocalInputBytes == stats.Total(today).LocalInputBytes &&
                loaded.Total(today).UsageRequests == stats.Total(today).UsageRequests && loaded.Total(today).ComparedLocalRate == 1 &&
                loaded.Recent(1).Single().LocalReusableBytes == stats.Recent(1).Single().LocalReusableBytes,
                "Local aggregates, comparison cohorts and request rows must survive reload.");
            using (var freshClient = Client(new Upstream(_ => Json("""{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""")), loaded))
            using (UsageScope.Begin("inline", "chat"))
                await freshClient.ChatAsync(new DefaultSection(), Model, inline2);
            Check(loaded.Recent(1).Single().LocalReusableBytes == 0, "Reload must start with a fresh local baseline.");
            stats.Reset(); await Send(Request());
            Check(stats.Total(today).Requests == 1 && stats.Recent(1).Single().LocalReusableBytes == 0, "Reset must clear local baselines too.");

            var sseStats = new UsageStats(Path.Combine(root, "sse.json"));
            using (var sseClient = Client(new Upstream(_ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n")), sseStats, streamUsage: false))
            using (UsageScope.Begin("stream-scope", "chat"))
            {
                for (var i = 0; i < 2; i++)
                    await sseClient.ChatIncrementalAsync(Model, Request(), _ => Task.CompletedTask);
                Check(sseStats.Total(today).Requests == 2 && sseStats.Total(today).UsageRequests == 0 && sseStats.Recent(1).Single() is
                    { UsageReported: false, LocalInputBytes: > 0 } && sseStats.Recent(1).Single().LocalInputBytes == sseStats.Recent(1).Single().LocalReusableBytes,
                    "SSE without usage, even with stream_usage disabled, must record local reuse independently.");
            }
            using (UsageScope.Begin("independent", "compact"))
            {
                await client.ChatAsync(new DefaultSection(), Model, Request());
                await client.ChatAsync(new DefaultSection(), Model, Request());
                Check(stats.Recent(1).Single().LocalReusableBytes == 0, "Independent summaries must not borrow a conversation baseline.");
            }

            var legacyPath = Path.Combine(root, "legacy.json");
            await File.WriteAllTextAsync(legacyPath, "{\"version\":1,\"buckets\":[{\"day\":\"" + today.ToString("yyyy-MM-dd") + "\",\"requests\":2,\"prompt\":100}],\"recent\":[]}");
            var legacy = new UsageStats(legacyPath); await legacy.LoadAsync();
            Check(legacy.Total(today).UsageRequests == 2 && legacy.Total(today).LocalRate is null,
                "Legacy totals must remain usable without inventing historical local data.");
            Console.WriteLine("PASS: local wire-prefix reuse, missing usage, tool/image mutations, scope isolation, weighted comparison, persistence and reset");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

}
