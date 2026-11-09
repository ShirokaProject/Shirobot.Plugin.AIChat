using System.Net;
using System.Reflection;
using ShiroBot.AiChatPlugin;
using ShiroBot.SDK.Models;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Tools;

internal static class ImageApiOfflineChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        foreach (var size in new[] { "auto", "1024x1024", "1536x864", "3840x2160", "2160x3840" })
            Check(ImageGenerationClient.IsValidSize(size), "Valid size rejected: " + size);
        foreach (var size in new[] { "0x0", "1537x864", "4096x2048", "16x1024", "2147483647x16", "-16x16" })
            Check(!ImageGenerationClient.IsValidSize(size), "Invalid size accepted: " + size);
        var calls = 0;
        using var tool = new ImageGenerationTool((_, options, _) => { calls++; Check(options.Size == "1536x864", "Tool lost custom size."); return Task.FromResult("queued"); });
        Check((await tool.ExecuteAsync("{\"prompt\":\"cat\",\"size\":\"1537x864\"}")).StartsWith("Invalid"), "Tool accepted invalid size.");
        await tool.ExecuteAsync("{\"prompt\":\"cat\",\"size\":\"1536x864\"}");
        await tool.ExecuteAsync("{\"prompt\":\"cat\"}");
        Check(calls == 1, "Retry duplicated image generation.");
        var defaults = new ImageGenerationSection { OutputFormat = "jpeg", Background = "opaque", OutputCompression = 73 };
        var captured = new List<ImageGenerationOptions>();
        using (var optionsTool = new ImageGenerationTool((_, options, _) => { captured.Add(options); return Task.FromResult("queued"); },
            (_, _, options, _) => { captured.Add(options); return Task.FromResult("edit queued"); }))
        {
            foreach (var arguments in new[] {
                "{\"prompt\":\"cat\",\"background\":\"invalid\"}",
                "{\"prompt\":\"cat\",\"output_format\":\"bmp\"}",
                "{\"prompt\":\"cat\",\"output_compression\":101}",
                "{\"prompt\":\"cat\",\"output_compression\":0.5}",
                "{\"prompt\":\"cat\",\"output_compression\":\"10\"}",
                "{\"prompt\":\"cat\",\"background\":\"transparent\",\"output_format\":\"jpeg\"}" })
                Check(await optionsTool.ExecuteAsync(arguments) != "queued", "Invalid options queued a job.");
            Check(captured.Count == 0, "Invalid options invoked generation.");
            await optionsTool.ExecuteAsync("{\"prompt\":\"cat\",\"background\":\"transparent\",\"output_format\":\"webp\",\"output_compression\":0}");
            var resolved = captured.Single().Resolve(defaults);
            Check(resolved.Background == "transparent" && resolved.OutputFormat == "webp" && resolved.OutputCompression == 0,
                "Tool did not preserve per-call overrides including zero compression.");
            Check(defaults.OutputFormat == "jpeg" && defaults.Background == "opaque" && defaults.OutputCompression == 73,
                "Per-call options mutated saved defaults.");
        }
        captured.Clear();
        using (var editTool = new ImageGenerationTool((_, _, _) => throw new Exception("Edit routed to generation."),
            (ids, _, options, _) => { Check(ids.SequenceEqual(new[] { 1 }), "Reference selection lost."); captured.Add(options); return Task.FromResult("edit queued"); }))
        {
            await editTool.ExecuteAsync("{\"prompt\":\"cat\",\"reference_images\":[1],\"background\":\"transparent\",\"output_format\":\"png\"}");
            Check(captured.Single().Background == "transparent" && captured.Single().OutputFormat == "png" && captured.Single().OutputCompression is null,
                "History edits lost overrides or omission semantics.");
        }
        Check(new ImageGenerationOptions().Resolve(defaults).OutputCompression == 73, "Omitted options did not use defaults.");
        var plugin = new AiChatPlugin();
        var pluginConfig = new PluginConfig();
        typeof(AiChatPlugin).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plugin, pluginConfig);
        var command = typeof(AiChatPlugin).GetMethod("HandleImageGenAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var queue = typeof(AiChatPlugin).GetMethod("QueueImageGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var generate = typeof(AiChatPlugin).GetMethod("GenerateAndSendImageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var message = new MessageEvent { Platform = "test", MessageId = "disabled", Channel = Channel.Group("g"), Sender = new User("u"), Segments = [new TextSegment("#imagegen RTX ON")] };
        foreach (var toolEnabled in new[] { false, true })
        {
            pluginConfig.ToolUse.Enabled = toolEnabled;
            pluginConfig.ToolUse.ImageGen.Enabled = !toolEnabled;
            // No bot context: any attempted reply, queue or network access fails this check.
            await (Task)command.Invoke(plugin, [message])!;
            Check((string)queue.Invoke(plugin, [message, "cat", new ImageGenerationOptions(), null, null])! == "", "Disabled queue must be silent.");
            Check(await (Task<string>)generate.Invoke(plugin, [message, "cat", null, CancellationToken.None, null, null, null])! == "", "Disabled execution must be silent.");
        }
        Console.WriteLine("PASS: AI parameter overrides, invalid argument rejection, history editing, default isolation, disabled command/queue/execution silence");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1sAAAAASUVORK5CYII=");
        foreach (var edit in new[] { false, true })
        foreach (var format in new[] { "png", "webp", "jpeg", "gif" })
        {
            var config = new ImageGenerationSection { EnableGif = true, Size = "1536x864", Background = format is "jpeg" or "gif" ? "opaque" : "transparent", OutputFormat = format, OutputCompression = 0 };
            using var handler = new Handler(async request =>
            {
                Check(request.RequestUri!.AbsolutePath == (edit ? "/v1/images/edits" : "/v1/images/generations"), "Wrong endpoint.");
                Check(request.Headers.Authorization?.ToString() == "Bearer test-key", "Missing authorization.");
                var requestBody = await request.Content!.ReadAsStringAsync();
                if (format == "gif") Check(requestBody.Contains("frame_count") && requestBody.Contains("frame_duration_ms") && !requestBody.Contains("output_compression"), "GIF parameters missing or compression leaked.");
                if (edit)
                {
                    var parts = ((MultipartFormDataContent)request.Content!).ToArray();
                    var values = new Dictionary<string, string>();
                    foreach (var part in parts) values[part.Headers.ContentDisposition!.Name!.Trim('"')] = await part.ReadAsStringAsync();
                    Check(values["output_format"] == format && values["background"] == config.Background && values["size"] == config.Size, "Edit options lost.");
                    Check(values.ContainsKey("image[]") && values.ContainsKey("output_compression") == (format is "jpeg" or "webp"), "Invalid edit fields.");
                }
                else
                {
                    using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    var body = json.RootElement;
                    Check(body.GetProperty("output_format").GetString() == format && body.GetProperty("background").GetString() == config.Background && body.GetProperty("size").GetString() == config.Size, "Generation options lost.");
                    Check(body.TryGetProperty("output_compression", out var compression) == (format is "jpeg" or "webp"), "PNG must omit compression.");
                    if (format is "jpeg" or "webp") Check(compression.GetInt32() == 0, "Zero compression was lost.");
                    Check(!body.TryGetProperty("response_format", out _), "GPT Image must omit response_format.");
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(png) + "\"}]}") };
            });
            var result = await ImageGenerationClient.GenerateAsync("https://example.invalid/v1/", "test-key", "gpt-image-2", "cat", config, "", default,
                edit ? new[] { new ImageGenerationInput(png, "image/png") } : null, handler);
            Check(result.SequenceEqual(png), "Image bytes changed.");
        }
        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");
        Check(ImageGenerationClient.DecodeResponse(Encoding.UTF8.GetBytes("{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(gif) + "\"}]}" )).SequenceEqual(gif), "GIF bytes must decode unchanged.");
        try { ImageGenerationClient.ValidateOptions(new() { Background = "transparent", OutputFormat = "jpeg" }); throw new Exception("Transparent JPEG accepted."); }
        catch (ImageGenerationException) { }
        Console.WriteLine("PASS: custom sizes, tool validation and deduplication, generation/edit options, all output formats, zero compression, base64 image decoding");
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
