using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Delivery;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

internal static class RichReplyChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static readonly ModelEntry Model = new() { Name = "test", BaseUrl = "https://example.invalid/v1", ApiKey = "test" };
    private static readonly MessageEvent Message = new()
    {
        Platform = "qq-official", MessageId = "incoming", SelfId = "bot",
        Channel = Channel.Direct("user"), Sender = new User("user"), Segments = []
    };
    private static IBotContext Context(FakeOfficialApi? api)
    {
        var context = DispatchProxy.Create<IBotContext, TestProxy>();
        ((TestProxy)(object)context).Call = (method, _) => method.Name == "GetAdapterExtension" ? api : throw new NotSupportedException();
        return context;
    }
    private static OpenAiClient Client(HttpMessageHandler handler)
    {
        var client = new OpenAiClient(10, "");
        var clients = (Dictionary<string, HttpClient>)typeof(OpenAiClient).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
        clients["https://example.invalid/v1"] = new HttpClient(handler);
        return client;
    }
    private static string Event(string text) => "data: " + JsonSerializer.Serialize(new
    {
        choices = new[] { new { index = 0, delta = new { content = text } } }
    }) + "\n\n";
    private const string End = "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
    private static HttpContent Sse(string content)
    {
        return new StringContent(content, Encoding.UTF8, "text/event-stream");
    }

    public static async Task RunAsync()
    {
        Check(OfficialReplyDelivery.ApplyPromptPolicy("custom\nlast", false) == "custom\nlast\n不要使用markdown", "Disabled Markdown must append the policy at the very end.");
        Check(OfficialReplyDelivery.ApplyPromptPolicy("custom", true) == "custom", "Enabled Markdown must not forbid formatting.");
        Check(!new DefaultSection().SharedPromptSuffix.Contains("Markdown"), "Default shared prompt must not forbid Markdown.");
        var behaviour = new BehaviourSection { UseMarkdown = true, UseStreaming = true };
        var customPlatformApi = new FakeOfficialApi();
        await using (var reply = new OfficialReplyDelivery(Context(customPlatformApi), Message with { Platform = "custom-adapter" }, behaviour))
            Check(reply.IsStreaming && reply.MarkdownAvailable && await reply.CompleteAsync("answer"),
                "Any adapter implementing the contracts must support Markdown and streaming regardless of platform name.");
        await using (var reply = new OfficialReplyDelivery(Context(null), Message, behaviour))
            Check(!reply.IsStreaming && !await reply.CompleteAsync("answer"), "Missing extension must fall back to ordinary replies.");
        var api = new FakeOfficialApi();
        await using (var reply = new OfficialReplyDelivery(Context(null), Message with { Platform = "custom-adapter" }, behaviour))
            Check(!reply.IsStreaming && !await reply.CompleteAsync("answer"), "Adapters without the contracts must use ordinary replies regardless of platform name.");
        await using (var reply = new OfficialReplyDelivery(Context(api), Message, new BehaviourSection()))
            Check(!reply.IsStreaming && !await reply.CompleteAsync("answer"), "Disabled switches must not send through official APIs.");
        await using (var reply = new OfficialReplyDelivery(Context(api), Message with { Channel = Channel.Group("group") }, behaviour))
        {
            Check(!reply.IsStreaming && await reply.CompleteAsync("# answer"), "Group must use Markdown without invoking private-chat stream.");
            Check(api.LastReply?.MessageId == "incoming" && api.LastTarget?.Scene == QOfficialMessageScene.Group, "Markdown must preserve target and passive reply ID.");
        }
        api = new FakeOfficialApi { AllowMarkdown = false };
        var mentionApi = new FakeOfficialApi();
        await using (var reply = new OfficialReplyDelivery(Context(mentionApi), Message with { Channel = Channel.Group("group") },
            new BehaviourSection { UseMarkdown = true, ReplyWithMention = true }))
        {
            Check(await reply.CompleteAsync("# answer") && mentionApi.LastMarkdown == "<qqbot-at-user id=\"user\" />\n\n# answer",
                "Official Markdown group reply must use the native sender mention.");
            var code = "```text\nfile.txt\n```\n正文";
            Check(await reply.CompleteAsync(code) && mentionApi.LastMarkdown == "<qqbot-at-user id=\"user\" />\n\n" + code,
                "Mention must precede fenced code on a separate line without altering code fences.");
        }
        await using (var reply = new OfficialReplyDelivery(Context(mentionApi), Message,
            new BehaviourSection { UseMarkdown = true, ReplyWithMention = true }))
        {
            Check(await reply.CompleteAsync("# answer") && mentionApi.LastMarkdown == "# answer",
                "Official Markdown private reply must not add a mention.");
        }
        await using (var reply = new OfficialReplyDelivery(Context(api), Message, new BehaviourSection { UseMarkdown = true }))
            Check(!await reply.CompleteAsync("answer"), "Markdown capability rejection must fall back.");
        api = new FakeOfficialApi { FailMarkdown = true };
        await using (var reply = new OfficialReplyDelivery(Context(api), Message, new BehaviourSection { UseMarkdown = true }))
            Check(!await reply.CompleteAsync("answer"), "Markdown send failure must fall back.");
        api = new FakeOfficialApi();
        await using (var reply = new OfficialReplyDelivery(Context(api), Message, behaviour))
        {
            Check(reply.IsStreaming && api.ContentType == QOfficialStreamContentType.Markdown, "Both switches must select Markdown streaming when available.");
            await reply.AppendAsync("hello");
            await reply.AppendAsync("hello world");
            Check(await reply.CompleteAsync("hello world"), "Stream must complete successfully.");
            Check(api.Stream.Frames.SequenceEqual(["hello", "hello world"]) && api.Stream.Completions == 1 && api.MarkdownSends == 0, "Coalescing must retain final full text without duplicate standalone replies.");
        }
        Check(api.Stream.Disposed, "Completed stream must be disposed.");
        api = new FakeOfficialApi();
        await using (var reply = new OfficialReplyDelivery(Context(api), Message, new BehaviourSection { UseStreaming = true }))
            Check(reply.IsStreaming && api.ContentType == QOfficialStreamContentType.Text, "Streaming must also support plain text.");
        api = new FakeOfficialApi { FailBegin = true };
        await using (var reply = new OfficialReplyDelivery(Context(api), Message, behaviour))
            Check(!reply.IsStreaming && !await reply.CompleteAsync("answer"), "BeginStream failure must fall back to default text.");
        api = new FakeOfficialApi();
        api.Stream.FailAppend = true;
        await using (var reply = new OfficialReplyDelivery(Context(api), Message, behaviour))
        {
            await reply.AppendAsync("answer");
            Check(!reply.IsStreaming && !await reply.CompleteAsync("answer") && api.Stream.Disposed, "Frame failure must retain full answer for ordinary text fallback.");
        }
        api = new FakeOfficialApi();
        api.Stream.FailComplete = true;
        await using (var reply = new OfficialReplyDelivery(Context(api), Message, behaviour))
        {
            await reply.AppendAsync("answer");
            Check(!await reply.CompleteAsync("answer"), "Completion failure must fall back to ordinary text.");
        }
        Console.WriteLine("PASS: Markdown prompt policy, capability checks, switches, passive replies, stream content type/lifecycle and send failures");

        // The second half of the HTTP body is unreadable until the first callback fires.
        // This fails if the client buffers the full response before publishing a prefix.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = new GatedSseStream(Event("你"), Event("好") + End, gate.Task);
        var handler = new ContentHandler(_ =>
        {
            var content = new StreamContent(gated);
            content.Headers.ContentType = new("text/event-stream");
            return content;
        });
        var prefixes = new List<string>();
        using (var client = Client(handler))
        {
            var result = await client.ChatIncrementalAsync(Model, new OpenAiChatRequest { Model = Model.Name }, text =>
            {
                prefixes.Add(text);
                gate.TrySetResult();
                return Task.CompletedTask;
            });
            Check(result == "你好" && prefixes.SequenceEqual(["你", "你好"]), "SSE callbacks must arrive before EOF and be cumulative.");
            Check(handler.Requests.Single().GetProperty("stream").GetBoolean(), "Incremental request must use stream:true.");
        }
        prefixes.Clear();
        using (var client = Client(new ContentHandler(_ => new StringContent("{\"choices\":[{\"message\":{\"content\":\"JSON answer\"}}]}", Encoding.UTF8, "application/json"))))
            Check(await client.ChatIncrementalAsync(Model, new OpenAiChatRequest { Model = Model.Name }, text => { prefixes.Add(text); return Task.CompletedTask; }) == "JSON answer", "Providers ignoring stream:true must remain compatible.");
        using (var client = Client(new ContentHandler(_ => Sse(Event("partial")))))
        {
            try
            {
                await client.ChatIncrementalAsync(Model, new OpenAiChatRequest { Model = Model.Name }, _ => Task.CompletedTask);
                throw new Exception("Expected truncated SSE to fail.");
            }
            catch (HttpRequestException) { }
        }
        handler = new ContentHandler(_ => Sse(Event("image answer") + End));
        using (var client = Client(handler))
        {
            Check(await client.ChatStreamingBodyAsync(new DefaultSection(), Model, [], onContent: _ => Task.CompletedTask) == "image answer", "Image-body path must support incremental responses.");
            Check(handler.Requests.Single().GetProperty("stream").GetBoolean(), "Image-body streaming request must use stream:true.");
        }
        Console.WriteLine("PASS: real SSE prefix before EOF, JSON fallback, interrupted SSE detection and streaming image-body requests");

        handler = new ContentHandler(_ => handlerDummy());
        HttpContent handlerDummy() => new StringContent("{\"choices\":[{\"message\":{\"content\":\"searching\",\"tool_calls\":[{\"id\":\"tool1\",\"function\":{\"name\":\"first\",\"arguments\":\"{}\"}}]}}]}", Encoding.UTF8, "application/json");
        handler.Respond = _ => handler.Requests.Count == 1 ? handlerDummy() : Sse(Event("final") + Event(" answer") + End);
        prefixes.Clear();
        using (var client = Client(handler))
        using (var registry = new ChatToolRegistry())
        {
            registry.Register(new FakeTool("first"));
            var answer = await registry.ChatAsync(client, Model, [new OpenAiMessage { Content = "question" }], 1,
                text => { prefixes.Add(text); return Task.CompletedTask; });
            Check(answer == "searching\n\nfinal answer" && prefixes.SequenceEqual(["searching", "searching\n\nfinal", "searching\n\nfinal answer"]), "Tool rounds must preserve all streamed visible text as an exact prefix.");
            Check(handler.Requests.All(r => r.GetProperty("stream").GetBoolean()), "Both tool-enabled and final generation requests must stream.");
        }
        string ToolEvent(string? id, string name, string arguments) => "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, delta = new { tool_calls = new[] { new { index = 0, id, function = new { name, arguments } } } } } }
        }) + "\n\n";
        string ReasoningEvent(string text) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { reasoning_content = text } } } }) + "\n\n";
        handler = new ContentHandler(_ => Sse(ToolEvent("call-1", "fi", "{\"que") +
            ToolEvent(null, "rst", "ry\":\"hello\"}") + End));
        handler.Respond = _ => handler.Requests.Count == 1
            ? Sse(ReasoningEvent("private ") + ReasoningEvent("reasoning") + ToolEvent("call-1", "fi", "{\"que") + ToolEvent(null, "rst", "ry\":\"hello\"}") + End)
            : Sse(Event("answer") + End);
        prefixes.Clear();
        using (var client = Client(handler))
        using (var registry = new ChatToolRegistry())
        {
            var tool = new FakeTool("first");
            registry.Register(tool);
            var answer = await registry.ChatAsync(client, Model, [], 1,
                text => { prefixes.Add(text); return Task.CompletedTask; });
            Check(answer == "answer" && tool.Calls == 1 && tool.LastArguments == "{\"query\":\"hello\"}",
                "Fragmented SSE tool name and arguments must assemble before execution.");
            Check(prefixes.SequenceEqual(["answer"]), "Tool argument fragments must never be sent as visible text.");
            var replay = handler.Requests[1].GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "assistant");
            Check(replay.GetProperty("reasoning_content").GetString() == "private reasoning",
                "SSE reasoning fragments must be concatenated and passed back to the next tool round, without being sent as visible text.");
        }
        gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gated = new GatedSseStream(Event("first"), Event(" answer") + End, gate.Task);
        handler = new ContentHandler(_ =>
        {
            var content = new StreamContent(gated);
            content.Headers.ContentType = new("text/event-stream");
            return content;
        });
        using (var client = Client(handler))
        using (var registry = new ChatToolRegistry())
        {
            registry.Register(new FakeTool("first"));
            var answer = await registry.ChatAsync(client, Model, [], 1,
                _ => { gate.TrySetResult(); return Task.CompletedTask; });
            Check(answer == "first answer" && handler.Requests[0].GetProperty("tools").GetArrayLength() == 1,
                "Tools being available must not block ordinary-answer streaming before EOF.");
        }
        api = new FakeOfficialApi();
        await using (var reply = new OfficialReplyDelivery(Context(api), Message, behaviour))
        {
            var attempts = 0;
            Func<Func<string, Task>?, Task<string>> operation = async onContent =>
            {
                attempts++;
                if (onContent is not null) { await onContent("old prefix"); throw new HttpRequestException("interrupted"); }
                return "fresh answer";
            };
            var plugin = new AiChatPlugin();
            var method = typeof(AiChatPlugin).GetMethod("ExecuteReplyAttemptAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Check(await (Task<string>)method.Invoke(plugin, [reply, operation, CancellationToken.None])! == "fresh answer" && attempts == 2, "Stream failure must rebuild a non-streaming attempt.");
            Check(!await reply.CompleteAsync("fresh answer") && api.Stream.Frames.SequenceEqual(["old prefix"]), "Retry must not append fresh answer to previous visible prefix.");
        }
        Console.WriteLine("PASS: streaming with tools, fragmented tool arguments, prefix continuity and clean retry after a partially sent response");
    }
}

