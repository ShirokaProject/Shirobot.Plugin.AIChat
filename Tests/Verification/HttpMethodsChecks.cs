using System.Net;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Tools;

internal static class HttpMethodsChecks
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static async Task RunAsync()
    {
        var count = 0;
        using var handler = new Handler(async request => {
            count++;
            Check(request.Headers.GetValues("X-Test").Single() == "test", "Custom header lost.");
            if (request.Method == HttpMethod.Post)
            {
                Check(request.Content!.Headers.ContentType!.MediaType == "application/json", "JSON MIME lost.");
                Check(JsonDocument.Parse(await request.Content.ReadAsStringAsync()).RootElement.GetProperty("value").GetInt32() == 7, "JSON body lost.");
            }
            if (request.Method == HttpMethod.Put) Check(await request.Content!.ReadAsStringAsync() == "hello", "Raw body lost.");
            if (request.Method == HttpMethod.Patch) Check(await request.Content!.ReadAsStringAsync() == "q=a+b%26c", "Form encoding wrong.");
            var result = new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK) { Content = new StringContent("<h1>hello</h1>", Encoding.UTF8, "text/html") };
            result.Headers.Add("X-Result", "ok"); return result;
        });
        using var fetch = new HttpFetchTool(new(), handler: handler);
        foreach (var method in new[] { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" })
        {
            var args = new Dictionary<string, object> { ["url"] = "https://example.com/api", ["method"] = method, ["headers"] = new Dictionary<string,string> { ["X-Test"] = "test" } };
            if (method == "POST") args["json"] = new { value = 7 };
            if (method == "PUT") args["body"] = "hello";
            if (method == "PATCH") args["form"] = new Dictionary<string,string> { ["q"] = "a b&c" };
            using var json = JsonDocument.Parse(await fetch.ExecuteAsync(PluginJson.Serialize(args)));
            Check(json.RootElement.GetProperty("headers").GetProperty("X-Result").GetString() == "ok", "Response headers lost.");
            Check(json.RootElement.GetProperty("text").GetString() == (method is "HEAD" or "DELETE" ? "" : method == "GET" ? "hello" : "<h1>hello</h1>"), "Body handling wrong.");
            Check(fetch.IsConcurrencySafe(PluginJson.Serialize(args)) == (method is "GET" or "HEAD" or "OPTIONS"), "Write call marked parallel.");
        }
        var before = count;
        foreach (var args in new[] {
            "{\"url\":\"https://example.com\",\"method\":\"TRACE\"}",
            "{\"url\":\"https://example.com\",\"method\":\"GET\",\"body\":\"no\"}",
            "{\"url\":\"https://example.com\",\"headers\":{\"Host\":\"localhost\"}}",
            "{\"url\":\"https://example.com\",\"headers\":{\"X-Test\":\"x\\r\\ny\"}}",
            "{\"url\":\"https://example.com\",\"method\":\"POST\",\"json\":{},\"body\":\"x\"}" })
            await fetch.ExecuteAsync(args);
        Check(count == before, "Invalid request reached transport.");
        using var registry = new ChatToolRegistry();
        var outputs = await registry.ExecuteCallsAsync([new() { Id = "write", Function = new() { Name = "http_fetch", Arguments = "{\"url\":\"https://example.com\",\"method\":\"POST\"}" } }],
            new Dictionary<string, IChatTool> { ["http_fetch"] = fetch }, new(), "test", 1, default, readOnlyOnly: true);
        Check(((string)outputs[0].Content).Contains("read-only") && count == before, "Read-only task allowed a write.");

        foreach (var status in new[] { HttpStatusCode.Redirect, HttpStatusCode.SeeOther, HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect })
        {
            var hops = 0;
            using var redirects = new HttpFetchTool(new(), handler: new Handler(async request => {
                hops++;
                if (hops == 1) { var r = new HttpResponseMessage(status); r.Headers.Location = new Uri("/next", UriKind.Relative); return r; }
                var expected = status is HttpStatusCode.Redirect or HttpStatusCode.SeeOther ? "GET" : "POST";
                Check(request.Method.Method == expected, "Redirect changed method incorrectly.");
                Check(expected == "GET" ? request.Content is null : await request.Content!.ReadAsStringAsync() == "payload", "Redirect replay body incorrect.");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            }));
            await redirects.ExecuteAsync("{\"url\":\"https://example.com/start\",\"method\":\"POST\",\"body\":\"payload\"}");
            Check(hops == 2, "Redirect did not complete.");
        }
        var crossed = 0;
        using var cross = new HttpFetchTool(new(), handler: new Handler(request => {
            crossed++; var r = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect); r.Headers.Location = new Uri("https://other.example/api"); return Task.FromResult(r);
        }));
        try { await cross.ExecuteAsync("{\"url\":\"https://example.com\",\"method\":\"POST\",\"body\":\"secret\"}"); throw new Exception("Cross origin write allowed."); }
        catch (InvalidOperationException) { }
        Check(crossed == 1, "Write body leaked across origins.");
        using var errors = new HttpFetchTool(new(), handler: new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"bad field\"}", Encoding.UTF8, "application/problem+json") })));
        using var error = JsonDocument.Parse(await errors.ExecuteAsync("{\"url\":\"https://example.com\",\"method\":\"POST\"}"));
        Check(error.RootElement.GetProperty("status").GetInt32() == 400 && error.RootElement.GetProperty("text").GetString()!.Contains("bad field"), "API error body lost.");
        Console.WriteLine("PASS: seven HTTP methods, JSON/form/raw bodies, headers/status, invalid arguments, read-only restrictions, redirect semantics, cross-origin writes and error bodies");
    }
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
