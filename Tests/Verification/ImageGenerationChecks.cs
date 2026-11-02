using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

internal static class ImageGenerationChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1sAAAAASUVORK5CYII=");
        using var listener = new HttpListener();
        var port = Random.Shared.Next(20000, 40000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var calls = 0; var forbidden = false;
        string? lastSize = null, lastQuality = null;
        var serve = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                calls++;
                if (forbidden) { ctx.Response.StatusCode = 403; ctx.Response.Close(); continue; }
                Check(ctx.Request.Url!.AbsolutePath == "/v1/images/generations", "Wrong image endpoint.");
                Check(ctx.Request.Headers["Authorization"] == "Bearer image-key", "Missing provider authorization.");
                using var reader = new StreamReader(ctx.Request.InputStream);
                using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
                Check(json.RootElement.GetProperty("model").GetString() == "gpt-image-2.5-sunburst" &&
                    json.RootElement.GetProperty("n").GetInt32() == 1 && !json.RootElement.TryGetProperty("response_format", out _), "Image request mismatch.");
                lastSize = json.RootElement.GetProperty("size").GetString();
                lastQuality = json.RootElement.GetProperty("quality").GetString();
                var bytes = Encoding.UTF8.GetBytes("{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(png) + "\"}]}");
                await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
            }
        });
        var config = new PluginConfig(); config.ToolUse.ImageGen.Enabled = true;
        config.Models = [new() { Name = "gpt-image-2.5-sunburst", Provider = "test" }];
        config.Providers = [new() { Name = "test", BaseUrl = $"http://127.0.0.1:{port}/v1", ApiKey = "image-key" }];
        var tempRoot = Path.Combine(Path.GetTempPath(), "image-send-check-" + Guid.NewGuid().ToString("N"));
        var paths = new List<string>(); var sendFails = false;
        TemporaryDirectory AllocateTemp()
        {
            var path = Path.Combine(tempRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
            return new TemporaryDirectory(path, DateTimeOffset.UtcNow.AddHours(1));
        }
        var deleted = new List<string>();
        var sent = new List<MessageSegment>(); var reactions = new List<bool>();
        var reaction = DispatchProxy.Create<IMessageReactionService, TestProxy>();
        ((TestProxy)(object)reaction).Call = (method, args) => method.Name switch {
            "GetReactionCapabilities" => ReactionCapabilities.Unicode,
            "SetReactionAsync" => RecordReaction((bool)args![2]!), _ => null };
        Task RecordReaction(bool add) { reactions.Add(add); return Task.CompletedTask; }
        var messages = DispatchProxy.Create<IMessageContext, TestProxy>();
        ((TestProxy)(object)messages).Call = (method, args) => {
            if (method.Name == "DeleteMessageAsync") { deleted.Add((string)args![1]!); return Task.CompletedTask; }
            if (method.Name == "SendMessageAsync")
            {
                var segments = (IReadOnlyList<MessageSegment>)args![1]!; sent.AddRange(segments);
                foreach (var image in segments.OfType<ImageSegment>())
                {
                    var uri = new Uri(image.Uri); Check(uri.IsFile, "Generated images must use file URIs.");
                    paths.Add(uri.LocalPath); Check(File.ReadAllBytes(uri.LocalPath).SequenceEqual(png), "File must exist throughout sending with original bytes.");
                }
                return Task.FromResult(new SentMessage("sent") { IsSuccess = !sendFails || !segments.OfType<ImageSegment>().Any() });
            }
            return null;
        };
        var supportReaction = true;
        var bot = DispatchProxy.Create<IBotContext, TestProxy>();
        ((TestProxy)(object)bot).Call = (method, _) => method.Name switch {
            "get_Message" => messages, "UseInstance" => null, "CreateTempDirectory" => AllocateTemp(),
            "GetAdapterExtension" => supportReaction ? reaction : null, _ => null };
        var plugin = new AiChatPlugin();
        typeof(AiChatPlugin).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plugin, config);
        // Context is assigned by the base class at load time; the full plugin starts unrelated background services.
        var contextField = typeof(PluginBase).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(IBotContext));
        contextField.SetValue(plugin, bot);
        var message = new MessageEvent { Platform = "test", InstanceId = "one", Channel = Channel.Group("g"),
            Sender = new User("u"), MessageId = "m", Segments = [] };
        var method = typeof(AiChatPlugin).GetMethod("GenerateAndSendImageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        async Task<string> Generate(string prompt, string? size, string? quality, CancellationToken ct)
        { using var pending = new PendingAiRequests().Register(message); return await (Task<string>)method.Invoke(plugin, [message, prompt, pending, ct, size, quality, null])!; }
        using var tool = new ImageGenerationTool(Generate);
        Check(await tool.ExecuteAsync("{\"prompt\":\"一只猫\",\"size\":\"1536x1024\",\"quality\":\"high\"}") == "图片已生成并发送。", "Image tool failed.");
        Check(lastSize == "1536x1024" && lastQuality == "high", "Tool size and quality were not forwarded.");
        Check(config.ToolUse.ImageGen.Size == "1024x1024" && config.ToolUse.ImageGen.Quality == "auto", "Tool mutated persistent defaults.");
        await tool.ExecuteAsync("{\"prompt\":\"再试一次\"}");
        Check(calls == 1 && reactions.SequenceEqual(new[] { true, false }) && sent.OfType<ImageSegment>().Count() == 1 &&
            !sent.OfType<TextSegment>().Any(), "Tool retry duplicated generation or reaction fallback was sent unnecessarily.");
        Check(paths.All(path => !File.Exists(path)), "Successful send must delete its image file.");
        supportReaction = false; sent.Clear();
        await Generate("cat", null, null, CancellationToken.None);
        Check(lastSize == "1024x1024" && lastQuality == "auto", "Omitted options must use defaults.");
        Check(sent.First() is TextSegment { Text: "生成图片中..." } && sent.Last() is ImageSegment, "No-reaction fallback must precede image.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await Generate("cat", null, null, canceled.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        Check(calls == 2, "Canceled generation reached upstream.");
        Check(deleted.SequenceEqual(new[] { "sent" }), "Successful generation must retract progress text.");
        forbidden = true;
        var failure = await Generate("cat", null, null, CancellationToken.None);
        Check(failure.Contains("HTTP 403") && deleted.Count == 2, "Forbidden generation must explain permissions and retract progress text.");
        forbidden = false; sendFails = true;
        var sendFailure = await Generate("cat", null, null, CancellationToken.None);
        Check(sendFailure.Contains("发送失败") && File.Exists(paths.Last()) && deleted.Count == 3,
            "Failed send must retain generated file and retract progress message.");
        config.ToolUse.ImageGen.Enabled = false;
        Check(await Generate("cat", null, null, CancellationToken.None) == "生图功能未启用。" && calls == 4, "Disabled generation called upstream.");
        listener.Stop(); await serve;
        Directory.Delete(tempRoot, recursive: true);
        Console.WriteLine("PASS: image generation provider request, one-image delivery, reaction cleanup, text fallback, retry deduplication, cancellation and enabled switch");
    }
}
