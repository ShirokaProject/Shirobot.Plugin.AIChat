using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.AiChatPlugin.Resources;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using PluginJson = ShiroBot.AiChatPlugin.PluginJson;

internal static class ImageGenerationChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        using (var baseline = new ImageGenerationTool((_, _, _) => Task.FromResult("queued")))
        using (var blank = new ImageGenerationTool((_, _, _) => Task.FromResult("queued"), promptInstructions: " \n "))
        {
            Check(blank.SystemPrompt == baseline.SystemPrompt, "An empty image prompt policy must preserve the existing instructions.");
            var configured = new AiChatPlugin();
            var policyConfig = new PluginConfig();
            const string policy = "用中文自然语言整理提示词，默认水彩风格，依次说明主体、构图、光影。";
            policyConfig.ToolUse.ImageGen.PromptInstructions = policy;
            typeof(AiChatPlugin).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(configured, policyConfig);
            var policyMessage = new MessageEvent { Platform = "test", MessageId = "policy", Channel = Channel.Group("g"),
                Sender = new User("u"), Segments = [] };
            using var first = configured.CreateImageGenerationTool(policyMessage, [], "history catalog");
            Check(first.SystemPrompt.Contains(policy) && first.SystemPrompt.Contains("用户本次明确") &&
                !first.SystemPrompt.Contains("history catalog") &&
                PluginJson.Serialize(first.Definition) == PluginJson.Serialize(baseline.Definition),
                "Image prompt rules must reach the chat model without changing tool schema or embedding dynamic image catalogs.");
            policyConfig.ToolUse.ImageGen.PromptInstructions = "Use English prose and a photographic style.";
            using var changed = configured.CreateImageGenerationTool(policyMessage, [], "");
            Check(changed.SystemPrompt.Contains(policyConfig.ToolUse.ImageGen.PromptInstructions) &&
                !changed.SystemPrompt.Contains(policy) && first.SystemPrompt.Contains(policy),
                "New turns must pick up changed prompt rules while an existing tool retains its snapshot.");
        }
        Console.WriteLine("PASS: configurable image prompt writing rules reach the model, preserve defaults and keep tool schema stable");
        foreach (var size in new[] { "auto", "1024x1024", "1536x864", "3840x2160", "2160x3840" })
            Check(ImageGenerationClient.IsValidSize(size), "Supported size rejected: " + size);
        foreach (var size in new[] { "0x0", "1537x864", "4096x2048", "16x1024", "2147483647x16", "-16x16", "1024X1024" })
            Check(!ImageGenerationClient.IsValidSize(size), "Invalid size accepted: " + size);
        try { ImageGenerationClient.ValidateOptions(new() { Background = "transparent", OutputFormat = "jpeg" }); throw new Exception("Transparent JPEG accepted."); }
        catch (ImageGenerationException) { }
        ImageGenerationClient.ValidateOptions(new() { Background = "transparent", OutputFormat = "webp", OutputCompression = 0 });
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1sAAAAASUVORK5CYII=");
        using var listener = new HttpListener();
        var port = Random.Shared.Next(20000, 40000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var calls = 0; var forbidden = false;
        string? lastSize = null, lastQuality = null, lastPrompt = null;
        var holdImage = false;
        var imageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseImage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serve = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                calls++;
                if (forbidden) { ctx.Response.StatusCode = 403; ctx.Response.Close(); continue; }
                Check(ctx.Request.Headers["Authorization"] == "Bearer image-key", "Missing provider authorization.");
                using var reader = new StreamReader(ctx.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                if (ctx.Request.Url!.AbsolutePath == "/v1/images/edits")
                {
                    Check(ctx.Request.ContentType!.StartsWith("multipart/form-data") && body.Contains("image[]"), "Missing edit input.");
                    Check(body.Contains("background") && body.Contains("transparent") && body.Contains("output_format") &&
                        body.Contains("webp") && body.Contains("output_compression") && body.Contains("73"), "Edit request lost image options.");
                    Check(body.Contains("保留地鼠姿势，把毛发改成蓝色，背景不变"), "Model edit prompt was replaced by the user message.");
                    lastPrompt = "保留地鼠姿势，把毛发改成蓝色，背景不变";
                }
                else
                {
                    Check(ctx.Request.Url.AbsolutePath == "/v1/images/generations", "Wrong image endpoint.");
                    using var json = JsonDocument.Parse(body);
                    Check(json.RootElement.GetProperty("model").GetString() == "gpt-image-2.5-sunburst" &&
                        json.RootElement.GetProperty("n").GetInt32() == 1 && !json.RootElement.TryGetProperty("response_format", out _), "Image request mismatch.");
                    Check(json.RootElement.GetProperty("background").GetString() == "transparent" &&
                        json.RootElement.GetProperty("output_format").GetString() == "webp" &&
                        json.RootElement.GetProperty("output_compression").GetInt32() == 73,
                        "Configured image options were lost before the generation request.");
                    lastPrompt = json.RootElement.GetProperty("prompt").GetString();
                    if (holdImage) { imageStarted.TrySetResult(); await releaseImage.Task; }
                    lastSize = json.RootElement.GetProperty("size").GetString();
                    lastQuality = json.RootElement.GetProperty("quality").GetString();
                }
                var bytes = Encoding.UTF8.GetBytes("{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(png) + "\"}]}");
                try { await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close(); }
                catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
                { try { ctx.Response.Abort(); } catch (ObjectDisposedException) { } }
            }
        });
        var config = new PluginConfig(); config.ToolUse.ImageGen.Enabled = true;
        config.ToolUse.ImageGen.Background = "transparent";
        config.ToolUse.ImageGen.OutputFormat = "webp";
        config.ToolUse.ImageGen.OutputCompression = 73;
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
        var jobs = new List<Task>();
        using var stopping = new CancellationTokenSource();
        var lifetime = DispatchProxy.Create<IPluginLifetime, TestProxy>();
        ((TestProxy)(object)lifetime).Call = (method, args) =>
        {
            if (method.Name == "get_Stopping") return stopping.Token;
            if (method.Name == "Run") { var task = Task.Run(() => ((Func<CancellationToken, Task>)args![0]!)(stopping.Token)); jobs.Add(task); return task; }
            return null;
        };
        var bot = DispatchProxy.Create<IBotContext, TestProxy>();
        ((TestProxy)(object)bot).Call = (method, _) => method.Name switch {
            "get_Message" => messages, "get_Lifetime" => lifetime, "UseInstance" => null, "CreateTempDirectory" => AllocateTemp(),
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
        { using var pending = new PendingAiRequests().Register(message); return await (Task<string>)method.Invoke(plugin, [message, prompt, pending, ct, new ImageGenerationOptions(size, quality), null, null])!; }
        using var tool = new ImageGenerationTool((prompt, options, ct) => Generate(prompt, options.Size, options.Quality, ct));
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
        Check(await Generate("cat", null, null, CancellationToken.None) == "" && calls == 4, "Disabled generation called upstream.");
        config.ToolUse.ImageGen.Enabled = true; sendFails = false; holdImage = true;
        config.ToolUse.ImageGen.PromptInstructions = "Use English prose, default watercolor, describe composition and light.";
        using var fetcher = new ResourceFetcher(5, 10000, 10000, "");
        typeof(AiChatPlugin).GetField("_fetcher", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, fetcher);
        typeof(AiChatPlugin).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, new ResourceCache(tempRoot, "cache", 1));
        var queue = typeof(AiChatPlugin).GetMethod("QueueImageGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var request = message with { Segments = [new TextSegment("#ai 帮我生成一个我的自画像")] };
        const string modelPrompt = "一位热爱硬件与编程的年轻人，坐在摆有电路板和代码屏幕的书桌前，二次元插画";
        using var chatTool = plugin.CreateImageGenerationTool(request, [], "当前没有可用历史图片。");
        var queued = await chatTool.ExecuteAsync(JsonSerializer.Serialize(new { prompt = modelPrompt, size = "1536x1024", quality = "high" }));
        Check(queued.Contains("后台"), "Image queue must return immediately.");
        await imageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!jobs.Single().IsCompleted && lastPrompt == modelPrompt,
            "Chat image generation must forward the model prompt unchanged while running independently.");
        Check(((string)queue.Invoke(plugin, [request, modelPrompt, new ImageGenerationOptions(), null, null])!).Contains("已有"), "Same message must not queue duplicate jobs.");
        // Unrelated work can complete while the image job is still waiting.
        await Task.Run(() => Check(!jobs.Single().IsCompleted, "Background job unexpectedly completed."));
        releaseImage.TrySetResult();
        await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(5));
        Check(calls == 5 && !File.Exists(paths.Last()) && lastSize == "1536x1024" && lastQuality == "high",
            "Background job did not deliver exactly once with model options and clean its file.");
        holdImage = false;
        var editRequest = request with { MessageId = "edit", Segments = [new TextSegment("怎么还是不蓝啊！")] };
        using var editTool = plugin.CreateImageGenerationTool(editRequest, [new ImagePart(null, "image/png", png.Length, png)], "历史图片 1：地鼠");
        Check((await editTool.ExecuteAsync("{\"prompt\":\"保留地鼠姿势，把毛发改成蓝色，背景不变\",\"reference_images\":[1]}")).Contains("后台"), "Edit must queue.");
        await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(5));
        Check(calls == 6 && lastPrompt == "保留地鼠姿势，把毛发改成蓝色，背景不变", "History editing lost the model prompt.");
        var command = request with { MessageId = "command", Segments = [new TextSegment("#imagegen 画一只白猫")] };
        await (Task)typeof(AiChatPlugin).GetMethod("HandleImageGenAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, [command])!;
        await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(5));
        Check(calls == 7 && lastPrompt == "画一只白猫", "Direct command must preserve the user's prompt without rewriting.");
        Check(config.ToolUse.ImageGen.CancelOnRecall && !config.Behaviour.CancelOnRecall,
            "Image recall cancellation must default on independently of ordinary chat recall.");
        holdImage = true;
        imageStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        releaseImage = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var recalledRequest = request with { MessageId = "recall-running", Segments = [new TextSegment("画一张海报")] };
        var deletedEvent = new MessageDeletedEvent { Platform = recalledRequest.Platform, InstanceId = recalledRequest.InstanceId,
            Channel = recalledRequest.Channel, MessageId = recalledRequest.MessageId };
        var onRecall = typeof(AiChatPlugin).GetMethod("HandleMessageDeletedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var beforeImages = paths.Count; var beforeDeletes = deleted.Count;
        queue.Invoke(plugin, [recalledRequest, "海报", new ImageGenerationOptions(), null, null]);
        await imageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = jobs.Last();
        await (Task)onRecall.Invoke(plugin, [deletedEvent with { InstanceId = "unrelated" }])!;
        Check(!running.IsCompleted, "Unrelated recall must not stop an image request.");
        await (Task)onRecall.Invoke(plugin, [deletedEvent])!;
        await running.WaitAsync(TimeSpan.FromSeconds(3));
        Check(paths.Count == beforeImages && deleted.Count == beforeDeletes + 1,
            "Recall must cancel the in-flight HTTP wait before upstream responds, send no image and retract progress.");
        releaseImage.TrySetResult(); holdImage = false;
        var callsAfterCancellation = calls;

        var imageGate = (SemaphoreSlim)typeof(AiChatPlugin).GetField("_imageGenerationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
        await imageGate.WaitAsync();
        try
        {
            var waiting = recalledRequest with { MessageId = "recall-queued" };
            queue.Invoke(plugin, [waiting, "排队图", new ImageGenerationOptions(), null, null]);
            var job = jobs.Last();
            config.ToolUse.ImageGen.CancelOnRecall = false;
            await (Task)onRecall.Invoke(plugin, [deletedEvent with { MessageId = waiting.MessageId }])!;
            Check(!job.IsCompleted, "Image recall switch off must preserve the queued job.");
            config.ToolUse.ImageGen.CancelOnRecall = true;
            await (Task)onRecall.Invoke(plugin, [deletedEvent with { MessageId = waiting.MessageId }])!;
            await job.WaitAsync(TimeSpan.FromSeconds(3));
            Check(calls == callsAfterCancellation, "A recalled queued job must never call the provider.");
        }
        finally { imageGate.Release(); }
        var late = recalledRequest with { MessageId = "recall-before-tool" };
        await (Task)onRecall.Invoke(plugin, [deletedEvent with { MessageId = late.MessageId }])!;
        var beforeJobs = jobs.Count;
        using var lateTool = plugin.CreateImageGenerationTool(late, [], "");
        Check((await lateTool.ExecuteAsync("{\"prompt\":\"海报\"}")).Contains("撤回") && jobs.Count == beforeJobs && calls == callsAfterCancellation,
            "A tool result arriving after recall must not queue a new image job.");
        Console.WriteLine("PASS: image recall cancels running HTTP and queued work, cleans progress, isolates instances and prevents late tool submission");
        listener.Stop(); await serve;
        Directory.Delete(tempRoot, recursive: true);
        Console.WriteLine("PASS: image generation provider request, one-image delivery, reaction cleanup, text fallback, retry deduplication, cancellation and enabled switch");
    }
}
