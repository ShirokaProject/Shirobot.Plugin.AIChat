using System.Net;
using System.Reflection;
using System.Text;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Resources;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

internal static class ImageEditQuoteChecks
{
    public static async Task RunAsync()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1sAAAAASUVORK5CYII=");
        using var listener = new HttpListener();
        var port = Random.Shared.Next(20000, 40000); var url = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(url); listener.Start();
        var serving = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var body = new MemoryStream(); await context.Request.InputStream.CopyToAsync(body);
            var bytes = body.ToArray(); var text = Encoding.UTF8.GetString(bytes);
            if (context.Request.Url!.AbsolutePath != "/images/edits" ||
                !context.Request.ContentType!.StartsWith("multipart/form-data") ||
                !text.Contains("image[]") || !text.Contains("image/png") ||
                bytes.AsSpan().IndexOf(png) < 0 || !text.Contains("change background"))
                throw new Exception("Editing did not submit original bytes and prompt as multipart.");
            var response = Encoding.UTF8.GetBytes("{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(png) + "\"}]}");
            await context.Response.OutputStream.WriteAsync(response); context.Response.Close();
        });
        var edited = await ImageGenerationClient.GenerateAsync(url, "key", "gpt-image-2.5-sunburst", "change background",
            new ImageGenerationSection(), "", CancellationToken.None, [new(png, "image/png")]);
        await serving; listener.Stop();
        if (!edited.SequenceEqual(png)) throw new Exception("Edited image response corrupted.");

        var original = new MessageEvent { Platform = "test", InstanceId = "one", MessageId = "old", Channel = Channel.Group("g"),
            Sender = new User("alice") { Name = "Alice" }, Segments = [new TextSegment("#ai 这是 Alice 的原话")] };
        var current = original with { MessageId = "new", Sender = new User("bob") { Name = "Bob" },
            Segments = [new QuoteSegment("old"), new TextSegment("#ai 你怎么看")] };
        var messages = DispatchProxy.Create<IMessageContext, TestProxy>();
        ((TestProxy)(object)messages).Call = (method, _) => method.Name == "GetMessageAsync" ? Task.FromResult<MessageEvent?>(original) : null;
        var bot = DispatchProxy.Create<IBotContext, TestProxy>();
        ((TestProxy)(object)bot).Call = (method, _) => method.Name == "get_Message" ? messages : null;
        var root = Path.Combine(Path.GetTempPath(), "quote-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var fetcher = new ResourceFetcher(5, 10000, 10000, "");
            var collector = new ResourceCollector(bot, fetcher, new ResourceCache(root, "cache", 1), new ResourcesSection());
            var parts = await collector.CollectAsync(current);
            typeof(AiChatPlugin).GetMethod("StripPrefixFromFirstTextPart", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [parts, "#ai"]);
            var texts = parts.OfType<TextPart>().Select(p => p.Text).ToArray();
            if (!texts.Any(t => t.Contains("引用原作者：Alice") && t.Contains("用户ID：alice") && t.Contains("不是当前用户")) ||
                !texts.Any(t => t.Contains("恢复当前用户")) || texts.Last() != "你怎么看" || !texts.Contains("#ai 这是 Alice 的原话"))
                throw new Exception("Quoted content lost its original author or current-user boundary.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: multipart image edit preserves reference bytes; quoted messages retain original author and current-user boundaries");
    }
}
