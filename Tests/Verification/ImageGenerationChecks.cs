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
        var calls = 0;
        var serve = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                calls++;
                Check(ctx.Request.Url!.AbsolutePath == "/v1/images/generations", "Wrong image endpoint.");
                Check(ctx.Request.Headers["Authorization"] == "Bearer image-key", "Missing provider authorization.");
                using var reader = new StreamReader(ctx.Request.InputStream);
                using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
                Check(json.RootElement.GetProperty("model").GetString() == "gpt-image-2.5-sunburst" &&
                    json.RootElement.GetProperty("n").GetInt32() == 1 && !json.RootElement.TryGetProperty("response_format", out _), "Image request mismatch.");
                var bytes = Encoding.UTF8.GetBytes("{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(png) + "\"}]}");
                await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
            }
        });
        var config = new PluginConfig(); config.ToolUse.ImageGen.Enabled = true;
        config.Models = [new() { Name = "gpt-image-2.5-sunburst", Provider = "test" }];
        config.Providers = [new() { Name = "test", BaseUrl = $"http://127.0.0.1:{port}/v1", ApiKey = "image-key" }];
        var sent = new List<MessageSegment>(); var reactions = new List<bool>();
        var reaction = DispatchProxy.Create<IMessageReactionService, TestProxy>();
        ((TestProxy)(object)reaction).Call = (method, args) => method.Name switch {
            "GetReactionCapabilities" => ReactionCapabilities.Unicode,
            "SetReactionAsync" => RecordReaction((bool)args![2]!), _ => null };
        Task RecordReaction(bool add) { reactions.Add(add); return Task.CompletedTask; }
        var messages = DispatchProxy.Create<IMessageContext, TestProxy>();
        ((TestProxy)(object)messages).Call = (method, args) => {
            if (method.Name == "SendMessageAsync") { sent.AddRange((IReadOnlyList<MessageSegment>)args![1]!); return Task.FromResult(new SentMessage("sent")); }
            return null;
        };
        var supportReaction = true;
        var bot = DispatchProxy.Create<IBotContext, TestProxy>();
        ((TestProxy)(object)bot).Call = (method, _) => method.Name switch {
            "get_Message" => messages, "UseInstance" => null,
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
        async Task<string> Generate(string prompt, CancellationToken ct)
        { using var pending = new PendingAiRequests().Register(message); return await (Task<string>)method.Invoke(plugin, [message, prompt, pending, ct])!; }
        using var tool = new ImageGenerationTool(Generate);
        Check(await tool.ExecuteAsync("{\"prompt\":\"一只猫\"}") == "图片已生成并发送。", "Image tool failed.");
        await tool.ExecuteAsync("{\"prompt\":\"再试一次\"}");
        Check(calls == 1 && reactions.SequenceEqual(new[] { true, false }) && sent.OfType<ImageSegment>().Count() == 1 &&
            !sent.OfType<TextSegment>().Any(), "Tool retry duplicated generation or reaction fallback was sent unnecessarily.");
        supportReaction = false; sent.Clear();
        await Generate("cat", CancellationToken.None);
        Check(sent.First() is TextSegment { Text: "生成图片中..." } && sent.Last() is ImageSegment, "No-reaction fallback must precede image.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await Generate("cat", canceled.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        Check(calls == 2, "Canceled generation reached upstream.");
        config.ToolUse.ImageGen.Enabled = false;
        Check(await Generate("cat", CancellationToken.None) == "生图功能未启用。" && calls == 2, "Disabled generation called upstream.");
        listener.Stop(); await serve;
        Console.WriteLine("PASS: image generation provider request, one-image delivery, reaction cleanup, text fallback, retry deduplication, cancellation and enabled switch");
    }
}
