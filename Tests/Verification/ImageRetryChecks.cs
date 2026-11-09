using System.Net;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;

internal static class ImageRetryChecks
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static HttpResponseMessage Error(string param = "", string message = "invalid parameter", string code = "invalid_request_error") => new(HttpStatusCode.BadRequest) {
        Content = new StringContent(JsonSerializer.Serialize(new { error = new { param, message, code } }), Encoding.UTF8, "application/json") };
    public static async Task RunAsync()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1sAAAAASUVORK5CYII=");
        HttpResponseMessage Success() => new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(png) + "\"}]}") };
        var calls = 0;
        var config = new ImageGenerationSection { Quality = "max", Size = "1536x864" };
        using (var handler = new Handler(async (request, cancellationToken) => {
            calls++; using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(body.RootElement.GetProperty("prompt").GetString() == "original prompt", "Prompt changed during recovery.");
            if (calls == 1) return Error("quality", "unsupported quality");
            Check(body.RootElement.GetProperty("quality").GetString() == "high", "High quality fallback missing.");
            if (calls == 2) return Error("size", "invalid size");
            Check(body.RootElement.GetProperty("size").GetString() == "1536x1024", "Aspect ratio fallback failed.");
            return Success();
        }))
            Check((await ImageGenerationClient.GenerateAsync("https://example.invalid/v1", "test", "image-model", "original prompt", config, "", default, handler: handler)).SequenceEqual(png) && calls == 3, "Bounded recovery did not succeed.");
        Check(config.Quality == "max" && config.Size == "1536x864", "Recovery mutated persistent defaults.");

        calls = 0;
        using (var handler = new Handler(async (request, cancellationToken) => {
            calls++; using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (calls == 1) return Error(message: "Bad Request");
            Check(!body.RootElement.TryGetProperty("background", out _) && !body.RootElement.TryGetProperty("quality", out _) && !body.RootElement.TryGetProperty("output_format", out _), "Generic recovery did not remove optional defaults.");
            Check(body.RootElement.GetProperty("model").GetString() == "image-model", "Recovery changed the selected model.");
            return Success();
        })) await ImageGenerationClient.GenerateAsync("https://example.invalid/v1", "test", "image-model", "prompt", new(), "", default, handler: handler);
        Check(calls == 2, "Generic 400 did not retry once.");

        calls = 0;
        using (var handler = new Handler(async (request, cancellationToken) => {
            calls++;
            Check(request.RequestUri!.AbsolutePath == "/v1/images/edits", "Retry lost edit endpoint.");
            var parts = ((MultipartFormDataContent)request.Content!).ToArray();
            var image = parts.Single(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "image[]");
            Check((await image.ReadAsByteArrayAsync()).SequenceEqual(png), "Retry lost reference bytes.");
            if (calls == 1) return Error("background", "unknown background parameter");
            Check(parts.All(part => part.Headers.ContentDisposition!.Name!.Trim('"') != "background"), "Multipart unsupported field not omitted.");
            return Success();
        })) await ImageGenerationClient.GenerateAsync("https://example.invalid/v1", "test", "image-model", "prompt", new(), "", default, [new(png, "image/png")], handler);
        Check(calls == 2, "Edit request was not rebuilt for retry.");

        foreach (var (param, message, code, cfg, status) in new[] {
            ("", "GIF output is not enabled for this request.", "invalid_request_error", new ImageGenerationSection { EnableGif = true, OutputFormat = "gif" }, HttpStatusCode.BadRequest),
            ("output_format", "gif unsupported", "unsupported_parameter", new ImageGenerationSection { EnableGif = true, OutputFormat = "gif" }, HttpStatusCode.BadRequest),
            ("", "content policy violation", "content_policy_violation", new ImageGenerationSection(), HttpStatusCode.BadRequest),
            ("", "no permission", "permission_denied", new ImageGenerationSection(), HttpStatusCode.BadRequest),
            ("", "bad key", "authentication_error", new ImageGenerationSection(), HttpStatusCode.Unauthorized)
        })
        {
            calls = 0;
            using var handler = new Handler((_, _) => { calls++; var result = Error(param, message, code); result.StatusCode = status; return Task.FromResult(result); });
            try { await ImageGenerationClient.GenerateAsync("https://example.invalid/v1", "test", "image-model", "prompt", cfg, "", default, handler: handler); throw new Exception("Expected failure."); }
            catch (ImageGenerationException) { }
            Check(calls == 1, "Nonrepairable error retried or required output semantics changed.");
        }
        calls = 0;
        using (var handler = new Handler((_, _) => { calls++; return Task.FromResult(Error(calls switch { 1 => "background", 2 => "quality", _ => "size" })); }))
        {
            try { await ImageGenerationClient.GenerateAsync("https://example.invalid/v1", "test", "image-model", "prompt", new(), "", default, handler: handler); throw new Exception("Expected bounded failure."); }
            catch (ImageGenerationException) { }
            Check(calls == 3, "Recovery exceeded two retries.");
        }
        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        calls = 0;
        using (var handler = new Handler(async (request, cancellationToken) => {
            calls++;
            var parts = ((MultipartFormDataContent)request.Content!).ToArray();
            var image = parts.Single(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "image[]");
            var bytes = await image.ReadAsByteArrayAsync(cancellationToken);
            if (calls == 1)
            {
                Check(image.Headers.ContentType!.MediaType == "image/gif" && image.Headers.ContentDisposition!.FileName!.Trim('"').EndsWith(".gif") && bytes.SequenceEqual(gif), "GIF must first upload original bytes and correct MIME/extension.");
                return Error("images[0].image_url", "Invalid image data.", "invalid_image_file");
            }
            Check(image.Headers.ContentType!.MediaType == "image/png" && bytes.AsSpan().StartsWith(new byte[] {137,80,78,71}), "Rejected GIF did not fall back to PNG.");
            Check(parts.Any(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "output_format" && part.ReadAsStringAsync().Result == "png"), "Input fallback enabled GIF output.");
            return Success();
        })) await ImageGenerationClient.GenerateAsync("https://example.invalid/v1", "test", "image-model", "prompt", new(), "", default, [new(gif, "image/gif")], handler);
        Check(calls == 2, "Expected one original GIF upload and one PNG fallback.");
        Check(gif.AsSpan().StartsWith("GIF89a"u8), "Original GIF was mutated.");

        using var cancelled = new CancellationTokenSource(); calls = 0;
        using (var handler = new Handler((_, _) => { calls++; cancelled.Cancel(); return Task.FromResult(Error("quality")); }))
        {
            try { await ImageGenerationClient.GenerateAsync("https://example.invalid/v1", "test", "image-model", "prompt", new(), "", cancelled.Token, handler: handler); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            Check(calls == 1, "Canceled task retried.");
        }
        Console.WriteLine("PASS: silent bounded 400 recovery, defaults/size/quality compatibility, multipart reference preservation, cancellation, immutable config, and GIF/auth/policy preservation");
    }
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
