using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Tools;

internal static class UpstreamCompletionChecks
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static readonly ModelEntry Model = new() { Name = "test", BaseUrl = "https://example.invalid/v1", ApiKey = "test" };
    static string Chunk(string text, string? reason = null) => "data: " + JsonSerializer.Serialize(new {
        choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = reason } }
    }) + "\n\n";
    static string Json(string text, string? reason = "stop") => JsonSerializer.Serialize(new {
        choices = new[] { new { message = new { role = "assistant", content = text }, finish_reason = reason } }
    });
    static OpenAiClient Client(Handler handler)
    {
        var client = new OpenAiClient(10, "");
        ((Dictionary<string,HttpClient>)typeof(OpenAiClient).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!)[Model.BaseUrl!] = new HttpClient(handler);
        return client;
    }
    public static async Task RunAsync()
    {
        foreach (var streaming in new[] { false, true })
        {
            foreach (var text in new[] {
                Chunk("ordinary partial answer"),
                Chunk("partial") + "data: [DONE]\n\n",
                Chunk("partial", "length") + "data: [DONE]\n\n",
                Chunk("partial") + "data: {broken\n\n" + Chunk("", "stop") + "data: [DONE]\n\n"
            })
            {
                using var handler = new Handler(_ => new StringContent(text, Encoding.UTF8, "text/event-stream"));
                using var client = Client(handler);
                var failed = false;
                try
                {
                    if (streaming) await client.ChatWithToolsIncrementalAsync(Model, new() { Model = Model.Name }, _ => Task.CompletedTask);
                    else await client.ChatWithToolsAsync(Model, new() { Model = Model.Name });
                }
                catch (IncompleteUpstreamResponseException) { failed = true; }
                Check(failed, "Incomplete SSE was accepted, streaming=" + streaming);
            }
            using (var handler = new Handler(_ => new StringContent(Chunk("complete", "stop"), Encoding.UTF8, "text/event-stream")))
            using (var client = Client(handler))
            {
                var response = streaming ? await client.ChatWithToolsIncrementalAsync(Model, new() { Model = Model.Name }, _ => Task.CompletedTask) : await client.ChatWithToolsAsync(Model, new() { Model = Model.Name });
                Check(response.Content == "complete", "Explicit normal finish at EOF should remain compatible.");
            }
        }
        var forcedToolStream = "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"t1\",\"function\":{\"name\":\"first\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n";
        using (var handler = new Handler(_ => new StringContent(forcedToolStream, Encoding.UTF8, "text/event-stream")))
        using (var client = Client(handler))
            Check((await client.ChatWithToolsAsync(Model, new() { Model = Model.Name })).ToolCalls is [{ Id: "t1", Function.Name: "first", Function.Arguments: "{}" }], "Forced SSE fallback lost completed tool calls.");
        // Build a transport-complete response containing incomplete function arguments.
        var badArguments = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"bad\",\"function\":{\"name\":\"first\",\"arguments\":\"{\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n";
        using (var handler = new Handler(_ => new StringContent(badArguments, Encoding.UTF8, "text/event-stream")))
        using (var client = Client(handler))
        {
            var failed = false;
            try { await client.ChatWithToolsAsync(Model, new() { Model = Model.Name }); } catch (IncompleteUpstreamResponseException) { failed = true; }
            Check(failed, "Incomplete tool arguments accepted for execution.");
        }
        foreach (var payload in new[] { Json("partial", "length"), Json("partial", null), "{\"choices\":[{\"message\":{\"content\":\"cut" })
        {
            using var handler = new Handler(_ => new StringContent(payload, Encoding.UTF8, "application/json")); using var client = Client(handler);
            var failed = false;
            try { await client.ChatWithToolsAsync(Model, new() { Model = Model.Name }); } catch (IncompleteUpstreamResponseException) { failed = true; }
            Check(failed, "Truncated JSON or explicit incomplete finish reason accepted.");
        }
        using (var handler = new Handler(_ => new StringContent(Json("[chat"), Encoding.UTF8, "application/json")))
        using (var client = Client(handler))
        using (var registry = new ChatToolRegistry())
        {
            Check(await registry.ChatAsync(client, Model, [], 3) == "[chat" && handler.Requests.Count == 1,
                "Text content must not trigger special repair when upstream reports normal completion.");
        }
        var tools = "{\"choices\":[{\"message\":{\"tool_calls\":[{\"id\":\"t1\",\"function\":{\"name\":\"first\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}";
        using (var handler = new Handler(request => new StringContent(request switch {
            1 => tools,
            2 => Chunk("ordinary answer cut off halfway") + "data: [DONE]\n\n",
            _ => Json("complete ordinary answer")
        }, Encoding.UTF8, request == 2 ? "text/event-stream" : "application/json")))
        using (var client = Client(handler))
        using (var registry = new ChatToolRegistry(retryDelayMs: 100))
        {
            var tool = new FakeTool("first"); registry.Register(tool);
            Check(await registry.ChatAsync(client, Model, [], 3) == "complete ordinary answer", "Failed round did not recover.");
            Check(tool.Calls == 1 && handler.Requests.Count == 3 && handler.Requests[2].GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("role").GetString() == "tool"), "Retry replayed tools or lost tool results.");
            Check(handler.Requests[2].GetProperty("tools").GetArrayLength() == 1, "Retry changed tool definitions rather than retrying same request.");
        }
        using (var handler = new Handler(request => new StringContent(request == 1 ? tools : Chunk("partial"), Encoding.UTF8, request == 1 ? "application/json" : "text/event-stream")))
        using (var client = Client(handler))
        using (var registry = new ChatToolRegistry(requestAttempts: 2, retryDelayMs: 100))
        {
            var tool = new FakeTool("first"); registry.Register(tool);
            try { await registry.ChatAsync(client, Model, [], 3); throw new Exception("Expected terminal failure."); } catch (ToolRoundResponseFailedException) { }
            Check(tool.Calls == 1 && handler.Requests.Count == 3, "Exhausted retry replayed tools or exceeded request limit.");
        }
        Console.WriteLine("PASS: real/forced SSE completeness, malformed chunks, length/null finish, partial JSON, no tag-based heuristics, same-round retries and no tool replay");
    }
    sealed class Handler(Func<int,HttpContent> content) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); Requests.Add(json.RootElement.Clone());
            return new(HttpStatusCode.OK) { Content = content(Requests.Count) };
        }
    }
}
