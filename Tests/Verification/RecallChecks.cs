using System.Reflection;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Delivery;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

internal static class RecallChecks
{
    static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    static async Task Canceled(Task task)
    {
        try { await task; throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { }
    }
    public static async Task RunAsync()
    {
        var message = new MessageEvent { Platform = "custom", InstanceId = "one", MessageId = "123",
            Channel = Channel.Group("group"), Sender = new User("user"), Segments = [] };
        var recall = new MessageDeletedEvent { Platform = message.Platform, InstanceId = message.InstanceId,
            MessageId = message.MessageId, Channel = message.Channel };
        var plugin = new AiChatPlugin();
        var registry = (PendingAiRequests)typeof(AiChatPlugin).GetField("_pendingRequests", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(plugin)!;
        var config = new PluginConfig();
        typeof(AiChatPlugin).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plugin, config);
        var handle = typeof(AiChatPlugin).GetMethod("HandleMessageDeletedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using (var pending = registry.Register(message))
        {
            await (Task)handle.Invoke(plugin, [recall])!;
            Check(!pending.Token.IsCancellationRequested, "Disabled recall switch must preserve request.");
            config.Behaviour.CancelOnRecall = true;
            foreach (var unrelated in new[] { recall with { InstanceId = "two" }, recall with { Platform = "other" },
                recall with { Channel = Channel.Group("other") }, recall with { Channel = Channel.Direct("group") }, recall with { MessageId = "456" } })
                await (Task)handle.Invoke(plugin, [unrelated])!;
            Check(!pending.Token.IsCancellationRequested, "Recall must match exact adapter, channel and message.");
            await (Task)handle.Invoke(plugin, [recall])!;
            Check(pending.Token.IsCancellationRequested && !pending.TryBeginReply(), "Canceled request must never start a reply.");
        }
        Check(registry.Count == 0, "Completed requests must be removed.");
        using (var pending = registry.Register(message))
        {
            Check(pending.TryBeginReply(), "Reply should start.");
            await registry.CancelBeforeReplyAsync(recall);
            Check(!pending.Token.IsCancellationRequested, "Visible replies must continue after recall.");
        }

        var primary = new ModelEntry { Name = "primary" };
        config.Models = [primary, new ModelEntry { Name = "backup" }];
        config.Default.FallbackModel = "backup";
        config.Default.RetryOnTimeoutMaxAttempts = 3;
        var method = typeof(AiChatPlugin).GetMethod("ExecuteTextWithFallbackAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var attempts = 0;
        using (var pending = registry.Register(message))
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<ModelEntry, Task<string>> operation = async _ => {
                attempts++;
                using var client = new OpenAiClient(10, "");
                var clients = (Dictionary<string, HttpClient>)typeof(OpenAiClient).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
                clients["https://example.invalid/v1"] = new HttpClient(new BlockingHandler(started));
                return await client.ChatAsync(config.Default, new ModelEntry { Name = "primary", BaseUrl = "https://example.invalid/v1", ApiKey = "test" },
                    new OpenAiChatRequest { Model = "primary", Messages = [] }, pending.Token);
            };
            var task = (Task<string>)method.Invoke(plugin, [primary, operation, null, pending.Token])!;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await registry.CancelBeforeReplyAsync(recall);
            await Canceled(task);
            Check(attempts == 1, "Recall must stop retries and fallback.");
        }
        using (var pending = registry.Register(message))
        {
            var ctx = DispatchProxy.Create<IBotContext, TestProxy>();
            var api = new FakeOfficialApi();
            ((TestProxy)(object)ctx).Call = (m, _) => m.Name == "GetAdapterExtension" ? api : throw new NotSupportedException();
            await using var delivery = new OfficialReplyDelivery(ctx, message with { Channel = Channel.Direct("user") },
                new BehaviourSection { UseStreaming = true }, pending.TryBeginReply, pending.Token);
            await registry.CancelBeforeReplyAsync(recall);
            await Canceled(delivery.AppendAsync("first frame"));
            Check(api.Stream.Frames.Count == 0, "Recall before first stream frame must send nothing.");
        }
        var store = new ConversationStore("unused", false, 20);
        var key = new ConversationKey("test-instance", ChatScene.Group, "1", "2");
        await store.AcquireAsync(key);
        using (var pending = registry.Register(message))
        {
            var queued = store.AcquireAsync(key, pending.Token);
            await registry.CancelBeforeReplyAsync(recall);
            await Canceled(queued);
        }
        store.Release(key);
        await store.AcquireAsync(key).WaitAsync(TimeSpan.FromSeconds(1));
        store.Release(key);
        Check(registry.Count == 0, "Canceled requests must be removed.");
        Console.WriteLine("PASS: recall switch, exact event matching, queue cancellation, no retry/fallback, no first frame and visible reply protection");
    }
}

internal sealed class BlockingHandler(TaskCompletionSource started) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        started.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        throw new Exception("Canceled HTTP request must never complete.");
    }
}
