using System.Reflection;
using System.Text.Json;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Dashboard;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

internal static class NudgeReplyChecks
{
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
    {
        var proxy = DispatchProxy.Create<T, TestProxy>();
        ((TestProxy)(object)proxy).Call = call;
        return proxy;
    }

    public static async Task RunAsync()
    {
        const string endpoint = "https://example.invalid/v1";
        var root = Directory.CreateTempSubdirectory("aichat-nudge-");
        using var stopping = new CancellationTokenSource();
        var plugin = new AiChatPlugin();
        var config = new PluginConfig();
        var delivered = new List<(ChannelReference Target, IReadOnlyList<MessageSegment> Segments)>();
        var messages = Proxy<IMessageContext>((method, args) =>
        {
            if (method.Name != "SendMessageAsync" || args is not [ChannelReference target, IReadOnlyList<MessageSegment> segments, CancellationToken])
                throw new InvalidOperationException("Nudges must use a scoped proactive send.");
            delivered.Add((target, segments));
            return Task.FromResult(new SentMessage("sent"));
        });
        var configContext = Proxy<IConfigContext>((method, _) => method.Name switch
        {
            "get_ConfigPath" => Path.Combine(root.FullName, "config.toml"),
            "Load" => config,
            _ => throw new NotSupportedException(method.Name)
        });
        var lifetime = Proxy<IPluginLifetime>((method, _) => method.Name == "get_Stopping" ? stopping.Token : null);
        var dashboard = Proxy<IPluginDashboard>((_, _) => null);
        var services = Proxy<IPluginServices>((_, _) => null);
        var host = Proxy<IBotContext>((method, _) => method.Name switch
        {
            "get_Config" => configContext,
            "get_Message" => messages,
            "get_Lifetime" => lifetime,
            "get_Dashboard" => dashboard,
            "get_Services" => services,
            _ => throw new NotSupportedException(method.Name)
        });
        try
        {
            await plugin.OnLoad(host);
            var events = (IBotEventSubscriber)plugin;
            var group = new QGroupNudge { GroupId = "123", SenderId = "alice", ReceiverId = "bot" };
            var groupEvent = new PlatformEvent
            {
                Kind = QEventKinds.GroupNudge, Platform = "qq", InstanceId = "qq-group",
                SelfId = "bot", Channel = Channel.Group(group.GroupId), Raw = group
            };
            await events.OnEventAsync(groupEvent);
            if (delivered.Count != 0) throw new Exception("Nudge replies must be off by default.");

            var enabled = new PluginConfig { Models = [new ModelEntry { Name = "test", BaseUrl = endpoint, ApiKey = "test" }] };
            enabled.Default.Model = "test";
            enabled.Behaviour.ReplyToNudge = true;
            await ((IConfigurableComponent)plugin).ApplyConfigAsync(enabled);
            var upstream = new StubHandler(_ => JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content = "我在呀！\n多余的一行" } } }
            }));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var client = (OpenAiClient)typeof(AiChatPlugin).GetField("_openAi", flags)!.GetValue(plugin)!;
            ((Dictionary<string, HttpClient>)typeof(OpenAiClient).GetField("_clients", flags)!.GetValue(client)!)[endpoint] = new HttpClient(upstream);

            var friend = new QFriendNudge { UserId = "alice", IsSelfReceive = true };
            var friendEvent = new PlatformEvent
            {
                Kind = QEventKinds.FriendNudge, Platform = "qq", InstanceId = "qq-direct",
                Channel = Channel.Direct(friend.UserId), Raw = friend
            };
            await events.OnEventAsync(groupEvent with { Raw = group with { ReceiverId = "other" } });
            await events.OnEventAsync(groupEvent with { Raw = group with { SenderId = "bot" } });
            await events.OnEventAsync(friendEvent with { Raw = friend with { IsSelfReceive = false, IsSelfSend = true } });
            if (upstream.Requests.Count != 0 || delivered.Count != 0)
                throw new Exception("Pokes between other users and pokes initiated by the bot must be ignored.");

            await events.OnEventAsync(groupEvent);
            await events.OnEventAsync(friendEvent);
            if (upstream.Requests.Count != 2 || delivered.Count != 2 ||
                delivered[0].Target != new ChannelReference("qq-group", Channel.Group("123")) ||
                delivered[1].Target != new ChannelReference("qq-direct", Channel.Direct("alice")) ||
                delivered.Any(send => send.Segments is not [TextSegment { Text: "我在呀！" }]) ||
                upstream.Requests.Any(request => request.TryGetProperty("tools", out _) ||
                    !request.GetProperty("messages")[1].GetProperty("content").GetString()!.Contains("戳了你")))
                throw new Exception("Each incoming poke must prompt the model and send one line to the QQ instance and chat where it occurred.");

            await ((IConfigurableComponent)plugin).ApplyConfigAsync(new PluginConfig());
            await events.OnEventAsync(groupEvent);
            if (delivered.Count != 2) throw new Exception("Turning off nudge replies must take effect on reload.");
            Console.WriteLine("PASS: QQ poke replies honor live configuration, event direction, instance/channel scope, and one-line delivery");
        }
        finally
        {
            await stopping.CancelAsync();
            await plugin.OnUnload();
            root.Delete(recursive: true);
        }
    }
}