internal sealed class FakeOfficialApi : IQOfficialMessageApi, IQOfficialDirectMessageApi
{
    public bool AllowMarkdown = true, FailMarkdown, FailBegin;
    public int MarkdownSends;
    public QOfficialMessageReply? LastReply;
    public QOfficialMessageTarget? LastTarget;
    public string? LastMarkdown;
    public QOfficialStreamContentType ContentType;
    public FakeOfficialStream Stream { get; } = new();
    public bool CanSendMarkdown(QOfficialMessageTarget target, QOfficialMarkdown markdown, QOfficialKeyboard? keyboard = null) => AllowMarkdown;
    public Task<string> SendMarkdownAsync(QOfficialMessageTarget target, QOfficialMarkdown markdown, QOfficialKeyboard? keyboard = null, QOfficialMessageReply? reply = null)
    {
        if (FailMarkdown) throw new NotSupportedException("no platform permission");
        LastReply = reply; LastTarget = target; MarkdownSends++;
        LastMarkdown = (markdown as QCustomMarkdown)?.Content;
        return Task.FromResult("markdown-id");
    }
    public Task AcknowledgeInteractionAsync(string interactionId, QOfficialInteractionResponseCode code = QOfficialInteractionResponseCode.Success) => Task.CompletedTask;
    public Task SendTypingAsync(QOfficialMessageTarget target, QOfficialMessageReply reply, TimeSpan duration) => Task.CompletedTask;
    public IQOfficialMessageStream BeginStream(QOfficialMessageTarget target, QOfficialMessageReply reply, QOfficialStreamContentType contentType = QOfficialStreamContentType.Text)
    {
        if (FailBegin) throw new NotSupportedException("stream unavailable");
        LastReply = reply; LastTarget = target; ContentType = contentType;
        return Stream;
    }
}
internal sealed class FakeOfficialStream : IQOfficialMessageStream
{
    public bool FailAppend, FailComplete, Disposed;
    public List<string> Frames { get; } = [];
    public int Completions;
    public bool HasStarted => Frames.Count > 0;
    public Task AppendAsync(string cumulativeText, CancellationToken cancellationToken = default)
    {
        if (FailAppend) throw new HttpRequestException("frame failed");
        if (Frames.Count > 0 && !cumulativeText.StartsWith(Frames[^1], StringComparison.Ordinal)) throw new Exception("Prefix changed");
        Frames.Add(cumulativeText);
        return Task.CompletedTask;
    }
    public Task<string> CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (FailComplete) throw new HttpRequestException("completion failed");
        Completions++;
        return Task.FromResult("stream-id");
    }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
internal sealed class ContentHandler(Func<JsonElement, HttpContent> respond) : HttpMessageHandler
{
    public List<JsonElement> Requests { get; } = [];
    public Func<JsonElement, HttpContent> Respond = respond;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        Requests.Add(doc.RootElement.Clone());
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = Respond(doc.RootElement) };
    }
}
internal sealed class GatedSseStream(string prefix, string suffix, Task gate) : Stream
{
    private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix), _suffix = Encoding.UTF8.GetBytes(suffix);
    private int _firstOffset, _secondOffset;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_firstOffset < _prefix.Length)
        {
            var count = Math.Min(buffer.Length, _prefix.Length - _firstOffset);
            _prefix.AsMemory(_firstOffset, count).CopyTo(buffer); _firstOffset += count;
            return count;
        }
        await gate.WaitAsync(ct);
        var next = Math.Min(buffer.Length, _suffix.Length - _secondOffset);
        _suffix.AsMemory(_secondOffset, next).CopyTo(buffer); _secondOffset += next;
        return next;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
