using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Contracts;
using ShiroBot.AiChatPlugin.OpenAi;

/// <summary>其他插件借用模型：按 [plugin_api] 选模型、识图时换模型、图片和参数传给上游。</summary>
internal static class ModelApiChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static async Task RunAsync()
    {
        using var listener = new HttpListener();
        var port = Random.Shared.Next(20000, 40000);
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        JsonNode? lastBody = null;
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch { return; }
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                lastBody = JsonNode.Parse(await reader.ReadToEndAsync());
                var reply = Encoding.UTF8.GetBytes("""{"choices":[{"message":{"role":"assistant","content":"好的"}}]}""");
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(reply);
                context.Response.Close();
            }
        });

        var plugin = new AiChatPlugin();
        var config = new PluginConfig();
        config.Default.Model = "text";
        config.Models =
        [
            new ModelEntry { Name = "text", BaseUrl = prefix, ApiKey = "k", SupportsVision = false },
            new ModelEntry { Name = "vision", BaseUrl = prefix, ApiKey = "k", SupportsVision = true },
            new ModelEntry { Name = "hidden", BaseUrl = prefix, ApiKey = "k", SupportsVision = true }
        ];
        config.PluginApi.Models = ["text", "vision"];
        Set(plugin, "_config", config);
        Set(plugin, "_openAi", new OpenAiClient(10, ""));
        Set(plugin, "_globalConcurrency", new SemaphoreSlim(2));

        var list = (ChatModelList)Invoke(plugin, "ListPluginModels");
        Check(list.DefaultModel == "text" && list.Models.Select(m => (m.Name, m.SupportsVision)).SequenceEqual([("text", false), ("vision", true)]),
            "Plugins must see only the allowed models with their vision support.");

        async Task<ChatModelResponse> CompleteAsync(ChatModelRequest request) =>
            await (Task<ChatModelResponse>)Invoke(plugin, "HandleModelCompleteAsync", request, CancellationToken.None);

        var text = await CompleteAsync(new ChatModelRequest([new ChatModelMessage("system", "你好"), new ChatModelMessage("user", "在吗")],
            MaxOutputTokens: 50, Temperature: 0));
        Check(text is { Text: "好的", Model: "text" } && (string?)lastBody?["model"] == "text" &&
              (int?)lastBody?["max_completion_tokens"] == 50 && lastBody?["messages"]?.AsArray().Count == 2,
            "A plain request must use the default model and pass messages, token limit and temperature.");

        var image = Convert.ToBase64String([1, 2, 3]);
        var vision = await CompleteAsync(new ChatModelRequest(
            [new ChatModelMessage("user", "这是什么", [new ChatModelImage("image/png", image)])], NeedsVision: true));
        var content = lastBody?["messages"]?[0]?["content"]?.AsArray();
        Check(vision.Model == "vision" && (string?)content?[1]?["image_url"]?["url"] == "data:image/png;base64," + image,
            "A vision request must switch to a vision model and send the image.");

        foreach (var (request, reason) in new[]
                 {
                     (new ChatModelRequest([new ChatModelMessage("user", "x")], Model: "hidden"), "an unlisted model"),
                     (new ChatModelRequest([new ChatModelMessage("user", "x")], Model: "text", NeedsVision: true), "vision on a text model")
                 })
        {
            try { await CompleteAsync(request); Check(false, "Accepted " + reason); }
            catch (InvalidOperationException) { }
        }

        config.PluginApi.Enabled = false;
        try { await CompleteAsync(new ChatModelRequest([new ChatModelMessage("user", "x")])); Check(false, "A disabled plugin API accepted a call."); }
        catch (InvalidOperationException) { }
        Check(((ChatModelList)Invoke(plugin, "ListPluginModels")).Models.Count == 0, "A disabled plugin API must list no models.");

        listener.Stop();
        await serving;
        Console.WriteLine("PASS: plugin model API selection, vision fallback, images and switches");
    }

    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private static object Invoke(object target, string method, params object[] args)
    {
        try { return target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args)!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
    }
}
