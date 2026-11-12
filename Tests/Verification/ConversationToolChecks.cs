using System.Reflection;
using System.Net;
using System.Text;
using ShiroBot.AiChatPlugin.Config;
using System.Text.Json;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Resources;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

internal static class ConversationToolChecks
{
    static void Check(bool value, string text) { if (!value) throw new Exception(text); }
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aichat-conversation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bot = DispatchProxy.Create<IBotContext, TestProxy>();
            var messages = DispatchProxy.Create<IMessageContext, TestProxy>();
            var users = DispatchProxy.Create<IUserService, TestProxy>();
            var channel = DispatchProxy.Create<IChannelService, TestProxy>();
            ((TestProxy)(object)bot).Call = (m, _) => m.Name switch { "get_Message" => messages, "get_User" => users, "get_Channel" => channel, _ => throw new NotSupportedException(m.Name) };
            var current = new MessageEvent { Platform = "qq", InstanceId = "main", SelfId = "bot", MessageId = "now", Channel = Channel.Group("g"), Sender = new User("123"), Segments = [] };
            var fetched = current with { MessageId = "old", Segments = [new TextSegment("a past message"), new ImageSegment("https://cdn.example/image")] };
            var reads = 0; var sends = 0; bool failSend = false;
            IReadOnlyList<MessageSegment>? sent = null;
            ((TestProxy)(object)messages).Call = (m, a) =>
            {
                Check(((Channel)a![0]!).Id == "g", "Tools must stay in the current channel.");
                if (m.Name == "GetMessageAsync") { reads++; Check((string)a[1]! == "old", "Wrong message ID."); return Task.FromResult<MessageEvent?>(fetched); }
                if (m.Name == "SendMessageAsync") { sends++; sent = (IReadOnlyList<MessageSegment>)a[1]!; return Task.FromResult(new SentMessage("sent") { IsSuccess = !failSend }); }
                throw new NotSupportedException(m.Name);
            };
            var avatarMethod = "";
            ((TestProxy)(object)users).Call = (m, _) => { avatarMethod = m.Name; return m.Name switch { "GetAvatarUrlAsync" => Task.FromResult<string?>("https://adapter.example/avatar/123"), "GetUserAsync" => Task.FromResult<User?>(new User("123") { AvatarUrl = "https://adapter.example/avatar/123" }), _ => throw new NotSupportedException(m.Name) }; };
            IReadOnlyList<Member> members = [new Member(new User("abcdefghijklmno123"))];
            ((TestProxy)(object)channel).Call = (_, _) => Task.FromResult(members);
            var png = ReferenceImage.ToPng(Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7")).Bytes;
            var urls = new List<string>();
            Task<(byte[],string?)> Download(string url, CancellationToken ct) { urls.Add(url); return Task.FromResult<(byte[],string?)>((png,"image/png")); }
            var files = new DownloadedFiles(() => root);
            using var tools = new ConversationToolSet(bot, current, files, Download, m => m.GetPlainText());
            using var lookup = tools.Message; using var avatar = tools.Avatar; using var mention = tools.Mention;
            var first = JsonDocument.Parse(await lookup.ExecuteAsync("{\"message_id\":\"old\"}"));
            Check(first.RootElement.GetProperty("images_attached").GetInt32() == 1 && reads == 1, "Omitted message images must be recovered.");
            var id = first.RootElement.GetProperty("files")[0].GetProperty("file_id").GetString()!;
            Check((await files.ImagesAsync([id], default)).Length == 1, "Recovered image must work as an imagegen reference.");
            var attachments = ((IToolAttachments)lookup).TakeAttachments();
            Check(attachments.Count == 1 && ((ChatMessageDescriptor)attachments[0].Content).HasImage && attachments[0].Role == "user", "Loaded image must become an actual model attachment.");
            Check(((IToolAttachments)lookup).TakeAttachments().Count == 0, "Attachments must drain once.");
            await lookup.ExecuteAsync("{\"message_id\":\"old\"}");
            Check(reads == 2 && ((IToolAttachments)lookup).TakeAttachments().Count == 1, "Explicit repeated message reads must not be deduplicated away.");
            await lookup.ExecuteAsync("{\"message_id\":\"old\",\"include_images\":false}");
            Check(((IToolAttachments)lookup).TakeAttachments().Count == 0, "Text-only lookup must not load images.");
            fetched = fetched with { Channel = Channel.Group("other") };
            Check((await lookup.ExecuteAsync("{\"message_id\":\"old\"}")).Contains("did not belong"), "Cross-channel result must be rejected.");
            await avatar.ExecuteAsync("{\"user_id\":\"123\"}");
            Check(urls.Last() == "https://adapter.example/avatar/123", "Generic adapter avatar address is incorrect.");
            Check(avatarMethod == (typeof(IUserService).GetMethod("GetAvatarUrlAsync") is null ? "GetUserAsync" : "GetAvatarUrlAsync"), "New SDK hosts must use the generic avatar method while old hosts keep the profile fallback.");
            Check(((IToolAttachments)avatar).TakeAttachments().Count == 1, "Avatar must be shown to the model.");
            Check((await mention.ExecuteAsync("{\"user_id\":\"456\",\"text\":\"hello\"}")).Contains("successfully"), "Mention did not send.");
            Check(sent![0] is MentionSegment { UserId: "456" } && sent[1] is TextSegment { Text: " hello" }, "Mention must use a structured segment, not literal @text.");
            await mention.ExecuteAsync("{\"user_id\":\"456\",\"text\":\"hello\"}"); Check(sends == 1, "Successful mention should not repeat in the same turn.");
            failSend = true;
            Check((await mention.ExecuteAsync("{\"user_id\":\"456\",\"text\":\"retry\"}")).Contains("failed"), "Failed send must not claim success.");
            failSend = false;
            Check((await mention.ExecuteAsync("{\"user_id\":\"456\",\"text\":\"retry\"}")).Contains("successfully"), "Failed send must remain retryable.");
            using var discord = new ConversationToolSet(bot, current with { Platform = "discord" }, files, Download, _ => "");
            using var dm = discord.Mention;
            await dm.ExecuteAsync("{\"user_id\":\"abcdefghijklmno\",\"text\":\"hi\"}");
            Check(sent![0] is MentionSegment { UserId: "abcdefghijklmno123" }, "Short ID must resolve to the full ID before sending.");
            members = [..members, new Member(new User("abcdefghijklmno456"))];
            using var ambiguous = new ConversationToolSet(bot, current with { Platform = "discord" }, files, Download, _ => "");
            Check((await ambiguous.Mention.ExecuteAsync("{\"user_id\":\"abcdefghijklmno\",\"text\":\"hi\"}")).Contains("ambiguous"), "Ambiguous prefixes must not pick a recipient.");
            using var direct = new ConversationToolSet(bot, current with { Channel = Channel.Direct("123") }, files, Download, _ => "");
            Check((await direct.Mention.ExecuteAsync("{\"user_id\":\"456\",\"text\":\"hi\"}")).Contains("only available"), "Group mention must be refused in direct conversations.");
            fetched = fetched with { Channel = current.Channel };
            var requestCount = 0;
            using var client = new OpenAiClient(10, "");
            using var http = new HttpClient(new Handler(async request =>
            {
                requestCount++;
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                if (requestCount == 1)
                    return JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", content = "", tool_calls = new[] { new { id = "recover", type = "function", function = new { name = "get_message", arguments = "{\"message_id\":\"old\"}" } } } } } } });
                var sentMessages = payload.RootElement.GetProperty("messages");
                Check(sentMessages[sentMessages.GetArrayLength() - 2].GetProperty("role").GetString() == "tool", "Tool result must precede the image attachment.");
                var last = sentMessages[sentMessages.GetArrayLength() - 1];
                Check(last.GetProperty("role").GetString() == "user" && last.GetRawText().Contains("data:image/png;base64,"), "The next upstream request must contain actual image bytes after all tool results.");
                return JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = "image received" } } } });
            }));
            var model = new ModelEntry { Name = "test", BaseUrl = "https://example.invalid/v1", ApiKey = "test" };
            ((Dictionary<string, HttpClient>)typeof(OpenAiClient).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!)[model.BaseUrl] = http;
            using var registry = new ChatToolRegistry();
            Check(await registry.ChatAsync(client, model, [new OpenAiMessage { Role = "user", Content = "read old image" }], 2, contextualTools: [lookup]) == "image received" && requestCount == 2,
                "Message recovery must work through the real tool loop and request serializer.");
            Console.WriteLine("PASS: scoped/repeated message recovery, actual image attachments and reference files, QQ avatars, structured mentions, delivery deduplication, failures and unique long-ID resolution");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<string>> handle) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await handle(request), Encoding.UTF8, "application/json") };
    }

}
