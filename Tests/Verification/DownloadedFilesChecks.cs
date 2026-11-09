using System.Net;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Resources;
using ShiroBot.AiChatPlugin.Tools;

internal static class DownloadedFilesChecks
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aichat-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var files = new DownloadedFiles(() => directory);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1sAAAAASUVORK5CYII=");
            using var fetch = new HttpFetchTool(new(), handler: new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) })), downloads: files);
            var response = await fetch.ExecuteAsync("{\"url\":\"https://example.com/image\",\"response_mode\":\"file\",\"file_name\":\"../../photo.exe\"}");
            using var result = JsonDocument.Parse(response);
            var id = result.RootElement.GetProperty("file").GetProperty("file_id").GetString()!;
            Check(!response.Contains(directory) && !response.Contains(Convert.ToBase64String(png)), "Tool leaked local path or image data.");
            Check(result.RootElement.GetProperty("file").GetProperty("mime").GetString() == "image/png", "Image signature not detected.");
            var stored = files.Resolve(id);
            Check(stored.Info.file_name == "photo.png" && Path.GetDirectoryName(stored.Path) == directory && File.ReadAllBytes(stored.Path).SequenceEqual(png), "Unsafe filename or changed bytes.");
            var images = await files.ImagesAsync([id], default);
            Check(images.Single().InlineBytes!.SequenceEqual(png), "Downloaded reference bytes lost.");
            using var edit = new ImageGenerationTool((_, _, _) => throw new Exception("Reference incorrectly generated a new image."), editFiles: async (ids, _, options, token) => {
                var references = await files.ImagesAsync(ids, token);
                Check(references.Length == 1 && references[0].InlineBytes!.SequenceEqual(png), "Image tool lost file selection."); return "queued";
            });
            Check(await edit.ExecuteAsync("{\"prompt\":\"change background\",\"reference_files\":[\"" + id + "\"]}") == "queued", "File references did not reach image tool.");
            var sends = 0;
            using var send = new SendDownloadedFileTool(files, (file, asImage, _) => {
                Check(asImage && File.ReadAllBytes(file.Path).SequenceEqual(png), "Send did not receive actual downloaded image."); sends++; return Task.FromResult("File sent successfully.");
            });
            var sendArgs = "{\"file_id\":\"" + id + "\"}";
            Check(await send.ExecuteAsync(sendArgs) == "File sent successfully.", "Image send failed.");
            await send.ExecuteAsync(sendArgs); Check(sends == 1, "Retry duplicated image send.");
            Check((await send.ExecuteAsync("{\"file_id\":\"/etc/passwd\"}")).Contains("Unknown"), "Arbitrary path was accepted.");
            var other = new DownloadedFiles(() => directory);
            try { other.Resolve(id); throw new Exception("Cross-turn file handle accepted."); } catch (ArgumentException) { }
            var fake = await files.SaveAsync(Encoding.UTF8.GetBytes("not an image"), "image/png", "fake.png", default);
            try { await files.ImagesAsync([fake.file_id], default); throw new Exception("Spoofed reference image accepted."); } catch (ArgumentException) { }
            using var sendFake = new SendDownloadedFileTool(files, (_, _, _) => throw new Exception("Fake image sent inline."));
            Check((await sendFake.ExecuteAsync("{\"file_id\":\"" + fake.file_id + "\",\"as_image\":true}")).Contains("cannot"), "Spoofed MIME permitted inline image send.");
            // Stored MIME alone must never grant inline-image capability.
            var text = await files.SaveAsync(Encoding.UTF8.GetBytes("file body"), "text/plain", "notes.txt", default);
            using var sendText = new SendDownloadedFileTool(files, (file, asImage, _) => { Check(!asImage && file.Info.file_name == "notes.txt", "Document not sent as a file."); return Task.FromResult("File sent successfully."); });
            Check(await sendText.ExecuteAsync("{\"file_id\":\"" + text.file_id + "\"}") == "File sent successfully.", "Document attachment failed.");
            using var oversized = new HttpFetchTool(new() { MaxFileBytes = 1024 }, handler: new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) })), downloads: files);
            try { await oversized.ExecuteAsync("{\"url\":\"https://example.com/large\",\"response_mode\":\"file\"}"); throw new Exception("Oversize file accepted."); } catch (InvalidOperationException) { }
            using var failed = new HttpFetchTool(new(), handler: new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent(png) })), downloads: files);
            try { await failed.ExecuteAsync("{\"url\":\"https://example.com/missing\",\"response_mode\":\"file\"}"); throw new Exception("Failed response saved as a file."); } catch (HttpFetchException) { }
            Console.WriteLine("PASS: binary download, opaque scoped IDs, filename sanitization, image references, image/document sending, send deduplication, spoof rejection, size and HTTP errors");
        }
        finally { Directory.Delete(directory, true); }
        using var tavily = new TavilySearchClient(new() { ApiKey = "test" }, "", new Handler(async request => {
            using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(input.RootElement.GetProperty("include_images").GetBoolean() && input.RootElement.GetProperty("include_image_descriptions").GetBoolean(), "Image search flags missing.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"images":["https://images.example/a.png",{"url":"http://127.0.0.1/private","description":"no"}],"results":[{"title":"page","url":"https://example.com/page","content":"summary","images":[{"url":"https://images.example/b.jpg","description":"a cat"},{"url":"https://images.example/a.png"}]}]}
                """, Encoding.UTF8, "application/json") };
        }));
        var search = await tavily.SearchAsync("cat", default, includeImages: true);
        Check(search.Contains("https://images.example/a.png") && search.Contains("https://images.example/b.jpg") && search.Contains("a cat") && !search.Contains("127.0.0.1"), "Search did not retain safe root/per-result image URLs.");
        using var onlyImages = JsonDocument.Parse("{\"images\":[\"https://images.example/a.png\"]}");
        Check(TavilySearchClient.ExtractImages(onlyImages.RootElement).Count == 1, "Image-only results discarded.");
        Console.WriteLine("PASS: Tavily image flags, string/object and per-result images, description/source, deduplication and URL validation");
    }
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
