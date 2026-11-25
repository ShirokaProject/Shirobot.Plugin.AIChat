using System.Net;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Resources;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.AiChatPlugin.Tools.Web;
using ShiroBot.AiChatPlugin.Tools.Images;

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

            using var create = new CreateTextFileTool(files);
            using (var created = JsonDocument.Parse(await create.ExecuteAsync("{\"content\":\"# 标题\\n正文\",\"file_name\":\"../notes.md\"}")))
            {
                var createdId = created.RootElement.GetProperty("file").GetProperty("file_id").GetString()!;
                var createdFile = files.Resolve(createdId);
                Check(createdFile.Info.file_name == "notes.md" && createdFile.Info.mime == "text/markdown"
                    && File.ReadAllText(createdFile.Path) == "# 标题\n正文", "Created text file lost content, name or type.");
                using var sendCreated = new SendDownloadedFileTool(files, (file, asImage, _) => { Check(!asImage && file.Info.file_name == "notes.md", "Created file not sent as an attachment."); return Task.FromResult("File sent successfully."); });
                Check(await sendCreated.ExecuteAsync("{\"file_id\":\"" + createdId + "\"}") == "File sent successfully.", "Created file could not be sent.");
            }
            Check(CreateTextFileTool.Normalize("run.exe") == ("run_exe.txt", "text/plain") && CreateTextFileTool.Normalize("") .Name == "file.txt"
                && CreateTextFileTool.Normalize("C:\\x\\data.CSV") == ("data.csv", "text/csv"), "Unsafe or unknown file names must become plain .txt files.");
            Check((await create.ExecuteAsync("{\"content\":\"" + new string('a', CreateTextFileTool.MaxBytes + 1) + "\",\"file_name\":\"big.txt\"}")).Contains("exceeds"),
                "Oversized text files must be rejected.");
            Check((await create.ExecuteAsync("{\"content\":\"\",\"file_name\":\"a.txt\"}")).Contains("empty"), "Empty files must be rejected.");
            Console.WriteLine("PASS: create_text_file stores sanitized text files that send_file can deliver");
        }
        finally { Directory.Delete(directory, true); }
        {
            var calls = 0;
            using var flaky = new HttpFetchTool(new() { RetryAttempts = 3, RetryDelayMs = 0 }, handler: new Handler(_ =>
                ++calls < 3 ? throw new HttpRequestException(HttpRequestError.ConnectionError, "connect failed")
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok", Encoding.UTF8, "text/plain") })));
            Check((await flaky.ExecuteAsync("{\"url\":\"https://example.com/a\"}")).Contains("\"success\":true") && calls == 3,
                "Transient connection failures must be retried until success.");

            calls = 0;
            using var down = new HttpFetchTool(new() { RetryAttempts = 3, RetryDelayMs = 0 }, handler: new Handler(_ =>
            { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") }); }));
            try { await down.ExecuteAsync("{\"url\":\"https://example.com/b\"}"); throw new Exception("503 not reported."); }
            catch (HttpFetchException ex) { Check(calls == 3 && ex.Attempts == 3 && ex.ToolResult.Contains("retried"), "Persistent 5xx must be retried and then reported with the attempt count."); }

            calls = 0;
            using var missing = new HttpFetchTool(new() { RetryAttempts = 3, RetryDelayMs = 0 }, handler: new Handler(_ =>
            { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no") }); }));
            try { await missing.ExecuteAsync("{\"url\":\"https://example.com/c\"}"); throw new Exception("404 not reported."); }
            catch (HttpFetchException) { Check(calls == 1, "A 404 must not be retried."); }

            calls = 0;
            using var write = new HttpFetchTool(new() { RetryAttempts = 3, RetryDelayMs = 0 }, handler: new Handler(_ =>
            { calls++; throw new HttpRequestException(HttpRequestError.ConnectionError, "connect failed"); }));
            try { await write.ExecuteAsync("{\"url\":\"https://example.com/d\",\"method\":\"POST\",\"body\":\"x\"}"); throw new Exception("POST failure not reported."); }
            catch (HttpFetchException) { Check(calls == 1, "Write requests must never be retried automatically."); }
            Console.WriteLine("PASS: http_fetch retries transient read failures, not 404s or writes");
        }
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
